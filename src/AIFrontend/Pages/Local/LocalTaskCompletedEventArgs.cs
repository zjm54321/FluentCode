using System;

namespace AIFrontend.Pages.Local;

/// <summary>
/// Local 聊天一轮“真实成功”完成时的事件参数。
/// </summary>
/// <remarks>
/// 语义（重要）：仅当协议流返回 <see cref="AIFrontend.Models.ChatStreamEventKind.Completed"/>
/// 且结束原因为 <see cref="AIFrontend.Models.ChatFinishReason.Stop"/> 时才触发。错误、用户取消、
/// 工具调用终止、长度/内容过滤截断、传输中断等所有非正常结束<strong>不会</strong>触发本事件，
/// 以免把失败伪装成成功。
/// <para>
/// 本控件<strong>不</strong>注册、不发送任何系统通知；本事件只是供父页面在后续接入通知时使用。
/// </para>
/// </remarks>
public sealed class LocalTaskCompletedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="sourceRequestId">发起本轮请求时生成的稳定 ID（供通知去重）。</param>
    /// <param name="sessionId">产生该完成的本地会话 ID。</param>
    public LocalTaskCompletedEventArgs(string sourceRequestId, string sessionId)
    {
        SourceRequestId = sourceRequestId ?? throw new ArgumentNullException(nameof(sourceRequestId));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
    }

    /// <summary>发起本轮请求时生成的稳定 ID；同一次请求幂等重放时保持不变。</summary>
    public string SourceRequestId { get; }

    /// <summary>产生该完成的本地会话 ID（供通知展示定位）。</summary>
    public string SessionId { get; }
}
