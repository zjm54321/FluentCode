using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using WindowsTerminalControl.WinUI;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Terminal card (design B2): 56px header (☰ list · title/subtitle · status · tools), collapsible side list and a
/// terminal well in the scheme background with a thin XAML scrollbar beside — never over — the native HWND.
/// Persistent PTYs and recorded agent-command output use this same view. Close only detaches; ending is explicit.
/// </summary>
internal sealed class OpenCodePersistentPtyView : Grid
{
    internal const double HeaderHeight = 56;
    /// <summary>List expansion is remembered for the app session; null means "not chosen yet" (≥2 terminals start expanded).</summary>
    internal static bool? ListExpanded;
    internal OpenCodePersistentPtyRenderer Renderer { get; } = new();
    private readonly Grid _header = new() { Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 10 };
    private readonly Button _menu = OpenCodeTerminalUi.IconButton("\uE700", "终端列表", 36, 36, 16);
    private readonly Border _badge = new() { MinWidth = 16, Height = 16, CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -6, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _badgeText = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _subtitle = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(24, 0, 0, 0) };
    private readonly Border _statusDot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusPill = new() { Height = 26, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _readOnly = new() { Height = 22, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _jump = new() { Height = 40, MinWidth = 40, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _jumpText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _find = OpenCodeTerminalUi.IconButton("\uE721", "查找 · Ctrl+Shift+F");
    private readonly Button _copy = OpenCodeTerminalUi.IconButton("\uE8C8", "复制 · Ctrl+Shift+C");
    private readonly Button _paste = OpenCodeTerminalUi.IconButton("\uE77F", "粘贴 · Ctrl+Shift+V");
    private readonly Button _more = OpenCodeTerminalUi.IconButton("\uE712", "更多");
    private readonly Button _back = OpenCodeTerminalUi.IconButton("\uE711", "关闭窗口 · Ctrl+`（终端继续运行）");
    private readonly Grid _confirm = new() { Padding = new Thickness(16, 0, 12, 0), ColumnSpacing = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock _confirmTitle = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _confirmDetail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _confirmEnd = new() { Content = "结束终端", MinWidth = 96, Height = 40 };
    private readonly Button _confirmCancel = new() { Content = "取消", MinWidth = 96, Height = 40 };
    private readonly InfoBar _notice = new() { IsClosable = false, IsOpen = false, Severity = InfoBarSeverity.Warning, CornerRadius = new CornerRadius(4), Margin = new Thickness(12, 0, 12, 8) };
    private readonly Button _reconnect = new() { Content = "重新连接", MinHeight = 40 };
    private readonly ColumnDefinition _listColumn = new() { Width = new GridLength(0) };
    private readonly Border _listHost = new();
    private readonly Border _well = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly Grid _findBar = new() { ColumnSpacing = 2, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly StackPanel _titles = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    private readonly TextBox _findBox = new() { Width = 300, MinHeight = 40, PlaceholderText = "查找", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _findCount = new() { FontSize = 12, MinWidth = 44, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    private readonly ToggleButton _findCase = new() { Content = "Aa", Width = 40, Height = 40, MinWidth = 0, Padding = new Thickness(0), FontSize = 12 };
    private readonly ToggleButton _findRegex = new() { Content = ".*", Width = 40, Height = 40, MinWidth = 0, Padding = new Thickness(0), FontSize = 12 };
    private readonly Canvas _markers = new() { IsHitTestVisible = false };
    private readonly Border _marker = new() { Width = 3, CornerRadius = new CornerRadius(1.5), Visibility = Visibility.Collapsed };
    private readonly OpenCodeTerminalScrollBar _scrollBar;
    private readonly MenuFlyout _moreMenu = new();
    private readonly MenuFlyoutItem _createItem = new() { Text = "新建终端", Icon = new FontIcon { Glyph = "\uE710" } };
    private readonly MenuFlyoutItem _copyAllItem = new() { Text = "复制全部输出", Icon = new FontIcon { Glyph = "\uE8C8" } };
    private readonly MenuFlyoutItem _appearanceItem = new() { Text = "终端外观", Icon = new FontIcon { Glyph = "\uE790" } };
    private readonly MenuFlyoutItem _activateItem = new() { Text = "在此输入", Icon = new FontIcon { Glyph = "\uE765" } };
    private readonly MenuFlyoutItem _releaseItem = new() { Text = "停止在此输入", Icon = new FontIcon { Glyph = "\uE72E" } };
    private readonly MenuFlyoutItem _terminate = new() { Text = "结束终端", Icon = new FontIcon { Glyph = "\uE7E8" } };
    private readonly DispatcherQueueTimer _hintTimer, _findTimer;
    private OpenCodeTerminalSwitcher? _switcher;
    private string _selection = string.Empty;
    private OpenCodePersistentPtyPresentation? _model;
    private bool _hinting;
    private int _fresh = -1; // -1 while at the live bottom; otherwise line feeds received since scrolling away.
    private int[] _matches = Array.Empty<int>();
    private int _match = -1;
    internal event Action<OpenCodePersistentPtyViewContext>? TakeControlRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? ReleaseControlRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? DetachRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? ReconnectRequested;
    internal event Action<OpenCodePersistentPtyViewContext>? TerminateRequested;
    internal event Action<OpenCodePersistentPtySelection>? CopySelectionRequested;
    internal event Action? AppearanceRequested;
    internal event Action? NewTerminalRequested;
    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: next or previous terminal in the shared list.</summary>
    internal event Action<int>? StepRequested;

    /// <summary>Esc belongs to the find bar or confirmation while either is open, not to the overlay.</summary>
    internal bool ConsumesEscape => _findBar.Visibility == Visibility.Visible || _confirm.Visibility == Visibility.Visible;

    internal OpenCodePersistentPtyView()
    {
        _hintTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(2.5); _hintTimer.IsRepeating = false;
        _hintTimer.Tick += (_, _) => { _hinting = false; PaintSubtitle(); };
        _findTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _findTimer.Interval = TimeSpan.FromMilliseconds(180); _findTimer.IsRepeating = false;
        _findTimer.Tick += async (_, _) => await SearchAsync(keepPosition: true);
        _scrollBar = new OpenCodeTerminalScrollBar(Renderer);

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildHeader();
        BuildConfirm();
        Grid.SetRow(_notice, 1); Children.Add(_notice);
        _notice.ActionButton = _reconnect;
        _reconnect.Click += (_, _) => { if (_model is { Phase: OpenCodePersistentPtyPhase.Disconnected } model) ReconnectRequested?.Invoke(model.Context); };
        BuildBody();

        Renderer.SelectionChanged += selection =>
        {
            if (_model?.Context != selection.Context) return;
            _selection = selection.Text; _copy.IsEnabled = _selection.Length > 0 && CopySelectionRequested is not null;
        };
        Renderer.ReadOnlyInteraction += Hint;
        Renderer.ScrollChanged += ScrollChanged;
        Renderer.OutputLines += lines => { if (_fresh >= 0) { _fresh += lines; PaintJump(); } };
        Renderer.AppearanceChanged += PaintWell;
        Renderer.Accelerator = Accelerator;
        PreviewKeyDown += (_, args) =>
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key == VirtualKey.Escape && _confirm.Visibility == Visibility.Visible) { HideConfirm(); args.Handled = true; }
            else if (args.Key == VirtualKey.Escape && _findBar.Visibility == Visibility.Visible) { CloseFind(); args.Handled = true; }
            else if (Accelerator(args.Key, ctrl, shift)) args.Handled = true;
        };
        ActualThemeChanged += (_, _) => { PaintStatus(); PaintWell(); };
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyView");
        Render(null);
    }

    private void BuildHeader()
    {
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        _badge.Child = _badgeText;
        OpenCodeTerminalUi.Themed(_badge, "Background", "SolidBackgroundFillColorSecondaryBrush");
        _badge.BorderBrush = OpenCodeTerminalUi.Brush("ControlStrokeColorDefaultBrush");
        OpenCodeTerminalUi.Themed(_badgeText, "Foreground", "TextFillColorSecondaryBrush");
        _menu.Click += (_, _) => SetListExpanded(_listColumn.Width.Value == 0);
        AutomationProperties.SetAutomationId(_menu, "OpenCodePersistentPtyList");
        var menuHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { _menu, _badge } };
        _header.Children.Add(menuHost);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { OpenCodeQuickAccessStyle.TerminalIcon(16), _title } };
        _titles.Children.Add(titleRow); _titles.Children.Add(_subtitle);
        Grid.SetColumn(_titles, 1); _header.Children.Add(_titles);
        // Find takes the title's place in the header, so opening it never resizes (and so never rebuilds) the native terminal.
        BuildFindBar();
        Grid.SetColumn(_findBar, 1); _header.Children.Add(_findBar);
        _statusPill.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _statusDot, _status } };
        OpenCodeTerminalUi.Themed(_statusPill, "Background", "SubtleFillColorSecondaryBrush");
        var readOnlyText = new TextBlock { Text = "只读", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeTerminalUi.Themed(readOnlyText, "Foreground", "TextFillColorSecondaryBrush");
        _readOnly.Child = readOnlyText;
        OpenCodeTerminalUi.Themed(_readOnly, "BorderBrush", "ControlStrokeColorDefaultBrush");
        _jump.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new FontIcon { Glyph = "\uE74B", FontSize = 12 }, _jumpText } };
        _jump.Click += (_, _) => JumpToLatest();
        ToolTipService.SetToolTip(_jump, "跳到最新 · Ctrl+Shift+End");
        AutomationProperties.SetAutomationId(_jump, "OpenCodePersistentPtyJump");
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _jump, _statusPill, _readOnly } };
        Grid.SetColumn(chips, 2); _header.Children.Add(chips);
        var divider = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        OpenCodeTerminalUi.Themed(divider, "Background", "DividerStrokeColorDefaultBrush");
        foreach (var button in new[] { _find, _copy, _paste, _more, _back }) ToolTipService.SetToolTip(button, new ToolTip { Content = AutomationProperties.GetName(button), Placement = PlacementMode.Top });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            Children = { _find, _copy, _paste, _more, divider, _back } };
        Grid.SetColumn(tools, 3); _header.Children.Add(tools);
        Children.Add(_header);

        AutomationProperties.SetAutomationId(_back, "OpenCodePersistentPtyClose");
        AutomationProperties.SetAutomationId(_copy, "OpenCodePersistentPtyCopy");
        AutomationProperties.SetAutomationId(_more, "OpenCodePersistentPtyMore");
        AutomationProperties.SetAutomationId(_find, "OpenCodePersistentPtyFind");
        AutomationProperties.SetAutomationId(_paste, "OpenCodePersistentPtyPaste");
        _copy.IsEnabled = false;
        _copy.Click += (_, _) => CopySelection();
        _paste.Click += async (_, _) => await PasteAsync();
        _find.Click += (_, _) => { if (_findBar.Visibility == Visibility.Visible) CloseFind(); else OpenFind(); };
        _back.Click += (_, _) => { if (_model is { } model) DetachRequested?.Invoke(model.Context); };

        _createItem.KeyboardAcceleratorTextOverride = "Ctrl+Shift+T";
        _createItem.Click += (_, _) => NewTerminalRequested?.Invoke();
        _copyAllItem.Click += async (_, _) =>
        {
            if (_model is not { } model) return;
            var text = (await Renderer.ReadDocumentAsync()).TrimEnd();
            if (text.Length > 0 && _model == model) CopySelectionRequested?.Invoke(new(model.Context, text));
        };
        _appearanceItem.Click += (_, _) => AppearanceRequested?.Invoke();
        _activateItem.Click += (_, _) => { if (_model is { CanActivate: true } model) TakeControlRequested?.Invoke(model.Context); };
        _releaseItem.Click += (_, _) => { if (_model is { } model) ReleaseControlRequested?.Invoke(model.Context); };
        _terminate.KeyboardAcceleratorTextOverride = "Ctrl+Shift+W";
        _terminate.Click += (_, _) => RequestEnd();
        _moreMenu.Items.Add(_createItem); _moreMenu.Items.Add(_copyAllItem); _moreMenu.Items.Add(_appearanceItem);
        _moreMenu.Items.Add(new MenuFlyoutSeparator()); _moreMenu.Items.Add(_activateItem); _moreMenu.Items.Add(_releaseItem);
        _moreMenu.Items.Add(new MenuFlyoutSeparator()); _moreMenu.Items.Add(_terminate);
        foreach (var item in _moreMenu.Items.OfType<MenuFlyoutItem>()) item.MinHeight = 40;
        _moreMenu.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        _more.Flyout = _moreMenu;
        // Menus may cover the terminal: keep its last frame in place instead of blanking it while the HWND hides.
        IDisposable? menuAirspace = null;
        _moreMenu.Opening += (_, _) => { menuAirspace?.Dispose(); menuAirspace = Renderer.SuppressAirspace(snapshot: true); PaintTerminateItem(); };
        _moreMenu.Closed += (_, _) => { menuAirspace?.Dispose(); menuAirspace = null; };
    }

    private void BuildConfirm()
    {
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _confirm.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        OpenCodeTerminalUi.Themed(_confirm, "Background", "SystemFillColorCautionBackgroundBrush");
        var warning = new FontIcon { Glyph = "\uE7BA", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeTerminalUi.Themed(warning, "Foreground", "SystemFillColorCautionBrush");
        _confirm.Children.Add(warning);
        OpenCodeTerminalUi.Themed(_confirmDetail, "Foreground", "TextFillColorSecondaryBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _confirmTitle, _confirmDetail } };
        Grid.SetColumn(text, 1); _confirm.Children.Add(text);
        if (OpenCodeInteractionUi.TryStyle("AccentButtonStyle") is { } accent) _confirmEnd.Style = accent;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _confirmEnd, _confirmCancel } };
        Grid.SetColumn(buttons, 2); _confirm.Children.Add(buttons);
        _confirmEnd.Click += (_, _) => { HideConfirm(focusTerminal: false); if (_model is { } model) TerminateRequested?.Invoke(model.Context); };
        _confirmCancel.Click += (_, _) => HideConfirm();
        AutomationProperties.SetAutomationId(_confirm, "OpenCodePersistentPtyConfirmEnd");
        AutomationProperties.SetAutomationId(_confirmEnd, "OpenCodePersistentPtyConfirmEndButton");
        Children.Add(_confirm);
    }

    private void BuildBody()
    {
        var body = new Grid { Margin = new Thickness(0, 0, 12, 12) };
        body.ColumnDefinitions.Add(_listColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_listHost);
        OpenCodeTerminalUi.Themed(_well, "BorderBrush", "CardStrokeColorDefaultBrush");
        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OpenCodeTerminalScrollBar.GutterWidth) });
        // The HWND is inset by the scheme-coloured padding; markers and the scrollbar live in that padding only.
        Renderer.Margin = new Thickness(0, 12, 0, 12);
        Grid.SetColumn(Renderer, 1); surface.Children.Add(Renderer);
        _markers.Children.Add(_marker);
        surface.Children.Add(_markers);
        _scrollBar.Margin = new Thickness(0, 12, 0, 12);
        Grid.SetColumn(_scrollBar, 2); surface.Children.Add(_scrollBar);
        _well.Child = surface;
        AutomationProperties.SetAutomationId(_well, "OpenCodeTerminalInset");
        Grid.SetColumn(_well, 1); body.Children.Add(_well);
        Grid.SetRow(body, 2); Children.Add(body);
        PaintWell();
    }

    private void BuildFindBar()
    {
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var previous = OpenCodeTerminalUi.IconButton("\uE70E", "上一个 · Shift+Enter", 32, 32, 12);
        var next = OpenCodeTerminalUi.IconButton("\uE70D", "下一个 · Enter", 32, 32, 12);
        var close = OpenCodeTerminalUi.IconButton("\uE711", "关闭查找 · Esc", 32, 32, 12);
        ToolTipService.SetToolTip(_findCase, "区分大小写"); ToolTipService.SetToolTip(_findRegex, "正则表达式");
        AutomationProperties.SetName(_findCase, "区分大小写"); AutomationProperties.SetName(_findRegex, "正则表达式");
        AutomationProperties.SetAutomationId(_findBox, "OpenCodePersistentPtyFindBox");
        var divider = new Border { Width = 1, Height = 20, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        OpenCodeTerminalUi.Themed(divider, "Background", "DividerStrokeColorDefaultBrush");
        OpenCodeTerminalUi.Themed(_findCount, "Foreground", "TextFillColorSecondaryBrush");
        var column = 0;
        foreach (var element in new FrameworkElement[] { _findBox, _findCount, previous, next, _findCase, _findRegex, divider, close })
        { Grid.SetColumn(element, column++); _findBar.Children.Add(element); }
        _findCount.Margin = new Thickness(-52, 0, 8, 0); // Count sits inside the box's right edge, like Windows Terminal.
        Canvas.SetZIndex(_findCount, 1);
        _findBox.Padding = new Thickness(10, 5, 52, 6);
        _findBox.TextChanged += (_, _) => { _findTimer.Stop(); _findTimer.Start(); };
        _findBox.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            _ = StepMatchAsync(shift ? -1 : 1); args.Handled = true;
        };
        previous.Click += (_, _) => _ = StepMatchAsync(-1);
        next.Click += (_, _) => _ = StepMatchAsync(1);
        _findCase.Click += (_, _) => _ = SearchAsync(keepPosition: true);
        _findRegex.Click += (_, _) => _ = SearchAsync(keepPosition: true);
        close.Click += (_, _) => CloseFind();
    }

    /// <summary>Hosts the shared side list; the list instance moves between views as terminals switch.</summary>
    internal void SetSwitcher(OpenCodeTerminalSwitcher switcher)
    {
        if (_switcher is { } old) old.CountChanged -= PaintList;
        if (switcher.Host is { } previous && !ReferenceEquals(previous, _listHost)) previous.Child = null;
        _switcher = switcher; _listHost.Child = switcher; switcher.Host = _listHost;
        switcher.CountChanged += PaintList;
        Unloaded += (_, _) =>
        {
            if (IsLoaded || !ReferenceEquals(_switcher, switcher)) return;
            switcher.CountChanged -= PaintList;
            if (ReferenceEquals(switcher.Host, _listHost)) { _listHost.Child = null; switcher.Host = null; }
        };
        PaintList();
    }

    private void PaintList()
    {
        var count = _switcher?.Count ?? 0;
        var expanded = _switcher is not null && (ListExpanded ?? count >= 2);
        _listColumn.Width = new GridLength(expanded ? OpenCodeTerminalSwitcher.ExpandedWidth : 0);
        _listHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _well.Margin = new Thickness(expanded ? 0 : 12, 0, 0, 0);
        _badgeText.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _badge.Visibility = !expanded && count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _menu.Visibility = _switcher is null ? Visibility.Collapsed : Visibility.Visible;
        var label = expanded ? "隐藏终端列表 · Ctrl+Shift+L" : "显示终端列表 · Ctrl+Shift+L";
        AutomationProperties.SetName(_menu, label);
        ToolTipService.SetToolTip(_menu, new ToolTip { Content = label, Placement = PlacementMode.Top });
    }

    private void SetListExpanded(bool expanded)
    {
        if (_switcher is null) return;
        ListExpanded = expanded; // One column change: the terminal is re-measured once after layout (renderer debounce).
        PaintList();
    }

    internal void Render(OpenCodePersistentPtyPresentation? model)
    {
        if (_model?.Context != model?.Context || model?.Phase is OpenCodePersistentPtyPhase.Connecting or OpenCodePersistentPtyPhase.Replaying)
        { _selection = string.Empty; _copy.IsEnabled = false; }
        if (_model?.Context != model?.Context || model?.Writable != true) Renderer.SuspendInput();
        if (_model?.Context.PtyId != model?.Context.PtyId) { HideConfirm(focusTerminal: false); _fresh = -1; _jump.Visibility = Visibility.Collapsed; }
        _model = model; Visibility = model is null ? Visibility.Collapsed : Visibility.Visible;
        Renderer.SetActivatable(model?.CanActivate == true);
        _title.Text = model?.Title ?? string.Empty;
        ToolTipService.SetToolTip(_title, model is null ? null : string.Join("\n", new[] { model.Title, model.Directory, model.ControllerLabel }.WherePresent()));
        _status.Text = model?.Status ?? string.Empty;
        AutomationProperties.SetName(_statusPill, _status.Text);
        _readOnly.Visibility = model is not null && !model.Writable ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_readOnly, model?.CanActivate == true ? "双击终端即可在此输入" : null);
        var notice = model?.ControlConflict == true ? string.Empty : model?.Notice ?? string.Empty;
        _notice.Message = notice;
        _notice.Severity = model?.Phase is OpenCodePersistentPtyPhase.Unavailable or OpenCodePersistentPtyPhase.Disconnected ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        _notice.IsOpen = notice.Length > 0;
        _reconnect.Visibility = model?.Phase == OpenCodePersistentPtyPhase.Disconnected && ReconnectRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        _back.IsEnabled = model is not null && DetachRequested is not null;
        _paste.IsEnabled = model?.Writable == true;
        _find.IsEnabled = model is not null;
        PaintSubtitle();
        PaintStatus();
        PaintTerminateItem();
    }

    private void PaintSubtitle()
    {
        var model = _model;
        string text;
        if (model is null) text = string.Empty;
        else if (model.ControlConflict) text = "其他客户端正在输入 · 双击终端即可接管，对方转为只读";
        else if (model.CanActivate) text = "双击终端即可在此输入" + (model.Directory is { Length: > 0 } cwd ? " · " + cwd : string.Empty);
        else text = string.Join(" · ", new[] { model.Source, model.Directory }.WherePresent());
        _subtitle.Text = text;
        ToolTipService.SetToolTip(_subtitle, text.Length == 0 ? null : text);
        // A click or keystroke on a read-only terminal briefly highlights how to type here.
        OpenCodeTerminalUi.Themed(_subtitle, "Foreground", _hinting && model?.CanActivate == true ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
    }

    private void Hint()
    {
        if (_model?.CanActivate != true) return;
        _hinting = true; PaintSubtitle();
        _hintTimer.Stop(); _hintTimer.Start();
    }

    private void PaintStatus()
    {
        if (Application.Current is null) return;
        OpenCodeTerminalUi.PaintDot(_statusDot, _model?.TerminalState ?? OpenCodeTerminalState.Disconnected);
        OpenCodeTerminalUi.Themed(_status, "Foreground", "TextFillColorPrimaryBrush");
        OpenCodeTerminalUi.Themed(_jump, "BorderBrush", "AccentFillColorDefaultBrush");
        OpenCodeTerminalUi.Themed(_jumpText, "Foreground", "AccentTextFillColorPrimaryBrush");
        _jump.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _marker.Background = OpenCodeTerminalUi.Brush("AccentFillColorDefaultBrush");
        _badge.BorderBrush = OpenCodeTerminalUi.Brush("ControlStrokeColorDefaultBrush");
    }

    private void PaintTerminateItem()
    {
        var model = _model;
        var exited = model?.Phase == OpenCodePersistentPtyPhase.Exited;
        _terminate.Text = exited ? "移除终端" : "结束终端";
        _terminate.Visibility = model is not null && TerminateRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        _terminate.Foreground = exited ? null : OpenCodeTerminalUi.Brush("SystemFillColorCriticalBrush");
        _createItem.Visibility = NewTerminalRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        _appearanceItem.Visibility = AppearanceRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        _activateItem.Visibility = model?.CanActivate == true && TakeControlRequested is not null ? Visibility.Visible : Visibility.Collapsed;
        _activateItem.Text = model?.ControlConflict == true ? "接管并在此输入" : "在此输入";
        _releaseItem.Visibility = model is { Writable: true, Role: OpenCodePersistentPtyRole.Controller } && ReleaseControlRequested is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PaintWell()
    {
        var (background, _) = Renderer.Appearance.SurfaceColors(ActualTheme == ElementTheme.Light);
        _well.Background = new SolidColorBrush(background);
    }

    /// <summary>End a running terminal after the in-header confirmation; an exited one is removed at once.</summary>
    internal void RequestEnd()
    {
        if (_model is not { } model || TerminateRequested is null) return;
        if (model.Phase == OpenCodePersistentPtyPhase.Exited) { TerminateRequested.Invoke(model.Context); return; }
        _confirmTitle.Text = $"结束「{model.Title}」？";
        _confirmDetail.Text = (model.ControllerLabel is { Length: > 0 } process ? process + " 仍在运行，" : "终端仍在运行，")
            + "结束后无法恢复。只想隐藏窗口？点 × 或按 Ctrl+`，终端会继续运行。";
        _confirm.Visibility = Visibility.Visible; _header.Visibility = Visibility.Collapsed;
        _confirmEnd.Focus(FocusState.Programmatic);
    }

    private void HideConfirm(bool focusTerminal = true)
    {
        if (_confirm.Visibility != Visibility.Visible) return;
        _confirm.Visibility = Visibility.Collapsed; _header.Visibility = Visibility.Visible;
        if (focusTerminal) Renderer.FocusTerminal();
    }

    private bool Accelerator(VirtualKey key, bool ctrl, bool shift)
    {
        if (_model is null || !ctrl) return false;
        switch (key)
        {
            case (VirtualKey)192 when !shift: if (DetachRequested is not null) DetachRequested(_model.Context); return true; // Ctrl+`
            case VirtualKey.Tab: StepRequested?.Invoke(shift ? -1 : 1); return StepRequested is not null;
        }
        if (!shift) return false;
        switch (key)
        {
            case VirtualKey.F: if (_findBar.Visibility == Visibility.Visible) _findBox.Focus(FocusState.Keyboard); else OpenFind(); return true;
            case VirtualKey.L: SetListExpanded(_listColumn.Width.Value == 0); return _switcher is not null;
            case VirtualKey.End: JumpToLatest(); return true;
            case VirtualKey.C: CopySelection(); return true;
            case VirtualKey.V: _ = PasteAsync(); return true;
            case VirtualKey.W: RequestEnd(); return TerminateRequested is not null;
            case VirtualKey.T: NewTerminalRequested?.Invoke(); return NewTerminalRequested is not null;
        }
        return false;
    }

    private void CopySelection()
    {
        if (_model is { } model && _selection.Length > 0) CopySelectionRequested?.Invoke(new(model.Context, _selection));
    }

    private async Task PasteAsync()
    {
        if (_model?.Writable != true) return;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (_model?.Writable == true) Renderer.Paste(text);
        }
        catch (Exception) { /* Clipboard unavailable: nothing is sent. */ }
    }

    private void ScrollChanged(TerminalScrollState state)
    {
        _scrollBar.Update(state);
        if (state.AtBottom || state.MaximumTop == 0) _fresh = -1;
        else if (_fresh < 0) _fresh = 0;
        PaintJump();
        PlaceMarker();
    }

    private void PaintJump()
    {
        _jump.Visibility = _fresh >= 0 ? Visibility.Visible : Visibility.Collapsed;
        _jumpText.Text = _fresh > 0 ? _fresh + " 行新内容" : "跳到最新";
    }

    private void JumpToLatest() { Renderer.ScrollToBottom(); }

    private void OpenFind()
    {
        if (_model is null) return;
        _findBar.Visibility = Visibility.Visible; _titles.Visibility = Visibility.Collapsed;
        _findBox.Focus(FocusState.Keyboard);
        _findBox.SelectAll();
        if (_findBox.Text.Length > 0) _ = SearchAsync(keepPosition: true);
    }

    private void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed; _titles.Visibility = Visibility.Visible;
        _matches = Array.Empty<int>(); _match = -1; _marker.Visibility = Visibility.Collapsed;
        Renderer.FocusTerminal();
    }

    /// <summary>
    /// The native control has no search/highlight API: the document is read through UI Automation, the view scrolls the
    /// matching row into view and an accent marker is drawn beside that row in the left padding (outside the HWND).
    /// </summary>
    private async Task SearchAsync(bool keepPosition)
    {
        var query = _findBox.Text;
        if (query.Length == 0) { _matches = Array.Empty<int>(); _match = -1; _findCount.Text = string.Empty; PlaceMarker(); return; }
        Regex pattern;
        try
        {
            var options = RegexOptions.CultureInvariant | (_findCase.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase);
            pattern = new Regex(_findRegex.IsChecked == true ? query : Regex.Escape(query), options, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException) { _findCount.Text = "无效"; _matches = Array.Empty<int>(); _match = -1; PlaceMarker(); return; }
        string document;
        try { document = await Renderer.ReadDocumentAsync(); }
        catch (Exception) { _findCount.Text = string.Empty; return; }
        if (query != _findBox.Text) return;
        var lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var matches = new List<int>();
        try { for (var i = 0; i < lines.Length; i++) if (pattern.IsMatch(lines[i])) matches.Add(i); }
        catch (RegexMatchTimeoutException) { }
        var previous = _match >= 0 && _match < _matches.Length ? _matches[_match] : -1;
        _matches = matches.ToArray();
        if (_matches.Length == 0) { _match = -1; _findCount.Text = "0 / 0"; PlaceMarker(); return; }
        var top = Renderer.ScrollState.ViewTop;
        _match = keepPosition && previous >= 0 ? Math.Max(0, Array.FindIndex(_matches, line => line >= previous))
            : Math.Max(0, Array.FindIndex(_matches, line => line >= top));
        if (Array.FindIndex(_matches, line => line >= (keepPosition && previous >= 0 ? previous : top)) < 0) _match = _matches.Length - 1;
        ShowMatch();
    }

    private async Task StepMatchAsync(int direction)
    {
        if (_matches.Length == 0) { await SearchAsync(keepPosition: false); return; }
        _match = ((_match + direction) % _matches.Length + _matches.Length) % _matches.Length;
        ShowMatch();
    }

    private void ShowMatch()
    {
        _findCount.Text = $"{_match + 1} / {_matches.Length}";
        var line = _matches[_match];
        var state = Renderer.ScrollState;
        if (line < state.ViewTop || line >= state.ViewTop + state.ViewHeight) Renderer.ScrollTo(Math.Max(0, line - state.ViewHeight / 3));
        PlaceMarker();
    }

    private void PlaceMarker()
    {
        if (_match < 0 || _match >= _matches.Length || _findBar.Visibility != Visibility.Visible) { _marker.Visibility = Visibility.Collapsed; return; }
        var state = Renderer.ScrollState; var row = _matches[_match] - state.ViewTop;
        var height = Renderer.RowHeight;
        if (row < 0 || row >= state.ViewHeight || height <= 0) { _marker.Visibility = Visibility.Collapsed; return; }
        _marker.Height = Math.Max(4, height - 2);
        Canvas.SetLeft(_marker, 6); Canvas.SetTop(_marker, 12 + row * height + 1);
        _marker.Visibility = Visibility.Visible;
    }
}

/// <summary>
/// Thin scrollbar in the well's right padding (never over the HWND): 3px at rest, 6px with a 10% track on hover.
/// Drag, track click (page) and wheel all scroll local history only.
/// </summary>
internal sealed class OpenCodeTerminalScrollBar : Grid
{
    internal const double GutterWidth = 40;
    private readonly OpenCodePersistentPtyRenderer _renderer;
    private readonly Border _track = new() { Width = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0 };
    private readonly Border _thumb = new() { Width = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    private TerminalScrollState _state;
    private bool _hover, _dragging;
    private double _dragOffset;

    internal OpenCodeTerminalScrollBar(OpenCodePersistentPtyRenderer renderer)
    {
        _renderer = renderer; Width = GutterWidth;
        ManipulationMode = ManipulationModes.None; // Keep touch drags here, not in an ancestor ScrollViewer.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Children.Add(_track); Children.Add(_thumb);
        AutomationProperties.SetName(this, "终端滚动条");
        AutomationProperties.SetAutomationId(this, "OpenCodeTerminalScrollBar");
        PointerEntered += (_, _) => { _hover = true; Paint(); };
        PointerExited += (_, _) => { _hover = false; Paint(); };
        PointerPressed += (_, args) =>
        {
            if (_state.MaximumTop <= 0) return;
            var y = args.GetCurrentPoint(this).Position.Y;
            if (y >= _thumb.Margin.Top && y <= _thumb.Margin.Top + _thumb.Height) { _dragging = true; _dragOffset = y - _thumb.Margin.Top; CapturePointer(args.Pointer); }
            else if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            {
                _dragging = true; _dragOffset = _thumb.Height / 2; CapturePointer(args.Pointer);
                DragTo(y);
            }
            else _renderer.ScrollTo(_state.ViewTop + (y < _thumb.Margin.Top ? -1 : 1) * Math.Max(1, _state.ViewHeight - 1));
            Paint();
            args.Handled = true;
        };
        PointerMoved += (_, args) =>
        {
            if (!_dragging) return;
            DragTo(args.GetCurrentPoint(this).Position.Y);
            args.Handled = true;
        };
        PointerReleased += (_, args) => { _dragging = false; ReleasePointerCapture(args.Pointer); Paint(); args.Handled = true; };
        PointerCanceled += (_, args) => { _dragging = false; ReleasePointerCapture(args.Pointer); Paint(); };
        PointerCaptureLost += (_, _) => { _dragging = false; Paint(); };
        PointerWheelChanged += (_, args) =>
        {
            var delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
            if (delta != 0) _renderer.ScrollTo(_state.ViewTop - Math.Sign(delta) * 3 * Math.Max(1, Math.Abs(delta) / 120));
            args.Handled = true;
        };
        SizeChanged += (_, _) => Layout();
        ActualThemeChanged += (_, _) => Paint();
        Loaded += (_, _) => Paint();
    }

    internal void Update(TerminalScrollState state) { _state = state; Layout(); }

    private void DragTo(double y)
    {
        var free = ActualHeight - _thumb.Height;
        if (free > 0) _renderer.ScrollTo((int)Math.Round(Math.Clamp((y - _dragOffset) / free, 0, 1) * _state.MaximumTop));
    }

    private void Layout()
    {
        // A collapsed element measures to zero. Show from buffer state first, then
        // SizeChanged supplies geometry; testing ActualHeight here kept it collapsed forever.
        var visible = _state.MaximumTop > 0 && _state.BufferSize > 0;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || ActualHeight <= 0) return;
        var height = Math.Max(24, ActualHeight * _state.ViewHeight / Math.Max(1, _state.BufferSize));
        _thumb.Height = Math.Min(ActualHeight, height);
        _thumb.Margin = new Thickness(0, (ActualHeight - _thumb.Height) * _state.ViewTop / Math.Max(1, _state.MaximumTop), 0, 0);
        Paint();
    }

    private void Paint()
    {
        var wide = _hover || _dragging;
        _thumb.Width = wide ? 6 : 3;
        _thumb.CornerRadius = new CornerRadius(wide ? 3 : 1.5);
        _track.Opacity = wide ? 1 : 0;
        var (_, foreground) = _renderer.Appearance.SurfaceColors(ActualTheme == ElementTheme.Light);
        _thumb.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(wide ? 0x99 : 0x66), foreground.R, foreground.G, foreground.B));
        _track.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, foreground.R, foreground.G, foreground.B));
    }
}

internal static class OpenCodePersistentPtyViewText
{
    internal static IEnumerable<string> WherePresent(this string?[] values)
    {
        foreach (var value in values) if (!string.IsNullOrEmpty(value)) yield return value;
    }
}
