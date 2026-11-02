// Test-only page shell. Navigation, return, binding, list operations, ClearMessages and ACK
// deduplication are linked/extracted production code. No navigation algorithms live here.
// Message loading, interaction widgets, App ACK transport and agent/recovery UI are boundaries.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode
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
namespace Microsoft.UI.Xaml.Media
{
    public static class CompositionTarget
    {
        public static event EventHandler<object>? Rendering;
        internal static void Frame() => Rendering?.Invoke(null, new());
    }
}

namespace FluentCode.Pages
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
namespace FluentCode
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
namespace FluentCode.Pages
{
    // These add only UI members absent from the reused inert ConversationView test host.
    public sealed class ReturnHostTextBlock : TextBlock { }
    internal sealed class ReturnHostTimer { internal void Stop() { } }
    internal sealed class ReturnHostScroll : ScrollView
    {
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
        // Session-bound file readers (production: OpenCodePage.InstructionFiles.cs / OpenCodePage.ToolImages.cs) are not exercised here.
        private Func<string, CancellationToken, Task<string>>? CreateInstructionFileReader(
            OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken) => null;
        private Func<OpenCodeToolImageSource, CancellationToken, Task<OpenCodeImageFile>>? CreateToolImageReader(
            OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken) => null;
        internal bool BlankTabActive { set => _newSessionDraftActive = value; }
        // Home layout boundary for the real RebuildSessionList; full tab actor/selection is
        // compiled and exercised by opencode-tabs, not substituted into this return harness.
        internal bool HomeTabActive { get => _homeActive; set => _homeActive = value; }
        private void ShowHomePage() => _homeActive = true;
        internal int NewSessionTabRequests;
        private void RequestNewSessionTab() => NewSessionTabRequests++;
        private CancellationTokenSource? _pageCts, _sessionCts;
        private bool _sessionMissing;
        private readonly Dictionary<string, OpenCodeSessionInfo> _sessionItems = new(StringComparer.Ordinal);
        private readonly OpenCodeConversationView _conversationView = new();
        private readonly ReturnHostScroll MessageScroll = new();
        private readonly Border _ackRoot = new();
        private readonly ScrollContentPresenter _ackPresenter = new();
        private bool _ackTreeBound;
        private const int MaxScrollSettlePasses = 3;
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
        private readonly InfoBar PermissionBar = new(), MessageBar = new();
        public new ReturnHostDispatcher DispatcherQueue { get; } = new();
        public new bool IsLoaded { get; private set; } = true;
        public new double ActualWidth => 800;
        public new double ActualHeight => 600;
        internal readonly Queue<Task> MessageLoads = new();
        internal TaskCompletionSource<string> LoadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Tracked = new();
        internal readonly List<OpenCodeNavigationStatus> NavigationStatuses = new();
        internal int BottomScrolls, MessageCount, QueuedNavigations;
        internal string? Viewed => _selectedSessionId;
        internal bool CanReturn => IsSessionReturnContextCurrent(_sessionReturnContext)
            && !ReferenceEquals(_sessionReturnContext, _sessionReturnAttempt);
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
        }
        // Fixture boot/reload binding, not an imitation of source reconciliation or navigation.
        internal void BindFixture(OpenCodePageSource source)
        {
            IsLoaded = true;
            _source = source;
            _sourceRevision = source.Revision;
            _client = source.CreateClient();
            _pageActive = true;
            _pageCts = new();
        }
        internal void ChangeSource(OpenCodePageSource source) { InvalidateForSourceChange(); BindFixture(source); }
        internal void Unload() { IsLoaded = false; OnPageUnloaded(this, new()); }
        private const string FixtureMessageDirectory = "/synthetic/return-fixture";
        internal Task Switch(string id) => SwitchToSessionAsync(id, directory: FixtureMessageDirectory);
        internal Task<int> Navigate(string id) => NavigateToSessionAsync(id);
        // The inert host has no agent tree; retain the navigation-contract harness for the
        // legacy helper while production row events call the real tree route.
        private Task OpenAgentTreeChildAsync(string childId) => Task.CompletedTask;
        internal Task Card(string messageId, string childId) => NavigateToSubagentSessionAsync(_conversationView, messageId, childId);
        internal Task Preview(string messageId, string childId) => ShowSubagentChatAsync(_conversationView, messageId, childId);
        internal Task Notify(NotificationNavigationTarget target, NotificationInteractionResult? result = null)
            => ProcessNavigationTargetAsync(target, result);
        internal IReadOnlyDictionary<string, OpenCodeSessionInfo> Catalog => _sessionItems;
        private void OnSessionCatalogChanged() { }
        private void MirrorSessionSelection(string id, bool userInitiated) { }
        internal string? Rebuild(List<OpenCodeSessionInfo> sessions) => RebuildSessionList(sessions);
        internal Task Return() => ReturnToSessionAsync();
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
        // Fluent motion (OpenCodePage.Motion.cs) is presentation-only; the transcript reveal is inert here.
        private void BeginTranscriptSwitchMotion() { }
        private void RevealTranscript() { }
        private void UpdateComposerButtons() { }
        // No composer is constructed by this return-navigation host. Current extracted lifecycle methods
        // notify its binding boundary; composer/continuation behavior remains outside this harness's proof.
        private void BindComposer() { }
        // This return/child fixture has NO controller/editor/slot payload. These represent the
        // real Page's no-composer branches only; actual Prepare/CAS/IME/source-recovery gates run
        // source-linked with the real core in opencode-tabs. Switch/generation guards below stay real.
        private bool TryPrepareDraftSlotNavigation() => true;
        private void RestoreDraftSlotTabSelection() { }
        private void ForceSuspendDraftSlot(string reason) { }
        private void SuspendDraftSlotForPageUnload() { } // No composer/editor exists in this navigation-only fixture; real helper covered in opencode-tabs.
        private void InvalidateDraftSourceEpoch() { }
        private void OnSessionContextSelected(string sessionId) => _homeActive = false;
        private void UpdateInteractionsSection() { }
        private void ConfigureAgentSelectorForSession(string id) { }
        private void ClearAgentSelector() { }
        private void PauseTerminalView() { }
        // Persistent PTY inventory/native UI is outside return navigation; its actual scope
        // reset and server transport are covered by tests/opencode-persistent-pty.
        private void ResetPersistentPtyScope() { }
        private bool _isLoadingMoreSessions, _statusRefreshRunning;
        internal bool PausedLoadingFlags => _isLoadingMoreSessions || _statusRefreshRunning;
        private void DetachConversationEvents() { }
        private void UnsubscribeAppSourceChanged() { }
        private void OpenCodePage_TrustedEndpointIdentityChanged(object? sender, EventArgs e) { }
        private void SetConnectionState(ConnectionState state) { }
        private void HandleApiException(OpenCodeException ex, string context) { }
        private void RestartSessionEventSync(string id) { }
        private void QueueChildInteractionDiscovery() { } // Permission routing runs source-linked in opencode-event-sync.
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
