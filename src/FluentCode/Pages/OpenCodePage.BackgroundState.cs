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
        internal readonly Dictionary<string, bool?> Children = new(StringComparer.Ordinal);
        internal HashSet<string> HistoryChildren = new(StringComparer.Ordinal);
        internal bool AbsenceKnown;
        internal OpenCodeSessionUiFact? Published;
        internal int PublishedCount;
        internal Task? Coverage;
    }

    // Relationships come only from accepted parent history or the verified child tree. They are
    // source-owned, so later child events can update an unselected parent's tab without any GET.
    private readonly Dictionary<string, BackgroundSessionEntry> _backgroundSessions = new(StringComparer.Ordinal);
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
        foreach (var child in children)
        {
            // A historical launch cannot resurrect a child whose native terminal was accepted.
            if (!entry.Children.TryGetValue(child, out var working) || working is null) entry.Children[child] = true;
        }
        if (complete)
            foreach (var child in entry.HistoryChildren.Where(child => !children.Contains(child))) entry.Children[child] = false;
        entry.HistoryChildren = children;
        entry.AbsenceKnown |= complete;
        PublishBackgroundState(sessionId, entry);
    }

    partial void OnSessionBackgroundTree(string sessionId, IEnumerable<(string SessionId, OpenCodeTreeStatus Status)> children, bool complete)
    {
        if (!CanObserveBackgroundProjection(sessionId)) return;
        var entry = BackgroundEntry(sessionId);
        foreach (var (child, status) in children)
        {
            if (status == OpenCodeTreeStatus.Running) entry.Children[child] = true;
            else if (status is OpenCodeTreeStatus.Succeeded or OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted) entry.Children[child] = false;
            else entry.Children.TryAdd(child, null); // Idle/unknown is not a child completion proof.
        }
        entry.AbsenceKnown |= complete;
        PublishBackgroundState(sessionId, entry);
    }

    partial void OnSessionBackgroundActivity(string sessionId, bool working)
    {
        if (!EnsureBackgroundSource()) return;
        foreach (var (parent, entry) in _backgroundSessions)
        {
            if (!entry.Children.ContainsKey(sessionId)) continue;
            // A new connection's event proves only this child. Do not carry sibling states across
            // a coverage gap and then grant them a fresh indefinite event lease.
            if (entry.Coverage is { } old && !HasContinuousEventCoverage(old))
                foreach (var child in entry.Children.Keys.ToArray()) entry.Children[child] = null;
            entry.Children[sessionId] = working;
            PublishBackgroundState(parent, entry);
        }
    }

    private void PublishBackgroundState(string sessionId, BackgroundSessionEntry entry)
    {
        if (!EnsureBackgroundSource() || Application.Current is not App app) return;
        if (!entry.AbsenceKnown && entry.Children.Count == 0) return;
        var count = entry.Children.Count(child => child.Value == true);
        bool? working = count > 0 ? true : entry.Children.Values.Any(value => value is null) ? null : false;
        var coverage = CaptureHealthyEventStream();
        if (entry.Published?.Value == working && entry.PublishedCount == count && ReferenceEquals(entry.Coverage, coverage)
            && entry.Published?.CurrentEventStream?.Invoke() == true) return; // No per-token snapshot or Narrator churn.
        var source = _backgroundSource!;
        var revision = _backgroundRevision;
        var sourceGeneration = _backgroundSourceGeneration;
        var pageGeneration = _backgroundPageGeneration;
        Func<bool>? current = coverage is null ? null : () => _pageActive && ReferenceEquals(source, _source)
            && !source.IsRevoked && revision == _sourceRevision && sourceGeneration == _sourceGeneration
            && pageGeneration == _pageGeneration && HasContinuousEventCoverage(coverage);
        var fact = new OpenCodeSessionUiFact(working, DateTimeOffset.UtcNow) { CurrentEventStream = current };
        entry.Published = fact;
        entry.PublishedCount = count;
        entry.Coverage = coverage;
        app.ApplyOpenCodeSessionBackgroundState(source, revision, sessionId, fact, count);
    }
}
