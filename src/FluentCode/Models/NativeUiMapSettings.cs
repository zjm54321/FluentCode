// SPDX-License-Identifier: GPL-3.0-or-later
using System;

namespace FluentCode.Models;

public enum NativeUiMapProvider { OpenStreetMap, AMap, Baidu, TianDiTu, Custom }

/// <summary>Native card maps. Keys are DPAPI credential references, never plaintext JSON.</summary>
public sealed record NativeUiMapSettings
{
    public NativeUiMapProvider Provider { get; init; } = NativeUiMapProvider.OpenStreetMap;
    public string? TianDiTuCredentialId { get; init; }
    public string? CustomTileUrlTemplate { get; init; }
    public string? CustomAttribution { get; init; }

    public string? ValidationError()
    {
        if (!Enum.IsDefined(Provider)) return "地图提供方无效。";
        if (CustomAttribution?.Length > 512) return "地图署名不能超过 512 个字符。";
        if (CustomTileUrlTemplate is { } template && !ValidTemplate(template)) return "自定义瓦片地址必须是包含 {z}/{x}/{y} 的 HTTPS 模板，且不能带凭据或片段。";
        if (Provider == NativeUiMapProvider.Custom && (string.IsNullOrWhiteSpace(CustomTileUrlTemplate) || string.IsNullOrWhiteSpace(CustomAttribution))) return "自定义地图需要 XYZ HTTPS 地址与提供方署名。";
        return null;
    }

    public static bool ValidTemplate(string value)
    {
        if (value.Length is 0 or > 2048 || !value.Contains("{x}", StringComparison.Ordinal) || !value.Contains("{y}", StringComparison.Ordinal) || !value.Contains("{z}", StringComparison.Ordinal)) return false;
        var sample = value.Replace("{x}", "0", StringComparison.Ordinal).Replace("{y}", "0", StringComparison.Ordinal).Replace("{z}", "0", StringComparison.Ordinal);
        return !sample.Contains('{') && !sample.Contains('}') && Uri.TryCreate(sample, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }
}
