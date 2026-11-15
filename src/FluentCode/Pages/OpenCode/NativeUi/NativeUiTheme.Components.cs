// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

// Structural helpers only; colour tokens and icon mappings are owned by NativeUiTheme.
internal static class NativeUiComponentTheme
{
    internal static double Number(NativeUiElement node, string prop, double fallback = 0)
        => NativeUiTheme.Prop(node, prop) is { } value && double.IsFinite(NativeUiValues.Number(value)) ? NativeUiValues.Number(value) : fallback;
    internal static TextBlock Text(string value, bool secondary = false)
    { var text = new TextBlock { Text = value }; NativeUiTheme.Text(text, new(secondary ? "Caption" : "Text", new Dictionary<string, object?>())); return text; }
    internal static CornerRadius Radius(object? value) => new(value is double n && double.IsFinite(n) ? Math.Clamp(n, 0, 1000) : NativeUiValues.Text(value) switch { "sm" => 4, "lg" => 12, "xl" => 16, "full" => 1000, _ => 8 });
    internal static void Length(FrameworkElement element, object? value, bool height = false)
    {
        var text = NativeUiValues.Text(value);
        var numeric = value is double number ? number : text.EndsWith("px", StringComparison.Ordinal) && double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
        numeric = double.IsFinite(numeric) ? Math.Clamp(numeric, 0, 4096) : double.NaN;
        if (height) element.Height = numeric; else element.Width = numeric;
        // Percentages are resolved by NativeUiBoxHost against the parent's offered size, not CSS measurement.
        if (!height && text.EndsWith('%')) element.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}

/// <summary>
/// The Pressable tile: a stock Button with the platform's own template and visual states, made to look at rest
/// like the tile it replaces. The facts below are from microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5.
/// <para>Rest look. DefaultButtonStyle (src/controls/dev/CommonStyles/Button_themeresources.xaml) sets a fill, a
/// border, padding 11,5,11,6, alignment Left/Center, a text colour, the body font and a focus rectangle 3 px
/// outside the control; it sets no minimum size. The renderer sets fill, border, padding and radius on every
/// update; the rest is put back here. The template is one ContentPresenter that draws border and padding with
/// the same code as the Border the tile used before (src/dxaml/xcp/core/core/elements/ContentPresenter.cpp).</para>
/// <para>States. PointerOver, Pressed and Disabled swap fill, border and text colour for the resources named
/// Button…PointerOver/Pressed/Disabled. Those names are answered from this tile's own Resources: the fill moves
/// by the platform's subtle hover and press tints over the tile's own colour, the border and the text keep
/// theirs (a selected tile keeps its ring under the pointer). The tile is never disabled: while the card runs
/// an action the renderer refuses the press and the hover and press fills are the rest fill. The brushes are the same objects for the tile's life,
/// since the template reads a resource once.</para>
/// <para>Text. A Button gives its content its own text colour and font family; the tile before had neither and
/// let the surroundings through (src/dxaml/xcp/core/core/elements/Control.cpp, PullInheritedTextFormatting).
/// Both are copied from above when the tile enters the page, so an uncoloured Icon and a Text keep their look.
/// </para>
/// <para>Cost. A theme colour is read by parsing a line of XAML, so the state colours and the text colour are
/// worked out only when what they depend on changed (NativeUiPressable.Memo): the fill, the theme, the contrast
/// scheme, or the tile entering a tree. An update that changes none of them does no such work here.</para>
/// <para>Pointer. Selectable text marks the pointer press as handled
/// (src/dxaml/xcp/core/text/common/TextSelectionManager.cpp, OnPointerPressed) and a Button ignores a handled
/// press (src/dxaml/xcp/dxaml/lib/ButtonBase_Partial.cpp, OnPointerPressed), so the tile's own text is not
/// selectable: a click anywhere on the tile presses it.</para>
/// </summary>
internal sealed partial class NativeUiPressableButton : Button
{
    private readonly SolidColorBrush _rest = new(), _over = new(), _down = new(), _edge = new(), _ink = new();
    private readonly NativeUiPressable.Memo _memo = new();
    private ElementTheme _pageTheme = ElementTheme.Default;
    private string _contrast = "";
    private Windows.UI.Color _overTint, _downTint;

    internal NativeUiPressableButton()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Center;
        BackgroundSizing = BackgroundSizing.InnerBorderEdge;
        // The stock rectangle sits 3 px outside the control, where a Carousel's ScrollViewer clips it.
        FocusVisualMargin = new Thickness(0);
        Resources["ButtonBackgroundPointerOver"] = _over; Resources["ButtonBackgroundPressed"] = _down; Resources["ButtonBackgroundDisabled"] = _rest;
        foreach (var state in new[] { "PointerOver", "Pressed", "Disabled" }) { Resources["ButtonBorderBrush" + state] = _edge; Resources["ButtonForeground" + state] = _ink; }
        // Raised once after each time the tile enters a tree, so also after a move to another parent
        // (src/dxaml/xcp/core/core/elements/framework.cpp: RaiseLoadingEventIfNeeded raises it in the first
        // measure, EnterImpl clears the flag). The handler is the tile's own method on the tile's own event.
        Loading += OnTileLoading;
    }

    /// <summary>
    /// The controls that keep their own input when they sit inside a tile. The map is here because a Button takes
    /// the pointer for itself on a press (ButtonBase::OnPointerPressed captures it), which would end a map drag.
    /// </summary>
    internal static bool Nested(DependencyObject node)
        => node is Button or TextBox or CheckBox or ComboBox or Slider or RadioButton or Segmented or Mapsui.UI.WinUI.MapControl;

    private void OnTileLoading(FrameworkElement sender, object args) { _memo.Moved(); TakeTextIfNeeded(); }

    /// <summary>
    /// Sets the tile's fill and border and derives the state colours from them. Call after RequestedTheme.
    /// <paramref name="contrast"/> is the name of the high contrast scheme in use, or empty.
    /// </summary>
    /// <param name="busy">The card is running an action: a press is refused, so the tile shows no hover or press fill.</param>
    internal void Paint(Brush fill, Brush edge, ElementTheme pageTheme, string contrast, bool busy)
    {
        _pageTheme = pageTheme; _contrast = contrast;
        Background = fill; BorderBrush = edge;
        if (fill is SolidColorBrush solid && _memo.NeedsTints((solid.Color.A, solid.Color.R, solid.Color.G, solid.Color.B), Theme.ToString(), contrast))
        {
            _rest.Color = solid.Color;
            _overTint = Tinted(solid.Color, "SubtleFillColorSecondaryBrush"); _downTint = Tinted(solid.Color, "SubtleFillColorTertiaryBrush");
        }
        // Switching between the remembered colours costs no lookup, so the start and the end of an action are cheap.
        var over = NativeUiPressable.StateFill(_rest.Color, _overTint, busy); var down = NativeUiPressable.StateFill(_rest.Color, _downTint, busy);
        if (_over.Color != over) _over.Color = over;
        if (_down.Color != down) _down.Color = down;
        if (edge is SolidColorBrush line && _edge.Color != line.Color) _edge.Color = line.Color;
        TakeTextIfNeeded();
    }

    private void TakeTextIfNeeded() { if (_memo.NeedsText(_pageTheme.ToString(), RequestedTheme.ToString(), _contrast)) TakeTextFromAbove(); }

    /// <summary>Switches text selection off in the tile's own content; controls nested in the tile are left alone.</summary>
    internal void Quiet() { if (Content is DependencyObject content) Quiet(content); }

    private static void Quiet(DependencyObject node)
    {
        if (Nested(node)) return;
        if (node is TextBlock { IsTextSelectionEnabled: true } text) text.IsTextSelectionEnabled = false;
        else if (node is RichTextBlock rich)
        {
            if (rich.IsTextSelectionEnabled) rich.IsTextSelectionEnabled = false;
            // Components placed inside a Text sit in inline containers.
            foreach (var block in rich.Blocks) if (block is Paragraph paragraph) foreach (var inline in paragraph.Inlines) if (inline is InlineUIContainer { Child: { } child }) Quiet(child);
            return;
        }
        for (int index = 0, count = VisualTreeHelper.GetChildrenCount(node); index < count; index++) Quiet(VisualTreeHelper.GetChild(node, index));
    }

    private ElementTheme Theme => RequestedTheme != ElementTheme.Default ? RequestedTheme : _pageTheme;

    private static Brush? ThemeBrush(string key, ElementTheme theme)
        => OpenCodeThemedXaml.Load<Border>("Border", "RequestedTheme=\"" + theme + "\" Background=\"{ThemeResource " + key + "}\"")?.Background;

    private Windows.UI.Color Tinted(Windows.UI.Color rest, string key)
    {
        if (ThemeBrush(key, Theme) is not SolidColorBrush tint) return rest;
        var mixed = NativeUiPressable.Over((rest.A, rest.R, rest.G, rest.B), (tint.Color.A, tint.Color.R, tint.Color.G, tint.Color.B));
        return Windows.UI.Color.FromArgb(mixed.A, mixed.R, mixed.G, mixed.B);
    }

    // What the tile's content took from its surroundings before the tile was a Button. Text colour: an element
    // with a theme of its own hands down that theme's default text brush (framework.cpp, NotifyThemeChangedCore);
    // otherwise the nearest control above decides. Font family: the nearest element above that carries one.
    private void TakeTextFromAbove()
    {
        Brush? ink = RequestedTheme != ElementTheme.Default ? ThemeBrush("DefaultTextForegroundThemeBrush", RequestedTheme) : null;
        FontFamily? family = null;
        for (var node = VisualTreeHelper.GetParent(this); node is not null && (ink is null || family is null); node = VisualTreeHelper.GetParent(node))
        {
            var (brush, font) = node switch
            {
                Control control => (control.Foreground, control.FontFamily),
                ContentPresenter presenter => (presenter.Foreground, presenter.FontFamily),
                RichTextBlock rich => (rich.Foreground, rich.FontFamily),
                TextBlock text => (text.Foreground, text.FontFamily),
                FrameworkElement { RequestedTheme: not ElementTheme.Default } themed => (ThemeBrush("DefaultTextForegroundThemeBrush", themed.RequestedTheme), null),
                _ => ((Brush?)null, (FontFamily?)null),
            };
            ink ??= brush; family ??= font;
        }
        ink ??= ThemeBrush("DefaultTextForegroundThemeBrush", _pageTheme);
        if (ink is not null && !ReferenceEquals(Foreground, ink)) Foreground = ink;
        if (ink is SolidColorBrush solid) _ink.Color = solid.Color;
        if (family is not null && FontFamily?.Source != family.Source) FontFamily = family;
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        // Content that arrived after the last update (a template applied later) is caught before a press can start.
        Quiet();
        base.OnPointerEntered(e);
    }

    // A press that starts in a nested control is that control's, whether or not it marks the press handled.
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (NativeUiPressable.InsideNested<DependencyObject>(e.OriginalSource as DependencyObject, this, VisualTreeHelper.GetParent, Nested)) return;
        base.OnPointerPressed(e);
    }

    // ButtonBase presses on Space and Enter whatever the key's source (src/dxaml/xcp/dxaml/lib/ButtonBase_Partial.cpp,
    // OnKeyDown / OnKeyUp); a key typed in a control inside the tile must not press the tile.
    protected override void OnKeyDown(KeyRoutedEventArgs e) { if (NativeUiPressable.OwnKey(e.OriginalSource, this)) base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyRoutedEventArgs e) { if (NativeUiPressable.OwnKey(e.OriginalSource, this)) base.OnKeyUp(e); }
}
