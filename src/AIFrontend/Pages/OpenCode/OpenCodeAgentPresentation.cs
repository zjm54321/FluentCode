using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

// Presentation only: no agent-ID palette/cache, catalog freshness, or selection authority.
internal static class OpenCodeAgentPresentation
{
    private static Style? _menuStyle;

    internal static Windows.UI.Color? Color(string? value)
        => value is { Length: 7 } && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            ? new Windows.UI.Color { A = 255, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb } : null;

    internal static void Apply(Control control, string? color)
    {
        if (control is MenuFlyoutItem)
        {
            _menuStyle ??= XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"MenuFlyoutItem\"><Setter Property=\"Foreground\" Value=\"{ThemeResource AccentTextFillColorPrimaryBrush}\"/></Style>") as Style;
            if (_menuStyle is not null) control.Style = _menuStyle;
        }
        control.ClearValue(Control.ForegroundProperty);
        if (Color(color) is { } rgb) control.Foreground = new SolidColorBrush(rgb);
    }

    internal static void Apply(TextBlock text, string? color)
    {
        OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text("AccentTextFillColorPrimaryBrush", OpenCodeInteractionUi.BodyStrongStyleKey));
        text.ClearValue(TextBlock.ForegroundProperty);
        if (Color(color) is { } rgb) text.Foreground = new SolidColorBrush(rgb);
    }

    internal static TextBlock Name(string name, double width = double.PositiveInfinity)
        => new() { Text = name, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            MaxLines = 0, MaxWidth = Math.Max(1, width), VerticalAlignment = VerticalAlignment.Center };

    internal static void WrapName(TextBlock text)
    { text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; text.MaxLines = 0; }

    // Preserve the native MenuFlyoutItem and its action/keyboard/template. Its generated name
    // presenter must wrap inside a finite budget rather than silently eliding the server name.
    internal static void WrapMenuName(DependencyObject root, string name, double width)
    {
        if (root is TextBlock text && string.Equals(text.Text, name, StringComparison.Ordinal))
        { WrapName(text); text.MaxWidth = Math.Max(1, width); }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            WrapMenuName(VisualTreeHelper.GetChild(root, i), name, width);
    }
}
