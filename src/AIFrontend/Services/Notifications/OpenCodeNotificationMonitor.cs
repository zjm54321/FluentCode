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
    Task<NotificationInteractionResult> NotifyAsync(TrustedOpenCodeNotificationSource source, string session, string request, bool form, CancellationToken token);
}

/// <summary>App-owned, explicitly tracked sessions only. Does not own client/service, register Windows,
/// read settings, resolve uncertain mutations, or infer completion of an individual prompt.</summary>
public sealed class OpenCodeNotificationMonitor : IDisposable, IAsyncDisposable
{
    private sealed class Sink(CompletionNotificationService service) : IOpenCodeNotificationSink
    {
        public bool IsReady => service.Status == NotificationServiceStatus.Initialized;
        public IReadOnlyList<NotificationDispatchResult> Observe(string endpoint, string session, IReadOnlyList<OpenCodeMessage> messages, bool historical)
            => service.ObserveOpenCodeMessages(endpoint, session, messages, historical);
        public Task<NotificationInteractionResult> NotifyAsync(TrustedOpenCodeNotificationSource source, string session, string request, bool form, CancellationToken token)
            => form ? service.NotifyFormAsync(source, session, request, token) : service.NotifyPermissionAsync(source, session, request, token);
    }

    private sealed class StreamState
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

    public OpenCodeNotificationMonitor(CompletionNotificationService service, TrustedOpenCodeNotificationSource source,
        TimeSpan? pollInterval = null, int pageSize = 100, int pageBudget = 8)
        : this(new Sink(service ?? throw new ArgumentNullException(nameof(service))), source, pollInterval, pageSize, pageBudget) { }

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

    /// <summary>Returns false until notification initialization succeeds. No automatic initialization.</summary>
    public bool Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sink.IsReady) return false;
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
            lock (_sync) { if (_disposed || !_sink.IsReady) return; streams = _tracked.ToArray(); }
            foreach (var pair in streams)
            {
                token.ThrowIfCancellationRequested();
                if (!Active(pair.Key, pair.Value)) continue;
                await MessagesAsync(pair.Key, pair.Value, token).ConfigureAwait(false);
                await PendingAsync(pair.Key, pair.Value, false, token).ConfigureAwait(false);
                await PendingAsync(pair.Key, pair.Value, true, token).ConfigureAwait(false);
            }
        }
        finally { _poll.Release(); }
    }

    private bool Active(string session, StreamState state)
    {
        lock (_sync) return !_disposed && _sink.IsReady && _tracked.TryGetValue(session, out var current) && ReferenceEquals(state, current);
    }

    private async Task MessagesAsync(string session, StreamState state, CancellationToken token)
    {
        try
        {
            var page = await _source.Client.GetMessagesAsync(session,
                new() { Order = OpenCodeSessionOrders.Desc, Limit = _pageSize }, token).ConfigureAwait(false);
            if (!Active(session, state)) return;
            var initialMessages = page.Data ?? throw new InvalidOperationException("Missing message page.");
            var newest = initialMessages.FirstOrDefault()?.Id;
            if (initialMessages.Any(m => m is null || !NotificationArgumentCodec.IsValidRequestId(m.Id)))
            { Emit(session, state, OpenCodeNotificationObservationKind.Gap, "invalid-message-id"); return; }
            if (!state.Baseline)
            {
                _sink.Observe(_source.Identity, session, initialMessages, true);
                state.Anchor = newest;
                state.Baseline = true;
                Emit(session, state, OpenCodeNotificationObservationKind.Baseline, "initial-history-suppressed");
                return;
            }
            var collected = new List<OpenCodeMessage>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            for (int pages = 1; ; pages++)
            {
                bool found = false;
                foreach (var message in page.Data!)
                {
                    if (message is null || !NotificationArgumentCodec.IsValidRequestId(message.Id))
                    { Emit(session, state, OpenCodeNotificationObservationKind.Gap, "invalid-message-id"); return; }
                    if (message.Id == state.Anchor) { found = true; break; }
                    collected.Add(message);
                }
                string? cursor = page.Cursor?.Next;
                if (found || (state.Anchor is null && string.IsNullOrEmpty(cursor))) break;
                if (string.IsNullOrEmpty(cursor) || pages >= _pageBudget || !cursors.Add(cursor))
                {
                    Emit(session, state, OpenCodeNotificationObservationKind.Gap,
                        string.IsNullOrEmpty(cursor) ? "anchor-missing" : "page-budget-or-cursor-loop");
                    return; // Preserve anchor and do not label any collected history as new completion.
                }
                page = await _source.Client.GetMessagesAsync(session,
                    new() { Cursor = cursor, Limit = _pageSize }, token).ConfigureAwait(false); // Never cursor + order.
            }
            token.ThrowIfCancellationRequested();
            if (!Active(session, state)) return;
            collected.Reverse();
            var results = _sink.Observe(_source.Identity, session, collected, false);
            state.Anchor = newest;
            Emit(session, state, OpenCodeNotificationObservationKind.Messages, "anchored", dispatches: results);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "messages-read-failed-anchor-retained"); }
    }

    private async Task PendingAsync(string session, StreamState state, bool form, CancellationToken token)
    {
        try
        {
            var ids = form
                ? (await _source.Client.GetPendingFormsAsync(session, token).ConfigureAwait(false)).Select(f => f.Id!).ToHashSet(StringComparer.Ordinal)
                : (await _source.Client.GetPendingPermissionsAsync(session, token).ConfigureAwait(false)).Select(p => p.Id!).ToHashSet(StringComparer.Ordinal);
            if (!Active(session, state)) return;
            if (ids.Count > 256) { Emit(session, state, OpenCodeNotificationObservationKind.Gap, "pending-budget"); return; }
            var old = form ? state.Forms : state.Permissions;
            if (form) state.Forms = ids; else state.Permissions = ids;
            Emit(session, state, OpenCodeNotificationObservationKind.PendingSnapshot, form ? "forms" : "permissions");
            foreach (var id in ids.Except(old))
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
