using System.Linq;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;

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
        _sessionControlCard.AgentButton.Click += (sender, e) =>
        {
            if (!SessionControlCardCanEdit()) return;
            if (_newSessionDraftActive) BlankAgentButton_Click(sender, e);
            else _agentSelector.OpenSelection();
        };
        _sessionControlCard.ModelButton.Click += (sender, e) =>
        {
            if (!SessionControlCardCanEdit()) return;
            if (_newSessionDraftActive) BlankModelButton_Click(sender, e);
            else OpenSessionControlModelPicker(variantOnly: false);
        };
        _sessionControlCard.VariantButton.Click += (sender, e) =>
        {
            if (!SessionControlCardCanEdit()) return;
            if (_newSessionDraftActive) BlankStrengthButton_Click(sender, e);
            else OpenSessionControlModelPicker(variantOnly: true);
        };
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
        && !HasPendingNativeAction() && !ComposerCoveredByOverlay && _composerBinding is { } binding
        && OwnsComposerBinding(binding) && GetComposerSnapshot() is { IsActive: true, IsBusy: false };

    private void RefreshSessionControlCard()
    {
        RefreshComposerToolbar();
        if (_sessionControlCard is not { } card) return;
        var visible = _pageActive && !_homeActive && (_selectedSessionId is not null || _newSessionDraftActive);
        LeftBottomCardSlot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var enabled = visible && SessionControlCardCanEdit();
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
        var selected = ownsSelection ? _agentSelector.CurrentModel : null;
        card.Render(ownsSelection ? _agentSelector.CurrentAgentDisplay ?? "智能体不可用" : "智能体不可用",
            ownsSelection ? _agentSelector.CurrentAgentColor : null, selected?.Id, selected?.Id ?? "模型不可用",
            selected?.Id is null ? "强度不可用" : OpenCodeModelPresentation.VariantLabel(selected.Variant),
            "仅修改当前会话；模型与强度选择仅影响之后的回合。", enabled && ownsSelection,
            enabled, enabled && selected?.Id is not null, ownsSelection ? _agentSelector.CurrentAgentDescription : null);
    }

    private void OpenSessionControlModelPicker(bool variantOnly)
    {
        if (!SessionControlCardCanEdit() || _composerView?.PrepareSubmission() != true || _composerController is null) return;
        var ticket = _composerController.TryTakeModelPickerAction(GetComposerSnapshot().Token);
        if (ticket is not null) _ = RunClientActionAsync(ticket, variantOnly);
    }
}
