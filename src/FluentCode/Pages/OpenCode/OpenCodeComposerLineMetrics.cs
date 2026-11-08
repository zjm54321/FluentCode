using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>Text-owned DIP geometry. Rasterization changes bitmap resolution, never inline layout size.</summary>
internal readonly record struct OpenCodeComposerLineMetrics(double Height, double Baseline)
{
    internal static OpenCodeComposerLineMetrics Measure(FontFamily font, double size, double scale)
    {
        var text = new TextBlock { Text = "Mg汉字", FontFamily = font, FontSize = OpenCodeComposerNativeGeometry.FontSize(size, scale),
            IsTextScaleFactorEnabled = false, TextWrapping = TextWrapping.NoWrap, TextLineBounds = TextLineBounds.Full };
        text.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var height = Math.Ceiling(text.DesiredSize.Height);
        OpenCodeComposerNativeGeometry.ImageSize(1, height, text.BaselineOffset);
        return new(height, text.BaselineOffset);
    }
}
