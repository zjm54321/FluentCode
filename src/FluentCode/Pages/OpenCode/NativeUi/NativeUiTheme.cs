// SPDX-License-Identifier: GPL-3.0-or-later
// All slice styling lives here. @designer can replace this file without changing parser/runtime/contracts.
// Colors always come from fixed Fluent ThemeResource keys, never model CSS or arbitrary brush values.
using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using FontWeights = Microsoft.UI.Text.FontWeights;
using FontStyle = Windows.UI.Text.FontStyle;
using TextDecorations = Windows.UI.Text.TextDecorations;

namespace FluentCode.Pages.OpenCode;

internal static class NativeUiTheme
{
    internal const double MaximumWidth = 640;
    internal const double WrapCellWidth = 144;
    private static readonly Dictionary<string, Style?> TextStyles = new(StringComparer.Ordinal);
    internal static double Gap(object? value, double fallback = 3) => 4 * Math.Clamp(value is int integer ? integer : value is double n && double.IsFinite(n) ? n : fallback, 0, 16);
    private static double Number(string resource, double fallback) => Application.Current.Resources.TryGetValue(resource, out var value) && value is double number ? number : fallback;
    private static Style? Style(string name) => Application.Current.Resources.TryGetValue(name, out var style) ? style as Style : null;
    internal static void Host(FrameworkElement element) { element.MaxWidth = MaximumWidth; element.HorizontalAlignment = HorizontalAlignment.Stretch; }
    internal static object? Prop(NativeUiElement node, string name) => node.Props.GetValueOrDefault(name);
    internal static string String(NativeUiElement node, string name) => NativeUiValues.Text(Prop(node, name));
    internal static bool Flag(NativeUiElement node, string name) => Prop(node, name) is true;

    private static string Foreground(string token) => token switch
    {
        "muted" => "TextFillColorSecondaryBrush", "faint" => "TextFillColorTertiaryBrush", "success" => "SystemFillColorSuccessBrush",
        "warning" => "SystemFillColorCautionBrush", "danger" => "SystemFillColorCriticalBrush", "info" => "SystemFillColorAttentionBrush",
        "purple" or "pink" or "chart1" or "chart2" or "chart3" or "chart4" or "chart5" or "chart6" => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorPrimaryBrush",
    };
    internal static void Text(TextBlock text, NativeUiElement node)
    {
        var size = String(node, "size");
        var type = node.TypeName;
        var typography = type == "Title" ? size switch { "3xl" => "DisplayTextBlockStyle", "2xl" => "TitleLargeTextBlockStyle", "xl" => "TitleTextBlockStyle", "lg" => "SubtitleTextBlockStyle", _ => "BodyStrongTextBlockStyle" }
            : type == "Caption" || type == "Badge" ? "CaptionTextBlockStyle" : "BodyTextBlockStyle";
        var foreground = Foreground(String(node, "color") is { Length: > 0 } color ? color : type == "Caption" ? "muted" : "ink");
        var key = typography + ":" + foreground;
        if (!TextStyles.TryGetValue(key, out var style))
        {
            style = OpenCodeThemedXaml.Load<Style>("Style", "TargetType=\"TextBlock\" BasedOn=\"{StaticResource " + typography + "}\"",
                "<Setter Property=\"Foreground\" Value=\"{ThemeResource " + foreground + "}\" />");
            TextStyles[key] = style;
        }
        text.Style = style;
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = true;
    }
    internal static void RichText(RichTextBlock text, NativeUiElement node)
    {
        text.Style = OpenCodeThemedXaml.BrushStyle("RichTextBlock", ("Foreground", Foreground(String(node, "color"))));
        text.FontSize = String(node, "size") switch { "xs" or "sm" => Number("CaptionTextBlockFontSize", 12), "lg" => Number("SubtitleTextBlockFontSize", 20), _ => Number("TextControlThemeFontSize", 14) };
        text.FontWeight = String(node, "weight") switch { "medium" => FontWeights.Medium, "semibold" => FontWeights.SemiBold, _ => FontWeights.Normal };
        text.FontStyle = Flag(node, "italic") ? FontStyle.Italic : FontStyle.Normal;
        text.TextWrapping = TextWrapping.Wrap;
        text.IsTextSelectionEnabled = true;
        text.MaxLines = Prop(node, "maxLines") is double lines && double.IsFinite(lines) ? (int)Math.Clamp(lines, 0, 100) : 0;
    }
    internal static void Inline(Run run, NativeUiElement node) => run.TextDecorations = Flag(node, "lineThrough") ? TextDecorations.Strikethrough : TextDecorations.None;
    internal static void Card(Border border, StackPanel content, NativeUiElement node)
    {
        border.Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", String(node, "variant") == "soft" ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"), ("BorderBrush", "CardStrokeColorDefaultBrush"));
        border.BorderThickness = new Thickness(Number("CardBorderThemeThickness", 1));
        border.CornerRadius = Application.Current.Resources.TryGetValue("ControlCornerRadius", out var radius) && radius is CornerRadius corner ? corner : new CornerRadius(8);
        border.Padding = new Thickness(Gap(Prop(node, "padding"), 4));
        content.Spacing = Gap(Prop(node, "gap"));
    }
    internal static void Badge(Border border, NativeUiElement node)
    {
        var background = String(node, "color") switch { "success" => "SystemFillColorSuccessBackgroundBrush", "warning" => "SystemFillColorCautionBackgroundBrush", "danger" => "SystemFillColorCriticalBackgroundBrush", "info" => "SystemFillColorAttentionBackgroundBrush", _ => "SubtleFillColorSecondaryBrush" };
        border.Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", background));
        border.Padding = new Thickness(Gap(2), Gap(1), Gap(2), Gap(1)); border.CornerRadius = new CornerRadius(Number("OverlayCornerRadius", 8));
        border.HorizontalAlignment = HorizontalAlignment.Left; border.VerticalAlignment = VerticalAlignment.Center;
    }
    internal static void Button(Button button, NativeUiElement node)
    {
        button.Style = String(node, "variant") is "" or "solid" ? Style("AccentButtonStyle") : null;
        button.HorizontalAlignment = Flag(node, "block") ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        var vertical = Gap(String(node, "size") == "lg" ? 3d : 2d);
        button.Padding = new Thickness(Gap(3), vertical, Gap(3), vertical);
        button.MinHeight = Number("TextControlThemeMinHeight", 32);
    }
    internal static void Select(ComboBox select) => select.HorizontalAlignment = HorizontalAlignment.Stretch;
    internal static void Checkbox(TextBlock text, bool checkedValue, NativeUiElement node)
        => text.TextDecorations = checkedValue && Flag(node, "lineThrough") ? TextDecorations.Strikethrough : TextDecorations.None;
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
        var columns = Flag(node, "wrap") && panel.ActualWidth > 0 ? Math.Max(1, Math.Min(count, (int)(panel.ActualWidth / WrapCellWidth))) : Math.Max(1, count);
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
    internal static Border Notice(string title, string message, bool error = false)
    {
        var panel = new StackPanel { Spacing = Gap(1) };
        var heading = new TextBlock { Text = title }; Text(heading, new("Title", new Dictionary<string, object?>()));
        var details = new TextBlock { Text = message }; Text(details, new("Caption", new Dictionary<string, object?> { ["color"] = error ? "danger" : "muted" }));
        panel.Children.Add(heading); panel.Children.Add(details);
        var border = new Border { Child = panel }; Card(border, panel, new("Card", new Dictionary<string, object?> { ["padding"] = 3d })); return border;
    }
    internal static FontIcon? Icon(string name)
    {
        var glyph = name switch { "plus" => "\uE710", "check" => "\uE73E", "send" => "\uE724", "calculator" => "\uE8EF", "arrow-up-right" => "\uE8A7", "clock" => "\uE823", "rotate-ccw" => "\uE72C", _ => null };
        return glyph is null ? null : new FontIcon { Glyph = glyph, FontSize = Number("TextControlThemeFontSize", 14) };
    }
}
