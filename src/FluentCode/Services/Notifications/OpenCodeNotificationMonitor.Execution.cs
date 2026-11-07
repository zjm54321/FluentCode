using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.Notifications;

public sealed record OpenCodeSessionObservation(string EndpointIdentity, string SessionId,
    OpenCodeExecutionProjectionResult Projection, string? Outcome, double? IdleWatermark,
    double? ViewedWatermark, DateTimeOffset ObservedAt, bool IsNeutralIdle = false)
{
    // 工作态 UI 事实；其携带时间戳为该事实的原始独立证据时间。
    public OpenCodeSessionUiFact? WorkingFact { get; init; }

    // 待处理动作 UI 事实；其携带时间戳为该事实的原始独立证据时间。
    public OpenCodeSessionUiFact? PendingActionFact { get; init; }
    public OpenCodeSessionUiFact? QuestionPendingFact { get; init; }

    // 成功 ID 匹配时 Info 读取的开始时间。
    public DateTimeOffset? InfoObservedAt { get; init; }

    // 联合完成证明的最早时间。
    public DateTimeOffset? CompletionObservedAt { get; init; }
    public double? CompletionAbsentThrough { get; init; }
    internal Func<bool>? EventStateCurrent { get; init; }
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
        internal bool QuestionPending;
        internal ImmutableArray<string> UnresolvedChildren;
    }

    private static bool Fresh(DateTimeOffset? at) => at is { } value && DateTimeOffset.UtcNow - value <= ObservationLifetime;

    private Task<OpenCodeSessionInfo> ReadSessionAsync(string session, CancellationToken token)
        => _source.Client.GetSessionInfoAsync(session, token);

    private async Task ExecutionAsync(string session, StreamState state,
        IReadOnlyDictionary<string, OpenCodeActiveSessionState>? activeSessions, DateTimeOffset activeAt, CancellationToken token)
    {
        if (!Active(session, state)) return;
        OpenCodeSessionInfo? info = null;
        var infoAt = DateTimeOffset.UtcNow;
        try
        {
            info = await ReadSessionAsync(session, token).ConfigureAwait(false);
            state.IsChild ??= info.ParentId is not null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsObservationFailure(error)) { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "session-watermark-read-failed"); }

        bool inboxFresh = false, hasInbox = false;
        var inboxAt = DateTimeOffset.UtcNow;
        try
        {
            var inbox = await _source.Client.GetInboxAsync(session, token).ConfigureAwait(false);
            inboxFresh = inbox.Count <= 256 && inbox.All(i => i?.SessionId == session);
            hasInbox = inbox.Count != 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (IsObservationFailure(error)) { Emit(session, state, OpenCodeNotificationObservationKind.ReadFailed, "inbox-read-failed"); }

        bool activeFresh = false, running = false;
        if (activeSessions is not null)
        {
            activeFresh = !activeSessions.TryGetValue(session, out var drain) || drain?.Type == "running";
            running = drain?.Type == "running";
        }
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
            && idle is null && (info.Outcome is null || window.Messages.Any(m => m.IsInternalInitiator));
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
        OpenCodeSessionUiFact? questionPendingFact = EarliestUiEvidence(uiNow, state.FormsAt) is { } formsAt
            ? new(state.QuestionPending, formsAt) : null;
        DateTimeOffset? completionObservedAt = outcome is not null
            && watermark is { } completionWatermark && double.IsFinite(completionWatermark) && completionWatermark >= 0
                ? EarliestUiEvidence(uiNow, state.HistoryAt, infoAt, inboxAt, activeAt,
                    state.PermissionsAt, state.FormsAt)
                : null;

        if (protectedSession) projection = projection with { State = OpenCodeExecutionState.Unknown };
        state.Idle = projection.CanSwitchAgent && !projection.Waiting && !projection.KnownUnresolvedBackground
            && state.Permissions.Count == 0 && state.Forms.Count == 0;
        PublishSession(session, state, new(_source.Identity, session, projection, outcome, watermark, viewed, evidenceAt, neutral)
        {
            WorkingFact = workingFact,
            PendingActionFact = pendingActionFact,
            QuestionPendingFact = questionPendingFact,
            InfoObservedAt = infoObservedAt,
            CompletionObservedAt = completionObservedAt,
            // A later internal report after a genuine idle never erases that unread result.
            CompletionAbsentThrough = neutral && window.IsComplete && window.IsChronological
                && window.Messages.Length > 0 ? window.Messages[^1].Created : null,
        });
        if (_sink.IsReady && viewed is { } covered && state.History is { } history && Fresh(state.HistoryAt))
            await _sink.ReconcileViewAsync(_source, session, covered, history.Messages).ConfigureAwait(false);
    }

    private static DateTimeOffset? EarliestUiEvidence(DateTimeOffset now, params DateTimeOffset?[] times)
    {
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

    private void PublishUnknown(string session, StreamState state)
    {
        state.Idle = false;
        PublishSession(session, state,
            new(_source.Identity, session, new(OpenCodeExecutionState.Unknown, false, false, false, false, []),
                null, null, null, DateTimeOffset.UtcNow));
    }

    private void PublishSession(string session, StreamState state, OpenCodeSessionObservation observation)
    {
        if (!Active(session, state) || SessionObserved is not { } handlers) return;
        foreach (EventHandler<OpenCodeSessionObservationEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, new(observation)); } catch { }
    }

    /// <summary>Explicit opened-and-visible session only. Reads the server watermark anew; never DateTime.Now.
    /// The caller's source/selection fence guards POST initiation, including exact-watermark receipt retries.</summary>
    internal async Task<double?> AcknowledgeSessionAsync(string session, double coveredIdle, Func<Action, bool> fence, CancellationToken token)
    {
        if (!double.IsFinite(coveredIdle)) return null;
        await _ack.WaitAsync(token).ConfigureAwait(false);
        try
        {
            StreamState? state;
            lock (_sync) _tracked.TryGetValue(session, out state);
            if (state is null || !Active(session, state)) return null;
            var at = DateTimeOffset.UtcNow;
            var info = await ReadSessionAsync(session, token).ConfigureAwait(false);
            if (!Active(session, state) || !Fresh(at) || info?.Id != session || info.Time?.Idle is not { } idle
                || !double.IsFinite(idle) || coveredIdle > idle) return null;
            // Reconcile an uncertain successful POST before repeating the same monotone receipt.
            if (info.Time.Viewed is { } viewed && double.IsFinite(viewed) && viewed >= coveredIdle)
                return fence(() => { }) ? coveredIdle : null;
            Task? post = null;
            lock (_sync)
            {
                if (!Active(session, state)) return null;
                if (!fence(() => post = _source.Client.MarkSessionViewedAsync(session, coveredIdle, token))) return null;
            }
            await post!.ConfigureAwait(false);
            if (!Active(session, state) || !fence(() => { })) return null;
            if (_sink.IsReady && state.History is { } history && Fresh(state.HistoryAt))
                await _sink.ReconcileViewAsync(_source, session, coveredIdle, history.Messages).ConfigureAwait(false);
            return coveredIdle;
        }
        finally { _ack.Release(); }
    }
}
