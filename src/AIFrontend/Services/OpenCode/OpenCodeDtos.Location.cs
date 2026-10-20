using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// GET api/location 的响应（OpenAPI 组件 Location.PublicInfo）。无外层包装。
/// 已核实必填：directory、project。
/// </summary>
public sealed record OpenCodeLocationInfo
{
    /// <summary>解析后的服务目录。</summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

    /// <summary>目录所属项目。</summary>
    [JsonPropertyName("project")]
    public OpenCodeProjectInfo? Project { get; init; }
}

/// <summary>项目身份；location 响应包含 directory，project.list 仅提供 canonical，不伪造 directory。</summary>
public sealed record OpenCodeProjectInfo
{
    /// <summary>项目 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>项目目录。</summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }

    /// <summary>规范化目录。</summary>
    [JsonPropertyName("canonical")]
    public string? Canonical { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("sandboxes")]
    public List<string>? Sandboxes { get; init; }

    [JsonPropertyName("time")]
    public OpenCodeProjectTime? Time { get; init; }
}

/// <summary>Project.Time；时间为 Unix 毫秒，archived 可选。</summary>
public sealed record OpenCodeProjectTime
{
    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("updated")]
    public long? Updated { get; init; }

    [JsonPropertyName("active")]
    public long? Active { get; init; }

    [JsonPropertyName("archived")]
    public long? Archived { get; init; }
}

/// <summary>
/// Location.PublicRef：仅含 directory（已核实必填）。
/// 同时用作 Session.Info.location 与 location-switched 消息的 location。
/// </summary>
public sealed record OpenCodeLocationRef
{
    /// <summary>目录。</summary>
    [JsonPropertyName("directory")]
    public string? Directory { get; init; }
}
