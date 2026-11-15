using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>Display-only projection. Missing metadata stays unavailable; no path synthesis.</summary>
internal sealed record OpenCodeRightPanelPresentation(string SessionId, string Title, string Directory)
{
    public static OpenCodeRightPanelPresentation? Create(bool hasSession, string? sessionId, string? title, string? directory)
        => !hasSession || string.IsNullOrWhiteSpace(sessionId) ? null
            : new(sessionId, string.IsNullOrWhiteSpace(title) ? "未命名会话" : title,
                string.IsNullOrWhiteSpace(directory) ? "不可用" : directory);

    /// <summary>Text beside the wordmark: the version, with the short product tag on OpenCode QoL ("QoL 2.2.5").</summary>
    public static string Version(string? version, OpenCodeServerProductKind product = OpenCodeServerProductKind.Unknown)
        => string.IsNullOrWhiteSpace(version) ? "不可用" : OpenCodeServerProduct.BesideWordmark(product, version);

    /// <summary>Narrator name of the footer row; it names the product in full.</summary>
    public static string ConnectionName(string versionText, OpenCodeServerProductKind product, string? version)
        => OpenCodeServerProduct.Name(product) + " 服务版本：" + (string.IsNullOrWhiteSpace(version) ? versionText : version) + "；查看连接详情";

    public static bool ShowConnectedDot(bool pageActive, bool sourceReady, bool connected)
        => pageActive && sourceReady && connected;
}
