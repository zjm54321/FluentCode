// Real tree event reducer + projection/render glue; only native tree/count controls are inert.
using System;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private sealed record TreeBinding(string SessionId);
    private TreeBinding? _treeBinding;
    private readonly List<OpenCodeAgentInfo> _treeAgents = new();
    private readonly Dictionary<string, OpenCodeSessionInfo> _treeChildren = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeTreeStatus> _treeStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Agent, OpenCodeModelRef? Model)> _treeObservedSteps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeTreeShell> _treeShells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeTreeShellKind> _treeShellKinds = new(StringComparer.Ordinal);
    private long _treeVersion;
    private readonly object? _treeParentTarget = null;
    private readonly bool _treeParentReturnBusy = false;
    private readonly string? _treeHint = null;
    private readonly BackgroundTreeSurface _agentTree = new();
    private sealed class BackgroundTreeSurface
    {
        internal IReadOnlyList<OpenCodeTreeAgent> Agents = Array.Empty<OpenCodeTreeAgent>();
        internal void ShowParentReturn(bool enabled) { }
        internal void Render(IReadOnlyList<OpenCodeTreeAgent> agents, OpenCodeTreeShell[] shells, string? hint) => Agents = agents;
    }
    private bool IsTreeCurrent(TreeBinding binding) => _pageActive && binding.SessionId == _selectedSessionId;
    private void OnBrowserAgentTreeChanged() { }
    private void QueueAgentTreeRefresh() { }
    private void SyncShellTerminalEntries() { }
    internal int TestBackgroundCount { get; private set; }
    private void RenderSubagentCount(int running) => TestBackgroundCount = running;
    internal IReadOnlyList<OpenCodeTreeAgent> TestBackgroundTree => _agentTree.Agents;
    internal void TestBindBackgroundChild(string childId)
    {
        _treeBinding = new(_selectedSessionId!);
        _treeAgents.Add(new() { Id = "worker", Name = "Worker", Mode = "subagent" });
        _treeChildren[childId] = new() { Id = childId, ParentId = _selectedSessionId, Agent = "worker" };
    }
}
