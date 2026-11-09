using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BruTile;
using FluentCode.Models;
using FluentCode.Services.NativeUi;
using Mapsui;

internal static class BaiduMapTests
{
    private readonly record struct Reference(string Name, NativeUiCoordinate Wgs, NativeUiCoordinate Gcj, NativeUiCoordinate Bd,
        double X, double Y, (int Zoom, int X, int Y)[] Tiles);
    private sealed class TileHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }

    internal static async Task Run(string repo, Action<bool, string> require)
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; require(condition, message); }
        void Reject(Action action, string message)
        {
            var rejected = false;
            try { action(); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, message);
        }
        var settings = new NativeUiMapSettings { Provider = NativeUiMapProvider.Baidu };
        Check(new NativeUiMapSettings().Provider == NativeUiMapProvider.OpenStreetMap, "Baidu never replaces the default OpenStreetMap provider");
        var schema = NativeUiMapTileSchema.Create(settings.Provider);
        Check(schema.Srs == "BD09MC" && schema.YAxis == YAxis.TMS && schema.GetOriginX(3) == 0 && schema.GetOriginY(3) == 0,
            "Baidu uses its native BD09MC origin and north-up signed tile grid");
        Check(schema.Resolutions.Keys.Order().SequenceEqual(Enumerable.Range(3, 17)), "Baidu exposes only zoom levels 3–19");
        foreach (var zoom in schema.Resolutions.Keys)
            Check(schema.Resolutions[zoom].UnitsPerPixel == Math.Pow(2, 18 - zoom) && schema.GetTileWidth(zoom) == 256 && schema.GetTileHeight(zoom) == 256,
                "Baidu resolution and 256px dimensions at zoom " + zoom);
        Check(schema.Extent.MinX < -20037726 && schema.Extent.MaxX > 20037726 && schema.Extent.MinY < -12474104 && schema.Extent.MaxY > 12474104,
            "Baidu extent covers longitude ±180° and latitude ±74°");
        Check(schema.GetMatrixFirstCol(3) < 0 && schema.GetMatrixFirstRow(3) < 0, "Baidu's matrix includes western and southern tiles");

        // Independent references from published gcoord 1.0.7, not values computed by production code.
        // https://cdn.jsdelivr.net/npm/gcoord@1.0.7/dist/gcoord.global.prod.js
        // The README publishes the first WGS84→BD09 result verbatim.
        Reference[] references =
        [
            new("Published README", new(39.914266, 116.403988), new(39.915670251113426, 116.41023240747266), new(39.92196580126834, 116.41661560068297),
                12959579.377773141, 4826930.7854737425, [(3, 1, 0), (5, 6, 2), (10, 197, 73), (15, 6327, 2356), (19, 101246, 37710)]),
            new("Tiananmen-area sample", new(39.9087, 116.3975), new(39.91010349934476, 116.40374357265176), new(39.91644274963076, 116.4101165864734),
                12958855.902948888, 4826132.330392358, [(3, 1, 0), (5, 6, 2), (10, 197, 73), (15, 6327, 2356), (16, 12655, 4713), (17, 25310, 9426), (18, 50620, 18852), (19, 101241, 37704)]),
            new("Shanghai", new(31.2304, 121.4737), new(31.22845773757727, 121.47822305927693), new(31.234310593689997, 121.484781468503),
                13523771.160137296, 3640856.297424988, [(3, 1, 0), (10, 206, 55), (15, 6603, 1777), (19, 105654, 28444)]),
            new("Shenzhen", new(22.543096, 114.057865), new(22.54037875229225, 114.06297895739921), new(22.546037398720113, 114.06952896308195),
                12698300.047215588, 2560680.414911008, [(3, 1, 0), (10, 193, 39), (15, 6200, 1250), (19, 99205, 20005)]),
            new("Chengdu", new(30.572269, 104.066541), new(30.569814587450697, 104.06904597951714), new(30.575824539969044, 104.0755222119776),
                11585760.198036814, 3555849.027640312, [(3, 1, 0), (10, 176, 54), (15, 5657, 1736), (19, 90513, 27780)]),
        ];
        foreach (var reference in references)
        {
            var gcj = NativeUiCoordinates.WgsToGcj(reference.Wgs);
            var bd = NativeUiCoordinates.GcjToBd(gcj);
            var mc = NativeUiCoordinates.Project(reference.Wgs, settings.Provider);
            Check(Math.Abs(gcj.Latitude - reference.Gcj.Latitude) < 1e-10 && Math.Abs(gcj.Longitude - reference.Gcj.Longitude) < 1e-10,
                reference.Name + " WGS84→GCJ02 reference");
            Check(Math.Abs(bd.Latitude - reference.Bd.Latitude) < 1e-10 && Math.Abs(bd.Longitude - reference.Bd.Longitude) < 1e-10,
                reference.Name + " GCJ02→BD09 reference");
            Check(Math.Abs(mc.X - reference.X) < 1e-5 && Math.Abs(mc.Y - reference.Y) < 1e-5,
                reference.Name + " WGS84→GCJ02→BD09→BD09MC reference within 0.01mm");
            Check(mc == NativeUiCoordinates.BaiduMercator(NativeUiCoordinates.ForProvider(reference.Wgs, settings.Provider)),
                reference.Name + " marker/polyline projection uses the same BD09MC chain");
            foreach (var expected in reference.Tiles)
            {
                var tile = schema.GetTileInfos(new Extent(mc.X - .001, mc.Y - .001, mc.X + .001, mc.Y + .001), expected.Zoom).Single();
                Check(tile.Index.Level == expected.Zoom && tile.Index.Col == expected.X && tile.Index.Row == expected.Y,
                    reference.Name + " native tile index at zoom " + expected.Zoom);
                var resolution = schema.Resolutions[expected.Zoom].UnitsPerPixel;
                var span = resolution * 256;
                Check(tile.Extent.MinX == expected.X * span && tile.Extent.MaxX == (expected.X + 1) * span
                    && tile.Extent.MinY == expected.Y * span && tile.Extent.MaxY == (expected.Y + 1) * span,
                    reference.Name + " native tile extent at zoom " + expected.Zoom);
                var pixelX = (mc.X - tile.Extent.MinX) / resolution;
                var pixelY = (tile.Extent.MaxY - mc.Y) / resolution;
                Check(pixelX is >= 0 and < 256 && pixelY is >= 0 and < 256, reference.Name + " projects inside the correctly oriented tile");
            }
        }

        foreach (var reference in new[]
        {
            (Latitude: 65d, X: 13358484.246390717, Y: 9569273.761394978),
            (Latitude: 50d, X: 13358484.24579993, Y: 6413230.4273446975),
            (Latitude: 35d, X: 13358484.2447159, Y: 4139145.6551143583),
            (Latitude: 20d, X: 13358484.246578075, Y: 2258286.1000064136),
            (Latitude: 5d, X: 13358484.248097567, Y: 553548.5774699782),
        })
        {
            var north = NativeUiCoordinates.BaiduMercator(new(reference.Latitude, 120));
            var south = NativeUiCoordinates.BaiduMercator(new(-reference.Latitude, -120));
            Check(Math.Abs(north.X - reference.X) < 1e-5 && Math.Abs(north.Y - reference.Y) < 1e-5, "Published BD09MC latitude band " + reference.Latitude);
            Check(south.X == -north.X && south.Y == -north.Y, "BD09MC latitude bands preserve western/southern signs");
        }
        Check(NativeUiCoordinates.BaiduMercator(new(90, 181)) == NativeUiCoordinates.BaiduMercator(new(74, -179))
            && NativeUiCoordinates.BaiduMercator(new(-90, -181)) == NativeUiCoordinates.BaiduMercator(new(-74, 179)),
            "BD09MC clamps polar latitudes and wraps longitudes");
        var equator = NativeUiCoordinates.BaiduMercator(new(0, 0));
        Check(double.IsFinite(equator.X) && double.IsFinite(equator.Y), "BD09MC's equatorial band remains finite");
        foreach (var expected in new[] { (Zoom: 3, X: -2, Y: -1), (Zoom: 10, X: -198, Y: -74), (Zoom: 15, X: -6328, Y: -2357), (Zoom: 19, X: -101242, Y: -37705) })
        {
            var tile = schema.GetTileInfos(new Extent(-12958855.903, -4826132.331, -12958855.902, -4826132.330), expected.Zoom).Single();
            Check(tile.Index.Col == expected.X && tile.Index.Row == expected.Y, "Negative Baidu indices use floor, not truncation, at zoom " + expected.Zoom);
        }
        var aboveOrigin = schema.GetTileInfos(new Extent(.25, .25, .75, .75), 18).Single();
        var belowOrigin = schema.GetTileInfos(new Extent(-.75, -.75, -.25, -.25), 18).Single();
        Check(aboveOrigin.Index.Col == 0 && aboveOrigin.Index.Row == 0 && belowOrigin.Index.Col == -1 && belowOrigin.Index.Row == -1,
            "Baidu's grid origin has no XYZ half-world shift or inverted rows");

        Check(settings.ValidationError() is null && NativeUiMapProviders.Name(settings.Provider) == "百度地图" && NativeUiMapProviders.Attribution(settings) == "© 百度地图",
            "Baidu is a valid enabled provider with the required attribution and no credential prerequisite");
        Check(NativeUiMapProviders.TileUrl(settings, 15, 6327, 2356).AbsoluteUri == "https://maponline2.bdimg.com/tile/?qt=vtile&x=6327&y=2356&z=15&styles=pl&scaler=1",
            "Baidu URL requests the native tile with no AK or stale udt parameter");
        foreach (var (x, server) in new[] { (0, 3), (1, 0), (2, 1), (3, 2) })
            Check(NativeUiMapProviders.TileUrl(settings, 3, x, 0).Host == $"maponline{server}.bdimg.com", "Baidu rotates all four subdomains");
        Check(NativeUiMapProviders.TileUrl(settings, 3, -2, -1).AbsoluteUri == "https://maponline0.bdimg.com/tile/?qt=vtile&x=-2&y=-1&z=3&styles=pl&scaler=1",
            "Baidu URLs preserve signed indices");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = new CultureInfo("ar-SA"); culture.NumberFormat.NegativeSign = "~"; CultureInfo.CurrentCulture = culture;
            Check(NativeUiMapProviders.TileUrl(settings, 3, -2, -1).Query.Contains("x=-2&y=-1&z=3", StringComparison.Ordinal), "Baidu URL numbers are culture invariant");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        Reject(() => NativeUiMapProviders.TileUrl(settings, 2, 0, 0), "Baidu rejects requests below zoom 3");
        Reject(() => NativeUiMapProviders.TileUrl(settings, 20, 0, 0), "Baidu rejects requests above zoom 19");
        Reject(() => NativeUiMapProviders.TileUrl(new(), 3, -2, -1), "Standard XYZ providers still reject negative indices");

        using var map = new Map(); NativeUiMapTileSchema.ConfigureMap(map, schema);
        Check(map.CRS == "BD09MC" && map.Navigator.OverrideZoomBounds is { Min: .5, Max: 32768 }, "The actual map has native CRS and explicit Baidu resolution limits");
        map.Navigator.SetSize(800, 320);
        Check(map.Navigator.Viewport.Resolution == 32768, "Baidu starts at a supported resolution before its first user zoom");
        map.Navigator.ZoomTo(.01);
        Check(map.Navigator.Viewport.Resolution == .5, "Navigator clamps zoom-in to Baidu level 19");
        map.Navigator.ZoomTo(1e7);
        Check(map.Navigator.Viewport.Resolution == 32768, "Navigator clamps zoom-out to Baidu level 3");
        foreach (var resolution in schema.Resolutions.Values)
        {
            map.Navigator.ZoomTo(resolution.UnitsPerPixel);
            Check(map.Navigator.Viewport.Resolution == resolution.UnitsPerPixel, "Navigator preserves supported Baidu zoom " + resolution.Level);
        }
        map.Navigator.ZoomToBox(new MRect(-1e9, -1e9, 1e9, 1e9));
        Check(map.Navigator.Viewport.Resolution == 32768, "Fit-to-markers cannot zoom out beyond Baidu level 3");
        map.Navigator.ZoomToBox(new MRect(0, 0, .01, .01));
        Check(map.Navigator.Viewport.Resolution == .5, "Fit-to-markers cannot zoom in beyond Baidu level 19");
        Check(NativeUiMapTileSchema.Create(NativeUiMapProvider.OpenStreetMap).Srs == "EPSG:3857"
            && NativeUiCoordinates.Project(references[0].Wgs, NativeUiMapProvider.AMap) == NativeUiCoordinates.WebMercator(NativeUiCoordinates.WgsToGcj(references[0].Wgs)),
            "Existing OSM and AMap retain their Web Mercator routes");

        var cacheDirectory = Path.Combine(repo, "artifacts", "baidu-no-disk-" + Guid.NewGuid().ToString("N"));
        var context = new NativeUiMapContext(settings, "not-a-baidu-ak", cacheDirectory); var requests = 0;
        HttpResponseMessage Reply(HttpRequestMessage request)
        {
            requests++;
            Check(request.Headers.UserAgent.ToString().Contains("FluentCode/") && request.Headers.Authorization is null && !request.Headers.Contains("Cookie"),
                "Baidu sends an identifying User-Agent without cookies or credentials");
            Check(request.RequestUri!.AbsoluteUri == "https://maponline2.bdimg.com/tile/?qt=vtile&x=6327&y=2356&z=15&styles=pl&scaler=1",
                "Baidu memory-only client uses the tested native URL and does not leak another provider's key");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new("image/png"); return response;
        }
        using (var source = new NativeUiBaiduTileSource(context, new TileHandler(Reply)))
        {
            var first = await source.GetAsync(15, 6327, 2356); var cached = await source.GetAsync(15, 6327, 2356);
            Check(first.SequenceEqual(cached) && requests == 1 && source.Requests == 1, "Baidu reuses tiles only in the current map session's memory");
            Check(!Directory.Exists(cacheDirectory), "Baidu requests create no disk cache directory or files");
        }
        using (var reopened = new NativeUiBaiduTileSource(context, new TileHandler(Reply)))
        {
            await reopened.GetAsync(15, 6327, 2356);
            Check(requests == 2 && !Directory.Exists(cacheDirectory), "A new Baidu map session has no persistent tile cache to reuse");
        }
        using (var failed = new NativeUiBaiduTileSource(context, new TileHandler(_ => new(HttpStatusCode.ServiceUnavailable))))
        {
            var notified = false; failed.Failed += () => notified = true;
            try { await failed.GetAsync(15, 6327, 2356); } catch (InvalidOperationException) { }
            Check(notified && !Directory.Exists(cacheDirectory), "Baidu tile failure signals the static-list fallback without disk writes");
        }
        using (var redirect = new NativeUiBaiduTileSource(context, new TileHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("http://example.org/tile.png"); return response;
        })))
        {
            var rejected = false;
            try { await redirect.GetAsync(15, 6327, 2356); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && redirect.Requests == 1, "Baidu rejects an insecure redirect before making the second request");
        }
        var renderer = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Map.cs"));
        Check(renderer.Contains("NativeUiMapTileSchema.Create(context.Settings.Provider)") && renderer.Contains("NativeUiMapTileSchema.ConfigureMap(map, source.Schema)")
            && renderer.Contains("NativeUiCoordinates.Project(") && renderer.Contains("new LineString(projected.Select(") && !renderer.Contains("throw new NotSupportedException"),
            "Native renderer uses the tested native schema and shared marker/polyline projection without blocking Baidu");
        Check(renderer.Contains("baiduTiles = new(context); baiduTiles.Failed += TileFailed; source = baiduTiles;") && renderer.Contains("baiduTiles?.Dispose(); baiduTiles = null;"),
            "Native renderer selects and disposes Baidu's memory-only source instead of the disk-caching client");
        var settingsSource = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/Settings/SettingsView.NativeUiMap.cs"));
        Check(settingsSource.Contains("Enum.GetValues<NativeUiMapProvider>()") && settingsSource.Contains("Tag = provider });")
            && !settingsSource.Contains("IsEnabled = provider !=") && settingsSource.Contains("百度地图服务条款适用。")
            && settingsSource.Contains("NavigateUri = new Uri(\"https://lbsyun.baidu.com/docs/pcsa?title=law/open/law\")")
            && settingsSource.Contains("_mapBaidu.Visibility = SelectedMapProvider == NativeUiMapProvider.Baidu ? Visibility.Visible : Visibility.Collapsed"),
            "Settings includes enabled Baidu and shows its short linked terms notice only when selected");
        Console.WriteLine($"PASS Baidu native coordinates, tile grid, URL, zoom clamping, settings and memory-only caching; {checks} checks");
    }
}
