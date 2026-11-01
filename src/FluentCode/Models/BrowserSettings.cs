using System;
using System.IO;
using System.Net;

namespace FluentCode.Models;

public enum BrowserEngine { WebView2, Chrome }
public enum BrowserChromeConnection { Managed, Attach }

/// <summary>Snapshot once when creating a workspace; never reconfigure running tabs.</summary>
public sealed record BrowserSettings
{
    public BrowserEngine Engine { get; init; } = BrowserEngine.WebView2;
    public string? ChromeExecutablePath { get; init; }
    public BrowserChromeConnection ChromeConnection { get; init; } = BrowserChromeConnection.Managed;
    public string? ChromeEndpoint { get; init; }

    public string? ValidationError()
    {
        if (!Enum.IsDefined(Engine) || !Enum.IsDefined(ChromeConnection)) return "浏览器引擎或 Chrome 连接方式无效。";
        if (!string.IsNullOrWhiteSpace(ChromeExecutablePath) &&
            (!Path.IsPathFullyQualified(ChromeExecutablePath) || !ChromeExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
             || ChromeExecutablePath.IndexOfAny(new[] { '\r', '\n', '\0', '"' }) >= 0))
            return "Chrome 路径应为完整的 .exe 文件路径，不要包含引号或启动参数。";
        if (ChromeEndpoint is { Length: > 0 } endpoint && !IsLoopbackEndpoint(endpoint))
            return "Chrome 调试端点仅允许不含凭据或片段的本机 HTTP(S) / WS(S) 地址。";
        if (Engine == BrowserEngine.Chrome && ChromeConnection == BrowserChromeConnection.Attach && string.IsNullOrWhiteSpace(ChromeEndpoint))
            return "连接已有 Chrome 时，请填写本机调试端点。";
        return null;
    }

    public static bool IsLoopbackEndpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "ws" or "wss" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
