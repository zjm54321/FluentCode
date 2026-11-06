using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// The turn navigator's touch form: a finger tap on the 12px rail expands it into a panel of 44px rows
/// (turn number + one-line question, the turn in view marked with the accent pill) beside the rail, inside the
/// timeline. Tap a row to jump, or drag (from the rail or anywhere in the list): the row under the finger is
/// highlighted live, the header repeats it above the finger, and lifting jumps once. Geometry and hit testing
/// are pure functions in <see cref="OpenCodeTurnOutline"/>.
/// </summary>
/// <remarks>
/// Gestures: the panel and the rail sit outside the transcript ScrollView (popup / sibling), capture the
/// pointer and use <see cref="ManipulationModes.None"/>, so a drag here never pans the transcript and a pan that
/// starts on the transcript never reaches the navigator. A drag is a selection, not a list scroll: holding near
/// or past the list's top/bottom edge auto-scrolls (a timer that runs only during such a drag). The transcript
/// does not jump while scrubbing — one jump on release keeps a Surface Go-class device from re-realising
/// transcript rows on every finger move. Idle cost: nothing runs while closed.
/// </remarks>
internal sealed partial class OpenCodeTurnTouchPanel
{
    private const double RowInset = 4;
    private readonly FrameworkElement _owner;
    private readonly Popup _popup = new() { ShouldConstrainToRootBounds = true, IsLightDismissEnabled = false };
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly TextBlock _caption = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _headline = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 2, 0, 0) };
    private readonly Canvas _list = new() { ManipulationMode = ManipulationModes.None, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly RectangleGeometry _clip = new();
    private readonly Rectangle _thumb = new() { Width = 3, RadiusX = 1.5, RadiusY = 1.5, IsHitTestVisible = false, Opacity = 0.6 };
    private readonly List<Row> _rows = new();
    private IReadOnlyList<OpenCodeTurnOutlineItem> _turns = Array.Empty<OpenCodeTurnOutlineItem>();
    private int _version;
    private int _current = -1;
    private int _highlight = -1;
    private double _offset;
    private OpenCodeTurnOutline.TouchPanelLayout _layout;
    private Point _listOrigin; // list top-left in XamlRoot coordinates
    // One tracked contact at a time (a second finger is ignored).
    private uint? _pointer;
    private UIElement? _captor;
    private Point _press;
    private bool _dragging;
    private bool _cancelled;
    private bool _toggleOnTap;
    private double _lastY;
    private double _velocity;
    private DispatcherQueueTimer? _autoScroll;
    private readonly Stopwatch _clock = new();
    private UIElement? _root;
    private FrameworkElement? _bounds;
    private readonly PointerEventHandler _rootPressed;
    private readonly KeyEventHandler _rootKey;

    internal event Action<int>? Invoked;
    /// <summary>Open/close and highlight changes (the rail mirrors the targeted turn).</summary>
    internal event Action? Changed;
    internal bool IsOpen => _popup.IsOpen;
    internal int Highlight => _highlight;
    internal Popup Popup => _popup;

    internal OpenCodeTurnTouchPanel(FrameworkElement owner)
    {
        _owner = owner;
        _rootPressed = OnRootPressed;
        _rootKey = OnRootKey;
        var header = new StackPanel { Height = OpenCodeTurnOutline.TouchHeaderHeight, Padding = new Thickness(14, 10, 14, 6) };
        OpenCodeTurnNavigator.Apply(_caption, OpenCodeTurnNavigator.TextStyle("TextFillColorSecondaryBrush", "CaptionTextBlockStyle"));
        OpenCodeTurnNavigator.Apply(_headline, OpenCodeTurnNavigator.TextStyle("TextFillColorPrimaryBrush", "BodyStrongTextBlockStyle"));
        header.Children.Add(_caption);
        header.Children.Add(_headline);
        // The header announces the target itself through the rows' names; keep it out of the Narrator scan order.
        AutomationProperties.SetAccessibilityView(header, AccessibilityView.Raw);
        var divider = new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        OpenCodeTurnNavigator.Apply(divider, OpenCodeTurnNavigator.FillStyle("DividerStrokeColorDefaultBrush"));
        _list.Clip = _clip;
        OpenCodeTurnNavigator.Apply(_thumb, OpenCodeTurnNavigator.FillStyle("ControlStrongFillColorDefaultBrush"));
        _list.Children.Add(_thumb);
        Canvas.SetZIndex(_thumb, 1);
        AutomationProperties.SetName(_list, "对话轮次");
        AutomationProperties.SetAutomationId(_list, "OpenCodeTurnNavigatorTouchList");
        var body = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition { Height = GridLength.Auto } } };
        body.Children.Add(header);
        body.Children.Add(divider);
        Grid.SetRow(_list, 1);
        body.Children.Add(_list);
        _frame.Child = body;
        OpenCodeTurnNavigator.Apply(_frame, OpenCodeTurnNavigator.BorderStyle("SolidBackgroundFillColorQuarternaryBrush", "SurfaceStrokeColorFlyoutBrush"));
        _frame.Shadow = new ThemeShadow();
        _frame.Translation = new Vector3(0, 0, 32);
        AutomationProperties.SetAutomationId(_frame, "OpenCodeTurnNavigatorTouchPanel");
        _popup.Child = _frame;
        FluentCode.Motion.FluentMotion.BindTransitions(_popup, transitions => _popup.ChildTransitions = transitions,
            () => new TransitionCollection { new PopupThemeTransition { FromHorizontalOffset = -12, FromVerticalOffset = 0 } });
        _popup.Closed += (_, _) => Detach();

        _list.PointerPressed += OnListPressed;
        _list.PointerMoved += (_, e) => Track(e);
        _list.PointerReleased += (_, e) => End(e);
        _list.PointerCanceled += (_, e) => Cancel(e);
        _list.PointerCaptureLost += (_, e) => Cancel(e);
        _list.PointerWheelChanged += OnListWheel;
        _frame.PointerPressed += (_, e) => e.Handled = true; // header presses neither dismiss nor reach anything else
    }

    // ---- Open / close -----------------------------------------------------------------------------------

    /// <param name="bounds">Timeline region the panel must stay inside (the composer is outside it).</param>
    /// <param name="boundsTop">Top of the navigator's row inside <paramref name="bounds"/>.</param>
    /// <param name="anchorRootY">Finger y (XamlRoot coordinates) the current turn's row is aligned with.</param>
    internal void Open(IReadOnlyList<OpenCodeTurnOutlineItem> turns, int current, FrameworkElement bounds, double boundsTop, double boundsHeight, double anchorRootY)
    {
        if (turns.Count == 0 || _owner.XamlRoot is null) return;
        _turns = turns;
        _version++;
        _current = Math.Clamp(current < 0 ? turns.Count - 1 : current, 0, turns.Count - 1);
        _highlight = -1;
        var boundsOrigin = bounds.TransformToVisual(null).TransformPoint(new Point(0, boundsTop));
        var owner = _owner.TransformToVisual(bounds).TransformPoint(default);
        _layout = OpenCodeTurnOutline.TouchPanel(bounds.ActualWidth, boundsHeight, owner.X + _owner.ActualWidth,
            anchorRootY - boundsOrigin.Y, turns.Count, _current);
        _offset = _layout.Offset;
        _listOrigin = new Point(boundsOrigin.X + _layout.Left + 1, boundsOrigin.Y + _layout.Top + 1 + OpenCodeTurnOutline.TouchHeaderHeight);
        _frame.Width = _layout.Width;
        _list.Width = _layout.Width - 2;
        _list.Height = _layout.ListHeight;
        _clip.Rect = new Rect(0, 0, _list.Width, _layout.ListHeight);
        Canvas.SetLeft(_thumb, _list.Width - 5);
        _popup.HorizontalOffset = _layout.Left - owner.X;
        _popup.VerticalOffset = _layout.Top - (owner.Y - boundsTop);
        _popup.XamlRoot = _owner.XamlRoot;
        _popup.RequestedTheme = _owner.ActualTheme;
        AutomationProperties.SetName(_frame, $"对话导航，共 {turns.Count} 轮。点按一轮跳转，或按住拖动选择");
        Layout();
        UpdateHeader();
        _popup.IsOpen = true;
        Attach(bounds);
        Changed?.Invoke();
    }

    internal void Close()
    {
        if (_pointer is not null) EndTracking();
        if (_popup.IsOpen) _popup.IsOpen = false;
        Detach();
        if (_highlight != -1) _highlight = -1;
        Changed?.Invoke();
    }

    /// <summary>The transcript's turn list changed while open: rebind; a different session closes.</summary>
    internal void Update(IReadOnlyList<OpenCodeTurnOutlineItem> turns, int current)
    {
        if (!IsOpen) return;
        if (turns.Count < 2 || _turns.Count == 0 || turns[0].Key != _turns[0].Key) { Close(); return; }
        _turns = turns;
        _version++;
        _current = Math.Clamp(current < 0 ? turns.Count - 1 : current, 0, turns.Count - 1);
        if (_highlight >= turns.Count) _highlight = turns.Count - 1;
        _offset = OpenCodeTurnOutline.ClampTouchOffset(_offset, turns.Count, _layout.ListHeight);
        Layout();
        UpdateHeader();
    }

    internal void SetTheme(ElementTheme theme) => _popup.RequestedTheme = theme;

    private void Attach(FrameworkElement bounds)
    {
        Detach();
        if (_owner.XamlRoot?.Content is UIElement root)
        {
            _root = root;
            // handledEventsToo: the outside tap closes the panel and still does what it does (no swallowed tap).
            root.AddHandler(UIElement.PointerPressedEvent, _rootPressed, true);
            root.AddHandler(UIElement.KeyDownEvent, _rootKey, true);
        }
        _bounds = bounds;
        bounds.SizeChanged += OnBoundsSizeChanged;
    }

    private void Detach()
    {
        if (_root is { } root)
        {
            root.RemoveHandler(UIElement.PointerPressedEvent, _rootPressed);
            root.RemoveHandler(UIElement.KeyDownEvent, _rootKey);
            _root = null;
        }
        if (_bounds is { } bounds) { bounds.SizeChanged -= OnBoundsSizeChanged; _bounds = null; }
        _autoScroll?.Stop();
    }

    private void OnBoundsSizeChanged(object sender, SizeChangedEventArgs e) => Close(); // geometry is stale (resize / rotation)

    private void OnRootPressed(object sender, PointerRoutedEventArgs e)
    {
        // Presses on the rail are the rail's (toggle / drag); the panel lives in a popup and never routes here.
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, _owner)) return;
        Close();
    }

    private void OnRootKey(object sender, KeyRoutedEventArgs e)
    {
        // Only an unclaimed Esc: Esc in the composer (or any control that uses it) keeps its own meaning.
        if (e.Key == VirtualKey.Escape && !e.Handled) { Close(); e.Handled = true; }
    }

    // ---- Keyboard (when the rail has focus and the panel is open) --------------------------------------

    internal bool HandleKey(VirtualKey key)
    {
        if (!IsOpen) return false;
        var from = _highlight >= 0 ? _highlight : _current;
        int? next = key switch
        {
            VirtualKey.Up or VirtualKey.Left => from - 1,
            VirtualKey.Down or VirtualKey.Right => from + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => _turns.Count - 1,
            _ => null,
        };
        if (next is { } turn)
        {
            SetHighlight(Math.Clamp(turn, 0, _turns.Count - 1), reveal: true);
            return true;
        }
        if (key is VirtualKey.Enter or VirtualKey.Space) { Invoke(from); return true; }
        if (key == VirtualKey.Escape) { Close(); return true; }
        return false;
    }

    // ---- Pointer tracking ---------------------------------------------------------------------------------

    /// <summary>
    /// A finger pressed on the rail (already captured by it). Opening is done by the caller; a press that never
    /// becomes a drag only toggles (<paramref name="toggleOnTap"/>: the panel was already open → close), so the
    /// first tap can never jump.
    /// </summary>
    internal void BeginFromRail(PointerRoutedEventArgs e, UIElement rail, bool toggleOnTap)
    {
        if (!IsOpen) return;
        Begin(e, rail, toggleOnTap);
    }

    internal bool IsTracking(PointerRoutedEventArgs e) => _pointer == e.Pointer.PointerId;

    private void OnListPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_pointer is not null || !_list.CapturePointer(e.Pointer)) return;
        Begin(e, _list, toggleOnTap: false);
        // Pressed feedback at once: the row under the finger is the target of a tap.
        SetHighlight(OpenCodeTurnOutline.TouchRowAt(ListPoint(e).Y, _offset, _turns.Count), reveal: false);
    }

    private void Begin(PointerRoutedEventArgs e, UIElement captor, bool toggleOnTap)
    {
        _pointer = e.Pointer.PointerId;
        _captor = captor;
        _press = ListPoint(e);
        _lastY = _press.Y;
        _dragging = false;
        _cancelled = false;
        _toggleOnTap = toggleOnTap;
    }

    internal void Track(PointerRoutedEventArgs e)
    {
        if (!IsTracking(e)) return;
        e.Handled = true;
        var p = ListPoint(e);
        if (!_dragging && !OpenCodeTurnOutline.IsTouchDrag(p.X - _press.X, p.Y - _press.Y)) return;
        _dragging = true;
        _lastY = p.Y;
        _cancelled = OpenCodeTurnOutline.TouchReleaseCancels(p.X, _list.Width);
        SetHighlight(_cancelled ? -1 : OpenCodeTurnOutline.TouchRowAt(p.Y, _offset, _turns.Count), reveal: false);
        SetVelocity(_cancelled ? 0 : OpenCodeTurnOutline.TouchAutoScrollVelocity(p.Y, _layout.ListHeight));
    }

    internal void End(PointerRoutedEventArgs e)
    {
        if (!IsTracking(e)) return;
        e.Handled = true;
        var captor = _captor;
        var tapFromRail = !_dragging && !ReferenceEquals(captor, _list);
        var target = _cancelled ? -1 : _highlight;
        EndTracking();
        captor?.ReleasePointerCapture(e.Pointer);
        if (tapFromRail) { if (_toggleOnTap) Close(); return; } // first tap opens only; tapping the rail again closes
        if (target >= 0) Invoke(target);
        else SetHighlight(-1, reveal: false);
    }

    internal void Cancel(PointerRoutedEventArgs e)
    {
        if (!IsTracking(e)) return;
        EndTracking();
        SetHighlight(-1, reveal: false);
    }

    private void EndTracking()
    {
        _pointer = null;
        _captor = null;
        _dragging = false;
        SetVelocity(0);
    }

    private Point ListPoint(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(null).Position;
        return new Point(p.X - _listOrigin.X, p.Y - _listOrigin.Y);
    }

    private void OnListWheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var delta = e.GetCurrentPoint(_list).Properties.MouseWheelDelta;
        ScrollTo(_offset - delta / 120.0 * OpenCodeTurnOutline.TouchRowHeight * 2);
    }

    // ---- Edge auto-scroll (runs only while a drag holds in an edge band) -------------------------------

    private void SetVelocity(double velocity)
    {
        _velocity = velocity;
        if (velocity == 0) { _autoScroll?.Stop(); return; }
        if (_autoScroll is null)
        {
            _autoScroll = _owner.DispatcherQueue.CreateTimer();
            _autoScroll.Interval = TimeSpan.FromMilliseconds(16);
            _autoScroll.IsRepeating = true;
            _autoScroll.Tick += (_, _) => AutoScrollTick();
        }
        if (!_autoScroll.IsRunning) { _clock.Restart(); _autoScroll.Start(); }
    }

    private void AutoScrollTick()
    {
        var seconds = Math.Min(0.05, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        if (_velocity == 0 || _pointer is null) { _autoScroll?.Stop(); return; }
        var before = _offset;
        ScrollTo(_offset + _velocity * seconds);
        if (_offset == before) { _autoScroll?.Stop(); return; } // reached the first/last turn
        SetHighlight(OpenCodeTurnOutline.TouchRowAt(_lastY, _offset, _turns.Count), reveal: false);
    }

    private void ScrollTo(double offset)
    {
        offset = OpenCodeTurnOutline.ClampTouchOffset(offset, _turns.Count, _layout.ListHeight);
        if (offset == _offset) return;
        _offset = offset;
        Layout();
    }

    // ---- Rows (virtualised pool) -----------------------------------------------------------------------

    private void SetHighlight(int turn, bool reveal)
    {
        if (turn >= _turns.Count) turn = _turns.Count - 1;
        if (reveal && turn >= 0) ScrollTo(OpenCodeTurnOutline.TouchReveal(turn, _offset, _turns.Count, _layout.ListHeight));
        if (turn == _highlight) return;
        _highlight = turn;
        foreach (var row in _rows) if (row.Visibility == Visibility.Visible) row.Paint(_current, _highlight);
        UpdateHeader();
        Changed?.Invoke();
    }

    private void UpdateHeader()
    {
        var turn = _highlight >= 0 ? _highlight : _current;
        if (turn < 0 || turn >= _turns.Count) return;
        _caption.Text = turn == _current ? $"第 {turn + 1} 轮 · 共 {_turns.Count} 轮 · 当前" : $"第 {turn + 1} 轮 · 共 {_turns.Count} 轮";
        _headline.Text = OpenCodeTurnOutline.TouchRowLabel(_turns[turn].Prompt, 240);
    }

    private void Layout()
    {
        var (first, count) = OpenCodeTurnOutline.TouchVisibleRange(_offset, _layout.ListHeight, _turns.Count);
        while (_rows.Count < count)
        {
            var row = new Row(this);
            _rows.Add(row);
            _list.Children.Insert(_list.Children.Count - 1, row);
        }
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (i >= count) { row.Visibility = Visibility.Collapsed; continue; }
            var turn = first + i;
            row.Visibility = Visibility.Visible;
            row.Width = _list.Width - 2 * RowInset;
            Canvas.SetLeft(row, RowInset);
            Canvas.SetTop(row, turn * OpenCodeTurnOutline.TouchRowHeight - _offset);
            row.Bind(turn, _turns[turn].Prompt, _turns.Count, _version);
            row.Paint(_current, _highlight);
        }
        var total = _turns.Count * OpenCodeTurnOutline.TouchRowHeight;
        if (total > _layout.ListHeight + 0.5)
        {
            var height = Math.Max(24, _layout.ListHeight * _layout.ListHeight / total);
            _thumb.Height = height;
            Canvas.SetTop(_thumb, _offset / (total - _layout.ListHeight) * (_layout.ListHeight - height));
            _thumb.Visibility = Visibility.Visible;
        }
        else _thumb.Visibility = Visibility.Collapsed;
    }

    private void Invoke(int turn)
    {
        if (turn < 0 || turn >= _turns.Count) return;
        Close();
        Invoked?.Invoke(turn);
    }

    /// <summary>One 44px row: accent pill (turn in view), turn number, one-line question.</summary>
    private sealed class Row : Grid
    {
        private readonly OpenCodeTurnTouchPanel _panel;
        private readonly Rectangle _pill = new() { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        private readonly TextBlock _number = new() { Width = 30, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0) };
        private readonly TextBlock _text = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center };
        private int _turn = -1;
        private int _version = -1;
        private string _state = string.Empty;

        internal Row(OpenCodeTurnTouchPanel panel)
        {
            _panel = panel;
            Height = OpenCodeTurnOutline.TouchRowHeight;
            CornerRadius = new CornerRadius(4);
            Padding = new Thickness(0, 0, 12, 0);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Children.Add(_pill);
            SetColumn(_number, 1);
            Children.Add(_number);
            SetColumn(_text, 2);
            Children.Add(_text);
            OpenCodeTurnNavigator.Apply(_pill, OpenCodeTurnNavigator.FillStyle("AccentFillColorDefaultBrush"));
        }

        internal void Bind(int turn, string prompt, int turns, int version)
        {
            if (turn == _turn && version == _version) return;
            _turn = turn;
            _version = version;
            _number.Text = (turn + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _text.Text = OpenCodeTurnOutline.TouchRowLabel(prompt);
            AutomationProperties.SetName(this, $"第 {turn + 1} 轮，共 {turns} 轮：{_text.Text}");
            _state = string.Empty;
        }

        internal void Paint(int current, int highlight)
        {
            var isCurrent = _turn == current;
            var state = (isCurrent ? "c" : "-") + (_turn == highlight ? "h" : "-");
            if (state == _state) return;
            _state = state;
            _pill.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
            OpenCodeTurnNavigator.Apply(this, OpenCodeTurnNavigator.PanelStyle(_turn == highlight ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush"));
            OpenCodeTurnNavigator.Apply(_number, OpenCodeTurnNavigator.TextStyle(isCurrent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush", "BodyTextBlockStyle"));
            OpenCodeTurnNavigator.Apply(_text, OpenCodeTurnNavigator.TextStyle("TextFillColorPrimaryBrush", isCurrent ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"));
            AutomationProperties.SetItemStatus(this, isCurrent ? "当前" : string.Empty);
        }

        internal void InvokeFromAutomation() => _panel.Invoke(_turn);

        protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);
    }

    /// <summary>Narrator / UI Automation: each row is an invokable list item (double-tap in Narrator touch mode jumps).</summary>
    private sealed partial class RowPeer : FrameworkElementAutomationPeer, IInvokeProvider
    {
        private readonly Row _row;

        internal RowPeer(Row row) : base(row) => _row = row;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "OpenCodeTurnNavigatorRow";
        protected override object GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        public void Invoke() => _row.InvokeFromAutomation();
    }
}
