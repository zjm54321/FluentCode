// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using FluentCode.Services.NativeUi;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentCode.Pages.OpenCode;

// Companion tokens/helpers only: the slice's theme and its fourteen visuals stay untouched.
internal static class NativeUiComponentTheme
{
    internal static double Number(NativeUiElement node, string prop, double fallback = 0)
        => NativeUiTheme.Prop(node, prop) is { } value && double.IsFinite(NativeUiValues.Number(value)) ? NativeUiValues.Number(value) : fallback;
    internal static TextBlock Text(string value, bool secondary = false)
    { var text = new TextBlock { Text = value }; NativeUiTheme.Text(text, new(secondary ? "Caption" : "Text", new Dictionary<string, object?>())); return text; }
    internal static Brush Brush(string token, bool background = false, ElementTheme theme = ElementTheme.Default)
    {
        if (token == "none") return new SolidColorBrush(Colors.Transparent);
        var resource = token switch
        {
            "muted" => "TextFillColorSecondaryBrush", "faint" => "TextFillColorTertiaryBrush", "surface" => "CardBackgroundFillColorDefaultBrush",
            "success" => background ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorSuccessBrush",
            "warning" => background ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorCautionBrush",
            "danger" => background ? "SystemFillColorCriticalBackgroundBrush" : "SystemFillColorCriticalBrush",
            "info" => background ? "SystemFillColorAttentionBackgroundBrush" : "SystemFillColorAttentionBrush",
            "purple" or "pink" or "chart1" or "chart2" or "chart3" or "chart4" or "chart5" or "chart6" => "AccentTextFillColorPrimaryBrush",
            _ => "TextFillColorPrimaryBrush",
        };
        if (token.StartsWith('#') && (token.Length == 7 || token.Length == 9) && uint.TryParse(token.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return new SolidColorBrush(Color.FromArgb(token.Length == 9 ? (byte)(hex >> 24) : (byte)255, (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex));
        // Resolve against the card's actual subtree theme, not the application's default theme.
        return OpenCodeThemedXaml.Load<Border>("Border", "RequestedTheme=\"" + theme + "\" Background=\"{ThemeResource " + resource + "}\"")?.Background
            ?? (Application.Current.Resources.TryGetValue(resource, out var value) && value is Brush brush ? brush : new SolidColorBrush(Colors.Gray));
    }
    internal static CornerRadius Radius(object? value) => new(value is double n && double.IsFinite(n) ? Math.Clamp(n, 0, 1000) : NativeUiValues.Text(value) switch { "sm" => 4, "lg" => 12, "xl" => 16, "full" => 1000, _ => 8 });
    internal static void Length(FrameworkElement element, object? value, bool height = false)
    {
        var text = NativeUiValues.Text(value);
        var numeric = value is double number ? number : text.EndsWith("px", StringComparison.Ordinal) && double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
        numeric = double.IsFinite(numeric) ? Math.Clamp(numeric, 0, 4096) : double.NaN;
        if (height) element.Height = numeric; else element.Width = numeric;
        // Percentages participate in the parent's native stretch layout, not CSS measurement.
        if (!height && text.EndsWith('%')) element.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
    internal static FontIcon Icon(string name, double size = 18) => NativeUiTheme.Icon(name) ?? new FontIcon
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size,
        Glyph = name switch { "map-pin" or "map" => "\uE707", "image" => "\uEB9F", "info" => "\uE946", "play" => "\uE768", "pause" => "\uE769", "search" => "\uE721", "star" => "\uE734", "heart" => "\uEB51", "copy" => "\uE8C8", "arrow-right" => "\uE72A", "arrow-left" => "\uE72B", "x" => "\uE711", "check-circle" => "\uE73E", "calendar" => "\uE787", "settings" => "\uE713", _ => "\uE946" },
    };
}
