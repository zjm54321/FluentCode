// Inert frame/control geometry/App boundary; production ACK, binding and pagination guards compile.
// Geometry is explicitly supplied, never inferred from DTOs. Native clipping is tested by UI lane.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using AIFrontend.Pages.OpenCode;

namespace Windows.Foundation { internal readonly record struct Rect(double X, double Y, double Width, double Height); }
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public sealed class XamlRoot { }
    public class RoutedEventArgs : EventArgs { }
    public class SizeChangedEventArgs : EventArgs { }
    public class Application { public static Application? Current { get; set; } }
    public class Element
    {
        public bool IsLoaded { get; set; } = true;
        public Visibility Visibility { get; set; }
        public XamlRoot? XamlRoot { get; set; } = new();
        public double ActualWidth { get; set; } = 800;
        public double ActualHeight { get; set; } = 600;
        public event EventHandler<object>? LayoutUpdated;
        public event Action<object, SizeChangedEventArgs>? SizeChanged;
        internal int LayoutListeners => LayoutUpdated?.GetInvocationList().Length ?? 0;
        internal void Layout() => LayoutUpdated?.Invoke(this, new());
        internal void Resize() => SizeChanged?.Invoke(this, new());
    }
}
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess { get; set; } = true;
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class Page : Microsoft.UI.Xaml.Element { public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new(); }
    public class ScrollViewerViewChangedEventArgs : EventArgs { }
    public class ScrollViewer : Microsoft.UI.Xaml.Element
    {
        public double VerticalOffset { get; private set; }
        public double ScrollableHeight => 0;
        public void UpdateLayout() { }
        public void ChangeView(double? x, double? y, double? z, bool disableAnimation) { if (y is { } v) VerticalOffset = v; }
        public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;
        internal void Scroll() => ViewChanged?.Invoke(this, new());
    }
    public enum InfoBarSeverity { Informational, Warning, Error, Success }
}
namespace Microsoft.UI.Xaml.Media
{
    public static class CompositionTarget
    {
        public static event EventHandler<object>? Rendering;
        internal static int Count => Rendering?.GetInvocationList().Length ?? 0;
        internal static EventHandler<object>? Capture() => Rendering;
        internal static void Frame() => Rendering?.Invoke(null, new());
    }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed record OpenCodeVisibleIdle(string MessageId, double Watermark, Windows.Foundation.Rect LabelBounds,
        Windows.Foundation.Rect EffectiveViewport, Windows.Foundation.Rect ScrollBounds);
    internal sealed record OpenCodeIdleViewportSnapshot(long RenderVersion, OpenCodeVisibleIdle? Candidate);
    internal sealed class OpenCodeConversationView : Microsoft.UI.Xaml.Element
    {
        internal void PreserveReadingAnchor(Microsoft.UI.Xaml.Controls.ScrollViewer viewport, Func<bool> ownerCurrent) { }
        internal long RenderVersion { get; private set; }
        private OpenCodeVisibleIdle? _candidate;
        internal event EventHandler? IdleViewportInvalidated;
        internal int InvalidatedListeners => IdleViewportInvalidated?.GetInvocationList().Length ?? 0;
        internal void ApplyMessages(IReadOnlyList<OpenCodeMessage> rows)
        { RenderVersion++; _candidate = null; IdleViewportInvalidated?.Invoke(this, EventArgs.Empty); }
        internal void ClearMessages() => ApplyMessages(Array.Empty<OpenCodeMessage>());
        internal OpenCodeIdleViewportSnapshot CaptureIdleViewport(Microsoft.UI.Xaml.Controls.ScrollViewer viewport) => new(RenderVersion, _candidate);
        internal void SupplyGeometry(double? watermark, double x = 0)
        {
            _candidate = watermark is { } value ? new("msg_visible_" + value, value,
                new(x, 10, 100, 20), new(0, 0, 800, 600), new(0, 0, 800, 600)) : null;
            Layout();
        }
    }
}
namespace AIFrontend
{
    public class App : Microsoft.UI.Xaml.Application
    {
        public bool ResultUnknown { get; set; }
        public int Calls, Posts, FenceBlocked;
        public double? LastSent;
        public Func<bool>? LastFence;
        public Task<double?> AcknowledgeOpenCodeSessionAsync(OpenCodePageSource source, long revision, string sessionId,
            double coveredIdle, Func<bool>? isStillVisible = null, CancellationToken cancellationToken = default)
        {
            Calls++; LastSent = coveredIdle; LastFence = isStillVisible;
            if (isStillVisible?.Invoke() == false) { FenceBlocked++; return Task.FromResult<double?>(null); }
            var receipt = ResultUnknown ? null : (double?)coveredIdle;
            if (receipt is null || isStillVisible?.Invoke() == false) return Task.FromResult<double?>(null);
            Posts++; return Task.FromResult(receipt);
        }
    }
}
namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage : Microsoft.UI.Xaml.Controls.Page
    {
        private OpenCodePageSource? _source;
        private OpenCodeClient? _client;
        private long _sourceRevision;
        private int _sourceGeneration, _pageGeneration, _sessionGeneration;
        private string? _selectedSessionId;
        private bool _pageActive, _sessionMissing, _homeActive, _newSessionDraftActive;
        private CancellationTokenSource? _pageCts, _sessionCts;
        private OpenCodeConversationView _conversationView = new();
        private readonly Microsoft.UI.Xaml.Controls.ScrollViewer MessageScroll = new();
        private readonly Dictionary<string, OpenCodeSessionInfo> _sessionItems = new();
        private bool _isRefreshing, _refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit;
        private readonly Dictionary<string, string> _lastIdleBySession = new();
        private const int MaxScrollSettlePasses = 3;
        private bool SourceReady => _source is { IsRevoked: false } && _client is not null;
        private enum ConnectionState { Connected, Failed }
        private void SetConnectionState(ConnectionState state) { }
        private void ShowMessage(Microsoft.UI.Xaml.Controls.InfoBarSeverity severity, string title, string message) { }
        private void HandleApiException(OpenCodeException ex, string context) { }
        private void UpdateEmptyState() { }
        private void UpdateComposerButtons() { }
        private void BindComposer() { } // Native composer binding is outside this acknowledgement fixture.
        private void CancelSessionOperations() { _sessionCts?.Cancel(); _sessionCts = null; }
        internal object Diagnostics => (_isRefreshing, _refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow,
            _refreshQueuedExplicit, _firstMessagesLoading, _messagesResponseEmpty, _messagesLoadError, _historyExecutionRebaseline);

        internal void TestActivate(OpenCodePageSource source, string sessionId)
        {
            InvalidateViewAcknowledgement();
            _source = source; _sourceRevision = source.Revision; _sourceGeneration++; _pageGeneration++; _sessionGeneration++;
            _selectedSessionId = sessionId; _pageActive = true; _sessionMissing = false;
            _homeActive = _newSessionDraftActive = false;
            _pageCts = new(); _sessionCts = new();
            _client = new(new System.Net.Http.HttpClient(), new() { BaseUrl = "https://offline.invalid" }, true);
            _messageBinding = new(source, source.Revision, _sourceGeneration, _pageGeneration, _client, sessionId, "/fixture");
            _messageHistoryBoundaryKnown = true; _messageHistoryNeedsState = false;
            Visibility = Microsoft.UI.Xaml.Visibility.Visible; IsLoaded = true;
            AttachViewAcknowledgementEvents();
        }
        internal void Apply(IReadOnlyList<OpenCodeMessage> rows, bool visible = true)
        { _conversationView.ApplyMessages(rows); NotifyViewAcknowledgementRendered(rows, visible); }
        internal void Layout(double? watermark, double x = 0) => _conversationView.SupplyGeometry(watermark, x);
        internal void RepeatLayout() { _conversationView.Layout(); MessageScroll.Layout(); }
        internal void Scroll() => MessageScroll.Scroll();
        internal void TestSetPageActive(bool active) { _pageActive = active; if (!active) InvalidateViewAcknowledgement(); }
        internal void TestSetPageVisible() { Visibility = Microsoft.UI.Xaml.Visibility.Visible; ActualWidth = 800; }
        internal void TestSetPageInvisible() { Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; ActualWidth = 0; InvalidateViewAcknowledgement(); }
        internal void TestSetThreadAccess(bool access) => DispatcherQueue.HasThreadAccess = access;
        internal void TestClearSource() { InvalidateViewAcknowledgement(); _source = null; }
        internal void TestRevokeSource() => _source!.Revoke();
        internal void Home(bool value) { if (value) InvalidateViewAcknowledgement(); _homeActive = value; RepeatLayout(); }
        internal void Blank(bool value) { if (value) InvalidateViewAcknowledgement(); _newSessionDraftActive = value; RepeatLayout(); }
        internal void Unload() { _pageActive = false; _pageGeneration++; DetachViewAcknowledgementEvents(); }
        internal void History(bool valid) { _messageHistoryNeedsState = !valid; InvalidateViewAcknowledgement(); RepeatLayout(); }
        internal void Directory(string? directory) { _messageBinding = _messageBinding! with { Directory = directory }; InvalidateViewAcknowledgement(); RepeatLayout(); }
        internal void OwnerChange(string dimension)
        {
            if (dimension == "page") _pageGeneration++;
            if (dimension == "session") { _sessionGeneration++; _selectedSessionId = "ses_other"; }
            if (dimension == "revision") _sourceRevision++;
            if (dimension == "client") _client = null;
            if (dimension == "directory") _messageBinding = _messageBinding! with { Directory = "/other" };
            if (dimension == "source") _source = new(_source!.Identity, _sourceRevision, () => _client!);
            if (dimension == "conversation") _conversationView = new();
            if (dimension == "epoch") _messageHistoryEpoch++;
            if (dimension == "workflow") _messageHistoryWorkflowOwner = 1;
            if (dimension == "revocation") _source!.Revoke();
            // Deliberately omit the normal transition's invalidation to exercise the frame's own guard.
        }
        internal int ViewListeners => _conversationView.InvalidatedListeners + _conversationView.LayoutListeners + MessageScroll.LayoutListeners;
    }
}
