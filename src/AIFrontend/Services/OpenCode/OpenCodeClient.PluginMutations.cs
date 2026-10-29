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
    /// <summary>The guard must synchronously authorize and invoke actual HTTP initiation exactly once.</summary>
    public Task<OpenCodeSessionPresetState> ApplySessionPresetGuardedAsync(string sessionId, string directory,
        string exactPresetName, Func<Action, bool> start, CancellationToken cancellationToken = default)
    {
        ValidatePluginMutationInput(sessionId, directory);
        // Whitespace and case are part of the exact name. There is no list, normalization or null-clear.
        ArgumentNullException.ThrowIfNull(exactPresetName);
        if (exactPresetName.Length == 0) throw new ArgumentException("An exact preset name is required.", nameof(exactPresetName));
        return SendPluginMutationAsync("api/rpc/oh-my-opencode-slim.session-preset.v1/apply", directory,
            new { input = new { sessionID = sessionId, presetName = exactPresetName } }, start,
            root => OpenCodeSessionPresetState.Parse(root, SessionPresetOwner, sessionId, directory),
            clear: false, cancellationToken);
    }

    public Task<ClearEncryptedResult> ClearEncryptedContentsGuardedAsync(string sessionId, string directory,
        Func<Action, bool> start, CancellationToken cancellationToken = default)
    {
        ValidatePluginMutationInput(sessionId, directory);
        return SendPluginMutationAsync("api/rpc/opencode.qol.clear-encrypted-contents/clear", directory,
            new { input = new { sessionID = sessionId } }, start, ParseClearEncryptedResult,
            clear: true, cancellationToken);
    }

    internal static void ValidatePluginMutationInput(string sessionId, string directory)
    {
        ValidateSessionId(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        foreach (var ch in directory)
            if (char.IsControl(ch)) throw new ArgumentException("Invalid captured directory.", nameof(directory));
    }

    private async Task<T> SendPluginMutationAsync<T>(string endpoint, string directory, object input,
        Func<Action, bool> start, Func<JsonElement, T> parse, bool clear, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var content = CreateJsonContent(input);
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        Task<HttpResponseMessage>? pending = null;
        try
        {
            var authorized = start(() =>
            {
                if (pending is not null) throw new InvalidOperationException("HTTP initiation must occur once.");
                timeout.Token.ThrowIfCancellationRequested();
                // SendAsync invokes HttpClient synchronously up to the actual send before its first await.
                pending = SendAsync(HttpMethod.Post, endpoint, query, content, timeout.Token);
            });
            if (pending is null) throw new OperationCanceledException("Plugin mutation was not started.", cancellationToken);
            // Once initiated, always observe and dispose the response, even if a faulty guard returns false.
            using var response = await pending.ConfigureAwait(false);
            if (!authorized) throw new OpenCodeProtocolException("Plugin mutation dispatch guard failed.");
            using var document = JsonDocument.Parse(await ReadPluginMutationBodyAsync(response.Content, timeout.Token).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = 16 });
            RequireUniqueMutationObject(document.RootElement);
            if (!response.IsSuccessStatusCode)
                throw ParsePluginMutationFailure((int)response.StatusCode, document.RootElement, clear);
            if (document.RootElement.TryGetProperty("output", out var output)) RequireUniqueMutationObject(output);
            // A fully validated acceptance wins over cancellation racing with completed response delivery.
            return parse(document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Plugin mutation was cancelled.", cancellationToken); }
        catch (OperationCanceledException) { throw new OpenCodeTimeoutException("Plugin mutation timed out; outcome is unconfirmed."); }
        catch (JsonException) { throw new OpenCodeProtocolException("Plugin mutation response is invalid."); }
        catch (HttpRequestException) { throw new OpenCodeTransportException("Plugin mutation connection failed; outcome is unconfirmed."); }
        catch (IOException) { throw new OpenCodeTransportException("Plugin mutation response could not be read."); }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadPluginMutationBodyAsync(HttpContent content, CancellationToken ct)
    {
        const int maxBytes = 16 * 1024;
        if (content.Headers.ContentLength > maxBytes) throw new OpenCodeProtocolException("Plugin mutation response is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) throw new OpenCodeProtocolException("Plugin mutation response is too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void RequireUniqueMutationObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new OpenCodeProtocolException("Plugin mutation response is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new OpenCodeProtocolException("Plugin mutation response has duplicate fields.");
    }

    private static ClearEncryptedResult ParseClearEncryptedResult(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
            || !TrySafeClearCounter(output, "changedMessages", out var messages)
            || !TrySafeClearCounter(output, "changedEvents", out var events)
            || !TrySafeClearCounter(output, "removedFields", out var fields))
            throw new OpenCodeProtocolException("Clear result is not a confirmed safe-counter result.");
        var count = 0;
        foreach (var property in output.EnumerateObject()) count++;
        if (count != 4) throw new OpenCodeProtocolException("Clear result contains unexpected fields.");
        return new(messages, events, fields);
    }

    private static bool TrySafeClearCounter(JsonElement output, string name, out long value)
    {
        value = 0;
        return output.TryGetProperty(name, out var counter) && counter.ValueKind == JsonValueKind.Number
            && counter.TryGetInt64(out value) && value is >= 0 and <= 9007199254740991L;
    }

    private static OpenCodePluginMutationRpcException ParsePluginMutationFailure(int status, JsonElement root, bool clear)
    {
        const string unknown = "The service did not confirm the operation outcome.";
        var tag = root.TryGetProperty("_tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (status == 401 && tag == "UnauthorizedError")
            return new(status, "unauthorized", "Authentication was rejected before the operation.", true);
        var code = root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        if (status == 400 && tag == "RpcError" && code is "rpc.invalid_input" or "rpc.method_not_found")
            return new(status, code, code == "rpc.invalid_input" ? "The RPC input was rejected." : "The RPC method is not registered.", true);
        if (clear && status == 400 && tag == "RpcError" && code == "failed"
            && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Null
            && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            // Exact plugin allowlist only. The first five reasons precede interruption/helper launch.
            var reason = message.GetString() switch
            {
                "Current OpenCode host lacks required clearing interface" => ("Current OpenCode host lacks required clearing interface", true),
                "Unable to determine session location" => ("Unable to determine session location", true),
                "Unable to determine local database" => ("Unable to determine local database", true),
                "Standalone Bun runtime is unavailable or incompatible" => ("Standalone Bun runtime is unavailable or incompatible", true),
                "Clearing operation is already in progress for this session" => ("Clearing operation is already in progress for this session", true),
                "Failed to stop or wait for session to end" => ("Failed to stop or wait for session to end", false),
                "Operation was cancelled or timed out" => ("Operation was cancelled or timed out", false),
                "Clearing process was not confirmed, check database status" => ("Clearing process was not confirmed, check database status", false),
                "Clear was committed, but UI refresh failed; please reopen session" => ("Clear was committed, but UI refresh failed; please reopen session", false),
                _ => (unknown, false),
            };
            return new(status, "failed", reason.Item1, reason.Item2);
        }
        // rpc.unavailable also races with in-flight handler execution; it is never pre-effect proof.
        code = code is "rpc.internal" or "rpc.invalid_output" or "rpc.unavailable" ? code : null;
        return new(status, code, unknown, false);
    }
}
