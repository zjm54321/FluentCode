namespace AIFrontend.Models;

/// <summary>工具调用的展示状态。</summary>
public enum ToolCallStatus
{
    /// <summary>已收到调用请求，尚未开始/状态未知。</summary>
    Pending = 0,

    /// <summary>执行中（含参数流式累积中）。</summary>
    Running = 1,

    /// <summary>已完成。</summary>
    Completed = 2,

    /// <summary>执行失败。</summary>
    Failed = 3,
}

/// <summary>
/// 一次工具调用的展示数据（不可变快照）。
/// </summary>
/// <remarks>
/// <see cref="ArgumentsJson"/> 在流式过程中可能是<strong>部分</strong>片段（配合
/// <see cref="ChatStreamEventKind.ToolCall"/> 的多次更新事件）；完整性与可解析性由协议实现保证。
/// <see cref="ResultText"/> 为展示用文本（可能被截断），完整工具结果应作为
/// <see cref="ChatMessage.ToolResult"/> 消息进入对话历史。
/// </remarks>
public sealed record ToolCall
{
    /// <summary>调用 ID（provider 生成，用于与工具结果消息关联）。</summary>
    public required string CallId { get; init; }

    /// <summary>工具名。</summary>
    public required string Name { get; init; }

    /// <summary>参数 JSON（可为部分片段；未知时为 null）。</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>展示状态。</summary>
    public ToolCallStatus Status { get; init; } = ToolCallStatus.Pending;

    /// <summary>展示用结果文本（可空，可能截断）。</summary>
    public string? ResultText { get; init; }
}
