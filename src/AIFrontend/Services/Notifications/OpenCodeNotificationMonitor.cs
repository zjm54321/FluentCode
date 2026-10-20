using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

public enum OpenCodeNotificationObservationKind { Baseline, Messages, Gap, ReadFailed, PendingSnapshot, Interaction }

/// <summary>No message text, permission scope, form answers, raw errors or credentials.</summary>
public sealed record OpenCodeNotificationObservation(OpenCodeNotificationObservationKind Kind,
    string EndpointIdentity, string SessionId, string Code,
    IReadOnlyList<string>? PendingPermissionIds = null, IReadOnlyList<string>? PendingFormIds = null,
    NotificationInteractionResult? Interaction = null, IReadOnlyList<NotificationDispatchResult>? Dispatches = null);

public sealed class OpenCodeNotificationObservationEventArgs(OpenCodeNotificationObservation observation) : EventArgs
{
    public OpenCodeNotificationObservation Observation { get; } = observation;
}

internal interface IOpenCodeNotificationSink
{
    bool IsReady { get; }
    IReadOnlyList<NotificationDispatchResult> Observe(string endpoint, string session, IReadOnlyList<OpenCodeMessage> messages, bool historical);
    IReadOnlyList<NotificationDispatchResult> ObserveContext(string endpoint, string session, IReadOnlyList<OpenCodeMessage> delta,
        IReadOnlyList<OpenCodeMessage> history, string? title)
        => Observe(endpoint, session, delta, false);
    Task<NotificationInteractionResult> NotifyAsync(TrustedOpenCodeNotificationSource source, string session, string request, bool form, CancellationToken token);
    Task ReconcileAsync(TrustedOpenCodeNotificationSource source, string session, NotificationNavigationKind kind, IReadOnlySet<string> ids)
        => Task.CompletedTask;
    Task ReconcileViewAsync(TrustedOpenCodeNotificationSource source, string session, double viewed, IReadOnlyList<OpenCodeMessage> messages)
        => Task.CompletedTask;
}

/// <summary>App-owned, explicitly tracked sessions only. Does not own client/service, register Windows,
/// read settings, resolve uncertain mutations, or infer completion of an individual prompt.</summary>
public sealed partial class OpenCodeNotificationMonitor : IDisposable, IAsyncDisposable
{
    private sealed class Sink : IOpenCodeNotificationSink
    {
        private readonly CompletionNotificationService? service;
        private readonly TrustedOpenCodeNotificationSource source;
        internal Sink(CompletionNotificationService? service, TrustedOpenCodeNotificationSource source)
        {
            this.service = service;
            this.source = source;
            service?.TryBindNotificationSource(source); // Delivery failure must not disable observation.
        }
        public bool IsReady => service?.IsCurrentNotificationSource(source) == true;
        public IReadOnlyList<NotificationDispatchResult> Observe(string endpoint, string session, IReadOnlyList<OpenCodeMessage> messages, bool historical)
            => IsReady ? service!.ObserveTrustedOpenCodeMessages(source, session, messages, historical) : [];
        public IReadOnlyList<NotificationDispatchResult> ObserveContext(string endpoint, string session, IReadOnlyList<OpenCodeMessage> delta,
            IReadOnlyList<OpenCodeMessage> history, string? title)
            => IsReady ? service!.ObserveTrustedOpenCodeMessages(source, session, delta, false, title, history) : [];
        public Task<NotificationInteractionResult> NotifyAsync(TrustedOpenCodeNotificationSource source, string session, string request, bool form, CancellationToken token)
            => form ? service!.NotifyFormAsync(source, session, request, token) : service!.NotifyPermissionAsync(source, session, request, token);
        public Task ReconcileAsync(TrustedOpenCodeNotificationSource source, string session, NotificationNavigationKind kind, IReadOnlySet<string> ids)
            => service!.ReconcilePendingAsync(source, session, kind, ids);
        public Task ReconcileViewAsync(TrustedOpenCodeNotificationSource source, string session, double viewed, IReadOnlyList<OpenCodeMessage> messages)
            => service!.ReconcileViewedCompletionsAsync(source, session, viewed, messages);
    }

    private sealed partial class StreamState
    {
        internal bool Baseline;
        internal string? Anchor;
        internal HashSet<string> Permissions = new(StringComparer.Ordinal);
        internal HashSet<string> Forms = new(StringComparer.Ordinal);
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _poll = new(1, 1);
    private readonly Dictionary<string, StreamState> _tracked = new(StringComparer.Ordinal);
    private readonly TrustedOpenCodeNotificationSource _source;
    private readonly IOpenCodeNotificationSink _sink;
    private readonly TimeSpan _interval;
    private readonly int _pageSize;
    private readonly int _pageBudget;
    private CancellationTokenSource? _runCancellation;
    private Task? _run;
    private bool _disposed;

    public OpenCodeNotificationMonitor(CompletionNotificationService? service, TrustedOpenCodeNotificationSource source,
        TimeSpan? pollInterval = null, int pageSize = 100, int pageBudget = 8)
        : this(new Sink(service, source ?? throw new ArgumentNullException(nameof(source))), source, pollInterval, pageSize, pageBudget) { }

    internal OpenCodeNotificationMonitor(IOpenCodeNotificationSink sink, TrustedOpenCodeNotificationSource source,
        TimeSpan? pollInterval = null, int pageSize = 100, int pageBudget = 8)
    {
        _sink = sink;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _interval = pollInterval ?? TimeSpan.FromSeconds(5);
        if (_interval < TimeSpan.FromMilliseconds(100) || pageSize is < 1 or > 500 || pageBudget is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _pageSize = pageSize;
        _pageBudget = pageBudget;
    }

    public event EventHandler<OpenCodeNotificationObservationEventArgs>? Observed;

    public bool TrackSession(string sessionId)
    {
        if (!NotificationArgumentCodec.IsValidRequestId(sessionId) || !sessionId.StartsWith("ses", StringComparison.Ordinal))
            throw new ArgumentException("Invalid session identity.", nameof(sessionId));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tracked.ContainsKey(sessionId)) return false;
            if (_tracked.Count >= 128) throw new InvalidOperationException("Tracked session capacity reached.");
            _tracked.Add(sessionId, new());
            return true;
        }
    }

    public bool UntrackSession(string sessionId) { lock (_sync) return _tracked.Remove(sessionId); }

    /// <summary>Observation runs independently of notification availability. No automatic initialization.</summary>
    public bool Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is { IsCompleted: false }) return true;
            _runCancellation?.Dispose();
            _runCancellation = new();
            var token = _runCancellation.Token;
            _run = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await PollOnceAsync(token).ConfigureAwait(false);
                        await Task.Delay(_interval, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            });
            return true;
        }
    }

    public async Task StopAsync()
    {
        Task? run;
        lock (_sync) { _runCancellation?.Cancel(); run = _run; }
        if (run is not null) await run.ConfigureAwait(false);
    }

    internal async Task PollOnceAsync(CancellationToken token = default)
    {
        await _poll.WaitAsync(token).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, StreamState>[] streams;
            lock (_sync) { if (_disposed) return; streams = _tracked.ToArray(); }
            foreach (var pair in streams)
            {
                token.ThrowIfCancellationRequested();
                if (!Active(pair.Key, pair.Value)) continue;
                // Retain still-valid evidence during refresh. App expires it using its original evidence time.
                await MessagesAsync(pair.Key, pair.Value, token).ConfigureAwait(false);
                await PendingAsync(pair.Key, pair.Value, false, token).ConfigureAwait(false);
                await PendingAsync(pair.Key, pair.Value, true, token).ConfigureAwait(false);
                try { await ExecutionAsync(pair.Key, pair.Value, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { PublishUnknown(pair.Key, pair.Value); }
            }
        }
        finally { _poll.Release(); }
    }

    private bool Active(string session, StreamState state)
    {
        lock (_sync) return !_disposed && _tracked.TryGetValue(session, out var current) && ReferenceEquals(state, current);
    }

    private async Task<bool> MessagesAsync(string session, StreamState state, CancellationToken token)
    {
        state.History = null;
        try
        {
            var started = DateTimeOffset.UtcNow;
            var history = await OpenCodeMessageHistory.ReadAsync((query, ct) => _source.Client.GetMessagesAsync(session, query, ct),
                _pageSize, _pageBudget, token).ConfigureAwait(false);
            if (!Active(session, state)) return false;
            state.History = history;
            state.HistoryAt = started;
            var messages = history.Messages; // chronological, including every bounded page
            var newest = messages.LastOrDefault()?.Id;
            if (!history.Complete) Emit(session, state, OpenCodeNotificationObservationKind.Gap, "history-incomplete");
            if (!_sink.IsReady) return true;
            if (!state.Baseline)
            {
                _sink.Observe(_source.Identity, session, messages, true);
                state.Anchor = newest;
                state.Baseline = true;
                Emit(session, state, OpenCodeNotificationObservationKind.Baseline, "initial-history-suppressed");
                return true;
            }
            int anchor = state.Anchor is null ? -1 : messages.FindIndex(m => m.Id == state.Anchor);
            if (state.Anchor is not null && anchor < 0 || state.Anchor is null && !history.Complete)
            {
                Emit(session, state, OpenCodeNotificationObservationKind.Gap, "anchor-missing");
                return false; // No notification replay or anchor advance. Pending reads remain independent.
            }
            token.ThrowIfCancellationRequested();
            if (!Active(session, state)) return false;
            var collected = messages.Skip(anchor + 1).ToArray();
            string? title = null;
            if (collected.Any(m => m.Type == "idle" && m.Outcome == "succeeded"))
            {
                try { title = (await _source.Client.GetSessionInfoAsync(session, token).ConfigureAwait(false)).Title; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { /* Metadata failure must not suppress a completion. */ }
            }
            if (!Active(session, state)) return false;
            var results = _sink.ObserveContext(_source.Identity, session, collected, messages, title);
            state.Anchor = newest;
            Emit(session, state, OpenCodeNotificationObservationKind.Messages, "anchored", dispatches: results);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "messages-read-failed-anchor-retained"); return false; }
    }

    private async Task PendingAsync(string session, StreamState state, bool form, CancellationToken token)
    {
        if (form) state.FormsAt = null; else state.PermissionsAt = null;
        try
        {
            if (!Active(session, state)) return;
            token.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            var ids = form
                ? (await _source.Client.GetPendingFormsAsync(session, token).ConfigureAwait(false)).Select(f => f.Id!).ToHashSet(StringComparer.Ordinal)
                : (await _source.Client.GetPendingPermissionsAsync(session, token).ConfigureAwait(false)).Select(p => p.Id!).ToHashSet(StringComparer.Ordinal);
            if (!Active(session, state)) return;
            if (ids.Count > 256) { Emit(session, state, OpenCodeNotificationObservationKind.Gap, "pending-budget"); return; }
            token.ThrowIfCancellationRequested();
            // Pending endpoints return complete, validated owner-scoped arrays, not paginated message data.
            // Initiation under the monitor lock excludes untrack/disposal between final Active and retirement.
            Task reconcile;
            lock (_sync)
            {
                if (!Active(session, state)) return;
                reconcile = _sink.IsReady && Fresh(started) ? _sink.ReconcileAsync(_source, session,
                    form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission, ids) : Task.CompletedTask;
            }
            await reconcile.ConfigureAwait(false);
            if (!Active(session, state)) return;
            var old = form ? state.Forms : state.Permissions;
            if (form) state.Forms = ids; else state.Permissions = ids;
            if (form) state.FormsAt = started; else state.PermissionsAt = started;
            Emit(session, state, OpenCodeNotificationObservationKind.PendingSnapshot, form ? "forms" : "permissions");
            foreach (var id in _sink.IsReady && Fresh(started) ? ids.Except(old) : Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();
                if (!Active(session, state)) return;
                NotificationInteractionResult result;
                try { result = await _sink.NotifyAsync(_source, session, id, form, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch
                {
                    result = new(NotificationInteractionStatus.Failed, "无法准备通知，请在应用处理。",
                        _source.Identity, session, id, Kind: form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission);
                }
                Emit(session, state, OpenCodeNotificationObservationKind.Interaction, "preparation-result", interaction: result);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, form ? "forms-read-failed-snapshot-retained" : "permissions-read-failed-snapshot-retained"); }
    }

    private void Emit(string session, StreamState state, OpenCodeNotificationObservationKind kind, string code,
        NotificationInteractionResult? interaction = null, IReadOnlyList<NotificationDispatchResult>? dispatches = null)
    {
        if (!Active(session, state) || Observed is not { } handlers) return;
        var args = new OpenCodeNotificationObservationEventArgs(new(kind, _source.Identity, session, code,
            state.Permissions.ToArray(), state.Forms.ToArray(), interaction, dispatches));
        foreach (EventHandler<OpenCodeNotificationObservationEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, args); } catch { /* Never leak subscriber errors or answers. */ }
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _runCancellation?.Cancel(); _tracked.Clear(); }
        // Owner awaits StopAsync/DisposeAsync before disposing source.Client. No sync-over-async UI deadlock.
    }

    public async ValueTask DisposeAsync() { Dispose(); await StopAsync().ConfigureAwait(false); }
}
