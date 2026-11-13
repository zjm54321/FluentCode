// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using FluentCode.Services.NativeUi;
using Mapsui;
// Mapsui.Extensions supplies Add/AddRange for the ConcurrentQueue that holds the map widgets.
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.WinUI;
using Mapsui.Widgets.ButtonWidgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetTopologySuite.Geometries;
using MapColor = Mapsui.Styles.Color;
using MapControl = Mapsui.UI.WinUI.MapControl;
// Mapsui's widget alignment/orientation enums clash with Microsoft.UI.Xaml's, so they are aliased here.
using MapOrientation = Mapsui.Widgets.Orientation;
using MapHorizontal = Mapsui.Widgets.HorizontalAlignment;
using MapVertical = Mapsui.Widgets.VerticalAlignment;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    internal Func<Task<NativeUiMapContext>>? MapContextProvider { get; set; }
    private sealed class CardTileSource(NativeUiMapContext context, NativeUiTileClient client) : IHttpTileSource
    {
        public ITileSchema Schema { get; } = NativeUiMapTileSchema.Create(context.Settings.Provider);
        public string Name => NativeUiMapProviders.Name(context.Settings.Provider);
        public Attribution Attribution => new(NativeUiMapProviders.Attribution(context.Settings), "");
        public async Task<byte[]?> GetTileAsync(HttpClient _, TileInfo tile, CancellationToken? cancellationToken = null) => await client.GetAsync(tile.Index.Level, tile.Index.Col, tile.Index.Row, cancellationToken ?? default);
    }
    private async Task<NativeUiMapContext> GetMapContextAsync()
    {
        if (MapContextProvider is { } provider) return await provider();
        var settings = AppServices.Settings.Current?.NativeUiMap ?? new();
        var key = settings.TianDiTuCredentialId is { } id ? await AppServices.Secrets.GetAsync(id) : null;
        return new(settings, key, Path.Combine(AppServices.Settings.DataDirectory, "cache", "native-ui-tiles"));
    }
    // Map paint follows one rule (docs/native-ui.md, "Map colours").
    //  - Drawn ON the tiles (pins, the order line, place labels): fixed colours chosen against a light map. Every
    //    provider serves a light raster whatever the app theme is (Services/NativeUi/NativeUiMapProviders.cs), so a
    //    colour picked for a dark app surface is the wrong colour here.
    //  - Drawn AROUND the map (the zoom buttons here, the XAML credit chip): follows the app theme.
    //  - High contrast: both groups take the system colours through the theme tokens.
    // The chosen pin is told apart twice, by size (1.3x) and by a warm colour next to the blue ones, and is drawn
    // last so a neighbour cannot cover it. Blue against orange-red stays apart for red-green colour-blind readers.
    // WCAG contrast against OpenStreetMap Carto's @land-color #F2EFE9 and @water-color #AAD3DF
    // (gravitystorm/openstreetmap-carto, master, style/style.mss): pin and line 5.50 / 3.93, chosen pin 4.64 / 3.32,
    // white edge against either fill 6.31 / 5.32, label ink on its plate 21. tests/native-ui recomputes these.
    private static readonly MapColor MapPinColor = new(0, 95, 184);
    private static readonly MapColor MapChosenPinColor = new(200, 52, 0);
    private static readonly MapColor MapEdgeColor = new(255, 255, 255);
    private static readonly MapColor MapLabelInkColor = new(0, 0, 0);
    /// <summary>
    /// Skia paints the map and its widgets and cannot read XAML theme resources, so a theme token is resolved to its
    /// brush and copied, alpha included. Used for what follows the theme (the zoom buttons) and for high contrast.
    /// </summary>
    private MapColor MapPaint(string token, bool background = false)
        => ComponentBrush(token, background) is SolidColorBrush brush
            ? new MapColor(brush.Color.R, brush.Color.G, brush.Color.B, brush.Color.A)
            : new MapColor(128, 128, 128);
    /// <summary>
    /// Composites a translucent theme colour over an opaque one. Fluent text and stroke colours are black or white
    /// with an alpha, and the map behind a widget is not the surface they were designed for.
    /// </summary>
    private static MapColor Flatten(MapColor top, MapColor under)
    {
        var alpha = top.A / 255d;
        int Mix(int over, int below) => (int)Math.Round(over * alpha + below * (1 - alpha));
        return new MapColor(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }
    private static bool MapHighContrast()
    {
        try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch (Exception) { return false; }
    }
    private Visual CreateMap(string type, string key)
    {
        var panel = new StackPanel { Spacing = 8 }; var locations = new StackPanel { Spacing = 8 };
        var load = new Button { Content = "加载地图" }; var fit = new Button { Content = "显示全部地点", Visibility = Visibility.Collapsed };
        var status = NativeUiComponentTheme.Text("点击后才会向设置中的提供方请求地图瓦片。", true);
        var host = new Grid { Visibility = Visibility.Collapsed }; var attribution = NativeUiComponentTheme.Text("", true);
        attribution.HorizontalAlignment = HorizontalAlignment.Right; attribution.VerticalAlignment = VerticalAlignment.Bottom; attribution.Margin = new(8); attribution.IsHitTestVisible = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { load, fit } };
        panel.Children.Add(actions); panel.Children.Add(status); panel.Children.Add(host); panel.Children.Add(locations);
        var visual = new Visual(panel); var gate = new NativeUiLoadGate(); MapControl? control = null; NativeUiTileClient? tiles = null; NativeUiBaiduTileSource? baiduTiles = null;
        NativeUiMapContext? context = null; MemoryLayer? points = null; MemoryLayer? route = null; NativeUiElement[] markers = []; var generation = 0;
        ZoomInOutWidget? zoom = null;
        void StopMap()
        {
            generation++; control?.Dispose(); control = null; tiles?.Dispose(); tiles = null; baiduTiles?.Dispose(); baiduTiles = null; points = route = null; context = null; zoom = null;
            host.Children.Clear(); host.Visibility = fit.Visibility = Visibility.Collapsed;
        }
        (MPoint Point, NativeUiElement Marker)[] Projected() => markers.Where(marker => NativeUiCoordinates.Valid(new(N(marker, "lat"), N(marker, "lng"))))
            .Select(marker => { var xy = NativeUiCoordinates.Project(new(N(marker, "lat"), N(marker, "lng")), context?.Settings.Provider ?? NativeUiMapProvider.OpenStreetMap); return (new MPoint(xy.X, xy.Y), marker); }).ToArray();
        void Fit()
        {
            if (control is null) return; var projected = Projected(); if (projected.Length == 0) return;
            var minX = projected.Min(item => item.Point.X); var maxX = projected.Max(item => item.Point.X); var minY = projected.Min(item => item.Point.Y); var maxY = projected.Max(item => item.Point.Y);
            var padding = Math.Max(200, Math.Max(maxX - minX, maxY - minY) * .15);
            control.Map.Navigator.ZoomToBox(new MRect(minX - padding, minY - padding, maxX + padding, maxY + padding));
        }
        void Features()
        {
            if (control is null || points is null || route is null) return;
            var projected = Projected(); var selected = NativeUiValues.Text(Field(visual, key, "selected", ""));
            // On the tiles: fixed colours, or the system colours in high contrast, where the selected pin takes the
            // system highlight and the others the window text colour.
            var contrast = MapHighContrast();
            var edge = contrast ? Flatten(MapPaint("canvas", true), MapEdgeColor) : MapEdgeColor;
            var pinFill = contrast ? Flatten(MapPaint("ink"), edge) : MapPinColor;
            var pinChosen = contrast ? Flatten(MapPaint("chart1", true), edge) : MapChosenPinColor;
            var labelInk = contrast ? pinFill : MapLabelInkColor;
            var outline = new Pen(edge, 2);
            // Around the map: the zoom buttons follow the theme. A theme change re-renders the card and lands here.
            // The fill is the opaque page colour; the frame and the signs are the theme's secondary and primary text
            // colours flattened onto it, so both keep their designed contrast with the button whatever tile is behind.
            // Light theme: frame #5C5C5C on fill #F3F3F3 is 6.0:1, and 5.8 / 4.2 against land / water. Dark theme:
            // fill #202020 is 14.2 / 10.2 against land / water, frame #CCCCCC on it 10.2. In high contrast all three
            // tokens are system colours (window, window text) and opaque, so flattening changes nothing.
            if (zoom is not null)
            {
                var fill = Flatten(MapPaint("canvas", true), MapEdgeColor);
                zoom.BackColor = fill; zoom.StrokeColor = Flatten(MapPaint("muted"), fill); zoom.TextColor = Flatten(MapPaint("ink"), fill);
            }
            var features = new List<IFeature>();
            // The chosen place is built last. A MemoryLayer draws its features in Id order whatever the list order is,
            // and Ids are handed out at construction (Mapsui/Mapsui, tag 5.1.0, Mapsui/Layers/MemoryLayer.cs
            // SortFeatures and Mapsui/Features/BaseFeature.cs), so the last one built is the one on top.
            foreach (var item in projected.OrderBy(place => S(place.Marker, "label") == selected))
            {
                var label = S(item.Marker, "label"); var chosen = label == selected;
                var feature = new PointFeature(item.Point); feature["label"] = label;
                feature.Styles.Add(new SymbolStyle { SymbolScale = chosen ? 1.3 : 1, Fill = new Mapsui.Styles.Brush(chosen ? pinChosen : pinFill), Outline = outline });
                feature.Styles.Add(new LabelStyle { Text = label, Font = new Mapsui.Styles.Font { FontFamily = NativeUiTheme.MapLabelFont, Size = 14 }, ForeColor = labelInk, BackColor = new Mapsui.Styles.Brush(edge), Offset = new Offset(0, 22), CollisionDetection = false }); features.Add(feature);
            }
            points.Features = features; points.FeaturesWereModified();
            // The order line is the pin colour with a casing in the edge colour, so it stays readable where it crosses
            // water, a motorway or map text. The casing is the style's Outline: the line renderer draws it first, as
            // wide as the line plus the outline on both sides, here 3 + 2 x 1.5 = 6 (Mapsui/Mapsui, tag 5.1.0,
            // Mapsui.Rendering.Skia/SkiaStyles/LineStringRenderer.cs). A VectorStyle's default outline is 1 px grey.
            route.Features = B(visual.Model, "route") && projected.Length > 1
                ? new IFeature[] { new GeometryFeature(new LineString(projected.Select(item => new Coordinate(item.Point.X, item.Point.Y)).ToArray())) { Styles = { new VectorStyle { Line = new Pen(pinFill, 3), Outline = new Pen(edge, 1.5) } } } }
                : Array.Empty<IFeature>();
            route.FeaturesWereModified(); control.RefreshGraphics();
        }
        fit.Click += (_, _) => Fit();
        load.Click += async (_, _) =>
        {
            if (visual.Disposed || !gate.Request()) return;
            StopMap(); var version = generation; load.IsEnabled = false; load.Content = "正在加载地图…";
            try
            {
                context = await GetMapContextAsync();
                if (visual.Disposed || version != generation) return;
                void TileFailed() => panel.DispatcherQueue.TryEnqueue(() => { if (!visual.Disposed && version == generation) { StopMap(); status.Text = "地图瓦片不可用，已保留地点列表；可重试或更换提供方。"; load.Content = "重试加载地图"; } });
                IHttpTileSource source;
                if (context.Settings.Provider == NativeUiMapProvider.Baidu)
                { baiduTiles = new(context); baiduTiles.Failed += TileFailed; source = baiduTiles; }
                else
                { tiles = new(context); tiles.Failed += TileFailed; source = new CardTileSource(context, tiles); }
                var map = new Map(); NativeUiMapTileSchema.ConfigureMap(map, source.Schema);
                map.Layers.Add(new TileLayer(source) { Name = "底图" });
                route = new MemoryLayer { Name = "地点顺序连线", Style = null }; points = new MemoryLayer { Name = "地点", Style = null };
                map.Layers.Add(route); map.Layers.Add(points);
                // The library's own +/- widget: it draws and handles taps inside the viewport, so it needs no XAML
                // overlay and works without a wheel or a pinch. Features() paints it from the theme. Opacity is 1
                // because the renderer multiplies every colour's alpha by it and the default is 0.8
                // (Mapsui/Mapsui, tag 5.1.0, Mapsui.Rendering.Skia/SkiaWidgets/ZoomInOutWidgetRenderer.cs).
                zoom = new ZoomInOutWidget { Orientation = MapOrientation.Vertical, HorizontalAlignment = MapHorizontal.Right, VerticalAlignment = MapVertical.Top, Margin = new MRect(8), Opacity = 1 };
                map.Widgets.Add(zoom);
                map.Tapped += (_, args) => { if (visual.Disposed || points is null) return; var feature = args.GetMapInfo(new ILayer[] { points }).Feature; if (feature?["label"] is string label) { SetField(visual, key, "selected", "", label); args.Handled = true; } };
                control = new MapControl { Map = map, Height = Math.Clamp(N(visual.Model, "height", 320), 180, 640) };
                AutomationProperties.SetName(control, "交互地图，可平移和缩放；右上角可放大和缩小"); AutomationProperties.SetAutomationId(control, "NativeUiLoadedMap");
                attribution.Text = NativeUiMapProviders.Attribution(context.Settings);
                // The chip follows the theme like every other card surface; Mapsui also draws the enabled layer's own
                // attribution widget, so the credit stays visible even if this chip is covered.
                var credit = new Border { Child = attribution, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = .95 };
                credit.Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SolidBackgroundFillColorBaseBrush"));
                host.Children.Add(control); host.Children.Add(credit); host.Visibility = fit.Visibility = Visibility.Visible;
                Features(); control.Loaded += (_, _) => Fit();
                status.Text = NativeUiMapProviders.Name(context.Settings.Provider) + " · 拖动平移，右上角或滚轮缩放；连线仅表示地点顺序。"; load.Content = "重新加载地图";
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!visual.Disposed && version == generation) { StopMap(); status.Text = error is NotSupportedException ? error.Message : "地图加载失败；仍可使用地点列表。请检查网络与地图设置。"; load.Content = "重试加载地图"; } }
            finally { if (!visual.Disposed) load.IsEnabled = true; }
        };
        visual.Apply = node =>
        {
            markers = type == "MapMarker" ? [node] : (P(node, "markers") as IReadOnlyList<object?> ?? []).OfType<NativeUiElement>().ToArray();
            var identity = NativeUiValues.ToJson(markers)?.ToJsonString() ?? "";
            if (gate.Source != identity) { StopMap(); gate.Bind(identity); load.Content = "加载地图"; status.Text = "点击后才会向设置中的提供方请求地图瓦片。"; }
            load.IsEnabled = markers.Any(marker => NativeUiCoordinates.Valid(new(N(marker, "lat"), N(marker, "lng"))));
            var cards = new List<UIElement>(); var selected = NativeUiValues.Text(Field(visual, key, "selected", ""));
            for (var i = 0; i < markers.Length; i++)
            {
                var marker = markers[i]; var label = S(marker, "label"); var lat = N(marker, "lat"); var lng = N(marker, "lng");
                var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (NativeUiTheme.Icon(S(marker, "icon") is { Length: > 0 } icon ? icon : "map-pin") is { } pin) title.Children.Add(pin);
                title.Children.Add(NativeUiComponentTheme.Text((B(node, "route") ? (i + 1) + ". " : "") + label));
                var choose = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Stretch }; choose.Click += (_, _) => { if (!visual.Disposed) SetField(visual, key, "selected", "", label); };
                AutomationProperties.SetName(choose, "选择地点：" + label);
                var body = new StackPanel { Spacing = 4, Children = { choose, NativeUiComponentTheme.Text(S(marker, "category") + " · WGS-84 " + lat.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + ", " + lng.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture), true) } };
                var open = new HyperlinkButton { Content = "在浏览器中查看地点", NavigateUri = null };
                open.Click += async (_, _) => await OpenAsync(FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={lat}&mlon={lng}#map=15/{lat}/{lng}")); body.Children.Add(open);
                var border = new Border { Child = body }; NativeUiTheme.Card(border, body, new("Card", new Dictionary<string, object?> { ["variant"] = selected == label ? "soft" : "outline", ["padding"] = 3d })); cards.Add(border);
            }
            Sync(locations, cards); Features(); if (control is not null) control.Height = Math.Clamp(N(node, "height", 320), 180, 640);
        };
        visual.Cleanup = StopMap; return visual;
    }
}
