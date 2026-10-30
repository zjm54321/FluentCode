using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Flat image-only native repeater; pages stay owned by the engine, not these previews.</summary>
internal sealed class OpenCodeBrowserStrip : Grid
{
    private readonly ObservableCollection<UIElement> _items = new();
    private readonly Dictionary<IOpenCodeBrowserPage, Tile> _tiles = new();
    private readonly ItemsRepeater _repeater;
    private readonly ScrollView _scroll;
    private readonly Button _previous, _next;
    private const double ExpandedHeight = 72;
    private readonly DispatcherTimer _capture = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IOpenCodeBrowserWorkspace? _workspace;
    private Storyboard? _transition;
    private bool _expanded;

    public OpenCodeBrowserStrip()
    {
        Height = 0; Opacity = 0; Visibility = Visibility.Collapsed;
        Margin = new Thickness(-8, 0, -8, 0);
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _repeater = new ItemsRepeater { ItemsSource = _items, ItemTemplate = new TileFactory(), Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8 } };
        _scroll = new ScrollView { Content = _repeater, HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollingScrollMode.Enabled,
            VerticalScrollMode = ScrollingScrollMode.Disabled, ContentOrientation = ScrollingContentOrientation.Horizontal,
            Padding = new Thickness(8, 4, 8, 4) };
        _previous = ScrollButton("\uE76B", "向左滚动浏览器页面", -1);
        _next = ScrollButton("\uE76C", "向右滚动浏览器页面", 1);
        Children.Add(_previous); Grid.SetColumn(_scroll, 1); Children.Add(_scroll); Grid.SetColumn(_next, 2); Children.Add(_next);
        _scroll.ExtentChanged += (_, _) => UpdateScrollButtons();
        _scroll.ViewChanged += (_, _) => UpdateScrollButtons();
        _scroll.SizeChanged += (_, _) => UpdateScrollButtons();
        _scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(_scroll);
            if (point.Properties.IsHorizontalMouseWheel || _scroll.ScrollableWidth <= 0) return;
            _scroll.ScrollBy(-point.Properties.MouseWheelDelta, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled)); e.Handled = true;
        }), true);
        _capture.Tick += async (_, _) =>
        {
            foreach (var tile in _tiles.Values.Where(tile => !tile.IsExternal).ToArray()) await tile.CaptureAsync();
        };
        Loaded += (_, _) => { Refresh(); if (_expanded) _capture.Start(); };
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            _capture.Stop();
            _transition?.Stop(); _transition = null;
            Height = _expanded ? ExpandedHeight : 0d;
            Opacity = _expanded ? 1 : 0;
            Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            // Keep the workspace/tiles (and horizontal reading position) while the page is cached.
            // The page's final-release path calls Bind(null) to detach and dispose them.
        };
    }

    private Button ScrollButton(string glyph, string name, int direction)
    {
        var button = new Button { Width = 24, Height = 32, MinWidth = 0, Padding = new(0), VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Visibility = Visibility.Collapsed };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } style) button.Style = style;
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => _scroll.ScrollBy(direction * Math.Max(104, _scroll.ViewportWidth * .75), 0);
        return button;
    }

    private void UpdateScrollButtons()
    {
        var overflow = _scroll.ScrollableWidth > 1;
        _previous.Visibility = _next.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        _previous.IsEnabled = _scroll.HorizontalOffset > 1;
        _next.IsEnabled = _scroll.HorizontalOffset < _scroll.ScrollableWidth - 1;
    }

    public void Bind(IOpenCodeBrowserWorkspace? workspace)
    {
        if (ReferenceEquals(_workspace, workspace)) return;
        if (_workspace is not null) _workspace.PagesChanged -= PagesChanged;
        foreach (var tile in _tiles.Values) tile.Dispose();
        _tiles.Clear(); _items.Clear(); _workspace = workspace;
        if (_workspace is not null) _workspace.PagesChanged += PagesChanged;
        Refresh();
    }
    private void PagesChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        var pages = _workspace?.Pages ?? Array.Empty<IOpenCodeBrowserPage>();
        foreach (var old in _tiles.Keys.Where(p => !pages.Contains(p)).ToArray())
        {
            _items.Remove(_tiles[old]); _tiles[old].Dispose(); _tiles.Remove(old);
        }
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (!_tiles.TryGetValue(page, out var tile))
            {
                tile = new Tile(page, () => _workspace?.Show(page)); _tiles.Add(page, tile);
            }
            var index = _items.IndexOf(tile);
            if (index < 0) _items.Insert(i, tile);
            else if (index != i) _items.Move(index, i);
            tile.SetShown(ReferenceEquals(_workspace?.Shown, page));
        }
    }
    public void Flash(IOpenCodeBrowserPage page)
    {
        Refresh();
        if (_tiles.TryGetValue(page, out var tile)) tile.Flash();
    }
    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        _transition?.Stop(); _transition = null;
        if (expanded) { Visibility = Visibility.Visible; _capture.Start(); }
        else _capture.Stop();
        var height = expanded ? ExpandedHeight : 0d;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            Height = height; Opacity = expanded ? 1 : 0; Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; return;
        }
        var storyboard = new Storyboard();
        var size = new DoubleAnimation { From = ActualHeight, To = height, Duration = TimeSpan.FromMilliseconds(200), EnableDependentAnimation = true };
        var fade = new DoubleAnimation { From = Opacity, To = expanded ? 1 : 0, Duration = TimeSpan.FromMilliseconds(200) };
        Storyboard.SetTarget(size, this); Storyboard.SetTargetProperty(size, "Height");
        Storyboard.SetTarget(fade, this); Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(size); storyboard.Children.Add(fade); _transition = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_transition, storyboard)) return;
            Height = height; Opacity = expanded ? 1 : 0; storyboard.Stop(); _transition = null;
            if (!expanded) Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private sealed class TileFactory : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => (UIElement)args.Data;
        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    private sealed class Tile : Grid, IDisposable
    {
        internal bool IsExternal => _page is OpenCodeBrowserPage { Presentation: OpenCodeBrowserPresentation.ExternalWindow };
        private readonly IOpenCodeBrowserPage _page;
        private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
        private readonly Image _large = new() { Width = 254, Height = 159, Stretch = Stretch.Uniform };
        private readonly Border _outline;
        private readonly Button _show;
        private readonly Button _close;
        private readonly Border _badge = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4), Padding = new Thickness(3, 1, 3, 1), CornerRadius = new CornerRadius(4) };
        private readonly TextBlock _label = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, MaxWidth = 254 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 254, FontSize = 12 };
        private readonly OpenCodeBrowserPreviewThrottle _throttle = new();
        private readonly DispatcherTimer _flash = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private int _flashFrame;
        private bool _flashOn;
        private bool _shown;
        private bool _disposed;
        private byte[]? _bytes;
        private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly ToolTip _tooltip;

        public Tile(IOpenCodeBrowserPage page, Action show)
        {
            _page = page; Width = 96; Height = 60; VerticalAlignment = VerticalAlignment.Top;
            _show = new Button { Padding = new Thickness(0), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Content = _image, CornerRadius = new CornerRadius(5) };
            _show.Click += (_, _) => show();
            _outline = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), IsHitTestVisible = false };
            _close = new Button { Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(11),
                Content = new FontIcon { Glyph = "\uE8BB", FontSize = 10 }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3), Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(_close, "关闭页面");
            ToolTipService.SetToolTip(_close, "关闭页面");
            _close.Click += (_, _) => page.Close();
            Children.Add(_show); Children.Add(_outline); Children.Add(_badge); Children.Add(_close);
            _badge.IsHitTestVisible = false;
            _tooltip = new ToolTip { Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Bottom, Content = new StackPanel { Spacing = 6, Children = { _label, _status, _large } } };
            ToolTipService.SetToolTip(_show, _tooltip);
            // Set the exact approved 400ms delay rather than relying on the OS tooltip default.
            _hover.Tick += (_, _) => { _hover.Stop(); if (!_disposed) _tooltip.IsOpen = true; };
            _leave.Tick += (_, _) => { _leave.Stop(); _tooltip.IsOpen = false; };
            _tooltip.PointerEntered += (_, _) => _leave.Stop();
            _tooltip.PointerExited += (_, _) => _leave.Start();
            PointerEntered += (_, _) => { _leave.Stop(); _close.Visibility = Visibility.Visible; Translation = new System.Numerics.Vector3(0, -2, 0); _hover.Start(); };
            PointerExited += (_, _) => { _hover.Stop(); _leave.Start(); _close.Visibility = Visibility.Collapsed; Translation = default; };
            GotFocus += (_, _) => { _close.Visibility = Visibility.Visible; _hover.Start(); };
            LostFocus += (_, _) => { _hover.Stop(); _leave.Start(); _close.Visibility = Visibility.Collapsed; };
            AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed) return;
                e.Handled = true; page.Close();
            }), true);
            _flash.Tick += (_, _) =>
            {
                _flashOn = ++_flashFrame % 2 == 1; Render();
                if (_flashFrame >= 4) _flash.Stop();
            };
            page.PropertyChanged += Changed;
            ActualThemeChanged += (_, _) => Render();
            Render(); _ = LoadImageAsync();
        }

        public void SetShown(bool shown) { _shown = shown; Render(); }
        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(OpenCodeBrowserPage.PreviewBytes)) _ = LoadImageAsync();
            else _throttle.Invalidate();
            if (IsExternal && e.PropertyName is nameof(OpenCodeBrowserPage.Loading) or nameof(OpenCodeBrowserPage.IsReady)
                && _page is OpenCodeBrowserPage { IsReady: true, Loading: false }) _ = CaptureAsync();
            Render();
        }
        private void Render()
        {
            _outline.BorderBrush = OpenCodeBrowserButton.Brush(_flashOn ? "SystemFillColorCautionBrush" : _shown || _page.AgentActivity is not null ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            _outline.BorderThickness = new Thickness(_shown || _page.AgentActivity is not null || _flashOn ? 2 : 1);
            _badge.Background = OpenCodeBrowserButton.Brush("SolidBackgroundFillColorBaseBrush");
            _badge.Visibility = Visibility.Visible;
            if (_page.LoadError is not null) _badge.Child = new FontIcon { Glyph = "\uE7BA", FontSize = 12, Foreground = OpenCodeBrowserButton.Brush("SystemFillColorCriticalBrush") };
            else if (_page.AgentActivity is not null) _badge.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = {
                new FontIcon { Glyph = "\uEC26", FontSize = 12, Foreground = OpenCodeBrowserButton.Brush("AccentTextFillColorPrimaryBrush") }, new TextBlock { Text = "智能体", FontSize = 10 } } };
            else if (_page.Loading) _badge.Child = new ProgressRing { Width = 16, Height = 16, IsActive = true };
            else if (_page.PreviewPath is { } path) _badge.Child = new TextBlock { Text = OpenCodeBrowserPanelState.PreviewKind(path), FontSize = 10 };
            else _badge.Visibility = Visibility.Collapsed;
            var host = Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri) ? uri.Host : _page.Url;
            _label.Text = (string.IsNullOrEmpty(_page.Title) ? _page.PreviewPath ?? _page.Url : _page.Title) + " · " + host;
            _status.Text = (IsExternal ? "外部 Chrome · 独立窗口 · " : "") + OpenCodeBrowserPanelState.PageState(_page, _shown);
            AutomationProperties.SetName(_show, _label.Text + " · " + _status.Text);
        }
        public void Flash()
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
            _flashFrame = 0; _flashOn = true; Render(); _flash.Start();
        }
        public async Task CaptureAsync()
        {
            if (_disposed || _page is not OpenCodeBrowserPage page || !_throttle.TryBegin(DateTimeOffset.UtcNow)) return;
            try
            {
                var bytes = await page.CapturePreviewAsync();
                if (bytes is null) _throttle.Invalidate();
                else await LoadImageAsync();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or OperationCanceledException)
            { if (!_disposed) _throttle.Invalidate(); } // Navigation/close can temporarily invalidate a real WebView capture.
            finally { _throttle.Complete(DateTimeOffset.UtcNow); }
        }
        private async Task LoadImageAsync()
        {
            if (_disposed || _page is not OpenCodeBrowserPage page || page.PreviewBytes is not { } bytes || ReferenceEquals(bytes, _bytes)) return;
            _bytes = bytes;
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
                stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                if (!_disposed && ReferenceEquals(_bytes, bytes)) { _image.Source = bitmap; _large.Source = bitmap; }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or OperationCanceledException)
            { if (!_disposed && ReferenceEquals(_bytes, bytes)) { _bytes = null; _throttle.Invalidate(); } }
        }
        public void Dispose()
        {
            _disposed = true; _page.PropertyChanged -= Changed; _flash.Stop(); _hover.Stop(); _leave.Stop(); _tooltip.IsOpen = false;
        }
    }
}
