using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AIFrontend.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

// Linked production page bridge, inert native template/controller probes. No app or desktop interaction.
internal static class AnnotatedScrollBarBridgeTests
{
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _passed++; Console.WriteLine("PASS: SB4: " + name);
    }
    internal static void Run()
    {
        var page = new OpenCodePage();
        page.LoadBridge();
        var bar = page.Bar;
        Check(bar.HandledEventsToo == 2, "exit/capture loss observed even when a native child handles the event");
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
        bar.RaiseCaptureLost();
        Check(!page.Tip.IsOpen && bar.ScrollController.Values == values && bar.Labels.Mutations == labels,
            "capture loss closes native tooltip without controller or label refresh");
        page.ScrollTo(200);
        Check(bar.ScrollController.Values == values + 1 && bar.ScrollController.Offset == 200,
            "real viewport change still updates controller offset");
        page.UnloadBridge();
        Check(bar.HandlerCount == 0 && !page.Tip.IsOpen, "unload closes detail and removes routed handlers");
        page.Tip.IsOpen = true;
        Check(page.Tip.IsOpen, "unload removes tooltip opened subscription");
        page.Tip.IsOpen = false;
        page.LoadBridge();
        Check(bar.HandlerCount == 2 && !page.Tip.IsOpen, "reload starts clean without duplicate exit handlers");
        page.UnloadBridge();
        Console.WriteLine($"Scroll bar bridge passed: {_passed}");
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly AnnotatedScrollBar ConversationScrollBar = new() { ActualWidth = 56, ActualHeight = 500 };
        private readonly ScrollViewer MessageScroll = new() { ScrollableHeight = 400 };
        private readonly ConversationProbe _conversationView = new();
        internal readonly ToolTip Tip = new();
        public OpenCodePage()
        {
            var rail = new FrameworkElement { Name = "PART_ToolTipRail", NativeToolTip = Tip };
            ConversationScrollBar.Children.Add(rail);
        }
        internal AnnotatedScrollBar Bar => ConversationScrollBar;
        internal void LoadBridge() => ConversationScrollBar_Loaded(this, new());
        internal void UnloadBridge() => ConversationScrollBar_Unloaded(this, new());
        internal void Layout() => MessageScroll.RaiseLayout();
        internal void ScrollTo(double offset) { MessageScroll.VerticalOffset = offset; MessageScroll.RaiseView(); }
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
        public static readonly object PointerExitedEvent = new(), PointerCaptureLostEvent = new();
        private readonly Dictionary<object, PointerEventHandler> _handlers = new();
        public int HandledEventsToo { get; private set; }
        public int HandlerCount => _handlers.Count;
        public event PointerEventHandler? PointerEntered;
        public void AddHandler(object key, PointerEventHandler handler, bool handledEventsToo)
        { _handlers.Add(key, handler); if (handledEventsToo) HandledEventsToo++; }
        public void RemoveHandler(object key, PointerEventHandler handler) => _handlers.Remove(key);
        public void RaiseEntered() => PointerEntered?.Invoke(this, new(10, 100));
        public void RaiseExited(double x, double y) => _handlers[PointerExitedEvent](this, new(x, y));
        public void RaiseCaptureLost() => _handlers[PointerCaptureLostEvent](this, new(10, 100));
    }
    public class FrameworkElement : UIElement
    {
        public string Name { get; set; } = "";
        public double ActualWidth { get; set; }
        public double ActualHeight { get; set; }
        public object? NativeToolTip { get; set; }
    }
}
namespace Microsoft.UI.Xaml.Input
{
    public delegate void PointerEventHandler(object sender, PointerRoutedEventArgs args);
    public sealed class PointerRoutedEventArgs(double x, double y) : RoutedEventArgs
    {
        public PointProbe GetCurrentPoint(object relativeTo) => new(x, y);
    }
    public sealed class PointProbe(double x, double y) { public (double X, double Y) Position => (x, y); }
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
    public sealed class ScrollViewerViewChangedEventArgs : EventArgs { }
    public sealed class ScrollViewer
    {
        public double ScrollableHeight { get; set; }
        public double VerticalOffset { get; set; }
        public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;
        public event EventHandler<object>? LayoutUpdated;
        public void RaiseView() => ViewChanged?.Invoke(this, new());
        public void RaiseLayout() => LayoutUpdated?.Invoke(this, new());
        public void ChangeView(object? horizontal, double offset, object? zoom, bool disableAnimation) => VerticalOffset = offset;
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
