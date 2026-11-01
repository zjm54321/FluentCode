using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentCode.Services.Taskbar;

public sealed record TaskbarSessionKey(string EndpointIdentity, string SessionId);
public enum TaskbarSessionState { Unknown, Working, Waiting, Completed, Failed, Cancelled, Idle }
/// <summary>A replaceable session observation, never an execution or a percentage. Terminal evidence
/// requires both a server outcome and the fresh server idle watermark that covers it.</summary>
public sealed record TaskbarSessionObservation(TaskbarSessionState State, string? Outcome = null,
    double? IdleWatermark = null, double? ViewedWatermark = null);

public sealed partial class TaskbarStateAggregator
{
    private sealed record SessionEntry(long Generation, TaskbarSessionObservation Observation, double? Acknowledged, bool Protected = false);
    private readonly Dictionary<TaskbarSessionKey, SessionEntry> _sessions = new();
    private readonly Dictionary<string, long> _sessionSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sessionSourceHighWater = new(StringComparer.Ordinal);

    // Caller owns source generations; a revoked generation can never be reintroduced by a late callback.
    public void SetSessionSource(string endpointIdentity, long generation)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointIdentity);
        if (_sessionSourceHighWater.TryGetValue(endpointIdentity, out var current) && generation <= current) return;
        RevokeSessionSource(endpointIdentity);
        _sessionSources[endpointIdentity] = generation;
        _sessionSourceHighWater[endpointIdentity] = generation;
    }

    public void RevokeSessionSource(string endpointIdentity)
    {
        _sessionSources.Remove(endpointIdentity);
        foreach (var key in _sessions.Keys.Where(k => k.EndpointIdentity == endpointIdentity).ToArray()) _sessions.Remove(key);
    }

    public bool ObserveSession(TaskbarSessionKey key, long generation, TaskbarSessionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrEmpty(key.SessionId);
        if (!_sessionSources.TryGetValue(key.EndpointIdentity, out var current) || generation != current) return false;
        if (!Enum.IsDefined(observation.State)) throw new ArgumentOutOfRangeException(nameof(observation));
        if (observation.State is TaskbarSessionState.Completed or TaskbarSessionState.Failed or TaskbarSessionState.Cancelled)
        {
            var expected = observation.State switch { TaskbarSessionState.Completed => "succeeded", TaskbarSessionState.Failed => "failed", _ => "interrupted" };
            if (observation.Outcome != expected || observation.IdleWatermark is not { } idle || !double.IsFinite(idle))
                observation = new(TaskbarSessionState.Unknown);
        }
        _sessions.TryGetValue(key, out var previous);
        _sessions[key] = new(generation, observation, previous?.Acknowledged, previous?.Protected == true);
        return true;
    }

    public bool RemoveSession(TaskbarSessionKey key, long generation) =>
        _sessions.TryGetValue(key, out var entry) && entry.Generation == generation && _sessions.Remove(key);

    public bool AcknowledgeSession(TaskbarSessionKey key, long generation, double observedIdle)
    {
        if (!double.IsFinite(observedIdle) || !_sessions.TryGetValue(key, out var entry) || entry.Generation != generation) return false;
        _sessions[key] = entry with { Acknowledged = Math.Max(entry.Acknowledged ?? double.NegativeInfinity, observedIdle) };
        return true; // ACK never removes Unknown, Working, or Waiting; it covers only terminal watermarks.
    }

    public TaskbarSessionObservation? GetSession(TaskbarSessionKey key) => _sessions.TryGetValue(key, out var entry) ? entry.Observation : null;

    // Local interaction arbitration is independent of replaceable/expiring server observations and view.
    public bool SetSessionProtection(TaskbarSessionKey key, long generation, bool isProtected)
    {
        if (!_sessions.TryGetValue(key, out var entry) || entry.Generation != generation) return false;
        _sessions[key] = entry with { Protected = isProtected };
        return entry.Protected != isProtected;
    }

    private IEnumerable<TaskbarTaskObservation> AllObservations()
    {
        foreach (var task in _tasks.Values) yield return task;
        foreach (var entry in _sessions.Values)
        {
            var observation = entry.Observation;
            if (entry.Protected) { yield return new(TaskbarTaskState.OutcomeUnknown); continue; }
            if (observation.State == TaskbarSessionState.Idle) continue;
            if (observation.State is TaskbarSessionState.Completed or TaskbarSessionState.Failed or TaskbarSessionState.Cancelled
                && observation.IdleWatermark is { } idle
                && (entry.Acknowledged >= idle || observation.ViewedWatermark >= idle)) continue;
            yield return new(observation.State switch
            {
                TaskbarSessionState.Working => TaskbarTaskState.Working,
                TaskbarSessionState.Waiting => TaskbarTaskState.Pending,
                TaskbarSessionState.Completed => TaskbarTaskState.Completed,
                TaskbarSessionState.Failed => TaskbarTaskState.Failed,
                TaskbarSessionState.Cancelled => TaskbarTaskState.Cancelled,
                _ => TaskbarTaskState.OutcomeUnknown,
            });
        }
    }
}
