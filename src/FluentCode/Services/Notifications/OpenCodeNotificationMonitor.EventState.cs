using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.Notifications;

public sealed partial class OpenCodeNotificationMonitor
{
    private sealed partial class StreamState
    {
        internal long EventEpoch, EventRevision;
        internal bool? EventWorking;
        internal bool EventPermissionsKnown, EventFormsKnown, EventTerminal, EventNotificationBaseline;
        internal string? StartedEvent, TerminalEvent, EventOutcome, EventTitle;
        internal double? EventWatermark;
        internal double? EventViewedWatermark;
        internal DateTimeOffset? EventInfoAt;
        internal DateTimeOffset EventAt;
        internal Func<bool>? EventLease;
        internal readonly CancellationTokenSource EventLifetime = new();
        internal readonly HashSet<string> QuestionForms = new(StringComparer.Ordinal);
        internal readonly HashSet<string> LivePermissions = new(StringComparer.Ordinal), LiveForms = new(StringComparer.Ordinal);
    }

    private sealed record EventEffect(string Session, StreamState State, OpenCodeEvent Event, string? Request = null, bool Form = false, bool Settled = false);

    private async Task<List<KeyValuePair<string, StreamState>>> BootstrapEventsAsync(ObservationRun owner, long epoch,
        KeyValuePair<string, StreamState>[] sessions, CancellationToken token)
    {
        var touched = new List<KeyValuePair<string, StreamState>>();
        var fresh = sessions.Where(p => p.Value.EventEpoch != epoch).ToArray();
        var observedAt = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, OpenCodeActiveSessionState>? active = null;
        if (fresh.Length > 0)
        {
            try { active = (await _source.Client.GetActiveSessionsAsync(token).ConfigureAwait(false)).Data; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsObservationFailure(error)) { }
        }
        foreach (var pair in sessions)
        {
            var (session, state) = pair;
            if (!EventStateCurrent(owner, epoch, session, state)) continue;
            if (state.EventEpoch == epoch) { touched.Add(pair); continue; }
            IReadOnlyList<OpenCodePermissionRequest>? permissions = null;
            IReadOnlyList<OpenCodeFormInfo>? forms = null;
            try { permissions = await _source.Client.GetPendingPermissionsAsync(session, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsObservationFailure(error)) { }
            try { forms = await _source.Client.GetPendingFormsAsync(session, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsObservationFailure(error)) { }
            OpenCodeSessionInfo? info = null;
            try { info = await _source.Client.GetSessionInfoAsync(session, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsObservationFailure(error)) { }
            if (!EventStateCurrent(owner, epoch, session, state)) continue;
            if (info is not null) { state.IsChild ??= info.ParentId is not null; state.EventTitle = info.Title; }
            state.EventWorking = active is null ? null : !active.TryGetValue(session, out var status) ? false : status?.Type == "running" ? true : null;
            state.EventPermissionsKnown = permissions is { Count: <= 256 };
            state.EventFormsKnown = forms is { Count: <= 256 };
            state.Permissions.Clear(); state.Forms.Clear(); state.QuestionForms.Clear();
            if (state.EventPermissionsKnown) state.Permissions.UnionWith(permissions!.Select(p => p.Id!));
            if (state.EventFormsKnown)
                foreach (var form in forms!)
                {
                    state.Forms.Add(form.Id!);
                    if (IsQuestion(form.Metadata)) state.QuestionForms.Add(form.Id!);
                }
            state.LivePermissions.IntersectWith(state.Permissions); state.LiveForms.IntersectWith(state.Forms);
            state.PreparedPermissions.Clear(); state.PreparedForms.Clear();
            state.PreparedPermissions.UnionWith(state.Permissions); state.PreparedForms.UnionWith(state.Forms);
            state.EventTerminal = false; state.StartedEvent = null; state.EventOutcome = null; state.EventWatermark = null;
            // Reconnect is a baseline, never a new completion. Preserve the server's durable
            // viewed watermark so an already-read result cannot become unread on rebind.
            state.EventViewedWatermark = info?.Time?.Viewed is { } viewed && double.IsFinite(viewed) && viewed >= 0 ? viewed : null;
            state.EventInfoAt = info?.Id == session ? observedAt : null;
            state.EventEpoch = epoch; state.EventAt = observedAt;
            lock (_sync) { state.EventRevision++; state.EventLease = null; }
            EnsureEventNotificationBaseline(session, state);
            touched.Add(pair);
            // Snapshot items never call Notify. Events buffered while these GETs were in flight
            // are reduced over this snapshot BEFORE publishing or reconciling pending displays.
        }
        return touched;
    }

    private void EnsureEventNotificationBaseline(string session, StreamState state)
    {
        if (state.EventNotificationBaseline || !_sink.IsReady) return;
        _sink.Observe(_source.Identity, session, [], true);
        state.EventNotificationBaseline = true;
    }

    private async Task ReduceEventsAsync(ObservationRun owner, long epoch, CancellationToken token,
        List<KeyValuePair<string, StreamState>> touched)
    {
        var effects = new List<EventEffect>();
        (long Epoch, string Session, StreamState State, OpenCodeEvent Event)[] events;
        lock (_sync) { events = owner.Events.ToArray(); owner.Events.Clear(); }
        foreach (var queued in events)
        {
            var (eventEpoch, session, state, item) = queued;
            if (eventEpoch != epoch || !EventStateCurrent(owner, epoch, session, state)) continue;
            if (state.EventEpoch != epoch)
            {
                lock (_sync) { owner.Events.Enqueue(queued); owner.Wake.Writer.TryWrite(0); }
                continue; // Newly tracked during another session's hydration: hydrate it first.
            }
            if (!ApplyNotificationEvent(session, state, item, effects)) continue;
            state.EventAt = item.ReceivedAt;
            lock (_sync) { state.EventRevision++; state.EventLease = null; }
            touched.Add(new(session, state));
        }
        foreach (var pair in touched.DistinctBy(p => p.Key))
        {
            var (session, state) = pair;
            if (!EventStateCurrent(owner, epoch, session, state)) continue;
            PublishEventState(owner, epoch, session, state);
            if (_sink.IsReady)
            {
                if (state.EventPermissionsKnown) await _sink.ReconcileAsync(_source, session, NotificationNavigationKind.Permission, state.Permissions).ConfigureAwait(false);
                if (state.EventFormsKnown) await _sink.ReconcileAsync(_source, session, NotificationNavigationKind.Form, state.Forms).ConfigureAwait(false);
            }
        }
        foreach (var effect in effects)
        {
            if (!EventStateCurrent(owner, epoch, effect.Session, effect.State)) continue;
            token.ThrowIfCancellationRequested();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, effect.State.EventLifetime.Token);
            try
            {
                if (effect.Request is { } request)
                {
                    if (effect.Settled)
                        await _sink.ReconcileSettledAsync(_source, effect.Session,
                            effect.Form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission, request).ConfigureAwait(false);
                    else if (_sink.IsReady && (effect.Form ? effect.State.Forms : effect.State.Permissions).Contains(request))
                    {
                        var result = await _sink.NotifyAsync(_source, effect.Session, request, effect.Form, lifetime.Token).ConfigureAwait(false);
                        Emit(effect.Session, effect.State, OpenCodeNotificationObservationKind.Interaction, "event-preparation", interaction: result);
                    }
                }
                else await NotifyExecutionEventAsync(owner, epoch, effect, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (effect.State.EventLifetime.IsCancellationRequested) { }
        }
    }

    private static bool ApplyNotificationEvent(string session, StreamState state, OpenCodeEvent item, List<EventEffect> effects)
    {
        if (item.Type == "session.renamed") { state.EventTitle = Text(item.Data, "title") ?? state.EventTitle; return false; }
        if (item.Type == "session.execution.started")
        {
            if (item.Id is not null && state.StartedEvent == item.Id) return false;
            state.StartedEvent = item.Id; state.EventTerminal = false; state.EventWorking = true;
            state.EventOutcome = null; state.EventWatermark = null;
            return true;
        }
        if (item.Type is "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted")
        {
            if (state.EventTerminal || item.Id is not null && state.TerminalEvent == item.Id) return false;
            // Some servers announce idle sessions on startup/recovery. An idle baseline plus
            // an orphan terminal event is not a newly completed execution.
            if (state.IsChild != true && state.StartedEvent is null && state.EventWorking != true) return false;
            state.EventTerminal = true; state.TerminalEvent = item.Id; state.EventWorking = false;
            var terminal = TerminalMessage(item);
            state.EventOutcome = terminal?.Outcome; state.EventWatermark = terminal?.Time?.Created;
            if (item.Type != "session.execution.interrupted") effects.Add(new(session, state, item));
            return true;
        }
        bool form = item.Type.StartsWith("form.", StringComparison.Ordinal);
        bool settled = item.Type is "permission.replied" or "form.replied" or "form.cancelled";
        var payload = item.Type == "form.created" && item.Data.TryGetProperty("form", out var value) ? value : item.Data;
        var id = Text(payload, item.Type == "permission.replied" ? "requestID" : "id");
        if (!NotificationArgumentCodec.IsValidRequestId(id)) return false;
        var pending = form ? state.Forms : state.Permissions;
        var seen = form ? state.LiveForms : state.LivePermissions;
        if (settled)
        {
            pending.Remove(id!); seen.Remove(id!); state.QuestionForms.Remove(id!);
            (form ? state.PreparedForms : state.PreparedPermissions).Remove(id!);
            effects.Add(new(session, state, item, id, form, true));
            return true;
        }
        if (pending.Count >= 256 && !pending.Contains(id!))
        { if (form) state.EventFormsKnown = false; else state.EventPermissionsKnown = false; return true; }
        var changed = pending.Add(id!);
        if (form && payload.TryGetProperty("metadata", out var metadata) && IsQuestion(metadata)) changed |= state.QuestionForms.Add(id!);
        var first = seen.Add(id!);
        if (first) effects.Add(new(session, state, item, id, form));
        return changed || first;
    }

    private static bool IsQuestion(JsonElement? metadata) => metadata is { } value && Text(value, "kind") == "question";

    private static OpenCodeMessage? TerminalMessage(OpenCodeEvent item)
    {
        if (item.Type == "session.execution.interrupted" && Text(item.Data, "reason") == "shutdown") return null;
        if (item.Id is not { } id || !id.StartsWith("evt_", StringComparison.Ordinal) || !NotificationArgumentCodec.IsValidRequestId(id)
            || item.Created is not { } at || !double.IsFinite(at) || at < 0) return null;
        return new() { Id = "msg_" + id[4..], Type = "idle", Time = new() { Created = at },
            Outcome = item.Type == "session.execution.succeeded" ? "succeeded" : item.Type == "session.execution.failed" ? "failed" : "interrupted" };
    }

    private async Task NotifyExecutionEventAsync(ObservationRun owner, long epoch, EventEffect effect, CancellationToken token)
    {
        if (TerminalMessage(effect.Event) is not { } terminal) return;
        var (session, state) = (effect.Session, effect.State);
        var title = state.EventTitle;
        if (state.IsChild == true) return;
        if (state.IsChild == true || !_sink.IsReady || !EventStateCurrent(owner, epoch, session, state)) return;
        var context = new List<OpenCodeMessage>();
        if (terminal.Outcome == "succeeded")
        {
            try
            {
                var page = await _source.Client.GetMessagesAsync(session, new() { Order = "desc", Limit = Math.Min(_pageSize, 20) }, token).ConfigureAwait(false);
                if (page.Data is { } messages)
                {
                    var index = messages.ToList().FindIndex(m => m.Id == terminal.Id);
                    var before = index >= 0 ? messages.Skip(index)
                        : messages.Where(m => m.Time is not null && m.Time.Created < terminal.Time!.Created);
                    context.AddRange(before.Reverse()); // Never borrow text from a later execution.
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (IsObservationFailure(error)) { }
        }
        if (!context.Any(m => m.Id == terminal.Id)) context.Add(terminal);
        if (!EventStateCurrent(owner, epoch, session, state)) return;
        EnsureEventNotificationBaseline(session, state);
        var errorText = effect.Event.Data.TryGetProperty("error", out var errorValue) ? Text(errorValue, "message") : null;
        var results = _sink.ObserveTerminal(_source.Identity, session, terminal, context, title, errorText);
        if (!results.Any(r => r.Status is NotificationDispatchStatus.Failed or NotificationDispatchStatus.Unavailable)) state.Anchor = terminal.Id;
        Emit(session, state, OpenCodeNotificationObservationKind.Messages, "execution-terminal", dispatches: results);
    }

    private void PublishEventState(ObservationRun owner, long epoch, string session, StreamState state)
    {
        var revision = state.EventRevision;
        var current = state.EventLease ??= () => EventStateCurrent(owner, epoch, session, state, revision);
        bool pending = state.Permissions.Count != 0 || state.Forms.Count != 0;
        bool pendingKnown = pending || state.EventPermissionsKnown && state.EventFormsKnown;
        var at = state.EventAt;
        OpenCodeSessionUiFact Fact(bool value) => new(value, at) { CurrentEventStream = current };
        PublishSession(session, state, new(_source.Identity, session,
            new(state.EventWorking == true ? OpenCodeExecutionState.Working : state.EventWorking == false ? OpenCodeExecutionState.Idle : OpenCodeExecutionState.Unknown,
                pending, pendingKnown, !state.UnresolvedChildren.IsDefaultOrEmpty, false,
                state.UnresolvedChildren.IsDefault ? ImmutableArray<string>.Empty : state.UnresolvedChildren),
            state.EventOutcome, state.EventWatermark, state.EventViewedWatermark, at, state.EventWorking == false && !pending && state.EventOutcome is null)
        {
            WorkingFact = state.EventWorking is { } working ? Fact(working) : null,
            PendingActionFact = pendingKnown ? Fact(pending) : null,
            QuestionPendingFact = state.QuestionForms.Count > 0 || state.EventFormsKnown ? Fact(state.QuestionForms.Count > 0) : null,
            CompletionObservedAt = state.EventOutcome is not null ? at : null,
            InfoObservedAt = state.EventInfoAt,
            EventStateCurrent = current,
        });
    }
}
