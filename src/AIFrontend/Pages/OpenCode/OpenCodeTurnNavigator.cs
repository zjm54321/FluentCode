using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using VirtualKey = Windows.System.VirtualKey;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Left-gutter turn navigator in the Fluent pips idiom: a column of identical short marks (one per loaded
/// user turn, capped and proportionally mapped) with an accent pill on the turn in view that glides between
/// marks (composition translation). Hover/focus shows a card with the turn's question and its reply text;
/// click, Enter or a second tap jumps. Idle cost: nothing runs until the page reports a change.
/// </summary>
internal sealed class OpenCodeTurnNavigator : Grid
{
    private const double CardWidth = 340;
    private const double CardGap = 6;
    private const double MarkWidth = 12;
    private const double MarkThickness = 2;
    private const double IndicatorWidth = 16;
    private const double IndicatorThickness = 3;
    private readonly StackPanel _lines = new() { Spacing = OpenCodeTurnOutline.LinePitch - MarkThickness, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    // Fluent selection-indicator pill; moves via an implicit composition translation (no layout, no timers).
    private readonly Rectangle _indicator = new() { Width = IndicatorWidth, Height = IndicatorThickness, RadiusX = IndicatorThickness / 2,
        RadiusY = IndicatorThickness / 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(250) } };
    private readonly Border _surface = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 10, 0, 10) };
    private readonly Popup _popup = new() { ShouldConstrainToRootBounds = true, IsLightDismissEnabled = false };
    private readonly Border _card = new() { Width = CardWidth, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Padding = new Thickness(16, 14, 16, 14) };
    private readonly TextBlock _question = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _reply = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 8, TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 8, 0, 0) };
    private IReadOnlyList<OpenCodeTurnOutlineItem> _turns = Array.Empty<OpenCodeTurnOutlineItem>();
    private DispatcherQueueTimer? _closeTimer;
    private int _current = -1;
    private int _preview = -1;
    private int _cardTurn = -1;
    private bool _pointerInside;
    private bool _pointerInCard;
    private bool _touchArmed;

    /// <summary>Lazily builds one turn's card content (question/reply verbatim).</summary>
    internal Func<int, OpenCodeTurnPreview?>? PreviewProvider { get; set; }

    internal event Action<int>? TurnInvoked;
    internal int PreviewTurn => _preview;
    internal bool IsCardOpen => _popup.IsOpen;

    internal OpenCodeTurnNavigator()
    {
        Width = 32; // touch target; the marks themselves stay 12px
        MinHeight = 32;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Visibility = Visibility.Collapsed;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        FocusVisualMargin = new Thickness(-2);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetAutomationId(this, "OpenCodeTurnNavigator");
        AutomationProperties.SetName(this, "对话导航");
        Apply(_indicator, FillStyle("AccentFillColorDefaultBrush"));
        _surface.Child = new Grid { Children = { _lines, _indicator } };
        Children.Add(_surface);

        var body = new StackPanel();
        Apply(_question, TextStyle("TextFillColorPrimaryBrush", "BodyStrongTextBlockStyle"));
        body.Children.Add(_question);
        Apply(_reply, TextStyle("TextFillColorSecondaryBrush", "BodyTextBlockStyle"));
        body.Children.Add(_reply);
        _card.Child = body;
        // Solid card (not acrylic): the reply must stay legible over the transcript it covers.
        Apply(_card, BorderStyle("SolidBackgroundFillColorQuarternaryBrush", "SurfaceStrokeColorFlyoutBrush"));
        _card.Shadow = new ThemeShadow();
        _card.Translation = new Vector3(0, 0, 32);
        AutomationProperties.SetAutomationId(_card, "OpenCodeTurnNavigatorCard");
        _card.PointerEntered += (_, _) => { _pointerInCard = true; _closeTimer?.Stop(); };
        _card.PointerExited += (_, _) => { _pointerInCard = false; ScheduleClose(); };
        _card.Tapped += (_, e) => { e.Handled = true; Invoke(_cardTurn); };
        _popup.Child = _card;
        _popup.ChildTransitions = new TransitionCollection { new PopupThemeTransition { FromHorizontalOffset = -8, FromVerticalOffset = 0 } };
        Children.Add(_popup);

        PointerEntered += OnPointerMoved;
        PointerMoved += OnPointerMoved;
        PointerExited += (_, e) => { _pointerInside = false; if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) ScheduleClose(); };
        PointerCanceled += (_, _) => { _pointerInside = false; ScheduleClose(); };
        PointerPressed += OnPointerPressed;
        PointerWheelChanged += OnPointerWheel;
        KeyDown += OnKeyDown;
        GotFocus += (_, _) => { if (_preview < 0) SetPreview(Math.Max(0, _current)); OpenCard(); };
        LostFocus += (_, _) => { if (!_pointerInside && !_pointerInCard) CloseCard(); };
        Unloaded += (_, _) => { _closeTimer?.Stop(); CloseCard(); };
        ActualThemeChanged += (_, _) => _popup.RequestedTheme = ActualTheme;
    }

    private bool _gutterAvailable = true;

    /// <summary>The page hides the navigator when the side gutter is too narrow to hold it.</summary>
    internal void SetGutterAvailable(bool available)
    {
        if (_gutterAvailable == available) return;
        _gutterAvailable = available;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        Visibility = _gutterAvailable && _turns.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        if (Visibility == Visibility.Collapsed) CloseCard();
    }

    internal void SetTurns(IReadOnlyList<OpenCodeTurnOutlineItem> turns, int current)
    {
        _turns = turns;
        ApplyVisibility();
        var lines = OpenCodeTurnOutline.LineCount(turns.Count);
        while (_lines.Children.Count < lines)
            _lines.Children.Add(new Rectangle { Width = MarkWidth, Height = MarkThickness, RadiusX = MarkThickness / 2, RadiusY = MarkThickness / 2 });
        while (_lines.Children.Count > lines) _lines.Children.RemoveAt(_lines.Children.Count - 1);
        _current = Math.Min(current, turns.Count - 1);
        if (_preview >= turns.Count) _preview = -1;
        if (Visibility == Visibility.Visible && _popup.IsOpen && _preview >= 0) ShowCard(_preview, force: true);
        Paint();
    }

    internal void SetCurrent(int current)
    {
        current = Math.Min(current, _turns.Count - 1);
        if (current == _current) return;
        _current = current;
        Paint();
    }

    private void Paint()
    {
        var lines = _lines.Children.Count;
        var currentLine = OpenCodeTurnOutline.LineForTurn(_current, lines, _turns.Count);
        var previewLine = _popup.IsOpen ? OpenCodeTurnOutline.LineForTurn(_preview, lines, _turns.Count) : -1;
        // Identical marks; only the hovered/previewed one strengthens. The current turn is the accent pill.
        for (var i = 0; i < lines; i++)
            Apply((Rectangle)_lines.Children[i], FillStyle(i == previewLine ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"));
        if (currentLine >= 0)
        {
            var y = (float)(currentLine * OpenCodeTurnOutline.LinePitch - (IndicatorThickness - MarkThickness) / 2);
            // First placement (or re-show) jumps; later changes glide via TranslationTransition.
            if (_indicator.Visibility == Visibility.Collapsed)
            {
                var transition = _indicator.TranslationTransition;
                _indicator.TranslationTransition = null;
                _indicator.Translation = new Vector3(0, y, 0);
                _indicator.TranslationTransition = transition;
                _indicator.Visibility = Visibility.Visible;
            }
            else if (_indicator.Translation.Y != y) _indicator.Translation = new Vector3(0, y, 0);
        }
        else _indicator.Visibility = Visibility.Collapsed;
        Apply(_surface, BorderStyle(_pointerInside || _popup.IsOpen ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush", null));
        AutomationProperties.SetName(this, _preview >= 0 && _preview < _turns.Count
            ? $"对话导航，第 {_preview + 1} 轮，共 {_turns.Count} 轮" : $"对话导航，共 {_turns.Count} 轮");
    }

    private int TurnAt(PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(_lines).Position.Y;
        // Extend the stack's first/last pitch to the hit area's padding so edges are reachable.
        return OpenCodeTurnOutline.TurnAtPosition(y, Math.Max(1, _lines.ActualHeight), _turns.Count);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_turns.Count == 0) return;
        _pointerInside = true;
        _closeTimer?.Stop();
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) return; // touch previews on tap
        SetPreview(TurnAt(e));
        OpenCard();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_turns.Count == 0) return;
        e.Handled = true;
        var turn = TurnAt(e);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && !(_touchArmed && _popup.IsOpen && turn == _preview))
        {
            _touchArmed = true;
            SetPreview(turn);
            OpenCard();
            return;
        }
        Invoke(turn);
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_turns.Count == 0) return;
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        SetPreview(Math.Clamp((_preview < 0 ? Math.Max(0, _current) : _preview) + (delta > 0 ? -1 : 1), 0, _turns.Count - 1));
        OpenCard();
        e.Handled = true; // stepping turns, not scrolling the transcript under the pointer
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_turns.Count == 0) return;
        var from = _preview < 0 ? Math.Max(0, _current) : _preview;
        int? next = e.Key switch
        {
            VirtualKey.Up or VirtualKey.Left => from - 1,
            VirtualKey.Down or VirtualKey.Right => from + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => _turns.Count - 1,
            _ => null,
        };
        if (next is { } turn) { SetPreview(Math.Clamp(turn, 0, _turns.Count - 1)); OpenCard(); e.Handled = true; }
        else if (e.Key is VirtualKey.Enter or VirtualKey.Space) { Invoke(from, keepCard: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape && _popup.IsOpen) { CloseCard(); e.Handled = true; }
    }

    private void Invoke(int turn, bool keepCard = false)
    {
        if (turn < 0 || turn >= _turns.Count) return;
        _touchArmed = false;
        if (!keepCard) CloseCard();
        _current = turn;
        Paint();
        TurnInvoked?.Invoke(turn);
    }

    private void SetPreview(int turn)
    {
        if (turn == _preview && _popup.IsOpen) return;
        _preview = turn;
        if (_popup.IsOpen) ShowCard(turn, force: false);
        Paint();
    }

    private void OpenCard()
    {
        if (_preview < 0 || _turns.Count == 0) return;
        if (!_popup.IsOpen)
        {
            _popup.XamlRoot = XamlRoot;
            _popup.RequestedTheme = ActualTheme;
            ShowCard(_preview, force: true);
            _popup.IsOpen = true;
            Paint();
        }
    }

    private void ShowCard(int turn, bool force)
    {
        if (!force && turn == _cardTurn) return;
        _cardTurn = turn;
        var preview = PreviewProvider?.Invoke(turn);
        _question.Text = preview?.Question ?? _turns[turn].Prompt;
        var reply = preview?.Reply ?? string.Empty;
        _reply.Text = reply;
        _reply.Visibility = reply.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_card, preview is null ? _question.Text : _question.Text + "\n" + reply);
        _card.Measure(new Windows.Foundation.Size(CardWidth, double.PositiveInfinity));
        // Beside the stack, vertically centred on the previewed line, kept inside the timeline.
        var lines = _lines.Children.Count;
        var line = OpenCodeTurnOutline.LineForTurn(turn, lines, _turns.Count);
        var lineY = _surface.Padding.Top + line * OpenCodeTurnOutline.LinePitch + 1 + (ActualHeight - _surface.ActualHeight) / 2;
        var top = lineY - Math.Min(28, _card.DesiredSize.Height / 2);
        if (Parent is FrameworkElement host)
        {
            var origin = TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            top = Math.Clamp(top, 8 - origin, Math.Max(8 - origin, host.ActualHeight - origin - _card.DesiredSize.Height - 8));
        }
        _popup.HorizontalOffset = ActualWidth + CardGap;
        _popup.VerticalOffset = top;
    }

    private void ScheduleClose()
    {
        if (FocusState != FocusState.Unfocused && _popup.IsOpen) { Paint(); return; }
        // One-shot grace so the pointer can cross the gap into the card; never runs while idle.
        _closeTimer ??= CreateCloseTimer();
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private DispatcherQueueTimer CreateCloseTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(180);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (!_pointerInside && !_pointerInCard && FocusState == FocusState.Unfocused) CloseCard(); else Paint(); };
        return timer;
    }

    private void CloseCard()
    {
        _closeTimer?.Stop();
        _touchArmed = false;
        _pointerInCard = false;
        if (_popup.IsOpen) _popup.IsOpen = false;
        _reply.Text = string.Empty;
        _cardTurn = -1;
        _preview = -1;
        Paint();
    }

    // Theme-following styles: {ThemeResource} setters re-resolve on theme switch; cached per key.
    private static readonly Dictionary<string, Style?> Styles = new(StringComparer.Ordinal);
    private static Style? FillStyle(string brush) => Load("Rectangle|" + brush,
        $"<Style TargetType=\"Rectangle\"><Setter Property=\"Fill\" Value=\"{{ThemeResource {brush}}}\" /></Style>");
    private static Style? TextStyle(string brush, string basedOn) => Load("TextBlock|" + brush + "|" + basedOn,
        $"<Style TargetType=\"TextBlock\" BasedOn=\"{{StaticResource {basedOn}}}\"><Setter Property=\"Foreground\" Value=\"{{ThemeResource {brush}}}\" /></Style>");
    private static Style? BorderStyle(string background, string? border) => Load("Border|" + background + "|" + border,
        $"<Style TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{{ThemeResource {background}}}\" />"
        + (border is null ? string.Empty : $"<Setter Property=\"BorderBrush\" Value=\"{{ThemeResource {border}}}\" />") + "</Style>");

    private static Style? Load(string key, string xaml)
    {
        if (Styles.TryGetValue(key, out var style)) return style;
        try
        {
            style = (Style)XamlReader.Load(xaml.Replace("<Style ", "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "));
        }
        catch (Exception) { style = null; } // missing resource: keep the default appearance
        Styles[key] = style;
        return style;
    }

    private static void Apply(FrameworkElement element, Style? style)
    {
        if (style is not null && !ReferenceEquals(element.Style, style)) element.Style = style;
    }
}
