using System.Text.Json.Serialization;

namespace FluentCode.Services.OpenCode;

/// <summary>GET /api/mcp 的 Mcp.Server。status 是对象，不是顶层字符串。</summary>
public sealed record OpenCodeMcpServer
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public OpenCodeMcpConnectionStatus? Status { get; init; }
}

public sealed record OpenCodeMcpConnectionStatus
{
    /// <summary>connected / pending / disabled / failed / needs_auth；未知值不当作已连接。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>服务端公开错误，仅用于显式状态详情，不写入异常或诊断。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>GET /api/plugin 的 Plugin.Info；配置来源、功能声明和运行状态分别保留。</summary>
public sealed record OpenCodePluginInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("source")]
    public OpenCodePluginSource? Source { get; init; }

    [JsonPropertyName("features")]
    public OpenCodePluginFeatures? Features { get; init; }

    [JsonPropertyName("state")]
    public OpenCodePluginRuntimeState? State { get; init; }
}

public sealed record OpenCodePluginSource
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>仅保留 API 声明的路径；不读取本地文件或推断包名。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>仅 package 来源：服务器报告有更新版本（OpenAPI 中只出现 true）。</summary>
    [JsonPropertyName("outdated")]
    public bool? Outdated { get; init; }

    /// <summary>仅 package 来源：服务器正在更新该包（OpenAPI 中只出现 true）。</summary>
    [JsonPropertyName("updating")]
    public bool? Updating { get; init; }
}

public sealed record OpenCodePluginFeatures
{
    [JsonPropertyName("server")]
    public bool? Server { get; init; }

    [JsonPropertyName("tui")]
    public bool? Tui { get; init; }

    /// <summary>功能声明不证明存在某个 Todo/OMO RPC，本客户端不调用插件 RPC。</summary>
    [JsonPropertyName("rpc")]
    public bool? Rpc { get; init; }
}

public sealed record OpenCodePluginRuntimeState
{
    /// <summary>active / failed；未知值保留，不据配置存在推断 active。</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
