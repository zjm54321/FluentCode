using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode.Browser;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private readonly Dictionary<string, string> _browserSessionDirectories = new(StringComparer.Ordinal);

    private bool IsBrowserChildSession(string sessionId)
        => _treeChildren.ContainsKey(sessionId)
        || _agentSelector.CurrentSessionInfo is { ParentId: not null } info && info.Id == sessionId
        || _sessionItems.TryGetValue(sessionId, out var known) && known.ParentId is not null;

    private string? BrowserSessionDirectory(string sessionId)
    {
        var directory = KnownSessionDirectory(sessionId);
        if (string.IsNullOrWhiteSpace(directory) && _treeChildren.TryGetValue(sessionId, out var child)) directory = child.Location?.Directory;
        // Child sessions are intentionally absent from the root-session list. Use their own accepted metadata.
        if (string.IsNullOrWhiteSpace(directory) && _agentSelectorSessionId == sessionId
            && ReferenceEquals(_agentSelectorSource, _source) && _agentSelectorRevision == _sourceRevision
            && _agentSelector.CurrentSessionInfo is { } info && info.Id == sessionId) directory = info.Location?.Directory;
        if (string.IsNullOrWhiteSpace(directory) && _messageBinding is { } binding && binding.SessionId == sessionId
            && IsSameMessageBinding(sessionId, binding.Directory)) directory = binding.Directory;
        return string.IsNullOrWhiteSpace(directory) ? _browserSessionDirectories.GetValueOrDefault(sessionId) : directory;
    }

    partial void OnBrowserAgentTreeChanged() => SyncBrowserChildSessions();

    private void SyncBrowserChildSessions()
    {
        if (_browserPeerManager is not { } manager) return;
        var current = _pageActive && SourceReady && _treeBinding is { } binding && IsTreeCurrent(binding) && _treeParentTarget is null;
        var running = current ? _treeChildren.Values.Where(child => child.Id is not null && child.ParentId == _selectedSessionId
            && (_treeStatuses.TryGetValue(child.Id, out var status) ? status == OpenCodeTreeStatus.Running
                : _liveSessionStatuses.TryGetValue(child.Id, out var live) && live.Working))
            .Where(child => !string.IsNullOrWhiteSpace(child.Location?.Directory))
            .Select(child => new OpenCodeBrowserChildSession(child.Id!, child.Location!.Directory!)).ToArray()
            : Array.Empty<OpenCodeBrowserChildSession>();
        foreach (var child in running) _browserSessionDirectories[child.SessionId] = child.Directory;
        manager.SyncRunningChildren(running);
        // A finished child's pages remain inspectable under its parent. Leaving the tree scope releases unopened engines.
        // Reset/loading is not evidence that an outgoing child's pages are untracked. Wait for the new root inventory.
        if (current && _treeChildrenAccepted)
            foreach (var id in _browserWorkspaces.Keys.Where(id => id != _selectedSessionId && !_sessionTabs.ContainsKey(id)
                && !_treeChildren.ContainsKey(id)).ToArray()) OnBrowserEngineSessionTabClosed(id);
        RefreshBrowserPanel();
    }
}
