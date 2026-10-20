using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// GET api/mcp：只读已配置 MCP 的公开状态。失败保持客户端异常，不伪装成空列表。
    /// directory 使用公开的 location[directory]；省略则查询服务端默认位置。
    /// </summary>
    public async Task<IReadOnlyList<OpenCodeMcpServer>> GetMcpServersAsync(
        string? directory = null, CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/mcp";
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint,
            CapabilityLocationQuery(directory), content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var servers = RequireData<List<OpenCodeMcpServer>>(document, "GET " + endpoint);
        foreach (var server in servers)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Name)
                || string.IsNullOrWhiteSpace(server.Status?.Status))
                throw new OpenCodeProtocolException("MCP 状态缺少 name/status（GET api/mcp）。");
        }
        return servers.AsReadOnly();
    }

    /// <summary>
    /// GET api/plugin：只读服务器插件目录与运行状态；不读取配置、存储、TUI 投影或 RPC。
    /// id 在 wire 中可省略；无法识别的本地插件必须保留为无法确认，不能猜测包名。
    /// </summary>
    public async Task<IReadOnlyList<OpenCodePluginInfo>> GetPluginsAsync(
        string? directory = null, CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/plugin";
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint,
            CapabilityLocationQuery(directory), content: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var plugins = RequireData<List<OpenCodePluginInfo>>(document, "GET " + endpoint);
        foreach (var plugin in plugins)
        {
            if (plugin is null || string.IsNullOrWhiteSpace(plugin.Source?.Type)
                || plugin.Features is null || string.IsNullOrWhiteSpace(plugin.State?.Status)
                || (plugin.Source.Type == "package" && string.IsNullOrWhiteSpace(plugin.Source.Target))
                || (plugin.Source.Type == "local" && string.IsNullOrWhiteSpace(plugin.Source.Path)))
                throw new OpenCodeProtocolException("插件状态缺少 source/features/state（GET api/plugin）。");
        }
        return plugins.AsReadOnly();
    }

    private static IReadOnlyList<KeyValuePair<string, string>>? CapabilityLocationQuery(string? directory)
        => string.IsNullOrEmpty(directory) ? null
            : new[] { new KeyValuePair<string, string>("location[directory]", directory) };
}
