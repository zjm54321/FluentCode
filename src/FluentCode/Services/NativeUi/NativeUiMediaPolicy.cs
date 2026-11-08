// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Text.RegularExpressions;

namespace FluentCode.Services.NativeUi;

internal static class NativeUiMediaPolicy
{
    internal static bool Https(string value) => value.Length <= 4096 && NativeUiImages.Https(value) && new Uri(value).UserInfo.Length == 0;
    internal static string? YouTubeId(string value)
    {
        if (Regex.IsMatch(value, "^[A-Za-z0-9_-]{11}$")) return value;
        if (!Https(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be" or "www.youtu.be" or "www.youtube-nocookie.com")) return null;
        var match = Regex.Match(uri.AbsoluteUri, @"(?:youtu\.be/|[?&]v=|/embed/|/shorts/)([A-Za-z0-9_-]{11})(?:[?&#/]|$)");
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>Per-card user consent. Rebinding a source revokes the previous load request.</summary>
internal sealed class NativeUiLoadGate
{
    internal string Source { get; private set; } = "";
    internal bool Requested { get; private set; }
    internal void Bind(string source) { if (Source == source) return; Source = source; Requested = false; }
    internal bool Request() { if (Source.Length == 0) return false; Requested = true; return true; }
}
