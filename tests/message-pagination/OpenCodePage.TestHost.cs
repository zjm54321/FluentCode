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
    }

    public class ScrollViewer
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
    public sealed class OpenCodeConversationView
    {
        public List<IReadOnlyList<OpenCodeMessage>> Applied { get; } = new();

        public void ApplyMessages(IReadOnlyList<OpenCodeMessage> messages) => Applied.Add(messages);

        public void ClearMessages() => Applied.Clear();
    }

    public sealed partial class OpenCodePage
    {
        private OpenCodeEventReducer? _eventReducer = null; // This suite exercises REST pagination; event reducer has its own suite.
        private Task WaitForEventSubscriptionAsync(CancellationToken ct) => Task.CompletedTask;
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
        private bool _hostWindowVisible = true;
        private readonly Dictionary<string, string> _lastIdleBySession = new();
        private readonly HashSet<string> _workingSessions = new();
        internal int AckPublications;
        internal int ExecutionPublications => _lastIdleBySession.Count;
        internal readonly List<OpenCodeExecutionStateChangedEventArgs> ExecutionEvents = new();
        private void RaiseExecutionState(OpenCodeExecutionStateChangedEventArgs args) => ExecutionEvents.Add(args);
        // Storage doubles for the extracted REAL invalidation method; ACK HTTP itself is not replayed.
        private object? _viewAcknowledgementLive = new();
        private bool _viewAcknowledgementWindowVisible = true;
        private readonly HashSet<(string, string, double)> _viewAcknowledgementAttempted = new();
        internal bool AckInvalidated => _viewAcknowledgementLive is null && !_viewAcknowledgementWindowVisible;
        internal int AttemptKeys => _viewAcknowledgementAttempted.Count;
        internal void SeedUnknownAck() => _viewAcknowledgementAttempted.Add(("endpoint", SessionForTests, 42));
        private const string SessionForTests = "ses_test";
        private bool IsScrolledToBottom() => false;
        private void ScrollToBottom(bool animated) { }
        private void UpdateEmptyState() { }
        private void UpdateComposerButtons() { }
        private void NotifyViewAcknowledgementRendered(List<OpenCodeMessage> messages, bool visible) => AckPublications++;
        internal Func<bool>? LastAuxiliaryFence;
        private Task RefreshAuxiliaryAsync(OpenCodeClient client, string id, int generation, CancellationToken ct, Func<bool> historyCurrent)
        { LastAuxiliaryFence = historyCurrent; return Task.CompletedTask; }

        private bool SourceReady => _testSourceReady && _client is not null;

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
        internal void TestActivate(string sessionId, OpenCodeClient client)
        {
            // 镜像 xaml.cs：进入页面 / 选中会话各自的代际递增（测试中仅一次）。
            _pageGeneration++;
            _sessionGeneration++;
            _testSourceReady = true;
            _client = client;
            _sourceRevision++;
            _sourceGeneration++;
            _source = new OpenCodePageSource("pagination-" + Guid.NewGuid(), _sourceRevision, () => client);
            _pageActive = true;
            _sessionMissing = false;
            _selectedSessionId = sessionId;
            _sessionCts = new CancellationTokenSource();
        }

        internal void TestApplyRefresh(List<OpenCodeMessage> asc, string? nextCursor, bool explicitRefresh)
        {
            // Direct ordinary-pagination fixtures supply a known boundary; they are not a GET bypass.
            _messageHistoryBoundaryKnown = true; _messageHistoryNeedsState = false;
            ApplyRefreshedMessages(asc, nextCursor, explicitRefresh);
        }

        internal Task TestLoadOlderAsync() => LoadOlderMessagesAsync();
        internal Task TestLatestAsync() => RefreshMessagesAsync();
        internal void TestAcceptedHistory(string? boundary) => ResetAcceptedHistoryProjection(boundary);
        internal long TestEpoch => _messageHistoryEpoch;
        internal int TestAppliedCount => _conversationView.Applied.Count;
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
