namespace FluentCode.Pages.OpenCode;

/// <summary>Display-only projection. Missing metadata stays unavailable; no path synthesis.</summary>
internal sealed record OpenCodeRightPanelPresentation(string SessionId, string Title, string Directory)
{
    public static OpenCodeRightPanelPresentation? Create(bool hasSession, string? sessionId, string? title, string? directory)
        => !hasSession || string.IsNullOrWhiteSpace(sessionId) ? null
            : new(sessionId, string.IsNullOrWhiteSpace(title) ? "未命名会话" : title,
                string.IsNullOrWhiteSpace(directory) ? "不可用" : directory);

    public static string Version(string? version) => string.IsNullOrWhiteSpace(version) ? "不可用" : version;

    public static bool ShowConnectedDot(bool pageActive, bool sourceReady, bool connected)
        => pageActive && sourceReady && connected;
}
