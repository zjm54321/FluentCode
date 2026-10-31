using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

/// <summary>One native RX loop, serialized TX and bounded ordered event queue. Never reconnects or retries writes.</summary>
internal sealed class PersistentPtyConnection : IPersistentPtyConnection
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private const int MaxQueuedBytes = 8 * 1024 * 1024;
    private readonly OpenCodeClient _client;
    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker _invoker;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _tx = new(1, 1);
    private readonly object _sync = new();
    private readonly Channel<PersistentPtyEvent> _events = Channel.CreateBounded<PersistentPtyEvent>(new BoundedChannelOptions(128)
    { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private CancellationTokenSource _authority = new();
    private readonly Task _receive;
    private int _queuedBytes, _disposed, _reader;
    private string? _controllerId;
    private long _generation = -1, _authorityRevision;
    private PersistentPtySize _size;
    private long _geometryRevision;
    private bool _attached, _dead, _exited;
    private readonly bool _requestedController;
    internal string? FailureCode { get; private set; }
    internal event Action? AuthorityChanged;
    internal event Action? InputStarted;
    internal Func<bool, bool>? CanWrite { get; set; }
    internal Func<bool>? CanResize { get; set; }
    internal Func<Action, bool>? TryStart { get; set; }
    public string PtyId { get; }
    public string AttachmentId { get; }
    internal (string? ControllerId, long Generation, bool Dead) Authority
    { get { lock (_sync) return (_controllerId, _generation, _dead); } }
    internal PersistentPtySize CanonicalSize { get { lock (_sync) return _size; } }
    internal long GeometryRevision { get { lock (_sync) return _geometryRevision; } }

    internal PersistentPtyConnection(OpenCodeClient client, ClientWebSocket socket, HttpMessageInvoker invoker,
        string sessionId, string ptyId, string attachmentId, CancellationToken cancellationToken, bool requestedController = false)
    {
        _client = client; _socket = socket; _invoker = invoker; _sessionId = sessionId; PtyId = ptyId; AttachmentId = attachmentId;
        _requestedController = requestedController;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receive = ReceiveAsync();
    }

    public async IAsyncEnumerable<PersistentPtyEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _reader, 1) != 0) throw new PersistentPtyException("multiple-readers");
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Add(ref _queuedBytes, -Weight(item));
            yield return item;
        }
    }

    private async Task ReceiveAsync()
    {
        Exception? failure = null;
        try
        {
            var chunk = new byte[64 * 1024];
            while (!_lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketMessageType? kind = null;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(chunk.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new PersistentPtyException(_requestedController && !_attached && (int?)_socket.CloseStatus == 4404 ? "control-busy" : "socket-closed");
                    if (kind is not null && kind != result.MessageType) throw new PersistentPtyException("fragment-type");
                    kind = result.MessageType;
                    if (message.Length > MaxMessageBytes - result.Count) throw new PersistentPtyException("message-overflow");
                    message.Write(chunk, 0, result.Count);
                } while (!result.EndOfMessage);
                var item = kind == WebSocketMessageType.Binary
                    ? new PersistentPtyBytes(ImmutableArray.CreateRange(message.ToArray()))
                    : ParseEvent(message.GetBuffer().AsMemory(0, (int)message.Length));
                if (!_attached && item is not PersistentPtyAttached) throw new PersistentPtyException("missing-attached");
                if (item is PersistentPtyAttached attached)
                {
                    if (_attached || attached.AttachmentId != AttachmentId || attached.InputProtocol != 1)
                        throw new PersistentPtyException("attachment-mismatch");
                    PersistentPtyValidation.Info(attached.Info, _sessionId, PtyId);
                    lock (_sync) _size = attached.Info.Size;
                    _attached = true;
                    SetAuthority(attached.Role == PersistentPtyRole.Controller ? AttachmentId : null, attached.Generation);
                }
                if (item is PersistentPtyControllerChanged changed) SetAuthority(changed.AttachmentId, changed.Generation);
                if (item is PersistentPtyResized resized)
                {
                    CancellationTokenSource? oldAuthority = null;
                    lock (_sync)
                    {
                        oldAuthority = _authority; _authority = new(); _authorityRevision++;
                        _size = resized.Size;
                        item = resized with { Revision = ++_geometryRevision };
                    }
                    oldAuthority?.Cancel();
                    AuthorityChanged?.Invoke();
                }
                if (item is PersistentPtyExited) { _exited = true; SetDead(); }
                var weight = Weight(item);
                if (Interlocked.Add(ref _queuedBytes, weight) > MaxQueuedBytes || !_events.Writer.TryWrite(item))
                    throw new PersistentPtyException("queue-overflow");
            }
        }
        catch (Exception ex)
        {
            if (!_exited)
            {
                failure = ex is PersistentPtyException ? ex : new PersistentPtyException(_lifetime.IsCancellationRequested ? "cancelled" : "socket-receive");
                FailureCode = ((PersistentPtyException)failure).Code;
            }
        }
        finally
        {
            SetDead();
            _socket.Abort();
            _events.Writer.TryComplete(failure);
        }
    }

    private void SetAuthority(string? id, long generation)
    {
        CancellationTokenSource? oldAuthority = null;
        lock (_sync)
        {
            if (generation < _generation) throw new PersistentPtyException("stale-generation");
            if (generation == _generation && id != _controllerId) throw new PersistentPtyException("conflicting-generation");
            if (id != _controllerId || generation != _generation)
            {
                oldAuthority = _authority; _authority = new(); _authorityRevision++;
            }
            _controllerId = id; _generation = generation;
        }
        oldAuthority?.Cancel();
        AuthorityChanged?.Invoke();
    }
    private void SetDead()
    {
        CancellationTokenSource authority;
        lock (_sync) { _dead = true; _authorityRevision++; authority = _authority; }
        authority.Cancel();
        AuthorityChanged?.Invoke();
    }

    public ValueTask RequestControlAsync(ushort cols, ushort rows, CancellationToken cancellationToken = default)
        => SendFrameAsync(0, ReadOnlyMemory<byte>.Empty, cols, rows, requireController: false, cancellationToken);
    public ValueTask SendInputAsync(ReadOnlyMemory<byte> input, ushort cols, ushort rows, CancellationToken cancellationToken = default)
        => SendFrameAsync(1, input, cols, rows, requireController: true, cancellationToken);
    private async ValueTask SendFrameAsync(byte type, ReadOnlyMemory<byte> input, ushort cols, ushort rows,
        bool requireController, CancellationToken cancellationToken)
    {
        PersistentPtyValidation.Size(new(cols, rows));
        if (input.Length > 65536) throw new PersistentPtyException("input-overflow");
        var frame = new byte[5 + input.Length];
        frame[0] = type; frame[1] = (byte)(cols >> 8); frame[2] = (byte)cols; frame[3] = (byte)(rows >> 8); frame[4] = (byte)rows;
        input.CopyTo(frame.AsMemory(5));
        var lease = CaptureAuthority(requireController);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, lease.Token);
        var started = false;
        try
        {
            await _tx.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                CheckAuthority(lease.Revision, requireController);
                Task? send = null;
                Start(() =>
                {
                    CheckAuthority(lease.Revision, requireController);
                    started = true;
                    if (type == 1) InputStarted?.Invoke();
                    send = _socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, linked.Token).AsTask();
                });
                await send!.ConfigureAwait(false);
            }
            finally { _tx.Release(); }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        { throw new PersistentPtyException(started ? "send-uncertain" : "input-disarmed"); }
    }

    public async ValueTask ResizeAsync(ushort cols, ushort rows, CancellationToken cancellationToken = default)
    {
        PersistentPtyValidation.Size(new(cols, rows));
        var lease = CaptureAuthority(true, resizing: true);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, lease.Token);
        await _tx.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            CheckAuthority(lease.Revision, true, resizing: true);
            Task? resize = null;
            Start(() =>
            {
                CheckAuthority(lease.Revision, true, resizing: true);
                resize = _client.ResizePersistentPtyAsync(_sessionId, PtyId, AttachmentId, new(cols, rows), linked.Token);
            });
            await resize!.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested
            && CanonicalSize == new PersistentPtySize(cols, rows) && Authority is var authority
            && !authority.Dead && authority.ControllerId == AttachmentId)
        {
            // The ordered resized event can arrive before the HTTP response. Its new
            // authority lease cancels this PUT; renderer acknowledgement still gates input.
        }
        finally { _tx.Release(); }
    }
    private (long Revision, CancellationToken Token) CaptureAuthority(bool requireController, bool resizing = false)
    {
        CheckAuthorityCallback(requireController, resizing);
        lock (_sync)
        {
            CheckAuthorityLocked(_authorityRevision, requireController);
            return (_authorityRevision, _authority.Token);
        }
    }
    private void CheckAuthority(long revision, bool requireController, bool resizing = false)
    {
        CheckAuthorityCallback(requireController, resizing);
        lock (_sync)
        {
            CheckAuthorityLocked(revision, requireController);
        }
    }
    private void CheckAuthorityLocked(long revision, bool requireController)
    {
        if (_dead || !_attached || revision != _authorityRevision || (requireController && _controllerId != AttachmentId))
            throw new PersistentPtyException("input-disarmed");
    }
    private void CheckAuthorityCallback(bool requireController, bool resizing = false)
    {
        if (resizing && CanResize is not null ? !CanResize() : CanWrite is not null && !CanWrite(requireController)) throw new PersistentPtyException("input-disarmed");
    }
    private void Start(Action action)
    {
        if (TryStart is null) action();
        else if (!TryStart(action)) throw new PersistentPtyException("input-disarmed");
    }

    private static int Weight(PersistentPtyEvent value) => value switch
    { PersistentPtyBytes bytes => bytes.Bytes.Length, PersistentPtyResized resize => resize.Checkpoint.Length, _ => 256 };

    private static PersistentPtyEvent ParseEvent(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var e = doc.RootElement;
            long Number(string name) => PersistentPtyValidation.Offset(e.GetProperty(name).GetInt64());
            string Text(string name) => e.GetProperty(name).GetString() ?? throw new PersistentPtyException("invalid-event");
            switch (Text("type"))
            {
                case "attached":
                    var replay = e.GetProperty("replay");
                    var role = Text("role") switch { "observer" => PersistentPtyRole.Observer, "controller" => PersistentPtyRole.Controller, _ => throw new PersistentPtyException("invalid-role") };
                    return new PersistentPtyAttached(Text("attachmentID"), e.GetProperty("inputProtocol").GetInt32(),
                        OpenCodeClient.PersistentDeserialize<PersistentPtyInfo>(e.GetProperty("info")), role, Number("generation"),
                        new(PersistentPtyValidation.Offset(replay.GetProperty("requestedOffset").GetInt64()),
                            PersistentPtyValidation.Offset(replay.GetProperty("availableOffset").GetInt64()),
                            PersistentPtyValidation.Offset(replay.GetProperty("endOffset").GetInt64()), replay.GetProperty("truncated").GetBoolean()));
                case "replay_complete": return new PersistentPtyReplayComplete(Number("endOffset"));
                case "resized":
                    var size = new PersistentPtySize(e.GetProperty("cols").GetUInt16(), e.GetProperty("rows").GetUInt16());
                    PersistentPtyValidation.Size(size);
                    return new PersistentPtyResized(size, Number("generation"), ImmutableArray.CreateRange(e.GetProperty("checkpoint").GetBytesFromBase64()));
                case "controller_changed":
                    return new PersistentPtyControllerChanged(e.TryGetProperty("attachmentID", out var id) && id.ValueKind != JsonValueKind.Null ? id.GetString() : null, Number("generation"));
                case "exited": return new PersistentPtyExited(e.TryGetProperty("exitCode", out var exit) && exit.ValueKind != JsonValueKind.Null ? exit.GetInt32() : null, Number("finalOffset"));
                case "title_changed": return new PersistentPtyTitleChanged(Text("title"));
                case "foreground_process_changed": return new PersistentPtyForegroundChanged(e.GetProperty("process").GetString());
                default: throw new PersistentPtyException("unknown-event");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new PersistentPtyException("invalid-event"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SetDead(); _lifetime.Cancel(); _socket.Abort();
        await _receive.ConfigureAwait(false);
        // Abort closes only this attachment, never DELETEs or shuts down the persistent process.
        _socket.Dispose(); _invoker.Dispose();
        _lifetime.Dispose();
        // CTS/semaphore remain valid for already-waiting TX callers to observe cancellation safely.
    }
}
