// Geometry/native-frame/App boundaries only; actual Page ACK, refresh, cache and SSE bodies compile.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

namespace Windows.Foundation { internal readonly record struct Rect(double X, double Y, double Width, double Height); }
namespace Microsoft.UI.Xaml
{
    public sealed class XamlRoot { }
    public class SizeChangedEventArgs : EventArgs { }
    public class Application { public static Application? Current { get; set; } }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class ScrollViewChangedEventArgs : EventArgs { }
    public partial class ScrollView
    {
        public event EventHandler<object>? LayoutUpdated;
        public event Action<object, Microsoft.UI.Xaml.SizeChangedEventArgs>? SizeChanged;
        public event Action<ScrollView, object>? ViewChanged;
        internal void TestLayout() => LayoutUpdated?.Invoke(this, new());
        internal void TestResize() => SizeChanged?.Invoke(this, new());
        internal void TestScroll() => ViewChanged?.Invoke(this, new());
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public static class CompositionTarget
    {
        public static event EventHandler<object>? Rendering;
        internal static int Count => Rendering?.GetInvocationList().Length ?? 0;
        internal static void Frame() => Rendering?.Invoke(null, new());
    }
}
namespace FluentCode.Pages.OpenCode
{
    internal sealed record OpenCodeVisibleIdle(string MessageId, double Watermark, Windows.Foundation.Rect LabelBounds,
        Windows.Foundation.Rect EffectiveViewport, Windows.Foundation.Rect ScrollBounds);
    internal sealed record OpenCodeIdleViewportSnapshot(long RenderVersion, OpenCodeVisibleIdle? Candidate);
}
namespace FluentCode
{
    public sealed partial class App : Microsoft.UI.Xaml.Application
    {
        internal int Calls;
        internal double? Last;
        private bool _closing = false;
        private OpenCodeSessionUiStateStore? _sessionUiStore;
        private OpenCodePageSource? _sessionUiSourceOwner;
        private object? _sessionUiViewedOwner;
        private bool _sessionUiChanged;
        public OpenCodePageSource? CurrentOpenCodePageSource { get; private set; }
        public event EventHandler<OpenCodeSessionUiStateChangedEventArgs>? OpenCodeSessionUiStateChanged;
        private static void RequireAppThread() { }
        private void RaiseAppEvent<T>(EventHandler<T>? handlers, T args) where T : EventArgs => handlers?.Invoke(this, args);
        internal void BindBackgroundSource(OpenCodePageSource source) { CurrentOpenCodePageSource = source; BindSessionUiStore(source); }
        internal void ExpireBackground() { _sessionUiChanged |= _sessionUiStore!.Expire(DateTimeOffset.UtcNow); PublishSessionUiChanges(); }
        internal void ViewSession(string session, bool viewed)
        { ApplySessionUiViewedSession(this, CurrentOpenCodePageSource, session, viewed); PublishSessionUiChanges(); }
        internal void CompleteSession(string session, double watermark)
        { _sessionUiChanged |= _sessionUiStore!.Apply(new(CurrentOpenCodePageSource!.Identity, CurrentOpenCodePageSource.Revision, session,
            Completion: new("succeeded", watermark)), DateTimeOffset.UtcNow); PublishSessionUiChanges(); }
        public Task<double?> AcknowledgeOpenCodeSessionAsync(OpenCodePageSource source, long revision, string session,
            double watermark, Func<bool> fence, CancellationToken token)
        {
            if (!fence() || token.IsCancellationRequested) return Task.FromResult<double?>(null);
            Calls++; Last = watermark; return Task.FromResult<double?>(watermark);
        }
    }
}
namespace FluentCode.Pages
{
    public sealed partial class OpenCodeConversationView
    {
        internal long RenderVersion { get; private set; }
        private OpenCode.OpenCodeVisibleIdle? _testIdle;
        internal bool IsIdleLayoutCurrent => _testIdle is not null;
        internal event EventHandler? IdleViewportInvalidated;
        public event EventHandler<object>? LayoutUpdated;
        public event Action<object, Microsoft.UI.Xaml.SizeChangedEventArgs>? SizeChanged;
        private void InvalidateGeometry() { RenderVersion++; _testIdle = null; IdleViewportInvalidated?.Invoke(this, EventArgs.Empty); }
        internal OpenCode.OpenCodeIdleViewportSnapshot CaptureIdleViewport(Microsoft.UI.Xaml.Controls.ScrollView viewport) => new(RenderVersion, _testIdle);
        internal void TestGeometry(double? watermark)
        {
            _testIdle = watermark is { } value ? new("msg_idle_" + value, value, new(0, 0, 10, 10), new(0, 0, 100, 100), new(0, 0, 100, 100)) : null;
            LayoutUpdated?.Invoke(this, new());
        }
        internal void TestResize() => SizeChanged?.Invoke(this, new());
    }
    public sealed partial class OpenCodePage
    {
        private bool IsViewAcknowledgementSessionTabSelected() => _selectedSessionId is { Length: > 0 };
        private readonly FluentCode.App _ackApp = new();
        private bool _newSessionDraftActive = false;
        public bool IsLoaded => true;
        public Microsoft.UI.Xaml.Visibility Visibility => Microsoft.UI.Xaml.Visibility.Visible;
        public Microsoft.UI.Xaml.XamlRoot XamlRoot { get; } = new();
        public double ActualWidth => 800;
        public double ActualHeight => 600;
        internal void TestViewportLayout(double? watermark) => _conversationView.TestGeometry(watermark);
        // Supplied layout proof can be the visible answer bottom, with separator offscreen;
        // the real ConversationView geometry walk is covered by session-return.
        internal void TestViewportContentBottom(double watermark) => _conversationView.TestGeometry(watermark);
        internal void TestViewportFrame() => Microsoft.UI.Xaml.Media.CompositionTarget.Frame();
        internal int TestViewportFrames => Microsoft.UI.Xaml.Media.CompositionTarget.Count;
        internal double? TestAckWatermark => _ackApp.Last;
        internal void TestDetachViewport() => DetachViewAcknowledgementEvents();
        partial void ApplyLiveSessionStatuses(System.Collections.Generic.Dictionary<string, OpenCodeSessionUiState> states);
        internal OpenCodeSessionUiState TestLiveOverride(OpenCodeSessionUiState previous)
        {
            var states = new System.Collections.Generic.Dictionary<string, OpenCodeSessionUiState> { [previous.SessionId] = previous };
            _liveSessionStatuses[previous.SessionId] = (true, 1);
            ApplyLiveSessionStatuses(states); return states[previous.SessionId];
        }
        internal static OpenCode.OpenCodeHomeStatus? TestAdaptHomeStatus(OpenCodeSessionUiState? state, OpenCodeSessionInfo info) => CreateHomeStatus(state, info);
        internal OpenCodeSessionUiState? TestTabState(string id)
        {
            var snapshot = _ackApp.GetOpenCodeSessionUiSnapshot(_source!, _sourceRevision);
            if (snapshot is null) return null;
            var states = new System.Collections.Generic.Dictionary<string, OpenCodeSessionUiState>();
            foreach (var state in snapshot.Sessions) states[state.SessionId] = state;
            ApplyLiveSessionStatuses(states);
            return states.GetValueOrDefault(id);
        }
        internal long TestBackgroundVersion => _ackApp.GetOpenCodeSessionUiSnapshot(_source!, _sourceRevision)!.Version;
        internal void TestExpireBackground() => _ackApp.ExpireBackground();
        internal void TestBackgroundProjection(string id, string[] children, bool complete) => OnSessionBackgroundProjection(id, System.Collections.Immutable.ImmutableArray.CreateRange(children), complete);
        internal void TestViewSession(bool viewed) => _ackApp.ViewSession(_selectedSessionId!, viewed);
        internal void TestCompleteSession(double watermark) => _ackApp.CompleteSession(_selectedSessionId!, watermark);
    }
}
