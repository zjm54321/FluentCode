using System;
using System.Collections.Immutable;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// 未知状态的独立事实原因（可合并）。仅描述"为什么未知"，
/// 与 Working/Pending 等业务状态无关，也不等同于 Pending。
/// </summary>
[Flags]
public enum OpenCodeStateUnknownReason
{
    None = 0,
    NotObserved = 1,
    Expired = 2,
    Incomplete = 4,
}

/// <summary>会话完成标记：Outcome 为完成结论，IdleWatermark 为完成时刻的观察水位。</summary>
public sealed record OpenCodeCompletionMark(string Outcome, double IdleWatermark);

/// <summary>
/// 单个会话的 UI 状态。Working/PendingAction 均为三值：null 表示未知。
/// UnknownReason 与 MutationProtected 不构成 Pending。
/// </summary>
public sealed record OpenCodeSessionUiState(
    string SessionId,
    bool? Working,
    bool? PendingAction,
    bool MutationProtected,
    OpenCodeCompletionMark? Completion,
    double? ViewedWatermark,
    OpenCodeStateUnknownReason UnknownReason)
{
    /// <summary>独立 question 事实；null 表示未观测或证据已过期。</summary>
    public bool? QuestionPending { get; init; }

    /// <summary>有 Completion 且（无 ViewedWatermark 或 IdleWatermark 更大）时为未读。</summary>
    public bool IsUnread =>
        Completion is not null &&
        (ViewedWatermark is null || Completion.IdleWatermark > ViewedWatermark.Value);

    /// <summary>PendingAction 为 true 或未读时需要引起注意。</summary>
    public bool NeedsAttention => PendingAction == true || IsUnread;
}

/// <summary>某端点某一 Revision/Version 下全部会话 UI 状态的不可变快照。</summary>
public sealed record OpenCodeSessionUiSnapshot(
    string EndpointIdentity,
    long Revision,
    long Version,
    ImmutableArray<OpenCodeSessionUiState> Sessions);

/// <summary>
/// 一次观察到的事实：Value 为 null 表示明确的未知观察；
/// 外层 Fact 为 null 才表示本次无该事实更新。UnknownReason 为独立原因合并。
/// </summary>
public sealed record OpenCodeSessionUiFact(
    bool? Value,
    DateTimeOffset ObservedAt,
    OpenCodeStateUnknownReason UnknownReason = OpenCodeStateUnknownReason.None)
{
    // A successfully hydrated, continuously connected event epoch keeps this exact revision
    // valid without falsifying ObservedAt. Disconnect/untrack/new state invalidates the lease.
    internal Func<bool>? CurrentEventStream { get; init; }
}

/// <summary>
/// 一次观察输入。nullable 字段为 null 表示本次不更新（不清历史）；
/// 水位/时间/迟到校验由后续 store 负责。
/// </summary>
public sealed record OpenCodeSessionUiObservation(
    string EndpointIdentity,
    long Revision,
    string SessionId,
    OpenCodeSessionUiFact? Working = null,
    OpenCodeSessionUiFact? PendingAction = null,
    bool? MutationProtected = null,
    OpenCodeCompletionMark? Completion = null,
    double? ViewedWatermark = null)
{
    public OpenCodeSessionUiFact? QuestionPending { get; init; }
}
