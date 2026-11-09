// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using FluentCode.Models;

namespace FluentCode.Services.NativeUi;

internal readonly record struct NativeUiCoordinate(double Latitude, double Longitude);

internal static class NativeUiCoordinates
{
    private const double A = 6378245;
    private const double Eccentricity = 0.00669342162296594323;
    private const double XPi = Math.PI * 3000 / 180;
    // BD09MC is Baidu's piecewise polynomial projection, not EPSG:3857.
    // Coefficients: https://github.com/hujiulong/gcoord/blob/master/src/crs/BD09MC.ts (MIT).
    // Baidu clamps latitude to ±74°, so the published 75° band is never used.
    private static readonly double[] BaiduLatitudeBands = [60, 45, 30, 15, 0];
    private static readonly double[][] BaiduLlToMc =
    [
        [.0008277824516172526, 111320.7020463578, 647795574.6671607, -4082003173.641316, 10774905663.51142, -15171875531.51559, 12053065338.62167, -5124939663.577472, 913311935.9512032, 67.5],
        [.00337398766765, 111320.7020202162, 4481351.045890365, -23393751.19931662, 79682215.47186455, -115964993.2797253, 97236711.15602145, -43661946.33752821, 8477230.501135234, 52.5],
        [.00220636496208, 111320.7020209128, 51751.86112841131, 3796837.749470245, 992013.7397791013, -1221952.21711287, 1340652.697009075, -620943.6990984312, 144416.9293806241, 37.5],
        [-.0003441963504368392, 111320.7020576856, 278.2353980772752, 2485758.690035394, 6070.750963243378, 54821.18345352118, 9540.606633304236, -2710.55326746645, 1405.483844121726, 22.5],
        [-.0003218135878613132, 111320.7020701615, .00369383431289, 823725.6402795718, .46104986909093, 2351.343141331292, 1.58060784298199, 8.77738589078284, .37238884252424, 7.45],
    ];
    internal static bool Valid(NativeUiCoordinate point) => double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) && point.Latitude is >= -90 and <= 90 && point.Longitude is >= -180 and <= 180;
    internal static bool InsideChina(NativeUiCoordinate point) => point.Longitude is >= 72.004 and <= 137.8347 && point.Latitude is >= 0.8293 and <= 55.8271;

    internal static NativeUiCoordinate WgsToGcj(NativeUiCoordinate point)
    {
        if (!InsideChina(point)) return point;
        var x = point.Longitude - 105; var y = point.Latitude - 35;
        var latitude = -100 + 2 * x + 3 * y + .2 * y * y + .1 * x * y + .2 * Math.Sqrt(Math.Abs(x));
        latitude += (20 * Math.Sin(6 * x * Math.PI) + 20 * Math.Sin(2 * x * Math.PI)) * 2 / 3;
        latitude += (20 * Math.Sin(y * Math.PI) + 40 * Math.Sin(y / 3 * Math.PI)) * 2 / 3;
        latitude += (160 * Math.Sin(y / 12 * Math.PI) + 320 * Math.Sin(y * Math.PI / 30)) * 2 / 3;
        var longitude = 300 + x + 2 * y + .1 * x * x + .1 * x * y + .1 * Math.Sqrt(Math.Abs(x));
        longitude += (20 * Math.Sin(6 * x * Math.PI) + 20 * Math.Sin(2 * x * Math.PI)) * 2 / 3;
        longitude += (20 * Math.Sin(x * Math.PI) + 40 * Math.Sin(x / 3 * Math.PI)) * 2 / 3;
        longitude += (150 * Math.Sin(x / 12 * Math.PI) + 300 * Math.Sin(x / 30 * Math.PI)) * 2 / 3;
        var radians = point.Latitude / 180 * Math.PI; var sine = Math.Sin(radians); var magic = 1 - Eccentricity * sine * sine; var root = Math.Sqrt(magic);
        latitude = latitude * 180 / (A * (1 - Eccentricity) / (magic * root) * Math.PI);
        longitude = longitude * 180 / (A / root * Math.Cos(radians) * Math.PI);
        return new(point.Latitude + latitude, point.Longitude + longitude);
    }

    internal static NativeUiCoordinate GcjToWgs(NativeUiCoordinate point)
    {
        if (!InsideChina(point)) return point;
        var result = point;
        for (var i = 0; i < 12; i++)
        {
            var projected = WgsToGcj(result); var latitude = projected.Latitude - point.Latitude; var longitude = projected.Longitude - point.Longitude;
            result = new(result.Latitude - latitude, result.Longitude - longitude);
            if (Math.Max(Math.Abs(latitude), Math.Abs(longitude)) < 1e-10) break;
        }
        return result;
    }

    internal static NativeUiCoordinate GcjToBd(NativeUiCoordinate point)
    {
        var x = point.Longitude; var y = point.Latitude;
        var radius = Math.Sqrt(x * x + y * y) + .00002 * Math.Sin(y * XPi); var angle = Math.Atan2(y, x) + .000003 * Math.Cos(x * XPi);
        return new(radius * Math.Sin(angle) + .006, radius * Math.Cos(angle) + .0065);
    }

    internal static NativeUiCoordinate BdToGcj(NativeUiCoordinate point)
    {
        var x = point.Longitude - .0065; var y = point.Latitude - .006;
        var radius = Math.Sqrt(x * x + y * y) - .00002 * Math.Sin(y * XPi); var angle = Math.Atan2(y, x) - .000003 * Math.Cos(x * XPi);
        return new(radius * Math.Sin(angle), radius * Math.Cos(angle));
    }

    internal static NativeUiCoordinate ForProvider(NativeUiCoordinate point, NativeUiMapProvider provider) => provider switch
    {
        NativeUiMapProvider.AMap => WgsToGcj(point), NativeUiMapProvider.Baidu => GcjToBd(WgsToGcj(point)), _ => point,
    };
    internal static (double X, double Y) WebMercator(NativeUiCoordinate point)
    {
        const double radius = 6378137;
        return (radius * point.Longitude * Math.PI / 180, radius * Math.Log(Math.Tan(Math.PI / 4 + Math.Clamp(point.Latitude, -85.0511287798, 85.0511287798) * Math.PI / 360)));
    }

    internal static (double X, double Y) BaiduMercator(NativeUiCoordinate point)
    {
        var longitude = point.Longitude % 360;
        if (longitude > 180) longitude -= 360;
        else if (longitude < -180) longitude += 360;
        var latitude = Math.Clamp(point.Latitude, -74, 74);
        var band = 0;
        while (band < BaiduLatitudeBands.Length - 1 && Math.Abs(latitude) < BaiduLatitudeBands[band]) band++;
        var factors = BaiduLlToMc[band];
        var normalized = Math.Abs(latitude) / factors[9];
        var x = factors[0] + factors[1] * Math.Abs(longitude);
        var y = factors[2];
        for (var power = 1; power <= 6; power++) y += factors[power + 2] * Math.Pow(normalized, power);
        return (longitude < 0 ? -x : x, latitude < 0 ? -y : y);
    }

    internal static (double X, double Y) Project(NativeUiCoordinate point, NativeUiMapProvider provider)
    {
        var transformed = ForProvider(point, provider);
        return provider == NativeUiMapProvider.Baidu ? BaiduMercator(transformed) : WebMercator(transformed);
    }
}
