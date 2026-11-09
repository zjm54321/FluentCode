// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using BruTile;
using BruTile.Predefined;
using FluentCode.Models;
using Mapsui;

namespace FluentCode.Services.NativeUi;

internal static class NativeUiMapTileSchema
{
    internal const string BaiduCrs = "BD09MC";

    internal static ITileSchema Create(NativeUiMapProvider provider)
    {
        if (provider != NativeUiMapProvider.Baidu)
            return new GlobalSphericalMercator(NativeUiMapProviders.MinZoom(provider), NativeUiMapProviders.MaxZoom(provider));

        var halfWidth = NativeUiCoordinates.BaiduMercator(new(0, 180)).X;
        var halfHeight = NativeUiCoordinates.BaiduMercator(new(74, 0)).Y;
        var schema = new TileSchema
        {
            Name = "Baidu BD09MC", Srs = BaiduCrs, Format = "png",
            OriginX = 0, OriginY = 0, YAxis = YAxis.TMS,
            Extent = new Extent(-halfWidth, -halfHeight, halfWidth, halfHeight),
        };
        // Baidu's 256px grid starts at (0, 0); signed rows increase northward.
        for (var zoom = NativeUiMapProviders.MinZoom(provider); zoom <= NativeUiMapProviders.MaxZoom(provider); zoom++)
            schema.Resolutions.Add(zoom, new Resolution(zoom, Math.Pow(2, 18 - zoom), 256, 256));
        return schema;
    }

    internal static void ConfigureMap(Map map, ITileSchema schema)
    {
        map.CRS = schema.Srs;
        if (schema.Srs == BaiduCrs)
            map.Navigator.OverrideZoomBounds = new MMinMax(schema.Resolutions.Values.Min(item => item.UnitsPerPixel), schema.Resolutions.Values.Max(item => item.UnitsPerPixel));
    }
}
