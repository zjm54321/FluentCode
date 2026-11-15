using System.Collections.Generic;

namespace FluentCode.Services.OpenCode;

/// <summary>One notice about the bridge. Actionable ones offer "查看详情" and stay until closed; the rest fade.</summary>
internal sealed record OpenCodeDesktopBridgeNotice(string Title, string Message, bool Actionable);

/// <summary>
/// Everything the client says about the Desktop Bridge, in one place. The facts behind the wording: the bridge is
/// compiled into the OpenCode QoL server and cannot be installed on its own; what helps is running (or updating)
/// OpenCode QoL and restarting the server. On a server that is not OpenCode QoL its absence is normal, so the text
/// describes optional features and offers nothing to fix.
/// </summary>
internal static class OpenCodeDesktopBridgeGuidance
{
    internal const string Name = "Desktop Bridge";
    internal const string DetailsAction = "查看详情";
    internal const string ProjectLink = "了解 OpenCode QoL";

    /// <summary>Short state for the connection-details row.</summary>
    internal static string StatusText(OpenCodeDesktopBridgeStatus status) => status switch
    {
        OpenCodeDesktopBridgeStatus.Ready => "已连接",
        OpenCodeDesktopBridgeStatus.Missing => "未安装",
        OpenCodeDesktopBridgeStatus.Outdated => "版本过旧",
        OpenCodeDesktopBridgeStatus.Mismatch => "版本不匹配",
        _ => "无法确认",
    };

    /// <summary>Something to fix (true) or plain information about optional features (false).</summary>
    internal static bool NeedsAttention(OpenCodeDesktopBridgeStatus status, OpenCodeServerProductKind product) => status switch
    {
        OpenCodeDesktopBridgeStatus.Outdated or OpenCodeDesktopBridgeStatus.Mismatch => true,
        OpenCodeDesktopBridgeStatus.Missing => product == OpenCodeServerProductKind.OpenCodeQol,
        _ => false,
    };

    /// <summary>Whether the page of the project is worth offering: only when a different server build would help.</summary>
    internal static bool OffersProjectLink(OpenCodeDesktopBridgeStatus status)
        => status is OpenCodeDesktopBridgeStatus.Missing or OpenCodeDesktopBridgeStatus.Outdated or OpenCodeDesktopBridgeStatus.Mismatch;

    /// <summary>The features this client loses with the given answer, in words ("模型收藏、自动发现 Billion 地址").</summary>
    internal static string Affected(OpenCodeDesktopBridgeState? state)
    {
        var missing = new List<string>(2);
        if (state?.Supports(OpenCodeDesktopBridgeState.FavoritesCapability) != true) missing.Add("模型收藏");
        if (state?.Supports(OpenCodeDesktopBridgeState.BillionOriginCapability) != true) missing.Add("自动发现 Billion 地址");
        return string.Join("、", missing);
    }

    /// <summary>The once-per-server notice; null when there is nothing to say (ready, or not known).</summary>
    internal static OpenCodeDesktopBridgeNotice? Notice(OpenCodeDesktopBridgeState? state, OpenCodeServerProductKind product)
    {
        var status = state?.Status ?? OpenCodeDesktopBridgeStatus.Unknown;
        var affected = Affected(state);
        return status switch
        {
            OpenCodeDesktopBridgeStatus.Missing when product == OpenCodeServerProductKind.OpenCodeQol => new(
                "Desktop Bridge 未启用", affected + "暂不可用。更新 OpenCode QoL 并重启服务后再试。", true),
            OpenCodeDesktopBridgeStatus.Missing => new(
                "OpenCode QoL 独享功能", affected + "需要 OpenCode QoL 服务端，当前服务没有提供。其他功能不受影响，详见连接详情。", false),
            OpenCodeDesktopBridgeStatus.Outdated => new(
                "Desktop Bridge 版本过旧", affected + "暂不可用。更新 OpenCode QoL 并重启服务后可用。", true),
            OpenCodeDesktopBridgeStatus.Mismatch => new(
                "Desktop Bridge 版本不匹配", affected + "暂不可用。把 FluentCode 和 OpenCode QoL 都更新到最新版后再试。", true),
            _ => null,
        };
    }

    /// <summary>The paragraph on the bridge's page in the connection details.</summary>
    internal static string Detail(OpenCodeDesktopBridgeState? state, OpenCodeServerProductKind product)
    {
        var status = state?.Status ?? OpenCodeDesktopBridgeStatus.Unknown;
        var affected = Affected(state);
        return status switch
        {
            OpenCodeDesktopBridgeStatus.Ready => "随 OpenCode QoL 服务端提供，用于模型收藏和自动发现 Billion 地址。",
            OpenCodeDesktopBridgeStatus.Missing when product == OpenCodeServerProductKind.OpenCodeQol =>
                "这个 OpenCode QoL 服务端没有 Desktop Bridge，" + affected + "暂不可用。它不能单独安装：更新 OpenCode QoL 并重启服务后再试。",
            OpenCodeDesktopBridgeStatus.Missing =>
                affected + "由 Desktop Bridge 提供。它随 OpenCode QoL 服务端一起提供，不能单独安装：改用 OpenCode QoL 运行服务并重启后即可使用。其他功能不受影响。",
            OpenCodeDesktopBridgeStatus.Outdated =>
                "服务端的 Desktop Bridge 版本过旧，" + affected + "暂不可用。它不能单独安装：更新 OpenCode QoL 并重启服务后可用。",
            OpenCodeDesktopBridgeStatus.Mismatch =>
                "FluentCode 和服务端的 Desktop Bridge 版本对不上，" + affected + "暂不可用。把 FluentCode 和 OpenCode QoL 都更新到最新版后再试。",
            _ => "还没有确认服务端是否带有 Desktop Bridge；" + affected + "可能暂不可用。",
        };
    }
}
