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
using FluentCode.Models;
using FluentCode.Services.NativeUi;
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
        }

        // ---------------------------------------------------------------- C30: one credit per map, the card's chip
        // Showing the credit with the tiles is a licence condition of every provider. The card shows it once, in a
        // XAML chip, and switches the base layer's own widget off. The production helper that does so runs here
        // against the real library, for every provider in the enum, so one added later cannot arrive without a credit.
        var credits = new Dictionary<NativeUiMapProvider, string>
        {
            [NativeUiMapProvider.OpenStreetMap] = "© OpenStreetMap contributors",
            [NativeUiMapProvider.AMap] = "© 高德地图 / AMap",
            [NativeUiMapProvider.Baidu] = "© 百度地图",
            [NativeUiMapProvider.TianDiTu] = "© 天地图 · 国家地理信息公共服务平台",
            [NativeUiMapProvider.Custom] = "© 示例图源 Example Tiles",
        };
        foreach (var provider in Enum.GetValues<NativeUiMapProvider>())
        {
            var expected = credits.GetValueOrDefault(provider) ?? "";
            var settings = provider == NativeUiMapProvider.Custom
                ? new NativeUiMapSettings { Provider = provider, CustomTileUrlTemplate = "https://tiles.example/{z}/{x}/{y}.png", CustomAttribution = expected }
                : new NativeUiMapSettings { Provider = provider };
            var credit = NativeUiMapProviders.Attribution(settings);
            Check(expected.Length > 0 && settings.ValidationError() is null && credit == expected,
                "The chip's text for " + provider + " is that provider's credit: \"" + credit + "\"");

            using var creditMap = new Map();
            creditMap.Navigator.SetSize(800, 320);
            // A base layer as a TileLayer leaves its constructor: the tile source's credit copied into its widget.
            var baseLayer = new MemoryLayer { Name = "底图" };
            baseLayer.Attribution.Text = credit; baseLayer.Attribution.Url = "";
            creditMap.Layers.Add(baseLayer);
            // Where the library puts that widget once a frame has been drawn: bottom right, 2 px in.
            baseLayer.Attribution.UpdateEnvelope(170, 18, 800, 320);
            var corner = new ScreenPosition(790, 310);
            Check(WidgetInput.GetWidgetsAtPosition(corner, creditMap).Contains(baseLayer.Attribution),
                "Before the card steps in, the library's own credit is live in the bottom right corner for " + provider);

            var returned = NativeUiMapProviders.ShowCreditInCardOnly(baseLayer);
            // Enabled is what the Skia widget renderer skips on, and what the map filters its widget list by.
            Check(ReferenceEquals(returned, baseLayer) && !baseLayer.Attribution.Enabled
                && !creditMap.GetWidgetsOfMapAndLayers().OfType<HyperlinkWidget>().Any(),
                "The library has no credit of its own left to draw for " + provider);
            Check(!WidgetInput.GetWidgetsAtPosition(corner, creditMap).Any(),
                "Nothing of the library's answers a tap in that corner any more for " + provider);
            Check(baseLayer.Enabled && baseLayer.Attribution.Text == credit,
                "The layer still draws its tiles and still states whose they are for " + provider);
        }
        // Tiles and credit go together: a custom source without a credit is refused before a tile is asked for.
        {
            var uncredited = new NativeUiMapSettings { Provider = NativeUiMapProvider.Custom, CustomTileUrlTemplate = "https://tiles.example/{z}/{x}/{y}.png", CustomAttribution = " " };
            var refused = false;
            try { NativeUiMapProviders.TileUrl(uncredited, 2, 1, 3); } catch (InvalidOperationException) { refused = true; }
            Check(string.IsNullOrWhiteSpace(NativeUiMapProviders.Attribution(uncredited)) && uncredited.ValidationError() is not null && refused,
                "A custom tile source without a credit never gets a tile URL");
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
        // C30: the chip is the one credit on the map. Every tile layer the card creates goes through the helper
        // exercised above, and the chip is given the provider's text and joins the map in the same step.
        Check(mapSource.Split("new TileLayer(").Length >= 2
            && mapSource.Split("new TileLayer(").Length == mapSource.Split("NativeUiMapProviders.ShowCreditInCardOnly(new TileLayer(").Length,
            "Every tile layer the card creates has the library's own credit switched off");
        Check(mapSource.Contains("attribution.Text = NativeUiMapProviders.Attribution(context.Settings);")
            && mapSource.Contains("host.Children.Add(control); host.Children.Add(credit);"),
            "The chip carries the provider's credit and is laid over the map together with it");
        Check(!mapSource.Contains("Microsoft.UI.Colors.White") && mapSource.Contains("SolidBackgroundFillColorBaseBrush"),
            "The attribution chip follows the theme instead of a hard-coded white background");
        // Legible on the light tiles. The chip is the page colour with caption text in the secondary text colour
        // (the same two tokens as the zoom buttons' fill and frame above), and the whole chip is slightly see-through,
        // so both mix with whatever the map shows beneath. 4.5:1 is the WCAG AA figure for text of this size. Like
        // the figures above this is arithmetic on the tokens' documented values, not a rendering.
        var chip = Regex.Match(mapSource, @"var credit = new Border \{[^}]*\bOpacity = ([0-9.]+) \}");
        var chipOpacity = chip.Success ? double.Parse(chip.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        Check(chip.Success && mapSource.Contains("var attribution = NativeUiComponentTheme.Text(\"\", true);"),
            "The chip's opacity and its caption text style can be read from the renderer");
        // Beneath the chip: the four tile colours, then the darkest and the lightest thing a map can show.
        var beneath = tiles.Select(tile => tile.Colour).Append((0, 0, 0)).Append((255, 255, 255)).ToArray();
        foreach (var (theme, plate, ink) in new[] { ("light", lightFill, lightFrame), ("dark", darkFill, darkFrame) })
        {
            var ratios = beneath.Select(under => Contrast(Over(ink, chipOpacity, under), Over(plate, chipOpacity, under))).ToArray();
            Check(ratios.All(ratio => ratio >= 4.5),
                "The credit reads at 4.5:1 or better on its chip whatever the map shows beneath, " + theme + " theme: " + string.Join(" / ", ratios.Select(Ratio)));
        }
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

        // ---------------------------------------------------------------- C09: the carousel comes to rest on item edges
        // The card's scroller uses the platform's snap points, and a horizontal StackPanel reports the running total of
        // its children's desired widths as those points. The previous and next buttons page with the same totals, so
        // this arithmetic decides where a carousel stops. It has no XAML types, so it runs here.
        const double itemWidth = 240, itemGap = 12, slack = NativeUiCarouselSnap.Tolerance;
        static double[] Track(int count, double width, double gap) => Enumerable.Repeat(width + gap, count).ToArray();
        // The far end: everything the items take up, less the gap after the last one, less what the view shows.
        static double FarEnd(double[] extents, double gap, double viewport) => Math.Max(0, extents.Sum() - gap - viewport);
        // Press a button for as long as the card shows it (it hides a button within one unit of its end).
        static List<double> Forward(double[] extents, double gap, double viewport)
        {
            var end = FarEnd(extents, gap, viewport); var stops = new List<double> { 0 };
            while (end - stops[^1] > NativeUiCarouselSnap.Tolerance && stops.Count <= extents.Length + 2) stops.Add(NativeUiCarouselSnap.Next(extents, gap, stops[^1], viewport, end));
            return stops;
        }
        static List<double> Backward(double[] extents, double gap, double viewport)
        {
            var stops = new List<double> { FarEnd(extents, gap, viewport) };
            while (stops[^1] > NativeUiCarouselSnap.Tolerance && stops.Count <= extents.Length + 2) stops.Add(NativeUiCarouselSnap.Previous(extents, gap, stops[^1], viewport));
            return stops;
        }
        var six = Track(6, itemWidth, itemGap); var ten = Track(10, itemWidth, itemGap);
        Check(NativeUiCarouselSnap.Edges(six).SequenceEqual(new[] { 0d, 252, 504, 756, 1008, 1260 }),
            "An item's edge is the total of the items and gaps before it, so the gap has to be part of what each item takes up");
        Check(FarEnd(six, itemGap, 700) == 800 && Forward(six, itemGap, 700).SequenceEqual(new[] { 0d, 504, 800 }),
            "Next pages to the first item that was cut off, then to the far end, where the last item is completely in view");
        Check(Backward(six, itemGap, 700).SequenceEqual(new[] { 800d, 504, 0 }),
            "Previous returns over the same item edges, back to the start");
        Check(Forward(ten, itemGap, 700).SequenceEqual(new[] { 0d, 504, 1008, 1512, 1808 })
            && Backward(ten, itemGap, 700).SequenceEqual(new[] { 1808d, 1512, 1008, 504, 0 }),
            "A longer carousel pages a view at a time in both directions, always starting the view on an item");
        Check(Forward(Track(3, itemWidth, itemGap), itemGap, 100).SequenceEqual(new[] { 0d, 252, 504, 644 })
            && Backward(Track(3, itemWidth, itemGap), itemGap, 100).SequenceEqual(new[] { 644d, 504, 252, 0 }),
            "A view narrower than one item still moves, one item for each press");
        Check(NativeUiCarouselSnap.Next(ten, itemGap, 300, 700, 1808) == 1008 && NativeUiCarouselSnap.Previous(ten, itemGap, 300, 700) == 0
            && NativeUiCarouselSnap.Next(six, itemGap, 300, 700, 800) == 800,
            "From a position between items (a scroll bar drag, a pan that stopped short) the buttons go to an item edge or to the end");
        Check(NativeUiCarouselSnap.Next(six, itemGap, 504 - slack / 2, 700, 800) == 800 && NativeUiCarouselSnap.Previous(six, itemGap, 504 + slack / 2, 700) == 0,
            "An offset within the slack of an item edge counts as being on it, so a rounded offset does not cost an extra press");
        Check(NativeUiCarouselSnap.Edges(Array.Empty<double>()).Length == 0 && NativeUiCarouselSnap.Next(Array.Empty<double>(), itemGap, 0, 700, 0) == 0
            && NativeUiCarouselSnap.Previous(Array.Empty<double>(), itemGap, 0, 700) == 0,
            "An empty carousel has nowhere to go and does not throw");

        // The same rules for many shapes: every width the catalog allows, every gap, short and long tracks, narrow and
        // wide views, and tracks with collapsed items (no extent). Stated as what a user needs, not as the formula.
        var problems = new List<string>();
        var shapes = new List<(string Name, double[] Extents, double Gap)>();
        foreach (var width in new[] { 80d, 240, 333.3, 640 }) foreach (var gap in new[] { 0d, 12, 64 }) foreach (var count in new[] { 1, 2, 3, 7, 12 })
            shapes.Add(($"{count}x{width}+{gap}", Track(count, width, gap), gap));
        shapes.Add(("collapsed first, middle and last", new[] { 0d, 252, 252, 0, 252, 252, 252, 0 }, 12));
        shapes.Add(("mixed widths", new[] { 92d, 652, 252, 345.3, 132, 652, 92 }, 12));
        foreach (var (name, extents, gap) in shapes) foreach (var viewport in new[] { 60d, 300, 700, 1234.5 })
        {
            var edges = NativeUiCarouselSnap.Edges(extents); var end = FarEnd(extents, gap, viewport);
            bool Seen(int item, double stop) => edges[item] >= stop - slack && edges[item] + extents[item] - gap <= stop + viewport + slack;
            foreach (var (direction, stops) in new[] { ("next", Forward(extents, gap, viewport)), ("previous", Backward(extents, gap, viewport)) })
            {
                var scene = $"{name}, view {viewport}, {direction}";
                if (stops.Count > extents.Length + 1) problems.Add($"{scene}: {stops.Count - 1} presses for {extents.Length} items");
                for (var index = 1; index < stops.Count; index++)
                {
                    var moved = direction == "next" ? stops[index] - stops[index - 1] : stops[index - 1] - stops[index];
                    if (!(moved > slack)) problems.Add($"{scene}: press {index} moved {moved}");
                    if (stops[index] < 0 || stops[index] > end) problems.Add($"{scene}: press {index} left the track ({stops[index]})");
                    if (!edges.Contains(stops[index]) && stops[index] != end) problems.Add($"{scene}: press {index} stopped between items ({stops[index]})");
                }
                var last = direction == "next" ? end : 0;
                if (Math.Abs(stops[^1] - last) > slack) problems.Add($"{scene}: ended at {stops[^1]}, not {last}");
                for (var item = 0; item < extents.Length; item++)
                {
                    if (extents[item] <= 0) continue;
                    // An item that fits the view must be completely in view at some stop; a wider one must start a view.
                    var shown = extents[item] - gap <= viewport ? stops.Any(stop => Seen(item, stop)) : stops.Any(stop => Math.Abs(stop - edges[item]) <= slack);
                    if (!shown) problems.Add($"{scene}: item {item} is never shown");
                }
            }
            // From anywhere on the track, not only from the stops above.
            for (var offset = 0d; offset <= end; offset += 37.5)
            {
                var next = NativeUiCarouselSnap.Next(extents, gap, offset, viewport, end); var previous = NativeUiCarouselSnap.Previous(extents, gap, offset, viewport);
                if (end - offset > slack && !(next > offset + slack && next <= end && (edges.Contains(next) || next == end))) problems.Add($"{name}, view {viewport}: next from {offset} gave {next}");
                if (offset > slack && !(previous < offset - slack && previous >= 0 && edges.Contains(previous))) problems.Add($"{name}, view {viewport}: previous from {offset} gave {previous}");
            }
        }
        Check(problems.Count == 0, "For every item width, gap, length and view width: each press moves, stays on the track, stops on an item edge or at an end, "
            + "takes no more presses than there are items, and every item is shown" + (problems.Count == 0 ? "" : " -- " + string.Join("; ", problems.Take(4)) + $" ({problems.Count} in all)"));

        var componentsSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Components.cs"));
        var carouselSource = componentsSource[componentsSource.IndexOf("case \"Carousel\":", StringComparison.Ordinal)..componentsSource.IndexOf("case \"Popover\":", StringComparison.Ordinal)];
        Check(carouselSource.Contains("HorizontalSnapPointsType = SnapPointsType.Optional") && carouselSource.Contains("SnapPointsAlignment.Near")
            && carouselSource.Contains("new ScrollViewer { Content = track") && carouselSource.Contains("new StackPanel { Orientation = Orientation.Horizontal }"),
            "The carousel snaps with the stock ScrollViewer and StackPanel; the type is optional because a mandatory type pulls the next button's ChangeView back from the far end");
        Check(!carouselSource.Contains("track.Spacing") && carouselSource.Contains("own.Right + itemGap") && carouselSource.Contains("track.Margin = new Thickness(0, 0, -itemGap, 0)")
            && carouselSource.Contains("child.DesiredSize.Width"),
            "The gap is inside each item's margin, which the panel's snap points count, and not in StackPanel.Spacing, which they do not; the buttons read the same desired widths");
        Check(carouselSource.Contains("NativeUiCarouselSnap.Previous(Extents(), itemGap, scroll.HorizontalOffset, scroll.ViewportWidth)")
            && carouselSource.Contains("NativeUiCarouselSnap.Next(Extents(), itemGap, scroll.HorizontalOffset, scroll.ViewportWidth, scroll.ScrollableWidth)")
            && !carouselSource.Contains(".85"),
            "The previous and next buttons page to item edges instead of moving a fixed share of the view");
        Check(carouselSource.Contains("previous.Visibility = scroll.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed")
            && carouselSource.Contains("next.Visibility = scroll.ScrollableWidth - scroll.HorizontalOffset > 1 ? Visibility.Visible : Visibility.Collapsed")
            && carouselSource.Contains("SetName(previous, \"上一组\")") && carouselSource.Contains("SetName(next, \"下一组\")")
            && carouselSource.Contains("scroll.ViewChanged += (_, _) => Edges(); scroll.SizeChanged += (_, _) => Edges();"),
            "Each button is still a named stock Button that is shown only while there is something to page to on its side");

        // ---------------------------------------------------------------- C08: the Pressable tile is a stock Button
        // The tile's rules that need no XAML type run here: the hover and press colours, which input is the tile's
        // own, and the name a screen reader reads.
        static string Argb((byte A, byte R, byte G, byte B) colour) => $"#{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
        // The platform's subtle hover and press tints (microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5,
        // src/controls/dev/CommonStyles/Common_themeresources_any.xaml: SubtleFillColorSecondary / Tertiary).
        (byte A, byte R, byte G, byte B) lightHover = (0x09, 0, 0, 0), lightPress = (0x06, 0, 0, 0), darkHover = (0x0F, 255, 255, 255), darkPress = (0x0A, 255, 255, 255);
        (byte A, byte R, byte G, byte B) white = (255, 255, 255, 255), darkSurface = (255, 0x2C, 0x2C, 0x2C);
        Check(Argb(NativeUiPressable.Over(white, lightHover)) == "#FFF6F6F6" && Argb(NativeUiPressable.Over(white, lightPress)) == "#FFF9F9F9",
            "A white tile goes to F6F6F6 under the pointer and F9F9F9 while pressed: " + Argb(NativeUiPressable.Over(white, lightHover)) + " " + Argb(NativeUiPressable.Over(white, lightPress)));
        Check(Argb(NativeUiPressable.Over(darkSurface, darkHover)) == "#FF383838" && Argb(NativeUiPressable.Over(darkSurface, darkPress)) == "#FF343434",
            "A dark tile goes to 383838 under the pointer and 343434 while pressed: " + Argb(NativeUiPressable.Over(darkSurface, darkHover)) + " " + Argb(NativeUiPressable.Over(darkSurface, darkPress)));
        Check(NativeUiPressable.Over((255, 0xEF, 0xE6, 0xD3), (255, 240, 240, 240)) == ((byte)255, (byte)0xEF, (byte)0xE6, (byte)0xD3)
            && NativeUiPressable.Over((255, 0xEF, 0xE6, 0xD3), (0, 0, 0, 0)) == ((byte)255, (byte)0xEF, (byte)0xE6, (byte)0xD3),
            "A tint that would hide the tile's own colour (high contrast gives an opaque one) or that is empty leaves the fill alone, so the tile's text stays readable");
        var tintProblems = new List<string>();
        foreach (var fill in new (byte A, byte R, byte G, byte B)[] { white, darkSurface, (255, 0xEF, 0xE6, 0xD3), (255, 0x2F, 0x3E, 0x55), (255, 0, 0, 0), (0x0F, 255, 255, 255), (0x80, 0xC9, 0x78, 0x5A), (0, 0, 0, 0) })
            foreach (var tint in new[] { lightHover, lightPress, darkHover, darkPress })
            {
                var mixed = NativeUiPressable.Over(fill, tint);
                static bool Between(byte value, byte one, byte other) => value >= Math.Min(one, other) && value <= Math.Max(one, other);
                // The tile never gets more see-through, and every channel stays between the fill's and the tint's.
                if (mixed.A < fill.A || mixed.A < tint.A || !Between(mixed.R, fill.R, tint.R) || !Between(mixed.G, fill.G, tint.G) || !Between(mixed.B, fill.B, tint.B))
                    tintProblems.Add(Argb(fill) + " under " + Argb(tint) + " = " + Argb(mixed));
                // An opaque fill moves by a few steps at most: the tile keeps its own colour.
                if (fill.A == 255 && (mixed.A != 255 || Math.Abs(mixed.R - fill.R) > 15 || Math.Abs(mixed.G - fill.G) > 15 || Math.Abs(mixed.B - fill.B) > 15))
                    tintProblems.Add(Argb(fill) + " moved too far under " + Argb(tint) + ": " + Argb(mixed));
            }
        Check(tintProblems.Count == 0, "Hover and press colours stay between the tile's fill and the tint: " + string.Join("; ", tintProblems));
        Check(NativeUiPressable.Over(white, lightHover) != NativeUiPressable.Over(white, lightPress) && NativeUiPressable.Over(white, lightHover) != white
            && NativeUiPressable.Over(darkSurface, darkHover) != NativeUiPressable.Over(darkSurface, darkPress) && NativeUiPressable.Over(darkSurface, darkHover) != darkSurface,
            "Rest, hover and pressed are three different fills on the default surface in both themes");

        // When a tile works its colours out again. A card applies every tile on each update, and reading a theme
        // colour is a XAML parse, so an update that changes nothing the colours depend on must not ask for one.
        var memo = new NativeUiPressable.Memo(); var tintWork = 0; var textWork = 0;
        void Apply((byte A, byte R, byte G, byte B) fill, string pageTheme, string ownTheme, string contrast)
        {
            if (memo.NeedsTints(fill, ownTheme != "Default" ? ownTheme : pageTheme, contrast)) tintWork++;
            if (memo.NeedsText(pageTheme, ownTheme, contrast)) textWork++;
        }
        Apply(white, "Light", "Default", "");
        Check(tintWork == 1 && textWork == 1, "The first update of a tile works out its colours: " + tintWork + " / " + textWork);
        for (var update = 0; update < 100; update++) Apply(white, "Light", "Default", "");
        Check(tintWork == 1 && textWork == 1, "A hundred updates that change neither fill nor theme do no colour work: " + tintWork + " / " + textWork);
        Apply((255, 0xEF, 0xE6, 0xD3), "Light", "Default", "");
        Check(tintWork == 2 && textWork == 1, "A new fill needs new hover and press colours and nothing else: " + tintWork + " / " + textWork);
        Apply((255, 0xEF, 0xE6, 0xD3), "Dark", "Default", "");
        Check(tintWork == 3 && textWork == 2, "A theme switch needs both again, also when the fill is a raw colour that did not change: " + tintWork + " / " + textWork);
        Apply((255, 0xEF, 0xE6, 0xD3), "Dark", "Default", "High Contrast Black"); Apply((255, 0xEF, 0xE6, 0xD3), "Dark", "Default", "High Contrast Black");
        Check(tintWork == 4 && textWork == 3, "Turning high contrast on is noticed at the next update, once: " + tintWork + " / " + textWork);
        Apply((255, 0xEF, 0xE6, 0xD3), "Dark", "Default", "High Contrast White"); Apply((255, 0xEF, 0xE6, 0xD3), "Dark", "Default", "");
        Check(tintWork == 6 && textWork == 5, "So is another contrast scheme, and turning it off: " + tintWork + " / " + textWork);
        Apply((255, 0xEF, 0xE6, 0xD3), "Dark", "Light", ""); Apply((255, 0xEF, 0xE6, 0xD3), "Light", "Light", "");
        Check(tintWork == 7 && textWork == 7,
            "A tile with a theme of its own (raw-colour fill) keeps its tints when the page theme changes, but looks above itself again: " + tintWork + " / " + textWork);
        memo.Moved(); Apply((255, 0xEF, 0xE6, 0xD3), "Light", "Light", ""); Apply((255, 0xEF, 0xE6, 0xD3), "Light", "Light", "");
        Check(tintWork == 7 && textWork == 8, "A tile that entered a tree again takes its text colour and font from above once more, and keeps its tints: " + tintWork + " / " + textWork);
        Apply(white, "Light", "Light", ""); Apply((255, 0xEF, 0xE6, 0xD3), "Light", "Light", "");
        Check(tintWork == 9 && textWork == 8, "Going back to an earlier fill is a change too; nothing stale is kept: " + tintWork + " / " + textWork);
        var otherTile = new NativeUiPressable.Memo();
        Check(otherTile.NeedsTints((255, 0xEF, 0xE6, 0xD3), "Light", "") && otherTile.NeedsText("Light", "Light", ""), "What one tile remembers is not shared with another");

        // A press while the card runs an action. The tile stays enabled (disabling the focused control makes the
        // platform move keyboard focus away), so the press itself is what gets refused.
        var started = 0; var running = false;
        void Click(bool gone) { if (NativeUiPressable.Accepts(gone, running)) { started++; running = true; } }
        Click(false); Click(false); Click(false);
        Check(started == 1, "A second and a third press while the action runs start nothing: " + started);
        running = false; Click(false);
        Check(started == 2, "When the action has ended the same tile can be pressed again: " + started);
        running = false; Click(true);
        Check(started == 2 && !NativeUiPressable.Accepts(true, true) && NativeUiPressable.Accepts(false, false), "A tile that has been removed from the card starts nothing: " + started);
        var restFill = NativeUiPressable.Over(white, (0, 0, 0, 0)); var hoverFill = NativeUiPressable.Over(white, lightHover);
        Check(NativeUiPressable.StateFill(restFill, hoverFill, busy: true) == restFill && NativeUiPressable.StateFill(restFill, hoverFill, busy: false) == hoverFill && restFill != hoverFill,
            "While a press would be refused the tile does not answer the pointer: its hover and press fill is the rest fill, and the tint returns with the end of the action");

        // Which input is the tile's own. A small tree stands in for the visual tree: page > flashcard button > tile >
        // (text, row > button > label, slider).
        var page = new TreeNode("page", null, false); var outerButton = new TreeNode("flashcard", page, true); var tileNode = new TreeNode("tile", outerButton, true);
        var tileText = new TreeNode("text", tileNode, false); var tileRow = new TreeNode("row", tileNode, false);
        var innerButton = new TreeNode("button", tileRow, true); var innerLabel = new TreeNode("label", innerButton, false); var innerSlider = new TreeNode("slider", tileNode, true);
        var elsewhere = new TreeNode("elsewhere", page, false);
        bool Inside(TreeNode? source) => NativeUiPressable.InsideNested(source, tileNode, node => node.Parent, node => node.Interactive);
        Check(!Inside(tileNode) && !Inside(tileText) && !Inside(tileRow) && !Inside(null),
            "A press on the tile itself, on its text or on its layout presses the tile; that the tile sits inside another button does not matter");
        Check(Inside(innerButton) && Inside(innerLabel) && Inside(innerSlider),
            "A press on a control inside the tile, or on anything inside that control, is the control's and does not press the tile");
        Check(!Inside(elsewhere), "The walk ends at the top of the tree without claiming a control it never met");
        Check(NativeUiPressable.OwnKey(tileNode, tileNode) && !NativeUiPressable.OwnKey(innerButton, tileNode) && !NativeUiPressable.OwnKey(tileText, tileNode) && !NativeUiPressable.OwnKey(null, tileNode),
            "Enter and Space press the tile only when the tile itself has the focus, not when the key comes from a control inside it");

        // The name a screen reader reads: the text the tile shows, in order, as displayed.
        static NativeUiElement El(string type, params (string Key, object? Value)[] props) => new(type, props.ToDictionary(prop => prop.Key, prop => prop.Value));
        static string Shown(string text) => string.Concat(FluentCode.Pages.OpenCode.NativeUiStyleMath.Inline(text).Select(span => span.Text));
        var shoppingRow = El("Pressable", ("children", new object?[] { El("Row", ("children", new object?[] {
            El("Icon", ("name", "circle")), El("Text", ("text", "**Bone-in** leg of lamb")), El("Spacer"), El("Text", ("text", "2.4 kg")) })) }));
        Check(NativeUiPressable.Name(shoppingRow, Shown) == "Bone-in leg of lamb 2.4 kg",
            "A tile is named by the text it shows, in reading order, without Markdown markers or the icon's name: " + NativeUiPressable.Name(shoppingRow, Shown));
        var dayTile = El("Pressable", ("children", new object?[] { El("Row", ("children", new object?[] {
            El("Box", ("children", new object?[] { El("Title", ("text", "1")) })),
            El("Col", ("children", new object?[] { El("Text", ("text", "Seattle")), El("Caption", ("text", "Arrive ·\n  overnight")), El("Badge", ("text", "Great match")) })) })) }));
        Check(NativeUiPressable.Name(dayTile, Shown) == "1 Seattle Arrive · overnight Great match",
            "Nested layout is read through and line breaks and runs of spaces become one space: " + NativeUiPressable.Name(dayTile, Shown));
        var mixedTile = El("Pressable", ("action", new Dictionary<string, object?>()), ("children", new object?[] {
            El("Image", ("src", "https://example.com/a.png"), ("alt", "Ferry Building")), "Plain words", 42d,
            El("Button", ("label", "Delete")), El("TextInput", ("label", "Note"), ("value", "typed")), El("Code", ("code", "rm -rf")),
            El("Text", ("text", new object?[] { "with ", El("Badge", ("text", "new")), " *part*" })) }));
        Check(NativeUiPressable.Name(mixedTile, Shown) == "Ferry Building Plain words 42 with new part",
            "A picture is named by its description; controls inside the tile keep their own names and code is not read as a name: " + NativeUiPressable.Name(mixedTile, Shown));
        Check(NativeUiPressable.Name(El("Pressable", ("children", new object?[] { El("Icon", ("name", "circle")), El("Svg") })), Shown) == NativeUiPressable.Unnamed
            && NativeUiPressable.Name(El("Pressable"), Shown) == NativeUiPressable.Unnamed && NativeUiPressable.Unnamed.Length > 0,
            "A tile that shows no text still has a name, so a screen reader does not announce a bare 'button'");
        var longName = NativeUiPressable.Name(El("Pressable", ("children", new object?[] { El("Text", ("text", string.Join(" ", Enumerable.Repeat("word", 200)))) })), Shown);
        var emojiName = NativeUiPressable.Name(El("Pressable", ("children", new object?[] { El("Title", ("text", new string('a', NativeUiPressable.NameLimit - 1) + "🚪🚪")) })), Shown);
        Check(longName.Length == NativeUiPressable.NameLimit + 1 && longName.EndsWith('…') && longName.StartsWith("word word", StringComparison.Ordinal)
            && emojiName.Length == NativeUiPressable.NameLimit && emojiName.EndsWith("a…", StringComparison.Ordinal),
            "A long name is cut and marked as cut, never in the middle of a character: " + longName.Length + " / " + emojiName.Length);

        // The renderer and the Button subclass call the rules above; these read the two files for that.
        var tileControls = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Controls.cs"));
        var pressableCase = tileControls[tileControls.IndexOf("case \"Pressable\":", StringComparison.Ordinal)..tileControls.IndexOf("case \"Timer\":", StringComparison.Ordinal)];
        var componentTheme = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiTheme.Components.cs"));
        var tileClass = componentTheme[componentTheme.IndexOf("class NativeUiPressableButton : Button", StringComparison.Ordinal)..];
        Check(pressableCase.Contains("new NativeUiPressableButton { Content = tileContent }") && pressableCase.Contains("tile.Click += ")
            && !pressableCase.Contains(".Tapped +=") && !pressableCase.Contains(".KeyDown +=") && !pressableCase.Contains("ContentControl") && !pressableCase.Contains("Template ="),
            "The tile is a Button with the platform's template and is pressed through Click, with no hand-written pointer or key handlers left");
        Check(pressableCase.Contains("AutomationProperties.SetName(tile, NativeUiPressable.Name(node,") && pressableCase.Contains("tile.Quiet();")
            && !pressableCase.Contains("IsEnabled") && !tileClass.Contains("IsEnabled")
            && pressableCase.Contains("if (NativeUiPressable.Accepts(pressable.Disposed, _runtime.IsExecuting) && P(pressable.Model, \"action\") is NativeUiActionPlan plan) await _runtime.ExecuteAsync(plan);")
            && pressableCase.Contains("ContrastScheme(), _runtime.IsExecuting);")
            && tileClass.Contains("NativeUiPressable.StateFill(_rest.Color, _overTint, busy)") && tileClass.Contains("NativeUiPressable.StateFill(_rest.Color, _downTint, busy)") && pressableCase.Contains("B(node, \"selected\") ? 2 : 1") && pressableCase.Contains("B(node, \"selected\") ? \"info\" : \"faint\""),
            "The tile is named from its text, its own text cannot swallow the press, it stays enabled and refuses a press while an action runs, and the selection ring is unchanged");
        Check(tileClass.Contains("protected override void OnKeyDown(KeyRoutedEventArgs e) { if (NativeUiPressable.OwnKey(e.OriginalSource, this)) base.OnKeyDown(e); }")
            && tileClass.Contains("protected override void OnKeyUp(KeyRoutedEventArgs e) { if (NativeUiPressable.OwnKey(e.OriginalSource, this)) base.OnKeyUp(e); }")
            && tileClass.Contains("if (NativeUiPressable.InsideNested<DependencyObject>(e.OriginalSource as DependencyObject, this, VisualTreeHelper.GetParent, Nested)) return;")
            && tileClass.Contains("node is Button or TextBox or CheckBox or ComboBox or Slider or RadioButton or Segmented"),
            "Keys and presses reach the Button only through the tested rules, with the same list of nested controls as before");
        Check(tileClass.Contains("HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;")
            && tileClass.Contains("HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Center;")
            && tileClass.Contains("FocusVisualMargin = new Thickness(0);") && !tileClass.Contains("Style =") && !tileClass.Contains("Template =") && !tileClass.Contains("MinWidth") && !tileClass.Contains("MinHeight"),
            "What the stock Button style would change at rest is put back, and the stock style and template themselves are kept");
        Check(new[] { "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "ButtonBackgroundDisabled", "\"ButtonBorderBrush\" + state", "\"ButtonForeground\" + state", "\"PointerOver\", \"Pressed\", \"Disabled\"" }.All(tileClass.Contains)
            && tileClass.Contains("Resources[\"ButtonBackgroundDisabled\"] = _rest") && tileClass.Contains("NativeUiPressable.Over(")
            && tileClass.Contains("&& _memo.NeedsTints((solid.Color.A, solid.Color.R, solid.Color.G, solid.Color.B), Theme.ToString(), contrast))")
            && tileClass.Contains("if (_memo.NeedsText(_pageTheme.ToString(), RequestedTheme.ToString(), _contrast)) TakeTextFromAbove();")
            && tileClass.Contains("Loading += OnTileLoading;") && tileClass.Contains("{ _memo.Moved(); TakeTextIfNeeded(); }")
            && Regex.Matches(tileClass, @"TakeTextFromAbove\(\)").Count == 2 && Regex.Matches(tileClass, @"Tinted\(solid\.Color").Count == 2 && Regex.Matches(tileClass, @"NativeUiPressable\.StateFill\(").Count == 2 && !tileClass.Contains(" static readonly ") && !tileClass.Contains("static Dictionary")
            && tileClass.Contains("\"SubtleFillColorSecondaryBrush\"") && tileClass.Contains("\"SubtleFillColorTertiaryBrush\""),
            "Every brush the stock states swap in is answered by the tile: fill from the tested tint rule, border and text as at rest, disabled as at rest");

        Console.WriteLine($"PASS lane N2 media/map chrome: Mapsui zoom widget, fixed on-map colours for light tiles, PipsPager and viewer zoom, carousel stops, pressable tile rules; {checks} checks");
    }
}

internal sealed class TreeNode(string name, TreeNode? parent, bool interactive)
{
    internal string Name { get; } = name;
    internal TreeNode? Parent { get; } = parent;
    internal bool Interactive { get; } = interactive;
}
