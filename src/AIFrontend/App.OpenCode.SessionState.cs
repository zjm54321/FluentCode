using System;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend;

public sealed class OpenCodeSessionUiStateChangedEventArgs : EventArgs
{
    public OpenCodeSessionUiStateChangedEventArgs(string endpointIdentity, long revision, long version,
        OpenCodeSessionUiSnapshot? snapshot)
    {
        EndpointIdentity = endpointIdentity;
        Revision = revision;
        Version = version;
        Snapshot = snapshot;
    }

    public string EndpointIdentity { get; }
    public long Revision { get; }
    public long Version { get; }
    public OpenCodeSessionUiSnapshot? Snapshot { get; }
}

public partial class App
{
    // Dispatcher-owned. Retain the store across bindings so Reset preserves monotonic Version.
    private OpenCodeSessionUiStateStore? _sessionUiStore;
    private OpenCodePageSource? _sessionUiSourceOwner;
    private bool _sessionUiChanged;

    public event EventHandler<OpenCodeSessionUiStateChangedEventArgs>? OpenCodeSessionUiStateChanged;

    public OpenCodeSessionUiSnapshot? GetOpenCodeSessionUiSnapshot(OpenCodePageSource source, long revision)
    {
        RequireAppThread();
        if (!IsSessionUiOwner(source, revision)) return null;
        return _sessionUiStore!.GetSnapshot();
    }

    private bool IsSessionUiOwner(OpenCodePageSource source, long revision)
    {
        if (_closing || source is null || source.IsRevoked || _sessionUiStore is null
            || !ReferenceEquals(source, _sessionUiSourceOwner)
            || !ReferenceEquals(source, CurrentOpenCodePageSource) || revision != source.Revision) return false;
        var snapshot = _sessionUiStore.GetSnapshot();
        return snapshot.Revision == revision
            && string.Equals(snapshot.EndpointIdentity, source.Identity, StringComparison.Ordinal);
    }

    private void BindSessionUiStore(OpenCodePageSource source)
    {
        if (_sessionUiStore is null) _sessionUiStore = new(source.Identity, source.Revision);
        else _sessionUiStore.Reset(source.Identity, source.Revision);
        _sessionUiSourceOwner = source;
        _sessionUiChanged = true;
    }

    private void InvalidateSessionUiStore(OpenCodePageSource? expectedOwner)
    {
        if (expectedOwner is null || !ReferenceEquals(expectedOwner, _sessionUiSourceOwner)) return;
        _sessionUiSourceOwner = null;
        _sessionUiChanged = false;
        _sessionUiStore!.Reset(expectedOwner.Identity, expectedOwner.Revision);
        var version = _sessionUiStore.GetSnapshot().Version;
        RaiseAppEvent(OpenCodeSessionUiStateChanged,
            new OpenCodeSessionUiStateChangedEventArgs(expectedOwner.Identity, expectedOwner.Revision, version, null));
    }

    private void PublishSessionUiChanges()
    {
        var owner = _sessionUiSourceOwner;
        if (!_sessionUiChanged || owner is null || !IsSessionUiOwner(owner, owner.Revision)) return;
        _sessionUiChanged = false;
        var snapshot = _sessionUiStore!.GetSnapshot();
        RaiseAppEvent(OpenCodeSessionUiStateChanged,
            new OpenCodeSessionUiStateChangedEventArgs(snapshot.EndpointIdentity, snapshot.Revision, snapshot.Version, snapshot));
    }

    private static bool IsFreshSessionUiEvidence(DateTimeOffset? observedAt, DateTimeOffset now)
        => observedAt is { } at && now >= at && now - at < OpenCodeNotificationMonitor.ObservationLifetime;

    private OpenCodeSessionUiObservation? CreateSessionUiObservation(OpenCodePageSource source,
        OpenCodeSessionObservation observation, DateTimeOffset now)
    {
        if (!IsSessionUiOwner(source, source.Revision)) return null;
        var continuous = observation.EventStateCurrent?.Invoke() == true;
        var completion = (continuous || IsFreshSessionUiEvidence(observation.CompletionObservedAt, now))
            && observation.Outcome is { } outcome && observation.IdleWatermark is { } idle
            ? new OpenCodeCompletionMark(outcome, idle) : null;
        var viewed = IsFreshSessionUiEvidence(observation.InfoObservedAt, now)
            && observation.ViewedWatermark is { } watermark && double.IsFinite(watermark) && watermark >= 0
            ? (double?)watermark : null;
        return new(source.Identity, source.Revision, observation.SessionId,
            observation.WorkingFact, observation.PendingActionFact, Completion: completion, ViewedWatermark: viewed)
        {
            QuestionPending = observation.QuestionPendingFact is { } question
                && (continuous || IsFreshSessionUiEvidence(question.ObservedAt, now)) ? question : null,
        };
    }

    private void ApplySessionUiProtection(OpenCodePageSource source, string sessionId, bool isProtected,
        OpenCodeSessionUiObservation? observation, DateTimeOffset now)
    {
        if (!IsSessionUiOwner(source, source.Revision)) return;
        observation ??= new(source.Identity, source.Revision, sessionId);
        _sessionUiChanged |= _sessionUiStore!.Apply(observation with { MutationProtected = isProtected }, now);
    }

    private void ApplySessionUiViewedReceipt(OpenCodePageSource source, long revision, string sessionId, double covered)
    {
        if (!IsSessionUiOwner(source, revision)) return;
        _sessionUiChanged |= _sessionUiStore!.Apply(
            new(source.Identity, revision, sessionId, ViewedWatermark: covered), DateTimeOffset.UtcNow);
    }
}
