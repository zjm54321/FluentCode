using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Taskbar;
using Microsoft.UI.Dispatching;

namespace FluentCode;

public partial class App
{
    private readonly Dictionary<TaskbarSessionKey, (long Revision, DateTimeOffset At)> _sessionObservationTimes = new();
    private readonly Dictionary<TaskbarSessionKey, Func<bool>> _sessionEventLeases = new();
    private DispatcherQueueTimer? _sessionObservationTimer;

    private void SeedOpenCodeObservation(OpenCodePageSource source, string session, bool sampleUiProtection = true)
    {
        if (sampleUiProtection && IsSessionUiOwner(source, source.Revision))
            RefreshOpenCodeSessionProtection(source, session, uiOnly: true);
        var key = new TaskbarSessionKey(source.Identity, session);
        _taskbarState.SetSessionSource(source.Identity, source.Revision);
        if (!_sessionObservationTimes.ContainsKey(key))
        {
            _taskbarState.ObserveSession(key, source.Revision, new(TaskbarSessionState.Unknown));
            _sessionObservationTimes[key] = (source.Revision, DateTimeOffset.UtcNow);
            ApplyTaskbarAggregate();
        }
        if (_sessionObservationTimer is null && _appDispatcher is { } dispatcher)
        {
            _sessionObservationTimer = dispatcher.CreateTimer();
            _sessionObservationTimer.Interval = TimeSpan.FromSeconds(1);
            _sessionObservationTimer.Tick += (_, _) =>
            {
                if (_closing) { _sessionObservationTimer?.Stop(); return; }
                bool changed = false;
                var now = DateTimeOffset.UtcNow;
                if (_sessionUiSourceOwner is { } owner && IsSessionUiOwner(owner, owner.Revision))
                    _sessionUiChanged |= _sessionUiStore!.Expire(now);
                foreach (var pair in _sessionObservationTimes.ToArray())
                {
                    if (CurrentOpenCodePageSource is { } source && source.Revision == pair.Value.Revision)
                        changed |= RefreshOpenCodeSessionProtection(source, pair.Key.SessionId);
                    var hasStream = _sessionEventLeases.TryGetValue(pair.Key, out var stream);
                    var continuous = stream?.Invoke() == true;
                    if (hasStream && !continuous) _sessionEventLeases.Remove(pair.Key);
                    if ((hasStream && !continuous || !continuous && DateTimeOffset.UtcNow - pair.Value.At > OpenCodeNotificationMonitor.ObservationLifetime)
                        && _taskbarState.GetSession(pair.Key)?.State != TaskbarSessionState.Unknown)
                        changed |= _taskbarState.ObserveSession(pair.Key, pair.Value.Revision, new(TaskbarSessionState.Unknown));
                }
                if (changed) ApplyTaskbarAggregate();
                PublishSessionUiChanges();
            };
            _sessionObservationTimer.Start();
        }
    }

    private void ResetOpenCodeObservations()
    {
        InvalidateSessionUiStore(_sessionUiSourceOwner);
        foreach (var endpoint in _sessionObservationTimes.Keys.Select(k => k.EndpointIdentity).Distinct())
            _taskbarState.RevokeSessionSource(endpoint);
        _sessionObservationTimes.Clear();
        _sessionEventLeases.Clear();
        ApplyTaskbarAggregate();
    }

    private void OnOpenCodeSessionObserved(object? sender, OpenCodeSessionObservationEventArgs args)
    {
        OpenCodePageSource? source;
        lock (_integrationSync)
        {
            if (_closing || !ReferenceEquals(sender, _openCodeMonitor)) return;
            source = _openCodePageSource;
        }
        if (source is null) return;
        PostToApp(() =>
        {
            if (!IsCurrentOpenCodeCallback(sender, source, source.Revision)) return;
            var observation = args.Observation;
            if (observation.EndpointIdentity != source.Identity) return;
            if (observation.EventStateCurrent is { } eventState && !eventState()) return;
            SeedOpenCodeObservation(source, observation.SessionId, sampleUiProtection: false);
            var key = new TaskbarSessionKey(source.Identity, observation.SessionId);
            var projection = observation.Projection;
            var state = observation.EventStateCurrent is null && DateTimeOffset.UtcNow - observation.ObservedAt > OpenCodeNotificationMonitor.ObservationLifetime
                ? TaskbarSessionState.Unknown
                : projection.WaitingKnown && projection.Waiting ? TaskbarSessionState.Waiting
                : projection.State == Services.Notifications.OpenCodeExecutionState.Working ? TaskbarSessionState.Working
                : observation.Outcome switch
                {
                    "succeeded" => TaskbarSessionState.Completed,
                    "failed" => TaskbarSessionState.Failed,
                    "interrupted" => TaskbarSessionState.Cancelled,
                    _ => observation.IsNeutralIdle ? TaskbarSessionState.Idle : TaskbarSessionState.Unknown,
                };
            _taskbarState.ObserveSession(key, source.Revision,
                new(state, observation.Outcome, observation.IdleWatermark, observation.ViewedWatermark));
            _sessionObservationTimes[key] = (source.Revision, observation.ObservedAt);
            if (observation.EventStateCurrent is { } lease) _sessionEventLeases[key] = lease;
            else _sessionEventLeases.Remove(key);
            var now = DateTimeOffset.UtcNow;
            RefreshOpenCodeSessionProtection(source, observation.SessionId,
                CreateSessionUiObservation(source, observation, now), now);
            RecordDiagnostic("taskbar-session-observed", taskbar: ApplyTaskbarAggregate());
            PublishSessionUiChanges();
        });
    }

    /// <summary>UI-thread entry for the page AFTER the exact session is opened and visible. The optional
    /// callback must read a thread-safe selection snapshot, not XAML; false cancels a changed selection.
    /// Pass the idle watermark actually rendered by the page, never a subsequently refreshed watermark.
    /// Returns that exact acknowledged watermark, or null if no safe receipt was obtained.
    /// No permission/form acknowledgement; no automatic retry after an uncertain POST.</summary>
    public async Task<double?> AcknowledgeOpenCodeSessionAsync(OpenCodePageSource source, long revision,
        string sessionId, double coveredIdle, Func<bool>? isStillVisible = null, CancellationToken cancellationToken = default)
    {
        RequireAppThread();
        if (_closing || !double.IsFinite(coveredIdle) || revision != source.Revision || !ReferenceEquals(source, CurrentOpenCodePageSource)) return null;
        TrackOpenCodeSessionCore(sessionId, publishUi: false);
        PublishSessionUiChanges();
        var monitor = _openCodeMonitor;
        if (monitor is null) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, source.RevocationToken);
        bool Fence(Action action)
        {
            bool ran = false;
            return source.TryExecute(linked.Token, () =>
            {
                if (isStillVisible?.Invoke() == false) return;
                action(); ran = true;
            }) && ran;
        }
        try
        {
            var idle = await monitor.AcknowledgeSessionAsync(sessionId, coveredIdle, Fence, linked.Token).ConfigureAwait(false);
            if (idle is { } covered)
                PostToApp(() =>
                {
                    if (!IsCurrentOpenCodeCallback(monitor, source, revision)) return;
                    _taskbarState.AcknowledgeSession(new(source.Identity, sessionId), revision, covered);
                    ApplySessionUiViewedReceipt(source, revision, sessionId, covered);
                    RefreshOpenCodeSessionProtection(source, sessionId);
                    RecordDiagnostic("opencode-session-viewed", taskbar: ApplyTaskbarAggregate());
                    PublishSessionUiChanges();
                });
            return idle;
        }
        catch (Exception ex)
        {
            PostToApp(() =>
            {
                if (IsCurrentOpenCodeCallback(monitor, source, revision))
                    RecordDiagnostic("opencode-session-view-unconfirmed", error: ex);
            });
            return null; // The server may have accepted the receipt. Never retry automatically.
        }
    }

    public void RefreshOpenCodeSessionProtection(string sessionId)
    {
        RequireAppThread();
        if (_closing || CurrentOpenCodePageSource is not { } source) return;
        TrackOpenCodeSessionCore(sessionId, publishUi: false);
        RefreshOpenCodeSessionProtection(source, sessionId);
        ApplyTaskbarAggregate();
        PublishSessionUiChanges();
    }

    private bool RefreshOpenCodeSessionProtection(OpenCodePageSource source, string sessionId,
        OpenCodeSessionUiObservation? uiObservation = null, DateTimeOffset? observedNow = null, bool uiOnly = false)
    {
        // Sample AND apply synchronously under the gate on the UI thread. Never queue an unprotected
        // snapshot that could clear a later unknown operation. A busy gate is conservatively protected.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken);
        var pending = OpenCodeInteractionGate.Shared.EnterAsync(source.Identity, sessionId, attempt.Token);
        bool isProtected = true;
        var now = observedNow ?? DateTimeOffset.UtcNow;
        if (pending.IsCompletedSuccessfully)
        {
            using var gate = pending.Result;
            isProtected = !gate.CanSwitchAgent();
            ApplySessionUiProtection(source, sessionId, isProtected, uiObservation, now);
            return !uiOnly && _taskbarState.SetSessionProtection(new(source.Identity, sessionId), source.Revision, isProtected);
        }
        attempt.Cancel(); // Do not accumulate waiters behind a long-running interaction every timer tick.
        _ = ReleaseGateAsync(pending); // A raced successful acquisition still needs its lease released.
        ApplySessionUiProtection(source, sessionId, isProtected, uiObservation, now);
        return !uiOnly && _taskbarState.SetSessionProtection(new(source.Identity, sessionId), source.Revision, isProtected);
    }

    private static async Task ReleaseGateAsync(Task<OpenCodeInteractionGate.Lease> pending)
    {
        try { using var gate = await pending.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // Gate capacity fails closed.
    }
}
