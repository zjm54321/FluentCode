using System;
using System.Collections.Generic;

namespace AIFrontend.Models;

/// <summary>聊天消息角色。</summary>
public enum ChatRole
{
    /// <summary>系统消息。</summary>
    System = 0,

    /// <summary>用户消息。</summary>
    User = 1,

    /// <summary>助手消息。</summary>
    Assistant = 2,

    /// <summary>工具结果消息（对应 <see cref="ChatMessage.ToolCallId"/>）。</summary>
    Tool = 3,
}

/// <summary>
/// 一轮对话中的单条消息（不可变快照）。
/// </summary>
/// <remarks>
/// <see cref="ReasoningText"/> 仅用于展示/可选持久化；协议实现<strong>不得</strong>默认把它
/// 回传给 provider（Anthropic 的 thinking 块带签名需专门处理，OpenAI 也不接受回传推理文本）。
/// </remarks>
public sealed record ChatMessage
{
    /// <summary>角色。</summary>
    public ChatRole Role { get; init; }

    /// <summary>正文（Markdown 源文本；渲染由 UI 负责）。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>助手思考文本（可空；展示/可选持久化用）。</summary>
    public string? ReasoningText { get; init; }

    /// <summary>助手发起的工具调用（可空）。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>工具结果消息对应的调用 ID（仅 <see cref="ChatRole.Tool"/> 使用）。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>创建系统消息。</summary>
    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };

    /// <summary>创建用户消息。</summary>
    public static ChatMessage User(string content) => new() { Role = ChatRole.User, Content = content };

    /// <summary>创建助手消息。</summary>
    public static ChatMessage Assistant(string content, string? reasoningText = null, IReadOnlyList<ToolCall>? toolCalls = null) =>
        new() { Role = ChatRole.Assistant, Content = content, ReasoningText = reasoningText, ToolCalls = toolCalls };

    /// <summary>创建工具结果消息。</summary>
    public static ChatMessage ToolResult(string toolCallId, string content) =>
        new() { Role = ChatRole.Tool, ToolCallId = toolCallId, Content = content };
}
