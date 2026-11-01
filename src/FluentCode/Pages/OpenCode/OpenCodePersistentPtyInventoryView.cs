using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Right session card terminal entry (the >_ tile) and the one-row strip below it, in the same arrangement as the
/// browser page strip. Persistent PTYs and running agent commands share one list, which the overlay's side list mirrors.
/// </summary>
internal sealed class OpenCodePersistentPtyInventoryView : Grid
{
    private const double StripHeight = 44;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
    private readonly ScrollViewer _scroll;
    private readonly Button _previous, _next;
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 8, 0) };
    private readonly Button _create;
    private bool _creating;
    private string? _createError;
    private readonly ToggleButton _toggle = new() { Width = 40, Height = 40, MinWidth = 0, Padding = new Thickness(0) };
    private readonly Border _live = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = OpenCodeQuickAccessStyle.StatusDotMargin,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private OpenCodePersistentPtyInventoryContext? _context;
    private OpenCodePersistentPtyInventoryState? _state;
    private string? _openId, _lastOpenedId;
    private IReadOnlyList<OpenCodeTerminalEntry> _shellEntries = Array.Empty<OpenCodeTerminalEntry>();
    private OpenCodeTerminalEntry[] _entries = Array.Empty<OpenCodeTerminalEntry>();
    private readonly Dictionary<string, Chip> _chips = new(StringComparer.Ordinal);
    internal event Action<OpenCodePersistentPtyInventoryRequest>? TerminalRequested;
    internal event Action<OpenCodePersistentPtyInventoryContext>? RefreshRequested;
    internal event Action<OpenCodePersistentPtyInventoryContext>? CreateRequested;
    internal event Func<OpenCodePersistentPtyInventoryContext, string, string?, Task<bool>>? RenameRequested;
    /// <summary>An agent command (shell tool) entry was chosen; opened read-only in the same overlay.</summary>
    internal event Action<string>? ShellRequested;
    /// <summary>× / middle click / menu: end a running PTY (after confirmation in the overlay) or remove an exited one.</summary>
    internal event Action<OpenCodeTerminalEntry>? EndRequested;
    /// <summary>The >_ tile was clicked while the overlay shows a terminal.</summary>
    internal event Action? CloseRequested;
    internal event Action? EntriesChanged;

    /// <summary>The 40px quick-access tile; the page places it beside the browser button.</summary>
    internal ToggleButton Button => _toggle;
    internal IReadOnlyList<OpenCodeTerminalEntry> Entries => _entries;
    internal string? OpenId => _openId;
    internal bool CanCreate => !_creating && _context is not null && _state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready;

    internal OpenCodePersistentPtyInventoryView()
    {
        Height = StripHeight; Visibility = Visibility.Collapsed;
        Margin = new Thickness(-6, 0, -6, 0); // The optional strip host owns the 8-DIP top gap.
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        _scroll = new ScrollViewer { Content = _row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false };
        Grid.SetColumn(_scroll, 1); Children.Add(_scroll);
        Grid.SetColumn(_status, 1); Children.Add(_status);
        _previous = OpenCodeTerminalUi.IconButton("\uE76B", "向左滚动终端", 24, 32, 12);
        _next = OpenCodeTerminalUi.IconButton("\uE76C", "向右滚动终端", 24, 32, 12);
        _previous.Click += (_, _) => _scroll.ChangeView(Math.Max(0, _scroll.HorizontalOffset - Math.Max(104, _scroll.ViewportWidth * 0.75)), null, null);
        _next.Click += (_, _) => _scroll.ChangeView(_scroll.HorizontalOffset + Math.Max(104, _scroll.ViewportWidth * 0.75), null, null);
        Children.Add(_previous); Grid.SetColumn(_next, 2); Children.Add(_next);
        _create = OpenCodeTerminalUi.IconButton("\uE710", "新建终端", 28, 28, 12);
        _create.Margin = new Thickness(2, 0, 0, 0);
        ToolTipService.SetToolTip(_create, "新建终端 · Ctrl+Shift+T");
        AutomationProperties.SetAutomationId(_create, "OpenCodePersistentPtyCreate");
        _create.Click += (_, _) => RequestCreate();
        Grid.SetColumn(_create, 3); Children.Add(_create);
        _scroll.ViewChanged += (_, _) => UpdateScrollButtons();
        _scroll.SizeChanged += (_, _) => UpdateScrollButtons();
        _row.SizeChanged += (_, _) => UpdateScrollButtons();
        // The wheel scrolls the one-row strip sideways, like the browser page strip.
        _scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var delta = args.GetCurrentPoint(_scroll).Properties.MouseWheelDelta;
            if (_scroll.ScrollableWidth <= 1 || delta == 0) return;
            _scroll.ChangeView(Math.Clamp(_scroll.HorizontalOffset - delta, 0, _scroll.ScrollableWidth), null, null, true);
            args.Handled = true;
        }), true);
        _status.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        AutomationProperties.SetName(this, "终端");
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyInventory");

        var glyph = OpenCodeQuickAccessStyle.TerminalIcon(); // Shared quick-access icon (>_ window), 16 DIP.
        _toggle.Content = new Grid { Width = 36, Height = 32, Children = { glyph, _live } };
        // Same native frame as the browser entry: transparent rest state, subtle (never accent) checked state.
        _toggle.Style = (Style)XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ToggleButton\" BasedOn=\"{StaticResource DefaultToggleButtonStyle}\"><Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorTransparentBrush}\"/><Setter Property=\"BorderBrush\" Value=\"{ThemeResource ButtonBorderBrush}\"/><Setter Property=\"BorderThickness\" Value=\"{ThemeResource ButtonBorderThemeThickness}\"/><Setter Property=\"CornerRadius\" Value=\"{ThemeResource ControlCornerRadius}\"/></Style>");
        Services.Appearance.ThemeResources.Aliases(_toggle,
            ("ToggleButtonBackgroundChecked", "SubtleFillColorSecondaryBrush"),
            ("ToggleButtonBackgroundCheckedPointerOver", "SubtleFillColorSecondaryBrush"),
            ("ToggleButtonBackgroundCheckedPressed", "SubtleFillColorTertiaryBrush"),
            ("ToggleButtonForegroundChecked", "TextFillColorPrimaryBrush"),
            ("ToggleButtonForegroundCheckedPointerOver", "TextFillColorPrimaryBrush"),
            ("ToggleButtonForegroundCheckedPressed", "TextFillColorPrimaryBrush"),
            ("ToggleButtonBorderBrushChecked", "ButtonBorderBrush"),
            ("ToggleButtonBorderBrushCheckedPointerOver", "ButtonBorderBrushPointerOver"),
            ("ToggleButtonBorderBrushCheckedPressed", "ButtonBorderBrushPressed"));
        AutomationProperties.SetAutomationId(_toggle, "OpenCodePersistentPtyButton");
        _toggle.Click += (_, _) => { ToggleOverlay(); RefreshTheme(); };
        _toggle.Loaded += (_, _) => RefreshTheme();
        _toggle.ActualThemeChanged += (_, _) => RefreshTheme();
        Render(null, null);
    }

    internal void Render(OpenCodePersistentPtyInventoryContext? context, OpenCodePersistentPtyInventoryState? state)
    {
        if (_context != context) { _creating = false; _createError = null; }
        _context = context; _state = state;
        var available = context is not null && state?.Phase != OpenCodePersistentPtyInventoryPhase.Unsupported;
        _toggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _create.IsEnabled = CanCreate;
        _toggle.IsEnabled = !_creating;
        var rows = state?.Phase is OpenCodePersistentPtyInventoryPhase.Ready or OpenCodePersistentPtyInventoryPhase.Loading
            ? state.Rows.ToArray() : Array.Empty<PersistentPtyInfo>();
        // Persistent terminals (live and exited ones the backend still retains) first, then running agent commands.
        var entries = rows.Select(OpenCodeTerminalUi.Entry).Concat(_shellEntries.Where(shell => rows.All(row => row.Id != shell.Id))).ToArray();
        _status.Text = _creating ? "正在新建…" : _createError ?? (state?.Phase switch
        {
            OpenCodePersistentPtyInventoryPhase.Loading => entries.Length == 0 ? "正在读取…" : string.Empty,
            OpenCodePersistentPtyInventoryPhase.Unsupported => string.Empty,
            OpenCodePersistentPtyInventoryPhase.Ready => string.Empty,
            null => string.Empty,
            _ => "暂时无法读取终端列表",
        });
        _status.Visibility = _status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Visibility = available && (entries.Length > 0 || _status.Text.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        ApplyEntries(entries);
        var running = entries.Count(entry => entry.State is OpenCodeTerminalState.Running or OpenCodeTerminalState.Elsewhere);
        _live.Visibility = running > 0 ? Visibility.Visible : Visibility.Collapsed;
        var label = entries.Length == 0 ? "新建终端" : "终端 · " + entries.Length;
        AutomationProperties.SetName(_toggle, label);
        ToolTipService.SetToolTip(_toggle, entries.Length == 0 ? "新建终端 · Ctrl+Shift+T" : _openId is null ? "打开终端 · Ctrl+`" : "关闭终端浮窗 · Ctrl+`");
        RefreshTheme();
    }

    /// <summary>Running agent commands (background shell tools) and the agent command currently shown in the overlay.</summary>
    internal void SetShellEntries(IReadOnlyList<OpenCodeTerminalEntry> shells)
    {
        if (shells.SequenceEqual(_shellEntries)) return;
        _shellEntries = shells;
        Render(_context, _state);
    }

    private void ApplyEntries(OpenCodeTerminalEntry[] entries)
    {
        var changed = !entries.SequenceEqual(_entries);
        _entries = entries;
        if (!changed) return;
        // Reconcile in place so an unchanged chip keeps hover/focus during the 2 s inventory refresh.
        var keep = new HashSet<string>(entries.Select(Key), StringComparer.Ordinal);
        foreach (var stale in _chips.Keys.Where(key => !keep.Contains(key)).ToArray()) { _row.Children.Remove(_chips[stale]); _chips.Remove(stale); }
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (!_chips.TryGetValue(Key(entry), out var chip))
            {
                chip = new Chip(this);
                _chips[Key(entry)] = chip;
            }
            chip.Update(entry, entry.Id == _openId);
            var at = _row.Children.IndexOf(chip);
            if (at != index) { if (at >= 0) _row.Children.RemoveAt(at); _row.Children.Insert(index, chip); }
        }
        EntriesChanged?.Invoke();
        DispatcherQueue?.TryEnqueue(UpdateScrollButtons);
    }

    private static string Key(OpenCodeTerminalEntry entry) => entry.Kind + ":" + entry.Id;

    internal void SetCreationState(bool creating, string? error = null)
    {
        _creating = creating;
        if (creating || error is not null) _createError = error;
        Render(_context, _state);
    }

    internal void RequestCreate()
    {
        if (CanCreate && _context is { } context) CreateRequested?.Invoke(context);
    }

    /// <summary>Marks the chip whose terminal is shown in the floating card.</summary>
    internal void SetOpen(string? id)
    {
        _openId = id;
        if (id is not null) _lastOpenedId = id;
        foreach (var (key, chip) in _chips) chip.SetShown(chip.Entry?.Id == id);
        if (id is not null && _chips.Values.FirstOrDefault(chip => chip.Entry?.Id == id) is { } shown)
            DispatcherQueue?.TryEnqueue(() => shown.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }));
        RefreshTheme();
        EntriesChanged?.Invoke();
    }

    internal void Open(OpenCodeTerminalEntry entry)
    {
        if (entry.Kind == OpenCodeTerminalEntryKind.Shell) { ShellRequested?.Invoke(entry.Id); return; }
        if (_context is { } context && _state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready && _state.Rows.Any(row => row.Id == entry.Id))
            TerminalRequested?.Invoke(new(context, entry.Id));
    }

    internal void End(OpenCodeTerminalEntry entry) { if (entry.CanEnd) EndRequested?.Invoke(entry); }
    internal Func<string?, Task<bool>> RenameAction(OpenCodeTerminalEntry entry)
    {
        var context = _context; // Capture the scope when editing begins, not when Enter is eventually pressed.
        return title => entry.Kind == OpenCodeTerminalEntryKind.Pty && context is not null && context == _context && RenameRequested is { } rename
            ? rename(context, entry.Id, title) : Task.FromResult(false);
    }

    /// <summary>Ctrl+Tab order shared by the strip and the overlay list.</summary>
    internal void Step(int direction)
    {
        if (_entries.Length < 2) return;
        var index = Array.FindIndex(_entries, entry => entry.Id == _openId);
        Open(_entries[((index < 0 ? 0 : index + direction) % _entries.Length + _entries.Length) % _entries.Length]);
    }

    internal void ToggleOverlay()
    {
        if (_openId is not null) { CloseRequested?.Invoke(); return; }
        if (_state?.Phase is not (OpenCodePersistentPtyInventoryPhase.Ready or OpenCodePersistentPtyInventoryPhase.Loading) && _context is { } context)
        { RefreshRequested?.Invoke(context); return; }
        var target = _entries.FirstOrDefault(entry => entry.Id == _lastOpenedId)
            ?? _entries.FirstOrDefault(entry => entry.State is OpenCodeTerminalState.Running or OpenCodeTerminalState.Elsewhere)
            ?? _entries.FirstOrDefault();
        if (target is not null) Open(target);
        else RequestCreate();
    }

    private void UpdateScrollButtons()
    {
        var overflow = _scroll.ScrollableWidth > 1;
        _previous.Visibility = _next.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        _previous.IsEnabled = _scroll.HorizontalOffset > 1;
        _next.IsEnabled = _scroll.HorizontalOffset < _scroll.ScrollableWidth - 1;
    }

    private void RefreshTheme()
    {
        if (Application.Current is null) return;
        _toggle.IsChecked = _openId is not null;
        OpenCodeTerminalUi.Themed(_live, "Background", "SystemFillColorSuccessBrush");
    }

    /// <summary>32px chip: >_ · command (≤104) · status dot; hover swaps the dot for ×; 2px accent outline while shown.</summary>
    private sealed class Chip : Grid
    {
        private readonly OpenCodePersistentPtyInventoryView _owner;
        private readonly Button _open = new() { Height = 40, MinWidth = 40, Padding = new Thickness(8, 0, 4, 0), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title = new() { FontSize = 12, MaxWidth = 104, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly FrameworkElement _icon = OpenCodeQuickAccessStyle.TerminalIcon(14);
        private readonly ContentControl _renameFeedback = new() { Width = 14, Height = 16, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly Border _dot = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _end = OpenCodeTerminalUi.IconButton("\uE711", "结束终端", 20, 20, 10);
        private readonly Border _outline = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly MenuFlyoutItem _endItem = new() { Text = "结束终端", Icon = new FontIcon { Glyph = "\uE7E8" } };
        private readonly MenuFlyoutItem _renameItem = new() { Text = "重命名…", KeyboardAcceleratorTextOverride = "F2", Icon = new FontIcon { Glyph = "\uE8AC" } };
        private readonly OpenCodeTerminalRenameBox _rename = new() { MinWidth = 144, Margin = new Thickness(4, 2, 4, 2) };
        private bool _hover;
        internal OpenCodeTerminalEntry? Entry { get; private set; }

        internal Chip(OpenCodePersistentPtyInventoryView owner)
        {
            _owner = owner; Height = 44; VerticalAlignment = VerticalAlignment.Center;
            // New chip: scale 0.9→1 plus fade (167ms); press: 0.97 (render-only, hit area unchanged).
            FluentCode.Motion.FluentMotion.ChipEntrance(this);
            FluentCode.Motion.FluentMotion.PressScale(this);
            _end.Visibility = Visibility.Collapsed; _end.VerticalAlignment = VerticalAlignment.Center;
            _renameFeedback.Content = _icon;
            _open.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { _renameFeedback, _title, new Grid { Width = 40, Children = { _dot, _end } } } };
            _dot.HorizontalAlignment = HorizontalAlignment.Center; _dot.Margin = new Thickness(0);
            _open.Click += (_, _) => { if (Entry is { } entry) { if (entry.Id == _owner._openId) _owner.CloseRequested?.Invoke(); else _owner.Open(entry); } };
            _end.Click += (_, _) => { if (Entry is { } entry) _owner.End(entry); };
            Children.Add(_open); Children.Add(_outline);
            Children.Add(_rename);
            _rename.Finished += () => _open.Focus(FocusState.Programmatic);
            _renameItem.Click += (_, _) => BeginRename();
            _open.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.F2 && BeginRename()) args.Handled = true; };
            var tip = new ToolTip { Placement = PlacementMode.Bottom };
            ToolTipService.SetToolTip(_open, tip);
            var menu = new MenuFlyout();
            var open = new MenuFlyoutItem { Text = "在浮窗中打开", Icon = new FontIcon { Glyph = "\uE8A7" } };
            open.Click += (_, _) => { if (Entry is { } entry) _owner.Open(entry); };
            var copy = new MenuFlyoutItem { Text = "复制命令", Icon = new FontIcon { Glyph = "\uE8C8" } };
            copy.Click += (_, _) =>
            {
                if (Entry is not { } entry) return;
                try
                {
                    var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(entry.Title); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                    FluentCode.Motion.ActionFeedback.Announce(_open, "已复制终端命令");
                }
                catch (Exception) { FluentCode.Motion.ActionFeedback.Announce(_open, "复制失败，请重试"); }
            };
            _endItem.Click += (_, _) => { if (Entry is { } entry) _owner.End(entry); };
            menu.Items.Add(open); menu.Items.Add(_renameItem); menu.Items.Add(copy); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(_endItem);
            _open.ContextFlyout = menu;
            PointerEntered += (_, _) => { _hover = true; Paint(); };
            PointerExited += (_, _) => { _hover = false; Paint(); };
            _open.GotFocus += (_, _) => Paint(); _open.LostFocus += (_, _) => Paint();
            AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                // Middle click is the same as ×, as on the browser strip.
                if (args.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed && Entry is { } entry)
                { _owner.End(entry); args.Handled = true; }
            }), true);
            ActualThemeChanged += (_, _) => Paint();
            Loaded += (_, _) => Paint();
        }

        internal void Update(OpenCodeTerminalEntry entry, bool shown)
        {
            if (Entry?.Id != entry.Id) _rename.Cancel();
            Entry = entry;
            _renameItem.Visibility = entry.Kind == OpenCodeTerminalEntryKind.Pty ? Visibility.Visible : Visibility.Collapsed;
            _title.Text = entry.Title;
            var name = entry.Title + " · " + entry.Source + " · " + entry.StateText;
            AutomationProperties.SetName(_open, name);
            if (ToolTipService.GetToolTip(_open) is ToolTip tip) tip.Content = name;
            var ending = entry.State is OpenCodeTerminalState.Done or OpenCodeTerminalState.Failed ? "移除终端" : "结束终端";
            AutomationProperties.SetName(_end, ending); ToolTipService.SetToolTip(_end, ending);
            _endItem.Text = ending; _endItem.Visibility = entry.CanEnd ? Visibility.Visible : Visibility.Collapsed;
            SetShown(shown);
        }

        internal void SetShown(bool shown) { _outline.Visibility = shown ? Visibility.Visible : Visibility.Collapsed; Paint(); }

        private bool BeginRename()
        {
            if (Entry is not { Kind: OpenCodeTerminalEntryKind.Pty } entry) return false;
            var save = _owner.RenameAction(entry);
            _rename.Begin(entry.Title, async title =>
            {
                var saved = await save(title);
                if (saved && Entry?.Id == entry.Id) FluentCode.Motion.ActionFeedback.Success(_renameFeedback, "已保存终端名称");
                return saved;
            });
            return true;
        }

        private void Paint()
        {
            if (Application.Current is null || Entry is not { } entry) return;
            var active = _hover || _outline.Visibility == Visibility.Visible;
            OpenCodeTerminalUi.Themed(_title, "Foreground", active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            OpenCodeTerminalUi.Themed(_outline, "BorderBrush", "AccentFillColorDefaultBrush");
            var showEnd = entry.CanEnd && (_hover || _open.FocusState != FocusState.Unfocused);
            _end.Visibility = showEnd ? Visibility.Visible : Visibility.Collapsed;
            _dot.Visibility = showEnd ? Visibility.Collapsed : Visibility.Visible;
            OpenCodeTerminalUi.PaintDot(_dot, entry.State);
        }
    }
}

/// <summary>The overlay's collapsible side list (220): the same entries as the right-panel strip.</summary>
internal sealed class OpenCodeTerminalSwitcher : Grid
{
    internal const double ExpandedWidth = 220;
    private readonly OpenCodePersistentPtyInventoryView _inventory;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _count = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _create = OpenCodeTerminalUi.IconButton("\uE710", "新建终端", 28, 28, 12);
    internal event Action? CountChanged;
    internal int Count => _inventory.Entries.Count;
    internal bool IsRenaming => _rows.Children.OfType<Row>().Any(row => row.IsRenaming);
    /// <summary>The view border currently hosting this single list instance.</summary>
    internal Border? Host { get; set; }

    internal OpenCodeTerminalSwitcher(OpenCodePersistentPtyInventoryView inventory)
    {
        _inventory = inventory; Width = ExpandedWidth; Padding = new Thickness(8, 0, 8, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new TextBlock { Text = "终端", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeTerminalUi.Themed(heading, "Foreground", "TextFillColorSecondaryBrush");
        OpenCodeTerminalUi.Themed(_count, "Foreground", "TextFillColorTertiaryBrush");
        var header = new Grid { Height = 44, Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { heading, _count } });
        _create.HorizontalAlignment = HorizontalAlignment.Right; ToolTipService.SetToolTip(_create, "新建终端 · Ctrl+Shift+T");
        _create.Click += (_, _) => _inventory.RequestCreate();
        header.Children.Add(_create);
        Children.Add(header);
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); Children.Add(scroll);
        AutomationProperties.SetName(this, "终端列表");
        AutomationProperties.SetAutomationId(this, "OpenCodeTerminalSwitcher");
        inventory.EntriesChanged += Render;
        Render();
    }

    private void Render()
    {
        var entries = _inventory.Entries;
        _count.Text = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _create.IsEnabled = _inventory.CanCreate;
        while (_rows.Children.Count > entries.Count) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        for (var index = 0; index < entries.Count; index++)
        {
            if (index >= _rows.Children.Count) _rows.Children.Add(new Row(_inventory));
            ((Row)_rows.Children[index]).Update(entries[index], entries[index].Id == _inventory.OpenId);
        }
        CountChanged?.Invoke();
    }

    private sealed class Row : Grid
    {
        private readonly OpenCodePersistentPtyInventoryView _inventory;
        private readonly Button _open = new() { Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 0, 6, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0) };
        private readonly Border _selected = new() { CornerRadius = new CornerRadius(4), IsHitTestVisible = false };
        private readonly Border _bar = new() { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly TextBlock _title = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly TextBlock _state = new() { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        private readonly Border _dot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _end = OpenCodeTerminalUi.IconButton("\uE711", "结束终端", 24, 24, 10);
        private readonly FrameworkElement _icon = OpenCodeQuickAccessStyle.TerminalIcon(16);
        private readonly ContentControl _renameFeedback = new() { Width = 16, Height = 18, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly OpenCodeTerminalRenameBox _rename = new() { Margin = new Thickness(28, 2, 4, 2) };
        private readonly MenuFlyoutItem _renameItem = new() { Text = "重命名…", KeyboardAcceleratorTextOverride = "F2", Icon = new FontIcon { Glyph = "\uE8AC" } };
        internal bool IsRenaming => _rename.IsEditing;
        private bool _hover, _isSelected;
        private OpenCodeTerminalEntry? _entry;

        internal Row(OpenCodePersistentPtyInventoryView inventory)
        {
            _inventory = inventory;
            if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quiet) _open.Style = quiet;
            _open.Height = 44; _open.HorizontalAlignment = HorizontalAlignment.Stretch; _open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _open.Padding = new Thickness(10, 0, 6, 0);
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            _renameFeedback.Content = _icon;
            content.Children.Add(_renameFeedback);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _state } };
            Grid.SetColumn(text, 1); content.Children.Add(text);
            _end.Visibility = Visibility.Collapsed;
            var trail = new Grid { Children = { _dot, _end } };
            Grid.SetColumn(trail, 2); content.Children.Add(trail);
            _open.Content = content;
            _open.Click += (_, _) => { if (_entry is { } entry && entry.Id != _inventory.OpenId) _inventory.Open(entry); };
            _end.Click += (_, _) => { if (_entry is { } entry) _inventory.End(entry); };
            Children.Add(_selected); Children.Add(_open); Children.Add(_bar);
            Children.Add(_rename);
            _rename.Finished += () => _open.Focus(FocusState.Programmatic);
            _renameItem.Click += (_, _) => BeginRename();
            _open.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.F2 && BeginRename()) args.Handled = true; };
            var menu = new MenuFlyout();
            var openItem = new MenuFlyoutItem { Text = "在浮窗中打开", Icon = new FontIcon { Glyph = "\uE8A7" } };
            openItem.Click += (_, _) => { if (_entry is { } entry) _inventory.Open(entry); };
            var endItem = new MenuFlyoutItem { Text = "结束 / 移除终端", Icon = new FontIcon { Glyph = "\uE7E8" } };
            endItem.Click += (_, _) => { if (_entry is { } entry) _inventory.End(entry); };
            menu.Items.Add(openItem); menu.Items.Add(_renameItem); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(endItem);
            _open.ContextFlyout = menu;
            OpenCodeTerminalUi.Themed(_state, "Foreground", "TextFillColorTertiaryBrush");
            PointerEntered += (_, _) => { _hover = true; Paint(); };
            PointerExited += (_, _) => { _hover = false; Paint(); };
            _open.GotFocus += (_, _) => Paint(); _open.LostFocus += (_, _) => Paint();
            ActualThemeChanged += (_, _) => Paint();
            Loaded += (_, _) => Paint();
        }

        internal void Update(OpenCodeTerminalEntry entry, bool selected)
        {
            if (_entry?.Id != entry.Id) _rename.Cancel();
            _entry = entry; _isSelected = selected;
            _renameItem.Visibility = entry.Kind == OpenCodeTerminalEntryKind.Pty ? Visibility.Visible : Visibility.Collapsed;
            _title.Text = entry.Title;
            _state.Text = entry.StateText;
            AutomationProperties.SetName(_open, entry.Title + " · " + entry.StateText);
            ToolTipService.SetToolTip(_open, entry.Title + "\n" + entry.Source + (entry.Cwd is { Length: > 0 } cwd ? " · " + cwd : string.Empty));
            var ending = entry.State is OpenCodeTerminalState.Done or OpenCodeTerminalState.Failed ? "移除终端" : "结束终端";
            AutomationProperties.SetName(_end, ending); ToolTipService.SetToolTip(_end, ending);
            Paint();
        }

        private bool BeginRename()
        {
            if (_entry is not { Kind: OpenCodeTerminalEntryKind.Pty } entry) return false;
            var save = _inventory.RenameAction(entry);
            _rename.Begin(entry.Title, async title =>
            {
                var saved = await save(title);
                if (saved && _entry?.Id == entry.Id) FluentCode.Motion.ActionFeedback.Success(_renameFeedback, "已保存终端名称");
                return saved;
            });
            return true;
        }

        private void Paint()
        {
            if (Application.Current is null || _entry is not { } entry) return;
            OpenCodeTerminalUi.Themed(_selected, "Background", _isSelected ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush");
            OpenCodeTerminalUi.Themed(_bar, "Background", "AccentFillColorDefaultBrush");
            _bar.Visibility = _isSelected ? Visibility.Visible : Visibility.Collapsed;
            OpenCodeTerminalUi.Themed(_title, "Foreground", "TextFillColorPrimaryBrush");
            OpenCodeTerminalUi.Themed((FrameworkElement)_icon, "Foreground", _isSelected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            var showEnd = entry.CanEnd && (_hover || _open.FocusState != FocusState.Unfocused);
            _end.Visibility = showEnd ? Visibility.Visible : Visibility.Collapsed;
            _dot.Visibility = showEnd ? Visibility.Collapsed : Visibility.Visible;
            OpenCodeTerminalUi.PaintDot(_dot, entry.State);
        }
    }
}

/// <summary>Shared terminal chrome: the one status component (dot + text), subtle icon buttons and themed brushes.</summary>
internal static class OpenCodeTerminalUi
{
    private static readonly Dictionary<(string, string, string), Style> Styles = new();

    internal static OpenCodeTerminalEntry Entry(PersistentPtyInfo row)
    {
        var exited = row.Status == "exited";
        var failed = exited && row.ExitCode is { } code && code != 0;
        var state = !exited ? OpenCodeTerminalState.Running : failed ? OpenCodeTerminalState.Failed : OpenCodeTerminalState.Done;
        var text = !exited ? "运行中" : OpenCodeTerminalNames.ExitText(row.ExitCode);
        return new(OpenCodeTerminalEntryKind.Pty, row.Id, OpenCodeTerminalNames.Title(row.Title, row.Command, row.UserTitle), "终端", row.Cwd, state, text, true);
    }

    internal static string BrushKey(OpenCodeTerminalState state) => state switch
    {
        OpenCodeTerminalState.Running => "SystemFillColorSuccessBrush",
        OpenCodeTerminalState.Failed or OpenCodeTerminalState.Disconnected => "SystemFillColorCriticalBrush",
        OpenCodeTerminalState.Connecting => "SystemFillColorCautionBrush",
        OpenCodeTerminalState.Elsewhere => "TextFillColorSecondaryBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    /// <summary>Filled dot for every state except "another client is typing", which is a neutral ring.</summary>
    internal static void PaintDot(Border dot, OpenCodeTerminalState state)
    {
        var ring = state == OpenCodeTerminalState.Elsewhere;
        dot.BorderThickness = new Thickness(ring ? 1.5 : 0);
        dot.ClearValue(Border.BackgroundProperty); dot.ClearValue(Border.BorderBrushProperty);
        Themed(dot, ring ? "BorderBrush" : "Background", BrushKey(state));
    }

    /// <summary>ThemeResource-backed setter, so element-level light/dark overrides repaint without code.</summary>
    internal static void Themed(FrameworkElement target, string property, string key)
    {
        var id = (target.GetType().Name, property, key);
        if (!Styles.TryGetValue(id, out var style))
            Styles[id] = style = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\""
                + target.GetType().Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
        target.Style = style;
    }

    internal static Button IconButton(string glyph, string name, double width = 32, double height = 32, double size = 14)
    {
        // Terminal-only hit targets. Glyphs stay small; their transparent button area is touch-sized.
        width = Math.Max(40, width); height = Math.Max(40, height);
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size },
            Width = width, Height = height, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } quiet) button.Style = quiet;
        button.Width = width; button.Height = height; button.CornerRadius = new CornerRadius(4);
        // Subtle buttons stay transparent when disabled (the default style paints a filled disabled plate).
        button.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Resources["ButtonBorderBrushDisabled"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }
}

/// <summary>Inline editor outside the terminal HWND. Enter saves, Esc/focus loss cancels; empty clears userTitle.</summary>
internal sealed class OpenCodeTerminalRenameBox : TextBox
{
    private Func<string?, Task<bool>>? _save;
    private bool _saving;
    internal bool IsEditing => Visibility == Visibility.Visible;
    internal event Action? Finished;

    internal OpenCodeTerminalRenameBox()
    {
        Visibility = Visibility.Collapsed; MinHeight = 36; VerticalAlignment = VerticalAlignment.Center;
        PlaceholderText = "终端名称（留空恢复默认）";
        AutomationProperties.SetName(this, "重命名终端");
        AutomationProperties.SetAutomationId(this, "OpenCodeTerminalRenameBox");
        KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Cancel(); }
            else if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                if (_saving || _save is not { } save) return;
                _saving = true; IsReadOnly = true;
                var succeeded = false;
                var message = "重命名失败，请重试";
                try { succeeded = await save(string.IsNullOrWhiteSpace(Text) ? null : Text.Trim()); }
                catch (PersistentPtyException error) when (error.Code == "rename-unsupported") { message = "当前终端服务需重启后才能重命名"; }
                _saving = false; IsReadOnly = false;
                if (succeeded) Cancel();
                else if (IsEditing)
                {
                    ToolTipService.SetToolTip(this, new ToolTip { Content = message, Placement = PlacementMode.Top, IsOpen = true });
                    AutomationProperties.SetHelpText(this, message); Focus(FocusState.Programmatic);
                }
            }
        };
        LostFocus += (_, _) => { if (!_saving) Cancel(restoreFocus: false); };
        Unloaded += (_, _) => Cancel(restoreFocus: false);
    }

    internal void Begin(string title, Func<string?, Task<bool>> save)
    {
        if (_saving) return;
        _save = save; Text = title; ToolTipService.SetToolTip(this, "Enter 保存 · Esc 取消 · 留空恢复默认名称");
        Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => { if (IsEditing) { Focus(FocusState.Programmatic); SelectAll(); } });
    }

    internal void Cancel(bool restoreFocus = true)
    {
        if (!IsEditing) return;
        if (ToolTipService.GetToolTip(this) is ToolTip tip) tip.IsOpen = false;
        Visibility = Visibility.Collapsed; _save = null;
        if (restoreFocus) Finished?.Invoke();
    }
}
