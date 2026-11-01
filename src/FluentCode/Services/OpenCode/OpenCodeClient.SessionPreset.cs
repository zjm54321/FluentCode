using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    internal object SessionPresetOwner { get; } = new();

    /// <summary>
    /// Reads oh-my-opencode-slim.session-preset.v1/read at this client's source and requested location.
    /// Sends { input: { sessionID } }, requires { output: { sessionID, status, presetName, affects } }.
    /// No Desktop Bridge negotiation, cache, fallback to global config, subscription, mutation or retry.
    /// Unsupported/unregistered RPCs throw OpenCodeSessionPresetRpcException, never an unbound snapshot.
    /// </summary>
    public async Task<OpenCodeSessionPresetState> GetSessionPresetAsync(string sessionId,
        string? directory = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSessionId(sessionId);
        if (directory is not null) ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        const string endpoint = "api/rpc/oh-my-opencode-slim.session-preset.v1/read";
        var query = directory is null ? null : new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        using var content = CreateJsonContent(new { input = new { sessionID = sessionId } });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Post, endpoint, query, content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                if ((int)response.StatusCode is 400 or 500)
                {
                    try
                    {
                        using var error = JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
                        code = SessionPresetErrorCode(error.RootElement);
                    }
                    catch (JsonException) { }
                }
                throw new OpenCodeSessionPresetRpcException((int)response.StatusCode, code);
            }
            using var document = JsonDocument.Parse(await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false));
            timeout.Token.ThrowIfCancellationRequested();
            return OpenCodeSessionPresetState.Parse(document.RootElement, SessionPresetOwner, sessionId, directory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Session preset RPC 已取消。", cancellationToken); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("Session preset RPC 请求超时。"); }
        catch (JsonException) { throw new OpenCodeProtocolException("Session preset RPC 响应不是合法 JSON。"); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("Session preset RPC 连接失败。"); }
        catch (IOException) { throw new OpenCodeTransportException("Session preset RPC 响应读取失败。"); }
    }

    private static string? SessionPresetErrorCode(JsonElement error)
        => error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() switch
            {
                "rpc.method_not_found" => "rpc.method_not_found",
                "rpc.unavailable" => "rpc.unavailable",
                "rpc.invalid_input" => "rpc.invalid_input",
                "rpc.invalid_output" => "rpc.invalid_output",
                "rpc.internal" => "rpc.internal",
                _ => null,
            } : null;
}
