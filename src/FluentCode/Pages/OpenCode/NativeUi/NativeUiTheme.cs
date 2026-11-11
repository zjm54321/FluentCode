// SPDX-License-Identifier: GPL-3.0-or-later
// All native-card styling lives here (spec: artifacts/design-native-ui-*/spec.md). Parser/runtime/contracts stay untouched.
// Colors always come from fixed Fluent ThemeResource keys (or the fixed data palette), never model CSS or arbitrary brush values.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Controls;
using FluentCode.Controls;
using FluentCode.Motion;
using FluentCode.Services.Appearance;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUI.TableView;
using FontWeights = Microsoft.UI.Text.FontWeights;
using FontStyle = Windows.UI.Text.FontStyle;
using TextDecorations = Windows.UI.Text.TextDecorations;

namespace FluentCode.Pages.OpenCode;

internal static class NativeUiTheme
{
    internal const double MaximumWidth = 640;
    /// <summary>Forms, calculators and pickers: a compact card left-aligned under the assistant text.</summary>
    internal const double CompactWidth = 480;
    /// <summary>Tables, charts, carousels and multi-column rows use the full transcript column.</summary>
    internal const double WideWidth = 808;
    internal const double WrapCellWidth = 144;
    internal const string PlaceholderTag = "native-ui-placeholder";
    // Mapsui draws through Skia, which does not apply XAML's CJK font fallback.
    internal const string MapLabelFont = "Microsoft YaHei UI";
    private const double SmallRadius = 4;
    private const string Xmlns = " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    private static readonly HashSet<string> WideTypes = new(StringComparer.Ordinal) { "Table", "Chart", "Carousel", "Map", "Grid", "Image", "ImageGallery", "Video" };
    private static readonly string[] LightPalette = { "", "#FF038387", "#FFCA5010", "#FF8764B8", "#FFC239B3", "#FF8A8A8A" };
    private static readonly string[] DarkPalette = { "", "#FF00B7C3", "#FFF7894A", "#FFB4A0FF", "#FFEE8BDD", "#FF9D9D9D" };
    private static readonly Dictionary<string, Style?> TextStyles = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> AliasXaml = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<NativeUiElement, object> SurfaceCards = new();
    private static readonly ConditionalWeakTable<FrameworkElement, string> AppliedAliases = new();
    private static readonly ConditionalWeakTable<FrameworkElement, FitState> Fits = new();
    private static readonly ConditionalWeakTable<Slider, SliderParts> Sliders = new();
    private static readonly ConditionalWeakTable<Button, KeycapState> Keycaps = new();
    private static readonly object Marker = new();
    private static bool? _palette;
    private static bool Palette => _palette ??= RegisterPalette();

    internal static double Gap(object? value, double fallback = 3) => 4 * Math.Clamp(value is int integer ? integer : value is double n && double.IsFinite(n) ? n : fallback, 0, 16);
    private static double Number(string resource, double fallback) => Application.Current.Resources.TryGetValue(resource, out var value) && value is double number ? number : fallback;
    private static Style? Style(string name) => Application.Current.Resources.TryGetValue(name, out var style) ? style as Style : null;
    internal static void Host(FrameworkElement element) { element.MaxWidth = WideWidth; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
    internal static object? Prop(NativeUiElement node, string name) => node.Props.GetValueOrDefault(name);
    internal static string String(NativeUiElement node, string name) => NativeUiValues.Text(Prop(node, name));
    internal static bool Flag(NativeUiElement node, string name) => Prop(node, name) is true;

    // ---------------------------------------------------------------- surface & width

    /// <summary>
    /// Called before the tree is realized. Layout-level Cards (reached through Col/Row only) become the transcript
    /// surface; otherwise the renderer root itself is framed and every Card inside renders as a nested card.
    /// </summary>
    internal static void Surface(Border root, NativeUiElement tree)
    {
        var leaves = new List<NativeUiElement>(); var wide = false;
        void Layout(object? value, int depth)
        {
            if (depth > 24) return;
            if (value is IReadOnlyList<object?> list) { foreach (var item in list) Layout(item, depth + 1); return; }
            if (value is not NativeUiElement element) { if (value is not null) leaves.Add(new("Text", new Dictionary<string, object?>())); return; }
            if (element.TypeName is "Col" or "Row")
            {
                if (element.TypeName == "Row" && Prop(element, "children") is IReadOnlyList<object?> row && row.Count(child => child is NativeUiElement { TypeName: "Card" or "Col" }) >= 2) wide = true;
                Layout(Prop(element, "children"), depth + 1);
            }
            else leaves.Add(element);
        }
        Layout(tree, 0);
        wide |= Contains(tree, 0);
        var cards = leaves.Count > 0 && leaves.All(leaf => leaf.TypeName == "Card");
        if (cards)
        {
            foreach (var leaf in leaves) SurfaceCards.AddOrUpdate(leaf, Marker);
            root.Style = null; root.BorderThickness = new Thickness(0); root.Padding = new Thickness(0); root.CornerRadius = new CornerRadius(0);
        }
        else Frame(root);
        Fit(root, wide);
    }
    private static bool Contains(object? value, int depth)
    {
        if (depth > 48) return false;
        if (value is IReadOnlyList<object?> list) return list.Any(item => Contains(item, depth + 1));
        if (value is not NativeUiElement element) return false;
        return WideTypes.Contains(element.TypeName) || element.Props.Values.Any(prop => Contains(prop, depth + 1));
    }
    private static void Frame(Border border, double padding = 16)
    {
        border.Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SolidBackgroundFillColorQuarternaryBrush"), ("BorderBrush", "CardStrokeColorDefaultBrush"));
        border.BorderThickness = new Thickness(Number("CardBorderThemeThickness", 1));
        border.CornerRadius = new CornerRadius(Number("OverlayCornerRadius", 8));
        border.Padding = new Thickness(padding);
    }

    private sealed class FitState { internal bool Wide; internal FrameworkElement? Parent; internal SizeChangedEventHandler? Handler; }
    /// <summary>
    /// Compact surfaces sit left-aligned at min(480, column); Stretch+MaxWidth would centre them instead.
    /// Wide surfaces stretch to the transcript column (capped by <see cref="WideWidth"/>).
    /// </summary>
    internal static void Fit(FrameworkElement element, bool wide)
    {
        if (!Fits.TryGetValue(element, out var state))
        {
            state = new(); Fits.Add(element, state);
            var owned = state; owned.Handler = (_, _) => Resize(element, owned);
            element.Loaded += (_, _) => { Attach(element, owned); Resize(element, owned); };
        }
        state.Wide = wide; Attach(element, state); Resize(element, state);
    }
    private static void Attach(FrameworkElement element, FitState state)
    {
        if (element.Parent is not FrameworkElement parent || ReferenceEquals(parent, state.Parent)) return;
        if (state.Parent is { } old) old.SizeChanged -= state.Handler;
        state.Parent = parent; parent.SizeChanged += state.Handler;
    }
    private static void Resize(FrameworkElement element, FitState state)
    {
        element.MaxWidth = WideWidth;
        if (state.Wide) { element.HorizontalAlignment = HorizontalAlignment.Stretch; element.Width = double.NaN; return; }
        element.HorizontalAlignment = HorizontalAlignment.Left;
        var available = state.Parent?.ActualWidth ?? 0;
        var width = available > 0 ? Math.Min(CompactWidth, available) : CompactWidth;
        if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > 0.5) element.Width = width;
    }

    // ---------------------------------------------------------------- typography

    private static string Foreground(string token) => token switch
    {
        "muted" => "TextFillColorSecondaryBrush", "faint" => "TextFillColorTertiaryBrush", "success" => "SystemFillColorSuccessBrush",
        "warning" => "SystemFillColorCautionBrush", "danger" => "SystemFillColorCriticalBrush", "info" => "AccentTextFillColorPrimaryBrush",
        "purple" => Palette ? "NativeUiPurpleBrush" : "AccentTextFillColorPrimaryBrush", "pink" => Palette ? "NativeUiPinkBrush" : "AccentTextFillColorPrimaryBrush",
        "chart1" or "chart2" or "chart3" or "chart4" or "chart5" or "chart6" => Palette ? "NativeUiChart" + token[5] + "Brush" : "AccentTextFillColorPrimaryBrush",
        "canvas" => "TextOnAccentFillColorPrimaryBrush",
        _ => "TextFillColorPrimaryBrush",
    };
    private static string BadgeTone(string color) => color is "info" or "success" or "warning" or "danger" or "purple" or "pink" ? color : "muted";
    // OIU backgrounds: status tokens are soft tints; chart, ink and canvas are solid fills
    // (split bars, funnels, progress tracks), border/surface-strong are the neutral greys.
    private static string Background(string color) => color switch
    {
        "surface" => "SolidBackgroundFillColorQuarternaryBrush",
        "success" => "SystemFillColorSuccessBackgroundBrush", "warning" => "SystemFillColorCautionBackgroundBrush", "danger" => "SystemFillColorCriticalBackgroundBrush",
        "info" => Palette ? "NativeUiAccentTintBrush" : "SystemFillColorAttentionBackgroundBrush",
        "purple" => Palette ? "NativeUiPurpleTintBrush" : "ControlAltFillColorTertiaryBrush", "pink" => Palette ? "NativeUiPinkTintBrush" : "ControlAltFillColorTertiaryBrush",
        "chart1" or "chart2" or "chart3" or "chart4" or "chart5" or "chart6" => Palette ? "NativeUiChart" + color[5] + "Brush" : "AccentFillColorDefaultBrush",
        "ink" => "TextFillColorPrimaryBrush", "canvas" => "SolidBackgroundFillColorBaseBrush", "border" => "DividerStrokeColorDefaultBrush",
        _ => "ControlAltFillColorTertiaryBrush",
    };
    private static bool Hex(string token, out Windows.UI.Color color)
    {
        var parsed = NativeUiStyleMath.TryHex(token, out var a, out var r, out var g, out var b);
        color = Windows.UI.Color.FromArgb(a, r, g, b); return parsed;
    }
    internal static ElementTheme ContrastTheme(string background)
        => NativeUiStyleMath.DarkTextOn(background) switch { true => ElementTheme.Light, false => ElementTheme.Dark, null => ElementTheme.Default };
    internal static Brush Brush(string token, bool background = false, ElementTheme theme = ElementTheme.Default)
    {
        if (token == "none") return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // Preserve the catalog's bounded SVG/Box hex colour props; no model XAML or CSS is loaded.
        if (Hex(token, out var hex)) return new SolidColorBrush(hex);
        var resource = token == "divider" ? "DividerStrokeColorDefaultBrush" : background || token == "surface" ? Background(token) : Foreground(token);
        return OpenCodeThemedXaml.Load<Border>("Border", "RequestedTheme=\"" + theme + "\" Background=\"{ThemeResource " + resource + "}\"")?.Background
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
    private static Style? TextStyle(string target, string basedOn, string foreground, double size = 0, double line = 0, string? weight = null)
    {
        var key = target + ":" + basedOn + ":" + foreground + ":" + size.ToString(CultureInfo.InvariantCulture) + ":" + line.ToString(CultureInfo.InvariantCulture) + ":" + weight;
        if (TextStyles.TryGetValue(key, out var style)) return style;
        var setters = "<Setter Property=\"Foreground\" Value=\"{ThemeResource " + foreground + "}\" />";
        if (size > 0) setters += "<Setter Property=\"FontSize\" Value=\"" + size.ToString(CultureInfo.InvariantCulture) + "\" />";
        if (line > 0) setters += "<Setter Property=\"LineHeight\" Value=\"" + line.ToString(CultureInfo.InvariantCulture) + "\" />";
        if (weight is not null) setters += "<Setter Property=\"FontWeight\" Value=\"" + weight + "\" />";
        style = OpenCodeThemedXaml.Load<Style>("Style", "TargetType=\"" + target + "\"" + (basedOn.Length > 0 ? " BasedOn=\"{StaticResource " + basedOn + "}\"" : ""), setters);
        return TextStyles[key] = style;
    }
    internal static void Text(TextBlock text, NativeUiElement node)
    {
        var size = String(node, "size");
        var type = node.TypeName;
        var color = String(node, "color");
        // Title md (the default) sits between Body Strong and Subtitle: 18/24 semibold.
        (string Style, double Size, double Line, string? Weight) ramp = type switch
        {
            "Title" => size switch
            {
                "3xl" => ("DisplayTextBlockStyle", 0d, 0d, null), "2xl" => ("TitleLargeTextBlockStyle", 0d, 0d, null), "xl" => ("TitleTextBlockStyle", 0d, 0d, null),
                "lg" => ("SubtitleTextBlockStyle", 0d, 0d, null), "sm" => ("BodyStrongTextBlockStyle", 0d, 0d, null), _ => ("BodyStrongTextBlockStyle", 18d, 24d, null),
            },
            "Caption" => ("CaptionTextBlockStyle", 0d, 0d, null),
            "Badge" => ("CaptionTextBlockStyle", 0d, 0d, "SemiBold"),
            _ => ("BodyTextBlockStyle", 0d, 0d, null),
        };
        var token = type == "Badge" ? BadgeTone(color) : color.Length > 0 ? color : type == "Caption" ? "muted" : "ink";
        text.Style = TextStyle("TextBlock", ramp.Style, Foreground(token), ramp.Size, ramp.Line, ramp.Weight);
        // A raw colour (OIU allows any CSS colour) is a local brush; tokens stay theme resources.
        if (type != "Badge" && Hex(color, out var raw)) text.Foreground = new SolidColorBrush(raw); else text.ClearValue(TextBlock.ForegroundProperty);
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = true;
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
    }
    internal static void RichText(RichTextBlock text, NativeUiElement node)
    {
        var (fontSize, line) = String(node, "size") switch { "xs" or "sm" => (12d, 16d), "lg" => (18d, 24d), _ => (14d, 20d) };
        text.Style = TextStyle("RichTextBlock", "", Foreground(String(node, "color")));
        if (Hex(String(node, "color"), out var raw)) text.Foreground = new SolidColorBrush(raw); else text.ClearValue(RichTextBlock.ForegroundProperty);
        text.FontSize = fontSize; text.LineHeight = line;
        text.FontWeight = String(node, "weight") switch { "medium" => FontWeights.Medium, "semibold" => FontWeights.SemiBold, _ => FontWeights.Normal };
        text.FontStyle = Flag(node, "italic") ? FontStyle.Italic : FontStyle.Normal;
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = true;
        text.MaxLines = Prop(node, "maxLines") is double lines && double.IsFinite(lines) ? (int)Math.Clamp(lines, 0, 100) : 0;
        text.TextTrimming = text.MaxLines > 0 ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        Typography.SetNumeralAlignment(text, Flag(node, "tabularNums") ? FontNumeralAlignment.Tabular : FontNumeralAlignment.Normal);
    }
    internal static ControlTemplate? TileTemplate => _tileTemplate ??= OpenCodeThemedXaml.Load<ControlTemplate>("ControlTemplate", "TargetType=\"ContentControl\"",
        "<Border Background=\"{TemplateBinding Background}\" BorderBrush=\"{TemplateBinding BorderBrush}\" BorderThickness=\"{TemplateBinding BorderThickness}\" CornerRadius=\"{TemplateBinding CornerRadius}\" Padding=\"{TemplateBinding Padding}\">"
        + "<ContentPresenter Content=\"{TemplateBinding Content}\" HorizontalContentAlignment=\"{TemplateBinding HorizontalContentAlignment}\" VerticalContentAlignment=\"Center\" /></Border>");
    private static ControlTemplate? _tileTemplate;
    internal static void Inline(Run run, NativeUiElement node, NativeUiStyleMath.InlineStyle style = NativeUiStyleMath.InlineStyle.None)
    {
        run.TextDecorations = Flag(node, "lineThrough") || style.HasFlag(NativeUiStyleMath.InlineStyle.Strike) ? TextDecorations.Strikethrough : TextDecorations.None;
        if (style.HasFlag(NativeUiStyleMath.InlineStyle.Bold)) run.FontWeight = FontWeights.SemiBold;
        if (style.HasFlag(NativeUiStyleMath.InlineStyle.Italic)) run.FontStyle = FontStyle.Italic;
        if (style.HasFlag(NativeUiStyleMath.InlineStyle.Code)) AppFonts.Apply(run, AppFontSlot.Monospace);
    }

    // ---------------------------------------------------------------- containers

    internal static void Card(Border border, StackPanel content, NativeUiElement node)
    {
        if (SurfaceCards.TryGetValue(node, out _)) Frame(border, Gap(Prop(node, "padding"), 4));
        else
        {
            var soft = String(node, "variant") == "soft";
            border.Style = soft ? OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SubtleFillColorSecondaryBrush"))
                : OpenCodeThemedXaml.BrushStyle("Border", ("BorderBrush", "CardStrokeColorDefaultBrush"));
            border.BorderThickness = new Thickness(soft ? 0 : Number("CardBorderThemeThickness", 1));
            border.CornerRadius = new CornerRadius(SmallRadius);
            border.Padding = new Thickness(Gap(Prop(node, "padding")));
        }
        content.Spacing = Gap(Prop(node, "gap"));
    }
    internal static void Divider(Border divider)
    { divider.Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "DividerStrokeColorDefaultBrush")); divider.Height = Number("CardBorderThemeThickness", 1); }
    internal static void Column(StackPanel panel, NativeUiElement node)
    {
        panel.Spacing = Gap(Prop(node, "gap"));
        foreach (var child in panel.Children.OfType<FrameworkElement>()) child.HorizontalAlignment = String(node, "align") switch
        { "start" => HorizontalAlignment.Left, "center" => HorizontalAlignment.Center, "end" => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch };
    }
    internal static void Row(Grid panel, NativeUiElement node)
    {
        panel.ColumnSpacing = panel.RowSpacing = Gap(Prop(node, "gap"), 2);
        var count = panel.Children.Count;
        // Buttons keep their natural width unless they are block (OIU Row is a flex row). Wrapped cells are at least as
        // wide as the widest child, so a long label wraps to the next line instead of being clipped by an equal column.
        var cell = WrapCellWidth;
        if (Flag(node, "wrap") && panel.ActualWidth > 0)
            foreach (var child in panel.Children) { child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity)); cell = Math.Max(cell, child.DesiredSize.Width + panel.ColumnSpacing); }
        var columns = Flag(node, "wrap") && panel.ActualWidth > 0 ? Math.Max(1, Math.Min(count, (int)((panel.ActualWidth + panel.ColumnSpacing) / cell))) : Math.Max(1, count);
        panel.ColumnDefinitions.Clear(); panel.RowDefinitions.Clear();
        var block = panel.Children.OfType<Button>().Any(button => button.HorizontalAlignment == HorizontalAlignment.Stretch);
        var justify = String(node, "justify");
        panel.HorizontalAlignment = block || Flag(node, "wrap") || justify == "between" ? HorizontalAlignment.Stretch : justify switch { "center" => HorizontalAlignment.Center, "end" => HorizontalAlignment.Right, _ => HorizontalAlignment.Left };
        for (var i = 0; i < columns; i++) panel.ColumnDefinitions.Add(new ColumnDefinition { Width = block || Flag(node, "wrap") || justify == "between" && i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        for (var i = 0; i < (count + columns - 1) / columns; i++) panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < count; i++)
        {
            if (panel.Children[i] is FrameworkElement child)
            {
                Grid.SetColumn(child, i % columns); Grid.SetRow(child, i / columns);
                child.VerticalAlignment = String(node, "align") switch { "start" => VerticalAlignment.Top, "end" => VerticalAlignment.Bottom, "stretch" => VerticalAlignment.Stretch, _ => VerticalAlignment.Center };
            }
        }
    }

    // ---------------------------------------------------------------- badge & button

    // C06: the tone a badge hands to the shared adapter. The adapter speaks an app-level enum only
    // (CONTRACT-F §2), so the tone vocabulary stays in one place: BadgeTone() normalizes anything it does not
    // know to "muted" first, and the two categorical tones keep their own adapter members instead of collapsing
    // into a status colour.
    internal static CommunityTextBadgeTone BadgeAdapterTone(string color) => BadgeTone(color) switch
    {
        "success" => CommunityTextBadgeTone.Success,
        "warning" => CommunityTextBadgeTone.Caution,
        "danger" => CommunityTextBadgeTone.Critical,
        "info" => CommunityTextBadgeTone.Attention,
        "purple" => CommunityTextBadgeTone.Purple,
        "pink" => CommunityTextBadgeTone.Pink,
        _ => CommunityTextBadgeTone.Neutral,
    };

    /// <summary>True when the merged app resources resolve the key to a brush right now.</summary>
    private static bool TryToneBrush(string key)
    {
        try { return Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Brush; }
        catch { return false; }
    }

    /// <summary>Styles the badge that carries a leading glyph; a glyph-less badge goes through CommunityTextBadge.</summary>
    internal static void Badge(Border border, NativeUiElement node)
    {
        var color = String(node, "color");
        var tone = BadgeTone(color);
        // C06: the icon badge mirrors the adapter pill so both forms read as one component — same box, same 1-DIP
        // tone ring, same palette. The status tones already share their SystemFillColor* keys through Foreground();
        // the categorical tones take the library's own IssueLabel* keys (NativeUiStyleMath.BadgeLibraryTone), so a
        // badge looks the same whether or not it carries an icon.
        var app = (Box: OpenCodeThemedXaml.BrushStyle("Border", ("Background", Background(tone)), ("BorderBrush", Foreground(tone))),
            Glyph: OpenCodeThemedXaml.BrushStyle("FontIcon", ("Foreground", Foreground(tone))),
            Label: TextStyle("TextBlock", "CaptionTextBlockStyle", Foreground(tone), 0d, 0d, "SemiBold"));
        // Probe before parsing: the style helpers cache a failed parse for good, so a key that is missing (renamed
        // by a later DevWinUI) or is not a brush must never reach them. Then the app tint is kept — silently; see
        // the C06 notes.
        var library = NativeUiStyleMath.BadgeLibraryTone(tone) is { } keys && TryToneBrush(keys.Background) && TryToneBrush(keys.Border) && TryToneBrush(keys.Foreground)
            ? (Box: OpenCodeThemedXaml.BrushStyle("Border", ("Background", keys.Background), ("BorderBrush", keys.Border)),
                Glyph: OpenCodeThemedXaml.BrushStyle("FontIcon", ("Foreground", keys.Foreground)),
                Label: TextStyle("TextBlock", "CaptionTextBlockStyle", keys.Foreground, 0d, 0d, "SemiBold"))
            : default;
        var (box, glyph, label) = NativeUiStyleMath.PickBadgePalette(app, library);
        border.Style = box;
        border.BorderThickness = new Thickness(1);
        border.Padding = new Thickness(8, 2, 8, 2); border.MinHeight = 20; border.CornerRadius = new CornerRadius(10);
        border.HorizontalAlignment = HorizontalAlignment.Left; border.VerticalAlignment = VerticalAlignment.Center;
        if (border.Child is not StackPanel panel) return;
        panel.Spacing = 4; panel.VerticalAlignment = VerticalAlignment.Center;
        foreach (var icon in panel.Children.OfType<FontIcon>())
        {
            icon.FontSize = 12; icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Style = glyph;
        }
        // The label keeps the ramp Text() gave it (Caption, SemiBold) and takes the badge's ink.
        foreach (var text in panel.Children.OfType<TextBlock>()) { text.VerticalAlignment = VerticalAlignment.Center; text.TextWrapping = TextWrapping.NoWrap; text.Style = label; }
    }
    internal static void Button(Button button, NativeUiElement node)
    {
        // OIU: Button defaults to solid, IconButton to outline.
        var variant = String(node, "variant") is { Length: > 0 } named ? named : node.TypeName == "IconButton" ? "outline" : "solid";
        var color = String(node, "color");
        var size = String(node, "size");
        button.Style = variant == "solid" ? Style("AccentButtonStyle") : null;
        button.CornerRadius = Application.Current.Resources.TryGetValue("ControlCornerRadius", out var radius) && radius is CornerRadius corner ? corner : new CornerRadius(SmallRadius);
        Aliases(button, variant + ":" + color, ButtonAliases(variant, color));
        var block = Flag(node, "block");
        button.HorizontalAlignment = block ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Center;
        // Explicit OIU sizes win. Unspecified short-label keypad keys follow their column width.
        var keycap = size.Length == 0 && node.TypeName == "Button" && button.Content is string label && label.Length is > 0 and <= 2;
        (button.MinHeight, button.Padding, button.FontSize) = keycap ? (40d, new Thickness(0), 16d) : size switch
        {
            "sm" => (32d, new Thickness(8, 4, 8, 4), 12d),
            "lg" => (48d, new Thickness(16, 12, 16, 12), 14d),
            _ => (40d, new Thickness(11, 8, 11, 8), 14d),
        };
        if (!Keycaps.TryGetValue(button, out var sizing))
        {
            sizing = new(); Keycaps.Add(button, sizing);
            var owned = sizing; button.SizeChanged += (_, _) => SizeKeycap(button, owned);
        }
        sizing.Adaptive = keycap; SizeKeycap(button, sizing);
        if (node.TypeName == "IconButton")
        {
            button.MinWidth = button.MinHeight; button.Padding = new Thickness(0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        Typography.SetNumeralAlignment(button, FontNumeralAlignment.Tabular);
        if (button.Content is StackPanel panel)
        {
            panel.Spacing = size == "sm" ? 6 : 8; panel.VerticalAlignment = VerticalAlignment.Center;
            foreach (var icon in panel.Children.OfType<FontIcon>()) { icon.FontSize = size == "sm" ? 12 : 16; icon.VerticalAlignment = VerticalAlignment.Center; }
        }
    }
    private sealed class KeycapState { internal bool Adaptive; }
    private static void SizeKeycap(Button button, KeycapState state)
    {
        if (state.Adaptive && button.Parent is Grid && button.ActualWidth > 0)
            button.MinHeight = Math.Clamp(button.ActualWidth * 0.8, 40, 56);
    }
    private static (string Key, string Color, double Opacity)[] States(string prefix, string part, string rest, double restOpacity, string over, double overOpacity, string pressed, double pressedOpacity, string disabled)
        => new[] { (prefix + part, rest, restOpacity), (prefix + part + "PointerOver", over, overOpacity), (prefix + part + "Pressed", pressed, pressedOpacity), (prefix + part + "Disabled", disabled, 1d) };
    private static (string Key, string Color, double Opacity)[] ClearBorder(string prefix)
        => States(prefix, "BorderBrush", "SubtleFillColorTransparent", 1d, "SubtleFillColorTransparent", 1d, "SubtleFillColorTransparent", 1d, "SubtleFillColorTransparent");
    private static (string Key, string Color, double Opacity)[] ButtonAliases(string variant, string color)
    {
        var tone = color switch { "danger" => "SystemFillColorCritical", "success" => "SystemFillColorSuccess", "primary" => "@accentText", _ => "" };
        var ink = tone.Length > 0 ? tone : "TextFillColorPrimary";
        var pressedInk = tone.Length > 0 ? tone : "TextFillColorSecondary";
        var pressedInkOpacity = tone.Length > 0 ? 0.8 : 1d;
        switch (variant)
        {
            case "solid" when color is "danger" or "success":
                return States("AccentButton", "Background", tone, 1d, tone, 0.9, tone, 0.8, "AccentFillColorDisabled")
                    .Concat(States("AccentButton", "Foreground", "TextOnAccentFillColorPrimary", 1d, "TextOnAccentFillColorPrimary", 1d, "TextOnAccentFillColorSecondary", 1d, "TextOnAccentFillColorDisabled"))
                    .Concat(ClearBorder("AccentButton")).ToArray();
            case "outline" when tone.Length > 0:
                return States("Button", "Foreground", tone, 1d, tone, 1d, tone, 0.8, "TextFillColorDisabled");
            case "soft":
                (string Idle, string Over, string Pressed, double IdleOpacity, double OverOpacity, double PressedOpacity) fill = color switch
                {
                    "primary" => ("@accent", "@accent", "@accent", 0.12, 0.18, 0.08),
                    "danger" => ("SystemFillColorCriticalBackground", "SystemFillColorCritical", "SystemFillColorCritical", 1d, 0.14, 0.08),
                    "success" => ("SystemFillColorSuccessBackground", "SystemFillColorSuccess", "SystemFillColorSuccess", 1d, 0.14, 0.08),
                    // A translucent foreground overlays the card: lighter in dark, darker in light.
                    _ => ("TextFillColorPrimary", "TextFillColorPrimary", "TextFillColorPrimary", 0.07, 0.10, 0.05),
                };
                return States("Button", "Background", fill.Idle, fill.IdleOpacity, fill.Over, fill.OverOpacity, fill.Pressed, fill.PressedOpacity, "ControlAltFillColorDisabled")
                    .Concat(States("Button", "Foreground", ink, 1d, ink, 1d, pressedInk, pressedInkOpacity, "TextFillColorDisabled"))
                    .Concat(ClearBorder("Button")).ToArray();
            case "ghost":
                return States("Button", "Background", "SubtleFillColorTransparent", 1d, "SubtleFillColorSecondary", 1d, "SubtleFillColorTertiary", 1d, "SubtleFillColorTransparent")
                    .Concat(States("Button", "Foreground", ink, 1d, ink, 1d, pressedInk, pressedInkOpacity, "TextFillColorDisabled"))
                    .Concat(ClearBorder("Button")).ToArray();
            default: return Array.Empty<(string, string, double)>();
        }
    }
    /// <summary>
    /// Lightweight styling: per-element Light/Dark theme dictionaries of brushes over ThemeResource colours, so the stock
    /// template keeps its hover/press/disabled/focus states and follows live theme changes. HighContrast stays empty.
    /// </summary>
    private static void Aliases(FrameworkElement element, string signature, (string Key, string Color, double Opacity)[] aliases)
    {
        var known = AppliedAliases.TryGetValue(element, out var applied);
        if (known && applied == signature) return;
        AppliedAliases.AddOrUpdate(element, signature);
        if (aliases.Length == 0) { if (known) element.Resources = new ResourceDictionary(); return; }
        if (!AliasXaml.TryGetValue(signature, out var xaml))
        {
            string Body(bool light) => string.Concat(aliases.Select(alias => "<SolidColorBrush x:Key=\"" + alias.Key + "\" Color=\"{ThemeResource " + Color(alias.Color, light) + "}\""
                + (alias.Opacity < 1 ? " Opacity=\"" + alias.Opacity.ToString(CultureInfo.InvariantCulture) + "\"" : "") + " />"));
            AliasXaml[signature] = xaml = ThemeDictionaryXaml(Body(false), Body(true), "");
        }
        try { element.Resources = (ResourceDictionary)XamlReader.Load(xaml); }
        catch (Exception) { element.Resources = new ResourceDictionary(); }
    }
    private static string Color(string token, bool light) => token switch
    {
        "@accent" => light ? "SystemAccentColorDark1" : "SystemAccentColorLight2",
        "@accentText" => light ? "SystemAccentColorDark2" : "SystemAccentColorLight3",
        _ => token,
    };
    private static string ThemeDictionaryXaml(string dark, string light, string highContrast)
        => "<ResourceDictionary" + Xmlns + "><ResourceDictionary.ThemeDictionaries>"
            + "<ResourceDictionary x:Key=\"Default\">" + dark + "</ResourceDictionary>"
            + "<ResourceDictionary x:Key=\"Light\">" + light + "</ResourceDictionary>"
            + "<ResourceDictionary x:Key=\"HighContrast\">" + highContrast + "</ResourceDictionary>"
            + "</ResourceDictionary.ThemeDictionaries></ResourceDictionary>";

    // ---------------------------------------------------------------- data palette

    internal static void Table(TableView table, ElementTheme theme)
    {
        table.HeaderRowHeight = table.HeaderRowMinHeight = 32;
        table.RowHeight = table.RowMinHeight = 36;
        table.Margin = new Thickness(0);
        table.Background = table.AlternateRowBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        table.HeadersVisibility = TableViewHeadersVisibility.Columns;
        table.GridLinesVisibility = table.HeaderGridLinesVisibility = TableViewGridLinesVisibility.Horizontal;
        table.HorizontalGridLinesStroke = Brush("divider", theme: theme); table.HorizontalGridLinesStrokeThickness = 1;
        table.CellStyle = new Style(typeof(TableViewCell)) { Setters =
        {
            new Setter(Control.PaddingProperty, new Thickness(0)),
            new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center),
        } };
        table.ItemContainerStyle = new Style(typeof(TableViewRow)) { Setters =
        {
            new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
        } };
    }
    internal static Style TableHeader(bool first, bool last, bool numeric, ElementTheme theme) => new(typeof(TableViewColumnHeader)) { Setters =
    {
        new Setter(Control.FontSizeProperty, 12d), new Setter(Control.FontWeightProperty, FontWeights.SemiBold),
        new Setter(Control.ForegroundProperty, Brush("muted", theme: theme)),
        new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
        new Setter(Control.PaddingProperty, new Thickness(first ? 16 : 12, 0, last ? 16 : 12, 0)),
        new Setter(Control.HorizontalContentAlignmentProperty, numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left),
    } };

    /// <summary>Series colour c1…c6 (0-based index, wraps). c1 follows the system accent.</summary>
    internal static Windows.UI.Color ChartColor(int index, ElementTheme theme)
    {
        var slot = ((index % 6) + 6) % 6; var light = theme == ElementTheme.Light;
        if (slot == 0)
            return Application.Current.Resources.TryGetValue(Color("@accent", light), out var accent) && accent is Windows.UI.Color color ? color : Parse(light ? "#FF005FB8" : "#FF60CDFF");
        return Parse((light ? LightPalette : DarkPalette)[slot]);
    }
    private static Windows.UI.Color Parse(string hex) => Windows.UI.Color.FromArgb(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber), byte.Parse(hex.AsSpan(7, 2), NumberStyles.HexNumber));
    /// <summary>App-level theme brushes: NativeUiAccentTintBrush, NativeUi{Purple,Pink}[Tint]Brush, NativeUiChart1…6Brush.</summary>
    private static bool RegisterPalette()
    {
        try
        {
            string Brushes(bool light)
            {
                var palette = light ? LightPalette : DarkPalette; var accent = Color("@accent", light);
                var body = "<SolidColorBrush x:Key=\"NativeUiAccentTintBrush\" Color=\"{ThemeResource " + accent + "}\" Opacity=\"0.12\" />"
                    + "<SolidColorBrush x:Key=\"NativeUiSelectionTintBrush\" Color=\"{ThemeResource " + accent + "}\" Opacity=\"0.07\" />"
                    + "<SolidColorBrush x:Key=\"NativeUiChart1Brush\" Color=\"{ThemeResource " + accent + "}\" />";
                for (var i = 1; i < 6; i++) body += "<SolidColorBrush x:Key=\"NativeUiChart" + (i + 1) + "Brush\" Color=\"" + palette[i] + "\" />";
                return body + "<SolidColorBrush x:Key=\"NativeUiPurpleBrush\" Color=\"" + palette[3] + "\" /><SolidColorBrush x:Key=\"NativeUiPurpleTintBrush\" Color=\"" + palette[3] + "\" Opacity=\"0.14\" />"
                    + "<SolidColorBrush x:Key=\"NativeUiPinkBrush\" Color=\"" + palette[4] + "\" /><SolidColorBrush x:Key=\"NativeUiPinkTintBrush\" Color=\"" + palette[4] + "\" Opacity=\"0.14\" />";
            }
            var contrast = "<SolidColorBrush x:Key=\"NativeUiAccentTintBrush\" Color=\"{ThemeResource SystemColorWindowColor}\" /><SolidColorBrush x:Key=\"NativeUiSelectionTintBrush\" Color=\"{ThemeResource SystemColorWindowColor}\" />";
            for (var i = 1; i <= 6; i++) contrast += "<SolidColorBrush x:Key=\"NativeUiChart" + i + "Brush\" Color=\"{ThemeResource " + (i == 1 ? "SystemColorHighlightColor" : "SystemColorWindowTextColor") + "}\" />";
            contrast += "<SolidColorBrush x:Key=\"NativeUiPurpleBrush\" Color=\"{ThemeResource SystemColorWindowTextColor}\" /><SolidColorBrush x:Key=\"NativeUiPurpleTintBrush\" Color=\"{ThemeResource SystemColorWindowColor}\" />"
                + "<SolidColorBrush x:Key=\"NativeUiPinkBrush\" Color=\"{ThemeResource SystemColorWindowTextColor}\" /><SolidColorBrush x:Key=\"NativeUiPinkTintBrush\" Color=\"{ThemeResource SystemColorWindowColor}\" />";
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(ThemeDictionaryXaml(Brushes(false), Brushes(true), contrast)));
            return true;
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- form controls

    internal static void Select(ComboBox select) { select.HorizontalAlignment = HorizontalAlignment.Stretch; select.MinHeight = 32; }
    internal static void Input(TextBox input, NativeUiElement node)
    {
        input.HorizontalAlignment = HorizontalAlignment.Stretch; input.MinHeight = 32;
        var number = String(node, "type") == "number";
        if ((input.InputScope?.Names.FirstOrDefault()?.NameValue == InputScopeNameValue.Number) != number)
            input.InputScope = number ? new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } } : null;
        Typography.SetNumeralAlignment(input, number ? FontNumeralAlignment.Tabular : FontNumeralAlignment.Normal);
    }
    internal static void Segmented(Segmented control)
    {
        control.MinHeight = 32; control.Padding = new Thickness(2); control.CornerRadius = new CornerRadius(SmallRadius);
        Aliases(control, "segmented", new[]
        {
            ("SegmentedBackground", "ControlAltFillColorSecondary", 1d),
            ("SegmentedBorderBrush", "SubtleFillColorTransparent", 1d),
            ("SegmentedItemBackgroundSelected", "ControlFillColorDefault", 1d),
            ("SegmentedPillBackground", "@accent", 1d),
        });
        foreach (var item in control.Items.OfType<SegmentedItem>())
        { item.MinHeight = 28; item.Padding = new Thickness(8, 0, 8, 0); item.FontSize = 14; item.CornerRadius = new CornerRadius(SmallRadius); }
    }
    internal static void RadioCard(RadioButton radio)
    {
        var selected = radio.IsChecked == true;
        radio.Padding = new Thickness(12); radio.CornerRadius = new CornerRadius(SmallRadius); radio.BorderThickness = new Thickness(1);
        radio.Style = OpenCodeThemedXaml.BrushStyle("RadioButton", ("BorderBrush", selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"),
            ("Background", selected && Palette ? "NativeUiSelectionTintBrush" : "SubtleFillColorTransparentBrush"));
    }
    internal static void Checkbox(TextBlock text, bool checkedValue, NativeUiElement node)
        => text.TextDecorations = checkedValue && Flag(node, "lineThrough") ? TextDecorations.Strikethrough : TextDecorations.None;

    private sealed class SliderParts
    {
        internal readonly Grid Header = new() { ColumnSpacing = 12 };
        internal readonly TextBlock Label = new() { TextWrapping = TextWrapping.Wrap };
        internal readonly TextBlock Value = new() { HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.NoWrap };
        internal readonly Grid Marks = new() { Margin = new Thickness(0, -6, 0, 0), IsHitTestVisible = false };
        internal string Signature = "";
    }
    /// <summary>Wraps a slider with a label/value header row and optional mark labels under the track.</summary>
    internal static FrameworkElement SliderHost(Slider slider)
    {
        var parts = Sliders.GetValue(slider, _ => new SliderParts());
        parts.Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parts.Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(parts.Value, 1); parts.Header.Children.Add(parts.Label); parts.Header.Children.Add(parts.Value);
        AutomationProperties.SetAccessibilityView(parts.Header, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(parts.Marks, AccessibilityView.Raw);
        parts.Marks.SizeChanged += (_, _) => PlaceMarks(slider, parts);
        return new StackPanel { Children = { parts.Header, slider, parts.Marks } };
    }
    internal static void Slider(Slider slider, NativeUiElement node, string readout)
    {
        var parts = Sliders.GetValue(slider, _ => new SliderParts());
        var label = String(node, "label");
        slider.Header = null; slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        parts.Header.Visibility = label.Length > 0 || String(node, "unit").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        parts.Label.Text = label; parts.Value.Text = readout;
        parts.Label.Style = TextStyle("TextBlock", "BodyTextBlockStyle", "TextFillColorPrimaryBrush");
        parts.Value.Style = TextStyle("TextBlock", "BodyStrongTextBlockStyle", "TextFillColorPrimaryBrush");
        Typography.SetNumeralAlignment(parts.Value, FontNumeralAlignment.Tabular);
        var marks = (Prop(node, "marks") as IReadOnlyList<object?> ?? Array.Empty<object?>()).Take(12)
            .Select(mark => mark is IReadOnlyDictionary<string, object?> obj
                ? (Value: NativeUiValues.Number(obj.GetValueOrDefault("value")), Label: NativeUiValues.Text(obj.GetValueOrDefault("label") ?? obj.GetValueOrDefault("value")))
                : (Value: NativeUiValues.Number(mark), Label: NativeUiValues.Text(mark)))
            .Where(mark => double.IsFinite(mark.Value)).ToArray();
        var signature = string.Join("|", marks.Select(mark => mark.Value.ToString(CultureInfo.InvariantCulture) + "=" + mark.Label))
            + "@" + slider.Minimum.ToString(CultureInfo.InvariantCulture) + ".." + slider.Maximum.ToString(CultureInfo.InvariantCulture);
        if (signature == parts.Signature) return;
        parts.Signature = signature; parts.Marks.Children.Clear();
        foreach (var mark in marks)
        {
            var text = new TextBlock { Text = mark.Label, HorizontalAlignment = HorizontalAlignment.Left, TextWrapping = TextWrapping.NoWrap, Tag = mark.Value };
            text.Style = TextStyle("TextBlock", "CaptionTextBlockStyle", "TextFillColorTertiaryBrush");
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            parts.Marks.Children.Add(text);
        }
        parts.Marks.Visibility = marks.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceMarks(slider, parts);
    }
    private static void PlaceMarks(Slider slider, SliderParts parts)
    {
        var width = parts.Marks.ActualWidth; if (width <= 0) return;
        const double inset = 10; // half the thumb: values map onto the thumb centre's travel
        var range = slider.Maximum - slider.Minimum;
        foreach (var text in parts.Marks.Children.OfType<TextBlock>())
        {
            var fraction = range > 0 && text.Tag is double value ? Math.Clamp((value - slider.Minimum) / range, 0, 1) : 0;
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = Math.Clamp(inset + (width - 2 * inset) * fraction - text.DesiredSize.Width / 2, 0, Math.Max(0, width - text.DesiredSize.Width));
            text.Margin = new Thickness(left, 0, 0, 0);
        }
    }

    // ---------------------------------------------------------------- notices & placeholder

    /// <summary>
    /// Non-error: the inline "未支持：Type" chip. Error: a stock <see cref="InfoBar"/> at <see cref="InfoBarSeverity.Error"/>.
    /// </summary>
    internal static FrameworkElement Notice(string title, string message, bool error = false)
    {
        if (!error)
        {
            // Inline chip (e.g. "未支持：Type") inside a card.
            var chip = new TextBlock { Text = message.Length > 0 ? title + "：" + message : title }; Text(chip, new("Caption", new Dictionary<string, object?>()));
            return new Border
            {
                Child = chip, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(SmallRadius), HorizontalAlignment = HorizontalAlignment.Left,
                Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SubtleFillColorSecondaryBrush")),
            };
        }
        // Card-level failure. The stock severity control draws the icon, the critical surface and the high-contrast
        // colours, so no chrome is rebuilt here. The diagnostic can be 1,200 characters and must stay capped at three
        // lines, which InfoBar.Message cannot express: the text goes in Content, where it trims like the card message
        // did, while Title, the accessible name and the polite live setting stay on the bar itself.
        var details = new TextBlock { Text = message }; Text(details, new("Caption", new Dictionary<string, object?>()));
        details.MaxLines = 3; details.TextTrimming = TextTrimming.CharacterEllipsis;
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Error, IsClosable = false, IsIconVisible = true,
            Title = title, Content = details,
        };
        AutomationProperties.SetName(bar, title);
        AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);
        // InfoBar announces when it opens, and it only has a UIA peer once it is in the tree, so the bar is
        // opened on Loaded rather than in the initializer. Nothing is lost by starting closed: the notice is
        // always its host's only child, so a collapsed bar adds no spacing gap before it opens.
        bar.Loaded += (_, _) => bar.IsOpen = true;
        Fit(bar, wide: false); return bar;
    }
    /// <summary>Streaming state: the card surface with a progress ring, the tool title and a pulsing skeleton.</summary>
    internal static Border Placeholder(string? title)
    {
        var caption = string.IsNullOrWhiteSpace(title) ? "正在生成交互卡片…" : "正在生成「" + title.Trim() + "」…";
        var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center }; Text(heading, new("Text", new Dictionary<string, object?> { ["color"] = "muted" }));
        var bars = new StackPanel { Spacing = 10 };
        foreach (var fraction in new[] { 0.4, 1, 0.72 })
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
            if (fraction < 1) line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
            line.Children.Add(new Border { Height = 12, CornerRadius = new CornerRadius(SmallRadius), Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SubtleFillColorSecondaryBrush")) });
            bars.Children.Add(line);
        }
        AutomationProperties.SetAccessibilityView(bars, AccessibilityView.Raw);
        var hint = new TextBlock { Text = "输入完成后显示原生控件" }; Text(hint, new("Caption", new Dictionary<string, object?> { ["color"] = "faint" }));
        var border = new Border
        {
            Tag = PlaceholderTag,
            Child = new StackPanel { Spacing = 12, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { ring, heading } }, bars, hint } },
        };
        Frame(border); Fit(border, wide: false);
        AutomationProperties.SetName(border, caption);
        var pulse = new DoubleAnimation { From = 1, To = 0.55, Duration = TimeSpan.FromMilliseconds(800), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(pulse, bars); Storyboard.SetTargetProperty(pulse, "Opacity");
        var story = new Storyboard { Children = { pulse } };
        border.Loaded += (_, _) => { if (FluentMotion.Enabled) story.Begin(); };
        border.Unloaded += (_, _) => { story.Stop(); bars.Opacity = 1; };
        return border;
    }

    // ---------------------------------------------------------------- icons (lucide name → Segoe Fluent Icons)

    internal static FontIcon? Icon(string name)
        => NativeUiStyleMath.Glyph(name) is { } glyph ? new FontIcon { Glyph = glyph, FontSize = 16 } : null;
}
