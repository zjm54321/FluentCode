using System.Collections.Generic;

namespace AIFrontend.Models;

/// <summary>思考（thinking）模式，对应 Anthropic thinking.type；None 表示不发送 thinking 参数。</summary>
public enum ThinkingMode
{
    /// <summary>不发送 thinking 参数（默认）。</summary>
    None = 0,

    /// <summary>自适应思考（thinking.type = "adaptive"）。</summary>
    Adaptive = 1,

    /// <summary>显式启用思考并给出预算（thinking.type = "enabled" + budget_tokens；需 <see cref="ReasoningRequest.ThinkingBudgetTokens"/>）。</summary>
    Enabled = 2,
}

/// <summary>
/// 单次请求的思考/推理参数（显式意图；默认空 = 不发送任何可选思考参数）。
/// </summary>
/// <remarks>
/// 约定（重要）：
/// <list type="bullet">
/// <item><description><see cref="Effort"/> 原样传递（OpenAI Responses 的 reasoning.effort、
/// Chat Completions 的 reasoning_effort、Anthropic 的 output_config.effort），
/// <strong>不得</strong>把 effort 映射为 token 预算，也不跨 provider 归一化取值。</description></item>
/// <item><description>协议实现必须先对照 <see cref="ModelDescriptor.Capabilities"/> 门控：
/// 能力未知（<see cref="CapabilitySource.Unknown"/> 或 <see cref="ModelCapabilities.Reasoning"/> 为 null）时不得发送；
/// <see cref="ReasoningCapabilities.SupportsEffort"/> 不包含的 effort 值不得发送。</description></item>
/// <item><description>Anthropic 的 "enabled" 思考需要模型能力支持且必须带预算；
/// 预算仅按用户显式填写值传递，不做任何隐式换算。</description></item>
/// </list>
/// </remarks>
public sealed record ReasoningRequest
{
    /// <summary>effort 取值（可空；null = 不发送 effort）。</summary>
    public string? Effort { get; init; }

    /// <summary>思考模式（默认 None = 不发送 thinking）。</summary>
    public ThinkingMode Thinking { get; init; } = ThinkingMode.None;

    /// <summary>思考 token 预算（仅 <see cref="ThinkingMode.Enabled"/> 时有意义）。</summary>
    public int? ThinkingBudgetTokens { get; init; }

    /// <summary>空请求（不发送任何可选思考参数）。</summary>
    public static ReasoningRequest None { get; } = new();

    /// <summary>是否没有任何显式思考意图。</summary>
    public bool IsEmpty => Effort is null && Thinking == ThinkingMode.None;
}

/// <summary>
/// 一次流式聊天请求（不可变快照）：多轮消息、模型（含显式能力）与可选思考参数。
/// </summary>
/// <remarks>
/// 取消通过 <c>IChatClient.StreamAsync</c> 的 <see cref="System.Threading.CancellationToken"/> 传入。
/// 系统提示词以 <see cref="ChatRole.System"/> 消息放入 <see cref="Messages"/>，不单独设字段。
/// </remarks>
public sealed record ChatRequest
{
    /// <summary>目标模型（含 provider 归属与显式能力，供协议实现门控可选参数）。</summary>
    public required ModelDescriptor Model { get; init; }

    /// <summary>多轮消息（按时间顺序，可含 System 消息）。</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>思考参数；null 或 <see cref="ReasoningRequest.IsEmpty"/> 表示不发送任何可选思考参数（默认）。</summary>
    public ReasoningRequest? Reasoning { get; init; }
}
