// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        // Percentages participate in the parent's native stretch layout, not CSS measurement.
        if (!height && text.EndsWith('%')) element.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}
