using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    internal OpenCodeDesktopBridge DesktopBridge { get; set; } = new();

    internal async Task<JsonElement> PluginRpcAsync(string method, string? directory, object input, CancellationToken ct)
    {
        if (method is not ("hello" or "model.favorites.get" or "model.favorites.set")) throw new ArgumentException("未知的插件方法。", nameof(method));
        if (directory is not null) ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var content = CreateJsonContent(new { input });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/rpc/" + OpenCodeDesktopBridge.PluginId + "/" + method,
                directory is null ? null : new[] { new KeyValuePair<string, string>("location[directory]", directory) }, content, timeout.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException("Desktop Bridge RPC 已取消。", ct); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("Desktop Bridge RPC 请求超时。"); }
        catch (JsonException) { throw new OpenCodeProtocolException("Desktop Bridge RPC 响应不是合法 JSON。"); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("Desktop Bridge RPC 连接失败。"); }
        catch (IOException) { throw new OpenCodeTransportException("Desktop Bridge RPC 响应读取失败。"); }
    }
}

public sealed class OpenCodePluginRpcException(int statusCode, string? code)
    : OpenCodeException($"Desktop Bridge RPC 失败（HTTP {statusCode}）。")
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsUnsupported => StatusCode == 404 || Code == "rpc.method_not_found";
}
