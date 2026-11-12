// Inert WinUI objects model identity, focus/viewport ownership and popup lifetime only. The
// actual Home reconciliation, row setters, menu commands, calendar and badge policy are extracted.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Windows.Foundation
{
    public readonly record struct Point(double X = 0, double Y = 0);
}
namespace Windows.System
{
    public enum VirtualKey { F2, Delete }
}
namespace FluentCode.Services.Appearance
{
    internal enum AppFontSlot { Monospace }
    internal static class AppFonts { internal static void Apply(FrameworkElement element, AppFontSlot slot) { } }
}
namespace Microsoft.UI.Xaml
{
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum FocusState { Unfocused, Pointer, Keyboard, Programmatic }
    public enum GridUnitType { Pixel, Star, Auto }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    {
        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
    }
    public readonly record struct CornerRadius(double TopLeft, double TopRight, double BottomRight, double BottomLeft)
    {
        public CornerRadius(double uniform) : this(uniform, uniform, uniform, uniform) { }
    }
    public readonly record struct GridLength(double Value, GridUnitType Unit = GridUnitType.Pixel)
    {
        public static GridLength Auto => new(0, GridUnitType.Auto);
    }
    public class Style { internal string Key = ""; }
    public class DependencyObject
    {
        internal DependencyObject? Parent;
        internal string? AutomationId, AutomationName;
        internal object? Tooltip;
    }
    public class FrameworkElement : DependencyObject
    {
        public object? Tag { get; set; }
        public object? XamlRoot { get; set; } = new object();
        public bool IsLoaded { get; set; } = true;
        public bool IsHitTestVisible { get; set; } = true;
        public bool IsTabStop { get; set; }
        public Visibility Visibility { get; set; } = Visibility.Visible;
        public double Width { get; set; }
        public double Height { get; set; }
        public double MinHeight { get; set; }
        public double MaxHeight { get; set; }
        public double MaxWidth { get; set; }
        public double ActualHeight { get; set; } = 40;
        internal double Top;
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Style? Style { get; set; }
        internal string? ThemeKey;
        public virtual void UpdateLayout() { }
        public Microsoft.UI.Xaml.Media.FakeTransform TransformToVisual(FrameworkElement target)
            => new(() => Top - (target is ScrollViewer scroll ? scroll.VerticalOffset : 0));
    }
}
namespace Microsoft.UI.Xaml.Input
{
    public sealed class KeyRoutedEventArgs : EventArgs { public Windows.System.VirtualKey Key; public bool Handled; }
    public static class FocusManager
    {
        internal static DependencyObject? Current;
        public static object? GetFocusedElement(object root) => Current;
        internal static void Set(Control control, FocusState state)
        {
            if (Current is Control previous) previous.FocusState = FocusState.Unfocused;
            Current = control; control.FocusState = state;
        }
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public sealed class FakeTransform(Func<double> top) { public Windows.Foundation.Point TransformPoint(Windows.Foundation.Point point) => new(point.X, top()); }
    public static class VisualTreeHelper { public static DependencyObject? GetParent(DependencyObject element) => element.Parent; }
}
namespace Microsoft.UI.Xaml.Automation.Peers { public enum AutomationHeadingLevel { Level2 } }
namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetAutomationId(DependencyObject element, string value) => element.AutomationId = value;
        public static void SetName(DependencyObject element, string value) => element.AutomationName = value;
        public static void SetHeadingLevel(DependencyObject element, Peers.AutomationHeadingLevel value) { }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public class Control : FrameworkElement
    {
        public FocusState FocusState { get; set; }
        public bool Focus(FocusState state) { Input.FocusManager.Set(this, state); return true; }
    }
    public class ContentControl : Control
    {
        private object? _content;
        public object? Content { get => _content; set { _content = value; if (value is DependencyObject child) child.Parent = this; } }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
        public VerticalAlignment VerticalContentAlignment { get; set; }
    }
    public class UserControl : ContentControl
    {
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new() { Deferred = true };
    }
    public sealed class ColumnDefinition { public GridLength Width; public double MaxWidth; }
    public sealed class ElementCollection(FrameworkElement owner) : Collection<FrameworkElement>
    {
        protected override void InsertItem(int index, FrameworkElement item) { item.Parent = owner; base.InsertItem(index, item); }
    }
    public class Grid : FrameworkElement
    {
        public Grid() => Children = new(this);
        public double ColumnSpacing { get; set; }
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public ElementCollection Children { get; }
        public static void SetColumn(FrameworkElement element, int value) { }
    }
    public enum Orientation { Horizontal, Vertical }
    public class StackPanel : FrameworkElement
    {
        public StackPanel() => Children = new(this);
        public Orientation Orientation { get; set; }
        public double Spacing { get; set; }
        public ElementCollection Children { get; }
    }
    public class Border : FrameworkElement { public FrameworkElement? Child { get; set; } }
    public class ProgressRing : Control { public bool IsActive { get; set; } }
    public class FontIcon : FrameworkElement { public string Glyph { get; set; } = ""; public double FontSize { get; set; } }
    public class HyperlinkButton : Button { public event EventHandler? Click { add { } remove { } } }
    public class AutoSuggestBox { public string Text { get; set; } = ""; }
    public class ListViewItem : ContentControl
    {
        public FlyoutBase? ContextFlyout { get; set; }
        public event EventHandler<Input.KeyRoutedEventArgs>? KeyDown { add { } remove { } }
    }
    public sealed class ItemCollection(ListView owner) : Collection<object>
    {
        protected override void InsertItem(int index, object item)
        {
            if (item is FrameworkElement element) { element.Parent = owner; element.IsLoaded = true; }
            base.InsertItem(index, item);
        }
        protected override void RemoveItem(int index)
        {
            if (this[index] is FrameworkElement element)
            {
                if (ReferenceEquals(Input.FocusManager.Current, element)) owner.Focus(FocusState.Keyboard);
                if (element is ListViewItem { ContextFlyout: { IsOpen: true } menu }) menu.Hide();
                element.Parent = null; element.IsLoaded = false;
            }
            base.RemoveItem(index);
        }
    }
    public class ListView : Control
    {
        public ListView() => Items = new(this);
        public ItemCollection Items { get; }
        public object? SelectedItem { get; set; }
        internal ScrollViewer? Scroller;
        internal int Layouts;
        public override void UpdateLayout()
        {
            Layouts++;
            for (var index = 0; index < Items.Count; index++)
                if (Items[index] is FrameworkElement element) element.Top = index * 40;
            if (Scroller is { } scroller) scroller.ExtentHeight = Items.Count * 40;
        }
    }
    public class ScrollViewer : Control
    {
        public double VerticalOffset { get; set; }
        public double ViewportHeight { get; set; } = 80;
        public double ExtentHeight { get; set; }
        internal int ViewChanges;
        public bool ChangeView(double? horizontal, double? vertical, float? zoom, bool disableAnimation)
        { if (vertical is { } value) VerticalOffset = value; ViewChanges++; return true; }
    }
    public static class ToolTipService { public static void SetToolTip(DependencyObject element, object value) => element.Tooltip = value; }
}
namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public class FlyoutBase
    {
        internal Func<FlyoutBase, OpenCodeContextCommand?[]>? Builder;
        internal OpenCodeContextCommand?[] Commands = [];
        internal bool IsOpen;
        private DependencyObject? _previousFocus;
        public event EventHandler<object>? Opened;
        public event EventHandler<object>? Closed;
        internal void Show()
        {
            _previousFocus = Microsoft.UI.Xaml.Input.FocusManager.Current;
            Microsoft.UI.Xaml.Input.FocusManager.Set(new Control(), FocusState.Programmatic);
            Commands = Builder!(this); IsOpen = true; Opened?.Invoke(this, new());
        }
        public void Hide()
        {
            if (!IsOpen) return;
            IsOpen = false; Microsoft.UI.Xaml.Input.FocusManager.Current = _previousFocus; Closed?.Invoke(this, new());
        }
    }
}
namespace FluentCode.Pages.OpenCode
{
    public sealed record OpenCodeContextCommand(string Label, string Glyph, Action Execute)
    {
        public bool Enabled { get; init; } = true;
        public bool Row { get; init; }
        public bool Critical { get; init; }
        public string? Accelerator { get; init; }
        public string? AutomationId { get; init; }
        public string? Description { get; init; }
    }
    internal static class OpenCodeContextMenu
    {
        internal static FlyoutBase Create(Func<FlyoutBase, OpenCodeContextCommand?[]> builder) => new() { Builder = builder };
    }
    internal static class OpenCodeInteractionUi { internal static Style TryStyle(string key) => new() { Key = key }; }
    internal static class OpenCodeToolTheme
    {
        internal const string Caution = "caution", Accent = "accent", Success = "success", Critical = "critical", TextSecondary = "secondary", TextTertiary = "tertiary", SubtleSecondary = "subtle";
        internal static string Ring(string key) => key;
        internal static string Icon(string key) => key;
        internal static string Border(string key) => key;
        internal static string Text(string key, string style) => key;
        internal static void Apply(FrameworkElement element, string key) => element.ThemeKey = key;
    }
    public sealed partial class OpenCodeHomeView : UserControl
    {
        private const string AttentionKey = "attention", AllProjectsTag = "all-projects", LoadingMoreKey = "load-more:loading", LoadMoreFailedKey = "load-more:failed";
        private OpenCodeHomeSnapshot _snapshot = new(0, [], []);
        private readonly ListView _sessions = new();
        private readonly ListView _projects = new();
        private readonly AutoSuggestBox _search = new();
        private readonly TextBlock _message = new();
        private readonly ProgressRing _loading = new();
        private readonly HashSet<ListViewItem> _removingSessions = new(), _openSessionMenus = new();
        private bool _allowRowExits, _reconcilingSessions, _refreshing;
        private int _rowExitGeneration = 0;
        private long _requestedRevision = -1;
        private readonly Dictionary<object, TextBlock> _headerCounts = new();
        private readonly Dictionary<OpenCodeHomeProjectKey, (ProgressRing Ring, TextBlock Count)> _projectBadges = new();
        private TextBlock? _allProjectsCount;
        private ListViewItem? _allProjectsItem;
        private readonly ScrollViewer _listScroller = new();
        private DateTime _calendarDay = DateTime.Today;
        private string _calendarZone = TimeZoneInfo.Local.Id;
        public OpenCodeHomeProjectKey? SelectedProject { get; set; }
        internal Func<long, OpenCodeHomeSession, OpenCodeHomeSessionAction, bool>? CanRequestSessionAction;
        internal event EventHandler<OpenCodeHomeSessionActionEventArgs>? SessionActionRequested;
        private void RebuildChips() { } // Native SelectorBar/menu entrance is compile-checked by the app build.
        private void MaybeLoadMore() { } // Transport/paging is tested by the source-linked page above.
        private void RequestMore() { }
        private static Style ThemeStyle(string type, (string Property, string Key) setter) => new() { Key = setter.Key };
        internal OpenCodeHomeView()
        {
            _sessions.Scroller = _listScroller;
            _sessions.Parent = _listScroller;
            _allowRowExits = true;
        }
        internal void TestSnapshot(OpenCodeHomeSnapshot snapshot)
        {
            SetSnapshot(snapshot);
            DispatcherQueue.Drain();
            _sessions.UpdateLayout(); // Native layout also occurs for the base implementation, which has no explicit anchor pass.
        }
        internal ListViewItem TestItem(string id) => _sessions.Items.OfType<ListViewItem>().Single(item => item.Tag is OpenCodeHomeSessionRow row && row.Session.Info.Id == id);
        internal int TestItems => _sessions.Items.Count;
        internal int TestLayouts => _sessions.Layouts;
        internal bool TestProjectSelection(OpenCodeHomeProjectKey project, ListViewItem expected)
            => !_refreshing && SelectedProject == project && ReferenceEquals(_projects.SelectedItem, expected);
        internal ListViewItem TestProjectItem(OpenCodeHomeProjectKey key) => _projects.Items.OfType<ListViewItem>()
            .Single(item => item.Tag is OpenCodeHomeProject project && project.Key == key);
        internal void TestSelectProject(OpenCodeHomeProjectKey key) { SelectedProject = key; _projects.SelectedItem = SelectedItemFor(key); }
        internal double TestOffset { get => _listScroller.VerticalOffset; set => _listScroller.VerticalOffset = value; }
        internal double TestTop(ListViewItem item) => item.TransformToVisual(_listScroller).TransformPoint(new()).Y;
        internal bool TestProjectWorking(OpenCodeHomeProjectKey project)
        {
            _projectBadges[project] = (new(), new()); RefreshProjectBadges(); return _projectBadges[project].Ring.IsActive;
        }
        internal void TestCalendarRollover()
        {
            _calendarDay = DateTime.Today.AddDays(-1);
            GetType().GetMethod("RefreshCalendar")?.Invoke(this, null);
            DispatcherQueue.Drain();
        }
        internal void TestActions(Func<long, OpenCodeHomeSession, OpenCodeHomeSessionAction, bool> current, Action<OpenCodeHomeSessionActionEventArgs> requested)
        { CanRequestSessionAction = current; SessionActionRequested += (_, args) => requested(args); }
    }
}
