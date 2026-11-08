using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentCode.Services.OpenCode.Browser;

namespace FluentCode.Pages.OpenCode;

/// <summary>UI-independent BQ3/BQ4/BQ10/BQ13 policy. A state belongs to one source's workspace.</summary>
internal sealed class OpenCodeBrowserPanelState
{
    public bool Expanded { get; private set; }
    public void Toggle() => Expanded = !Expanded;
    public void Expand() => Expanded = true;
    public void AgentActivated() => Expand(); // Never calls Show: background activity must not open the overlay.
    public bool ShowCommands(int count) => Expanded && count > 0;
    public static bool ColoredIcon(int count) => count > 0;
    public static bool ShowCount(int count) => count >= 2;
    public static string StatusBrushKey(OpenCodeBrowserAvailability availability) => availability switch
    {
        OpenCodeBrowserAvailability.Available => "SystemFillColorSuccessBrush",
        OpenCodeBrowserAvailability.Attaching or OpenCodeBrowserAvailability.Reconnecting => "SystemFillColorCautionBrush",
        _ => "SystemFillColorCriticalBrush",
    };
    public static string TooltipTitle(int count) => count == 0 ? "浏览器 · 打开浏览器页面" : $"浏览器 · {count} 个页面";
    public static string TooltipStatus(OpenCodeBrowserAvailability availability, string? reason) => availability == OpenCodeBrowserAvailability.Available
        ? "智能体可以使用浏览器" : reason ?? (availability switch
        {
            OpenCodeBrowserAvailability.Attaching => "正在接入浏览器…",
            OpenCodeBrowserAvailability.Reconnecting => "与 OpenCode 的连接断开，正在重连…",
            OpenCodeBrowserAvailability.Replaced => "这个会话的浏览器现在由另一个桌面应用提供",
            OpenCodeBrowserAvailability.Unsupported => "这个 OpenCode 版本不支持让智能体使用浏览器",
            _ => "智能体暂时不能使用浏览器：会话尚未接入",
        });
    public static string CompactTooltipTitle(int count) => count == 0 ? "浏览器" : $"浏览器（{count} 个页面）";
    public static string CompactTooltipStatus(OpenCodeBrowserAvailability availability, string? reason) => reason switch
    {
        "智能体可以使用浏览器" or "智能体可以使用浏览器（45 个操作）" => "已连接",
        "正在接入浏览器…" => "连接中",
        "与 OpenCode 的连接断开，正在重连…" or "智能体暂时不能使用浏览器：与 OpenCode 的连接断开了，正在重连…" => "重连中",
        "这个会话的浏览器现在由另一个桌面应用提供" or "这个会话的浏览器现在由另一台电脑 / 另一个窗口提供" => "已由其他应用接管",
        "这个 OpenCode 版本不支持让智能体使用浏览器" => "当前版本不支持",
        "智能体暂时不能使用浏览器：会话尚未接入" or "此会话尚未接入浏览器" => "未连接",
        "智能体暂时不能使用浏览器：接入失败，请重试" => "连接失败",
        _ => reason ?? (availability switch
        {
            OpenCodeBrowserAvailability.Available => "已连接",
            OpenCodeBrowserAvailability.Attaching => "连接中",
            OpenCodeBrowserAvailability.Reconnecting => "重连中",
            OpenCodeBrowserAvailability.Replaced => "已由其他应用接管",
            OpenCodeBrowserAvailability.Unsupported => "当前版本不支持",
            _ => "未连接",
        }),
    };
    public static string? ReclaimLabel(OpenCodeBrowserAvailability availability) => availability switch
    {
        OpenCodeBrowserAvailability.Replaced => "改为在这里提供",
        OpenCodeBrowserAvailability.Reconnecting => "立即重连",
        _ => null,
    };
    public static IReadOnlyList<IOpenCodeBrowserPage> AgentOpened(IOpenCodeBrowserWorkspace workspace) => workspace.Pages.Where(p => p.OpenedByAgent).ToArray();
    public static string PageState(IOpenCodeBrowserPage page, bool shown) => page.LoadError is not null ? "无法访问此页面"
        : page.AgentActivity is not null ? "智能体正在操作 · " + page.AgentActivity
        : page.Loading ? "正在加载…" : page.PreviewPath is not null ? "预览 · " + PreviewKind(page.PreviewPath)
        : shown ? "正在浮窗中显示" : "页面已就绪";
    public static string PreviewKind(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } kind ? kind : "预览";
}

internal sealed class OpenCodeBrowserPreviewThrottle
{
    private DateTimeOffset? _lastAttempt;
    public bool Dirty { get; private set; } = true;
    public bool InFlight { get; private set; }
    public void Invalidate() => Dirty = true;
    public bool TryBegin(DateTimeOffset now)
    {
        if (!Dirty || InFlight || _lastAttempt is { } last && now - last < TimeSpan.FromSeconds(2)) return false;
        Dirty = false; InFlight = true; _lastAttempt = now; return true;
    }
    public void Complete(DateTimeOffset? completedAt = null)
    {
        InFlight = false;
        if (completedAt is { } time) _lastAttempt = time;
    }
}
