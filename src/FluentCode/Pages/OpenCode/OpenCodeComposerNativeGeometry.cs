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

    internal static bool IsRecoverable(Exception error) => error is COMException or ArgumentException or UnauthorizedAccessException;

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
        // A product that is a whole number apart from floating-point noise is that whole number.
        var pixelsWide = Math.Ceiling(width * scale - 1e-6);
        var pixelsHigh = Math.Ceiling(height * scale - 1e-6);
        Positive(pixelsWide, MaxRasterPixels, nameof(width));
        Positive(pixelsHigh, MaxRasterPixels, nameof(height));
        return ((int)pixelsWide, (int)pixelsHigh);
    }

    /// <summary>The picture RichEdit gets for a chip, in the editor's units (whole DIPs) and in bitmap pixels.</summary>
    internal readonly record struct ChipRasterPlan(int Width, int Height, int Ascent, int PixelWidth, int PixelHeight, int BaselinePixel)
    {
        // The staged visual is laid out at exactly the bitmap's size, so its edges are whole device pixels.
        internal double LayoutWidth(double scale) => PixelWidth / scale;
        internal double LayoutHeight(double scale) => PixelHeight / scale;
        internal double LayoutBaseline(double scale) => BaselinePixel / scale;
    }

    // The smallest whole number of DIPs that is also a whole number of device pixels (4 at 125% and 175%, 2 at 150%).
    internal static int WholePixelStep(double scale)
    {
        Positive(scale, double.MaxValue, nameof(scale));
        for (var dips = 1; dips <= 20; dips++)
            if (Math.Abs(dips * scale - Math.Round(dips * scale)) < 1e-6) return dips;
        return 1;
    }

    /// <summary>
    /// One bitmap pixel per device pixel. RenderTargetBitmap.RenderAsync(element, scaledWidth, scaledHeight) takes DIPs and
    /// multiplies by the rasterization scale itself (microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5,
    /// src/dxaml/xcp/core/core/elements/RenderTargetElement.cpp, CRenderTargetElementData::UpdateMetrics), so the chip is
    /// rendered at its natural size and the scale is applied exactly once. InsertImage takes whole DIPs and RichEdit draws
    /// the picture into that rectangle times the scale (same tag, src/dxaml/xcp/core/native/text/Controls/HWRenderTarget.cpp,
    /// HWRenderTarget::DrawBitmap), so the width is rounded up to a DIP count that is a whole number of device pixels.
    /// The height stays the line height: the line box must not change.
    /// </summary>
    internal static ChipRasterPlan ChipRaster(double contentWidth, double lineHeight, double baseline, double scale)
    {
        var size = ImageSize(contentWidth, lineHeight, baseline);
        Positive(scale, double.MaxValue, nameof(scale));
        var step = WholePixelStep(scale);
        var width = (size.Width + step - 1) / step * step;
        if (width > MaxImageDip) width = size.Width;
        var pixels = RasterSize(width, size.Height, scale);
        var baselinePixel = Math.Min(pixels.Height, (int)Math.Round(size.Ascent * scale, MidpointRounding.AwayFromZero));
        return new(width, size.Height, size.Ascent, pixels.Width, pixels.Height, baselinePixel);
    }

    /// <summary>Top-left copy of a BGRA bitmap into the planned pixel size: never a resample, extra pixels are transparent.</summary>
    internal static byte[] FitPixels(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        Positive(sourceWidth, MaxRasterPixels, nameof(sourceWidth));
        Positive(sourceHeight, MaxRasterPixels, nameof(sourceHeight));
        Positive(width, MaxRasterPixels, nameof(width));
        Positive(height, MaxRasterPixels, nameof(height));
        if (source.Length != sourceWidth * sourceHeight * 4)
            throw new ArgumentException("Bitmap data must be four bytes per pixel.", nameof(source));
        if (sourceWidth == width && sourceHeight == height) return source;
        var result = new byte[width * height * 4];
        var bytes = Math.Min(sourceWidth, width) * 4;
        for (var row = 0; row < Math.Min(sourceHeight, height); row++)
            Buffer.BlockCopy(source, row * sourceWidth * 4, result, row * width * 4, bytes);
        return result;
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
