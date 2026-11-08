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
            && !OpenCodeComposerNativeGeometry.IsRecoverable(new InvalidOperationException()), "native boundary catches only COM/argument failures");

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
        Console.WriteLine($"native composer geometry/paste boundaries: {checks - before} assertions");
    }
}
