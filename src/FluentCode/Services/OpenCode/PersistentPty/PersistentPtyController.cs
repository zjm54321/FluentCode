using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// Renderer-aware, source-bound persistent-terminal attachment. Snapshot→attach is not an atomic generation fence.
/// Reconnection restores observation only; no input/control/resize operation is ever retried.
/// </summary>
public sealed class PersistentPtyController : IAsyncDisposable
{
    private readonly PersistentPtyOrigin _origin;
    private readonly string _ptyId;
    private readonly IPersistentPtyRenderer _renderer;
    private readonly Func<PersistentPtyOrigin, bool> _isCurrent;
    private readonly CancellationTokenSource _stop;
    private readonly object _sync = new();
    private CancellationTokenSource? _attempt;
    private PersistentPtyConnection? _connection;
    private PersistentPtyState _state = new(new(0), PersistentPtyPhase.Connecting, PersistentPtyRole.Observer, false, false, null, null);
    private Task? _run;
    private bool _consent, _controlSent, _disposed;
    private volatile bool _sentInput;
    private PersistentPtySize? _requestedControlSize;
    private long _consentGeneration;
    private long _renderedGeometryRevision;
    private long _epoch;
    private TaskCompletionSource<bool>? _acquireRequest, _activeAcquire;
    public PersistentPtyState State => Volatile.Read(ref _state);
    public event Action<PersistentPtyState>? StateChanged;

    public PersistentPtyController(PersistentPtyOrigin origin, string ptyId, IPersistentPtyRenderer renderer,
        Func<PersistentPtyOrigin, bool> isCurrent, bool acquireOnStart = false)
    {
        ArgumentNullException.ThrowIfNull(origin); ArgumentNullException.ThrowIfNull(origin.Source);
        PersistentPtyValidation.Id(origin.SessionId); PersistentPtyValidation.Id(ptyId);
        if (origin.Revision != origin.Source.Revision || origin.Directory is null) throw new PersistentPtyException("invalid-origin");
        _origin = origin; _ptyId = ptyId; _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(origin.Source.RevocationToken);
        if (acquireOnStart) _acquireRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _run ??= RunCoreAsync(cancellationToken);
        }
    }

    private bool Current() => !_stop.IsCancellationRequested && !_origin.Source.IsRevoked
        && _origin.Revision == _origin.Source.Revision && _isCurrent(_origin);
    private bool CanSend() => Current() && State.Phase == PersistentPtyPhase.Ready;
    private void RequireCurrent(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Current()) throw new OperationCanceledException("Persistent terminal origin expired.", ct);
    }

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            while (!lifetime.IsCancellationRequested && Current())
            {
                TaskCompletionSource<bool>? acquisition;
                lock (_sync) { acquisition = _acquireRequest; _acquireRequest = null; _activeAcquire = acquisition; }
                var epoch = new PersistentPtyEpoch(Interlocked.Increment(ref _epoch));
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (_sync) { _attempt = attempt; _consent = _controlSent = _sentInput = false; _renderedGeometryRevision = 0; }
                Publish(new(epoch, PersistentPtyPhase.Connecting, PersistentPtyRole.Observer, false, State.InputOutcomeUncertain, null, null));
                PersistentPtyConnection? connection = null;
                try
                {
                    using var client = _origin.Source.CreateClient();
                    var capability = await client.GetPersistentPtyCapabilityAsync(attempt.Token).ConfigureAwait(false);
                    RequireCurrent(attempt.Token);
                    if (capability != true)
                    {
                        Publish(State with { Phase = capability == false ? PersistentPtyPhase.Unsupported : PersistentPtyPhase.Unavailable,
                            FailureCode = capability == false ? "unsupported" : "capability-unknown" });
                        return;
                    }
                    var snapshot = await client.GetPersistentPtySnapshotAsync(_origin.SessionId, _ptyId, attempt.Token).ConfigureAwait(false);
                    RequireCurrent(attempt.Token);
                    Publish(State with { Info = snapshot.Info, Phase = PersistentPtyPhase.Replaying });
                    RequireRendererGrid(snapshot.Info.Size);
                    await _renderer.ResetAsync(epoch, snapshot.Info.Size, snapshot.Checkpoint.ToArray(), attempt.Token).ConfigureAwait(false);
                    RequireCurrent(attempt.Token);
                    connection = (PersistentPtyConnection)await client.ConnectPersistentPtyAsync(_origin.SessionId, _ptyId,
                        snapshot.Info.Output.Tail, attempt.Token, requestControl: acquisition is not null).ConfigureAwait(false);
                    connection.CanWrite = requireController => CanSend() && (!requireController || State.InputArmed);
                    connection.CanResize = () => CanSend() && State.Phase == PersistentPtyPhase.Ready && State.Role == PersistentPtyRole.Controller
                        && Volatile.Read(ref _consent) && Volatile.Read(ref _controlSent);
                    connection.TryStart = action => _origin.Source.TryExecute(_stop.Token, () =>
                    {
                        if (!Current()) throw new PersistentPtyException("input-disarmed");
                        action(); // Synchronous network initiation only; never a renderer/UI callback.
                    });
                    connection.AuthorityChanged += AuthorityChanged;
                    connection.InputStarted += OnInputStarted;
                    lock (_sync) _connection = connection;
                    var attached = false;
                    var replayComplete = false;
                    long receivedOffset = snapshot.Info.Output.Tail, replayEnd = -1, generation = -1;
                    await foreach (var item in connection.ReadEventsAsync(attempt.Token).ConfigureAwait(false))
                    {
                        RequireCurrent(attempt.Token);
                        switch (item)
                        {
                            case PersistentPtyAttached a:
                                RequireRendererGrid(a.Info.Size);
                                if (attached || a.Info.Size != snapshot.Info.Size) throw new PersistentPtyException("geometry-mismatch");
                                if (a.Replay.Truncated || a.Replay.RequestedOffset != receivedOffset || a.Replay.AvailableOffset > receivedOffset
                                    || a.Replay.EndOffset < receivedOffset || a.Replay.EndOffset > a.Info.Output.Tail)
                                    throw new PersistentPtyException("replay-gap");
                                attached = true; replayEnd = a.Replay.EndOffset; generation = a.Generation;
                                Publish(State with { Info = a.Info });
                                AuthorityChanged();
                                break;
                            case PersistentPtyBytes b:
                                if (!attached) throw new PersistentPtyException("missing-attached");
                                receivedOffset = checked(receivedOffset + b.Bytes.Length);
                                if (!replayComplete && receivedOffset > replayEnd) throw new PersistentPtyException("replay-overrun");
                                await _renderer.WriteAsync(epoch, b.Bytes.ToArray(), attempt.Token).ConfigureAwait(false);
                                RequireCurrent(attempt.Token);
                                break;
                            case PersistentPtyReplayComplete replay:
                                if (!attached || replayComplete || replay.EndOffset != replayEnd || receivedOffset != replayEnd)
                                    throw new PersistentPtyException("replay-offset-mismatch");
                                // Every prior write/checkpoint has been awaited. This is a renderer ACK barrier.
                                replayComplete = true;
                                if (acquisition is not null && connection.Authority.ControllerId == connection.AttachmentId)
                                {
                                    lock (_sync) { _consent = _controlSent = true; _consentGeneration = connection.Authority.Generation; _requestedControlSize = null; }
                                }
                                Publish(State with { Phase = PersistentPtyPhase.Ready, FailureCode = null });
                                AuthorityChanged();
                                acquisition?.TrySetResult(State.InputArmed);
                                lock (_sync) if (ReferenceEquals(_activeAcquire, acquisition)) _activeAcquire = null;
                                break;
                            case PersistentPtyResized resized:
                                RequireRendererGrid(resized.Size);
                                if (resized.Generation < generation) throw new PersistentPtyException("stale-geometry");
                                generation = resized.Generation;
                                await _renderer.ResizeAsync(epoch, resized.Size, resized.Checkpoint.ToArray(), attempt.Token).ConfigureAwait(false);
                                RequireCurrent(attempt.Token);
                                Volatile.Write(ref _renderedGeometryRevision, resized.Revision);
                                Publish(State with { Info = State.Info! with { Size = resized.Size } });
                                AuthorityChanged();
                                break;
                            case PersistentPtyControllerChanged changed:
                                if (changed.Generation < generation) throw new PersistentPtyException("stale-generation");
                                generation = changed.Generation; AuthorityChanged();
                                break;
                            case PersistentPtyTitleChanged title:
                                Publish(State with { Info = State.Info! with { Title = title.Title } }); break;
                            case PersistentPtyForegroundChanged process:
                                Publish(State with { Info = State.Info! with { ForegroundProcess = process.Process } }); break;
                            case PersistentPtyExited exited:
                                if (exited.FinalOffset != receivedOffset) throw new PersistentPtyException("exit-offset-mismatch");
                                Publish(State with { Phase = PersistentPtyPhase.Closed, InputArmed = false,
                                    Info = State.Info! with { Status = "exited", ExitCode = exited.ExitCode } });
                                return;
                        }
                    }
                    throw new PersistentPtyException("socket-closed");
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested || !Current()) { break; }
                catch (OperationCanceledException) when (_acquireRequest is not null) { /* Intentional observer-to-controller attachment change. */ }
                catch (Exception ex)
                {
                    if (_sentInput) MarkInputUncertain();
                    if (ex is PersistentPtyException { Code: "unsupported-grid" or "unsupported-renderer-state" } unsupported)
                    {
                        Publish(State with { Phase = PersistentPtyPhase.Unavailable, InputArmed = false, FailureCode = unsupported.Code });
                        return; // The offline renderer cannot consume this canonical state; retrying cannot repair it.
                    }
                    var code = connection?.FailureCode ?? (ex is PersistentPtyException pty ? pty.Code : "attachment-failed");
                    Publish(State with { Phase = PersistentPtyPhase.Disconnected, InputArmed = false, FailureCode = code });
                    acquisition?.TrySetResult(false);
                    if (code is "not-found" or "identity-mismatch" or "unauthorized") return;
                }
                finally
                {
                    acquisition?.TrySetResult(false);
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeAcquire, acquisition)) _activeAcquire = null;
                        _consent = _controlSent = false;
                        if (ReferenceEquals(_connection, connection)) _connection = null;
                        if (ReferenceEquals(_attempt, attempt)) _attempt = null;
                    }
                    if (connection is not null)
                    {
                        connection.AuthorityChanged -= AuthorityChanged;
                        connection.InputStarted -= OnInputStarted;
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }
                }
                // Bounded backoff, observation only. Re-bootstrap uses a fresh snapshot, cursor and single-use ticket.
                lock (_sync) if (_acquireRequest is not null) continue;
                await Task.Delay(TimeSpan.FromMilliseconds(500), lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            if (lifetime.IsCancellationRequested || !Current())
                Publish(State with { Phase = PersistentPtyPhase.Closed, InputArmed = false }, allowStale: true);
        }
    }

    private void AuthorityChanged()
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is null || !Current()) return;
        var authority = connection.Authority;
        PersistentPtyState next;
        CancellationTokenSource? failedAttempt = null;
        lock (_sync)
        {
            if (!ReferenceEquals(connection, _connection)) return;
            var own = authority.ControllerId == connection.AttachmentId && !authority.Dead;
            if (!own && (authority.Dead || !_consent || authority.Generation > _consentGeneration))
            { _consent = _controlSent = false; _requestedControlSize = null; }
            if (_requestedControlSize == State.Info?.Size) _requestedControlSize = null;
            next = State with { Role = own ? PersistentPtyRole.Controller : PersistentPtyRole.Observer,
                InputArmed = own && _consent && _controlSent && _requestedControlSize is null
                    && State.Info?.Size == connection.CanonicalSize && _renderedGeometryRevision == connection.GeometryRevision
                    && State.Phase == PersistentPtyPhase.Ready };
            if (authority.Dead && connection.FailureCode is not null)
            {
                next = next with { InputOutcomeUncertain = next.InputOutcomeUncertain || _sentInput };
                failedAttempt = _attempt;
            }
        }
        Publish(next, authorityOnly: true);
        // Cancel blocked renderer consumption immediately on protocol/queue/transport failure.
        // Exited events instead drain prior output and the ordered terminal-exit notification.
        try { failedAttempt?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask TakeControlAsync(ushort cols, ushort rows, CancellationToken cancellationToken = default)
    {
        PersistentPtyValidation.Size(new(cols, rows));
        var connection = RequireConnection(armed: false);
        var authority = connection.Authority;
        lock (_sync)
        {
            if (!ReferenceEquals(connection, _connection)) throw new PersistentPtyException("input-disarmed");
            _consent = true; _controlSent = false; _requestedControlSize = new(cols, rows); _consentGeneration = authority.Generation;
        }
        try
        {
            await connection.RequestControlAsync(cols, rows, cancellationToken).ConfigureAwait(false);
            lock (_sync) if (ReferenceEquals(connection, _connection) && _consent) _controlSent = true;
            AuthorityChanged();
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(connection, _connection)) _consent = _controlSent = false;
            }
            AuthorityChanged();
            throw;
        }
    }

    /// <summary>First user input may acquire an unowned terminal atomically, never displacing another controller.</summary>
    public async Task<bool> TryTakeControlAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection(armed: false);
        Task<bool>? acquiring = null; bool own;
        lock (_sync)
        {
            var authority = connection.Authority;
            own = !authority.Dead && authority.ControllerId == connection.AttachmentId;
            if (own)
            {
                _consent = _controlSent = true; _consentGeneration = authority.Generation;
            }
            else
            {
                var request = _acquireRequest ?? _activeAcquire;
                if (request is null)
                {
                    request = _acquireRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _attempt?.Cancel(); // Reattach with role=controller&takeover=false; server decides atomically.
                }
                acquiring = request.Task;
            }
        }
        if (own) { AuthorityChanged(); return true; }
        return await acquiring!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForInputReadyAsync(CancellationToken cancellationToken = default, PersistentPtySize? expectedSize = null)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(PersistentPtyState state)
        {
            if (state.InputArmed && (expectedSize is null || state.Info?.Size == expectedSize)) ready.TrySetResult();
            else if (state.Phase != PersistentPtyPhase.Ready || state.Role != PersistentPtyRole.Controller)
                ready.TrySetException(new PersistentPtyException("input-disarmed"));
        }
        StateChanged += Changed;
        try { Changed(State); await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false); }
        finally { StateChanged -= Changed; }
    }

    public async ValueTask SendInputAsync(ReadOnlyMemory<byte> bytes, ushort cols, ushort rows, CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0) return;
        var connection = RequireConnection(armed: true);
        if (State.Info?.Size != new PersistentPtySize(cols, rows)) throw new PersistentPtyException("geometry-mismatch");
        try
        {
            // CanWrite is rechecked under serialized TX; loss cancels every already-waiting write.
            await connection.SendInputAsync(bytes, cols, rows, cancellationToken).ConfigureAwait(false);
        }
        catch (PersistentPtyException error) when (error.Code == "send-uncertain")
        {
            lock (_sync)
            {
                if (ReferenceEquals(connection, _connection)) { _consent = _controlSent = false; _attempt?.Cancel(); }
            }
            Publish(State with { InputOutcomeUncertain = true });
            throw new PersistentPtyException("input-outcome-uncertain");
        }
    }

    public async ValueTask ResizeAsync(ushort cols, ushort rows, CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection(armed: true);
        var requested = new PersistentPtySize(cols, rows);
        if (State.Info?.Size == requested) return;
        lock (_sync) _requestedControlSize = requested;
        Publish(State with { InputArmed = false }); // Keyboard input waits for the ordered resized checkpoint, not merely PUT completion.
        try { await connection.ResizeAsync(cols, rows, cancellationToken).ConfigureAwait(false); }
        catch
        {
            lock (_sync) if (ReferenceEquals(connection, _connection)) _requestedControlSize = null;
            AuthorityChanged(); throw;
        }
        // Canonical renderer geometry changes only when the ordered resized checkpoint is consumed.
    }
    public Task ReleaseControlAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) { _consent = _controlSent = false; _attempt?.Cancel(); }
        Publish(State with { InputArmed = false });
        return Task.CompletedTask;
    }

    private PersistentPtyConnection RequireConnection(bool armed)
    {
        var connection = Volatile.Read(ref _connection);
        if (!CanSend() || connection is null || (armed && !State.InputArmed)) throw new PersistentPtyException("input-disarmed");
        return connection;
    }
    private void MarkInputUncertain() => Publish(State with { InputOutcomeUncertain = true, InputArmed = false });
    private void OnInputStarted() => _sentInput = true;
    private static void RequireRendererGrid(PersistentPtySize size)
    {
        // Frozen offline renderer resource envelope. Do not clamp/reflow an authoritative server grid.
        if (size.Cols is < 2 or > 1000 || size.Rows is < 1 or > 1000)
            throw new PersistentPtyException("unsupported-grid");
    }
    private void Publish(PersistentPtyState state, bool allowStale = false, bool authorityOnly = false)
    {
        if (!allowStale && !Current()) return;
        lock (_sync)
        {
            if (state.Epoch.Value < State.Epoch.Value) return;
            // RX authority callbacks may race a renderer ACK. Merge into the current epoch's
            // projection rather than restoring an older phase, geometry, title or process.
            if (authorityOnly) state = State with { InputArmed = true,
                InputOutcomeUncertain = state.InputOutcomeUncertain || State.InputOutcomeUncertain };
            // Preserve uncertainty across concurrent state notifications and every future reconnect.
            state = state with { InputOutcomeUncertain = state.InputOutcomeUncertain || State.InputOutcomeUncertain };
            if (_connection is { } connection)
            {
                var authority = connection.Authority;
                var own = !authority.Dead && authority.ControllerId == connection.AttachmentId;
                state = state with { Role = own ? PersistentPtyRole.Controller : PersistentPtyRole.Observer,
                    InputArmed = state.Phase == PersistentPtyPhase.Ready && own && _consent && _controlSent
                        && _requestedControlSize is null && state.Info?.Size == connection.CanonicalSize
                        && _renderedGeometryRevision == connection.GeometryRevision && state.InputArmed };
            }
            else state = state with { InputArmed = false };
            Volatile.Write(ref _state, state);
        }
        try { StateChanged?.Invoke(state); }
        catch (Exception) { /* UI observer failures never become socket errors or expose peer data. */ }
    }

    public async ValueTask DisposeAsync()
    {
        Task? run;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _consent = _controlSent = false; run = _run;
            _acquireRequest?.TrySetCanceled(); _activeAcquire?.TrySetCanceled();
        }
        _stop.Cancel();
        Publish(State with { Phase = PersistentPtyPhase.Closed, InputArmed = false }, allowStale: true);
        if (run is not null) await run.ConfigureAwait(false);
    }
}
