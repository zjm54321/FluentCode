using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIFrontend.Services.OpenCode.Browser;

namespace AIFrontend.Pages.OpenCode;

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
    public static bool ShowUnavailable(OpenCodeBrowserAvailability availability) => availability != OpenCodeBrowserAvailability.Available;
    public static string TooltipTitle(int count) => count == 0 ? "浏览器 · 打开浏览器页面" : $"浏览器 · {count} 个页面 · 点击展开缩略图";
    public static string TooltipStatus(OpenCodeBrowserAvailability availability, string? reason) => availability == OpenCodeBrowserAvailability.Available
        ? "智能体可以使用浏览器（45 个操作）" : reason ?? "智能体暂时不能使用浏览器：会话尚未接入";
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

/// <summary>Like the quick-access preference, stored beside app settings (works unpackaged too).</summary>
internal sealed class OpenCodeBrowserDiscoveryPreference(string path)
{
    public bool Seen() => File.Exists(path);
    public void MarkSeen()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "seen");
    }
}
