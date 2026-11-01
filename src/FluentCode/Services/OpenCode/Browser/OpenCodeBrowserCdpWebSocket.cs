using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>A browser-level, flattened CDP transport. Only the receive loop reads the socket.</summary>
internal sealed class OpenCodeBrowserCdpWebSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly HttpMessageInvoker _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private Task? _reader;
    private long _nextId;
    private int _closed;

    public event Action<OpenCodeBrowserCdpEvent>? Received;
    public event Action<Exception>? Disconnected;

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        endpoint = OpenCodeBrowserChromeConnector.ValidateEndpoint(endpoint.AbsoluteUri, true);
        if (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            endpoint = new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;
        // The handshake must not follow a loopback server's redirect to an untrusted remote endpoint.
        await _socket.ConnectAsync(endpoint, _http, cancellationToken).ConfigureAwait(false);
        _reader = ReadAsync();
    }

    public async Task<JsonObject> SendAsync(string method, JsonObject? parameters = null,
        string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Pending(method, completion);
        if (Volatile.Read(ref _closed) != 0 && _pending.TryRemove(id, out var closed))
            closed.Completion.TrySetException(new ObjectDisposedException(nameof(OpenCodeBrowserCdpWebSocket)));
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var waiting)) waiting.Completion.TrySetCanceled(cancellationToken);
        });
        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters.DeepClone();
            if (sessionId != null) message["sessionId"] = sessionId;
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Never cancel a WebSocket write for a single command. That can abort the entire socket.
                await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _lifetime.Token).ConfigureAwait(false);
            }
            finally { _send.Release(); }
            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _pending.TryRemove(id, out _);
            if (error is WebSocketException || error is IOException) Fail(error);
            throw;
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                        throw new IOException("Chrome closed the CDP connection.");
                    if (received.MessageType != WebSocketMessageType.Text)
                        throw new IOException("Chrome sent a non-text CDP message.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var value = JsonNode.Parse(message.GetBuffer().AsSpan(0, checked((int)message.Length))) as JsonObject
                    ?? throw new IOException("Chrome sent an invalid CDP object.");
                if (value["id"] is JsonValue idNode && idNode.TryGetValue<long>(out var id))
                {
                    if (!_pending.TryRemove(id, out var pending)) continue;
                    if (value["error"] is JsonObject error)
                        pending.Completion.TrySetException(new OpenCodeBrowserChromeProtocolException(
                            error["code"]?.GetValue<int>() ?? -1, pending.Method + ": " + (error["message"]?.GetValue<string>() ?? "CDP command failed.")));
                    else pending.Completion.TrySetResult(value["result"] as JsonObject ?? new JsonObject());
                }
                else if (value["method"]?.GetValue<string>() is string method)
                    Received?.Invoke(new OpenCodeBrowserCdpEvent(method,
                        value["params"] as JsonObject ?? new JsonObject(), value["sessionId"]?.GetValue<string>()));
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _lifetime.Cancel();
        _socket.Abort();
        foreach (var pair in _pending)
            if (_pending.TryRemove(pair.Key, out var pending)) pending.Completion.TrySetException(error);
        Disconnected?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        Fail(new ObjectDisposedException(nameof(OpenCodeBrowserCdpWebSocket)));
        if (_reader != null) await _reader.ConfigureAwait(false);
        _socket.Dispose();
        _http.Dispose();
        _lifetime.Dispose();
    }

    private sealed record Pending(string Method, TaskCompletionSource<JsonObject> Completion);
}

internal sealed class OpenCodeBrowserChromeProtocolException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
