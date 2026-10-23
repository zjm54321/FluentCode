using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>A detached V2 event envelope. Data is the native event data, not v1 properties.</summary>
public sealed record OpenCodeEvent(string Type, JsonElement Data, DateTimeOffset ReceivedAt)
{
    public string? Id { get; init; }
    public double? Created { get; init; }
    public JsonElement? Metadata { get; init; }
    public string? SessionId => Data.TryGetProperty("sessionID", out var id) ? id.GetString()
        : Data.TryGetProperty("info", out var info) && info.TryGetProperty("sessionID", out id) ? id.GetString()
        : Data.TryGetProperty("part", out var part) && part.TryGetProperty("sessionID", out id) ? id.GetString() : null;

    internal static OpenCodeEvent Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        // Effect's SSE encoding carries a JSON-encoded string, not a JSON object.
        using var decoded = document.RootElement.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(document.RootElement.GetString()!) : null;
        var root = decoded?.RootElement ?? document.RootElement;
        // Every V2 envelope (server.connected included) carries a string type and an object data; heartbeats are SSE comments.
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not { Length: > 0 } name
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new OpenCodeProtocolException("OpenCode 事件流不符合 V2 契约。");
        return new(name, data.Clone(), DateTimeOffset.UtcNow)
        {
            Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Created = root.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                && created.TryGetDouble(out var timestamp) ? timestamp : null,
            Metadata = root.TryGetProperty("metadata", out var metadata) ? metadata.Clone() : null,
        };
    }
}

public sealed partial class OpenCodeClient
{
    /// <summary>One volatile subscription. Caller reconnects and refetches; no Last-Event-ID replay exists.</summary>
    public async IAsyncEnumerable<OpenCodeEvent> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_timeout);
        using var response = await SendAsync(HttpMethod.Get, "api/event", null, null, connectTimeout.Token).ConfigureAwait(false);
        connectTimeout.CancelAfter(Timeout.InfiniteTimeSpan); // Header timeout only; body remains a long-lived cancellable stream.
        if (!response.IsSuccessStatusCode)
            throw new OpenCodeApiException((int)response.StatusCode, null, null,
                $"OpenCode 服务返回 HTTP {(int)response.StatusCode}（GET api/event）。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? eventName = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventName == "effect/http-api/stream/failure")
                    throw new OpenCodeProtocolException("OpenCode 事件流已中断；需要重新订阅并刷新。");
                if (data.Length > 0)
                {
                    OpenCodeEvent item;
                    try { item = OpenCodeEvent.Parse(data.ToString()); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                    { throw new OpenCodeProtocolException("OpenCode 事件流不符合 V2 契约。"); }
                    yield return item;
                }
                data.Clear();
                eventName = null;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
        }
        throw new OpenCodeTransportException("OpenCode 事件流已结束；需要重新订阅并刷新。");
    }
}
