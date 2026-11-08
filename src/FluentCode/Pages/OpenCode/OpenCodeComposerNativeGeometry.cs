using System;
using System.Runtime.InteropServices;

namespace FluentCode.Pages.OpenCode;

/// <summary>Validated composer dimensions before conversion to TOM integers/points or bitmap pixels.</summary>
internal static class OpenCodeComposerNativeGeometry
{
    // Composer safety budgets, not advertised TOM maxima. They keep DIP/HIMETRIC conversions
    // inside Int32, paragraph spacing below 32,767 twips, and raster allocations bounded.
    internal const int MaxImageDip = 4096;
    internal const int MaxRasterPixels = 4096;
    internal const double MaxParagraphPoints = 1638;

    internal static bool IsRecoverable(Exception error) => error is COMException or ArgumentException;

    internal static (int Width, int Height, int Ascent) ImageSize(double width, double height, double ascent)
    {
        Positive(width, MaxImageDip, nameof(width));
        Positive(height, MaxImageDip, nameof(height));
        if (!double.IsFinite(ascent) || ascent < 0 || ascent > height)
            throw new ArgumentOutOfRangeException(nameof(ascent), "Image ascent must be finite and between zero and its height.");
        return ((int)Math.Ceiling(width), (int)Math.Ceiling(height), (int)Math.Round(ascent));
    }

    internal static (int Width, int Height) RasterSize(double width, double height, double scale)
    {
        Positive(scale, double.MaxValue, nameof(scale));
        Positive(width, MaxImageDip, nameof(width));
        Positive(height, MaxImageDip, nameof(height));
        var pixelsWide = Math.Ceiling(width * scale);
        var pixelsHigh = Math.Ceiling(height * scale);
        Positive(pixelsWide, MaxRasterPixels, nameof(width));
        Positive(pixelsHigh, MaxRasterPixels, nameof(height));
        return ((int)pixelsWide, (int)pixelsHigh);
    }

    internal static float ParagraphSpacing(double lineHeight, double textScale)
    {
        Positive(lineHeight, MaxImageDip, nameof(lineHeight));
        Positive(textScale, double.MaxValue, nameof(textScale));
        // XAML metrics are already text-scaled; RichEdit applies that scale itself.
        var points = lineHeight * (72d / 96) / textScale;
        Positive(points, MaxParagraphPoints, nameof(lineHeight));
        if (points < 1d / 20) throw new ArgumentOutOfRangeException(nameof(lineHeight), "Line spacing must be at least one twip.");
        return (float)points;
    }

    internal static double FontSize(double size, double scale)
    {
        Positive(size, MaxImageDip, nameof(size));
        Positive(scale, double.MaxValue, nameof(scale));
        var scaled = size * scale;
        Positive(scaled, MaxParagraphPoints * 96 / 72, nameof(size));
        return scaled;
    }

    private static void Positive(double value, double maximum, string name)
    {
        if (!double.IsFinite(value) || value <= 0 || value > maximum)
            throw new ArgumentOutOfRangeException(name, "Composer dimension must be finite, positive and within its native safety budget.");
    }

    internal static (OpenCodeComposerDocument Document, OpenCodeComposerSelection Selection) PlainToken(
        OpenCodeComposerDocument document, Guid id, OpenCodeComposerSelection selection)
    {
        foreach (var item in document.Objects)
        {
            if (item.Token.Id != id) continue;
            var text = document.Serialize(item.Position, 1).Text;
            var plain = document.Replace(item.Position, 1, text);
            int Offset(int position) => position <= item.Position ? position : position + text.Length - 1;
            return (plain, new OpenCodeComposerSelection(Offset(selection.Anchor), Offset(selection.Head)).Clamp(plain.Length));
        }
        throw new ArgumentException("The failing inline token must belong to the current document.", nameof(id));
    }
}
