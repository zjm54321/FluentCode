using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FluentCode.Services.OpenCode;

public enum OpenCodeBillionDiscoveryStatus
{
    Ready, NotReady, Disabled, UnsupportedSource, InvalidLocation, UnsupportedProtocol,
    Malformed, HttpError, Timeout, TransportUnavailable, Cancelled,
}

/// <summary>Metadata only. The frontend plugin reports the OpenCode process-declared Billion address;
/// Ready means a declaration exists, not verified engine readiness, ownership or user consent.</summary>
public sealed record OpenCodeBillionDiscoveryResult(OpenCodeBillionDiscoveryStatus Status,
    string? Origin = null, int? HttpStatus = null);

internal static class OpenCodeBillionDiscoveryJson
{
    internal static bool LoopbackHost(Uri uri) => uri.Host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    internal static OpenCodeBillionDiscoveryResult Parse(JsonElement root)
    {
        static OpenCodeBillionDiscoveryResult Bad() => new(OpenCodeBillionDiscoveryStatus.Malformed);
        if (!ExactObject(root, "output") || !root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)) return Bad();
        if (number != 1) return new(OpenCodeBillionDiscoveryStatus.UnsupportedProtocol);
        if (!output.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) return Bad();
        if (status.GetString() == "ready")
        {
            if (!ExactObject(output, "protocolVersion", "status", "origin")
                || !output.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.String
                || !TryOrigin(origin.GetString()!, out var normalized)) return Bad();
            return new(OpenCodeBillionDiscoveryStatus.Ready, normalized);
        }
        if (status.GetString() != "unavailable" || !ExactObject(output, "protocolVersion", "status", "reason")
            || !output.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String) return Bad();
        return reason.GetString() switch
        {
            "not-ready" => new(OpenCodeBillionDiscoveryStatus.NotReady),
            "disabled" => new(OpenCodeBillionDiscoveryStatus.Disabled),
            _ => Bad(),
        };
    }

    private static bool ExactObject(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    private static bool TryOrigin(string value, out string? origin)
    {
        origin = null;
        if (value.Length is 0 or > 256 || value.IndexOfAny(new[] { '@', '?', '#', '\\' }) >= 0) return false;
        foreach (var ch in value) if (char.IsWhiteSpace(ch) || char.IsControl(ch)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || !LoopbackHost(uri) || uri.Port <= 0 || uri.UserInfo.Length != 0) return false;
        int start = value.IndexOf("://", StringComparison.Ordinal) + 3;
        int slash = value.IndexOf('/', start);
        if (start < 3 || slash >= 0 && value[slash..] != "/") return false; // Reject raw dot segments before URI normalization.
        var authority = slash < 0 ? value[start..] : value[start..slash];
        int colon = authority.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(authority[(colon + 1)..], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var port) || port != uri.Port) return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
