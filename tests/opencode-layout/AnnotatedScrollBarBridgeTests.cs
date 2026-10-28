using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AIFrontend.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

// Linked production page bridge, inert native template/controller probes. No app or desktop interaction.
internal static class AnnotatedScrollBarBridgeTests
{
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _passed++; Console.WriteLine("PASS: SB4/SB5: " + name);
    }
    internal static void Run()
    {
        var page = new OpenCodePage();
        page.LoadBridge();
        var bar = page.Bar;
        Check(ReferenceEquals(page.Presenter.VerticalScrollController, bar.ScrollController), "native ScrollPresenter owns the actual annotated controller");
        Check(bar.ScrollController.Values == 0, "page does not manually supply controller range or offset");
        page.LoadBridge();
        Check(bar.HandledEventsToo == 4, "enter/move/exit/capture loss observed even when a native child handles the event");
        Check(!page.Tip.IsHitTestVisible, "native detail pill cannot intercept pointer input over the left side of the rail");
        bar.RaiseEntered();
        Check(page.Detail() is string detail && detail == "AI 回复\nReply", "native detail still uses nearest conversation text while hovered");
        page.Tip.IsOpen = true;
        Check(page.Tip.IsOpen, "native tooltip remains open while pointer is inside");
        var values = bar.ScrollController.Values;
        var labels = bar.Labels.Mutations;
        bar.RaiseExited(10, 100);
        Check(page.Tip.IsOpen, "template-child exit inside rail is ignored");
        bar.RaiseExited(80, 100);
        Check(!page.Tip.IsOpen && page.Detail() is null, "leaving rail closes native tooltip and stops stale detail requests");
        page.Layout();
        Check(bar.ScrollController.Values == values && bar.Labels.Mutations == labels,
            "pointer-exit layout does not SetValues or refresh unchanged labels");
        page.Tip.IsOpen = true;
        Check(!page.Tip.IsOpen, "delayed native tooltip open after exit is immediately closed");

        bar.RaiseEntered(); page.Tip.IsOpen = true;
        bar.RaiseExited(2, 100);
        Check(!page.Tip.IsOpen && page.Detail() is null,
            "left exit inside outer control padding but outside native interaction grid closes detail");
        page.Tip.IsOpen = true;
        Check(!page.Tip.IsOpen && page.Detail() is null, "delayed detail request/open after left exit cannot reopen pill");
        bar.RaiseEntered(); page.Tip.IsOpen = true;
        bar.RaiseExited(-40, 100);
        Check(!page.Tip.IsOpen && page.Detail() is null, "left exit over the native pill closes detail");
        bar.RaiseEntered(-40, 100); page.Tip.IsOpen = true;
        Check(!page.Tip.IsOpen && page.Detail() is null, "popup-induced enter outside rail does not restore hover");

        bar.RaiseMoved(10, 100); page.Tip.IsOpen = true;
        Check(page.Tip.IsOpen && page.Detail() is string, "fresh movement inside restores ordinary hover");
        bar.RaiseMoved(4.5, 100);
        Check(!page.Tip.IsOpen && page.Detail() is null, "left-edge rounding tolerance agrees with native hit area");
        bar.RaiseMoved(10, 100); page.Tip.IsOpen = true;
        bar.RaiseExited(2, 100, 1000);
        bar.RaiseMoved(10, 100, 999); page.Tip.IsOpen = true;
        Check(!page.Tip.IsOpen && page.Detail() is null, "stale inside move cannot undo newer left exit");
        bar.RaiseMoved(10, 100, 1001); page.Tip.IsOpen = true;
        bar.RaiseExited(2, 100, 1000);
        Check(page.Tip.IsOpen && page.Detail() is string, "quick left-right jitter retains newest inside position");
        bar.RaiseMoved(80, 100); page.Tip.IsOpen = true;
        Check(!page.Tip.IsOpen && page.Detail() is null, "quick jitter ending outside right leaves no pill");
        bar.RaiseMoved(10, 100); page.Tip.IsOpen = true;
        bar.RaiseExited(10, 8);
        Check(!page.Tip.IsOpen, "button row outside native interaction grid is not detail hover");

        bar.RaiseEntered(); page.Tip.IsOpen = true;
        bar.RaiseCaptureLost();
        Check(!page.Tip.IsOpen && bar.ScrollController.Values == values && bar.Labels.Mutations == labels,
            "capture loss closes native tooltip without controller or label refresh");
        page.ScrollTo(200);
        Check(bar.ScrollController.Values == values && ReferenceEquals(page.Presenter.VerticalScrollController, bar.ScrollController),
            "viewport changes do not invoke a second manual scrolling owner");
        page.LateUnload();
        Check(bar.HandlerCount == 4 && ReferenceEquals(page.Presenter.VerticalScrollController, bar.ScrollController), "late unload after remount preserves active native integration");
        page.UnloadBridge();
        Check(bar.HandlerCount == 0 && !page.Tip.IsOpen && page.Presenter.VerticalScrollController is null, "unload closes detail, removes routed handlers and releases native controller");
        page.Tip.IsOpen = true;
        Check(page.Tip.IsOpen, "unload removes tooltip opened subscription");
        page.Tip.IsOpen = false;
        page.LoadBridge();
        Check(bar.HandlerCount == 4 && !page.Tip.IsOpen && page.Detail() is null, "reload starts clean without duplicate handlers or stale position");
        page.UnloadBridge();
        Console.WriteLine($"Scroll bar bridge passed: {_passed}");
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly AnnotatedScrollBar ConversationScrollBar = new() { ActualWidth = 56, ActualHeight = 500 };
        private readonly ScrollView MessageScroll = new() { ExtentHeight = 1000 };
        private readonly ConversationProbe _conversationView = new();
        internal readonly ToolTip Tip = new();
        public OpenCodePage()
        {
            var grid = new FrameworkElement { Name = "PART_VerticalGrid", ActualWidth = 48, ActualHeight = 468, Left = 4, Top = 16 };
            grid.Children.Add(new FrameworkElement { Name = "PART_ToolTipRail", NativeToolTip = Tip });
            ConversationScrollBar.Children.Add(grid);
        }
        internal AnnotatedScrollBar Bar => ConversationScrollBar;
        internal ScrollPresenter Presenter => MessageScroll.ScrollPresenter;
        internal void LoadBridge() { ConversationScrollBar.IsLoaded = true; ConversationScrollBar_Loaded(this, new()); }
        internal void LateUnload() => ConversationScrollBar_Unloaded(this, new());
        internal void UnloadBridge() { ConversationScrollBar.IsLoaded = false; ConversationScrollBar_Unloaded(this, new()); }
        internal void Layout() => MessageScroll.RaiseLayout();
        internal void ScrollTo(double offset) { MessageScroll.VerticalOffset = offset; MessageScroll.RaiseLayout(); }
        internal object? Detail()
        {
            var args = new AnnotatedScrollBarDetailLabelRequestedEventArgs();
            ConversationScrollBar_DetailLabelRequested(ConversationScrollBar, args); return args.Content;
        }
    }
    internal sealed class ConversationProbe
    {
        internal IEnumerable<(string Text, bool User, double Offset)> ScrollAnnotations() => new[] { ("Reply", false, 100d) };
    }
}

namespace Microsoft.UI.Xaml
{
    public class DependencyObject { public List<DependencyObject> Children { get; } = new(); }
    public class RoutedEventArgs : EventArgs { }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs args);
    public class UIElement : DependencyObject
    {
        public static readonly object PointerEnteredEvent = new(), PointerMovedEvent = new(), PointerExitedEvent = new(), PointerCaptureLostEvent = new();
        private readonly Dictionary<object, PointerEventHandler> _handlers = new();
        public int HandledEventsToo { get; private set; }
        public int HandlerCount => _handlers.Count;
        public bool IsHitTestVisible { get; set; } = true;
        private ulong _timestamp;
        public void AddHandler(object key, PointerEventHandler handler, bool handledEventsToo)
        { _handlers.Add(key, handler); if (handledEventsToo) HandledEventsToo++; }
        public void RemoveHandler(object key, PointerEventHandler handler) => _handlers.Remove(key);
        private void Raise(object key, double x, double y, ulong? timestamp)
        {
            var time = timestamp ?? _timestamp + 1;
            _timestamp = Math.Max(time, _timestamp);
            _handlers[key](this, new(x, y, time));
        }
        public void RaiseEntered(double x = 10, double y = 100) => Raise(PointerEnteredEvent, x, y, null);
        public void RaiseMoved(double x, double y, ulong? timestamp = null) => Raise(PointerMovedEvent, x, y, timestamp);
        public void RaiseExited(double x, double y, ulong? timestamp = null) => Raise(PointerExitedEvent, x, y, timestamp);
        public void RaiseCaptureLost() => Raise(PointerCaptureLostEvent, 10, 100, null);
    }
    public class FrameworkElement : UIElement
    {
        public bool IsLoaded { get; set; }
        public string Name { get; set; } = "";
        public double ActualWidth { get; set; }
        public double ActualHeight { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public object? NativeToolTip { get; set; }
    }
}
namespace Microsoft.UI.Xaml.Input
{
    public delegate void PointerEventHandler(object sender, PointerRoutedEventArgs args);
    public sealed class PointerRoutedEventArgs(double x, double y, ulong timestamp) : RoutedEventArgs
    {
        public PointProbe GetCurrentPoint(FrameworkElement relativeTo) => new(x - relativeTo.Left, y - relativeTo.Top, timestamp);
    }
    public sealed class PointProbe(double x, double y, ulong timestamp)
    {
        public (double X, double Y) Position => (x, y);
        public ulong Timestamp => timestamp;
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public static class VisualTreeHelper
    {
        public static int GetChildrenCount(DependencyObject root) => root.Children.Count;
        public static DependencyObject GetChild(DependencyObject root, int index) => root.Children[index];
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ToolTip : FrameworkElement
    {
        private bool _open;
        public event RoutedEventHandler? Opened;
        public bool IsOpen { get => _open; set { _open = value; if (value) Opened?.Invoke(this, new()); } }
    }
    public static class ToolTipService { public static object? GetToolTip(FrameworkElement element) => element.NativeToolTip; }
    public sealed class AnnotatedScrollBar : FrameworkElement
    {
        public ScrollControllerProbe ScrollController { get; } = new();
        public LabelsProbe Labels { get; } = new();
        public bool ApplyTemplate() => true;
    }
    public sealed class LabelsProbe : Collection<AnnotatedScrollBarLabel>
    {
        public int Mutations { get; private set; }
        protected override void ClearItems() { Mutations++; base.ClearItems(); }
        protected override void InsertItem(int index, AnnotatedScrollBarLabel item) { Mutations++; base.InsertItem(index, item); }
    }
    public sealed record AnnotatedScrollBarLabel(string Text, double Offset);
    public sealed class AnnotatedScrollBarDetailLabelRequestedEventArgs { public double ScrollOffset { get; set; } public object? Content { get; set; } }
    public sealed class AnnotatedScrollBarScrollingEventArgs { public bool Cancel { get; set; } public double ScrollOffset { get; set; } }
    public sealed class ScrollControllerProbe
    {
        public int Values { get; private set; }
        public double Offset { get; private set; }
        public PanningProbe? PanningInfo { get; } = new();
        public void SetIsScrollable(bool value) { }
        public void SetValues(double min, double max, double offset, double viewport) { Values++; Offset = offset; }
    }
    public sealed class PanningProbe
    {
        public void SetPanningElementExpressionAnimationSources(Microsoft.UI.Composition.CompositionPropertySet sources,
            string min, string max, string offset, string multiplier) { }
    }
    public sealed class ScrollView
    {
        public ScrollPresenter ScrollPresenter { get; } = new();
        public bool ApplyTemplate() => true;
        public double ExtentHeight { get; set; }
        public double VerticalOffset { get; set; }
        public event EventHandler<object>? LayoutUpdated;
        public void RaiseLayout() => LayoutUpdated?.Invoke(this, new());
    }
}
namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public sealed class ScrollPresenter
    {
        public Microsoft.UI.Xaml.Controls.ScrollControllerProbe? VerticalScrollController { get; set; }
    }
}
namespace Microsoft.UI.Composition
{
    public sealed class CompositionPropertySet : IDisposable
    {
        public void InsertScalar(string name, float value) { }
        public void Dispose() { }
    }
    public sealed class Compositor { public CompositionPropertySet CreatePropertySet() => new(); }
    public sealed class Visual { public Compositor Compositor { get; } = new(); }
}
namespace Microsoft.UI.Xaml.Hosting
{
    public static class ElementCompositionPreview
    {
        public static Microsoft.UI.Composition.Visual GetElementVisual(object element) => new();
    }
}
