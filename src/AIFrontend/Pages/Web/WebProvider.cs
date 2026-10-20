using System;
using System.Collections.Generic;
using System.IO;

namespace AIFrontend.Pages.Web;

/// <summary>
/// 官方站点 provider 的封闭枚举。
/// </summary>
/// <remarks>
/// 环境目录名与导航地址只由该枚举推导，绝不来自 UI 文本或任何用户输入，
/// 因此不存在路径注入，也不会导航到任意 URI。新增站点必须同时扩展本枚举及其元数据。
/// </remarks>
public enum WebProvider
{
    ChatGPT = 0,
    Gemini = 1,
    Claude = 2,
}

/// <summary>
/// provider 的固定元数据（显示名 / 环境目录名 / 官网地址 / newwindow 允许域名）。
/// 全部为硬编码常量；无网络、无 IO、无用户输入。
/// </summary>
public static class WebProviderInfo
{
    /// <summary>应用数据目录名。与 Services.Configuration 的路径约定保持一致；本 lane 不依赖该服务，仅沿用约定。</summary>
    public const string AppFolderName = "AIFrontend";

    /// <summary>WebView2 用户数据根目录名，位于应用数据目录之下。</summary>
    public const string WebView2FolderName = "WebView2";

    /// <summary>全部官方站点 provider（顺序与选择栏一致）。</summary>
    public static IReadOnlyList<WebProvider> All { get; } = new[]
    {
        WebProvider.ChatGPT,
        WebProvider.Gemini,
        WebProvider.Claude,
    };

    /// <summary>provider 的显示名。</summary>
    public static string GetDisplayName(WebProvider provider) => provider switch
    {
        WebProvider.ChatGPT => "ChatGPT",
        WebProvider.Gemini => "Gemini",
        WebProvider.Claude => "Claude",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>环境子目录名（硬编码常量，非用户输入）。</summary>
    public static string GetFolderName(WebProvider provider) => provider switch
    {
        WebProvider.ChatGPT => "ChatGPT",
        WebProvider.Gemini => "Gemini",
        WebProvider.Claude => "Claude",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>官网固定地址（https）。</summary>
    public static Uri GetHomeUri(WebProvider provider) => provider switch
    {
        WebProvider.ChatGPT => new Uri("https://chatgpt.com/"),
        WebProvider.Gemini => new Uri("https://gemini.google.com/"),
        WebProvider.Claude => new Uri("https://claude.ai/"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// <c>NewWindowRequested</c> 允许“在同一 WebView 内”导航的主机后缀（含其子域）。
    /// 只列 provider 自身与其母公司域名，不含任何第三方，避免执行任意外部 URI。
    /// </summary>
    public static IReadOnlyList<string> GetAllowedHostSuffixes(WebProvider provider) => provider switch
    {
        WebProvider.ChatGPT => new[] { "chatgpt.com", "openai.com" },
        WebProvider.Gemini => new[] { "google.com", "googleusercontent.com" },
        WebProvider.Claude => new[] { "claude.ai", "anthropic.com" },
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>WebView2 用户数据根目录：<c>%LOCALAPPDATA%\AIFrontend\WebView2</c>（绝对路径）。</summary>
    public static string GetWebView2RootDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("无法获取 LocalApplicationData，拒绝回退到当前工作目录。");
        }

        return Path.GetFullPath(Path.Combine(localAppData, AppFolderName, WebView2FolderName));
    }

    /// <summary>某个 provider 的独立、固定的 WebView2 用户数据目录（绝对路径）。</summary>
    public static string GetUserDataFolder(WebProvider provider) =>
        Path.Combine(GetWebView2RootDirectory(), GetFolderName(provider));

    /// <summary>主机是否属于 provider 允许的域名（精确匹配或子域匹配，Ordinal 忽略大小写）。</summary>
    public static bool IsHostAllowed(WebProvider provider, string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        foreach (string suffix in GetAllowedHostSuffixes(provider))
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 严格解析选择栏 Tag：仅接受与已知 provider 名称完全一致的字符串（Ordinal）。
    /// 未知值（含 <c>Local</c>、数字、任意文本）一律返回 false，不产生任何路径或 URI。
    /// </summary>
    public static bool TryParseProvider(string? tag, out WebProvider provider)
    {
        provider = default;

        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        foreach (WebProvider candidate in All)
        {
            if (string.Equals(GetFolderName(candidate), tag, StringComparison.Ordinal))
            {
                provider = candidate;
                return true;
            }
        }

        return false;
    }
}
