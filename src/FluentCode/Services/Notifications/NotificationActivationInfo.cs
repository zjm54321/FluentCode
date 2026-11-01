using System;

namespace FluentCode.Services.Notifications;

/// <summary>
/// 解析自通知激活参数的、经最小化处理的信息，用于向未来 App 集成暴露
/// “用户点击了本服务的完成通知”这一事实。
/// </summary>
/// <remarks>
/// 安全边界（重要）：
/// <list type="bullet">
/// <item><description>仅解析本服务写入的固定键（<c>action</c>/<c>source</c>/<c>requestId</c>），
/// 不反射、不执行任何参数、不做任何 I/O。</description></item>
/// <item><description>不持有原始参数字符串之外的任何外部内容；<see cref="RawArguments"/> 仅为诊断保留，
/// 调用方不得将其当作可执行输入。</description></item>
/// <item><description>该信息<strong>不</strong>代表应用已完成冷启动处理。未来 App 接线时
/// 必须自行区分“通知激活”与“普通启动”，并避免额外窗口（见 docs/notifications-and-tray.md）。</description></item>
/// </list>
/// </remarks>
public sealed class NotificationActivationInfo : EventArgs
{
    /// <summary>创建激活信息。</summary>
    /// <param name="isCompletionAction">激活参数中的 action 是否为本服务写入的完成动作。</param>
    /// <param name="source">识别出的来源；无法识别时为 null。</param>
    /// <param name="requestId">识别出的请求标识；缺失或非法时为 null。</param>
    /// <param name="rawArguments">平台提供的原始参数字符串，仅用于诊断。</param>
    public NotificationActivationInfo(
        bool isCompletionAction,
        NotificationSource? source,
        string? requestId,
        string rawArguments)
    {
        IsCompletionAction = isCompletionAction;
        Source = source;
        RequestId = requestId;
        RawArguments = rawArguments;
    }

    /// <summary>激活参数是否声明为本服务的“完成”动作。</summary>
    public bool IsCompletionAction { get; }

    /// <summary>识别出的来源；无法识别时为 null。</summary>
    public NotificationSource? Source { get; }

    /// <summary>识别出的请求标识；缺失或非法时为 null。</summary>
    public string? RequestId { get; }

    /// <summary>平台提供的原始参数字符串，仅用于诊断，不得作为可执行输入。</summary>
    public string RawArguments { get; }
}
