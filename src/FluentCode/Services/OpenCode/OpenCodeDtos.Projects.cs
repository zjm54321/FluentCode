using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluentCode.Services.OpenCode;

/// <summary>GET api/fs/list；条目路径相对于响应 location，而不是当前浏览的 path。</summary>
public sealed record OpenCodeFileSystemResponse
{
    [JsonPropertyName("location")]
    public OpenCodeLocationRef? Location { get; init; }

    [JsonPropertyName("data")]
    public List<OpenCodeFileSystemEntry>? Data { get; init; }
}

public sealed record OpenCodeFileSystemEntry
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>已核实值为 file 或 directory；不是本机文件系统类型。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
