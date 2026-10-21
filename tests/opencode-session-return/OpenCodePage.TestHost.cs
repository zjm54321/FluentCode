// Test-only page shell. Navigation, return, binding, list operations, ClearMessages and ACK
// deduplication are linked/extracted production code. No navigation algorithms live here.
// Message loading, interaction widgets, App ACK transport and agent/recovery UI are boundaries.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode
{
    // Queue-presentation event payload; this harness does not construct the queue widget.
    internal sealed record OpenCodePendingDeliveryRequest(string InboxId, string Delivery);
    internal static class OpenCodeQueuedPromptsView
    {
        // Queue rows are not supplied by these return/child-preview fixtures.
        internal static bool IsSteerUser(OpenCodeInboxItem item) => false;
        internal static string ReadText(System.Text.Json.JsonElement? payload) => string.Empty;
    }
}

namespace Microsoft.UI.Xaml { public class SizeChangedEventArgs : EventArgs { } }
namespace Microsoft.UI.Xaml.Controls { public class ScrollViewerViewChangedEventArgs : EventArgs { } }
namespace Microsoft.UI.Xaml.Media
{
    public static class CompositionTarget
    {
        public static event EventHandler<object>? Rendering;
        internal static void Frame() => Rendering?.Invoke(null, new());
    }
}

namespace AIFrontend.Pages
{
    public sealed class ReturnHostDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class SelectionChangedEventArgs : EventArgs { }
    public sealed class ListViewItem : ContentControl { public object? Tag { get; set; } }
    public sealed class ListView : FrameworkElement
    {
        private object? _selected;
        public ListView() => Items = new ItemCollection(this);
        public ItemCollection Items { get; }
        public event Action<object, SelectionChangedEventArgs>? SelectionChanged;
        internal List<object> Scrolls { get; } = new();
        public object? SelectedItem
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                SelectionChanged?.Invoke(this, new());
            }
        }
        public void ScrollIntoView(object item) => Scrolls.Add(item);
        public sealed class ItemCollection(ListView owner) : Collection<object>
        {
            protected override void ClearItems() { base.ClearItems(); owner.SelectedItem = null; }
            protected override void RemoveItem(int index)
            {
                var old = this[index];
                base.RemoveItem(index);
                if (ReferenceEquals(old, owner.SelectedItem)) owner.SelectedItem = null;
            }
        }
    }
}
namespace AIFrontend
{
    // The real ACK helper is exercised up to this App boundary. No real POST/ACK reconciliation.
    public sealed class App : Application
    {
        internal sealed record Ack(string Endpoint, string Session, double Watermark, Func<bool> Fence);
        internal List<Ack> Acks { get; } = new();
        public Task<bool?> AcknowledgeOpenCodeSessionAsync(OpenCodePageSource source, long revision,
            string sessionId, double watermark, Func<bool> fence, CancellationToken token)
        {
            Acks.Add(new(source.Identity, sessionId, watermark, fence));
            return Task.FromResult<bool?>(null); // Unknown result; the real helper must still deduplicate.
        }
    }
}
namespace AIFrontend.Pages
{
    // These add only UI members absent from the reused inert ConversationView test host.
    public sealed class ReturnHostTextBlock : TextBlock { }
    public sealed class ReturnHostButton : Button
    {
        private bool _enabled;
        internal TaskCompletionSource<bool>? EnabledSignal;
        public new bool IsEnabled
        {
            get => _enabled;
            set { _enabled = value; if (value) EnabledSignal?.TrySetResult(true); }
        }
    }
    internal sealed class ReturnHostTimer { internal void Stop() { } }
    internal sealed class ReturnHostScroll : ScrollViewer
    {
        public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged { add { } remove { } }
        internal double VerticalOffset { get; private set; }
        internal double ScrollableHeight => 0;
        internal void UpdateLayout() { }
        internal void ChangeView(double? horizontalOffset, double? verticalOffset, double? zoomFactor, bool disableAnimation)
        { if (verticalOffset is { } offset) VerticalOffset = offset; }
    }
    internal sealed class ReturnHostRecovery
    {
        internal void HideConfirm() { }
        internal void HideEntry() { }
        internal void SetBusy(bool busy, string? message) { }
    }
    internal sealed class ReturnHostInteractions
    {
        internal int FocusCount, SuppressCount, ClearCount;
        internal void ClearAll() => ClearCount++;
        internal bool TryFocusCard(bool isForm, string id) { FocusCount++; return true; }
        internal void Suppress(bool isForm, string id) => SuppressCount++;
        internal void MarkFormBlocked(string id, string message) { }
        internal void MarkPermissionBlocked(string id, string message) { }
    }

    public sealed partial class OpenCodePage : FrameworkElement
    {
        private OpenCodePageSource? _source;
        private OpenCodeClient? _client;
        private long _sourceRevision;
        private int _sourceGeneration, _pageGeneration, _sessionGeneration;
        private bool _pageActive;
        private string? _selectedSessionId;
        // L2: blank "新会话" layout flag (production field lives in OpenCodePage.Composer.cs).
        private bool _newSessionDraftActive = false;
        private bool _homeActive = false;
        private void ResetHomeSource() => _homeActive = false;
        internal bool BlankTabActive { set => _newSessionDraftActive = value; }
        // L2 follow-up: zero-session refresh requests the blank tab (production: OpenCodePage.NewSessionTab.cs / Tabs.cs).
        private bool _noOpenTabsShown = false;
        internal bool NoOpenTabsShown { set => _noOpenTabsShown = value; }
        internal int NewSessionTabRequests;
        private void RequestNewSessionTab() => NewSessionTabRequests++;
        private CancellationTokenSource? _pageCts, _sessionCts;
        private bool _suppressSessionSelection, _sessionMissing;
        private readonly Dictionary<string, ListViewItem> _sessionItems = new(StringComparer.Ordinal);
        private readonly OpenCodeConversationView _conversationView = new();
        private readonly ReturnHostScroll MessageScroll = new();
        private readonly Border _ackRoot = new();
        private readonly ScrollContentPresenter _ackPresenter = new();
        private bool _ackTreeBound;
        private const int MaxScrollSettlePasses = 3;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, OpenCodeSessionInfo> HomeSessionMetadata = new();
        private bool _isRefreshing, _refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit;
        private readonly ReturnHostInteractions _interactionsView = new();
        private readonly ReturnHostRecovery _sessionRecoveryPanel = new();
        private ReturnHostTimer? _pollTimer = null;
        private object? _recoveryOrigin = null;
        private bool _sessionProtectionHint = false, _interactionBusy = false, _deliveredNoticeShown = false;
        private int _mutationEpoch;
        private readonly HashSet<string> _pendingAckIds = new();
        private readonly Dictionary<string, double> _lastIdleBySession = new();
        private readonly HashSet<string> _workingSessions = new();
        private enum ConnectionState { Unknown, Connected, Failed }
        private event EventHandler? TrustedEndpointIdentityChanged { add { } remove { } }
        internal readonly ReturnHostButton SessionReturnButton = new() { Visibility = Visibility.Collapsed };
        internal readonly ListView SessionList = new();
        internal readonly ReturnHostTextBlock SessionsHint = new();
        private readonly InfoBar PermissionBar = new(), MessageBar = new();
        public ReturnHostDispatcher DispatcherQueue { get; } = new();
        public new bool IsLoaded => true;
        public new double ActualWidth => 800;
        public new double ActualHeight => 600;
        internal readonly Queue<Task> MessageLoads = new();
        internal TaskCompletionSource<string> LoadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Tracked = new();
        internal readonly List<OpenCodeNavigationStatus> NavigationStatuses = new();
        internal int BottomScrolls, MessageCount, QueuedNavigations;
        internal string? Viewed => _selectedSessionId;
        internal string Label => SessionReturnLabel;
        internal bool CanReturn => CanReturnToSession;
        internal object? ContextIdentity => _sessionReturnContext;
        internal object? AttemptIdentity => _sessionReturnAttempt;
        internal string? ReturnTarget => _sessionReturnContext?.TargetSessionId;
        internal int SessionGeneration => _sessionGeneration;
        internal OpenCodeConversationView Conversation => _conversationView;
        internal int FocusCount => _interactionsView.FocusCount;
        internal int ResultSuppressions => _interactionsView.SuppressCount;
        // Read otherwise unrelated fields so warnings-as-errors remains enabled without suppressions.
        internal object Diagnostics => (_recoveryOrigin, _sessionProtectionHint, _interactionBusy,
            _deliveredNoticeShown, _mutationEpoch, _sessionMissing, _isRefreshing, _refreshQueued,
            _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit,
            _firstMessagesLoading, _messagesResponseEmpty, _messagesLoadError, _historyExecutionRebaseline);

        public OpenCodePage(OpenCodePageSource source)
        {
            BindFixture(source);
            SessionList.SelectionChanged += SessionList_SelectionChanged;
        }
        // Fixture boot/reload binding, not an imitation of source reconciliation or navigation.
        internal void BindFixture(OpenCodePageSource source)
        {
            _source = source;
            _sourceRevision = source.Revision;
            _client = source.CreateClient();
            _pageActive = true;
            _pageCts = new();
        }
        internal void ChangeSource(OpenCodePageSource source) { InvalidateForSourceChange(); BindFixture(source); }
        internal void Unload() => OnPageUnloaded(this, new());
        private const string FixtureMessageDirectory = "/synthetic/return-fixture";
        internal Task Switch(string id) => SwitchToSessionAsync(id, directory: FixtureMessageDirectory);
        internal Task<int> Navigate(string id) => NavigateToSessionAsync(id);
        internal Task Card(string messageId, string childId) => NavigateToSubagentSessionAsync(_conversationView, messageId, childId);
        internal Task Preview(string messageId, string childId) => ShowSubagentChatAsync(_conversationView, messageId, childId);
        internal Task Notify(NotificationNavigationTarget target, NotificationInteractionResult? result = null)
            => ProcessNavigationTargetAsync(target, result);
        internal string? Rebuild(List<OpenCodeSessionInfo> sessions) => RebuildSessionList(sessions);
        internal Task Return() => ReturnToSessionAsync();
        internal void ClickReturn() => SessionReturnButton_Click(SessionReturnButton, new());
        internal void RefreshReturnUi() => UpdateSessionReturnUi();
        // Controllable asynchronous boundary: tests explicitly render fixtures through real ApplyMessages.
        private Task RefreshMessagesAsync(bool initial)
        {
            // Existing message-load boundary supplies synthetic completed-load metadata. It does not
            // run a real parent history GET; A3's real boundary/directory adoption is tested separately
            // by message-pagination. The real Switch/no-op still checks the full binding and source.
            if (_messageBinding is { } binding) _messageBinding = binding with { Directory = FixtureMessageDirectory };
            _messageHistoryBoundaryKnown = true;
            _messageHistoryNeedsState = false;
            LoadEntered.TrySetResult(_selectedSessionId ?? string.Empty);
            return MessageLoads.Count == 0 ? Task.CompletedTask : MessageLoads.Dequeue();
        }
        private void RaiseSessionTracked(string id, OpenCodeSessionTrackReason reason) => Tracked.Add(id);
        private void ScrollToBottom(bool animated) => BottomScrolls++;
        private void ShowMessage(InfoBarSeverity severity, string title, string detail) => MessageCount++;
        private void RaiseNavigationHandled(OpenCodeNavigationStatus status, string detail, NotificationNavigationTarget? target)
            => NavigationStatuses.Add(status);
        private void EnqueueNavigation(NotificationNavigationTarget target, NotificationInteractionResult? result) => QueuedNavigations++;
        internal OpenCodeMarkdownImageScope? Images => _conversationImages;
        internal void RebindImages() => EnsureConversationImages();
        internal void ReplaceHistoryToken() { CancelSessionOperations(); _sessionCts = new(); }
        private void ResetSessionPaginationState() { }
        private void UpdateWaitingText() { }
        private void UpdateEmptyState() { }
        private void UpdateComposerButtons() { }
        // No composer is constructed by this return-navigation host. Current extracted lifecycle methods
        // notify its binding boundary; composer/continuation behavior remains outside this harness's proof.
        private void BindComposer() { }
        private void UpdateInteractionsSection() { }
        private void ConfigureAgentSelectorForSession(string id) { }
        private void ClearAgentSelector() { }
        private void DetachConversationEvents() { }
        private void UnsubscribeAppSourceChanged() { }
        private void OpenCodePage_TrustedEndpointIdentityChanged(object? sender, EventArgs e) { }
        private void SetConnectionState(ConnectionState state) { }
        private void HandleApiException(OpenCodeException ex, string context) { }
        private void RestartSessionEventSync(string id) { }
        private void StopEventSync() { }
        // Inert native layout input only: uses the real ConversationView CaptureIdleViewport query.
        internal void ProveViewport()
        {
            if (!_ackTreeBound)
            {
                _ackRoot.Child = MessageScroll;
                MessageScroll.Content = _ackPresenter;
                _ackPresenter.Content = _conversationView;
                _ackTreeBound = true;
            }
            void Prepare(UIElement element)
            {
                if (element is FrameworkElement fe)
                {
                    fe.XamlRoot = XamlRoot; fe.IsLoaded = true; fe.ActualWidth = 500; fe.ActualHeight = 500;
                    fe.TestTransform = _ => new Microsoft.UI.Xaml.Media.GeneralTransform(0);
                }
                if (element is Panel panel) foreach (var child in panel.Children) Prepare(child);
                if (element is Border { Child: { } borderChild }) Prepare(borderChild);
                if (element is ContentControl { Content: UIElement content }) Prepare(content);
            }
            Prepare(_ackRoot);
            _conversationView.RaiseLayoutUpdatedForTest();
            Microsoft.UI.Xaml.Media.CompositionTarget.Frame();
        }
    }
}
