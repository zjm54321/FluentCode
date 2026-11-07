using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private readonly OpenCodeAgentTreeView _agentTree = new();
    private readonly List<OpenCodeAgentInfo> _treeAgents = new();
    private readonly Dictionary<string, OpenCodeSessionInfo> _treeChildren = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeTreeStatus> _treeStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string? Agent, OpenCodeModelRef? Model)> _treeObservedSteps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeTreeShell> _treeShells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeTreeShellKind> _treeShellKinds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _treeCts;
    private TreeBinding? _treeBinding;
    private long _treeVersion;
    private string? _treeMessageSignature;
    private bool _treeRefreshQueued;
    private bool _treeLoading;
    private bool _treeChildrenAccepted;
    private bool _treeReload;
    private string? _treeDirectory;
    private string? _treeHint;
    private string? _treeScopeAgentId;
    private IReadOnlyList<OpenCodeModelInfo>? _treeModels;
    private SessionReturnTarget? _treeParentTarget;
    private bool _treeParentReturnBusy;
    partial void OnBrowserAgentTreeChanged();

    private sealed record TreeBinding(OpenCodePageSource Source, long Revision, int SourceGeneration,
        OpenCodeClient Client, int PageGeneration, int SessionGeneration, string SessionId);

    private void InitializeAgentTree()
    {
        _conversationView.ChildSessionTarget = FindKnownChildSession;
        LeftAgentTreeSlot.Children.Add(_agentTree);
        InitializePersistentPty();
        InitializePersistentPtyPanel();
        _agentTree.SessionRequested += id => _ = OpenAgentTreeChildAsync(id);
        _agentTree.ParentReturnRequested += () => _ = ReturnFromAgentTreeAsync();
        _agentTree.ShellRequested += OpenTerminalView;
        InitializeChildReturn();
        _agentSelector.DisplayChanged += (_, _) => RefreshAgentTreeScope();
        Loaded += (_, _) => QueueAgentTreeRefresh();
        Unloaded += (_, _) => { if (!IsLoaded) ResetAgentTree(); };
    }

    private (string? Agent, string? Description)? FindKnownChildSession(string sessionId)
    {
        if (!_pageActive || !SourceReady) return null;
        if (_treeBinding is { } binding && IsTreeCurrent(binding) && _treeChildren.TryGetValue(sessionId, out var child))
            return (child.Agent, child.Title);
        return _sessionItems.TryGetValue(sessionId, out var known) ? (known.Agent, known.Title) : null;
    }

    private TreeBinding? CaptureTreeBinding() => _pageActive && SourceReady && !_sessionMissing
        && _source is { } source && _client is { } client && _selectedSessionId is { } id
            ? new(source, _sourceRevision, _sourceGeneration, client, _pageGeneration, _sessionGeneration, id) : null;

    private bool IsTreeCurrent(TreeBinding binding) => CaptureTreeBinding() == binding && !binding.Source.IsRevoked;

    private void ResetAgentTree()
    {
        ResetPersistentPtyScope();
        CloseTerminalView(restoreFocus: false);
        _treeCts?.Cancel();
        _treeCts?.Dispose();
        _treeCts = null;
        _treeBinding = null;
        _treeParentTarget = null;
        _treeVersion++;
        _treeMessageSignature = null;
        _treeAgents.Clear();
        _treeChildren.Clear();
        _treeStatuses.Clear();
        _treeObservedSteps.Clear();
        _treeShells.Clear();
        _treeShellKinds.Clear();
        _treeDirectory = null;
        _treeHint = null;
        _treeScopeAgentId = null;
        _treeModels = null;
        _treeLoading = false;
        _treeChildrenAccepted = false;
        _treeReload = false;
        OnBrowserAgentTreeChanged();
        _agentTree.Reset();
        LeftAgentTreeSlot.Visibility = Visibility.Collapsed;
        RefreshChildReturnButton();
        RenderSubagentCount(0);
    }

    partial void OnAgentTreeReset()
    {
        ResetAgentTree();
        // Reset is called before a new source/session has finished binding.
        DispatcherQueue.TryEnqueue(QueueAgentTreeRefresh);
    }

    partial void OnAgentTreeHistoryReset() => OnAgentTreeReset();

    partial void OnAgentTreeConnected()
    {
        QueuePersistentPtyInventoryRefresh(recheckCapability: true);
        // The stream is volatile. Reconnect discards pre-gap overrides so REST can repair them;
        // events received during the new read will populate fresh overrides and win again.
        _treeStatuses.Clear();
        _treeObservedSteps.Clear();
        QueueAgentTreeRefresh();
    }

    private void QueueAgentTreeRefresh()
    {
        if (_treeRefreshQueued) return;
        _treeRefreshQueued = true;
        RunAfterTranscriptFirstFrame(() =>
        {
            _treeRefreshQueued = false;
            _ = RefreshAgentTreeAsync();
        });
    }

    private async Task<T> TreeReadAsync<T>(TreeBinding binding, Func<Task<T>> read, CancellationToken ct)
    {
        Task<T>? pending = null;
        binding.Source.TryExecute(ct, () => { if (IsTreeCurrent(binding)) pending = read(); });
        if (pending is null) throw new OperationCanceledException(ct);
        return await pending;
    }

    private async Task RefreshAgentTreeAsync()
    {
        var binding = CaptureTreeBinding();
        if (binding is null) { ResetAgentTree(); return; }
        if (_treeBinding != binding)
        {
            ResetAgentTree();
            _treeBinding = binding;
            DispatcherQueue.TryEnqueue(() => RefreshRightPanelMetadata()); // Restore current PTY scope after the tree's generation reset.
            _treeCts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts!.Token, binding.Source.RevocationToken);
        }
        if (_treeLoading) { _treeReload = true; return; }
        _treeLoading = true;
        var ct = _treeCts!.Token;
        bool Current() => !ct.IsCancellationRequested && IsTreeCurrent(binding) && _treeBinding == binding;
        LeftAgentTreeSlot.Visibility = Visibility.Visible;
        try
        {
            bool SelectorCurrent() => ReferenceEquals(_agentSelectorSource, binding.Source)
                && _agentSelectorRevision == binding.Revision && _agentSelectorSessionId == binding.SessionId;
            if (SelectorCurrent()) await _agentSelector.InitialMetadataRead.ConfigureAwait(true);
            if (!Current()) return;
            var session = SelectorCurrent() && _agentSelector.CurrentSessionInfo is { } acceptedSession
                && acceptedSession.Id == binding.SessionId ? acceptedSession
                : await TreeReadAsync(binding, () => binding.Client.GetSessionInfoAsync(binding.SessionId, ct), ct);
            if (!Current()) return;
            _treeParentTarget = CreateVerifiedParentTarget(session, binding.SessionId);
            if (_treeParentTarget is not null)
            {
                _agentTree.ShowParentReturn(!_treeParentReturnBusy);
                RefreshChildReturnButton();
                return; // A child session replaces the entire tree card; do not load its own agent tree.
            }
            var directory = session.Location?.Directory;
            if (string.IsNullOrWhiteSpace(directory)) { _treeHint = "会话目录不可用"; RenderAgentTree(); return; }
            _treeDirectory = directory;
            var agents = SelectorCurrent() && _agentSelector.CurrentSessionInfo?.Location?.Directory == directory
                && _agentSelector.AgentCatalog is { } acceptedAgents ? acceptedAgents
                : (await TreeReadAsync(binding, () => binding.Client.ListAgentsAsync(directory, ct), ct)).Data ?? new();
            if (!Current()) return;
            _treeAgents.Clear();
            _treeAgents.AddRange(agents);
            var children = new Dictionary<string, OpenCodeSessionInfo>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var page = await TreeReadAsync(binding, () => binding.Client.ListSessionsAsync(new()
                {
                    ParentFilter = OpenCodeSessionParentFilter.Children, ParentId = binding.SessionId,
                    Limit = 100, Cursor = cursor, Order = cursor is null ? OpenCodeSessionOrders.Asc : null
                }, ct), ct);
                if (!Current()) return;
                foreach (var child in page.Data ?? new())
                {
                    if (child.Id is not { Length: > 0 } id || child.ParentId != binding.SessionId) continue;
                    // Session.model is a selection; only assistant.model proves the model actually used.
                    var projected = child with { Model = null };
                    try
                    {
                        var messages = await TreeReadAsync(binding, () => binding.Client.GetMessagesAsync(id,
                            new() { Type = "assistant", Limit = 1, Order = OpenCodeSessionOrders.Desc }, ct), ct);
                        if (!Current()) return;
                        var assistant = messages.Data?.FirstOrDefault(m => m.Type == "assistant");
                        projected = projected with { Model = assistant?.Model };
                    }
                    catch (OpenCodeException) { /* Child remains navigable; model is honestly unavailable. */ }
                    children[id] = projected;
                }
                cursor = page.Cursor?.Next;
                if (cursor is not null && !cursors.Add(cursor)) throw new OpenCodeProtocolException("子会话分页游标重复。");
            } while (cursor is not null);
            if (!Current()) return;
            _treeChildren.Clear();
            foreach (var pair in children)
                _treeChildren[pair.Key] = _treeObservedSteps.TryGetValue(pair.Key, out var step)
                    ? pair.Value with { Agent = step.Agent ?? pair.Value.Agent, Model = step.Model } : pair.Value;
            _treeChildrenAccepted = true;
            OnBrowserAgentTreeChanged();
            _treeHint = children.Values.Any(s => !_treeAgents.Any(a => a.Id == s.Agent))
                ? "部分子会话的智能体不在当前有效配置中" : null;
            try
            {
                var shellVersion = _treeVersion;
                var shells = await TreeReadAsync(binding, () => binding.Client.ListTreeShellsAsync(directory, ct), ct);
                if (!Current()) return;
                foreach (var info in shells.Data ?? new())
                    if (OpenCodeAgentTreeProjection.Shell(info, binding.SessionId) is { } shell
                        && (shellVersion == _treeVersion || !_treeShells.ContainsKey(shell.Id)))
                        MergeAgentTreeShell(shell);
                // /shell lists running commands only. Absence does not prove completion.
                foreach (var id in _treeShells.Where(p => p.Value.Status == OpenCodeTreeStatus.Running).Select(p => p.Key).ToArray())
                {
                    var before = _treeVersion;
                    try
                    {
                        var info = await TreeReadAsync(binding, () => binding.Client.GetTreeShellAsync(id, directory, ct), ct);
                        if (!Current()) return;
                        if (before == _treeVersion && OpenCodeAgentTreeProjection.Shell(info, binding.SessionId) is { } shell) MergeAgentTreeShell(shell);
                    }
                    catch (OpenCodeException)
                    {
                        if (Current() && before == _treeVersion && _treeShells.TryGetValue(id, out var unavailable))
                            _treeShells[id] = unavailable with { Status = OpenCodeTreeStatus.Unknown };
                    }
                }
            }
            catch (OpenCodeException) { if (Current()) _treeHint = "后台终端列表不可用"; }
            if (Current()) { RenderAgentTree(); ResumeTerminalView(); }
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { if (Current()) { _treeHint = "智能体或子会话读取不可用"; RenderAgentTree(); } }
        finally
        {
            if (_treeBinding == binding)
            {
                _treeLoading = false;
                if (_treeReload) { _treeReload = false; QueueAgentTreeRefresh(); }
            }
        }
    }

    private void RenderAgentTree()
    {
        if (_treeBinding is not { } binding || !IsTreeCurrent(binding)) return;
        OnBrowserAgentTreeChanged();
        if (_treeParentTarget is not null)
        {
            _agentTree.ShowParentReturn(!_treeParentReturnBusy);
            return;
        }
        var statuses = new Dictionary<string, OpenCodeTreeStatus>(_treeStatuses, StringComparer.Ordinal);
        foreach (var id in _treeChildren.Keys)
            if (!statuses.ContainsKey(id) && _liveSessionStatuses.TryGetValue(id, out var live) && live.Working)
                statuses[id] = OpenCodeTreeStatus.Running;
        _treeModels = _agentSelector.ModelChoices;
        var agents = OpenCodeAgentTreeProjection.Agents(_treeAgents, _treeChildren.Values, binding.SessionId, statuses, AgentTreeScope(), _treeModels);
        RenderSubagentCount(agents.Sum(agent => agent.Children.Count(child => child.Status == OpenCodeTreeStatus.Running)));
        _agentTree.Render(agents, _treeShells.Values.ToArray(), _treeHint);
        SyncShellTerminalEntries();
    }

    // Native delegation rules of the composer's primary agent; null (show all) until a primary is known.
    private Func<string, bool>? AgentTreeScope()
    {
        _treeScopeAgentId = ComposerMentionAgentId();
        var primary = _treeAgents.FirstOrDefault(a => a.Id == _treeScopeAgentId && a.Mode != OpenCodeAgentModes.Subagent);
        return primary is null ? null : id => OpenCodeAgentMentionScope.AllowsMention(primary, id);
    }

    private void RefreshAgentTreeScope()
    {
        if (_treeBinding is not null && _treeParentTarget is null
            && (ComposerMentionAgentId() != _treeScopeAgentId || !ReferenceEquals(_treeModels, _agentSelector.ModelChoices))) RenderAgentTree();
    }

    private async Task ReturnFromAgentTreeAsync()
    {
        if (_treeParentReturnBusy || _treeBinding is not { } binding || !IsTreeCurrent(binding)
            || _treeParentTarget is not { } parent) return;
        _treeParentReturnBusy = true;
        _agentTree.ShowParentReturn(false);
        RefreshChildReturnButton();
        try
        {
            // The current GET/session's verified direct parent, never a sidebar or visited-origin guess.
            InstallSessionReturnContext(parent);
            await ReturnToSessionAsync();
        }
        finally
        {
            _treeParentReturnBusy = false;
            if (IsTreeCurrent(binding)) _agentTree.ShowParentReturn(true);
            RefreshChildReturnButton();
        }
    }

    partial void OnAgentTreeActivityChanged()
    {
        // The source-wide active snapshot repairs a missed completion, including unopened children.
        foreach (var child in _treeChildren.Values)
            if (child.Id is { } id && _liveSessionStatuses.TryGetValue(id, out var live) && !live.Working
                && _treeStatuses.GetValueOrDefault(id) == OpenCodeTreeStatus.Running)
                _treeStatuses[id] = OpenCodeAgentTreeProjection.Outcome(child.Outcome);
        RenderAgentTree();
    }

    partial void OnAgentTreeMessages(IReadOnlyList<OpenCodeMessage> messages)
    {
        if (_treeBinding is not { } binding || !IsTreeCurrent(binding)) { QueueAgentTreeRefresh(); return; }
        var signature = AgentTreeMessageSignature(messages);
        if (_treeMessageSignature == signature) return;
        _treeMessageSignature = signature;
        var changed = false;
        foreach (var message in messages)
        {
            if (OpenCodeAgentTreeProjection.Shell(message) is { } shell) changed |= MergeAgentTreeShell(shell);
            // Native tool input/metadata identifies background shell jobs; no parsing of prose.
            foreach (var part in message.Content ?? new())
            {
                if (OpenCodeAgentTreeProjection.Shell(part) is { } projected) changed |= MergeAgentTreeShell(projected);
                if (part.Type != "tool" || OpenCodeToolStates.Read(part.State) is not { } state) continue;
                var id = OpenCodeAgentTreeProjection.String(state.Metadata, "shellID");
                if (id is null) continue;
                var background = OpenCodeAgentTreeProjection.Boolean(state.Input, "background")
                    ?? OpenCodeAgentTreeProjection.Boolean(state.Metadata, "background");
                // The verified shell tool returns metadata.status=running when detached, including
                // manual backgrounding. A completed tool call is not a completed shell job.
                var detached = OpenCodeAgentTreeProjection.String(state.Metadata, "status") == "running"
                    && state.Status == "completed";
                var kind = background == true || detached ? OpenCodeTreeShellKind.Background
                    : background == false || part.Name == "shell" ? OpenCodeTreeShellKind.Foreground : OpenCodeTreeShellKind.Unknown;
                _treeShellKinds[id] = kind;
                if (_treeShells.TryGetValue(id, out var toolShell) && kind != OpenCodeTreeShellKind.Unknown && toolShell.Kind != kind)
                {
                    _treeShells[id] = toolShell with { Kind = kind };
                    changed = true;
                }
            }
        }
        if (changed) RenderAgentTree();
    }

    private static string AgentTreeMessageSignature(IReadOnlyList<OpenCodeMessage> messages)
    {
        var signature = new StringBuilder();
        foreach (var message in messages)
        {
            if (message.Type is "shell")
            {
                signature.Append("m:").Append(message.Id).Append(':').Append(message.ShellId).Append(':')
                    .Append(message.Command).Append(':').Append(message.Status).Append(':').Append(message.Exit?.GetRawText())
                    .Append(':').Append(message.Metadata?.GetRawText()).Append(':').Append(message.Output?.Output).Append(';');
            }
            else if (message.Type is "synthetic" or "system"
                && OpenCodeAgentTreeProjection.String(message.Metadata, "source") == "shell")
            {
                signature.Append("n:").Append(message.Id).Append(':').Append(message.Description).Append(':')
                    .Append(message.Metadata?.GetRawText()).Append(':').Append(message.Text).Append(':').Append(message.Output?.Output).Append(';');
            }

            foreach (var part in message.Content ?? new())
            {
                if (part.Type != "tool" || OpenCodeToolStates.Read(part.State) is not { } state
                    || OpenCodeAgentTreeProjection.String(state.Metadata, "shellID") is not { } shellId) continue;
                signature.Append("p:").Append(message.Id).Append(':').Append(part.Id).Append(':').Append(shellId).Append(':')
                    .Append(part.Name).Append(':').Append(part.State?.GetRawText()).Append(';');
            }
        }
        return signature.ToString();
    }

    private bool MergeAgentTreeShell(OpenCodeTreeShell shell)
    {
        if (_treeShells.TryGetValue(shell.Id, out var previous))
            shell = shell with { Cwd = shell.Cwd ?? previous.Cwd, Output = previous.Completed && !shell.Completed ? previous.Output ?? shell.Output : shell.Output ?? previous.Output,
                Completed = shell.Completed || previous.Completed,
                Command = shell.Command == shell.Id ? previous.Command : shell.Command,
                Kind = shell.Kind == OpenCodeTreeShellKind.Unknown ? previous.Kind : shell.Kind,
                Status = shell.Status == OpenCodeTreeStatus.Unknown
                    || shell.Status == OpenCodeTreeStatus.Running && previous.Status is OpenCodeTreeStatus.Succeeded or OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted
                    ? previous.Status : shell.Status,
                Exit = shell.Exit ?? previous.Exit };
        if (_treeShellKinds.TryGetValue(shell.Id, out var kind) && kind != OpenCodeTreeShellKind.Unknown) shell = shell with { Kind = kind };
        _treeShells[shell.Id] = shell;
        return shell != previous;
    }

    partial void OnAgentTreeEvent(OpenCodeEvent item)
    {
        if (_treeBinding is not { } binding || !IsTreeCurrent(binding)) return;
        var id = item.SessionId;
        if (item.Type == "session.created" && item.Data.TryGetProperty("info", out var sessionInfo)
            && sessionInfo.Deserialize<OpenCodeSessionInfo>() is { Id: { Length: > 0 } childId } created
            && created.ParentId == binding.SessionId)
        {
            _treeChildren[childId] = created;
            RenderAgentTree();
        }
        if (item.Type == "session.deleted" && id is not null && _treeChildren.Remove(id)) RenderAgentTree();
        if (item.Type is "agent.updated" or "config.updated" or "session.created" or "session.deleted"
            || item.Type == "session.agent.selected") QueueAgentTreeRefresh();
        if (id is not null)
        {
            var status = item.Type switch
            {
                "session.execution.started" or "session.step.started" or "session.retry.scheduled" => OpenCodeTreeStatus.Running,
                "session.execution.succeeded" => OpenCodeTreeStatus.Succeeded,
                "session.execution.failed" => OpenCodeTreeStatus.Failed,
                "session.execution.interrupted" => OpenCodeTreeStatus.Interrupted,
                "session.idle" => OpenCodeTreeStatus.Unknown,
                _ => (OpenCodeTreeStatus?)null
            };
            if (item.Type == "session.status" && item.Data.TryGetProperty("status", out var liveStatus))
                status = OpenCodeAgentTreeProjection.String(liveStatus, "type") switch
                {
                    "running" or "busy" or "retry" => OpenCodeTreeStatus.Running,
                    "idle" => OpenCodeTreeStatus.Unknown,
                    _ => status
                };
            if (status.HasValue && !(status == OpenCodeTreeStatus.Unknown && _treeStatuses.GetValueOrDefault(id) is OpenCodeTreeStatus.Succeeded or OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted))
                _treeStatuses[id] = status.Value;
            if (item.Type == "session.step.started")
            {
                var agent = OpenCodeAgentTreeProjection.String(item.Data, "agent");
                var model = item.Data.TryGetProperty("model", out var raw) ? raw.Deserialize<OpenCodeModelRef>() : null;
                _treeObservedSteps[id] = (agent, model);
                if (_treeChildren.TryGetValue(id, out var child))
                    _treeChildren[id] = child with { Agent = agent ?? child.Agent, Model = model };
            }
            if (status.HasValue) RenderAgentTree();
            if (status == OpenCodeTreeStatus.Running && id != binding.SessionId && !_treeChildren.ContainsKey(id)) QueueAgentTreeRefresh();
        }
        if (item.Type == "shell.created" && item.Data.TryGetProperty("info", out var info))
        {
            var dto = info.Deserialize<OpenCodeTreeShellInfo>();
            if (dto is not null && OpenCodeAgentTreeProjection.Shell(dto, binding.SessionId) is { } shell)
            {
                _treeVersion++;
                MergeAgentTreeShell(shell);
                RenderAgentTree();
            }
        }
        else if (item.Type == "shell.exited" && OpenCodeAgentTreeProjection.String(item.Data, "id") is { } shellId
            && _treeShells.TryGetValue(shellId, out var previous))
        {
            double? exit = item.Data.TryGetProperty("exit", out var number) && number.TryGetDouble(out var value) ? value : null;
            _treeVersion++;
            _treeShells[shellId] = previous with { Status = OpenCodeAgentTreeProjection.ShellStatus(OpenCodeAgentTreeProjection.String(item.Data, "status"), exit), Exit = exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            RenderAgentTree();
        }
        // session.shell.* is owned by the message reducer (B8); accepted messages feed the projection above.
    }

    private async Task OpenAgentTreeChildAsync(string id)
    {
        var binding = _treeBinding;
        // Both tree items and timeline calls use this route. A just-created call need not have
        // reached the tree cache yet; the real session's ParentId below is the ownership check.
        if (binding is null || !IsTreeCurrent(binding) || _subagentNavigationBusy) return;
        _subagentNavigationBusy = true;
        var ct = _treeCts!.Token;
        try
        {
            var child = await TreeReadAsync(binding, () => binding.Client.GetSessionInfoAsync(id, ct), ct);
            if (!IsTreeCurrent(binding) || ct.IsCancellationRequested) return;
            if (child.ParentId != binding.SessionId) { ShowMessage(InfoBarSeverity.Warning, "无法确认子会话归属", "该子会话不再属于当前会话。"); return; }
            DismissSidebarDrawers();
            SelectSessionItem(id);
            await SwitchToSessionAsync(id, new SessionReturnTarget(binding.SessionId, SessionReturnKind.VisitedOrigin));
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { if (IsTreeCurrent(binding)) ShowMessage(InfoBarSeverity.Warning, "子会话读取不可用", "请稍后重试。"); }
        finally { _subagentNavigationBusy = false; }
    }

}
