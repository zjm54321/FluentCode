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

/// <summary>Location.PublicInfo.project。已核实必填：id、directory、canonical。</summary>
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
