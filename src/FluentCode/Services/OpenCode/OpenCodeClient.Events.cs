using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>A detached V2 event envelope. Data is the native event data, not v1 properties.</summary>
public sealed record OpenCodeEvent(string Type, JsonElement Data, DateTimeOffset ReceivedAt)
{
    public string? Id { get; init; }
    public double? Created { get; init; }
    public JsonElement? Metadata { get; init; }
    public string? LocationDirectory { get; init; }
    public string? SessionId => Data.TryGetProperty("sessionID", out var id) && id.ValueKind == JsonValueKind.String
        ? id.GetString()
        : Type == "form.created" && Data.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.Object
            && form.TryGetProperty("sessionID", out var owner) && owner.ValueKind == JsonValueKind.String ? owner.GetString() : null;

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
            LocationDirectory = root.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.Object
                && location.TryGetProperty("directory", out var directory) && directory.ValueKind == JsonValueKind.String
                ? directory.GetString() : null,
        };
    }
}

public sealed partial class OpenCodeClient
{
    internal static readonly TimeSpan EventReadIdleTimeout = TimeSpan.FromSeconds(45);

    /// <summary>One volatile subscription. Caller reconnects and refetches; no Last-Event-ID replay exists.</summary>
    public IAsyncEnumerable<OpenCodeEvent> SubscribeEventsAsync(CancellationToken cancellationToken = default)
        => SubscribeEventsAsync(cancellationToken, EventReadIdleTimeout);

    internal async IAsyncEnumerable<OpenCodeEvent> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken, TimeSpan readIdleTimeout)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_timeout);
        using var response = await SendAsync(HttpMethod.Get, "api/event", null, null, connectTimeout.Token).ConfigureAwait(false);
        connectTimeout.CancelAfter(Timeout.InfiniteTimeSpan); // Header timeout only; body remains a long-lived cancellable stream.
        if (!response.IsSuccessStatusCode)
            throw new OpenCodeApiException((int)response.StatusCode, null, null,
                $"OpenCode 服务返回 HTTP {(int)response.StatusCode}（GET api/event）。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var readIdle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reader = new StreamReader(new EventActivityStream(stream, readIdle, readIdleTimeout), Encoding.UTF8);
        var data = new StringBuilder();
        string? eventName = null;
        while (true)
        {
            string? line;
            // Every byte read resets the watchdog, including partial lines and heartbeat comments.
            readIdle.CancelAfter(readIdleTimeout);
            try { line = await reader.ReadLineAsync(readIdle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OpenCodeTransportException("OpenCode 事件流读取超时；需要重新订阅并刷新。");
            }
            finally { readIdle.CancelAfter(Timeout.InfiniteTimeSpan); }
            if (line is null) break;
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

    private sealed class EventActivityStream(Stream inner, CancellationTokenSource idle, TimeSpan timeout) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            idle.CancelAfter(timeout);
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count > 0) idle.CancelAfter(timeout);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // The outer await-using owns inner; disposing the reader only disposes this adapter.
    }
}
