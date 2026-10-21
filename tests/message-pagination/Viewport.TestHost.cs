// Geometry/native-frame/App boundaries only; actual Page ACK, refresh, cache and SSE bodies compile.
using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace Windows.Foundation { internal readonly record struct Rect(double X, double Y, double Width, double Height); }
namespace Microsoft.UI.Xaml
{
    public sealed class XamlRoot { }
    public class SizeChangedEventArgs : EventArgs { }
    public class Application { public static Application? Current { get; set; } }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class ScrollViewerViewChangedEventArgs : EventArgs { }
    public partial class ScrollViewer
    {
        public event EventHandler<object>? LayoutUpdated;
        public event Action<object, Microsoft.UI.Xaml.SizeChangedEventArgs>? SizeChanged;
        public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;
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
namespace AIFrontend.Pages.OpenCode
{
    internal sealed record OpenCodeVisibleIdle(string MessageId, double Watermark, Windows.Foundation.Rect LabelBounds,
        Windows.Foundation.Rect EffectiveViewport, Windows.Foundation.Rect ScrollBounds);
    internal sealed record OpenCodeIdleViewportSnapshot(long RenderVersion, OpenCodeVisibleIdle? Candidate);
}
namespace AIFrontend
{
    public sealed class App : Microsoft.UI.Xaml.Application
    {
        internal int Calls;
        internal double? Last;
        public Task<double?> AcknowledgeOpenCodeSessionAsync(OpenCodePageSource source, long revision, string session,
            double watermark, Func<bool> fence, CancellationToken token)
        {
            if (!fence() || token.IsCancellationRequested) return Task.FromResult<double?>(null);
            Calls++; Last = watermark; return Task.FromResult<double?>(watermark);
        }
    }
}
namespace AIFrontend.Pages
{
    public sealed partial class OpenCodeConversationView
    {
        internal long RenderVersion { get; private set; }
        private OpenCode.OpenCodeVisibleIdle? _testIdle;
        internal event EventHandler? IdleViewportInvalidated;
        public event EventHandler<object>? LayoutUpdated;
        public event Action<object, Microsoft.UI.Xaml.SizeChangedEventArgs>? SizeChanged;
        private void InvalidateGeometry() { RenderVersion++; _testIdle = null; IdleViewportInvalidated?.Invoke(this, EventArgs.Empty); }
        internal OpenCode.OpenCodeIdleViewportSnapshot CaptureIdleViewport(Microsoft.UI.Xaml.Controls.ScrollViewer viewport) => new(RenderVersion, _testIdle);
        internal void TestGeometry(double? watermark)
        {
            _testIdle = watermark is { } value ? new("msg_idle_" + value, value, new(0, 0, 10, 10), new(0, 0, 100, 100), new(0, 0, 100, 100)) : null;
            LayoutUpdated?.Invoke(this, new());
        }
        internal void TestResize() => SizeChanged?.Invoke(this, new());
    }
    public sealed partial class OpenCodePage
    {
        private readonly AIFrontend.App _ackApp = new();
        private bool _newSessionDraftActive = false;
        public bool IsLoaded => true;
        public Microsoft.UI.Xaml.Visibility Visibility => Microsoft.UI.Xaml.Visibility.Visible;
        public Microsoft.UI.Xaml.XamlRoot XamlRoot { get; } = new();
        public double ActualWidth => 800;
        public double ActualHeight => 600;
        internal void TestViewportLayout(double? watermark) => _conversationView.TestGeometry(watermark);
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
    }
}
