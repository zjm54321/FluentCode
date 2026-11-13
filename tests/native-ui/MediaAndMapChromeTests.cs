// SPDX-License-Identifier: GPL-3.0-or-later
// Lane N2 (media + maps) focused checks. WinUI-free on purpose: it runs under tests/native-ui/run.ps1 with
// /warnaserror+, so it can only use the WinUI-free production sources, Mapsui/BruTile and the reference pack.
// Map chrome is exercised through the real library types; the media/gallery chrome is checked as renderer rules
// because MediaPlayerElement, FlipView and PipsPager all need a XAML tree that this harness deliberately has not.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Manipulations;
using Mapsui.Rendering;
using Mapsui.Widgets;
using Mapsui.Widgets.ButtonWidgets;

internal static class MediaAndMapChromeTests
{
    internal static async Task Run(string repo, Action<bool, string> require)
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; require(condition, message); }

        // ---------------------------------------------------------------- C29: the library's own zoom widget
        // The +/- control is Mapsui's ZoomInOutWidget, which draws itself into the viewport and claims its own taps.
        // This exercises the real widget against the real navigator, without opening a window.
        using (var map = new Map())
        {
            map.Navigator.SetSize(800, 320);
            map.Navigator.ZoomTo(1000);
            Check(map.Navigator.Viewport.Resolution == 1000, "Map navigator starts at the resolution the test set");

            var zoom = new ZoomInOutWidget
            {
                Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new MRect(8), Size = 40,
            };
            map.Widgets.Add(zoom);
            Check(map.GetWidgetsOfMapAndLayers().Contains(zoom), "The map renders its own zoom widget without any XAML overlay");

            // Vertical layout: the renderer reserves Size by (2 * Size - stroke) and honours alignment and margin.
            zoom.UpdateEnvelope(40, 77, 800, 320);
            var envelope = zoom.Envelope ?? throw new InvalidOperationException("Zoom widget has no envelope");
            Check(envelope.MinX == 752 && envelope.MinY == 8 && envelope.MaxX == 792 && envelope.MaxY == 85,
                "Zoom widget sits 8px inside the top-right corner: " + envelope.MinX + "," + envelope.MinY + "," + envelope.MaxX + "," + envelope.MaxY);
            Check(envelope.MaxY < 160 && envelope.MaxX > 400,
                "Zoom widget stays in the top band, clear of the bottom-right attribution chip");

            var inside = new ScreenPosition(772, 40);
            var plus = new ScreenPosition(772, 40);
            var minus = new ScreenPosition(772, 70);
            Check(WidgetInput.GetWidgetsAtPosition(inside, map).Contains(zoom), "A tap inside the zoom widget envelope reaches the widget");
            Check(!WidgetInput.GetWidgetsAtPosition(new ScreenPosition(100, 200), map).Contains(zoom), "A tap elsewhere on the map does not reach the zoom widget");

            bool ZoomTap(ScreenPosition position)
            {
                MapInfo Info(ScreenPosition at, IEnumerable<ILayer> layers) => new(at, new MPoint(0, 0), map.Navigator.Viewport.Resolution);
                Task<MapInfo> Remote(ScreenPosition at, Viewport viewport, IEnumerable<ILayer> layers) => Task.FromResult(Info(at, layers));
                var args = new WidgetEventArgs(position, new MPoint(0, 0), GestureType.SingleTap, map, false,
                    (GetMapInfoDelegate)Info, (GetRemoteMapInfoAsyncDelegate)Remote);
                zoom.OnTapped(args);
                return args.Handled;
            }

            Check(ZoomTap(plus), "Tapping the upper half of the widget is handled by the widget itself");
            for (var i = 0; i < 400 && map.Navigator.UpdateAnimations(); i++) await Task.Delay(5);
            Check(Math.Abs(map.Navigator.Viewport.Resolution - 500) < 1e-9,
                "Zoom-in halves the resolution without a wheel or a pinch: " + map.Navigator.Viewport.Resolution.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Check(ZoomTap(minus), "Tapping the lower half of the widget is handled by the widget itself");
            for (var i = 0; i < 400 && map.Navigator.UpdateAnimations(); i++) await Task.Delay(5);
            Check(Math.Abs(map.Navigator.Viewport.Resolution - 1000) < 1e-9,
                "Zoom-out restores the previous resolution: " + map.Navigator.Viewport.Resolution.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The attribution widget the library draws for an enabled layer is a separate visible copy at the bottom right.
            var layer = new MemoryLayer { Name = "底图" };
            layer.Attribution.Text = "© OpenStreetMap contributors";
            map.Layers.Add(layer);
            var credit = map.GetWidgetsOfMapAndLayers().OfType<IWidget>().Where(widget => !ReferenceEquals(widget, zoom)).ToArray();
            Check(credit.Any(widget => widget is HyperlinkWidget { Text: "© OpenStreetMap contributors" }),
                "Mapsui exposes every enabled layer's attribution widget to the renderer as well as to the card overlay");
        }

        // ---------------------------------------------------------------- map colours: the rule, recomputed
        // Rule (docs/native-ui.md, "Map colours"): what is drawn on the tiles has fixed colours chosen against a
        // light map; what is drawn around the map follows the theme; high contrast takes the system colours.
        // The fixed colours are read out of the renderer and judged by arithmetic, so a later edit that makes a pin
        // or the order line too pale for a light map fails here whatever the colour is called.
        var mapSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Map.cs"));
        var paint = Regex.Matches(mapSource, @"private static readonly MapColor (\w+) = new\((\d+), (\d+), (\d+)\);")
            .ToDictionary(match => match.Groups[1].Value, match => (R: int.Parse(match.Groups[2].Value), G: int.Parse(match.Groups[3].Value), B: int.Parse(match.Groups[4].Value)));
        Check(paint.Count == 4 && paint.ContainsKey("MapPinColor") && paint.ContainsKey("MapChosenPinColor") && paint.ContainsKey("MapEdgeColor") && paint.ContainsKey("MapLabelInkColor"),
            "The renderer declares exactly the four fixed on-map colours: " + string.Join(", ", paint.Keys));
        // WCAG 2 relative luminance and contrast ratio.
        static double Channel(int value) { var c = value / 255d; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance((int R, int G, int B) c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
        { var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b))); return (hi + 0.05) / (lo + 0.05); }
        static string Ratio(double value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        // OpenStreetMap Carto, the default provider's style (gravitystorm/openstreetmap-carto, master): @land-color and
        // @water-color in style/style.mss, @park and @forest in style/landcover.mss. The other providers' default
        // styles are light maps of the same kind (pale land, pale blue water), which is the assumption behind the rule.
        var tiles = new (string Name, (int R, int G, int B) Colour)[] { ("land", (242, 239, 233)), ("water", (170, 211, 223)), ("park", (200, 250, 204)), ("forest", (173, 209, 158)) };
        var pin = paint["MapPinColor"]; var chosenPin = paint["MapChosenPinColor"]; var mapEdge = paint["MapEdgeColor"]; var labelInk = paint["MapLabelInkColor"];
        foreach (var (name, colour) in tiles)
        {
            Check(Contrast(pin, colour) >= 3, "A pin and the order line reach 3:1 on " + name + ": " + Ratio(Contrast(pin, colour)));
            Check(Contrast(chosenPin, colour) >= 3, "The chosen pin reaches 3:1 on " + name + ": " + Ratio(Contrast(chosenPin, colour)));
            Check(Contrast(labelInk, colour) >= 7, "Label ink would still read at 7:1 directly on " + name + ": " + Ratio(Contrast(labelInk, colour)));
        }
        Check(Contrast(pin, tiles[0].Colour) >= 4.5 && Contrast(chosenPin, tiles[0].Colour) >= 4.5,
            "Both pin colours reach 4.5:1 on land, where most pins sit: " + Ratio(Contrast(pin, tiles[0].Colour)) + " / " + Ratio(Contrast(chosenPin, tiles[0].Colour)));
        Check(Contrast(mapEdge, pin) >= 4.5 && Contrast(mapEdge, chosenPin) >= 4.5,
            "The pin outline and the line casing separate from both fills: " + Ratio(Contrast(mapEdge, pin)) + " / " + Ratio(Contrast(mapEdge, chosenPin)));
        Check(Contrast(labelInk, mapEdge) >= 7, "Label text on its plate: " + Ratio(Contrast(labelInk, mapEdge)));
        Check(pin.B > 2 * pin.R && chosenPin.R > 2 * chosenPin.B,
            "The chosen pin is the warm one among cool ones, a pair that red-green colour blindness does not merge");
        // The thresholds above are the ones the theme-following pins failed: the dark theme's default accent
        // (SystemAccentColorLight2, #60CDFF) on the same tiles.
        Check(Contrast((96, 205, 255), tiles[0].Colour) < 3 && Contrast((96, 205, 255), tiles[1].Colour) < 3,
            "A dark-theme accent on a light map is what these checks reject: " + Ratio(Contrast((96, 205, 255), tiles[0].Colour)) + " / " + Ratio(Contrast((96, 205, 255), tiles[1].Colour)));

        // The chosen pin is drawn on top of its neighbours, and every pin under its own label. Both rest on the order
        // in which Mapsui visits features and styles, so that order is exercised here against the locked library: a
        // MemoryLayer yields features by Id (construction order), not by list order, and a feature's styles in order.
        {
            var first = new PointFeature(0, 0); var second = new PointFeature(1, 1);
            var under = new Mapsui.Styles.VectorStyle(); var over = new Mapsui.Styles.VectorStyle(); var only = new Mapsui.Styles.SymbolStyle();
            first.Styles.Add(only); second.Styles.Add(under); second.Styles.Add(over);
            var order = new MemoryLayer { Name = "顺序", Style = null, Features = new IFeature[] { second, first } };
            var visited = new List<(IFeature Feature, Mapsui.Styles.IStyle Style)>();
            VisibleFeatureIterator.IterateLayers(new Viewport(0, 0, 1, 0, 200, 200), new ILayer[] { order }, 0, (_, _, style, feature, _, _) => visited.Add((feature, style)));
            Check(visited.Count == 3 && ReferenceEquals(visited[0].Feature, first) && ReferenceEquals(visited[1].Style, under) && ReferenceEquals(visited[2].Style, over),
                "Mapsui draws the feature built last on top and a feature's styles in order (" + visited.Count + " visits)");
        }

        // ---------------------------------------------------------------- renderer rules (source text)
        Check(mapSource.Contains("zoom = new ZoomInOutWidget") && mapSource.Contains("map.Widgets.Add(zoom)"), "Map chrome uses Mapsui's own zoom widget instead of a hand-built pair of buttons");
        // Zoom buttons are around the map, so they follow the theme, with an opaque fill and a frame that shows on
        // any tile. Mapsui's renderer multiplies each colour's alpha by the widget's Opacity, 0.8 unless set.
        Check(Math.Abs(new ZoomInOutWidget().Opacity - 0.8) < 1e-6, "Mapsui's zoom widget is translucent unless its opacity is set");
        Check(Regex.IsMatch(mapSource, @"zoom = new ZoomInOutWidget \{[^}]*\bOpacity = 1 \}"), "The zoom buttons are drawn opaque");
        Check(mapSource.Contains("var fill = Flatten(MapPaint(\"canvas\", true), MapEdgeColor);")
            && mapSource.Contains("zoom.BackColor = fill; zoom.StrokeColor = Flatten(MapPaint(\"muted\"), fill); zoom.TextColor = Flatten(MapPaint(\"ink\"), fill);"),
            "The zoom buttons are repainted from the theme on every re-render: page colour fill, text colours flattened onto it for the frame and the signs");
        // The figures behind that choice, from the values of those tokens (microsoft/microsoft-ui-xaml, branch
        // winui3/release/2.6.5, src/controls/dev/CommonStyles/Common_themeresources_any.xaml): SolidBackgroundFillColorBase
        // #F3F3F3 / #202020 and TextFillColorSecondary #9E000000 / #C5FFFFFF. This is arithmetic on documented values,
        // not a rendering: it shows the rule can hold, and what the hairline divider colour it replaces came to.
        static (int R, int G, int B) Over((int R, int G, int B) top, double alpha, (int R, int G, int B) under)
        { int Mix(int a, int b) => (int)Math.Round(a * alpha + b * (1 - alpha)); return (Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B)); }
        var lightFill = (R: 243, G: 243, B: 243); var lightFrame = Over((0, 0, 0), 0x9E / 255d, lightFill);
        var darkFill = (R: 32, G: 32, B: 32); var darkFrame = Over((255, 255, 255), 0xC5 / 255d, darkFill);
        Check(Contrast(lightFrame, lightFill) >= 3 && tiles.All(tile => Contrast(lightFrame, tile.Colour) >= 3),
            "Light theme: the frame reaches 3:1 against the button and against every tile colour: " + Ratio(Contrast(lightFrame, lightFill)) + ", " + string.Join(" / ", tiles.Select(tile => Ratio(Contrast(lightFrame, tile.Colour)))));
        Check(Contrast(darkFrame, darkFill) >= 3 && tiles.All(tile => Contrast(darkFill, tile.Colour) >= 3),
            "Dark theme: the fill reaches 3:1 against every tile colour and the frame against the fill: " + string.Join(" / ", tiles.Select(tile => Ratio(Contrast(darkFill, tile.Colour)))) + ", " + Ratio(Contrast(darkFrame, darkFill)));
        var hairline = Over((0, 0, 0), 0x0F / 255d * 0.8, tiles[0].Colour);
        Check(Contrast(hairline, tiles[0].Colour) < 1.2 && Contrast(lightFill, tiles[0].Colour) < 1.2,
            "The divider colour at 0.8 opacity left a light button with no visible frame on land: " + Ratio(Contrast(hairline, tiles[0].Colour)) + " frame, " + Ratio(Contrast(lightFill, tiles[0].Colour)) + " fill");
        Check(mapSource.Contains("var pinFill = contrast ? Flatten(MapPaint(\"ink\"), edge) : MapPinColor;")
            && mapSource.Contains("var pinChosen = contrast ? Flatten(MapPaint(\"chart1\", true), edge) : MapChosenPinColor;")
            && mapSource.Contains("var edge = contrast ? Flatten(MapPaint(\"canvas\", true), MapEdgeColor) : MapEdgeColor;")
            && mapSource.Contains("var labelInk = contrast ? pinFill : MapLabelInkColor;"),
            "On the tiles the fixed colours are used; only high contrast replaces them, with the system text, highlight and window colours");
        Check(mapSource.Contains("var contrast = MapHighContrast();") && mapSource.Contains("new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast"),
            "High contrast is read from the system setting each time the map is painted");
        Check(!mapSource.Contains("MapPaint(\"chart2\"") && !mapSource.Contains("MapPaint(\"border\""),
            "Selection is not a second series colour, and nothing on the map is painted with the hairline divider colour");
        Check(mapSource.Contains("SymbolScale = chosen ? 1.3 : 1, Fill = new Mapsui.Styles.Brush(chosen ? pinChosen : pinFill), Outline = outline")
            && mapSource.Contains("foreach (var item in projected.OrderBy(place => S(place.Marker, \"label\") == selected))"),
            "The chosen pin is larger, has its own colour and is built last");
        Check(mapSource.Contains("new VectorStyle { Line = new Pen(pinFill, 3), Outline = new Pen(edge, 1.5) }"),
            "The order line is the pin colour with a casing in the edge colour");
        // The casing is the style's Outline, which only draws while it has a width; the library's default is 1 px grey.
        Check(new Mapsui.Styles.VectorStyle().Outline is { Width: 1.0 },
            "Mapsui's vector style carries an outline pen by default, which the line renderer draws around a line");
        Check(mapSource.Contains("new MapColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A)"),
            "Theme brushes are converted to Mapsui colours, including alpha, so high contrast keeps the system colours");
        Check(mapSource.Contains("private MapColor MapPaint(") && mapSource.Contains("ComponentBrush(token, background)"),
            "Map colours are resolved from the same brush helper every other native card component uses");
        Check(mapSource.Contains("FontFamily = NativeUiTheme.MapLabelFont"), "Map labels keep the shared CJK-capable font");
        // C30: neither attribution copy is removed until the on-screen duplicate check says which one is visible.
        Check(mapSource.Contains("public Attribution Attribution => new(NativeUiMapProviders.Attribution(context.Settings), \"\")")
            && mapSource.Contains("attribution.Text = NativeUiMapProviders.Attribution(context.Settings)"),
            "Both attribution copies (layer widget and card overlay) are still in place");
        Check(!mapSource.Contains("Microsoft.UI.Colors.White") && mapSource.Contains("SolidBackgroundFillColorBaseBrush"),
            "The attribution chip follows the theme instead of a hard-coded white background");
        Check(mapSource.Contains("baiduTiles = new(context); baiduTiles.Failed += TileFailed; source = baiduTiles;")
            && mapSource.Contains("baiduTiles?.Dispose(); baiduTiles = null;"),
            "The optional Baidu source keeps its own selection and disposal path");

        var mediaSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Media.cs"));
        // C33: stock media element and transport controls, click-to-load, in-card playback only.
        Check(mediaSource.Contains("new MediaTransportControls { IsCompact = false }") && mediaSource.Contains("AreTransportControlsEnabled = true")
            && mediaSource.Contains("new MediaPlayer { AutoPlay = false }") && mediaSource.Contains("AutoPlay = false, TransportControls = transport"),
            "Video/Audio keep the stock MediaPlayerElement with its own transport controls and no autoplay");
        Check(!Regex.IsMatch(mediaSource, @"\bIsFullWindow\s*="),
            "No code writes IsFullWindow, whose handling is compiled out in Windows App SDK 2.6.5");
        Check(!mediaSource.Contains("fullWindow") && !mediaSource.Contains("FullWindowLabel")
            && !mediaSource.Contains("Symbol.BackToWindow") && !mediaSource.Contains("Symbol.FullScreen")
            && !mediaSource.Contains("全窗口") && mediaSource.Contains("已加载 · 使用原生播放控件。"),
            "No full-window button, icon/label helper or unsupported full-window status sentence remains");
        Check(!mediaSource.Contains("IsFullWindowButtonVisible"),
            "No dependency on MediaTransportControls.IsFullWindowButtonVisible, which this WinUI version does not have");
        Check(mediaSource.Contains("load.Content = \"点击加载\"") && mediaSource.Contains("|| !gate.Request()) return;")
            && mediaSource.Contains("NativeUiMediaPolicy.Https(gate.Source)"),
            "Media still loads only after an explicit click on an allowed HTTPS source");
        Check(mediaSource.Contains("element.Unloaded -= OnUnloaded; Stop(); element.SetMediaPlayer(null); player.Dispose();"),
            "Media still detaches the player and the unload callback before disposal");
        Check(mediaSource.Contains("media?.Dispose(); media = null; stream?.Dispose(); stream = null;")
            && mediaSource.Contains("element.Visibility = Visibility.Collapsed;"),
            "Reloading or disposing still releases the media buffers and hides the in-card player");

        // C34: FlipView + PipsPager + zoomable full-size viewer.
        Check(mediaSource.Contains("new PipsPager") && mediaSource.Contains("pager.NumberOfPages = flip.Items.Count")
            && mediaSource.Contains("pager.SelectedPageIndex") && mediaSource.Contains("pager.SelectedIndexChanged"),
            "The gallery pages with the stock PipsPager bound to the FlipView's realized image count");
        Check(mediaSource.Contains("flip.SelectionChanged") && mediaSource.Contains("navigating"),
            "FlipView and PipsPager mirror each other through one guarded path instead of recursing");
        Check(mediaSource.Contains("pager.Visibility = flip.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed"),
            "A single-image gallery does not show a pager that carries no information");
        Check(mediaSource.Contains("ZoomMode = ZoomMode.Enabled") && mediaSource.Contains("MinZoomFactor") && mediaSource.Contains("MaxZoomFactor"),
            "The full-size viewer zooms with Ctrl+wheel or pinch, bounded so the image cannot be lost");
        Check(mediaSource.Contains("Stretch = Stretch.None") && mediaSource.Contains("new ContentDialog")
            && mediaSource.Contains("NativeUiAirspace.Begin(panel.XamlRoot?.Content, snapshot: true)")
            && mediaSource.Contains("finally { airspace?.Dispose(); viewer = null; }") && !mediaSource.Contains("SuppressAirspace("),
            "The full-size viewer keeps the original image size and modal dialog, with shared airspace released in finally");
        Check(mediaSource.Contains("new ScrollViewer") && mediaSource.Contains("HorizontalScrollMode = ScrollMode.Enabled"),
            "The full-size viewer still scrolls in both directions");
        Check(mediaSource.Contains("NativeUiMediaPolicy.Https(NativeUiValues.Text(item.GetValueOrDefault(\"src\"))")
            && mediaSource.Contains(".Take(12)"),
            "Gallery sources are still validated per image and capped at twelve");

        Console.WriteLine($"PASS lane N2 media/map chrome: Mapsui zoom widget, fixed on-map colours for light tiles, PipsPager and viewer zoom; {checks} checks");
    }
}
