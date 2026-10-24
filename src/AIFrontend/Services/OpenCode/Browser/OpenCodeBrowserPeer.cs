using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>One source/location/session attachment. Start and dispose on the backend's owning context.</summary>
public sealed class OpenCodeBrowserPeer : IAsyncDisposable
{
    private sealed record Signal(string Kind, OpenCodeEvent? Event = null, Attempt? Owner = null,
        string? Value = null, Exception? Error = null);
    private sealed class Attempt(string connectionId, CancellationToken lifetime) : IDisposable
    {
        internal string ConnectionId { get; } = connectionId;
        internal CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal Channel<OpenCodeBrowserState> States { get; } = Channel.CreateBounded<OpenCodeBrowserState>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        internal Dictionary<string, CancellationTokenSource> Requests { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        internal List<Task> Workers { get; } = new();
        internal bool Attached;
        public void Dispose()
        {
            Stop.Cancel();
            States.Writer.TryComplete();
            Stop.Dispose();
        }
    }

    private readonly OpenCodeClient _client;
    private readonly string _directory;
    private readonly IOpenCodeBrowserEventFeed _feed;
    private readonly IOpenCodeBrowserSessionBackend _backend;
    private readonly TimeSpan _commandTimeout;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Task> _cleanup = new();
    private Task? _run;
    private Attempt? _attempt;
    private int _stateQueued;
    private bool _disposed;
    private bool _paused;
    private int _retry;
    private bool _attempted;

    public OpenCodeBrowserPeer(OpenCodeClient client, string directory, IOpenCodeBrowserEventFeed feed,
        IOpenCodeBrowserSessionBackend backend, TimeSpan? commandTimeout = null, Func<int, TimeSpan>? backoff = null)
    {
        _client = client;
        _directory = directory;
        _feed = feed;
        _backend = backend;
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(60);
        _backoff = backoff ?? (retry => TimeSpan.FromSeconds(Math.Min(1 << Math.Min(retry, 4), 15)));
    }

    public string SessionId => _backend.SessionId;
    public OpenCodeBrowserAvailability Availability { get; private set; } = OpenCodeBrowserAvailability.Detached;
    public string AvailabilityReason { get; private set; } = "智能体暂时不能使用浏览器：会话尚未接入";
    public Exception? LastError { get; private set; }
    public event EventHandler? AvailabilityChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run is not null) return;
        // These subscriptions MUST precede even the synchronous initiation of attach.
        _feed.Control += OnControl;
        _feed.ConnectionChanged += OnConnectionChanged;
        _backend.StateChanged += OnStateChanged;
        OnConnectionChanged(this, EventArgs.Empty);
        _run = RunAsync();
    }

    /// <summary>Explicit user action 「改为在这里提供」 (also usable for 「立即重连」).</summary>
    public Task ReclaimAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Start();
        _signals.Writer.TryWrite(new("reclaim"));
        return Task.CompletedTask;
    }

    private void OnControl(object? sender, OpenCodeEvent item) => _signals.Writer.TryWrite(new("control", item));
    private void OnConnectionChanged(object? sender, EventArgs e)
        => _signals.Writer.TryWrite(new("health", Value: _feed.IsConnected ? "connected" : "disconnected"));
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _stateQueued, 1) == 0) _signals.Writer.TryWrite(new("state"));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(_stop.Token))
            {
                if (signal.Kind == "state") Interlocked.Exchange(ref _stateQueued, 0);
                if (signal.Kind == "done")
                {
                    if (signal.Owner is { } completed && signal.Value is { } requestId
                        && completed.Requests.Remove(requestId, out var cancellation)) cancellation.Dispose();
                    continue;
                }
                if (signal.Owner is not null && !ReferenceEquals(signal.Owner, _attempt)) continue;
                switch (signal.Kind)
                {
                    case "health":
                        if (signal.Value == "disconnected")
                        {
                            EndAttempt();
                            if (!_paused) SetAvailability(OpenCodeBrowserAvailability.Reconnecting);
                        }
                        else if (!_paused && _attempt is null)
                        {
                            if (_attempted) _ = RetryAsync(_backoff(_retry++));
                            else BeginAttempt();
                        }
                        break;
                    case "reclaim":
                        EndAttempt();
                        _paused = false;
                        _retry = 0;
                        if (_feed.IsConnected) BeginAttempt();
                        else SetAvailability(OpenCodeBrowserAvailability.Reconnecting);
                        break;
                    case "retry":
                        if (!_paused && _feed.IsConnected && _attempt is null) BeginAttempt();
                        break;
                    case "ended":
                        EndAttempt();
                        if (signal.Value == "replaced")
                        {
                            _paused = true;
                            SetAvailability(OpenCodeBrowserAvailability.Replaced);
                        }
                        else HandleFailure(signal.Error);
                        break;
                    case "fault":
                        EndAttempt();
                        HandleFailure(signal.Error);
                        break;
                    case "state":
                        if (_attempt is { Attached: true } stateOwner) stateOwner.States.Writer.TryWrite(_backend.State);
                        break;
                    case "control":
                        HandleControl(signal.Event!);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { EndAttempt(); SetAvailability(OpenCodeBrowserAvailability.Detached); }
    }

    private void BeginAttempt()
    {
        var owner = new Attempt(Guid.NewGuid().ToString("N"), _stop.Token);
        _attempt = owner;
        SetAvailability(!_attempted ? OpenCodeBrowserAvailability.Attaching : OpenCodeBrowserAvailability.Reconnecting);
        _attempted = true;
        owner.Workers.Add(HoldAttachmentAsync(owner, owner.Stop.Token));
    }

    private async Task HoldAttachmentAsync(Attempt owner, CancellationToken ct)
    {
        try
        {
            var reason = await _client.BrowserAttachAsync(_directory, SessionId, owner.ConnectionId, ct);
            _signals.Writer.TryWrite(new("ended", Owner: owner, Value: reason));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _signals.Writer.TryWrite(new("ended", Owner: owner, Error: ex)); }
    }

    private void HandleControl(OpenCodeEvent item)
    {
        if (_attempt is not { } owner || item.LocationDirectory != _directory
            || !item.Data.TryGetProperty("connectionID", out var connection) || connection.ValueKind != JsonValueKind.String
            || connection.GetString() != owner.ConnectionId
            || !item.Data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
        if (type.GetString() == "attached")
        {
            if (owner.Attached) return;
            if (!item.Data.TryGetProperty("version", out var version) || !version.TryGetInt32(out var protocol) || protocol != 4)
            {
                EndAttempt();
                _paused = true;
                SetAvailability(OpenCodeBrowserAvailability.Unsupported);
                return;
            }
            owner.Attached = true;
            _retry = 0;
            LastError = null;
            SetAvailability(OpenCodeBrowserAvailability.Available);
            owner.States.Writer.TryWrite(_backend.State);
            owner.Workers.Add(PostStatesAsync(owner, owner.Stop.Token));
            return;
        }
        if (!owner.Attached || !item.Data.TryGetProperty("requestID", out var request) || request.ValueKind != JsonValueKind.String
            || request.GetString() is not { } requestId) return;
        if (type.GetString() == "cancel")
        {
            if (owner.Requests.TryGetValue(requestId, out var cancellation)) cancellation.Cancel();
        }
        else if (type.GetString() == "command" && owner.Seen.Add(requestId))
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Stop.Token);
            owner.Requests.Add(requestId, cancellation);
            owner.Workers.RemoveAll(task => task.IsCompletedSuccessfully);
            owner.Workers.Add(ExecuteAsync(owner, requestId, cancellation, owner.Stop.Token));
        }
    }

    private async Task PostStatesAsync(Attempt owner, CancellationToken ct)
    {
        try
        {
            await foreach (var state in owner.States.Reader.ReadAllAsync(ct))
                await _client.PostBrowserStateAsync(_directory, SessionId, owner.ConnectionId, state, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _signals.Writer.TryWrite(new("fault", Owner: owner, Error: ex)); }
    }

    private async Task ExecuteAsync(Attempt owner, string requestId, CancellationTokenSource request, CancellationToken attachment)
    {
        try
        {
            OpenCodeBrowserOutcome outcome;
            var executingBackend = false;
            try
            {
                var command = await _client.GetBrowserCommandAsync(_directory, SessionId, owner.ConnectionId, requestId, request.Token);
                executingBackend = true;
                var execution = _backend.ExecuteAsync(command, request.Token);
                // WaitAsync enforces the guard even when a backend ignores cancellation.
                try { outcome = await execution.WaitAsync(_commandTimeout, request.Token); }
                finally
                {
                    // Observe a late backend failure without waiting for a stuck backend.
                    _ = execution.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            catch (TimeoutException)
            {
                request.Cancel();
                outcome = new OpenCodeBrowserOutcome.Failure("timeout", "浏览器操作超时；操作可能已经执行，请先检查页面再决定是否重试。");
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            { outcome = new OpenCodeBrowserOutcome.Failure("cancelled", "浏览器操作已取消；操作可能已经执行。"); }
            catch (OpenCodeException) when (!executingBackend) { throw; } // Fetch failures are not backend outcomes.
            catch (Exception)
            { outcome = new OpenCodeBrowserOutcome.Failure("execution_failed", "浏览器操作失败。"); }
            attachment.ThrowIfCancellationRequested();
            await _client.PostBrowserResultAsync(_directory, SessionId, owner.ConnectionId, requestId, outcome, attachment);
        }
        catch (OperationCanceledException) when (attachment.IsCancellationRequested) { }
        catch (Exception ex) { _signals.Writer.TryWrite(new("fault", Owner: owner, Error: ex)); }
        finally
        {
            _signals.Writer.TryWrite(new("done", Owner: owner, Value: requestId));
            // Removal is serialized by the actor; cancellation can still be queued before it.
            // CTS disposal is delayed until that removal (or attempt teardown).
        }
    }

    private void HandleFailure(Exception? error)
    {
        LastError = error;
        if (error is OpenCodeBrowserRpcException { IsUnsupported: true })
        { _paused = true; SetAvailability(OpenCodeBrowserAvailability.Unsupported); return; }
        // Transport/unavailable may recover. Other errors remain visible and require explicit retry.
        _paused = error is not null && error is not (OpenCodeTransportException or OpenCodeTimeoutException
            or OpenCodeBrowserRpcException { IsUnavailable: true });
        SetAvailability(OpenCodeBrowserAvailability.Reconnecting);
        if (!_paused) _ = RetryAsync(_backoff(_retry++));
    }

    private async Task RetryAsync(TimeSpan delay)
    {
        try { await Task.Delay(delay, _stop.Token); _signals.Writer.TryWrite(new("retry")); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void EndAttempt()
    {
        var owner = _attempt;
        _attempt = null;
        if (owner is null) return;
        var requests = new List<CancellationTokenSource>(owner.Requests.Values);
        owner.Dispose();
        _cleanup.RemoveAll(task => task.IsCompletedSuccessfully);
        _cleanup.Add(CleanupAsync(owner.Workers, requests));
    }

    private static async Task CleanupAsync(List<Task> workers, List<CancellationTokenSource> requests)
    {
        await Task.WhenAll(workers);
        foreach (var request in requests) request.Dispose();
    }

    private void SetAvailability(OpenCodeBrowserAvailability availability)
    {
        Availability = availability;
        AvailabilityReason = availability switch
        {
            OpenCodeBrowserAvailability.Available => "智能体可以使用浏览器（45 个操作）",
            OpenCodeBrowserAvailability.Replaced => "这个会话的浏览器现在由另一台电脑 / 另一个窗口提供",
            OpenCodeBrowserAvailability.Unsupported => "这个 OpenCode 版本不支持让智能体使用浏览器",
            OpenCodeBrowserAvailability.Detached => "智能体暂时不能使用浏览器：会话尚未接入",
            _ when LastError is not null && _paused => "智能体暂时不能使用浏览器：接入失败，请重试",
            _ => "智能体暂时不能使用浏览器：与 OpenCode 的连接断开了，正在重连…",
        };
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _feed.Control -= OnControl;
        _feed.ConnectionChanged -= OnConnectionChanged;
        _backend.StateChanged -= OnStateChanged;
        _stop.Cancel();
        if (_run is not null) await _run;
        await Task.WhenAll(_cleanup);
        _stop.Dispose();
    }
}
