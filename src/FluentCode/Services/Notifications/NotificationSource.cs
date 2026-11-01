namespace FluentCode.Services.Notifications;

/// <summary>
/// 完成通知的来源任务类别。用于在通知中区分是哪一类任务完成，并作为
/// (source, requestId) 去重键的一半。
/// </summary>
/// <remarks>
/// 本枚举仅覆盖 OpenCode 任务；旧版来源标签不再可激活。
/// 后续如需新增来源，应同步更新 <see cref="CompletionNotificationService"/> 的
/// 显示名/参数标签映射，并更新 docs/notifications-and-tray.md。
/// </remarks>
public enum NotificationSource
{
    /// <summary>OpenCode 任务。</summary>
    OpenCode = 0,

}

/// <summary>
/// <see cref="NotificationSource"/> 的展示与序列化标签映射。
/// </summary>
internal static class NotificationSourceLabels
{
    /// <summary>
    /// 用于通知首个文本行（标题行）的展示名。正文固定为“任务已完成”，
    /// 来源差异仅体现在此展示名上。
    /// </summary>
    internal static string GetDisplayName(NotificationSource source) => source switch
    {
        NotificationSource.OpenCode => "OpenCode",
        _ => "任务",
    };

    /// <summary>
    /// 写入通知激活参数（key=value）的稳定 ASCII 标签，供未来 App 集成识别来源。
    /// 不得包含用户内容或密钥。
    /// </summary>
    internal static string GetArgumentTag(NotificationSource source) => source switch
    {
        NotificationSource.OpenCode => "opencode",
        _ => "unknown",
    };

    /// <summary>由激活参数标签反解来源；未知标签返回 null。</summary>
    internal static NotificationSource? TryParseTag(string? tag) => tag switch
    {
        "opencode" => NotificationSource.OpenCode,
        _ => null,
    };
}
