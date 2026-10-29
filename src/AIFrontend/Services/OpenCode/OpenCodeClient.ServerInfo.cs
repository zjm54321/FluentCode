using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>
/// GET api/info 白名单投影：版本与可空 persistentPty 能力，不解析进程、URL 或路径。
/// </summary>
public sealed record OpenCodeServerInfo
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("capabilities")]
    public OpenCodeServerCapabilities? Capabilities { get; init; }
}

public sealed record OpenCodeServerCapabilities
{
    [JsonPropertyName("persistentPty")]
    public bool? PersistentPty { get; init; }
}

public sealed partial class OpenCodeClient
{
    /// <summary>
    /// 已规范化的服务根地址（只含协议、主机、端口与路径前缀；构造时已拒绝 userinfo / query / fragment，凭据只在请求头里）。
    /// 仅用于在连接详情中显示用户自己配置的地址。
    /// </summary>
    public string DisplayBaseUrl => _baseUri.AbsoluteUri.TrimEnd('/');

    /// <summary>GET api/info：只读服务版本。失败保持客户端异常（调用方显示“无法确认”），不重试、不轮询。</summary>
    public async Task<OpenCodeServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/info";
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query: null, content: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var info = Deserialize<OpenCodeServerInfo>(document, "GET " + endpoint);
        if (string.IsNullOrWhiteSpace(info.Version))
        {
            throw new OpenCodeProtocolException("服务信息缺少 version（GET api/info）。");
        }

        return info;
    }
}
