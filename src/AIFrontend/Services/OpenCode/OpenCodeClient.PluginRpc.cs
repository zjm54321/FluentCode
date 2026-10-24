using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    // A client belongs to one source revision. Locations can activate different plugin registrations.
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _favoriteCapabilities = new(StringComparer.Ordinal);

    internal async Task<bool> SupportsModelFavoritesAsync(string directory, CancellationToken ct)
    {
        var entry = _favoriteCapabilities.GetOrAdd(directory, key => new(() => ProbeAsync(key, ct)));
        try { return await entry.Value.ConfigureAwait(false); }
        catch { _favoriteCapabilities.TryRemove(directory, out _); throw; }
        async Task<bool> ProbeAsync(string location, CancellationToken token)
        {
            try
            {
                var output = await PluginRpcAsync("hello", location, new { protocolVersions = new[] { 1 } }, token).ConfigureAwait(false);
                return OpenCodeModelFavoritesRpc.HasCapability(output);
            }
            catch (OpenCodePluginRpcException ex) when (ex.IsUnsupported) { return false; }
        }
    }

    internal async Task<JsonElement> PluginRpcAsync(string method, string directory, object input, CancellationToken ct)
    {
        if (method is not ("hello" or "model.favorites.get" or "model.favorites.set")) throw new ArgumentException("未知的插件方法。", nameof(method));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var content = CreateJsonContent(new { input });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/rpc/opencode.windows-frontend/" + method,
                LocationQuery(directory), content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                if ((int)response.StatusCode is 400 or 500)
                {
                    try
                    {
                        using var error = JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
                        code = OpenCodeModelFavoritesRpc.ErrorCode(error.RootElement);
                    }
                    catch (JsonException) { }
                }
                throw new OpenCodePluginRpcException((int)response.StatusCode, code);
            }
            using var document = JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
            return OpenCodeModelFavoritesRpc.Output(document.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException("收藏 RPC 已取消。", ct); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("收藏 RPC 请求超时。"); }
        catch (JsonException) { throw new OpenCodeProtocolException("收藏 RPC 响应不是合法 JSON。"); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("收藏 RPC 连接失败。"); }
        catch (IOException) { throw new OpenCodeTransportException("收藏 RPC 响应读取失败。"); }
    }
}

public sealed class OpenCodePluginRpcException(int statusCode, string? code)
    : OpenCodeException($"收藏 RPC 失败（HTTP {statusCode}）。")
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsUnsupported => StatusCode == 404 || Code == "rpc.method_not_found";
}
