using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Right session card terminal strip: native toggle beside the browser, tiles below it.</summary>
internal sealed class OpenCodePersistentPtyInventoryView : Grid
{
    private const double ExpandedHeight = 72;
    private readonly ObservableCollection<UIElement> _items = new();
    private readonly ItemsRepeater _repeater;
    private readonly ScrollView _scroll;
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Button _refresh;
    private readonly Button _create = new() { Content = new FontIcon { Glyph = "\uE710", FontSize = 14 }, Width = 28, Height = 32,
        MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private bool _creating;
    private string? _createError;
    private readonly ToggleButton _toggle = new() { Width = 40, Height = 36, MinWidth = 0, Padding = new Thickness(0) };
    private readonly InfoBadge _count = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly Border _live = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = OpenCodeQuickAccessStyle.StatusDotMargin,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private OpenCodePersistentPtyInventoryContext? _context;
    private OpenCodePersistentPtyInventoryState? _state;
    private string? _openId;
    private bool _expanded;
    private Storyboard? _transition;
    internal event Action<OpenCodePersistentPtyInventoryRequest>? TerminalRequested;
    internal event Action<OpenCodePersistentPtyInventoryContext>? RefreshRequested;
    internal event Action<OpenCodePersistentPtyInventoryContext>? CreateRequested;

    /// <summary>The 40px session-card entry; the page places it beside the browser button.</summary>
    internal ToggleButton Button => _toggle;

    internal OpenCodePersistentPtyInventoryView()
    {
        Height = 0; Opacity = 0; Visibility = Visibility.Collapsed;
        Margin = new Thickness(-8, 0, -8, 0);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _repeater = new ItemsRepeater { ItemsSource = _items, ItemTemplate = new TileFactory(), Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 8 } };
        _scroll = new ScrollView { Content = _repeater, HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollingScrollMode.Enabled,
            VerticalScrollMode = ScrollingScrollMode.Disabled, ContentOrientation = ScrollingContentOrientation.Horizontal,
            Padding = new Thickness(8, 4, 8, 4) };
        Children.Add(_scroll); Children.Add(_status);
        _refresh = new Button { Width = 28, Height = 32, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0), Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 } };
        if (Application.Current?.Resources.TryGetValue("OpenCodeTransparentToolButtonStyle", out var quiet) == true && quiet is Style style) _refresh.Style = style;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { _create, _refresh } };
        Grid.SetColumn(actions, 1); Children.Add(actions);
        AutomationProperties.SetName(_create, "新建终端"); AutomationProperties.SetAutomationId(_create, "OpenCodePersistentPtyCreate");
        ToolTipService.SetToolTip(_create, "新建终端");
        _create.Click += (_, _) => RequestCreate();
        _status.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        AutomationProperties.SetName(this, "持久终端");
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyInventory");
        AutomationProperties.SetName(_refresh, "刷新持久终端列表"); ToolTipService.SetToolTip(_refresh, "刷新");
        _refresh.Click += (_, _) => { if (_context is { } context) RefreshRequested?.Invoke(context); };

        var glyph = OpenCodeQuickAccessStyle.TerminalIcon(); // Shared quick-access icon (>_ window), 16 DIP.
        _toggle.Content = new Grid { Width = 36, Height = 32, Children = { glyph, _count, _live } };
        // Same native frame as the browser entry: transparent rest state, subtle (never accent) checked state.
        _toggle.Style = (Style)XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ToggleButton\" BasedOn=\"{StaticResource DefaultToggleButtonStyle}\"><Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorTransparentBrush}\"/><Setter Property=\"BorderBrush\" Value=\"{ThemeResource ButtonBorderBrush}\"/><Setter Property=\"BorderThickness\" Value=\"{ThemeResource ButtonBorderThemeThickness}\"/><Setter Property=\"CornerRadius\" Value=\"{ThemeResource ControlCornerRadius}\"/></Style>");
        AutomationProperties.SetAutomationId(_toggle, "OpenCodePersistentPtyButton");
        _toggle.Click += (_, _) =>
        {
            if (_state is { Phase: OpenCodePersistentPtyInventoryPhase.Ready, Rows.Length: 0 }) { RequestCreate(); RefreshTheme(); }
            else SetExpanded(!_expanded);
        };
        _toggle.Loaded += (_, _) => RefreshTheme();
        _toggle.ActualThemeChanged += (_, _) => RefreshTheme();
        Unloaded += (_, _) =>
        {
            if (IsLoaded) return;
            _transition?.Stop(); _transition = null;
            Height = _expanded ? ExpandedHeight : 0d; Opacity = _expanded ? 1 : 0;
            Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        };
        Render(null, null);
    }

    internal void Render(OpenCodePersistentPtyInventoryContext? context, OpenCodePersistentPtyInventoryState? state)
    {
        if (_context != context) { _creating = false; _createError = null; }
        _context = context; _state = state;
        var available = context is not null && state?.Phase != OpenCodePersistentPtyInventoryPhase.Unsupported;
        _toggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available && _expanded) SetExpanded(false);
        _refresh.IsEnabled = context is not null && state?.Phase != OpenCodePersistentPtyInventoryPhase.Loading;
        _create.IsEnabled = context is not null && state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready && !_creating;
        _toggle.IsEnabled = !_creating;
        var rows = state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready ? state.Rows : default;
        var count = rows.IsDefault ? 0 : rows.Length;
        _status.Text = _creating ? "正在新建…" : _createError ?? (state?.Phase switch
        {
            OpenCodePersistentPtyInventoryPhase.Loading => "正在读取…",
            OpenCodePersistentPtyInventoryPhase.Unsupported => "当前服务不支持持久终端",
            OpenCodePersistentPtyInventoryPhase.Ready => count == 0 ? "此会话没有持久终端" : string.Empty,
            _ => "暂时无法确认持久终端列表",
        });
        _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _scroll.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _items.Clear();
        if (context is not null && count > 0)
            foreach (var info in rows) _items.Add(new Tile(info, info.Id == _openId, () => Request(context, info.Id)));
        _count.Value = count;
        _count.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed; // Same rule as the browser count.
        _live.Visibility = count > 0 && rows.Any(row => row.Status != "exited") ? Visibility.Visible : Visibility.Collapsed;
        var label = count == 0 && state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready ? "新建终端" : "持久终端 · " + count;
        AutomationProperties.SetName(_toggle, label); ToolTipService.SetToolTip(_toggle, label);
        RefreshTheme();
    }

    internal void SetCreationState(bool creating, string? error = null)
    {
        _creating = creating;
        if (creating || error is not null) { _createError = error; SetExpanded(true); }
        Render(_context, _state);
    }

    private void RequestCreate()
    {
        if (!_creating && _context is { } context && _state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready)
            CreateRequested?.Invoke(context);
    }

    /// <summary>Marks the tile whose terminal is shown in the floating card.</summary>
    internal void SetOpen(string? ptyId)
    {
        _openId = ptyId;
        foreach (var tile in _items.OfType<Tile>()) tile.SetShown(tile.Id == ptyId);
    }

    private void Request(OpenCodePersistentPtyInventoryContext context, string id)
    {
        if (context == _context && _state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready && _state.Rows.Any(row => row.Id == id))
            TerminalRequested?.Invoke(new(context, id));
    }

    private void RefreshTheme()
    {
        if (Application.Current is null) return;
        _toggle.IsChecked = _expanded;
        _live.Background = Brush("SystemFillColorSuccessBrush");
        _toggle.Resources["ToggleButtonBackgroundChecked"] = Brush("SubtleFillColorSecondaryBrush");
        _toggle.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Brush("SubtleFillColorSecondaryBrush");
        _toggle.Resources["ToggleButtonBackgroundCheckedPressed"] = Brush("SubtleFillColorTertiaryBrush");
        _toggle.Resources["ToggleButtonForegroundChecked"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonForegroundCheckedPointerOver"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonForegroundCheckedPressed"] = Brush("TextFillColorPrimaryBrush");
        _toggle.Resources["ToggleButtonBorderBrushChecked"] = Brush("ButtonBorderBrush");
        _toggle.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = Brush("ButtonBorderBrushPointerOver");
        _toggle.Resources["ToggleButtonBorderBrushCheckedPressed"] = Brush("ButtonBorderBrushPressed");
    }

    private void SetExpanded(bool expanded)
    {
        _toggle.IsChecked = expanded;
        if (_expanded == expanded) return;
        _expanded = expanded;
        _transition?.Stop(); _transition = null;
        if (expanded) Visibility = Visibility.Visible;
        var height = expanded ? ExpandedHeight : 0d;
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        { Height = height; Opacity = expanded ? 1 : 0; Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; return; }
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

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private sealed class TileFactory : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => (UIElement)args.Data;
        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    /// <summary>Miniature of the terminal surface itself: the same #0c0c0c ground in both themes.</summary>
    private sealed class Tile : Grid
    {
        private static readonly Windows.UI.Color Ground = Windows.UI.Color.FromArgb(255, 12, 12, 12);
        private readonly Border _outline = new() { CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
        private bool _shown;
        internal string Id { get; }

        internal Tile(PersistentPtyInfo info, bool shown, Action open)
        {
            Id = info.Id; _shown = shown; Width = 96; Height = 60; VerticalAlignment = VerticalAlignment.Top;
            var exited = info.Status == "exited";
            var mono = new FontFamily("Cascadia Mono, Consolas");
            var title = new TextBlock { Text = info.Title, FontFamily = mono, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 204, 204)) };
            var process = new TextBlock { Text = "$ " + (info.ForegroundProcess ?? info.Command), FontFamily = mono, FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)) };
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(exited ? Windows.UI.Color.FromArgb(255, 118, 118, 118) : Windows.UI.Color.FromArgb(255, 108, 203, 95)) };
            var state = new TextBlock { Text = exited ? "已退出" : "运行中", FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 150, 150, 150)) };
            var face = new Grid { Padding = new Thickness(8, 6, 8, 6), RowSpacing = 1 };
            face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            face.Children.Add(title); Grid.SetRow(process, 1); face.Children.Add(process);
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Bottom, Children = { dot, state } };
            Grid.SetRow(footer, 2); face.Children.Add(footer);
            var show = new Button { Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Ground), Content = face };
            // Keep the dark ground under pointer/press instead of the theme's button fill.
            foreach (var key in new[] { "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed" })
                show.Resources[key] = new SolidColorBrush(key == "ButtonBackground" ? Ground : Windows.UI.Color.FromArgb(255, 28, 28, 28));
            show.Click += (_, _) => open();
            Children.Add(show); Children.Add(_outline);
            var name = info.Title + "，" + state.Text;
            AutomationProperties.SetName(show, name);
            ToolTipService.SetToolTip(show, new ToolTip { Content = new StackPanel { Spacing = 4, Children = {
                new TextBlock { Text = name },
                new TextBlock { Text = string.Join(" ", new[] { info.Command }.Concat(info.Args)), FontFamily = mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
                new TextBlock { Text = info.Cwd, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, Opacity = 0.7 } } } });
            PointerEntered += (_, _) => Translation = new System.Numerics.Vector3(0, -2, 0);
            PointerExited += (_, _) => Translation = default;
            ActualThemeChanged += (_, _) => Render();
            Loaded += (_, _) => Render();
        }

        internal void SetShown(bool shown) { _shown = shown; Render(); }
        private void Render()
        {
            if (Application.Current is null) return;
            _outline.BorderBrush = Brush(_shown ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            _outline.BorderThickness = new Thickness(_shown ? 2 : 1);
        }
    }
}
