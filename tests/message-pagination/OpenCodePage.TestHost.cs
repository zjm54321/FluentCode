// Test host for OpenCodePage.MessagePagination.cs: only the WinUI/XAML surface and unrelated page
// members (normally supplied by OpenCodePage.xaml.cs + XAML codegen) are substituted with minimal
// fakes. The production pagination partial and the real OpenCode transport compile as-is; no
// pagination behavior is re-implemented here except OrderMessages (mirrors xaml.cs verbatim).
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Windows.ApplicationModel.DataTransfer
{
    public sealed class DataPackage { internal string? Text; public void SetText(string text) => Text = text; }
    public static class Clipboard
    {
        internal static string? Text; internal static int Writes;
        public static void SetContent(DataPackage data) { Text = data.Text; Writes++; }
    }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }

    public class RoutedEventArgs : EventArgs { }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Button
    {
        public Visibility Visibility { get; set; }

        public bool IsEnabled { get; set; }
    }

    public class TextBlock
    {
        public string Text { get; set; } = string.Empty;
        public Visibility Visibility { get; set; }
    }

    public partial class ScrollViewer
    {
        public double VerticalOffset { get; set; }

        public double ScrollableHeight { get; set; }

        public void UpdateLayout() { }

        public void ChangeView(double? horizontalOffset, double? verticalOffset, double? zoomFactor, bool disableAnimation)
        {
            if (verticalOffset is { } value)
            {
                VerticalOffset = value;
            }
        }
    }

    public enum InfoBarSeverity { Informational, Success, Warning, Error }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        // 同步执行排队回调：测试中无真实布局，有界复核立即收敛。
        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }
}

namespace AIFrontend.Pages
{
    /// <summary>记录每次 ApplyMessages 的应用快照（不渲染）：测试据此断言应用顺序、去重与内容更新。</summary>
    public sealed partial class OpenCodeConversationView
    {
        public IReadOnlyList<OpenCodeMessage> Children { get; private set; } = Array.Empty<OpenCodeMessage>();
        public List<IReadOnlyList<OpenCodeMessage>> Applied { get; } = new();
        public bool? TurnActivity { get; private set; }
        public int ActivityPublications { get; private set; }
        public void SetCurrentTurnActivity(bool? active) { TurnActivity = active; ActivityPublications++; }

        public void ApplyMessages(IReadOnlyList<OpenCodeMessage> messages) { Applied.Add(messages); Children = messages; InvalidateGeometry(); }

        public void ClearMessages() { Applied.Clear(); Children = Array.Empty<OpenCodeMessage>(); InvalidateGeometry(); }
    }

    public sealed partial class OpenCodePage
    {
        private OpenCodeEventReducer? _eventReducer = null; // Real reducer is enabled by the controlled readiness cases.
        // ---- 替代 OpenCodePage.xaml.cs（未参与本测试编译）的最小页面面 ----
        private readonly OpenCodeConversationView _conversationView = new();
        // F3: no "load older" button; the production partial reports its presentation state through this hook.
        private bool _testMoreAvailable, _testCanAutoLoad, _testLoadingShown;
        internal int OlderUiUpdates;
        partial void OnOlderMessagesUiChanged(bool hasMore, bool canLoad, bool loading)
        { _testMoreAvailable = hasMore; _testCanAutoLoad = canLoad; _testLoadingShown = hasMore && loading; OlderUiUpdates++; }
        private readonly ScrollViewer MessageScroll = new();

        private Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();

        private int _pageGeneration;
        private int _sessionGeneration;
        private bool _pageActive;
        private bool _sessionMissing;
        private string? _selectedSessionId;
        private OpenCodeClient? _client;
        private OpenCodePageSource? _source;
        private long _sourceRevision;
        private int _sourceGeneration;
        private CancellationTokenSource? _sessionCts;
        private bool _testSourceReady;
        private bool _isRefreshing, _refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit;
        private readonly Dictionary<string, string> _lastIdleBySession = new();
        private readonly HashSet<string> _workingSessions = new();
        internal int AckPublications => _ackApp.Calls;
        internal int ExecutionPublications => _lastIdleBySession.Count;
        internal readonly List<OpenCodeExecutionStateChangedEventArgs> ExecutionEvents = new();
        private void RaiseExecutionState(OpenCodeExecutionStateChangedEventArgs args) => ExecutionEvents.Add(args);
        internal bool AckInvalidated => _viewAcknowledgementLive is null;
        internal int AttemptKeys => _viewAcknowledgementAttempted.Count;
        internal void SeedUnknownAck() => _viewAcknowledgementAttempted.Add(new("endpoint", SessionForTests, 42));
        private const string SessionForTests = "ses_test";
        private bool IsScrolledToBottom() => false;
        private void ScrollToBottom(bool animated) { }
        private readonly TextBlock EmptyStateText = new();
        private void UpdateScrollToBottomButton() { }
        private void RefreshRightPanelMetadata() { } // B2 native slot projection is outside this pagination host.
        private void RefreshWslQuickAccess() { } // B12 native quick-access projection is outside this pagination host.
        private void UpdateComposerButtons() { }
        internal Func<bool>? LastAuxiliaryFence;
        internal Func<Task> Auxiliary = () => Task.CompletedTask;
        private Task RefreshAuxiliaryAsync(OpenCodeClient client, string id, int generation, CancellationToken ct, Func<bool> historyCurrent)
        { LastAuxiliaryFence = historyCurrent; return Auxiliary(); }

        private bool SourceReady => _testSourceReady && _client is not null;
        // Existing runtime-feedback visuals are an explicit native presentation boundary in this headless host.
        private void RefreshCurrentRetry() { }
        private void ResetRuntimeFeedback() { }
        private bool _homeActive = true;
        private string _homeRuntimeSource = "runtime-a";
        private long _homeRevision = 7;
        private readonly Dictionary<string, AIFrontend.Pages.OpenCode.OpenCodeHomeProject> _homeProjects = new(StringComparer.Ordinal);
        internal bool TestHomeEvent(long revision, AIFrontend.Pages.OpenCode.OpenCodeHomeProjectKey key) => HomeEventCurrent(revision, key);
        internal void TestHomeDirectory(string directory) { _homeActive = true; _homeProjects[directory] = new(new(_homeRuntimeSource, directory), directory); }
        internal void TestReplaceHomeSource() { _homeRuntimeSource = "runtime-b"; _homeRevision++; }
        // Native selector and existing SwitchToSessionAsync are explicit boundaries here. The actual
        // extracted Home router must call that switch even when the hidden selected object is unchanged.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, OpenCodeSessionInfo> HomeSessionMetadata = new();
        private object? _testHiddenSelection;
        private string? _homeMessage;
        internal readonly List<string> HomeSwitchRequests = new();
        internal int HomeMirrors, HomeProjectionRequests;
        private void QueueHomeProjection() { CaptureHomeActionRoots(); HomeProjectionRequests++; }
        private volatile HomeActionOwner? _homeActionOwner;
        private sealed record HomeActionRoot(string Directory, string? Title);
        private volatile System.Collections.Immutable.ImmutableDictionary<string, HomeActionRoot> _homeActionRoots = System.Collections.Immutable.ImmutableDictionary<string, HomeActionRoot>.Empty;
        private readonly OpenCodeSessionControlCoordinator _sessionControls = new(OpenCodeInteractionGate.Shared);
        private CancellationTokenSource? _homeCancellation = new();
        private object? _homeDialog = null;
        private bool _isSending = false;
        private bool HasPendingNativeAction() => false;
        private void RefreshComposerView() { }
        internal Func<Task<string?>> HomeConfirmation = () => Task.FromResult<string?>(null);
        internal string? HomeDialogTitle, HomeDialogId;
        private Task<string?> ConfirmHomeSessionActionAsync(HomeActionOwner owner, OpenCodeHomeSessionAction action)
        { HomeDialogTitle = owner.Title; HomeDialogId = owner.SessionId; return HomeConfirmation(); }
        private readonly Dictionary<string, object> _sessionTabs = new();
        private readonly Dictionary<string, string> _sessionTitles = new();
        private string? _activeTabSessionId = null;
        private sealed record TestReturnContext(string TargetSessionId);
        private TestReturnContext? _sessionReturnContext = null;
        private bool IsSessionReturnContextCurrent(TestReturnContext context) => ReferenceEquals(_sessionReturnContext, context);
        internal int DeletedBindings, MetadataApplications, Bindings, RemovedTabs;
        private void RemoveSessionTab(object tab) { RemovedTabs++; }
        private void DeselectSessionForNewTab() { DeletedBindings++; _selectedSessionId = null; CancelSessionOperations(); ResetMessagePaginationState(); }
        private void BindComposer() { Bindings++; if (_selectedSessionId is { } id) HomeSwitchRequests.Add(id); }
        // Draft/agent native integration is outside this headless message-binding host.
        private bool TryPrepareDraftSlotNavigation() => true;
        private void RestoreDraftSlotTabSelection() { }
        private void ForceSuspendDraftSlot(string reason) { }
        private void OnSessionContextSelected(string sessionId) { }
        private int _sessionListEpoch;
        private string? _homeSessionsMessage = null;
        private int BeginSessionRefresh() => ++_sessionListEpoch;
        private void RestartSessionPagination(string? next) { }
        private string? RebuildSessionList(List<OpenCodeSessionInfo> sessions)
        {
            MetadataApplications++; _sessionItems.Clear();
            foreach (var info in sessions) { var item = new object(); _sessionItems[info.Id!] = item; HomeSessionMetadata.Add(item, info); }
            CaptureHomeActionRoots(); return null;
        }
        internal Task TestRunHomeAction(long revision, string directory, string id, OpenCodeHomeSessionAction action)
            => RunHomeSessionActionAsync(revision, new(new(_homeRuntimeSource, directory), new() { Id = id }), action);
        internal void TestAcceptHomeRoot(string id, string directory, string title)
        {
            TestHomeDirectory(directory); var item = new object(); _sessionItems[id] = item;
            HomeSessionMetadata.Add(item, new() { Id = id, Title = title, Location = new() { Directory = directory } });
            CaptureHomeActionRoots();
        }
        internal void TestHomeCosmeticRefresh() { _homeRevision++; CaptureHomeActionRoots(); }
        internal void TestHomeSelected(string id) { _selectedSessionId = id; _sessionCts = new(); _sessionTabs[id] = new(); }
        internal string? TestHomeRootTitle(string id) => _sessionItems.TryGetValue(id, out var item) && HomeSessionMetadata.TryGetValue(item, out var info) ? info.Title : null;
        internal bool TestHomeBusy => _homeActionOwner is not null;
        internal string? TestHomeStatus => _homeSessionsMessage ?? _homeMessage;
        internal void TestHomeResetActionScope() { _homeActionOwner = null; TestReplaceHomeSource(); }
        internal async Task<bool> TestHomeComposerProtected(string id)
        { using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(_source!.Identity, id); return !lease.CanSubmitComposer(); }
        private void MirrorSessionSelection(string sessionId, bool userInitiated) { HomeMirrors++; _homeActive = false; }
        private void SelectSessionItem(string sessionId) { if (_sessionItems.TryGetValue(sessionId, out var item)) _testHiddenSelection = item; }
        private sealed record SessionReturnTarget(string Id);
        private void ClearSessionReturnContext() { _sessionReturnContext = null; }
        private void InstallSessionReturnContext(SessionReturnTarget? target) { }
        private void RestartSessionEventSync(string sessionId) { StopEventSync(); }
        private sealed class Surface
        {
            internal bool IsOpen;
            internal void ClearAll() { }
            internal void HideConfirm() { }
            internal void SetBusy(bool busy, object? message) { }
        }
        private readonly Surface _interactionsView = new(), _sessionRecoveryPanel = new(), PermissionBar = new();
        private bool _sessionProtectionHint = false, _interactionBusy = false, _deliveredNoticeShown = false;
        private object? _recoveryOrigin = null;
        private long _mutationEpoch;
        private readonly HashSet<string> _pendingAckIds = new();
        internal bool TestResetSurface => !_sessionProtectionHint && !_interactionBusy && !_deliveredNoticeShown && _recoveryOrigin is null;
        private void ConfigureAgentSelectorForSession(string sessionId) { }
        private void UpdateWaitingText() { }
        private void UpdateInteractionsSection() { }
        private void RaiseSessionTracked(string sessionId, OpenCodeSessionTrackReason reason) { }
        internal object TestPrepareHomeClick(string id, string directory)
        {
            _homeActive = true; _selectedSessionId = null; _sessionCts = null;
            TestHomeDirectory(directory);
            var item = new object(); _sessionItems[id] = item; _testHiddenSelection = item;
            HomeSessionMetadata.Add(item, new() { Id = id, Location = new() { Directory = directory } });
            return item;
        }
        internal Task TestOpenHome(long revision, string directory, string id) => OpenHomeSessionAsync(revision, new(_homeRuntimeSource, directory), id);
        internal void TestHomeAction(long revision, string directory, string id, OpenCodeHomeSessionAction action)
            => CopyHomeSessionId(revision, new(_homeRuntimeSource, directory), id, action);
        internal bool TestHiddenSelectionIs(object value) => ReferenceEquals(_testHiddenSelection, value);
        internal void TestHomeUnavailable() { _homeActive = true; _testSourceReady = false; }
        internal bool TestHomeUnavailableExplained => !string.IsNullOrEmpty(_homeMessage) && HomeProjectionRequests > 0;

        private const int MaxScrollSettlePasses = 3;

        private enum ConnectionState { NotConfigured, Unknown, Connected, Failed }

        internal readonly List<(InfoBarSeverity Severity, string Title, string Message)> Notices = new();

        private void ShowMessage(InfoBarSeverity severity, string title, string message) =>
            Notices.Add((severity, title, message));

        private void SetConnectionState(ConnectionState state)
        {
        }

        private void HandleApiException(OpenCodeException ex, string context)
        {
        }

        private bool IsScrollContextCurrent(int pageGeneration, int sessionGeneration, string? sessionId) =>
            _pageActive
            && pageGeneration == _pageGeneration
            && sessionGeneration == _sessionGeneration
            && string.Equals(sessionId, _selectedSessionId, StringComparison.Ordinal);

        // ---- 测试钩子（仅本测试编译可见） ----
        internal void TestActivate(string sessionId, OpenCodeClient client, Func<OpenCodeClient>? clientFactory = null)
        {
            InvalidateViewAcknowledgement();
            StopEventSync();
            // 镜像 xaml.cs：进入页面 / 选中会话各自的代际递增（测试中仅一次）。
            _pageGeneration++;
            _sessionGeneration++;
            _testSourceReady = true;
            _client = client;
            _sourceRevision++;
            _sourceGeneration++;
            _source = new OpenCodePageSource("pagination-" + Guid.NewGuid(), _sourceRevision, clientFactory ?? (() => client));
            _pageActive = true;
            _sessionMissing = false;
            _selectedSessionId = sessionId;
            _sessionCts = new CancellationTokenSource();
            _homeActive = false;
            Microsoft.UI.Xaml.Application.Current = _ackApp;
            AttachViewAcknowledgementEvents();
            NotifyHostWindowVisibility(true);
        }

        internal void TestApplyRefresh(List<OpenCodeMessage> asc, string? nextCursor, bool explicitRefresh)
        {
            // Direct ordinary-pagination fixtures supply a known boundary; they are not a GET bypass.
            _messageHistoryBoundaryKnown = true; _messageHistoryNeedsState = false;
            ApplyRefreshedMessages(asc, nextCursor, explicitRefresh);
        }

        internal Task TestLoadOlderAsync() => LoadOlderMessagesAsync();
        internal Task TestLatestAsync() => RefreshMessagesAsync();
        internal Task TestSwitchAsync(string id, string? directory = null, Action? onBound = null) => SwitchToSessionAsync(id, directory: directory, onBound: onBound);
        internal Task TestNavigateAsync(string id) => NavigateToSessionAsync(id);
        internal string TestEmptyText => EmptyStateText.Text;
        internal bool TestRefreshQueued => _refreshQueued;
        internal string? TestBindingDirectory => _messageBinding?.Directory;
        internal int TestCachedBindings => _retainedMessageWindows.Count;
        internal long TestCachedBytes => System.Linq.Enumerable.Sum(_retainedMessageWindows, slot => slot.Bytes);
        internal int TestCachedRows => System.Linq.Enumerable.Sum(_retainedMessageWindows, slot => slot.Older.Count + slot.Latest.Count);
        internal void TestRevokeSource() { _source!.Revoke(); ResetMessagePaginationState(); _conversationView.ClearMessages(); }
        internal void TestUnload() { _pageActive = false; _pageGeneration++; CancelSessionOperations(); ResetMessagePaginationState(); _conversationView.ClearMessages(); }
        internal void TestRemoveDirectory(string id) => _sessionItems.Remove(id);
        internal void TestReplaceSource()
        {
            _source = new OpenCodePageSource(_source!.Identity, ++_sourceRevision, () => _client!);
            _sourceGeneration++;
        }
        internal object TestOlderBuffer => _olderMessages;
        internal object TestLatestBuffer => _latestMessages;
        internal void TestAdvanceRevisionOnly() => _sourceRevision++;
        internal void TestAdvancePageGeneration() => _pageGeneration++;
        internal void TestForeignSource() { _source = new OpenCodePageSource("foreign-source", _sourceRevision, () => _client!); }
        internal void TestAcceptedHistory(string? boundary) => ResetAcceptedHistoryProjection(boundary);
        internal long TestEpoch => _messageHistoryEpoch;
        internal int TestAppliedCount => _conversationView.Applied.Count;
        internal IReadOnlyList<string> TestAppliedTexts => _conversationView.Applied.ConvertAll(rows =>
            rows.Count == 1 && rows[0].Content is { Count: > 0 } parts ? parts[0].Text ?? "" : "");
        internal bool TestOlderBusy => _isLoadingOlderMessages;
        internal void TestBeginSend(long owner) => BeginMessageHistoryWorkflow(owner);
        internal void TestEndSend(long owner, bool reset) => EndMessageHistoryWorkflow(owner, reset);
        internal bool TestNeedsState => _messageHistoryNeedsState;
        internal void TestUnreadSelection() => ResetMessagePaginationState();
        internal bool TestWorking => _workingSessions.Contains(_selectedSessionId!);
        internal void TestAcceptedWorking(string id)
        { _historyAcceptedPromptId = id; MarkWorking(_selectedSessionId!); }
        internal void TestObserve(List<OpenCodeMessage> rows) => ObserveExecutionState(_selectedSessionId!, rows);
        internal IReadOnlyList<OpenCodeMessage> TestRetainedRows => MergeMessageHistory();

        internal bool TestHistoryComplete => IsLoadedMessageHistoryComplete();

        internal void TestHardReset() => ResetMessagePaginationState();

        internal OpenCodeContextEstimateResult TestContextEstimate(OpenCodeModelRef model, OpenCodeModelInfo modelInfo) =>
            OpenCodeContextEstimate.Estimate(_selectedSessionId!, MergeMessageHistory(), model, modelInfo,
                IsLoadedMessageHistoryComplete());

        internal string? TestNextCursor => _messageNextCursor;

        internal bool TestMoreAvailable => _testMoreAvailable;
        internal bool TestCanAutoLoad => _testCanAutoLoad;
        internal bool TestLoadingShown => _testLoadingShown;
        internal double TestScrollOffset => MessageScroll.VerticalOffset;

        /// <summary>Simulates a timeline ViewChanged at the given offset (drives the production auto-load path).</summary>
        internal void TestScroll(double offset, double scrollableHeight)
        {
            MessageScroll.ScrollableHeight = scrollableHeight;
            MessageScroll.VerticalOffset = offset;
            OnMessageScrollViewChangedForHistory();
        }

        internal IReadOnlyList<OpenCodeMessage> TestLastApplied()
        {
            if (_conversationView.Applied.Count == 0)
            {
                throw new InvalidOperationException("No ApplyMessages call recorded.");
            }

            return _conversationView.Applied[^1];
        }

        internal int TestWarningNoticeCount()
        {
            var count = 0;
            foreach (var notice in Notices)
            {
                if (notice.Severity == InfoBarSeverity.Warning)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
