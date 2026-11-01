using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    // Typed protocol-4 calls live in Browser/OpenCodeBrowserRpc.cs. Keeping this
    // transport independent lets existing transport-only consumers keep their file set.
    internal async Task<JsonElement> BrowserRpcAsync(string method, string directory, JsonObject input,
        bool attachment, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var content = CreateJsonContent(new { input });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Attach and tunnel.read are scoped, held-open requests, not finite REST operations.
        // An idle tunnel remains readable until data/EOF or the immutable attachment lifetime ends.
        if (!attachment && method != "tunnel.read") timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "api/rpc/experimental.browser/" + method,
                LocationQuery(directory), content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // RpcErrorEncoded has a top-level type. Only retain known codes; never
                // surface the error message/body (it may echo page data or credentials).
                string? code = null;
                if ((int)response.StatusCode is 400 or 500)
                {
                    try
                    {
                        using var error = JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
                        if (error.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                            code = type.GetString() switch
                            {
                                "rpc.method_not_found" => "rpc.method_not_found",
                                "rpc.unavailable" => "rpc.unavailable",
                                "unavailable" => "unavailable",
                                "rpc.invalid_input" => "rpc.invalid_input",
                                "rpc.invalid_output" => "rpc.invalid_output",
                                "rpc.internal" => "rpc.internal",
                                _ => null,
                            };
                    }
                    catch (JsonException) { }
                }
                throw new OpenCodeBrowserRpcException((int)response.StatusCode, code);
            }
            using var document = JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new OpenCodeProtocolException("浏览器 RPC 响应不是对象。");
            return document.RootElement.TryGetProperty("output", out var output) ? output.Clone() : default;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("浏览器 RPC 已取消。", cancellationToken); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("浏览器 RPC 请求超时。"); }
        catch (JsonException) { throw new OpenCodeProtocolException("浏览器 RPC 响应不是合法 JSON。"); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("浏览器 RPC 连接失败。"); }
        catch (IOException) { throw new OpenCodeTransportException("浏览器 RPC 响应读取失败。"); }
    }
}

public sealed class OpenCodeBrowserRpcException(int statusCode, string? code)
    : OpenCodeException($"浏览器 RPC 失败（HTTP {statusCode}）。")
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsUnsupported => StatusCode == 404 || Code == "rpc.method_not_found";
    public bool IsUnavailable => Code is "unavailable" or "rpc.unavailable";
}
