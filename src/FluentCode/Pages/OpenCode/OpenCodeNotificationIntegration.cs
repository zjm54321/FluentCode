using System;
using FluentCode.Services.Notifications;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// <c>OpenCodePage.NavigateToNotificationAsync</c> 的处理结果状态（结构化、可被 App 消费）。
/// <see cref="Succeeded"/> 只表示页面已定位并聚焦目标（或在带 result 时表示服务器已接受该决定），
/// <strong>绝不</strong>表示 AI 已完成回合。
/// </summary>
public enum OpenCodeNavigationStatus
{
    /// <summary>已定位会话并聚焦目标（Completion 为滚动到时间线底部）；带 result 时另见 Detail。</summary>
    Succeeded = 0,

    /// <summary>页面未就绪（未加载/后端未配置/初始化中）：目标已排队，稍后处理。</summary>
    Queued = 1,

    /// <summary>宿主尚未提供可信 endpointIdentity、或目标需要应用侧处理：本页不猜测身份、不提交。</summary>
    NeedsApp = 2,

    /// <summary>目标会话不在可枚举窗口内（分页预算内未找到）；不代表一定不存在。</summary>
    Gap = 3,

    /// <summary>定位/读取失败（网络、超时、协议）；未提交任何决定。</summary>
    ReadFailed = 4,

    /// <summary>会话已定位，但目标 Permission/Form 当前不处于待处理状态（或尚未出现）。</summary>
    TargetNotFound = 5,

    /// <summary>目标来源身份与本页可信身份不匹配（或 result 与 target 不一致）：已拒绝，不定位、不提交。</summary>
    Refused = 6,
}

/// <summary>
/// <c>OpenCodePage.NavigationHandled</c> 事件参数：只含结构化状态、固定诊断文案与不透明标识，
/// 不含聊天正文、权限资源、表单答案或凭据。
/// </summary>
public sealed class OpenCodeNavigationResultEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="status">结构化处理结果。</param>
    /// <param name="detail">固定诊断文案（不含敏感内容）。</param>
    /// <param name="target">原始导航目标（不透明标识）。</param>
    public OpenCodeNavigationResultEventArgs(OpenCodeNavigationStatus status, string detail, NotificationNavigationTarget? target)
    {
        Status = status;
        Detail = detail;
        Target = target;
    }

    /// <summary>结构化处理结果。</summary>
    public OpenCodeNavigationStatus Status { get; }

    /// <summary>固定诊断文案。</summary>
    public string Detail { get; }

    /// <summary>原始导航目标（不透明标识）。</summary>
    public NotificationNavigationTarget? Target { get; }
}
