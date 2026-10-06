using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Models;

/// <summary>模型条目的来源。</summary>
public enum ModelOrigin
{
    /// <summary>用户手动添加。</summary>
    Manual = 0,

    /// <summary>由 /v1/models 等发现接口写入（重新发现时可据此保留手动条目）。</summary>
    Discovered = 1,
}

/// <summary>能力信息的可信来源。</summary>
public enum CapabilitySource
{
    /// <summary>未知：不得据此推断“支持”任何可选参数（默认不发送）。</summary>
    Unknown = 0,

    /// <summary>来自 provider 的发现接口（如 Anthropic /v1/models 的 capabilities）。</summary>
    Discovered = 1,

    /// <summary>用户手动明确指定。</summary>
    Manual = 2,
}

/// <summary>
/// 模型思考/推理能力的显式描述。
/// </summary>
/// <remarks>
/// 语义约定（重要）：
/// <list type="bullet">
/// <item><description><see cref="ModelCapabilities.Reasoning"/> 为 null 表示“未知/未配置”，
/// 调用方不得默认发送任何思考相关可选参数。</description></item>
/// <item><description>非 null 且 <see cref="EffortValues"/> 为空、两个 bool 均为 false，
/// 表示“明确不支持”任何思考参数。</description></item>
/// <item><description>不同 provider 的 effort 取值集合不保证一致（OpenAI 与 Anthropic 各有取值），
/// 因此以字符串原样保存，不跨 provider 归一化，也不做 effort→token 预算的隐式映射。</description></item>
/// </list>
/// </remarks>
public sealed record ReasoningCapabilities
{
    /// <summary>该模型允许的 effort 取值（原样保存 provider 返回值，通常为小写；空 = 明确不支持 effort）。</summary>
    public IReadOnlyList<string> EffortValues { get; init; } = Array.Empty<string>();

    /// <summary>是否支持 Anthropic 风格的自适应思考（thinking.type = "adaptive"）。</summary>
    public bool SupportsThinkingAdaptive { get; init; }

    /// <summary>是否支持显式启用思考并提供 token 预算（thinking.type = "enabled" + budget_tokens）。</summary>
    public bool SupportsThinkingEnabled { get; init; }

    /// <summary>思考 token 预算下限（若已知）；null 表示未知。</summary>
    public int? MinThinkingBudgetTokens { get; init; }

    /// <summary>思考 token 预算上限（若已知）；null 表示未知。</summary>
    public int? MaxThinkingBudgetTokens { get; init; }

    /// <summary>effort 是否在允许集合内（Ordinal 精确比较，不改写协议取值）。</summary>
    public bool SupportsEffort(string effort) =>
        EffortValues.Any(v => string.Equals(v, effort, StringComparison.Ordinal));

    /// <summary>指定思考模式是否受支持；<see cref="ThinkingMode.None"/> 恒为 true（不发送即不涉及能力）。</summary>
    public bool SupportsThinking(ThinkingMode mode) => mode switch
    {
        ThinkingMode.None => true,
        ThinkingMode.Adaptive => SupportsThinkingAdaptive,
        ThinkingMode.Enabled => SupportsThinkingEnabled,
        _ => false,
    };
}

/// <summary>模型能力的整体描述；<see cref="Source"/> 表示这些信息的可信度来源。</summary>
public sealed record ModelCapabilities
{
    /// <summary>能力信息来源（默认未知）。</summary>
    public CapabilitySource Source { get; init; } = CapabilitySource.Unknown;

    /// <summary>思考/推理能力；null 表示未知/未配置（默认不发送可选思考参数）。</summary>
    public ReasoningCapabilities? Reasoning { get; init; }

    /// <summary>“未知”能力快照（默认值）。</summary>
    public static ModelCapabilities Unknown { get; } = new();
}

/// <summary>
/// 一个可用模型的描述：provider 归属、模型 ID 与显式能力。
/// </summary>
/// <remarks>
/// 本类型不猜测具体模型名，也不内置任何模型清单；条目全部来自用户手动配置或
/// 发现接口的返回值。协议实现必须按 <see cref="Capabilities"/> 门控可选参数。
/// </remarks>
public sealed record ModelDescriptor
{
    /// <summary>所属 provider 的稳定 ID（对应 <see cref="ProviderEndpoint.Id"/>）。</summary>
    public required string ProviderId { get; init; }

    /// <summary>模型 ID（原样传给 provider，不重写）。</summary>
    public required string ModelId { get; init; }

    /// <summary>UI 展示名；null 时显示 <see cref="ModelId"/>。</summary>
    public string? DisplayName { get; init; }

    /// <summary>条目来源（手动/发现）。</summary>
    public ModelOrigin Origin { get; init; } = ModelOrigin.Manual;

    /// <summary>显式能力；默认 <see cref="ModelCapabilities.Unknown"/>（未知，不发送可选参数）。</summary>
    public ModelCapabilities Capabilities { get; init; } = ModelCapabilities.Unknown;
}
