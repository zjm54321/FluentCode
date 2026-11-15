// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using FluentCode.Models;
using Mapsui.Layers;

namespace FluentCode.Services.NativeUi;

internal static class NativeUiMapProviders
{
    internal const string UserAgent = "FluentCode/1.2611.10 (Windows; native-ui map)";
    internal static string Name(NativeUiMapProvider provider) => provider switch { NativeUiMapProvider.AMap => "高德地图", NativeUiMapProvider.Baidu => "百度地图", NativeUiMapProvider.TianDiTu => "天地图", NativeUiMapProvider.Custom => "自定义 XYZ", _ => "OpenStreetMap" };
    internal static string Attribution(NativeUiMapSettings settings) => settings.Provider switch
    {
        NativeUiMapProvider.AMap => "© 高德地图 / AMap", NativeUiMapProvider.TianDiTu => "© 天地图 · 国家地理信息公共服务平台", NativeUiMapProvider.Custom => settings.CustomAttribution ?? "", NativeUiMapProvider.Baidu => "© 百度地图", _ => "© OpenStreetMap contributors",
    };
    /// <summary>
    /// The provider's credit is shown once, by the map card: a XAML chip over the map's bottom right corner, which
    /// follows the app theme, is set in the app's fonts (the Chinese provider names included) and is text Narrator can
    /// read. The base layer's own attribution widget is therefore switched off. Left on, the library draws the same
    /// text a second time in the same corner, with Skia's default typeface and no font fallback (Mapsui/Mapsui, tag
    /// 5.1.0: Mapsui.Tiling/Layers/TileLayer.cs copies the tile source's attribution into the layer's widget,
    /// Mapsui.Rendering.Skia/MapRenderer.cs adds every enabled layer's widget to the map's own, and
    /// Mapsui.Rendering.Skia/SkiaWidgets/TextBoxWidgetRenderer.cs draws it). A disabled widget is neither drawn nor
    /// hit-tested (Mapsui.Rendering.Skia/SkiaWidgets/WidgetRenderer.cs; Mapsui/Map.cs GetWidgetsOfMapAndLayers, which
    /// Mapsui/Widgets/WidgetInput.cs reads). The widget keeps its text, so the layer still states whose tiles it shows.
    /// </summary>
    internal static TLayer ShowCreditInCardOnly<TLayer>(TLayer baseLayer) where TLayer : ILayer
    { baseLayer.Attribution.Enabled = false; return baseLayer; }
    internal static int MinZoom(NativeUiMapProvider provider) => provider == NativeUiMapProvider.Baidu ? 3 : 0;
    internal static int MaxZoom(NativeUiMapProvider provider) => provider == NativeUiMapProvider.TianDiTu ? 18 : 19;
    internal static Uri TileUrl(NativeUiMapSettings settings, int zoom, int x, int y, string? key = null)
    {
        if (settings.ValidationError() is { } error) throw new InvalidOperationException(error);
        var baidu = settings.Provider == NativeUiMapProvider.Baidu;
        if (zoom < MinZoom(settings.Provider) || zoom > MaxZoom(settings.Provider) || (!baidu && (x < 0 || y < 0 || x >= 1L << zoom || y >= 1L << zoom))) throw new ArgumentOutOfRangeException(nameof(zoom), "瓦片坐标超出范围。");
        var z = zoom.ToString(CultureInfo.InvariantCulture); var column = x.ToString(CultureInfo.InvariantCulture); var row = y.ToString(CultureInfo.InvariantCulture);
        var url = settings.Provider switch
        {
            NativeUiMapProvider.OpenStreetMap => $"https://tile.openstreetmap.org/{z}/{column}/{row}.png",
            NativeUiMapProvider.AMap => $"https://webrd01.is.autonavi.com/appmaptile?style=7&x={column}&y={row}&z={z}",
            NativeUiMapProvider.Baidu => $"https://maponline{((long)x + y + zoom) & 3}.bdimg.com/tile/?qt=vtile&x={column}&y={row}&z={z}&styles=pl&scaler=1",
            NativeUiMapProvider.TianDiTu when !string.IsNullOrWhiteSpace(key) => $"https://t0.tianditu.gov.cn/vec_w/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=vec&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles&TILEMATRIX={z}&TILEROW={row}&TILECOL={column}&tk={Uri.EscapeDataString(key)}",
            NativeUiMapProvider.TianDiTu => throw new InvalidOperationException("请先在设置中保存天地图用户密钥。"),
            NativeUiMapProvider.Custom => settings.CustomTileUrlTemplate!.Replace("{z}", z, StringComparison.Ordinal).Replace("{x}", column, StringComparison.Ordinal).Replace("{y}", row, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
        return new(url, UriKind.Absolute);
    }
}
