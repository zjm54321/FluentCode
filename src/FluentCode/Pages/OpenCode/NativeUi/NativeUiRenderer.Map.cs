// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using BruTile.Predefined;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using FluentCode.Services.NativeUi;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using NetTopologySuite.Geometries;
using MapColor = Mapsui.Styles.Color;
using MapControl = Mapsui.UI.WinUI.MapControl;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    internal Func<Task<NativeUiMapContext>>? MapContextProvider { get; set; }
    private sealed class CardTileSource(NativeUiMapContext context, NativeUiTileClient client) : IHttpTileSource
    {
        public ITileSchema Schema { get; } = new GlobalSphericalMercator(0, NativeUiMapProviders.MaxZoom(context.Settings.Provider));
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
    private Visual CreateMap(string type, string key)
    {
        var panel = new StackPanel { Spacing = 8 }; var locations = new StackPanel { Spacing = 8 };
        var load = new Button { Content = "加载地图" }; var fit = new Button { Content = "显示全部地点", Visibility = Visibility.Collapsed };
        var status = NativeUiComponentTheme.Text("点击后才会向设置中的提供方请求地图瓦片。", true);
        var host = new Grid { Visibility = Visibility.Collapsed }; var attribution = NativeUiComponentTheme.Text("", true);
        attribution.HorizontalAlignment = HorizontalAlignment.Right; attribution.VerticalAlignment = VerticalAlignment.Bottom; attribution.Margin = new(8); attribution.IsHitTestVisible = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { load, fit } };
        panel.Children.Add(actions); panel.Children.Add(status); panel.Children.Add(host); panel.Children.Add(locations);
        var visual = new Visual(panel); var gate = new NativeUiLoadGate(); MapControl? control = null; NativeUiTileClient? tiles = null;
        NativeUiMapContext? context = null; MemoryLayer? points = null; MemoryLayer? route = null; NativeUiElement[] markers = []; var generation = 0;
        void StopMap()
        {
            generation++; control?.Dispose(); control = null; tiles?.Dispose(); tiles = null; points = route = null; context = null;
            host.Children.Clear(); host.Visibility = fit.Visibility = Visibility.Collapsed;
        }
        (MPoint Point, NativeUiElement Marker)[] Projected() => markers.Where(marker => NativeUiCoordinates.Valid(new(N(marker, "lat"), N(marker, "lng"))))
            .Select(marker => { var p = NativeUiCoordinates.ForProvider(new(N(marker, "lat"), N(marker, "lng")), context?.Settings.Provider ?? NativeUiMapProvider.OpenStreetMap); var xy = NativeUiCoordinates.WebMercator(p); return (new MPoint(xy.X, xy.Y), marker); }).ToArray();
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
            var features = new List<IFeature>();
            foreach (var item in projected)
            {
                var label = S(item.Marker, "label"); var chosen = label == selected;
                var feature = new PointFeature(item.Point); feature["label"] = label;
                feature.Styles.Add(new SymbolStyle { SymbolScale = chosen ? 1.3 : 1, Fill = new Mapsui.Styles.Brush(chosen ? MapColor.OrangeRed : MapColor.DodgerBlue), Outline = new Pen(MapColor.White, 2) });
                feature.Styles.Add(new LabelStyle { Text = label, Font = new Mapsui.Styles.Font { FontFamily = NativeUiTheme.MapLabelFont, Size = 14 }, ForeColor = MapColor.Black, BackColor = new Mapsui.Styles.Brush(MapColor.White), Offset = new Offset(0, 22), CollisionDetection = false }); features.Add(feature);
            }
            points.Features = features; points.FeaturesWereModified();
            route.Features = B(visual.Model, "route") && projected.Length > 1
                ? new IFeature[] { new GeometryFeature(new LineString(projected.Select(item => new Coordinate(item.Point.X, item.Point.Y)).ToArray())) { Styles = { new VectorStyle { Line = new Pen(MapColor.DodgerBlue, 3) } } } }
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
                if (context.Settings.Provider == NativeUiMapProvider.Baidu) throw new NotSupportedException("百度地图暂不支持，请在设置中选择其他提供方。");
                tiles = new(context); var owned = tiles;
                owned.Failed += () => panel.DispatcherQueue.TryEnqueue(() => { if (!visual.Disposed && version == generation) { StopMap(); status.Text = "地图瓦片不可用，已保留地点列表；可重试或更换提供方。"; load.Content = "重试加载地图"; } });
                var map = new Map { CRS = "EPSG:3857" }; map.Layers.Add(new TileLayer(new CardTileSource(context, owned)) { Name = "底图" });
                route = new MemoryLayer { Name = "地点顺序连线", Style = null }; points = new MemoryLayer { Name = "地点", Style = null };
                map.Layers.Add(route); map.Layers.Add(points);
                map.Tapped += (_, args) => { if (visual.Disposed || points is null) return; var feature = args.GetMapInfo(new ILayer[] { points }).Feature; if (feature?["label"] is string label) { SetField(visual, key, "selected", "", label); args.Handled = true; } };
                control = new MapControl { Map = map, Height = Math.Clamp(N(visual.Model, "height", 320), 180, 640) };
                AutomationProperties.SetName(control, "交互地图，可平移和缩放"); AutomationProperties.SetAutomationId(control, "NativeUiLoadedMap");
                attribution.Text = NativeUiMapProviders.Attribution(context.Settings); attribution.Foreground = NativeUiTheme.Brush("ink", theme: ElementTheme.Light);
                var credit = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), Child = attribution, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = .95 };
                host.Children.Add(control); host.Children.Add(credit); host.Visibility = fit.Visibility = Visibility.Visible;
                Features(); control.Loaded += (_, _) => Fit();
                status.Text = NativeUiMapProviders.Name(context.Settings.Provider) + " · 拖动平移，滚轮缩放；连线仅表示地点顺序。"; load.Content = "重新加载地图";
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
