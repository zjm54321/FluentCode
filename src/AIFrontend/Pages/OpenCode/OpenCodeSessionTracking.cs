using System;
using System.Collections.Generic;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 请求宿主（App/MainWindow）跟踪某会话的原因。仅表达“本页当前关注该会话”，
/// 不表示执行结果，也不携带任何会话正文或凭据。
/// </summary>
public enum OpenCodeSessionTrackReason
{
    /// <summary>用户选择或本页自动选中了一个会话。</summary>
    Selected = 0,

    /// <summary>用户在本页新建了会话。</summary>
    Created = 1,

    /// <summary>用户在本页向会话发送了一条输入（仅表示已接纳并排程，不代表完成）。</summary>
    PromptSent = 2,
}

/// <summary>
/// 会话跟踪请求事件参数（<c>OpenCodePage.SessionTracked</c>）。
/// 只含不透明会话 ID 与结构化原因；宿主据此调用 App 的 <c>TrackOpenCodeSession</c>。
/// </summary>
public sealed class OpenCodeSessionTrackedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="sessionId">会话 ID（模式 ^ses）。</param>
    /// <param name="reason">跟踪原因。</param>
    public OpenCodeSessionTrackedEventArgs(string sessionId, OpenCodeSessionTrackReason reason)
    {
        SessionId = sessionId ?? string.Empty;
        Reason = reason;
    }

    /// <summary>会话 ID。</summary>
    public string SessionId { get; }

    /// <summary>跟踪原因。</summary>
    public OpenCodeSessionTrackReason Reason { get; }
}

/// <summary>A closed tab retires observation only for the source that owned it.</summary>
public sealed class OpenCodeSessionRetiredEventArgs(OpenCodePageSource source, string sessionId) : EventArgs
{
    public OpenCodePageSource Source { get; } = source;
    public string SessionId { get; } = sessionId;
}

/// <summary>
/// 回合级执行状态。<see cref="Working"/> 由“输入已接纳并排程”产生；
/// 终态（<see cref="Completed"/> / <see cref="Failed"/> / <see cref="Cancelled"/>）
/// <strong>仅</strong>由已核实的 idle 回合终态投影产生——绝不把 prompt 回执、静默或单条
/// assistant 消息当作完成。
/// </summary>
public enum OpenCodeExecutionState
{
    /// <summary>已有输入被接纳并排程，尚未观察到回合终态。</summary>
    Working = 0,

    /// <summary>idle.outcome = succeeded（回合级成功，不代表某次 prompt 的请求级成功）。</summary>
    Completed = 1,

    /// <summary>idle.outcome = failed。</summary>
    Failed = 2,

    /// <summary>idle.outcome = interrupted。</summary>
    Cancelled = 3,
}

/// <summary>
/// 执行状态变化事件参数（<c>OpenCodePage.ExecutionStateChanged</c>）。
/// 终态携带产生该状态的 idle 消息 ID，供宿主去重/关联；<see cref="Working"/> 无消息 ID。
/// </summary>
public sealed class OpenCodeExecutionStateChangedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="sessionId">会话 ID。</param>
    /// <param name="state">新的回合级执行状态。</param>
    /// <param name="messageId">产生终态的 idle 消息 ID；<see cref="OpenCodeExecutionState.Working"/> 时为 null。</param>
    public OpenCodeExecutionStateChangedEventArgs(string sessionId, OpenCodeExecutionState state, string? messageId)
    {
        SessionId = sessionId ?? string.Empty;
        State = state;
        MessageId = messageId;
    }

    /// <summary>会话 ID。</summary>
    public string SessionId { get; }

    /// <summary>回合级执行状态。</summary>
    public OpenCodeExecutionState State { get; }

    /// <summary>产生终态的 idle 消息 ID（仅终态非 null）。</summary>
    public string? MessageId { get; }
}

/// <summary>
/// 纯策略：从消息投影读取“回合级”执行终态。无 WinUI / Notifications 依赖，可隔离编译。
/// </summary>
/// <remarks>
/// 依据 docs/opencode-client-contract.md 第 7 节：idle 消息是回合终态边界，
/// 但不能通过 <c>type=idle</c> 过滤获取；本读取器只识别已核实的 outcome 取值，
/// 未知/缺失一律返回 null（不猜测成功）。
/// </remarks>
internal static class OpenCodeExecutionStateReader
{
    /// <summary>
    /// 返回按展示顺序（旧→新）排列的消息中最后一个 idle 消息；不存在返回 null。
    /// </summary>
    internal static OpenCodeMessage? LatestIdle(IReadOnlyList<OpenCodeMessage>? messages)
    {
        if (messages is null)
        {
            return null;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message is not null && message.Type == OpenCodeMessageTypes.Idle)
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>
    /// 把 idle.outcome 映射为回合级执行状态；未知或缺失返回 null（不猜测）。
    /// </summary>
    internal static OpenCodeExecutionState? MapOutcome(string? outcome) => outcome switch
    {
        OpenCodeSessionOutcomes.Succeeded => OpenCodeExecutionState.Completed,
        OpenCodeSessionOutcomes.Failed => OpenCodeExecutionState.Failed,
        OpenCodeSessionOutcomes.Interrupted => OpenCodeExecutionState.Cancelled,
        _ => null,
    };
}
