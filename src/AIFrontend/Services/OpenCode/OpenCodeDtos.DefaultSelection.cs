using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>仅含配置文档中最后定义的选择；不保留配置文档、路径或其他配置字段。</summary>
public sealed record OpenCodeConfiguredSelection
{
    public string? DefaultAgentId { get; init; }
    public OpenCodeModelRef? Model { get; init; }

    public override string ToString() => "OpenCodeConfiguredSelection { [redacted] }";
}

/// <summary>GET api/model/default；Data 为 null 是合法的无默认模型结果，不包含 variant 选择。</summary>
public sealed record OpenCodeDefaultModelResponse
{
    [JsonRequired, JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }

    [JsonRequired, JsonPropertyName("data")]
    public OpenCodeModelInfo? Data { get; init; }
}
