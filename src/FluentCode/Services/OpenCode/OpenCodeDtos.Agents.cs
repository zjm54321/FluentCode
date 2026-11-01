using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentCode.Services.OpenCode;

/// <summary>v2.0.24 Agent.Info；id 用于切换，name 用于显示，未知 mode 原样保留。</summary>
public sealed record OpenCodeAgentInfo
{
    [JsonRequired, JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonRequired, JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>未展开的 Model.Ref；缺省时省略，不猜测其内部结构。</summary>
    [JsonPropertyName("model")]
    public JsonElement? Model { get; init; }

    [JsonRequired, JsonPropertyName("request")]
    public JsonElement Request { get; init; }

    [JsonPropertyName("system")]
    public string? System { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonRequired, JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonRequired, JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("steps")]
    public double? Steps { get; init; }

    [JsonRequired, JsonPropertyName("permissions")]
    public JsonElement Permissions { get; init; }

    /// <summary>原始配置不得写入日志。</summary>
    public override string ToString() => "OpenCodeAgentInfo { [redacted] }";
}

/// <summary>GET api/agent 的包装；Location 仅有 Directory，无 workspaceID。</summary>
public sealed record OpenCodeAgentsResponse
{
    [JsonRequired, JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }

    [JsonRequired, JsonPropertyName("data")]
    public List<OpenCodeAgentInfo>? Data { get; init; }

    public override string ToString() => "OpenCodeAgentsResponse { [redacted] }";
}

public static class OpenCodeAgentModes
{
    public const string Subagent = "subagent";
    public const string Primary = "primary";
    public const string All = "all";
}

/// <summary>仅当前 server 的 foreground session drains，不代表后台 Job 的状态。</summary>
public sealed record OpenCodeActiveSessionsResponse
{
    [JsonRequired, JsonPropertyName("data")]
    public Dictionary<string, OpenCodeActiveSessionState>? Data { get; init; }
}

/// <summary>已知 type 为 running；未知值保留，不推断完成或百分比。</summary>
public sealed record OpenCodeActiveSessionState
{
    [JsonRequired, JsonPropertyName("type")]
    public string? Type { get; init; }
}
