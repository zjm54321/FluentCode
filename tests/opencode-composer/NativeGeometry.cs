using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentCode.Pages.OpenCode;

internal static partial class Program
{
    private static void NativeGeometry()
    {
        var before = checks;
        void Reject(Action action, string note)
        {
            try { action(); Check(false, note); }
            catch (ArgumentException) { Check(true, note); }
        }

        Check(OpenCodeComposerNativeGeometry.ImageSize(100.2, 18.4, 14.7) == (101, 19, 15), "image dimensions round up before TOM integer conversion");
        Check(OpenCodeComposerNativeGeometry.ImageSize(.1, .1, 0) == (1, 1, 0), "positive sub-DIP images cannot truncate to zero");
        Check(OpenCodeComposerNativeGeometry.ImageSize(19, 19, 19) == (19, 19, 19), "ascent equal to height is valid");
        Check(OpenCodeComposerNativeGeometry.ImageSize(4096, 4096, 0) == (4096, 4096, 0), "image safety budget is inclusive");
        foreach (var bad in new[] { 0d, -1, double.NaN, double.NegativeInfinity, double.PositiveInfinity, double.MaxValue, 4096.001 })
        {
            Reject(() => OpenCodeComposerNativeGeometry.ImageSize(bad, 19, 15), "invalid image width rejected");
            Reject(() => OpenCodeComposerNativeGeometry.ImageSize(100, bad, 0), "invalid image height rejected");
        }
        foreach (var bad in new[] { -1d, double.NaN, double.NegativeInfinity, double.PositiveInfinity, double.MaxValue, 19.001 })
            Reject(() => OpenCodeComposerNativeGeometry.ImageSize(100, 19, bad), "invalid/over-height ascent rejected");
        Check(OpenCodeComposerNativeGeometry.RasterSize(100, 19, 1.5) == (150, 29), "DPI changes pixel resolution only");
        Check(OpenCodeComposerNativeGeometry.RasterSize(4096, 1, 1) == (4096, 1), "raster safety budget is inclusive");
        Reject(() => OpenCodeComposerNativeGeometry.RasterSize(4096, 1, 1.001), "oversized raster rejected");
        Reject(() => OpenCodeComposerNativeGeometry.RasterSize(100, 19, double.MaxValue), "raster product overflow rejected");
        foreach (var scale in new[] { 0d, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Reject(() => OpenCodeComposerNativeGeometry.RasterSize(100, 19, scale), "invalid DPI scale rejected");
            Reject(() => OpenCodeComposerNativeGeometry.ParagraphSpacing(19, scale), "invalid text scale rejected");
            Reject(() => OpenCodeComposerNativeGeometry.FontSize(14, scale), "invalid font scale rejected");
        }
        Check(OpenCodeComposerNativeGeometry.ParagraphSpacing(19, 1) == 14.25f, "DIP to TOM point conversion");
        Check(OpenCodeComposerNativeGeometry.ParagraphSpacing(29, 1.5) == 14.5f, "paragraph spacing removes accessibility scaling exactly once");
        Check(OpenCodeComposerNativeGeometry.ParagraphSpacing(60, 2.25) == 20f, "225 percent text scale stays bounded");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2, 2.25 })
        {
            var height = Math.Ceiling(19 * scale);
            var size = OpenCodeComposerNativeGeometry.ImageSize(100 * scale, height, 15 * scale);
            var points = OpenCodeComposerNativeGeometry.ParagraphSpacing(height, scale);
            Check(size.Width > 0 && size.Height > 0 && size.Ascent <= size.Height && float.IsFinite(points) && points > 0,
                "100-225 percent text metrics produce valid TOM image and spacing arguments");
        }
        foreach (var bad in new[] { 0d, -1, double.NaN, double.PositiveInfinity, double.MaxValue, 2185 })
            Reject(() => OpenCodeComposerNativeGeometry.ParagraphSpacing(bad, 1), "invalid/over-budget paragraph metric rejected");
        Reject(() => OpenCodeComposerNativeGeometry.ParagraphSpacing(19, double.Epsilon), "division overflow rejected");
        Reject(() => OpenCodeComposerNativeGeometry.ParagraphSpacing(double.Epsilon, 1), "zero-twip spacing rejected");
        Reject(() => OpenCodeComposerNativeGeometry.FontSize(14, double.MaxValue), "scaled font overflow rejected");
        Check(OpenCodeComposerNativeGeometry.IsRecoverable(new COMException("fixture", unchecked((int)0x80070057)))
            && OpenCodeComposerNativeGeometry.IsRecoverable(new ArgumentException())
            && OpenCodeComposerNativeGeometry.IsRecoverable(new UnauthorizedAccessException())
            && !OpenCodeComposerNativeGeometry.IsRecoverable(new InvalidOperationException()), "native boundary catches only COM/argument/access-denied failures");

        var link = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, "https://example.test/paste", "paste");
        var other = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention, "explorer", "Explorer");
        var document = OpenCodeComposerDocument.Text("AB").InsertToken(1, 0, link).InsertToken(2, 0, other);
        var plain = OpenCodeComposerNativeGeometry.PlainToken(document, link.Id, new(3, 1));
        Check(plain.Document.RawText == document.RawText && plain.Document.Objects.Count == 1
            && plain.Document.Objects[0].Token.Id == other.Id, "bad-chip fallback preserves raw text and unrelated token identity");
        Check(plain.Selection == new OpenCodeComposerSelection(3 + link.Value.Length - 1, 1), "bad-chip fallback rebases caret and reverse selection");
        Reject(() => OpenCodeComposerNativeGeometry.PlainToken(document, Guid.NewGuid(), new(1, 1)), "fallback cannot downgrade an unknown token");

        var source = File.ReadAllText(Path.Combine(SplashRepo(), "src/FluentCode/Pages/OpenCode/OpenCodeComposerEditor.cs"));
        var compact = source[source.IndexOf("internal double MeasureCompactContentHeight", StringComparison.Ordinal)..];
        Check(compact.Contains("new RichTextBlock") && compact.Contains("new Paragraph()")
            && compact.Contains("paragraph.Inlines.Add(new InlineUIContainer") && !compact.Contains("text.Inlines.Add"),
            "compact chip projection embeds UI only in RichTextBlock paragraphs, never TextBlock");
        Check(source.Contains("RequestPaste(DataPackageView? content = null)") && source.Contains("RequestPaste();"),
            "native fixture injection and keyboard/event paste share the same handler");
        Check(source.Contains("OpenCodeComposerNativeGeometry.IsRecoverable(error) && renderingToken is { } id")
            && source.Contains("OpenCodeComposerNativeGeometry.PlainToken(document, id, _selection)"),
            "native render COM/argument failures explicitly downgrade only the failing chip");
        ChipRaster();
        Console.WriteLine($"native composer geometry/paste boundaries: {checks - before} assertions");
    }

    // RenderTargetBitmap's pixel size for an element of elementDip DIPs: microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5,
    // src/dxaml/xcp/core/core/elements/RenderTargetElement.cpp, CRenderTargetElementData::UpdateMetrics. The requested size
    // is in DIPs (0 = natural size) and the rasterization scale is multiplied in afterwards.
    private static int RenderTargetPixels(double elementDip, int requestedDip, double scale)
    {
        var requestScale = requestedDip == 0 ? 1 : requestedDip / elementDip;
        return (int)Math.Ceiling(elementDip * requestScale * scale - 1e-4);
    }

    // The picture RichEdit keeps and XAML shows for an inserted image, per axis: one pixel per DIP of the box given to
    // InsertImage, whatever the PNG holds. WinUIEdit.dll (Windows App SDK 2.6.5) CImage::CheckSize sizes the bitmap to the
    // drawing rectangle times the render target's transform, and CImage::CreateBitmapSource reduces the decoded picture
    // to that size with a WIC Fant scaler; the XAML host's render target reports an identity transform and 96 DPI
    // (microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5, src/dxaml/xcp/core/native/text/Controls/HWRenderTarget.cpp,
    // HWRenderTarget::GetTransform and GetDpi) and XAML draws the picture at its natural size (same tag,
    // src/dxaml/xcp/core/native/text/Common/D2DTextDrawingContext.cpp, D2DTextDrawingContext::HwRenderImages).
    // Measured on 1.2611.22 at 125%: a 120x19 DIP chip is a 120x19 pixel picture magnified 1.25 times, linear filtering.
    private static int RichEditPicturePixels(int boxDip) => boxDip;

    private static void ChipRaster()
    {
        void Reject(Action action, string note)
        {
            try { action(); Check(false, note); }
            catch (ArgumentException) { Check(true, note); }
        }
        static bool Whole(double value) => Math.Abs(value - Math.Round(value)) < 1e-6;

        Check(OpenCodeComposerNativeGeometry.WholePixelStep(1) == 1 && OpenCodeComposerNativeGeometry.WholePixelStep(1.25) == 4
            && OpenCodeComposerNativeGeometry.WholePixelStep(1.5) == 2 && OpenCodeComposerNativeGeometry.WholePixelStep(1.75) == 4
            && OpenCodeComposerNativeGeometry.WholePixelStep(2) == 1 && OpenCodeComposerNativeGeometry.WholePixelStep(2.25) == 4
            && OpenCodeComposerNativeGeometry.WholePixelStep(1.1) == 10, "whole DIP counts that are whole device pixels");
        Check(OpenCodeComposerNativeGeometry.WholePixelStep(1.33) == 1, "a scale with no small whole-pixel step keeps whole DIPs");
        Check(OpenCodeComposerNativeGeometry.RasterSize(4, 20, 1.25) == (5, 25) && OpenCodeComposerNativeGeometry.RasterSize(150, 20, 1.1) == (165, 22),
            "floating-point noise never adds a pixel to a whole product");

        Console.WriteLine("chip picture per scale (content 121.3 DIP, line 19 DIP); only 100% is one to one on screen");
        Console.WriteLine("scale | box given to InsertImage, DIP | PNG, px | picture RichEdit keeps, px | PNG to picture | picture to screen, linear");
        foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
        {
            foreach (var content in new[] { 23.2, 121.3, 150, 271.9 })
            foreach (var line in new[] { 19d, 20, 24 })
            {
                var plan = OpenCodeComposerNativeGeometry.ChipRaster(content, line, 14.7, scale);
                var step = OpenCodeComposerNativeGeometry.WholePixelStep(scale);
                Check(plan.Height == (int)line && plan.Ascent == 15, "the picture keeps the line height and the rounded baseline");
                Check(plan.Width >= content && plan.Width - Math.Ceiling(content) < step && plan.Width % step == 0,
                    "the width grows by less than one whole-pixel step and never clips the chip");
                Check(plan.Width * scale == plan.PixelWidth, "the box is a whole number of device pixels wide, so the next chip starts on a whole pixel");
                Check(plan.PixelWidth >= RichEditPicturePixels(plan.Width) && plan.PixelHeight >= RichEditPicturePixels(plan.Height),
                    "the PNG holds at least the one pixel per DIP that RichEdit keeps, so RichEdit never has to enlarge it");
                Check(RenderTargetPixels(plan.LayoutWidth(scale), 0, scale) == plan.PixelWidth
                    && RenderTargetPixels(plan.LayoutHeight(scale), 0, scale) == plan.PixelHeight,
                    "the staged visual renders at natural size to exactly the planned pixels");
                Check(plan.PixelHeight == (int)Math.Ceiling(line * scale - 1e-6) && plan.PixelHeight - line * scale < 1,
                    "the bitmap covers the line height with less than one spare pixel row");
                if (Whole(line * scale)) Check(plan.Height * scale == plan.PixelHeight, "a whole-pixel line height gives a box of whole device pixels in height");
                Check(Whole(plan.LayoutBaseline(scale) * scale) && Math.Abs(plan.BaselinePixel - plan.Ascent * scale) <= .5
                    && plan.BaselinePixel <= plan.PixelHeight, "the label baseline is the nearest whole pixel row to the line's baseline");
                if (scale is 1 or 2)
                    Check(plan.Width == (int)Math.Ceiling(content) && plan.LayoutHeight(scale) == line && plan.LayoutBaseline(scale) == 15,
                        "whole scales keep the chip's box and baseline unchanged");
            }
            var shown = OpenCodeComposerNativeGeometry.ChipRaster(121.3, 19, 14.7, scale);
            var oldWidth = 122;
            var oldPixels = (Width: RenderTargetPixels(oldWidth, (int)Math.Ceiling(oldWidth * scale), scale),
                Height: RenderTargetPixels(19, (int)Math.Ceiling(19 * scale), scale));
            if (scale > 1)
                Check(oldPixels.Width > oldWidth * scale * 1.2 && RenderTargetPixels(oldWidth, 0, scale) - oldWidth * scale < 1,
                    "a requested size in pixels is multiplied by the scale again; the natural size is not");
            var kept = (Width: RichEditPicturePixels(shown.Width), Height: RichEditPicturePixels(shown.Height));
            Console.WriteLine(FormattableString.Invariant($"{scale:0.00} | {shown.Width}x{shown.Height} | {shown.PixelWidth}x{shown.PixelHeight} | {kept.Width}x{kept.Height} | {kept.Width / (double)shown.PixelWidth:0.000}x{kept.Height / (double)shown.PixelHeight:0.000} | x{scale:0.00}"));
        }
        var odd = OpenCodeComposerNativeGeometry.ChipRaster(121.3, 19, 14.7, 1.33);
        Check(odd.Width == 122 && odd.PixelWidth == 163 && odd.PixelHeight == 26, "an unusual scale still covers the picture with whole pixels");
        Check(OpenCodeComposerNativeGeometry.ChipRaster(4095.5, 19, 15, 1).Width == 4096, "the image budget stays inclusive");
        Reject(() => OpenCodeComposerNativeGeometry.ChipRaster(100, 19, 15, 0), "invalid chip scale rejected");
        Reject(() => OpenCodeComposerNativeGeometry.ChipRaster(100, 19, 20, 1.25), "chip baseline below the line rejected");
        Reject(() => OpenCodeComposerNativeGeometry.ChipRaster(4096, 19, 15, 1.25), "oversized chip raster rejected");

        // 3x2 source, one distinct value per pixel.
        var pixels = new byte[3 * 2 * 4];
        for (var index = 0; index < 6; index++) pixels[index * 4 + 3] = (byte)(index + 1);
        Check(ReferenceEquals(OpenCodeComposerNativeGeometry.FitPixels(pixels, 3, 2, 3, 2), pixels), "a bitmap of the planned size is used as rendered");
        var cropped = OpenCodeComposerNativeGeometry.FitPixels(pixels, 3, 2, 2, 1);
        Check(cropped.Length == 8 && cropped[3] == 1 && cropped[7] == 2, "a spare pixel column and row are cut off, not scaled away");
        var padded = OpenCodeComposerNativeGeometry.FitPixels(pixels, 3, 2, 4, 3);
        Check(padded.Length == 48 && padded[3] == 1 && padded[11] == 3 && padded[15] == 0 && padded[16 + 3] == 4 && padded[16 + 11] == 6
            && padded[32 + 3] == 0, "missing pixels stay transparent and every rendered pixel keeps its place");
        Reject(() => OpenCodeComposerNativeGeometry.FitPixels(new byte[5], 3, 2, 3, 2), "bitmap data of the wrong length rejected");
        Reject(() => OpenCodeComposerNativeGeometry.FitPixels(pixels, 3, 2, 0, 2), "empty target bitmap rejected");

        var cache = File.ReadAllText(Path.Combine(SplashRepo(), "src/FluentCode/Pages/OpenCode/OpenCodeComposerBitmapCache.cs"));
        Check(cache.Contains("await target.RenderAsync(visual);") && !cache.Contains("RenderAsync(visual,")
            && cache.Contains("OpenCodeComposerNativeGeometry.FitPixels(rendered, target.PixelWidth, target.PixelHeight, plan.PixelWidth, plan.PixelHeight)")
            && cache.Contains("new Bitmap(png, width, height, ascent, alt)") && cache.Contains("double width = plan.Width, height = plan.Height;"),
            "the cache renders the chip at natural size and inserts the planned whole-pixel box");
    }
}
