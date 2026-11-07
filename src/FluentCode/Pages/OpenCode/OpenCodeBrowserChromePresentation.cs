using System;
using System.Collections.Generic;
using FluentCode.Models;

namespace FluentCode.Pages.OpenCode;

/// <summary>What the address field says about a page: where it comes from, its address split for emphasis, and its title as secondary text.</summary>
internal sealed record OpenCodeBrowserAddressPresentation(
    string Glyph,
    string? OriginLabel,
    string Lead,
    string Emphasis,
    string Trail,
    string? Title,
    string ToolTip,
    bool IsBlank);

/// <summary>Width each part of the address line receives.</summary>
internal readonly record struct OpenCodeBrowserAddressFit(double Lead, double Emphasis, double Trail, double Side);

/// <summary>Text and layout decisions for the browser chrome. No WinUI types, so they can be tested without a window.</summary>
internal static class OpenCodeBrowserChromePresentation
{
    private const string LockGlyph = "\uE72E", InfoGlyph = "\uE946", GlobeGlyph = "\uE774", NetworkGlyph = "\uE968", FileGlyph = "\uE8A5";
    private const double SideGap = 16, MinSideWidth = 72;
    private static readonly char[] AuthorityEnd = ['/', '?', '#'];
    private static readonly char[] PathSeparators = ['/', '\\'];

    internal static OpenCodeBrowserAddressPresentation Describe(string? url, string? title, string? previewPath, string? loadError, OpenCodeConnectionKind? connection)
    {
        if (previewPath is not null)
        {
            var cut = previewPath.LastIndexOfAny(PathSeparators) + 1;
            return new(FileGlyph, "服务器文件", previewPath[..cut], previewPath[cut..], "", null,
                previewPath + "\n服务器文件 · 由 OpenCode 服务器读取，不是这台电脑上的路径", false);
        }
        if (string.IsNullOrWhiteSpace(url) || url == "about:blank")
            return new(GlobeGlyph, null, "", "", "", null, "输入网址", true);

        var (lead, emphasis, trail) = SplitUrl(url);
        var shownTitle = loadError is not null ? "无法访问此页面"
            : string.IsNullOrWhiteSpace(title) || RepeatsAddress(title, url, emphasis, trail) ? null : title.Trim();
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        var (glyph, label, note) = Origin(uri, connection);

        var lines = new List<string>(3);
        if (shownTitle is not null) lines.Add(shownTitle);
        lines.Add(url);
        if (note is not null) lines.Add(note);
        return new(glyph, label, lead, emphasis, trail, shownTitle, string.Join('\n', lines), false);
    }

    private static (string Glyph, string? Label, string? Note) Origin(Uri? uri, OpenCodeConnectionKind? connection)
    {
        // Loopback addresses resolve on the server side, which is the thing worth saying about them.
        if (uri is { IsLoopback: true, Scheme: "http" or "https" })
            return connection == OpenCodeConnectionKind.Wsl
                ? (NetworkGlyph, "WSL 网络", "WSL 网络 · 这个地址在 WSL 里访问，不是在 Windows 上")
                : (NetworkGlyph, "远程服务器", "远程服务器 · 这个地址在远程服务器上访问，不是在这台电脑上");
        return uri?.Scheme switch
        {
            "https" => (LockGlyph, null, "HTTPS 连接"),
            "http" => (InfoGlyph, null, "HTTP 连接（未加密）"),
            _ => (GlobeGlyph, null, null),
        };
    }

    /// <summary>The host or file name stays whole longest; the title only gets the room the address leaves over.</summary>
    internal static OpenCodeBrowserAddressFit FitAddress(double available, double lead, double emphasis, double trail, double side)
    {
        var room = Math.Max(0, available);
        var fittedEmphasis = Math.Min(emphasis, room); room -= fittedEmphasis;
        var fittedLead = Math.Min(lead, room); room -= fittedLead;
        var fittedTrail = Math.Min(trail, room); room -= fittedTrail;
        var fittedSide = side > 0 && room - SideGap >= Math.Min(side, MinSideWidth) ? Math.Min(side, room - SideGap) : 0;
        return new(fittedLead, fittedEmphasis, fittedTrail, fittedSide);
    }

    private static (string Lead, string Emphasis, string Trail) SplitUrl(string url)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0) return ("", "", url);
        var start = separator + 3;
        var end = url.IndexOfAny(AuthorityEnd, start);
        if (end < 0) end = url.Length;
        // Credentials stay dim so the highlighted part is always the real host.
        var host = start + url[start..end].LastIndexOf('@') + 1;
        var trail = url[end..];
        return (url[..host], url[host..end], trail == "/" ? "" : trail);
    }

    private static bool RepeatsAddress(string title, string url, string emphasis, string trail)
    {
        var candidate = title.Trim().TrimEnd('/');
        return string.Equals(candidate, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, (emphasis + trail).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
