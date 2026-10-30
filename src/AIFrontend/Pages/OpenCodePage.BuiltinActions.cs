using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodeSessionControlCoordinator _sessionControls = new(OpenCodeInteractionGate.Shared);
    private sealed record NativeActionOwner(OpenCodeComposerClientActionTicket Ticket, SessionControlOrigin Origin,
        SessionControlDraft Draft, Guid OperationId, OpenCodeComposerInputEvidence Input);
    private volatile NativeActionOwner? _nativeActionOwner;
    private long _nativeActionSequence;
    private bool _nativeDialogPending;
    private OpenCodeDraftEditorDialog? _nativeEditor;
    private OpenCodeComposerClientActionTicket? _nativeEditorTicket;
    private bool _applyingNativeText;
    private Guid? _newSessionOperation;
    private sealed record ModelPickerBasis(NativeActionOwner Owner, SessionControlState State,
        SessionControlModels Catalog, SessionControlDefaultModel? Inherited, bool DeferSelection = false);
    private sealed record DeferredModelPickerBasis(NativeActionOwner Owner);

    // These callbacks execute inside Source/controller guards. Field comparisons only: no XAML/App.
    private bool OwnsNativeOperation(NativeActionOwner owner, SessionControlOrigin origin)
        => ReferenceEquals(_nativeActionOwner, owner) && ReferenceEquals(owner.Origin, origin)
        && OwnsComposerBinding(owner.Ticket.Binding);

    private bool CanPublishNativeOperation(NativeActionOwner owner)
        => !owner.Origin.Source.IsRevoked && OwnsNativeOperation(owner, owner.Origin);

    private bool CanStartNativeAction(NativeActionOwner owner, SessionControlOrigin origin)
        => OwnsNativeOperation(owner, origin) && !_isComposing
        && _composerView is { } view && OpenCodeComposerInputEvidence.StillCurrent(owner.Input, view.InputEvidence)
        && _composerController?.OwnsClientAction(owner.Ticket) == true;

    private bool HasPendingNativeAction()
        => _nativeActionOwner is not null || _nativeDialogPending || _newSessionCreatePending
        || _composerController?.Snapshot.ClientIntent is not null;

    // Non-blocking display probe only. The actual sender/Stop rechecks the exact slots under its lease.
    private bool NativeSendStopDisplayAllowed()
        => !HasPendingNativeAction() && _source is { IsRevoked: false } source
        && (_selectedSessionId is { } id
            ? OpenCodeInteractionGate.Shared.TryPeekComposerSubmissionProtection(source.Identity, id, out _, out var allowed) && allowed
            : _newSessionDraftActive); // Blank tab: Send only requests creation; the new session is re-probed before sending.

    private void InvalidateNativeActionIfBindingLost()
    {
        if (_nativeActionOwner is { } owner && !OwnsComposerBinding(owner.Ticket.Binding))
            _nativeActionOwner = null;
        CheckNativeEditorOwnership();
    }

    private void CheckNativeEditorOwnership()
    {
        if (_applyingNativeText || _nativeEditorTicket is null) return;
        void Check()
        {
            if (!_applyingNativeText && _nativeEditorTicket is { } ticket
                && (_composerController?.OwnsClientAction(ticket) != true || !OwnsComposerBinding(ticket.Binding)))
                _nativeEditor?.MarkStale();
        }
        // Snapshot publication may be inside TextChanging. Never mutate the visual tree there.
        DispatcherQueue.TryEnqueue(Check);
    }

    private void DispatchClientAction(OpenCodeComposerClientActionTicket ticket)
        => _ = RunClientActionAsync(ticket);

    private async Task RunClientActionAsync(OpenCodeComposerClientActionTicket ticket, bool variantOnly = false,
        Microsoft.UI.Xaml.FrameworkElement? anchor = null, OpenCodeModelChoiceKey? selectedChoice = null)
    {
        if (_composerController is null || _composerView is null) return;
        if (_nativeActionOwner is not null || _nativeDialogPending || !_composerController.OwnsClientAction(ticket))
        { _composerController.CompleteClientAction(ticket); return; }
        if (!_composerView.PrepareClientTextApplication() || !_composerView.InputEvidence.IsClean
            || _composerController.CaptureDraft(ticket.Token) is not { } basis)
        { _composerController.CompleteClientAction(ticket); return; }
        var input = _composerView.InputEvidence;
        var snapshot = _composerController.Snapshot;
        var origin = new SessionControlOrigin(ticket.Binding.Source, ticket.Binding.Revision, ticket.Binding.SessionId,
            ++_nativeActionSequence)
        {
            PreviouslyKnownUnresolvedChildSessionIds = _historyUnresolvedChildren,
            LocalSubmissionInProgress = _isSending,
        };
        var owner = new NativeActionOwner(ticket, origin, basis.Draft, Guid.NewGuid(), input);
        _nativeActionOwner = owner;
        UpdateComposerButtons();
        RefreshComposerView();
        try
        {
            using var cancellation = CreateOperationToken(ticket.Binding.Source);
            if (!CanPublishNativeOperation(owner) || !CanStartNativeAction(owner, origin)) return;
            switch (ticket.Kind)
            {
                case OpenCodeComposerClientActionKind.NewSession:
                    // Navigation intent only: the existing hook defers the blank tab until this action settles.
                    RequestNewSessionTab();
                    break;
                case OpenCodeComposerClientActionKind.ChooseSession:
                    // /session and /sessions return to the pinned home/session list after native ownership settles.
                    DispatcherQueue.TryEnqueue(ShowHomePage);
                    break;
                case OpenCodeComposerClientActionKind.ChooseAgent:
                    // §7: opens the agent picker dialog; switching still requires its own explicit 切换 gesture and
                    // runs through the selector's existing AgentSwitchCoordinator flow (origin, operationId, unknown).
                    _agentSelector.OpenSelection();
                    break;
                case OpenCodeComposerClientActionKind.ChooseModel:
                    await ChooseNativeModelAsync(owner, cancellation.Token, variantOnly, anchor, selectedChoice);
                    break;
                case OpenCodeComposerClientActionKind.Undo:
                case OpenCodeComposerClientActionKind.Redo:
                    await RunNativeRollbackAsync(owner, cancellation.Token);
                    break;
                case OpenCodeComposerClientActionKind.EditDraft:
                    QueueFullScreenComposer(ticket);
                    break;
                case OpenCodeComposerClientActionKind.SideQuestion:
                    // A9: only asks for the question; the generate request then runs detached from this action.
                    await AskNativeSideQuestionAsync(owner, snapshot.Text);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (CanPublishNativeOperation(owner))
                ShowMessage(InfoBarSeverity.Warning, "操作未完成", "未自动重试；草稿与已有保护保持不变。请重新检查当前状态。");
        }
        finally
        {
            _composerController.CompleteClientAction(ticket);
            if (ReferenceEquals(_nativeActionOwner, owner)) _nativeActionOwner = null;
            if (_pageActive) UpdateComposerButtons();
            RefreshComposerView();
        }
    }

    private async Task<T> ReadNativeAsync<T>(NativeActionOwner owner, Func<Task<T>> start, CancellationToken ct)
    {
        Task<T>? task = null;
        owner.Origin.Source.TryExecute(ct, () =>
        {
            if (CanStartNativeAction(owner, owner.Origin)) task = start();
        });
        if (task is null) throw new OperationCanceledException(ct);
        var result = await task;
        if (!CanStartNativeAction(owner, owner.Origin)) throw new OperationCanceledException(ct);
        return result;
    }

    private async Task<OpenCodeModelPickerPresentation?> ReadNativeModelsAsync(NativeActionOwner owner, CancellationToken ct)
    {
        if (IsComposerSessionWorking(owner.Origin.SessionId) || _agentSelector.HasPendingModel)
        {
            // The selector owns the directory's catalog; committing a choice performs a fresh
            // membership check and guarded subsequent-provider-turn mutation, even while running.
            var rows = _agentSelector.ModelChoices.Where(m => m.Id is not null && m.ProviderId is not null && m.Name is not null)
                .Select(m => new OpenCodeModelPickerRow(m.ProviderId!, m.Id!, m.Name!, null, m.Enabled == true,
                    m.Variants?.Where(v => v.Id is not null).Select(v => v.Id!).ToArray() ?? Array.Empty<string>(), null)).ToImmutableArray();
            OpenCodeModelCurrent current = _agentSelector.CurrentModel is { Id: { } id, ProviderId: { } provider } model
                ? new OpenCodeModelCurrent.Explicit(new(provider, id, model.Variant), rows.Any(r => r.ModelId == id && r.ProviderId == provider && r.Available))
                : new OpenCodeModelCurrent.Unknown();
            return new(owner.Origin.SessionId, rows, current, "接纳后影响后续模型回合；不会改动正在进行的模型请求。", new DeferredModelPickerBasis(owner));
        }
        var client = owner.Ticket.Binding.Client;
        var state = await ReadNativeAsync(owner, () => client.GetSessionControlStateAsync(owner.Origin.SessionId, ct), ct);
        var catalog = await ReadNativeAsync(owner, () => client.GetSelectableModelsAsync(state.Directory, ct), ct);
        SessionControlDefaultModel? inherited = null;
        if (state.Model is null)
            inherited = await ReadNativeAsync(owner, () => client.GetDefaultModelAsync(state.Directory, ct), ct);
        if (catalog.Directory != state.Directory || inherited is not null && inherited.Directory != state.Directory)
            return null;
        return PresentNativeModels(new(owner, state, catalog, inherited,
            IsComposerSessionWorking(owner.Origin.SessionId)));
    }

    private static OpenCodeModelPickerPresentation PresentNativeModels(ModelPickerBasis basis)
    {
        static OpenCodeModelChoiceKey Key(SessionControlModelRef m) => new(m.ProviderId, m.Id, m.Variant);
        OpenCodeModelCurrent current = basis.State.Model is { } explicitModel
            ? new OpenCodeModelCurrent.Explicit(Key(explicitModel), basis.Catalog.Models.Any(m => m.Offers(explicitModel)))
            : new OpenCodeModelCurrent.Inherited(basis.Inherited?.Model is { } inherited
                ? new(inherited.ProviderId, inherited.Id, null) : null);
        return new(basis.State.SessionId, basis.Catalog.Models.Select(m => new OpenCodeModelPickerRow(
            m.ProviderId, m.Id, m.Name, null, m.Enabled, m.Variants, null)).ToImmutableArray(), current,
            "仅影响之后的回合；确认前会重新读取，状态变化时需要重新选择。", basis);
    }

    private static bool MatchesDisplayedModel(ModelPickerBasis basis, SessionControlPreview fresh, SessionControlModelRef choice)
        => fresh.State.Fingerprint == basis.State.Fingerprint && fresh.Catalog?.Directory == basis.Catalog.Directory
        && fresh.Request.Model == choice
        && basis.Catalog.Models.SingleOrDefault(m => m.Offers(choice)) is { } displayed
        && fresh.Catalog.Models.SingleOrDefault(m => m.Offers(choice))?.Fingerprint == displayed.Fingerprint
        && fresh.Inherited?.Model?.Fingerprint == basis.Inherited?.Model?.Fingerprint;

    private async Task ChooseNativeModelAsync(NativeActionOwner owner, CancellationToken ct, bool variantOnly = false,
        Microsoft.UI.Xaml.FrameworkElement? anchor = null, OpenCodeModelChoiceKey? selectedChoice = null)
    {
        var sourceKey = owner.Ticket.Binding.Source.Identity;
        var presentation = await ReadNativeModelsAsync(owner, ct);
        if (presentation is null) return;
        if (selectedChoice is { } requested)
        {
            var state = new OpenCodeModelPickerState(presentation);
            if (!state.SelectModel(requested.ProviderId, requested.ModelId) || !state.SelectVariant(requested.Variant)) return;
            var confirmation = state.TryFreezeConfirmation();
            if (confirmation is not null) await CommitNativeModelAsync(owner, confirmation, ct, variantOnly);
            return;
        }
        await OpenCodeRecentModels.Shared.LoadAsync();
        if (ct.IsCancellationRequested || !CanPublishNativeOperation(owner) || !CanStartNativeAction(owner, owner.Origin)) return;
        _nativeDialogPending = true;
        OpenCodeModelPickerResult choice;
        try { choice = variantOnly
            ? await OpenCodeVariantPickerDialog.ShowAsync(XamlRoot, presentation, anchor)
            : await OpenCodeModelPickerDialog.ShowAsync(XamlRoot, presentation, () => ReadNativeModelsAsync(owner, ct), sourceKey, anchor: anchor,
                favoriteClient: owner.Ticket.Binding.Client,
                favoriteDirectory: presentation.Basis is ModelPickerBasis modelBasis ? modelBasis.Catalog.Directory : owner.Ticket.Binding.Slot?.Directory,
                favoriteCancellation: ct); }
        finally { _nativeDialogPending = false; }
        await CommitNativeModelAsync(owner, choice, ct, variantOnly);
    }

    private async Task CommitNativeModelAsync(NativeActionOwner owner, OpenCodeModelPickerResult choice, CancellationToken ct, bool variantOnly = false)
    {
        if (!CanStartNativeAction(owner, owner.Origin) || choice.Outcome != OpenCodeModelPickerOutcome.Confirmed
            || choice.Choice is not { } key) return;
        if (choice.Presentation?.Basis is DeferredModelPickerBasis deferred && ReferenceEquals(deferred.Owner, owner))
        {
            var state = new OpenCodeModelPickerState(choice.Presentation);
            if (state.SelectModel(key.ProviderId, key.ModelId) && state.SelectVariant(key.Variant))
                _agentSelector.SelectPendingModel(new() { Id = key.ModelId, ProviderId = key.ProviderId, Variant = key.Variant });
            return;
        }
        if (choice.Presentation?.Basis is not ModelPickerBasis basis || !ReferenceEquals(basis.Owner, owner)) return;
        var model = new SessionControlModelRef(key.ModelId, key.ProviderId, key.Variant);
        if (basis.DeferSelection || IsComposerSessionWorking(owner.Origin.SessionId) || _agentSelector.HasPendingModel)
        {
            // The selector persists this choice now using the same guarded subsequent-turn route.
            _agentSelector.SelectPendingModel(new() { Id = key.ModelId, ProviderId = key.ProviderId, Variant = key.Variant });
            return;
        }
        var fresh = await _sessionControls.PreviewAsync(owner.Origin, new(SessionControlKind.SwitchModel, model), owner.Draft,
            o => CanStartNativeAction(owner, o), ct);
        if (!CanStartNativeAction(owner, owner.Origin)) return;
        if (fresh.Preview is not { } preview || !MatchesDisplayedModel(basis, preview, model))
        { ReportNativeControlStatus(SessionControlStatus.ConfirmationRequired); return; }
        var result = await _sessionControls.ConfirmAsync(preview, owner.OperationId, owner.Draft,
            o => CanStartNativeAction(owner, o), o => OwnsNativeOperation(owner, o), ct);
        if (!CanPublishNativeOperation(owner)) return;
        if (result.AcceptanceKnown)
        {
            ShowMessage(InfoBarSeverity.Success, variantOnly ? "推理强度选择已接纳" : "模型选择已接纳",
                (variantOnly ? "仅影响之后的回合。" : "仅影响之后的回合，不代表模型正在执行。") +
                (result.Observed is null ? "当前显示状态未能重新读取。" : string.Empty));
            _agentSelector.RefreshDisplay(); // Read-only status line re-reads the real session state.
        }
        else ReportNativeControlStatus(result.Status);
    }

    private async Task RunNativeRollbackAsync(NativeActionOwner owner, CancellationToken ct)
    {
        var undo = owner.Ticket.Kind == OpenCodeComposerClientActionKind.Undo;
        var kind = undo ? SessionControlKind.StageRevert : SessionControlKind.ClearRevert;
        var read = await _sessionControls.PreviewAsync(owner.Origin, new(kind), owner.Draft,
            o => CanStartNativeAction(owner, o), ct);
        if (!CanStartNativeAction(owner, owner.Origin)) return;
        if (read.Preview is not { } preview) { ReportNativeControlStatus(read.Status); return; }
        var shown = new OpenCodeRollbackPreviewPresentation(undo ? OpenCodeRollbackKind.Undo : OpenCodeRollbackKind.Redo,
            owner.Origin.SessionId, preview.Target?.Text, preview.Target?.Id, preview.State.Revert is not null,
            preview.PriorStageMayHaveFileEffects, preview.Target?.HasNonTextReferences == true,
            preview.Target is { ReferencesComplete: false }, owner.Draft.Empty, preview);
        _nativeDialogPending = true;
        OpenCodeRollbackConfirmationResult confirmation;
        try { confirmation = await OpenCodeRollbackConfirmationDialog.ShowAsync(XamlRoot, shown); }
        finally { _nativeDialogPending = false; }
        if (!CanStartNativeAction(owner, owner.Origin) || confirmation.Outcome != OpenCodeRollbackOutcome.Confirmed
            || !ReferenceEquals(confirmation.Preview, shown)
            || undo && (shown.TargetHasNonTextReferences || shown.ReferencesIncomplete) && !confirmation.TextOnlyAcknowledged) return;
        if (undo && confirmation.RestoreFiles)
        {
            // Files changes the opaque backend preview. Prepare that exact choice, then require a NEW
            // confirmation. Never forge a fingerprint, nor silently retarget after the first click.
            var fileRead = await _sessionControls.PreviewAsync(owner.Origin, new(kind, Files: true), owner.Draft,
                o => CanStartNativeAction(owner, o), ct);
            if (!CanStartNativeAction(owner, owner.Origin)) return;
            if (fileRead.Preview is not { } filesPreview || filesPreview.State.Fingerprint != preview.State.Fingerprint
                || filesPreview.Target?.Fingerprint != preview.Target?.Fingerprint)
            { ReportNativeControlStatus(SessionControlStatus.ConfirmationRequired); return; }
            preview = filesPreview;
            shown = shown with { Identity = preview };
            _nativeDialogPending = true;
            try { confirmation = await OpenCodeRollbackConfirmationDialog.ShowAsync(XamlRoot, shown); }
            finally { _nativeDialogPending = false; }
            if (!CanStartNativeAction(owner, owner.Origin) || confirmation.Outcome != OpenCodeRollbackOutcome.Confirmed
                || !ReferenceEquals(confirmation.Preview, shown) || !confirmation.RestoreFiles
                || (shown.TargetHasNonTextReferences || shown.ReferencesIncomplete) && !confirmation.TextOnlyAcknowledged)
            { ReportNativeControlStatus(SessionControlStatus.ConfirmationRequired); return; }
        }
        var result = await _sessionControls.ConfirmAsync(preview, owner.OperationId, owner.Draft,
            o => CanStartNativeAction(owner, o), o => OwnsNativeOperation(owner, o), ct);
        if (!CanPublishNativeOperation(owner)) return;
        if (!result.AcceptanceKnown)
        {
            if (result.MutationStarted && result.Status == SessionControlStatus.OutcomeUnknown)
            {
                // A started rollback may have changed the projection even without a readable reply.
                // Re-read only; its retained protection remains untouched and there is no retry.
                ResetAcceptedHistoryProjection(_messageRevertBoundary, _messageHistoryBoundaryKnown);
                _messageHistoryNeedsState = true;
                await RefreshMessagesAsync(explicitRefresh: true);
            }
            if (CanPublishNativeOperation(owner)) ReportNativeControlStatus(result.Status);
            return;
        }
        ApplyNativeHistoryChange(owner, undo ? preview.Target!.Id : null, result.Observed);
        if (undo && result.Restoration is { } restoration)
        {
            if (!TryApplyNativeText(owner, restoration.Text)) ShowNativeTextCandidate(restoration.Text);
        }
        else if (undo)
        {
            // Acceptance without a matched observed boundary does not authorize automatic restoration.
            ShowNativeTextCandidate(preview.Target!.Text ?? string.Empty);
        }
        else ShowMessage(InfoBarSeverity.Success, "撤回状态已清除", "整个撤回状态已清除；输入框内容保持不变。");
        await RefreshMessagesAsync(explicitRefresh: true);
    }

    private bool TryApplyNativeText(NativeActionOwner owner, string text)
    {
        if (_composerView?.PrepareClientTextApplication() != true || !CanStartNativeAction(owner, owner.Origin)) return false;
        _applyingNativeText = true;
        try
        {
            if (_composerController!.TryApplyClientText(owner.Ticket, text) is null) return false;
            _nativeEditorTicket = null; // Our successful application must not stale its own dialog.
            _composerView.RequestRender();
            return true;
        }
        finally { _applyingNativeText = false; }
    }

    private async Task EditNativeDraftAsync(NativeActionOwner owner, string initial)
    {
        var dialog = _nativeEditor = new OpenCodeDraftEditorDialog();
        _nativeEditorTicket = owner.Ticket;
        _nativeDialogPending = true;
        OpenCodeDraftEditorResult result;
        try
        {
            result = await dialog.ShowAsync(XamlRoot, initial, text => TryApplyNativeText(owner, text)
                ? OpenCodeDraftEditorApplyOutcome.Applied : OpenCodeDraftEditorApplyOutcome.Stale,
                Composer.SelectionStart, Composer.SelectionLength);
        }
        finally { _nativeDialogPending = false; _nativeEditor = null; _nativeEditorTicket = null; }
        if (result.Outcome == OpenCodeDraftEditorOutcome.Applied && OwnsComposerBinding(owner.Ticket.Binding))
            RestoreExpandedComposerSelection(owner.Ticket.Binding, dialog.SelectionStart, dialog.SelectionLength);
        if (result.RetainedText is { } retained && CanPublishNativeOperation(owner))
            ShowNativeTextCandidate(retained);
    }

    private void ShowNativeTextCandidate(string text)
    {
        ShowMessage(InfoBarSeverity.Warning, "保留的文字候选（未写入草稿）", text);
    }

    private void ReportNativeControlStatus(SessionControlStatus status)
        => ShowMessage(InfoBarSeverity.Warning, "操作未执行或尚未确认", status switch
        {
            SessionControlStatus.ConfirmationRequired => "会话、目标、模型或草稿已变化；请重新打开并明确确认，不会自动改选或重试。",
            SessionControlStatus.InsufficientHistory => "历史不完整，无法安全确定目标；未执行。",
            SessionControlStatus.NothingToUndo => "没有可撤回的用户消息。",
            SessionControlStatus.NoStage => "当前没有撤回状态。",
            SessionControlStatus.OutcomeUnknown => "结果未知，保护已保留；不会自动重试或解除。",
            SessionControlStatus.DefinitivelyRejected => "服务器明确拒绝了此次操作；草稿保持不变。",
            _ => "当前来源、执行状态或保护不允许操作；草稿保持不变。",
        });

    private void ApplyNativeHistoryChange(NativeActionOwner owner, string? boundary, SessionControlState? observed)
    {
        if (!CanPublishNativeOperation(owner)) return;
        ResetAcceptedHistoryProjection(observed is null ? boundary : observed.Revert?.MessageId, observed is not null);
        _messageHistoryNeedsState = observed is null;
    }

    // Accepted undo/redo, or owned staged-send effects (accepted OR unresolved). A reset does not
    // prove the current stage is empty, nor which cross-client boundary was committed.
    private void ResetAcceptedHistoryProjection(string? boundary, bool boundaryKnown = true)
    {
        var children = _historyUnresolvedChildren;
        ResetMessagePaginationState();
        _historyUnresolvedChildren = children; // Removed launch rows cannot prove their children finished.
        _messageRevertBoundary = boundary;
        _messageHistoryNeedsState = !boundaryKnown;
        _messageHistoryBoundaryKnown = boundaryKnown;
        _historyExecutionRebaseline = true;
        _historyAcceptedPromptId = null;
        InvalidateViewAcknowledgement(); // Does not erase unknown-attempt keys.
        // Cancel old ACK starters as well as GETs. Merely nulling their live snapshot could revive
        // a pre-epoch ACK when this same session publishes its next visible snapshot.
        CancelSessionOperations();
        _sessionCts = new CancellationTokenSource();
        if (_selectedSessionId is { } id)
        {
            _lastIdleBySession.Remove(id);
        }
        _conversationView.ClearMessages();
        UpdateEmptyState();
    }

    // L2 seams (OpenCodePage.NewSessionTab.cs): open the blank tab; arm the exact draft handoff before the created
    // session is bound; afterwards mirror it into the tab strip (the blank tab becomes that session's tab).
    partial void RequestNewSessionTab();
    partial void OnNativeSessionCreating(string sessionId);
    partial void OnNativeSessionCreated(string sessionId, OpenCodeComposerBinding? binding, OpenCodeComposerToken? token);

    private async Task CreateNativeSessionAsync(OpenCodePageSource source, OpenCodeClient client,
        Func<bool> canStart, Func<bool> ownsOperation, CancellationToken ct, OpenCodeBlankDraftPreferences? preferences = null)
    {
        if (preferences is null && _newSessionOperation is not null) return;
        if (preferences is not null && (preferences.BlankSlotId == Guid.Empty
            || _blankCreationResults.ContainsKey((source.Identity, preferences.BlankSlotId)))) return;
        var operation = Guid.NewGuid();
        _newSessionOperation = operation;
        var originBlank = preferences is not null && _blankTabs.TryGetValue(preferences.BlankSlotId, out var blank) ? blank : null;
        var originSlot = originBlank?.Slot;
        if (originSlot is not null) originSlot.CreateOperation = operation;
        Task<OpenCodeSessionInfo>? pending = null;
        try
        {
            source.TryExecute(ct, () =>
            {
                if (canStart())
                {
                    // Register BEFORE invoking transport. Draft edits, activation, close and epoch
                    // changes must never re-issue an uncertain create for the same stable blank.
                    if (preferences is not null) _blankCreationResults[(source.Identity, preferences.BlankSlotId)] = null;
                    pending = preferences is null ? client.CreateSessionAsync(null, ct)
                        : client.CreateSessionConfiguredAsync(preferences.Directory, preferences.Agent, preferences.Model, ct);
                }
            });
            if (pending is null) return;
            var session = await pending;
            if (preferences is not null)
            {
                _blankCreationResults[(source.Identity, preferences.BlankSlotId)] = session.Id;
                if (originSlot is not null) originSlot.CreatedSessionId = session.Id;
                RecordCreatedBlankSlot(source, preferences, session);
            }
            if (!ownsOperation() || source.IsRevoked || _newSessionOperation != operation) return;
            if (session.Id is { Length: > 0 } id)
            {
                SetConnectionState(ConnectionState.Connected);
                InsertSessionItemFirst(session);
                RefreshSessionStateUi();
                RaiseSessionTracked(id, OpenCodeSessionTrackReason.Created);
                if (ownsOperation())
                {
                    OnNativeSessionCreating(id);
                    OpenCodeComposerBinding? createdBinding = null;
                    OpenCodeComposerToken? createdToken = null;
                    await SwitchToSessionAsync(id, onBound: () =>
                    {
                        createdBinding = _composerBinding;
                        createdToken = _composerController?.Snapshot.Token;
                        if (preferences is not null) ConvertCreatedBlankTab(preferences.BlankSlotId, id, select: true);
                    }, forceDraftTransition: preferences is not null);
                    OnNativeSessionCreated(id, createdBinding, createdToken);
                    if (preferences is not null && _newSessionCreatedId is null && _pageActive
                        && ReferenceEquals(source, _source) && ReferenceEquals(client, _client) && !source.IsRevoked
                        && _selectedSessionId == id)
                        ShowMessage(InfoBarSeverity.Informational, "会话已创建，未发送", "草稿已保留；原输入归属已变化，请检查后明确发送。");
                }
            }
        }
        catch (Exception error)
        {
            if (preferences is not null && pending is not null && error is OpenCodeApiException { StatusCode: >= 400 and < 500 and not 408 })
                _blankCreationResults.Remove((source.Identity, preferences.BlankSlotId)); // Known rejection permits an explicit corrected attempt.
            if (ownsOperation() && !source.IsRevoked)
                ShowMessage(InfoBarSeverity.Warning, "创建会话未确认", pending is null
                    ? "未开始创建。" : "创建结果未确认；不会自动重试，请先检查会话列表。");
        }
        finally
        {
            if (originSlot?.CreateOperation == operation)
            {
                originSlot.CreateOperation = null;
                if (ReferenceEquals(_activeDraftSlot, originSlot)) _newSessionCreatePending = false;
            }
            if (_newSessionOperation == operation)
            {
                _newSessionOperation = null;
            }
        }
    }
}
