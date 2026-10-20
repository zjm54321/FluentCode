using System;

namespace AIFrontend.Pages.Local;

/// <summary>
/// Local 一次执行的<strong>结构化工作/终态</strong>。
/// </summary>
/// <remarks>
/// 与 <c>Services.Taskbar.TaskbarTaskState</c> 的 Working/Completed/Failed/Cancelled 语义保持一致；
/// 本枚举<strong>不</strong>含 <c>Pending</c> / <c>OutcomeUnknown</c>——Local 单轮请求不存在“待用户操作”
/// 或“提交结果未知”的中间态，不臆造二者。
/// </remarks>
public enum LocalTaskStatus
{
    /// <summary>执行已开始（本轮请求进行中）。</summary>
    Working = 0,

    /// <summary>正常完成（协议 Completed 且结束原因为 Stop）；与 <c>TaskCompleted</c> 成功条件一致。</summary>
    Completed = 1,

    /// <summary>非成功终态（错误、传输中断、长度/内容过滤/工具终止、结束原因未知等）。</summary>
    Failed = 2,

    /// <summary>用户/页面取消（<see cref="AIFrontend.Models.ChatFinishReason.Cancelled"/>）。</summary>
    Cancelled = 3,
}

/// <summary>
/// Local 一次执行的状态事件参数（<strong>结构化、无正文、无凭据</strong>）。
/// </summary>
/// <remarks>
/// 载荷仅含：执行标识 <see cref="SourceRequestId"/>（每次执行新建的不透明 GUID("N")，供任务栏/通知按执行关联）
/// 与 <see cref="SessionId"/>（本地会话 ID）。<strong>不包含</strong>消息正文、错误文本、模型输出或任何凭据。
/// <para>
/// 语义：每次执行先发 <see cref="LocalTaskStatus.Working"/>，随后<strong>恰好一次</strong>终态
/// （Completed/Failed/Cancelled）；所有异常与取消路径都会终结 Working，绝不滞留。
/// </para>
/// </remarks>
public sealed class LocalTaskStatusEventArgs : EventArgs
{
    /// <summary>创建状态事件参数。</summary>
    /// <param name="sourceRequestId">本轮执行的不透明稳定 ID（每次执行新建，不用于授权）。</param>
    /// <param name="sessionId">产生该执行的本地会话 ID。</param>
    /// <param name="status">结构化状态。</param>
    public LocalTaskStatusEventArgs(string sourceRequestId, string sessionId, LocalTaskStatus status)
    {
        SourceRequestId = sourceRequestId ?? throw new ArgumentNullException(nameof(sourceRequestId));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        Status = status;
    }

    /// <summary>本轮执行的不透明稳定 ID（同一执行的所有状态事件复用同一值）。</summary>
    public string SourceRequestId { get; }

    /// <summary>产生该执行的本地会话 ID。</summary>
    public string SessionId { get; }

    /// <summary>结构化状态。</summary>
    public LocalTaskStatus Status { get; }

    /// <summary>是否为终态（非 Working）。</summary>
    public bool IsTerminal => Status != LocalTaskStatus.Working;
}
