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
    void Retire(string session) { }
    Task ReconcileSettledAsync(TrustedOpenCodeNotificationSource source, string session, NotificationNavigationKind kind, string id)
        => Task.CompletedTask;
    IReadOnlyList<NotificationDispatchResult> ObserveTerminal(string endpoint, string session, OpenCodeMessage terminal,
        IReadOnlyList<OpenCodeMessage> context, string? title, string? error)
        => terminal.Outcome == "succeeded" ? ObserveContext(endpoint, session, new[] { terminal }, context, title) : Array.Empty<NotificationDispatchResult>();
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
        public void Retire(string session) => service?.RetireOpenCodeObservation(source, session);
        public Task ReconcileSettledAsync(TrustedOpenCodeNotificationSource source, string session, NotificationNavigationKind kind, string id)
            => IsReady ? service!.ReconcileSettledAsync(source, session, kind, id) : Task.CompletedTask;
        public IReadOnlyList<NotificationDispatchResult> ObserveTerminal(string endpoint, string session, OpenCodeMessage terminal,
            IReadOnlyList<OpenCodeMessage> context, string? title, string? error)
            => IsReady ? service!.ObserveTrustedOpenCodeTerminal(source, session, terminal, context, title, error) : [];
    }

    private sealed partial class StreamState
    {
        internal bool Baseline;
        internal string? Anchor;
        internal bool? IsChild; // parentID is immutable; retain known ancestry across metadata failures.
        internal DateTimeOffset NextPollAt;
        internal bool Idle;
        internal HashSet<string> Permissions = new(StringComparer.Ordinal);
        internal HashSet<string> Forms = new(StringComparer.Ordinal);
        internal HashSet<string> PreparedPermissions = new(StringComparer.Ordinal);
        internal HashSet<string> PreparedForms = new(StringComparer.Ordinal);
        internal Dictionary<string, int> PermissionAttempts = new(StringComparer.Ordinal);
        internal Dictionary<string, int> FormAttempts = new(StringComparer.Ordinal);
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _poll = new(1, 1);
    private readonly SemaphoreSlim _ack = new(1, 1);
    private readonly Dictionary<string, StreamState> _tracked = new(StringComparer.Ordinal);
    private string? _prioritySession;
    private readonly TrustedOpenCodeNotificationSource _source;
    private readonly IOpenCodeNotificationSink _sink;
    private readonly TimeSpan _interval;
    private readonly int _pageSize;
    private readonly int _pageBudget;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _runCancellation;
    private Task? _run;
    private bool _disposed;

    public OpenCodeNotificationMonitor(CompletionNotificationService? service, TrustedOpenCodeNotificationSource source,
        TimeSpan? pollInterval = null, int pageSize = 100, int pageBudget = 8)
        : this(new Sink(service, source ?? throw new ArgumentNullException(nameof(source))), source, pollInterval, pageSize, pageBudget) { }

    internal OpenCodeNotificationMonitor(IOpenCodeNotificationSink sink, TrustedOpenCodeNotificationSource source,
        TimeSpan? pollInterval = null, int pageSize = 100, int pageBudget = 8, TimeProvider? timeProvider = null)
    {
        _sink = sink;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _interval = pollInterval ?? TimeSpan.FromSeconds(5);
        if (_interval < TimeSpan.FromMilliseconds(100) || pageSize is < 1 or > 500 || pageBudget is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _pageSize = pageSize;
        _pageBudget = pageBudget;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<OpenCodeNotificationObservationEventArgs>? Observed;

    public bool TrackSession(string sessionId)
    {
        if (!NotificationArgumentCodec.IsValidRequestId(sessionId) || !sessionId.StartsWith("ses", StringComparison.Ordinal))
            throw new ArgumentException("Invalid session identity.", nameof(sessionId));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _prioritySession = sessionId;
            if (_tracked.TryGetValue(sessionId, out var tracked))
            {
                tracked.NextPollAt = default;
                QueueObservationLocked(sessionId, tracked, immediate: true);
                return false;
            }
            if (_tracked.Count >= 128) throw new InvalidOperationException("Tracked session capacity reached.");
            _tracked.Add(sessionId, tracked = new());
            QueueObservationLocked(sessionId, tracked, immediate: true);
            return true;
        }
    }

    public bool UntrackSession(string sessionId)
    {
        lock (_sync)
        {
            if (!_tracked.Remove(sessionId, out var state)) return false;
            state.EventLifetime.Cancel();
            _observationRun?.Bootstrap.Remove(sessionId);
            _sink.Retire(sessionId);
            return true;
        }
    }

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
            var owner = _observationRun = new(token);
            _run = Task.Run(() => RunObservationsAsync(owner, token));
            return true;
        }
    }

    public async Task StopAsync()
    {
        Task? run;
        lock (_sync) { _runCancellation?.Cancel(); run = _run; }
        if (run is not null) await run.ConfigureAwait(false);
        lock (_sync)
            if (ReferenceEquals(run, _run))
            {
                _runCancellation?.Dispose();
                _runCancellation = null;
                _observationRun = null;
            }
        await _poll.WaitAsync().ConfigureAwait(false);
        _poll.Release();
        await _ack.WaitAsync().ConfigureAwait(false);
        _ack.Release();
    }

    internal Task PollOnceAsync(CancellationToken token = default, bool scheduled = false)
        => ObserveSessionsAsync(null, token, scheduled);

    private async Task ObserveSessionsAsync(KeyValuePair<string, StreamState>[]? targets, CancellationToken token, bool scheduled = false)
    {
        await _poll.WaitAsync(token).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, StreamState>[] streams;
            string? priority;
            lock (_sync) { if (_disposed) return; streams = targets ?? _tracked.ToArray(); priority = _prioritySession; }
            if (streams.Length == 0) return;
            IReadOnlyDictionary<string, OpenCodeActiveSessionState>? active = null;
            var activeAt = DateTimeOffset.UtcNow;
            try { active = (await _source.Client.GetActiveSessionsAsync(token).ConfigureAwait(false)).Data; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsObservationFailure(error)) { }
            var ordered = streams.OrderByDescending(p => p.Key == priority)
                .ThenByDescending(p => active?.TryGetValue(p.Key, out var value) == true && value?.Type == "running").ToArray();
            foreach (var batch in ordered.Chunk(4))
                await Task.WhenAll(batch.Select(ObserveStreamAsync)).ConfigureAwait(false);

            async Task ObserveStreamAsync(KeyValuePair<string, StreamState> pair)
            {
                token.ThrowIfCancellationRequested();
                if (!Active(pair.Key, pair.Value)) return;
                var running = active?.TryGetValue(pair.Key, out var activity) == true && activity?.Type == "running";
                // Only disconnected fallback honors idle backoff. Event/track/reconnect/safety
                // observations bypass it; a healthy stream has no 5/15-second HTTP polling.
                if (scheduled && active is not null && !running && pair.Value.Idle
                    && _timeProvider.GetUtcNow() < pair.Value.NextPollAt) return;
                // Retain still-valid evidence during refresh. App expires it using its original evidence time.
                await MessagesAsync(pair.Key, pair.Value, token).ConfigureAwait(false);
                await PendingAsync(pair.Key, pair.Value, false, token).ConfigureAwait(false);
                await PendingAsync(pair.Key, pair.Value, true, token).ConfigureAwait(false);
                try { await ExecutionAsync(pair.Key, pair.Value, active, activeAt, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (IsObservationFailure(error)) { PublishUnknown(pair.Key, pair.Value); }
                pair.Value.NextPollAt = _timeProvider.GetUtcNow() + (pair.Value.Idle && !running
                    ? TimeSpan.FromTicks(Math.Max(_interval.Ticks, TimeSpan.FromSeconds(15).Ticks)) : _interval);
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
        var retained = state.History;
        state.History = null;
        try
        {
            var started = DateTimeOffset.UtcNow;
            var history = await OpenCodeMessageHistory.ReadAsync((query, ct) => _source.Client.GetMessagesAsync(session, query, ct),
                _pageSize, _pageBudget, token, state.Anchor ?? retained?.Messages.LastOrDefault()?.Id).ConfigureAwait(false);
            if (!Active(session, state)) return false;
            if (history.AnchorFound && !history.Complete && retained is not null)
            {
                // Fresh overlapping rows replace retained revisions. Keep only the proven older
                // prefix and the fresh suffix, bounded by the original history budget.
                var freshIds = history.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                var overlap = retained.Messages.FindIndex(m => freshIds.Contains(m.Id));
                if (overlap >= 0)
                {
                    var merged = retained.Messages.Take(overlap).Concat(history.Messages).ToList();
                    var overflow = Math.Max(0, merged.Count - _pageSize * _pageBudget);
                    history = new(merged.Skip(overflow).ToList(), retained.Complete && overflow == 0, true);
                }
            }
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
                _sink.Observe(_source.Identity, session, messages, true);
                state.Anchor = newest;
                Emit(session, state, OpenCodeNotificationObservationKind.Baseline, "gap-recovery-suppressed");
                return false; // Unknown gap is suppressed; later anchored completions can notify.
            }
            token.ThrowIfCancellationRequested();
            if (!Active(session, state)) return false;
            var collected = messages.Skip(anchor + 1).ToArray();
            string? title = null;
            if (state.IsChild != true && collected.Any(m => m.Type == "idle" && m.Outcome == "succeeded"))
            {
                try
                {
                    var info = await _source.Client.GetSessionInfoAsync(session, token).ConfigureAwait(false);
                    state.IsChild ??= info.ParentId is not null;
                    title = info.Title;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (IsObservationFailure(error)) { /* Unknown ancestry must not suppress a completion. */ }
            }
            if (!Active(session, state)) return false;
            // Consume child completions as history: mark them seen and advance the anchor without
            // creating a toast or a display/view binding. Pending permission/form delivery is separate.
            var results = state.IsChild == true
                ? _sink.Observe(_source.Identity, session, collected, true)
                : _sink.ObserveContext(_source.Identity, session, collected, messages, title);
            if (!results.Any(r => r.Status is NotificationDispatchStatus.Failed or NotificationDispatchStatus.Unavailable))
                state.Anchor = newest;
            Emit(session, state, OpenCodeNotificationObservationKind.Messages, "anchored", dispatches: results);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsObservationFailure(error)) { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "messages-read-failed-anchor-retained"); return false; }
    }

    private async Task PendingAsync(string session, StreamState state, bool form, CancellationToken token)
    {
        if (form) state.FormsAt = null; else state.PermissionsAt = null;
        try
        {
            if (!Active(session, state)) return;
            token.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            bool questionPending = false;
            HashSet<string> ids;
            if (form)
            {
                var forms = await _source.Client.GetPendingFormsAsync(session, token).ConfigureAwait(false);
                if (!Active(session, state)) return;
                if (forms.Count > 256) { Emit(session, state, OpenCodeNotificationObservationKind.Gap, "pending-budget"); return; }
                ids = forms.Select(f => f.Id!).ToHashSet(StringComparer.Ordinal);
                questionPending = forms.Any(f => f.Metadata is { ValueKind: System.Text.Json.JsonValueKind.Object } metadata
                    && metadata.TryGetProperty("kind", out var kind) && kind.ValueKind == System.Text.Json.JsonValueKind.String
                    && kind.GetString() == "question");
            }
            else ids = (await _source.Client.GetPendingPermissionsAsync(session, token).ConfigureAwait(false))
                .Select(p => p.Id!).ToHashSet(StringComparer.Ordinal);
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
            token.ThrowIfCancellationRequested();
            if (Fresh(started))
            {
                using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(_source.Identity, session, token).ConfigureAwait(false);
                lock (_sync)
                    if (Active(session, state) && Fresh(started))
                        lease.RetireSettledRequests(form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission, ids);
            }
            if (form)
            {
                state.Forms = ids;
                state.QuestionPending = questionPending;
                state.FormsAt = started;
            }
            else { state.Permissions = ids; state.PermissionsAt = started; }
            var prepared = form ? state.PreparedForms : state.PreparedPermissions;
            prepared.RemoveWhere(id => !ids.Contains(id));
            var attempts = form ? state.FormAttempts : state.PermissionAttempts;
            foreach (var id in attempts.Keys.Where(id => !ids.Contains(id)).ToArray()) attempts.Remove(id);
            Emit(session, state, OpenCodeNotificationObservationKind.PendingSnapshot, form ? "forms" : "permissions");
            foreach (var id in _sink.IsReady && Fresh(started) ? ids.Except(prepared) : Enumerable.Empty<string>())
            {
                token.ThrowIfCancellationRequested();
                if (!Active(session, state)) return;
                if (attempts.GetValueOrDefault(id) >= 3) continue;
                attempts[id] = attempts.GetValueOrDefault(id) + 1;
                NotificationInteractionResult result;
                try { result = await _sink.NotifyAsync(_source, session, id, form, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (IsObservationFailure(error))
                {
                    result = new(NotificationInteractionStatus.Failed, "无法准备通知，请在应用处理。",
                        _source.Identity, session, id, Kind: form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission);
                }
                Emit(session, state, OpenCodeNotificationObservationKind.Interaction, "preparation-result", interaction: result);
                if (result.Status is NotificationInteractionStatus.Submitted or NotificationInteractionStatus.NeedsApp or NotificationInteractionStatus.Suppressed)
                    prepared.Add(id);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsObservationFailure(error)) { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, form ? "forms-read-failed-snapshot-retained" : "permissions-read-failed-snapshot-retained"); }
    }

    private void Emit(string session, StreamState state, OpenCodeNotificationObservationKind kind, string code,
        NotificationInteractionResult? interaction = null, IReadOnlyList<NotificationDispatchResult>? dispatches = null)
    {
        if (!Active(session, state) || Observed is not { } handlers) return;
        var args = new OpenCodeNotificationObservationEventArgs(new(kind, _source.Identity, session, code,
            state.Permissions.ToArray(), state.Forms.ToArray(), interaction, dispatches));
        foreach (EventHandler<OpenCodeNotificationObservationEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, args); } catch (Exception) { /* Subscriber containment; no answers in diagnostics. */ }
    }

    private static bool IsObservationFailure(Exception error) => error is OpenCodeException
        or System.Net.Http.HttpRequestException or OperationCanceledException or InvalidOperationException
        or ArgumentException or System.IO.IOException or System.Text.Json.JsonException;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _runCancellation?.Cancel();
            foreach (var pair in _tracked) { pair.Value.EventLifetime.Cancel(); _sink.Retire(pair.Key); }
            _tracked.Clear();
        }
        // Owner awaits StopAsync/DisposeAsync before disposing source.Client. No sync-over-async UI deadlock.
    }

    public async ValueTask DisposeAsync() { Dispose(); await StopAsync().ConfigureAwait(false); }
}
