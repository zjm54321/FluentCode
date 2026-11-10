using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

internal static partial class Program
{
    // Source/resource contracts only: actual theme resolution and glyph rendering need the desktop pass.
    private static void PresentationResources()
    {
        var before = checks;
        var repo = SplashRepo();
        string Source(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var bitmap = Source("OpenCodeComposerBitmapCache.cs");
        Check(bitmap.Contains("FontFamily = (FontFamily)Application.Current.Resources[\"SymbolThemeFontFamily\"]", StringComparison.Ordinal)
            && !bitmap.Contains("Segoe Fluent Icons", StringComparison.Ordinal), "bitmap glyphs use the platform symbol font resource, not a machine-specific font literal");
        Check(bitmap.Contains("FontFamily = new FontFamily(key.Font)", StringComparison.Ordinal)
            && bitmap.Contains("editor.FontFamily.Source, editor.FontSize, textScale, scale, _colors", StringComparison.Ordinal),
            "atomic chip labels and bitmap cache retain editor font/size/text/raster scale identity");
        var view = Source("OpenCodeComposerView.cs");
        foreach (var element in new[] { "_mentionQueryText", "title", "where", "name", "h" })
            Check(view.Contains($"AppFonts.Apply({element}, AppFontSlot.Monospace);", StringComparison.Ordinal),
                "composer candidate font tracks the user monospace slot: " + element);
        var picker = Source("OpenCodePickerFlyout.cs");
        Check(picker.Contains("{ThemeResource AppBodyFontFamily}", StringComparison.Ordinal)
            && picker.Contains("{ThemeResource OverlayCornerRadius}", StringComparison.Ordinal),
            "explicit picker presenter follows body font while retaining its overlay radius");
        Check(picker.Contains("new Setter(Control.PaddingProperty, new Thickness(4))", StringComparison.Ordinal)
            && picker.Contains("new Setter(FrameworkElement.MinWidthProperty, 0d)", StringComparison.Ordinal)
            && picker.Contains("Math.Max(1, anchor.XamlRoot.Size.Width - 24)", StringComparison.Ordinal),
            "picker presenter extraction must preserve padding and owner-dependent width");
        var dialogs = Source("OpenCodeComposerNativeDialogs.cs");
        Check(dialogs.Contains("Style = OpenCodeInteractionUi.TryStyle(\"SubtleButtonStyle\")", StringComparison.Ordinal)
            && dialogs.Contains("Debug.Assert(favorite.Style is not null", StringComparison.Ordinal),
            "model favorite keeps its optional native subtle style with a Debug resolution assertion");
        Check(Source("OpenCodeContextMenu.cs").Contains("Style = OpenCodeInteractionUi.TryStyle(\"OpenCodeDividerSurfaceStyle\")", StringComparison.Ordinal),
            "context separator reuses the same-value divider resource");
        Check(Source("OpenCodeUserAttachmentsView.cs").Contains("Style = OpenCodeInteractionUi.TryStyle(\"OpenCodeSubtleSurfaceStyle\")", StringComparison.Ordinal),
            "noninteractive attachment chip reuses the same-value subtle surface resource");

        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var app = XDocument.Load(Path.Combine(repo, "src/FluentCode/App.xaml"));
        var cautionIcon = app.Descendants(xaml + "Style").Single(s => (string?)s.Attribute(x + "Key") == "OpenCodeCautionIconStyle");
        Check((string?)cautionIcon.Attribute("TargetType") == "FontIcon"
            && cautionIcon.Elements(xaml + "Setter").Count() == 1
            && (string?)cautionIcon.Element(xaml + "Setter")?.Attribute("Property") == "Foreground"
            && (string?)cautionIcon.Element(xaml + "Setter")?.Attribute("Value") == "{ThemeResource SystemFillColorCautionBrush}",
            "existing caution icon resource changes foreground only");
        var cautionText = app.Descendants(xaml + "Style").Single(s => (string?)s.Attribute(x + "Key") == "OpenCodeQuestionCautionTextStyle");
        Check((string?)cautionText.Attribute("TargetType") == "TextBlock"
            && (string?)cautionText.Attribute("BasedOn") == "{StaticResource CaptionTextBlockStyle}"
            && cautionText.Elements(xaml + "Setter").Count() == 1
            && (string?)cautionText.Element(xaml + "Setter")?.Attribute("Property") == "Foreground"
            && (string?)cautionText.Element(xaml + "Setter")?.Attribute("Value") == "{ThemeResource SystemFillColorCautionBrush}",
            "unavailable command caption preserves caption ramp and caution foreground");
        foreach (var (key, property, resource) in new[]
        {
            ("OpenCodeDividerSurfaceStyle", "Background", "DividerStrokeColorDefaultBrush"),
            ("OpenCodeSubtleSurfaceStyle", "Background", "SubtleFillColorSecondaryBrush"),
            ("OpenCodeCautionBadgeStyle", "Background", "SystemFillColorCautionBackgroundBrush"),
        })
        {
            var style = app.Descendants(xaml + "Style").Single(s => (string?)s.Attribute(x + "Key") == key);
            var setters = style.Elements(xaml + "Setter").ToArray();
            Check((string?)style.Attribute("TargetType") == "Border" && style.Attribute("BasedOn") is null
                && setters.Length == 1 && (string?)setters[0].Attribute("Property") == property
                && (string?)setters[0].Attribute("Value") == "{ThemeResource " + resource + "}",
                "same-value resource has no extra geometry or behavior: " + key);
        }
        Console.WriteLine($"PASS presentation resources: {checks - before} source/resource assertions; no native XAML resolution.");
    }
}
