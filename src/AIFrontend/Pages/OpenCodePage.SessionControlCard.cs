using System.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeSessionControlCard? _sessionControlCard;
    private bool _sessionControlCardRefreshPending;

    private void InitializeSessionControlCard()
    {
        _sessionControlCard = new();
        LeftBottomCardSlot.Children.Add(_sessionControlCard);
        _agentSelector.DisplayChanged += (_, _) => QueueSessionControlCardRefresh();
        _composerView!.StateChanged += (_, _) => QueueSessionControlCardRefresh();
        // Conversation changes include child agent/model switch rows even when token/context totals did not change.
        _conversationView.IdleViewportInvalidated += (_, _) => QueueSessionControlCardRefresh();
        BindNativePickerChoices(_sessionControlCard.AgentButton, _sessionControlCard.ModelButton, _sessionControlCard.VariantButton);
        QueueSessionControlCardRefresh();
    }

    partial void QueueSessionControlCardRefresh()
    {
        if (_sessionControlCard is null || _sessionControlCardRefreshPending) return;
        _sessionControlCardRefreshPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _sessionControlCardRefreshPending = false;
            RefreshSessionControlCard(); // Pull live state, never an event payload or a saved selection.
        });
    }

    private bool SessionControlCardCanEdit()
        => _pageActive && !_homeActive && !_draftRecoveryPending && !_draftSlotChanging
        && SourceReady && !_sessionMissing && !_isSending && !_isComposing && !_composerPickerPending
        && (_newSessionDraftActive || _agentSelector.CurrentSessionInfo is { ParentId: null } info && info.Id == _selectedSessionId)
        && !HasPendingNativeAction() && !ComposerCoveredByOverlay && _composerBinding is { } binding
        && OwnsComposerBinding(binding) && GetComposerSnapshot() is { IsActive: true, IsBusy: false };

    private void RefreshSessionControlCard()
    {
        RefreshComposerToolbar();
        if (_sessionControlCard is not { } card) return;
        // Layout D: the agent/model card docks bottom-left only in wide mode; narrow pages show the same
        // choices in the composer's second row, so the narrow drawer holds only the 子智能体 tree.
        var visible = _pageActive && !_homeActive && (_selectedSessionId is not null || _newSessionDraftActive)
            && _sidebarLayout.IsWide;
        LeftBottomCardSlot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var enabled = visible && SessionControlCardCanEdit();
        RenderNativePickerChoices(card.AgentButton, card.ModelButton, card.VariantButton, enabled);
        if (_newSessionDraftActive)
        {
            // Read the existing per-slot blank preferences, not Session.Info. The same handlers save
            // these choices for first-send creation; the card never persists or clones them.
            var value = _blankPreferences;
            var current = value is not null && value.BlankSlotId == _activeBlankSlotId
                && value.Matches(_homeRuntimeSource, _composerController!.Snapshot.Token.Draft);
            var agent = current && HasBlankAgentPresentation(value!)
                ? _activeDraftSlot!.Agents.FirstOrDefault(a => a.Id == value!.Agent) : null;
            var model = current && HasBlankMetadata(value!)
                ? _blankModels.FirstOrDefault(m => m.Id == value!.Model?.Id && m.ProviderId == value.Model?.ProviderId) : null;
            card.Render("草稿 · " + (agent?.Name ?? "智能体未就绪"), agent?.Color, model?.Id, model?.Name ?? "模型未就绪",
                model is null ? "强度未就绪" : BlankStrengthText(model, value?.Model),
                "新会话草稿：仅用于首次发送，尚未保存为会话选择。", enabled && BlankAgentButton.IsEnabled,
                enabled && BlankModelButton.IsEnabled, enabled && BlankStrengthButton.IsEnabled, agent?.Description);
            return;
        }

        var ownsSelection = ReferenceEquals(_agentSelectorSource, _source) && _agentSelectorRevision == _sourceRevision
            && _agentSelectorSessionId == _selectedSessionId;
        if (ownsSelection && _agentSelector.CurrentSessionInfo is { ParentId: not null, Id: { } childId } child && childId == _selectedSessionId)
        {
            // Never use pending UI preferences or the parent's model. These are this child's real execution rows.
            var messages = _messageWindowAccepted && IsSameMessageBinding(childId, _messageBinding?.Directory)
                ? ProjectMessageHistory(MergeMessageHistory()).Where(m => m.Type is OpenCodeMessageTypes.Assistant
                    or OpenCodeMessageTypes.AgentSwitched or OpenCodeMessageTypes.ModelSwitched)
                    .Select(m => (m.Agent, m.Model))
                : Enumerable.Empty<(string? Agent, OpenCodeModelRef? Model)>();
            var projection = OpenCodeChildSessionControl.Project(child.Agent, child.Model, messages,
                model => TryCaptureInsightsBinding(out var childBinding) ? _insights?.CurrentModelInfo(childBinding!, model) : null);
            card.RenderReadOnly(projection);
            return;
        }
        var selected = ownsSelection ? _agentSelector.CurrentModel : null;
        var modelName = selected is not null && TryCaptureInsightsBinding(out var binding)
            ? _insights?.CurrentModelInfo(binding!, selected)?.Name : null;
        card.Render(ownsSelection ? _agentSelector.CurrentAgentDisplay ?? "智能体不可用" : "智能体不可用",
            ownsSelection ? _agentSelector.CurrentAgentColor : null, selected?.Id, modelName ?? "模型不可用",
            selected?.Id is null ? "强度不可用" : OpenCodeModelPresentation.VariantLabel(selected.Variant),
            "仅修改当前会话；模型与强度选择仅影响之后的回合。", enabled && ownsSelection,
            enabled, enabled && selected?.Id is not null, ownsSelection ? _agentSelector.CurrentAgentDescription : null,
            pendingAgent: ownsSelection && _agentSelector.HasPendingSelection);
    }

    private void OpenSessionControlModelPicker(bool variantOnly, FrameworkElement? anchor = null)
    {
        if (!SessionControlCardCanEdit() || _composerView?.PrepareSubmission() != true || _composerController is null) return;
        var ticket = _composerController.TryTakeModelPickerAction(GetComposerSnapshot().Token);
        if (ticket is not null) _ = RunClientActionAsync(ticket, variantOnly, anchor);
    }

    private void BindNativePickerChoices(OpenCodeNativeComboBox agent, Button model, OpenCodeNativeComboBox effort)
    {
        agent.ChoiceChanged += async key =>
        {
            if (!SessionControlCardCanEdit() || key is not string id) return;
            if (_newSessionDraftActive && _blankPreferences is { } basis && _composerController is { } controller)
                await SetBlankAgentAsync(basis, controller.Snapshot.Token, id);
            else _agentSelector.SelectUserAgent(id);
            QueueSessionControlCardRefresh();
        };
        model.Click += ComposerModelButton_Click;
        effort.ChoiceChanged += key =>
        {
            var selected = _newSessionDraftActive ? _blankPreferences?.Model : _agentSelector.CurrentModel;
            if (key is OpenCodeEffortChoice variant && selected?.Id is { } id && selected.ProviderId is { } provider)
                _ = SetNativePickerModelAsync(new(provider, id, variant.Id), true);
        };
    }

    private async System.Threading.Tasks.Task SetNativePickerModelAsync(OpenCodeModelChoiceKey choice, bool effortOnly)
    {
        if (!SessionControlCardCanEdit() || _composerController is not { } controller || _composerView?.PrepareSubmission() != true) return;
        var current = _newSessionDraftActive ? _blankPreferences?.Model : _agentSelector.CurrentModel;
        // Same model is a no-op, not a request to reset its effort to default.
        if (current?.Id == choice.ModelId && current.ProviderId == choice.ProviderId
            && (!effortOnly || current.Variant == choice.Variant)) return;
        if (_newSessionDraftActive && _blankPreferences is { } basis)
        {
            if (effortOnly) await SetBlankVariantAsync(basis, controller.Snapshot.Token, choice.Variant);
            else await ChooseBlankModelAsync(basis, controller.Snapshot.Token, selectedChoice: choice);
        }
        else if (controller.TryTakeModelPickerAction(GetComposerSnapshot().Token) is { } ticket)
            await RunClientActionAsync(ticket, effortOnly, selectedChoice: choice);
        QueueSessionControlCardRefresh();
    }

    private void RenderNativePickerChoices(OpenCodeNativeComboBox agent, Button model,
        OpenCodeNativeComboBox effort, bool enabled)
    {
        var blank = _newSessionDraftActive;
        var owns = blank ? _blankPreferences is { } value && value.BlankSlotId == _activeBlankSlotId
            && value.Matches(_homeRuntimeSource, _composerController!.Snapshot.Token.Draft)
            : ReferenceEquals(_agentSelectorSource, _source) && _agentSelectorRevision == _sourceRevision && _agentSelectorSessionId == _selectedSessionId;
        var rows = blank ? owns && HasBlankAgentPresentation(_blankPreferences!) ? _activeDraftSlot!.Agents.Where(IsBlankAgentSelectable)
            .Select(a => new OpenCodeAgentPickerRow(a.Id!, a.Name!, a.Description, true, null, a.Color)) : Enumerable.Empty<OpenCodeAgentPickerRow>()
            : owns ? _agentSelector.AgentChoices : Enumerable.Empty<OpenCodeAgentPickerRow>();
        var models = blank ? owns && HasBlankMetadata(_blankPreferences!) ? _blankModels : System.Array.Empty<OpenCodeModelInfo>()
            : owns ? _agentSelector.ModelChoices : System.Array.Empty<OpenCodeModelInfo>();
        var selected = owns ? blank ? _blankPreferences?.Model : _agentSelector.CurrentModel : null;
        var currentModel = models.FirstOrDefault(m => m.Id == selected?.Id && m.ProviderId == selected?.ProviderId);
        var editable = enabled && owns && (!blank || !_blankPickerPending && !_newSessionCreatePending);
        agent.Render(OpenCodeNativeComboBox.Agents(rows), owns ? blank ? _blankPreferences?.Agent : _agentSelector.CurrentAgentId : null, editable);
        OpenCodeSessionControlCard.RenderModelButton(model, currentModel, editable);
        effort.Render(OpenCodeNativeComboBox.Efforts(currentModel), selected is null ? null : new OpenCodeEffortChoice(selected.Variant), editable && currentModel is not null);
    }
}
