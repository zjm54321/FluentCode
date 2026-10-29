using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Read-only per-session inventory; no coreShell mapping, control frames, creation or deletion.</summary>
internal sealed class OpenCodePersistentPtyInventory : IDisposable
{
    private readonly PersistentPtyOrigin _origin;
    private readonly OpenCodeClient _client;
    private readonly Func<PersistentPtyOrigin, bool> _isCurrent;
    private readonly CancellationTokenSource _stop;
    private readonly object _sync = new();
    private Task? _running;
    private bool _again, _recheck, _capabilityRead;
    private bool? _capability;
    private OpenCodePersistentPtyInventoryState _state = new(OpenCodePersistentPtyInventoryPhase.Loading, ImmutableArray<PersistentPtyInfo>.Empty);
    internal OpenCodePersistentPtyInventoryState State => Volatile.Read(ref _state);
    internal event Action<OpenCodePersistentPtyInventoryState>? Changed;
    internal OpenCodePersistentPtyInventory(PersistentPtyOrigin origin, OpenCodeClient client, Func<PersistentPtyOrigin, bool> isCurrent)
    { _origin = origin; _client = client; _isCurrent = isCurrent; _stop = CancellationTokenSource.CreateLinkedTokenSource(origin.Source.RevocationToken); }
    private bool Current() => !_stop.IsCancellationRequested && !_origin.Source.IsRevoked && _origin.Source.Revision == _origin.Revision && _isCurrent(_origin);
    internal Task RefreshAsync(bool recheckCapability = false)
    {
        lock (_sync)
        {
            if (!Current()) return Task.CompletedTask;
            _again = true; _recheck |= recheckCapability;
            return _running ??= Task.Run(ReadLoopAsync);
        }
    }
    private Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        Task<T>? pending = null;
        _origin.Source.TryExecute(_stop.Token, () => { if (Current()) pending = read(); });
        return pending ?? Task.FromCanceled<T>(new CancellationToken(true));
    }
    private async Task ReadLoopAsync()
    {
        while (Current())
        {
            bool recheck;
            lock (_sync)
            {
                if (!_again) { _running = null; return; }
                _again = false; recheck = _recheck; _recheck = false;
            }
            Publish(new(OpenCodePersistentPtyInventoryPhase.Loading, State.Rows));
            try
            {
                if (!_capabilityRead || recheck)
                {
                    _capability = await ReadAsync(() => _client.GetPersistentPtyCapabilityAsync(_stop.Token)).ConfigureAwait(false);
                    _capabilityRead = true;
                }
                if (!Current()) break;
                if (_capability != true) Publish(new(_capability == false ? OpenCodePersistentPtyInventoryPhase.Unsupported : OpenCodePersistentPtyInventoryPhase.Unavailable,
                    ImmutableArray<PersistentPtyInfo>.Empty));
                else
                {
                    var rows = await ReadAsync(() => _client.ListPersistentPtysAsync(_origin.SessionId, _stop.Token)).ConfigureAwait(false);
                    if (Current()) Publish(new(OpenCodePersistentPtyInventoryPhase.Ready, ImmutableArray.CreateRange(rows)));
                }
            }
            catch (OperationCanceledException) when (!Current()) { break; }
            catch (Exception error) when (error is OpenCodeException or PersistentPtyException or OperationCanceledException or ObjectDisposedException)
            { Publish(new(OpenCodePersistentPtyInventoryPhase.Unavailable, ImmutableArray<PersistentPtyInfo>.Empty)); }
        }
        lock (_sync) _running = null;
    }
    private void Publish(OpenCodePersistentPtyInventoryState state)
    {
        if (!Current()) return;
        Volatile.Write(ref _state, state);
        try { Changed?.Invoke(state); } catch (Exception) { /* Observer failure cannot turn an inventory read into a terminal mutation. */ }
    }
    public void Dispose() { if (!_stop.IsCancellationRequested) _stop.Cancel(); }
}
