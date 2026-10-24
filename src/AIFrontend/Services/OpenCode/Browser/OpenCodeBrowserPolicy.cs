using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AIFrontend.Services.OpenCode.Browser;

public sealed class OpenCodeBrowserException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Protocol policy shared by the address bar and the command executor; no WebView dependency.</summary>
public static class OpenCodeBrowserPolicy
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public static string NormalizeUrl(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text == "about:blank") return "about:blank";
        if (text.Length > 2048 || text.Contains('\\') || text.StartsWith('/')
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-zA-Z]:[/\\]"))
            throw new OpenCodeBrowserException("invalid_input", "请输入 HTTP 或 HTTPS 网址，不支持文件路径");
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")
                && !System.Text.RegularExpressions.Regex.IsMatch(text, @"^[^/:]+:[0-9]+(?:[/?#]|$)"))
                throw new OpenCodeBrowserException("invalid_input", "仅支持 HTTP、HTTPS 和 about:blank");
            var host = text.Split('/', '?', '#')[0];
            var local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("127.", StringComparison.Ordinal) || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
                || host.StartsWith("[::1]:", StringComparison.OrdinalIgnoreCase);
            text = (local ? "http://" : "https://") + text;
        }
        if (text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0].Contains('@')
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0)
            throw new OpenCodeBrowserException("invalid_input", "网址无效：仅支持不含用户名和密码的 HTTP / HTTPS 网址");
        return uri.AbsoluteUri;
    }

    public static void CheckPage(long? requested, long current, bool takeover)
    {
        if (takeover) throw new OpenCodeBrowserException("user_takeover", "用户已接管此页面");
        if (requested is < 0) throw new OpenCodeBrowserException("invalid_input", "generation 必须为非负整数");
        if (requested < current) throw new OpenCodeBrowserException("stale_generation", "页面已导航，请重新读取页面状态");
    }

    public static JsonObject Tab(OpenCodeBrowserTab tab)
    {
        var value = new JsonObject { ["id"] = tab.Id, ["url"] = tab.Url, ["title"] = tab.Title, ["loading"] = tab.Loading,
            ["canGoBack"] = tab.CanGoBack, ["canGoForward"] = tab.CanGoForward, ["generation"] = tab.Generation };
        if (tab.LoadError is not null) value["loadError"] = tab.LoadError;
        return value;
    }

    public static JsonObject State(OpenCodeBrowserState state) => new()
    {
        ["tabs"] = new JsonArray(state.Tabs.Select(t => (JsonNode)Tab(t)).ToArray()), ["focusedTabID"] = state.FocusedTabId,
    };
    public static string Bound(string? text, int max = 2048) => text is null ? string.Empty : text[..Math.Min(text.Length, max)];
}

/// <summary>Oldest entries are evicted, never represented as a successful empty capture.</summary>
public sealed class OpenCodeBrowserRing<T>(int capacity)
{
    private readonly List<T> _items = new();
    public long Dropped { get; private set; }
    public IReadOnlyList<T> Items => _items;
    public void Add(T value)
    {
        if (_items.Count == capacity) { _items.RemoveAt(0); Dropped++; }
        _items.Add(value);
    }
    public void Clear() { _items.Clear(); Dropped = 0; }
}
