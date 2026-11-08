using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private sealed class BackgroundSessionEntry
    {
        internal readonly Dictionary<string, OpenCodeSessionUiFact?> Children = new(StringComparer.Ordinal);
        internal HashSet<string> HistoryChildren = new(StringComparer.Ordinal);
        internal OpenCodeSessionUiFact? Absence;
        internal OpenCodeSessionUiFact? Published;
        internal int PublishedCount;
        internal Task? Coverage;
    }

    // Relationships come only from accepted parent history or the verified child tree. They are
    // source-owned, so later child events can update an unselected parent's tab without any GET.
    private readonly Dictionary<string, BackgroundSessionEntry> _backgroundSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeSessionUiFact> _backgroundChildActivity = new(StringComparer.Ordinal);
    private OpenCodePageSource? _backgroundSource;
    private long _backgroundRevision;
    private int _backgroundSourceGeneration, _backgroundPageGeneration;

    private bool EnsureBackgroundSource()
    {
        if (!DispatcherQueue.HasThreadAccess || !_pageActive || !SourceReady || _source is not { IsRevoked: false } source
            || source.Revision != _sourceRevision || Application.Current is not App app
            || !ReferenceEquals(source, app.CurrentOpenCodePageSource)) return false;
        if (!ReferenceEquals(source, _backgroundSource) || _sourceRevision != _backgroundRevision
            || _sourceGeneration != _backgroundSourceGeneration || _pageGeneration != _backgroundPageGeneration)
        {
            _backgroundSessions.Clear();
            _backgroundChildActivity.Clear();
            _backgroundSource = source;
            _backgroundRevision = _sourceRevision;
            _backgroundSourceGeneration = _sourceGeneration;
            _backgroundPageGeneration = _pageGeneration;
        }
        return true;
    }

    private bool CanObserveBackgroundProjection(string sessionId) => sessionId == _selectedSessionId && !_sessionMissing
        && _messageHistoryWorkflowOwner is null && !_messageHistoryNeedsState && _messageHistoryBoundaryKnown && EnsureBackgroundSource();

    private BackgroundSessionEntry BackgroundEntry(string sessionId)
    {
        if (!_backgroundSessions.TryGetValue(sessionId, out var entry)) _backgroundSessions[sessionId] = entry = new();
        return entry;
    }

    partial void OnSessionBackgroundProjection(string sessionId, ImmutableArray<string> unresolvedChildren, bool complete)
    {
        if (!CanObserveBackgroundProjection(sessionId)) return;
        var entry = BackgroundEntry(sessionId);
        var children = unresolvedChildren.ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var absence = CreateBackgroundFact(false, now, CaptureHealthyEventStream());
        foreach (var child in children)
        {
            // History proves a relationship, not that an old launch is still running. Native
            // activity is retained even when it arrived before the parent history/tree did.
            if (_backgroundChildActivity.TryGetValue(child, out var activity) && IsBackgroundFactCurrent(activity, now))
                entry.Children[child] = activity;
            else entry.Children.TryAdd(child, null);
        }
        if (complete)
        {
            foreach (var child in entry.HistoryChildren.Where(child => !children.Contains(child))) entry.Children[child] = absence;
            entry.Absence = absence;
        }
        entry.HistoryChildren = children;
        PublishBackgroundState(sessionId, entry);
    }

    partial void OnSessionBackgroundTree(string sessionId, IEnumerable<(string SessionId, OpenCodeTreeStatus Status)> children,
        bool complete, DateTimeOffset? observedAt, Task? coverage)
    {
        if (!CanObserveBackgroundProjection(sessionId)) return;
        var entry = BackgroundEntry(sessionId);
        var now = DateTimeOffset.UtcNow;
        var evidence = observedAt is { } at ? CreateBackgroundFact(false, at, coverage) : null;
        foreach (var (child, status) in children)
        {
            entry.Children.TryAdd(child, null);
            if (_backgroundChildActivity.TryGetValue(child, out var activity) && IsBackgroundFactCurrent(activity, now)
                && (evidence is null || activity.ObservedAt >= evidence.ObservedAt)) entry.Children[child] = activity;
            else if (IsBackgroundFactCurrent(evidence, now)
                && status is OpenCodeTreeStatus.Running or OpenCodeTreeStatus.Succeeded or OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted
                && (entry.Children[child] is not { } previous || evidence!.ObservedAt >= previous.ObservedAt))
            {
                var fact = evidence! with { Value = status == OpenCodeTreeStatus.Running };
                entry.Children[child] = fact;
                _backgroundChildActivity[child] = fact;
            }
            // Idle/unknown is not a child completion proof; redraws keep the GET's original evidence.
        }
        if (complete && IsBackgroundFactCurrent(evidence, now)
            && (entry.Absence is null || evidence!.ObservedAt >= entry.Absence.ObservedAt)) entry.Absence = evidence;
        PublishBackgroundState(sessionId, entry);
    }

    partial void OnSessionBackgroundActivity(string sessionId, bool working)
    {
        var coverage = CaptureHealthyEventStream();
        RememberBackgroundActivity(sessionId, working, DateTimeOffset.UtcNow, HasContinuousEventCoverage(coverage) ? coverage : null);
    }

    partial void OnSessionBackgroundActiveSnapshot(string sessionId, DateTimeOffset observedAt, Task? coverage)
        => RememberBackgroundActivity(sessionId, true, observedAt, coverage);

    private void RememberBackgroundActivity(string sessionId, bool working, DateTimeOffset observedAt, Task? coverage)
    {
        if (!EnsureBackgroundSource()) return;
        var fact = CreateBackgroundFact(working, observedAt, coverage);
        var now = DateTimeOffset.UtcNow;
        // Active reads can finish before the parent's first boundary hydration. Retain that
        // exact healthy epoch for the later accepted relationship; a replaced epoch is rejected.
        if (!IsBackgroundFactCurrent(fact, now)
            && !(now >= observedAt && coverage is not null && ReferenceEquals(coverage, CaptureHealthyEventStream()))) return;
        if (_backgroundChildActivity.TryGetValue(sessionId, out var previous) && previous.Value == working
            && previous.CurrentEventStream?.Invoke() == true) fact = previous;
        else if (previous is not null && previous.ObservedAt > observedAt) return;
        _backgroundChildActivity[sessionId] = fact;
        foreach (var (parent, entry) in _backgroundSessions)
        {
            if (!entry.Children.ContainsKey(sessionId)) continue;
            entry.Children[sessionId] = fact;
            PublishBackgroundState(parent, entry);
        }
    }

    private static bool IsBackgroundFactCurrent(OpenCodeSessionUiFact? fact, DateTimeOffset now)
        => fact is not null && now >= fact.ObservedAt && (fact.CurrentEventStream?.Invoke() is { } live
            ? live : now - fact.ObservedAt < OpenCodeNotificationMonitor.ObservationLifetime);

    private void PublishBackgroundState(string sessionId, BackgroundSessionEntry entry)
    {
        if (!EnsureBackgroundSource() || Application.Current is not App app) return;
        var now = DateTimeOffset.UtcNow;
        if (entry.Absence is null && entry.Children.Count == 0) return;
        var current = entry.Children.Values.Select(fact => IsBackgroundFactCurrent(fact, now) ? fact : null).ToArray();
        var count = current.Count(fact => fact?.Value == true);
        bool? working = count > 0 ? true : current.Any(fact => fact?.Value is null)
            || current.Length == 0 && !IsBackgroundFactCurrent(entry.Absence, now) ? null : false;
        var proofs = working == true ? current.Where(fact => fact?.Value == true).Select(fact => fact!).ToArray()
            : working == false ? current.Length == 0 ? new[] { entry.Absence! } : current.Select(fact => fact!).ToArray()
            : Array.Empty<OpenCodeSessionUiFact>();
        var observedAt = proofs.Length > 0 ? proofs.Min(fact => fact.ObservedAt) : now;
        // Only the child proofs themselves can grant continuity. A fresh parent history, tree
        // redraw or one sibling's event cannot renew another child's expired execution state.
        var coverage = proofs.Length > 0 && proofs.All(fact => fact.CurrentEventStream?.Invoke() == true)
            ? CaptureHealthyEventStream() : null;
        if (entry.Published is { } previous)
        {
            if (working is null && !IsBackgroundFactCurrent(previous, now)) return;
            if (previous.Value == working && entry.PublishedCount == count
                && (working is null || previous.ObservedAt == observedAt && ReferenceEquals(entry.Coverage, coverage)
                    && previous.CurrentEventStream?.Invoke() != false)) return;
        }
        var fact = CreateBackgroundFact(working, observedAt, coverage);
        entry.Published = fact;
        entry.PublishedCount = count;
        entry.Coverage = coverage;
        app.ApplyOpenCodeSessionBackgroundState(_backgroundSource!, _backgroundRevision, sessionId, fact, count);
    }

    private OpenCodeSessionUiFact CreateBackgroundFact(bool? working, DateTimeOffset observedAt, Task? coverage)
    {
        var source = _backgroundSource!;
        var revision = _backgroundRevision;
        var sourceGeneration = _backgroundSourceGeneration;
        var pageGeneration = _backgroundPageGeneration;
        Func<bool>? current = coverage is null ? null : () => _pageActive && ReferenceEquals(source, _source)
            && !source.IsRevoked && revision == _sourceRevision && sourceGeneration == _sourceGeneration
            && pageGeneration == _pageGeneration && HasContinuousEventCoverage(coverage);
        return new(working, observedAt) { CurrentEventStream = current };
    }
}
