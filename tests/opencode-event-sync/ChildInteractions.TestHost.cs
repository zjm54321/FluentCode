// Page surface and unrelated transcript/agent rendering boundaries. All interaction routing,
// source/session fencing, permission HTTP, gate leases and overlay/card state are production code.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage : FrameworkElement
{
    partial void OnMessageHistoryWorkflowEnded();
    partial void OnSessionBackgroundActivity(string sessionId, bool working);
    private readonly record struct SessionRecoveryOrigin(OpenCodePageSource Source, long Revision, int SourceGeneration,
        string SessionId, int SessionGeneration, string RequestId, NotificationNavigationKind Kind);
    private OpenCodePageSource? _source;
    private OpenCodeClient? _client;
    private long _sourceRevision;
    private int _sourceGeneration = 1, _sessionGeneration = 1;
    private bool _pageActive = true, _sessionMissing = false, _interactionBusy = false, _sessionProtectionHint = false;
    private int _mutationEpoch;
    private SessionRecoveryOrigin? _recoveryOrigin;
    private string? _selectedSessionId;
    private CancellationTokenSource? _pageCts = new(), _sessionCts;
    private readonly OpenCodeInteractionsView _interactionsView = new();
    private readonly RecoverySurface _sessionRecoveryPanel = new();
    private readonly OpenCodePermissionOverlay _permissionOverlay = new();
    private bool _composerCoveredByPermission, _isComposing = false, _suppressComposerFocus = false;
    private readonly Grid ComposerCell = new(), ComposerQueueHost = new(), PermissionOverlayHost = new(), QuestionOverlayHost = new();
    private readonly Border ComposerCard = new();
    private readonly TextBox Composer = new();
    private readonly TextBlock InteractionsHeader = new();
    private readonly Grid InteractionsSection = new();
    private readonly InfoBar PermissionBar = new();
    private IReadOnlyList<OpenCodeInboxItem>? _queuedInbox;
    private string? _queuedInboxSessionId;
    private readonly List<string> _pendingAckIds = new();
    private bool _newSessionDraftActive = false, _homeActive = false;
    private readonly SelectorSurface _agentSelector = new();
    private OpenCodePageSource? _agentSelectorSource;
    private long _agentSelectorRevision;
    private string? _agentSelectorSessionId;
    private bool FullScreenComposerActive => false;
    private object? _fullScreenBinding = null;
    private bool OwnsComposerBinding(object binding) => false;
    private void CloseFullScreenComposer(bool focus) { }
    private void ExitShellComposer(bool focus) { }
    private void RefreshChildStopButton() { }
    private void RefreshChildReturnButton() { }
    private void RefreshComposerView() { }
    private void UpdateComposerButtons() => Composer.IsEnabled = !ComposerCoveredByOverlay;
    private bool SourceReady => _source is { IsRevoked: false } && _client is not null;
    private CancellationTokenSource? _eventCts;
    private TaskCompletionSource? _eventReady;
    private bool _eventSnapshotBeforeReady = false, _eventReconciliationPending = false, _eventProjectionDirty;
    private readonly ConcurrentQueue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _eventQueue = new();
    private readonly Queue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _workflowEventQueue = new();
    private object? _messageHistoryWorkflowOwner = null;
    private bool _messageHistoryNeedsState = false;
    private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new();
    private readonly Dictionary<string, OpenCodeSessionRetryMark> _liveSessionRetries = new();
    private long _liveStatusVersion;
    private OpenCodeEventReducer? _eventReducer;
    private readonly List<OpenCodeMessage> _latestMessages = new();
    private readonly ConversationSurface _conversationView = new();
    private bool _hostWindowVisible = true;
    private bool HostAttentionVisible => _hostWindowVisible; // Activation is fixed true in this inert ACK boundary.
    private bool _isRefreshing = false;
    private ImmutableArray<string> _historyUnresolvedChildren = ImmutableArray<string>.Empty;
    private bool _firstMessagesLoading = false;
    private string? _boundSessionId;
    private readonly Dictionary<string, OpenCodeSessionInfo> _sessionItems = new();
    private enum SessionReturnKind { VisitedOrigin }
    private sealed record SessionReturnTarget(string SessionId, SessionReturnKind Kind);
    private SessionReturnTarget? _returnTarget;
    internal readonly List<(InfoBarSeverity Severity, string Title, string Detail)> Notices = new();
    internal int MessageRefreshes, BottomScrolls;
    internal Task LastDecision = Task.CompletedTask;

    internal OpenCodePage(OpenCodePageSource source)
    {
        _source = source; _sourceRevision = source.Revision; _client = source.CreateClient();
        PermissionOverlayHost.Visibility = QuestionOverlayHost.Visibility = Visibility.Collapsed; // Actual XAML defaults.
        ComposerCard.Child = Composer; ComposerCell.Children.Add(ComposerCard);
        ComposerCell.Children.Add(PermissionOverlayHost); PermissionOverlayHost.Children.Add(_permissionOverlay);
        ComposerCell.Children.Add(QuestionOverlayHost);
        _interactionsView.PermissionDecisionRequested += (_, e) => LastDecision = SubmitPermissionDecisionAsync(e.Request, e.Decision);
        _interactionsView.PermissionRecheckRequested += (_, e) => LastDecision = RecheckPermissionAsync(e.RequestId);
        _interactionsView.ResolutionRequested += (_, e) => LastDecision = ResolveInteractionAsync(e.IsForm, e.Id, e.Retry);
        _interactionsView.FormSubmitRequested += (_, e) => LastDecision = SubmitFormAnswerAsync(e.Form, e.Answer);
    }
    internal string? Selected => _selectedSessionId;
    internal bool PermissionVisible => ComposerCell.Visibility == Visibility.Visible && PermissionOverlayHost.Visibility == Visibility.Visible && _permissionOverlay.Current is not null;
    internal bool ChildEditorHidden => ComposerCard.Visibility == Visibility.Collapsed;
    internal OpenCodePermissionCard? CurrentPermission => _permissionOverlay.Current;
    internal int PermissionCount => _permissionOverlay.Count;
    internal IReadOnlyList<OpenCodeFormCard> Forms => _interactionsView.FormCards;
    internal string Draft { get => Composer.Text; set => Composer.Text = value; }
    internal bool ComposerEnabled => Composer.IsEnabled;
    internal bool RecoveryConfirm => _sessionRecoveryPanel.IsShowingConfirm;
    internal bool Busy => _interactionBusy;
    internal bool ChildReadsIdle => _childInteractionBinding is { Discovering: false } binding && binding.Sessions.Values.All(s => !s.Reading);
    internal CancellationTokenSource? ChildCancellation => _childInteractionBinding?.Cancellation;
    internal string? RecoverySession => _recoveryOrigin?.SessionId;
    internal object Diagnostics => (_queuedInbox, _queuedInboxSessionId, _returnTarget, _historyUnresolvedChildren, _firstMessagesLoading);
    internal async Task Select(string sessionId)
    {
        await SwitchToSessionAsync(sessionId, directory: "/synthetic");
        _agentSelector.CurrentSessionInfo = await _client!.GetSessionInfoAsync(sessionId);
        _agentSelectorSource = _source; _agentSelectorRevision = _sourceRevision; _agentSelectorSessionId = sessionId;
        RefreshChildComposerVisibility();
    }
    internal void Receive(OpenCodeEvent item) { _eventQueue.Enqueue((_eventCts!, item)); DrainEventQueue(); }
    internal void Connected(bool reconnect) => OnEventStreamConnected(_eventCts!, reconnect);
    internal void Poll(bool connected)
    {
        if (connected) _eventReady!.TrySetResult(); else _eventReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PollTimer_Tick(new(), new());
    }
    internal void BeginWorkflow() => _messageHistoryWorkflowOwner = new();
    internal void EndWorkflow() { _messageHistoryWorkflowOwner = null; OnMessageHistoryWorkflowEnded(); }
    internal void BeginComposition() { _isComposing = true; Composer.Focus(FocusState.Programmatic); }
    internal void EndComposition() { _isComposing = false; QueueDeferredComposerOverlayRefresh(); }
    internal Task Decide(OpenCodePermissionRequest request, string decision) => SubmitPermissionDecisionAsync(request, decision);
    internal Task Recheck(string id) => RecheckPermissionAsync(id);
    internal Task Recover(bool retry) => CompleteSessionRecoveryAsync(retry);
    internal Task ReloadParent() => RefreshMessagesAsync();
    internal void FocusPermission(string id) => _interactionsView.TryFocusCard(false, id);
    internal void Revoke() { _source!.Revoke(); StopEventSync(); }
    internal void DisposeFixture() { StopEventSync(); _pageCts?.Cancel(); _sessionCts?.Cancel(); _client?.Dispose(); }
    private void StartEventSync() { _eventCts?.Cancel(); _eventCts = new(); _eventReady = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private void ResetRuntimeFeedback() { }
    private void RefreshCurrentRetry() { }
    private void RetainHistoryChildEvidence(string sessionId, IReadOnlyList<OpenCodeMessage> messages) { } // Real history/child projection is linked by message-pagination, not this permission UI fixture.
    private void OnBrowserEventSyncStopped() { }
    private void OnBrowserEventStreamHealthChanged(bool connected) { }
    private void OnBrowserControlEvent(OpenCodeEvent item) { }
    private void OnAgentTreeReset() { }
    private void OnAgentTreeEvent(OpenCodeEvent item) { }
    private void OnAgentTreeConnected() { }
    private void OnSessionInsightsEvent(OpenCodeEvent item) { }
    private void OnPersistentPtyEvent(OpenCodeEvent item) { }
    private void RefreshSessionStateUi() { }
    private void RefreshComposerSessionActivity() { }
    private void RefreshConversationSessionActivity() { }
    private void RefreshAgentSelectorDisplay() { }
    private Task RefreshSessionsAsync() => Task.CompletedTask;
    private Task RefreshListedSessionStatusesAsync() => Task.CompletedTask;
    private bool IsSelectedBackgroundChildCompletion(OpenCodeEvent item) => false;
    private IReadOnlyList<OpenCodeMessage> MergeMessageHistory() => _latestMessages;
    private IReadOnlyList<OpenCodeMessage> ProjectMessageHistory(IReadOnlyList<OpenCodeMessage> messages) => messages;
    private void EnsureConversationImages() { }
    private void OnContextMessagesAccepted(IReadOnlyList<OpenCodeMessage> messages, IReadOnlyList<OpenCodeMessage>? changedMessages = null) { }
    private void UpdateMessageWindowBudget() { }
    private void UpdateMessageWindowBudget(OpenCodeMessage previous, OpenCodeMessage accepted) { }
    private int _pageGeneration = 1; // Native page lifetime boundary; child-permission tests keep one loaded page.
    private bool _messageHistoryBoundaryKnown = true, _isLoadingOlderMessages = false;
    private string? _messageRevertBoundary = null;
    private void ObserveExecutionState(string session, IReadOnlyList<OpenCodeMessage> messages) { }
    private void NotifyViewAcknowledgementRendered(IReadOnlyList<OpenCodeMessage> messages, bool visible) { }
    private void InvalidateViewAcknowledgement() { }
    private bool IsScrolledToBottom() => false;
    private void ScrollToBottom(bool animated) => BottomScrolls++;
    private void UpdateEmptyState() { }
    private void UpdateWaitingText() { }
    private void ShowMessage(InfoBarSeverity severity, string title, string detail) => Notices.Add((severity, title, detail));
    private static string DescribeApiError(OpenCodeApiException error, string operation) => operation + ": " + error.StatusCode;
    private async Task RefreshMessagesAsync(bool initial = false, bool forceFollow = false, bool explicitRefresh = false)
    {
        MessageRefreshes++;
        var source = _source!; var client = _client!; var generation = _sessionGeneration; var session = _selectedSessionId!;
        var read = OpenCodeAuxiliarySnapshot.ReadAsync(client, session, _sessionCts?.Token ?? CancellationToken.None);
        await RefreshAuxiliaryAsync(read, session, () => IsCurrentBinding(source, source.Revision, _sourceGeneration, client, generation));
    }
    private bool TryPrepareDraftSlotNavigation() => true;
    private void RestoreDraftSlotTabSelection() { }
    private void ForceSuspendDraftSlot(string reason) { }
    private string? KnownSessionDirectory(string id) => "/synthetic";
    private bool IsSameMessageBinding(string id, string? directory) => _boundSessionId == id;
    private void BeginSessionMessageBinding(string id, string? directory) => _boundSessionId = id;
    private void OnSessionContextSelected(string id) { }
    private void ClearSessionReturnContext() => _returnTarget = null;
    private void InstallSessionReturnContext(SessionReturnTarget? target) => _returnTarget = target;
    private void BeginTranscriptSwitchMotion() { }
    private void RevealTranscript() { }
    private void BindComposer() { }
    private void ConfigureAgentSelectorForSession(string id) { }
    private void RaiseSessionTracked(string id, OpenCodeSessionTrackReason reason) { }
    private sealed class SelectorSurface { internal OpenCodeSessionInfo? CurrentSessionInfo; }
    private sealed class ConversationSurface
    {
        internal readonly List<FrameworkElement> Children = new();
        internal long FullApplyCount => 0;
        internal long FastApplyCount => 0;
        internal long UnchangedApplyCount => 0;
        internal long UpdateLayoutCount => 0;
        internal void BeginSessionSwitchLayout() { }
        internal void EndSessionSwitchLayout() { }
        internal void ApplyMessages(IReadOnlyList<OpenCodeMessage> messages) { }
        internal bool TryApplyMessageChanges(IReadOnlyList<OpenCodeMessage> changed, object owner, long baseline, long version) => false;
        internal void BindIncrementalSnapshot(object owner, long version) { }
    }
    private sealed class RecoverySurface
    {
        internal bool IsShowingConfirm, IsShowing;
        internal void HideConfirm() { IsShowingConfirm = false; IsShowing = false; }
        internal void HideEntry() => IsShowing = false;
        internal void ShowEntry(string detail) => IsShowing = true;
        internal void ShowConfirm(string detail, bool allowRetry) { IsShowingConfirm = true; IsShowing = true; }
        internal void SetBusy(bool busy, string? label) { }
    }
}
