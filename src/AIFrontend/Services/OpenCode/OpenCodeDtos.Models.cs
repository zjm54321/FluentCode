using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>GET api/model 的最小公开投影；不保留 headers/settings/body 等无关字段。</summary>
public sealed record OpenCodeModelsResponse
{
    [JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }

    [JsonPropertyName("data")]
    public List<OpenCodeModelInfo>? Data { get; init; }
}

public sealed record OpenCodeModelInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("providerID")]
    public string? ProviderId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>真实 Model.Info 变体；仅公开选择所需的 id，不复制配置/headers/body。</summary>
    [JsonPropertyName("variants")]
    public List<OpenCodeModelVariant>? Variants { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>每项为同一模型的一个价格 tier；缺失/空列表不代表免费。</summary>
    [JsonPropertyName("cost")]
    public List<OpenCodeModelCost>? Cost { get; init; }

    [JsonPropertyName("limit")]
    public OpenCodeModelLimit? Limit { get; init; }
}

public sealed record OpenCodeModelVariant
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>所有价格均为 USD / 百万 token，非倍率；nullable 保留缺价。</summary>
public sealed record OpenCodeModelCost
{
    [JsonPropertyName("tier")]
    public OpenCodeModelCostTier? Tier { get; init; }

    [JsonPropertyName("input")]
    public double? Input { get; init; }

    [JsonPropertyName("output")]
    public double? Output { get; init; }

    [JsonPropertyName("cache")]
    public OpenCodeModelCacheCost? Cache { get; init; }
}

public sealed record OpenCodeModelCostTier
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }
}

public sealed record OpenCodeModelCacheCost
{
    [JsonPropertyName("read")]
    public double? Read { get; init; }

    [JsonPropertyName("write")]
    public double? Write { get; init; }
}

public sealed record OpenCodeModelLimit
{
    [JsonPropertyName("context")]
    public long? Context { get; init; }

    [JsonPropertyName("input")]
    public long? Input { get; init; }

    [JsonPropertyName("output")]
    public long? Output { get; init; }
}
