using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

// Page-only orchestration: binding, UI intents and receipt publication. The controller owns drafts,
// transport initiation and typed protection; the standalone view owns presentation/input policy.
public sealed partial class OpenCodePage
{
    private OpenCodeComposerController? _composerController;
    private OpenCodeComposerView? _composerView;
    private OpenCodeComposerBinding? _composerBinding;
    private bool _composerPickerPending;
    private bool _suppressComposerFocus;
    // Redesign §3 (L2): the blank "新会话" layout is shown with no selected session. The same composer then binds
    // an UNBOUND binding (controller never sends it); Enter asks OpenCodePage.NewSessionTab.cs to create first.
    private bool _newSessionDraftActive;
    private bool _newSessionCreatePending;
    private string? ComposerBindingSessionId
        => _selectedSessionId ?? (_newSessionDraftActive ? OpenCodeComposerController.UnboundSessionId : null);
    partial void OnUnboundComposerSubmitRequested(OpenCodeComposerToken token);
    // Only durable server admission is published; presentation may deduplicate by receipt.Id.
    partial void OnComposerPromptAdmitted(string sessionId, OpenCodeInboxUser receipt);

    private void InitializeComposer()
    {
        if (_composerController is not null) return;
        ConfigureComposerDelivery(true);
        _composerController = new(OwnsComposerBinding);
        _composerView = new(GetComposerSnapshot, _composerController.BeginCatalogLifetime, _composerController.EndCatalogLifetime);
        _composerView.AttachEditor(Composer, () => _isComposing || _suppressComposerFocus || !_pageActive);
        InitializeComposerLayout();
        ComposerCompanionHost.Children.Add(_composerView);
        ComposerCompanionHost.Visibility = Visibility.Visible;
        // Subscribed once for this page's lifetime, not once per load/session. Both ends are page-owned.
        _composerController.SnapshotChanged += OnComposerSnapshotChanged;
        _composerController.SubmitRequested += OnComposerSubmitRequested;
        _composerController.UnboundSubmitRequested += (_, e) => OnUnboundComposerSubmitRequested(e.Token);
        _composerView.StateChanged += (_, _) => { if (_pageActive) { UpdateComposerButtons(); TryRestoreExpandedComposerSelection(); } };
        _composerView.TextEdited += (_, e) => e.Acknowledgment =
            _composerController.TryApplyEditorInput(e.Token, e.Text, e.AllowUnboundCarry);
        _composerView.CatalogOpenRequested += (_, e) =>
        {
            if (_pageActive && !_isSending && !_composerPickerPending && !_nativeDialogPending && _nativeActionOwner is null)
                _ = _composerController.LoadCatalogAsync(e.Token, e.Lifetime, e.Refresh, _pageCts?.Token ?? CancellationToken.None);
        };
        _composerView.CatalogDismissRequested += (_, e) => _composerController.DismissCatalog(e.Handle);
        _composerView.CommandChosen += (_, e) => e.Accepted = _composerController.TrySelectCommand(e.Token, e.Handle, e.Identity);
        // Tab completion inside the displayed opening: a catalog-proven trigger edit (never a selection or submission).
        // Click on a catalog-declared argument-free server command: the SAME ordinary submission bridge as Enter/Send.
        _composerView.SubmitRequested += (_, _) => RequestComposerSubmission();
        _composerView.CompletionRequested += (_, e) => e.Accepted = !_isSending && _composerController.TryCompleteTrigger(e.Token, e.Handle, e.Completion);
        _composerView.ClientIntentDismissed += (_, e) => _composerController.DismissClientIntent(e.Token, e.IntentId);
        _composerView.ClientActionInvoked += (_, e) =>
        {
            var ticket = _pageActive && !_isSending && !_composerPickerPending && !_nativeDialogPending && _nativeActionOwner is null
                ? _composerController.TryTakeClientAction(e.Token, e.IntentId) : null;
            e.Accepted = ticket is not null;
            if (ticket is not null) DispatchClientAction(ticket); // The capability, never e.Kind, chooses the route.
        };
        _composerView.CommandRemoved += (_, e) => _composerController.RemoveCommand(e.Token);
        _composerView.AttachmentRemoved += (_, e) => _composerController.RemoveAttachment(e.Token, e.Id);
        _composerView.DiscardRequested += (_, e) => _composerController.DiscardDraft(e.Token);
        _composerView.PickRequested += (_, e) => _ = PickComposerFilesAsync(e.Token);
    }

    // Called under the controller/source locks and on worker threads: ONLY captured page field comparisons.
    private bool OwnsComposerBinding(OpenCodeComposerBinding b)
        => _pageActive && !_sessionMissing
        && ReferenceEquals(b.Source, _source) && b.Revision == _sourceRevision
        && b.SourceGeneration == _sourceGeneration && ReferenceEquals(b.Client, _client)
        && b.PageGeneration == _pageGeneration && b.SessionGeneration == _sessionGeneration
        && string.Equals(b.SessionId, ComposerBindingSessionId, StringComparison.Ordinal);

    private void BindComposer()
    {
        if (_composerController is null) return;
        _composerBinding = _pageActive && !_sessionMissing && _source is { IsRevoked: false } source
            && _client is { } client && ComposerBindingSessionId is { } sessionId
            ? new(source, _sourceRevision, _sourceGeneration, client, _pageGeneration, _sessionGeneration, sessionId)
            : null;
        _composerController.Bind(_composerBinding);
        InvalidateNativeActionIfBindingLost();
        RefreshComposerView();
    }

    private OpenCodeComposerSnapshot GetComposerSnapshot()
    {
        var snapshot = _composerController!.Snapshot;
        // A native dialog may outlive a binding. Keep the page busy until it returns, and do not let
        // the view offer edits/picking during the separate legacy sender's local submission lifetime.
        return _isSending || _composerPickerPending || _nativeActionOwner is not null || _nativeDialogPending
            || _newSessionCreatePending ? snapshot with { IsBusy = true, CanSubmit = false } : snapshot;
    }

    private void OnComposerSnapshotChanged(object? sender, OpenCodeComposerSnapshotChangedEventArgs e)
    {
        RefreshComposerView(); // Never render a queued event payload.
        CheckNativeEditorOwnership();
    }

    private void RefreshComposerView()
    {
        if (_composerView is null) return;
        // RequestRender is coalesced and pulls the latest snapshot on the UI dispatcher. It does not
        // focus the editor. An unloaded page can render its inactive snapshot, never its old context.
        _composerView.RequestRender();
    }

    private (OpenCodeComposerBinding Binding, OpenCodeComposerToken Token, string Text, int Start, int Length)? _expandedComposerSelection;

    private void RestoreExpandedComposerSelection(OpenCodeComposerBinding binding, int start, int length)
    {
        var snapshot = _composerController!.Snapshot;
        _expandedComposerSelection = (binding, snapshot.Token, snapshot.Text, start, length);
        TryRestoreExpandedComposerSelection();
    }

    private void TryRestoreExpandedComposerSelection()
    {
        if (_expandedComposerSelection is not { } restore) return;
        if (!OwnsComposerBinding(restore.Binding) || _composerController?.Snapshot.Token != restore.Token)
        { _expandedComposerSelection = null; return; }
        if (_isComposing || _suppressComposerFocus || Composer.Text != restore.Text) return;
        var start = Math.Clamp(restore.Start, 0, restore.Text.Length);
        Composer.Select(start, Math.Clamp(restore.Length, 0, restore.Text.Length - start));
        _expandedComposerSelection = null;
        Composer.Focus(FocusState.Programmatic);
    }

    private async Task PickComposerFilesAsync(OpenCodeComposerToken token)
    {
        if (_composerController is null || _composerView is null) return;
        var binding = _composerBinding;
        if (_composerPickerPending || _isSending || HasPendingNativeAction() || binding is null || !OwnsComposerBinding(binding))
        {
            _suppressComposerFocus = true;
            try { _composerView.ReportPickOutcome(OpenCodeComposerPickOutcome.NotAllowed); }
            finally { _suppressComposerFocus = false; }
            return;
        }
        _composerPickerPending = true;
        UpdateComposerButtons();
        RefreshComposerView();
        var outcome = OpenCodeComposerPickOutcome.Cancelled;
        try
        {
            using var cancellation = CreateOperationToken(binding.Source);
            outcome = await OpenCodeComposerFilePicker.PickAsync(_composerController, token,
                (Application.Current as App)?.OpenCodeOwnerWindow, cancellation.Token);
        }
        catch (Exception)
        {
            // Adapter owns exactly-once ticket completion; never retry it from this page.
            outcome = OpenCodeComposerPickOutcome.Failed;
        }
        finally
        {
            _composerPickerPending = false;
            var current = OwnsComposerBinding(binding) && !binding.Source.IsRevoked;
            _suppressComposerFocus = !current;
            try { _composerView.ReportPickOutcome(current ? outcome : OpenCodeComposerPickOutcome.Cancelled); }
            finally { _suppressComposerFocus = false; }
            if (_pageActive) UpdateComposerButtons();
        }
    }

    // Both button and eligible Enter use this one event bridge. Enter never invokes Stop.
    private void RequestComposerSubmission(string? delivery = null)
    {
        if (!_pageActive || _composerController is null || _composerView is null
            || !_composerView.PrepareSubmission() || HasPendingNativeAction()) return;
        var snapshot = GetComposerSnapshot();
        if (!snapshot.IsActive || snapshot.IsBusy || !snapshot.HasDraft) return;
        if (delivery == OpenCodeDeliveryModes.Queue && snapshot.SelectedCommand is not null) return;
        _composerController.RequestSubmit(snapshot.Token, delivery ?? RequestedComposerDelivery);
    }

    private void OnComposerSubmitRequested(object? sender, OpenCodeComposerSubmitRequestedEventArgs e)
    {
        var delivery = e.Delivery ?? RequestedComposerDelivery;
        if (DispatcherQueue.HasThreadAccess) _ = RouteComposerSubmissionAsync(e.Token, delivery);
        else DispatcherQueue.TryEnqueue(() => _ = RouteComposerSubmissionAsync(e.Token, delivery));
    }

    private async Task RouteComposerSubmissionAsync(OpenCodeComposerToken token, string? delivery)
    {
        if (_composerController is null || _composerView is null || !_pageActive
            || !_composerView.PrepareSubmission() || HasPendingNativeAction()) return;
        var snapshot = GetComposerSnapshot();
        if (snapshot.Token != token || !snapshot.IsActive || snapshot.IsBusy || !snapshot.HasDraft) return;
        // IsBoundDraft intentionally does NOT choose transport: removing command/files leaves text bound.
        var route = OpenCodeComposerInputPolicy.Route(snapshot.SelectedCommand is not null, snapshot.Attachments.Count, snapshot.Text);
        if (delivery == OpenCodeDeliveryModes.Queue && snapshot.SelectedCommand is not null) return;
        if (route == OpenCodeComposerSubmitRoute.LegacyText) await SendPromptAsync(delivery);
        else if (route == OpenCodeComposerSubmitRoute.Enhanced) await SubmitEnhancedComposerAsync(token, delivery);
    }

    private bool OwnsComposerSubmission(OpenCodeComposerBinding binding, long localOwner)
        => OwnsComposerBinding(binding) && IsLocalSubmissionOwnerCurrent(localOwner);

    // Core calls these delegates while holding its controller lock. Never acquire the Source lock
    // here: actual initiation/settlement is fenced by Source.TryExecute (Source -> controller).
    private OpenCodeComposerSendContext CreateComposerSendContext(OpenCodeComposerController controller,
        OpenCodeComposerView view, OpenCodeComposerDraftBasis basis, OpenCodeComposerInputEvidence input, long owner)
    {
        var origin = new SessionControlOrigin(basis.Binding.Source, basis.Binding.Revision, basis.Binding.SessionId, owner)
        { PreviouslyKnownUnresolvedChildSessionIds = _historyUnresolvedChildren };
        bool Owns(SessionControlOrigin candidate) => ReferenceEquals(candidate, origin)
            && OwnsComposerSubmission(basis.Binding, owner);
        bool CanStart(SessionControlOrigin candidate) => Owns(candidate) && ReferenceEquals(view, _composerView)
            && !_isComposing && OpenCodeComposerInputEvidence.StillCurrent(input, view.InputEvidence)
            && controller.Snapshot.Token == basis.Token && !HasPendingNativeAction();
        return new(origin, basis, null, CanStart, Owns);
    }

    // UI/publication boundary only; never pass this source-locking liveness check into Core.
    private static bool HasLiveComposerSendOrigin(OpenCodeComposerSendContext context)
        => !context.Origin.Source.IsRevoked && context.OwnsOperation(context.Origin);

    private Task SubmitEnhancedComposerAsync(OpenCodeComposerToken token, string? delivery = null)
        => RunComposerSendWorkflowAsync(token, enhanced: true, delivery);

    private async Task RunComposerSendWorkflowAsync(OpenCodeComposerToken token, bool enhanced, string? delivery = null)
    {
        var controller = _composerController;
        var view = _composerView;
        if (!_pageActive || controller is null || view is null || _isSending || _composerPickerPending
            || HasPendingNativeAction() || !view.PrepareSubmission()) return;
        var input = view.InputEvidence;
        if (!input.IsClean || controller.Snapshot.Token != token) return;
        var localOwner = BeginLocalAgentSubmission();
        _isSending = true;
        OpenCodeComposerSendContext? context = null;
        OpenCodeComposerSubmitOutcome outcome = new OpenCodeComposerSubmitOutcome.NotStarted();
        try
        {
            var basis = controller.CaptureDraft(token, delivery ?? RequestedComposerDelivery);
            if (basis is null) return;
            var binding = basis.Binding;
            if ((basis.ServerCommand is not null || basis.HasFiles) != enhanced) return;
            context = CreateComposerSendContext(controller, view, basis, input, localOwner);
            var origin = context.Origin;
            if (!HasLiveComposerSendOrigin(context) || !context.CanStart(origin)) return;
            BeginMessageHistoryWorkflow(localOwner);
            UpdateComposerButtons();
            RefreshComposerView();
            using var lifetime = CreateOperationToken(binding.Source);
            // Legacy HTTP retains the original session cancellation policy. Local finalization and
            // the dialog additionally observe page/source lifetime, as they did before this adapter.
            var transportCancellation = _sessionCts?.Token ?? CancellationToken.None;
            while (true)
            {
                outcome = enhanced
                    ? await controller.SubmitAsync(context, lifetime.Token, view.HandoffAcceptedDraftAsync)
                    : await SendLegacyAttemptAsync(context, transportCancellation);
                if (!HasLiveComposerSendOrigin(context)) return;
                if (outcome is not OpenCodeComposerSubmitOutcome.ConfirmationRequired required) break;
                var question = required.Question;
                if (outcome.MutationStarted || !ReferenceEquals(question.Origin, origin)
                    || !ReferenceEquals(question.Basis, basis) || !context.CanStart(origin)) return;
                var presentation = new OpenCodeContinuationPresentation(binding.SessionId,
                    basis.ServerCommand is not null, question.ObservedState.Revert?.MayHaveFileEffects == true, question)
                { HasStagedRevert = question.ObservedState.Revert is not null };
                _nativeDialogPending = true;
                OpenCodeContinuationResult answer;
                try { answer = await OpenCodeComposerContinuationDialog.ShowAsync(XamlRoot, presentation, lifetime.Token); }
                finally { _nativeDialogPending = false; }
                if (answer.Outcome != OpenCodeContinuationOutcome.Confirmed
                    || !ReferenceEquals(answer.Presentation, presentation)
                    || !ReferenceEquals(answer.Presentation.Basis, question)
                    || !view.PrepareSubmission() || !HasLiveComposerSendOrigin(context) || !context.CanStart(origin)) return;
                // A new gesture permits only a fresh pre-start read with this SAME origin/basis/owner.
                // This loop never repeats a mutation attempt; changed drafts need a new Send gesture.
                context = context with { Consent = question.ConsentToUse };
            }
            if (!enhanced && outcome is OpenCodeComposerSubmitOutcome.PromptAccepted)
            {
                try
                {
                    await controller.FinalizeAcceptedLegacyDraftAsync(basis.Token, binding,
                        () => context.OwnsOperation(origin), lifetime.Token, view.HandoffAcceptedDraftAsync);
                }
                catch (Exception) { /* Local clearing failure cannot rewrite an accepted receipt. */ }
            }
            if (!HasLiveComposerSendOrigin(context)) return;
            var resetHistory = outcome.Effects?.Revert is OpenCodeComposerRevertEvidence.StagedPromptAccepted
                or OpenCodeComposerRevertEvidence.Unresolved;
            EndMessageHistoryWorkflow(localOwner, resetHistory);
            if (!HasLiveComposerSendOrigin(context)) return;
            switch (outcome)
            {
                case OpenCodeComposerSubmitOutcome.PromptAccepted accepted:
                    PublishComposerReceipt(binding.SessionId, accepted.Receipt);
                    break;
                case OpenCodeComposerSubmitOutcome.CommandAcknowledged:
                    ShowMessage(InfoBarSeverity.Informational, "命令回调已确认", "服务器已确认命令回调（204）；不代表模型执行完成，不创建消息投递跟踪。");
                    break;
                case OpenCodeComposerSubmitOutcome.Rejected:
                    ShowMessage(InfoBarSeverity.Warning, "提交未被接纳", "服务器拒绝了此次提交；草稿已保留。");
                    break;
                case OpenCodeComposerSubmitOutcome.NotStarted:
                    ShowMessage(InfoBarSeverity.Informational, "未发送", "提交未开始；请检查当前会话与保护状态，草稿已保留。");
                    break;
                case OpenCodeComposerSubmitOutcome.Unknown:
                    ShowMessage(InfoBarSeverity.Warning, "提交结果未知", "草稿已保留；不会自动重试，也不会解除未确认结果的保护。");
                    break;
            }
            if (outcome.Effects?.Revert == OpenCodeComposerRevertEvidence.Unresolved)
                ShowMessage(InfoBarSeverity.Warning, "撤回影响尚未确认", "提交结果与撤回影响分别记录；需要重新读取会话。读取不会解除未知结果保护，不会自动重试。");
            await RefreshMessagesAsync(forceFollow: outcome is OpenCodeComposerSubmitOutcome.PromptAccepted,
                explicitRefresh: resetHistory);
        }
        catch (Exception)
        {
            if (context is not null && HasLiveComposerSendOrigin(context))
                ShowMessage(InfoBarSeverity.Warning, "发送流程未完成", "请重新检查当前会话；不会自动重试，也不会解除保护。");
        }
        finally
        {
            if (context is not null && HasLiveComposerSendOrigin(context))
                EndMessageHistoryWorkflow(localOwner, outcome.Effects?.Revert is
                    OpenCodeComposerRevertEvidence.StagedPromptAccepted or OpenCodeComposerRevertEvidence.Unresolved);
            if (EndLocalAgentSubmission(localOwner))
            {
                _isSending = false;
                if (_pageActive) UpdateComposerButtons();
                RefreshComposerView();
            }
        }
    }

    private async Task<OpenCodeComposerSubmitOutcome> SendLegacyAttemptAsync(
        OpenCodeComposerSendContext context, CancellationToken ct)
    {
        var origin = context.Origin;
        var basis = context.Basis;
        var operation = Guid.NewGuid();
        var started = false;
        var settled = false;
        var retained = false;
        SessionControlContinuationPermit? permit = null;
        OpenCodeComposerSubmitOutcome outcome = new OpenCodeComposerSubmitOutcome.NotStarted();
        try
        {
            // This is the actual shared gate, not merely a foreign same-scope lease.
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(origin.Source.Identity, origin.SessionId, ct);
            bool CanStart(SessionControlOrigin o) => context.OwnsOperation(o) && context.CanStart(o);
            if (!CanStart(origin)) return outcome;
            var prepared = await _sessionControls.PrepareContinuationAsync(origin, lease, basis.Draft,
                context.Consent, CanStart, ct);
            permit = prepared.Permit;
            if (prepared.Status == SessionControlStatus.ConfirmationRequired && prepared.State is not null)
                return new OpenCodeComposerSubmitOutcome.ConfirmationRequired(new(origin, basis, prepared.State, prepared.RequiredConsent));
            if (!(prepared.Status == SessionControlStatus.NoStage || prepared.Status == SessionControlStatus.Ready && permit is not null)
                || lease.GetAgentSwitchProtection() is not null || lease.GetComposerSubmissionProtection() is not null
                || lease.GetSessionControlProtection() is not null) return outcome;
            try
            {
                var receipt = await basis.Binding.Client.SendPromptGuardedAsync(origin.SessionId, basis.Text, null, Start, ct, basis.Delivery);
                outcome = new OpenCodeComposerSubmitOutcome.PromptAccepted(receipt);
            }
            catch (OpenCodeApiException error) when (started && error.StatusCode is 400 or 401 or 403 or 404 or 409)
            { outcome = new OpenCodeComposerSubmitOutcome.Rejected(); }
            catch (Exception)
            { outcome = started ? new OpenCodeComposerSubmitOutcome.Unknown() : new OpenCodeComposerSubmitOutcome.NotStarted(); }
            if (started && permit is not null)
            {
                try
                {
                    // Generic 4xx cannot prove that rollback had no effect. Only valid prompt admission
                    // permits successful continuation settlement; all other staged results stay unknown.
                    var status = outcome is OpenCodeComposerSubmitOutcome.PromptAccepted
                        ? NotificationInteractionStatus.Succeeded : NotificationInteractionStatus.OutcomeUnknown;
                    settled = _sessionControls.CompleteContinuation(permit, lease, operation, status, status, context.OwnsOperation);
                }
                catch (Exception) { /* Keep acceptance separate from unresolved protection. */ }
            }
            retained = started && lease.GetSessionControlProtection() is not null;
            if (started && !context.OwnsOperation(origin)) outcome = new OpenCodeComposerSubmitOutcome.OriginLost();

            bool Start(Action transport) => _composerController!.TryStartLegacySend(context, lease, () =>
            {
                if (started || lease.GetAgentSwitchProtection() is not null || lease.GetComposerSubmissionProtection() is not null
                    || lease.GetSessionControlProtection() is not null) return false;
                if (permit is not null)
                    return _sessionControls.TryStartContinuation(permit, lease, operation, basis.Draft, null, CanStart, Send, ct);
                Send(); // Original unstaged legacy policy: NO newly installed shared protection.
                return true;
                void Send() { started = true; transport(); }
            }, ct);
        } // Lease always ends before returning a question, UI, handoff, receipt or refresh.
        catch (Exception)
        {
            if (outcome is not (OpenCodeComposerSubmitOutcome.PromptAccepted or OpenCodeComposerSubmitOutcome.OriginLost))
                outcome = started ? new OpenCodeComposerSubmitOutcome.Unknown() : new OpenCodeComposerSubmitOutcome.NotStarted();
            if (started && permit is not null) retained = true;
        }
        return outcome with { MutationStarted = started,
            Effects = OpenCodeComposerController.DescribeSendEffects(permit, started, outcome, settled, retained) };
    }

    private Task SendPromptAsync(string? delivery = null)
    {
        if (!SourceReady || _selectedSessionId is null || _isSending || _sessionMissing || _composerController is null
            || _composerView is null || _composerPickerPending || HasPendingNativeAction() || !_composerView.PrepareSubmission())
            return Task.CompletedTask;
        var submitted = _composerController.Snapshot;
        if (!submitted.IsActive || submitted.IsBusy || submitted.SelectedCommand is not null || submitted.Attachments.Count != 0)
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(submitted.Text))
        {
            ShowMessage(InfoBarSeverity.Informational, "未发送", "输入为空（仅空白字符）；请输入内容后再发送。");
            Composer.Focus(FocusState.Programmatic);
            return Task.CompletedTask;
        }
        return RunComposerSendWorkflowAsync(submitted.Token, enhanced: false, delivery);
    }

    private void PublishComposerReceipt(string sessionId, OpenCodeInboxUser receipt)
    {
        OnComposerPromptAdmitted(sessionId, receipt);
        if (_historyExecutionRebaseline) _historyAcceptedPromptId = receipt.Id;
        if (receipt.Id is { Length: > 0 } id && !_pendingAckIds.Contains(id))
        {
            _pendingAckIds.Add(id);
            _deliveredNoticeShown = false;
        }
        UpdateWaitingText();
        SetConnectionState(ConnectionState.Connected);
        RaiseSessionTracked(sessionId, OpenCodeSessionTrackReason.PromptSent);
        MarkWorking(sessionId);
    }

}
