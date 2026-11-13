// SPDX-License-Identifier: GPL-3.0-or-later
// Lane N2 (media + maps) focused checks. WinUI-free on purpose: it runs under tests/native-ui/run.ps1 with
// /warnaserror+, so it can only use the WinUI-free production sources, Mapsui/BruTile and the reference pack.
// Map chrome is exercised through the real library types; the media/gallery chrome is checked as renderer rules
// because MediaPlayerElement, FlipView and PipsPager all need a XAML tree that this harness deliberately has not.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Manipulations;
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

        // ---------------------------------------------------------------- renderer rules (source text)
        var mapSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Map.cs"));
        Check(mapSource.Contains("zoom = new ZoomInOutWidget") && mapSource.Contains("map.Widgets.Add(zoom)"), "Map chrome uses Mapsui's own zoom widget instead of a hand-built pair of buttons");
        Check(mapSource.Contains("zoom.StrokeColor = MapPaint(\"border\", true); zoom.TextColor = ink; zoom.BackColor = plate;"),
            "The zoom widget is repainted from the theme tokens on every re-render, so a theme switch reaches it");
        Check(!mapSource.Contains("DodgerBlue") && !mapSource.Contains("OrangeRed") && !mapSource.Contains("MapColor.White"),
            "Map markers and polylines no longer use fixed DodgerBlue/OrangeRed/white colours");
        Check(mapSource.Contains("MapPaint(\"chart1\", true)") && mapSource.Contains("MapPaint(\"chart2\", true)")
            && mapSource.Contains("MapPaint(\"canvas\", true)") && mapSource.Contains("MapPaint(\"ink\")"),
            "Map paints come from the shared theme tokens and the accent-first series palette");
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
        // C33: stock media element and transport controls, click-to-load, explicit full-window toggle.
        Check(mediaSource.Contains("new MediaTransportControls { IsCompact = false }") && mediaSource.Contains("AreTransportControlsEnabled = true")
            && mediaSource.Contains("new MediaPlayer { AutoPlay = false }") && mediaSource.Contains("AutoPlay = false, TransportControls = transport"),
            "Video/Audio keep the stock MediaPlayerElement with its own transport controls and no autoplay");
        Check(mediaSource.Contains("element.IsFullWindow = !element.IsFullWindow"), "The explicit full-window toggle is preserved");
        Check(mediaSource.Contains("Symbol.BackToWindow") && mediaSource.Contains("Symbol.FullScreen"),
            "The full-window button uses the framework's own full screen/back-to-window symbols and reports its state");
        Check(!mediaSource.Contains("IsFullWindowButtonVisible"),
            "No dependency on MediaTransportControls.IsFullWindowButtonVisible, which this WinUI version does not have");
        Check(mediaSource.Contains("load.Content = \"点击加载\"") && mediaSource.Contains("|| !gate.Request()) return;")
            && mediaSource.Contains("NativeUiMediaPolicy.Https(gate.Source)"),
            "Media still loads only after an explicit click on an allowed HTTPS source");
        Check(mediaSource.Contains("element.Unloaded -= OnUnloaded; Stop(); element.SetMediaPlayer(null); player.Dispose();"),
            "Media still detaches the player and the unload callback before disposal");
        Check(mediaSource.Contains("element.IsFullWindow = false; FullWindowLabel(false);"),
            "Reloading or unloading resets both the full-window state and its button label");

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

        Console.WriteLine($"PASS lane N2 media/map chrome: Mapsui zoom widget, theme-tinted pins, PipsPager and viewer zoom; {checks} checks");
    }
}
