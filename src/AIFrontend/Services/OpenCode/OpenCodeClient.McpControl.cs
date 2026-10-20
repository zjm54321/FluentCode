using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>MCP 运行时连接控制（OpenCode v2 实验接口）。</summary>
public enum OpenCodeMcpControlAction
{
    /// <summary>POST api/experimental/mcp/{server}/connect：运行时连接（覆盖“已禁用”配置直到服务重启）。</summary>
    Connect,

    /// <summary>POST api/experimental/mcp/{server}/disconnect：运行时断开（移除其工具，直到重新连接）。</summary>
    Disconnect,
}

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// 运行时连接 / 断开一个已配置的 MCP 服务器（OpenAPI <c>experimental.mcp.connect</c> / <c>experimental.mcp.disconnect</c>，
    /// 成功为 HTTP 204）。directory 与 GET api/mcp 相同，用公开的 location[directory] 指定会话目录。
    /// 只发一次，不重试；失败保持客户端异常（不读取错误体）。
    /// </summary>
    public Task ControlMcpServerAsync(string serverName, OpenCodeMcpControlAction action,
        string? directory = null, CancellationToken cancellationToken = default)
    {
        // "." / ".." 会被 Uri 当作相对路径段归一化（改变目标路由）：拒绝。
        if (string.IsNullOrWhiteSpace(serverName) || serverName.Length > 256 || serverName is "." or "..")
            throw new ArgumentException("MCP 服务器名称无效。", nameof(serverName));
        foreach (var ch in serverName)
            if (char.IsControl(ch)) throw new ArgumentException("MCP 服务器名称无效。", nameof(serverName));
        var verb = action switch
        {
            OpenCodeMcpControlAction.Connect => "connect",
            OpenCodeMcpControlAction.Disconnect => "disconnect",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var path = "api/experimental/mcp/" + Uri.EscapeDataString(serverName) + "/" + verb;
        return SendForNoContentAsync(HttpMethod.Post, path, CapabilityLocationQuery(directory), content: null, cancellationToken);
    }
}
