using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

public sealed record OpenCodeSessionObservation(string EndpointIdentity, string SessionId,
    OpenCodeExecutionProjectionResult Projection, string? Outcome, double? IdleWatermark,
    double? ViewedWatermark, DateTimeOffset ObservedAt, bool IsNeutralIdle = false)
{
    // 工作态 UI 事实；其携带时间戳为该事实的原始独立证据时间。
    public OpenCodeSessionUiFact? WorkingFact { get; init; }

    // 待处理动作 UI 事实；其携带时间戳为该事实的原始独立证据时间。
    public OpenCodeSessionUiFact? PendingActionFact { get; init; }

    // 成功 ID 匹配时 Info 读取的开始时间。
    public DateTimeOffset? InfoObservedAt { get; init; }

    // 联合完成证明的最早时间。
    public DateTimeOffset? CompletionObservedAt { get; init; }
}
public sealed class OpenCodeSessionObservationEventArgs(OpenCodeSessionObservation observation) : EventArgs
{
    public OpenCodeSessionObservation Observation { get; } = observation;
}

public sealed partial class OpenCodeNotificationMonitor
{
    public static readonly TimeSpan ObservationLifetime = TimeSpan.FromSeconds(15);
    public event EventHandler<OpenCodeSessionObservationEventArgs>? SessionObserved;

    private sealed partial class StreamState
    {
        internal OpenCodeMessageHistoryResult? History;
        internal DateTimeOffset HistoryAt;
        internal DateTimeOffset? PermissionsAt, FormsAt;
        internal ImmutableArray<string> UnresolvedChildren;
    }

    private static bool Fresh(DateTimeOffset? at) => at is { } value && DateTimeOffset.UtcNow - value <= ObservationLifetime;

    private async Task<OpenCodeSessionInfo?> ReadSessionAsync(string session, CancellationToken token)
    {
        string? cursor = null;
        var cursors = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _pageBudget; i++)
        {
            var page = await _source.Client.ListSessionsAsync(new() { Limit = _pageSize, Cursor = cursor,
                Order = cursor is null ? OpenCodeSessionOrders.Desc : null }, token).ConfigureAwait(false);
            if (page.Data is null || page.Data.Count > _pageSize) return null;
            var found = page.Data.Where(s => s?.Id == session).ToArray();
            if (found.Length != 0) return found.Length == 1 ? found[0] : null;
            cursor = page.Cursor?.Next;
            if (page.Data.Count == 0 || !NotificationArgumentCodec.IsValidRequestId(cursor) || !cursors.Add(cursor!)) break;
        }
        return null;
    }

    private async Task ExecutionAsync(string session, StreamState state, CancellationToken token)
    {
        if (!Active(session, state)) return;
        OpenCodeSessionInfo? info = null;
        var infoAt = DateTimeOffset.UtcNow;
        try { info = await ReadSessionAsync(session, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "session-watermark-read-failed"); }

        bool inboxFresh = false, hasInbox = false;
        var inboxAt = DateTimeOffset.UtcNow;
        try
        {
            var inbox = await _source.Client.GetInboxAsync(session, token).ConfigureAwait(false);
            inboxFresh = inbox.Count <= 256 && inbox.All(i => i?.SessionId == session);
            hasInbox = inbox.Count != 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "inbox-read-failed"); }

        bool activeFresh = false, running = false;
        var activeAt = DateTimeOffset.UtcNow;
        try
        {
            var active = await _source.Client.GetActiveSessionsAsync(token).ConfigureAwait(false);
            if (active.Data is { } data)
            {
                activeFresh = !data.TryGetValue(session, out var drain) || drain?.Type == "running";
                running = drain?.Type == "running";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "active-read-failed"); }
        if (!Active(session, state)) return;

        bool protectedSession;
        using (var gate = await OpenCodeInteractionGate.Shared.EnterAsync(_source.Identity, session, token).ConfigureAwait(false))
            protectedSession = !gate.CanSwitchAgent();
        var window = OpenCodeExecutionProjection.CreateWindow(session, state.History?.Messages ?? [],
            state.History is not null && Fresh(state.HistoryAt), state.History?.Complete == true, true);
        var projection = OpenCodeExecutionProjection.Evaluate(new(session, window,
            activeFresh && Fresh(activeAt), running, Fresh(state.PermissionsAt) && Fresh(state.FormsAt),
            state.Permissions.Count != 0 || state.Forms.Count != 0, inboxFresh && Fresh(inboxAt), hasInbox,
            false, protectedSession, state.UnresolvedChildren));
        state.UnresolvedChildren = projection.KnownUnresolvedChildSessionIds;
        var idle = window.Messages.LastOrDefault(m => m.Type == "idle");
        double? watermark = Fresh(infoAt) && info?.Time?.Idle is { } value && double.IsFinite(value) ? value : null;
        double? viewed = Fresh(infoAt) && info?.Time?.Viewed is { } seen && double.IsFinite(seen) ? seen : null;
        // Outcome and watermark must come from the SAME server snapshot. An older idle cannot be
        // rebound to a newer turn's watermark; require exact message corroboration, or remain unknown.
        string? outcome = projection.State == OpenCodeExecutionState.Idle && !projection.Waiting
            && !protectedSession && !projection.KnownUnresolvedBackground && idle is not null
            && watermark == idle.Created && info?.Outcome == idle.Outcome ? info?.Outcome : null;
        bool neutral = projection.CanSwitchAgent && Fresh(infoAt) && info is not null
            && info.Outcome is null && idle is null;
        // Never renew evidence at publication. Working from active needs only active; Waiting needs
        // both complete pending reads; terminal/neutral absence depends on EVERY auxiliary snapshot.
        var evidenceAt = projection.WaitingKnown && projection.Waiting
            ? new[] { state.PermissionsAt!.Value, state.FormsAt!.Value }.Min()
            : projection.State == OpenCodeExecutionState.Working && activeFresh && running && Fresh(activeAt)
                ? activeAt
                : projection.State == OpenCodeExecutionState.Working ? state.HistoryAt
                : new[] { state.HistoryAt, infoAt, inboxAt, activeAt,
                    state.PermissionsAt ?? DateTimeOffset.MinValue, state.FormsAt ?? DateTimeOffset.MinValue }.Min();
        // UI facts keep their own original evidence times, independent of the legacy projection
        // and its protection override. No new proof means no update, not a renewed unknown.
        var rawProjection = projection;
        var uiNow = DateTimeOffset.UtcNow;
        OpenCodeSessionUiFact? workingFact = null;
        if (activeFresh && running && EarliestUiEvidence(uiNow, activeAt) is { } runningAt)
            workingFact = new(true, runningAt);
        else if (rawProjection.State == OpenCodeExecutionState.Working
            && EarliestUiEvidence(uiNow, state.HistoryAt) is { } historyAt
            // Disable active evidence: legacy Fresh accepts the exact TTL boundary, so raw
            // Working alone cannot establish that history actually proves execution.
            && OpenCodeExecutionProjection.Evaluate(new(session, window, false, false,
                rawProjection.WaitingKnown, rawProjection.Waiting, inboxFresh, hasInbox,
                false, protectedSession, state.UnresolvedChildren)).State == OpenCodeExecutionState.Working)
            workingFact = new(true, historyAt);
        else if (rawProjection.State == OpenCodeExecutionState.Idle
            && EarliestUiEvidence(uiNow, state.HistoryAt, activeAt, inboxAt,
                state.PermissionsAt, state.FormsAt) is { } idleAt)
            workingFact = new(false, idleAt);

        OpenCodeSessionUiFact? pendingActionFact = null;
        if (rawProjection.WaitingKnown
            && EarliestUiEvidence(uiNow, state.PermissionsAt, state.FormsAt) is { } pendingAt)
            pendingActionFact = new(rawProjection.Waiting, pendingAt);

        DateTimeOffset? infoObservedAt = info?.Id == session ? infoAt : null;
        DateTimeOffset? completionObservedAt = outcome is not null
            && watermark is { } completionWatermark && double.IsFinite(completionWatermark) && completionWatermark >= 0
                ? EarliestUiEvidence(uiNow, state.HistoryAt, infoAt, inboxAt, activeAt,
                    state.PermissionsAt, state.FormsAt)
                : null;

        if (protectedSession) projection = projection with { State = OpenCodeExecutionState.Unknown };
        PublishSession(session, state, new(_source.Identity, session, projection, outcome, watermark, viewed, evidenceAt, neutral)
        {
            WorkingFact = workingFact,
            PendingActionFact = pendingActionFact,
            InfoObservedAt = infoObservedAt,
            CompletionObservedAt = completionObservedAt
        });
        if (_sink.IsReady && viewed is { } covered && state.History is { } history && Fresh(state.HistoryAt))
            await _sink.ReconcileViewAsync(_source, session, covered, history.Messages).ConfigureAwait(false);
    }

    private static DateTimeOffset? EarliestUiEvidence(DateTimeOffset now, params DateTimeOffset?[] times)
    {
        if (times is null || times.Length == 0)
        {
            return null;
        }

        DateTimeOffset? earliest = null;
        foreach (DateTimeOffset? candidate in times)
        {
            // 任意一项缺失即视为证据不完整，整体作废。
            if (candidate is not DateTimeOffset at)
            {
                return null;
            }

            TimeSpan age = now - at;

            // 未来时间(age < 0)或已达到/超过 ObservationLifetime，整体作废。
            if (age < TimeSpan.Zero || age >= ObservationLifetime)
            {
                return null;
            }

            // 全部项均合法时，保留最早的原始时间点。
            if (earliest is null || at < earliest.Value)
            {
                earliest = at;
            }
        }

        return earliest;
    }

    private void PublishUnknown(string session, StreamState state) => PublishSession(session, state,
        new(_source.Identity, session, new(OpenCodeExecutionState.Unknown, false, false, false, false, []),
            null, null, null, DateTimeOffset.UtcNow));

    private void PublishSession(string session, StreamState state, OpenCodeSessionObservation observation)
    {
        if (!Active(session, state) || SessionObserved is not { } handlers) return;
        foreach (EventHandler<OpenCodeSessionObservationEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, new(observation)); } catch { }
    }

    /// <summary>Explicit opened-and-visible session only. Reads the server watermark anew; never DateTime.Now.
    /// The caller's source/selection fence guards POST initiation. No retry of an uncertain receipt.</summary>
    internal async Task<double?> AcknowledgeSessionAsync(string session, double coveredIdle, Func<Action, bool> fence, CancellationToken token)
    {
        if (!double.IsFinite(coveredIdle)) return null;
        await _poll.WaitAsync(token).ConfigureAwait(false);
        try
        {
            StreamState? state;
            lock (_sync) _tracked.TryGetValue(session, out state);
            if (state is null || !Active(session, state)) return null;
            var at = DateTimeOffset.UtcNow;
            var info = await ReadSessionAsync(session, token).ConfigureAwait(false);
            if (!Active(session, state) || !Fresh(at) || info?.Time?.Idle is not { } idle
                || !double.IsFinite(idle) || coveredIdle > idle) return null;
            Task? post = null;
            if (!fence(() => post = _source.Client.MarkSessionViewedAsync(session, coveredIdle, token))) return null;
            await post!.ConfigureAwait(false);
            if (!Active(session, state) || !fence(() => { })) return null;
            if (_sink.IsReady && state.History is { } history && Fresh(state.HistoryAt))
                await _sink.ReconcileViewAsync(_source, session, coveredIdle, history.Messages).ConfigureAwait(false);
            return coveredIdle;
        }
        finally { _poll.Release(); }
    }
}
