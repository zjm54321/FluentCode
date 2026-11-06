using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Notifications;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private CancellationTokenSource? _eventCts;
    private TaskCompletionSource? _eventReady;
    private OpenCodeEventReducer? _eventReducer;
    private readonly ConcurrentQueue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _eventQueue = new();
    private readonly Queue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _workflowEventQueue = new();
    private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new(StringComparer.Ordinal);
    private long _liveStatusVersion;
    private bool _eventProjectionDirty;
    private bool _statusRefreshRunning;
    private bool _eventSnapshotBeforeReady;
    private bool _eventReconciliationPending;

    partial void OnBrowserEventSyncStarting();
    partial void OnBrowserEventSyncStopped();
    partial void OnBrowserEventStreamHealthChanged(bool connected);
    partial void OnBrowserControlEvent(OpenCodeEvent item);
    partial void RefreshAgentSelectorDisplay();

    // Capture connection identity as well as readiness: a disconnect/reconnect during an action
    // cannot retroactively cover its missed interval. Boundary/gap repair is never suppressed.
    private Task? CaptureHealthyEventStream()
        => SourceReady && _eventCts is { IsCancellationRequested: false }
            && _eventReady?.Task.IsCompletedSuccessfully == true && !_eventReconciliationPending
            ? _eventReady.Task : null;

    private bool HasContinuousEventCoverage(Task? coverage)
        => coverage is not null && ReferenceEquals(coverage, CaptureHealthyEventStream())
            && !_messageHistoryNeedsState;

    private void StopEventSync()
    {
        ResetChildInteractions();
        _eventProjectionDirty = false;
        OnBrowserEventSyncStopped();
        OnAgentTreeReset();
        ResetRuntimeFeedback();
        _eventCts?.Cancel();
        _eventCts = null;
        _eventReady = null;
        _eventSnapshotBeforeReady = false;
        _eventReconciliationPending = false;
        while (_eventQueue.TryDequeue(out _)) { }
        _workflowEventQueue.Clear();
        _liveSessionStatuses.Clear();
        RefreshSessionStateUi();
        RefreshComposerSessionActivity();
        RefreshConversationSessionActivity();
    }

    private void StartEventSync()
    {
        if (_client is not { } client || _source is not { } source || _pageCts is null || !SourceReady) return;
        _eventCts?.Cancel();
        while (_eventQueue.TryDequeue(out _)) { }
        _workflowEventQueue.Clear();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token, source.RevocationToken);
        _eventCts = cts;
        _eventReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _eventSnapshotBeforeReady = false;
        _eventReconciliationPending = false;
        OnBrowserEventSyncStarting();
        _ = Task.Run(() => RunEventSyncAsync(client, source, cts));
    }

    private void RestartSessionEventSync(string sessionId)
    {
        ResetChildInteractions();
        ResetRuntimeFeedback();
        _eventReducer = new(sessionId);
        StartEventSync();
    }

    partial void OnEventMessageHistoryReset()
    {
        _workflowEventQueue.Clear(); // Buffered pre-revert/handoff events cannot cross the retired epoch.
        ResetRuntimeFeedback();
        _eventReducer = _selectedSessionId is { } id ? new(id) : null;
    }

    /// <summary>REST may paint before the SSE handshake. Remember the uncovered interval for one late-ready repair.</summary>
    private void PrepareEventSnapshot()
    {
        DrainEventQueue(); // Caller captures the pre-drain version so buffered events also beat a stale snapshot.
        if (_eventCts is null) return;
        if (_eventReady?.Task.IsCompleted != true) _eventSnapshotBeforeReady = true;
        if (_eventReconciliationPending)
        {
            _eventReconciliationPending = false;
            _messageHistoryNeedsState = true; // Gap may contain a model/revert boundary, not just text.
            InvalidateViewAcknowledgement();
        }
    }

    private void OnEventStreamConnected(CancellationTokenSource owner, bool reconnect)
    {
        if (owner.IsCancellationRequested || !ReferenceEquals(owner, _eventCts) || !_pageActive || !SourceReady) return;
        _eventReady?.TrySetResult();
        OnBrowserEventStreamHealthChanged(true);
        var repair = reconnect || _eventSnapshotBeforeReady;
        _eventSnapshotBeforeReady = false;
        if (repair)
        {
            RefreshAgentSelectorDisplay(); // A missed interval can contain selection changes from another client.
            _eventReconciliationPending = true;
            // Preserve the existing reconnect boundary fence. First readiness must NOT hide the initial paint.
            if (reconnect)
            {
                _messageHistoryNeedsState = true;
                InvalidateViewAcknowledgement();
            }
            if (_messageHistoryWorkflowOwner is null) _ = RefreshMessagesAsync();
        }
        _ = RefreshListedSessionStatusesAsync();
        QueueChildInteractionDiscovery();
        OnAgentTreeConnected();
    }

    private void OnEventStreamDisconnected(CancellationTokenSource owner)
    {
        if (owner.IsCancellationRequested || !ReferenceEquals(owner, _eventCts)) return;
        // Readiness belongs to this connection, not the first handshake of the subscription loop.
        _eventReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OnBrowserEventStreamHealthChanged(false);
    }

    partial void OnMessageHistoryWorkflowEnded()
    {
        DrainEventQueue(); // Publish buffered events only after the send/handoff ownership fence ends.
        if (_eventReconciliationPending && _eventCts is { IsCancellationRequested: false }
            && _pageActive && SourceReady) _ = RefreshMessagesAsync();
    }

    private static async IAsyncEnumerable<OpenCodeEvent> ReadSourceEventsAsync(OpenCodeClient client,
        OpenCodePageSource source, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var reader = client.SubscribeEventsAsync(ct).GetAsyncEnumerator(ct);
        Task<bool>? first = null;
        // Async iterators are lazy: MoveNext (not creation of the enumerable) starts the HTTP request.
        source.TryExecute(ct, () => first = reader.MoveNextAsync().AsTask());
        if (first is null) yield break;
        if (!await first.ConfigureAwait(false)) yield break;
        do { yield return reader.Current; }
        while (await reader.MoveNextAsync().ConfigureAwait(false));
    }

    private async Task RunEventSyncAsync(OpenCodeClient client, OpenCodePageSource source, CancellationTokenSource owner)
    {
        var ct = owner.Token;
        var backoff = 1;
        var connectedOnce = false;
        var applyScheduled = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var readyInConnection = false;
                    await foreach (var item in ReadSourceEventsAsync(client, source, ct).ConfigureAwait(false))
                    {
                        if (item.Type == "server.connected")
                        {
                            if (readyInConnection) continue; // Duplicate readiness frames are not reconnects.
                            readyInConnection = true;
                            backoff = 1;
                            var reconnect = connectedOnce;
                            DispatcherQueue.TryEnqueue(() => OnEventStreamConnected(owner, reconnect));
                            connectedOnce = true;
                            continue;
                        }
                        _eventQueue.Enqueue((owner, item));
                        if (Interlocked.Exchange(ref applyScheduled, 1) == 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(100, ct).ConfigureAwait(false);
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        Interlocked.Exchange(ref applyScheduled, 0);
                                        if (!ct.IsCancellationRequested && ReferenceEquals(owner, _eventCts)) DrainEventQueue();
                                    });
                                }
                                catch (OperationCanceledException) { }
                            }, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is OpenCodeException or HttpRequestException or IOException or OperationCanceledException)
                {
                    Debug.WriteLine($"OpenCode.SSE reconnect delay_s={backoff} error={ex.GetType().Name}");
                }
                DispatcherQueue.TryEnqueue(() => OnEventStreamDisconnected(owner));
                await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false);
                backoff = Math.Min(backoff * 2, 15);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void DrainEventQueue()
    {
        if (!_pageActive || !SourceReady) return;
        var drainStarted = Stopwatch.GetTimestamp();
        var drainedCount = 0;
        var changed = false;
        var refetch = false;
        var refreshSessionList = false;
        var statusesChanged = false;
        var selectedStatusChanged = false;
        var selectionChanged = false;
        var first = DateTimeOffset.MaxValue;
        var count = 0;
        bool Next(out (CancellationTokenSource Owner, OpenCodeEvent Event) queued)
        {
            if (_messageHistoryWorkflowOwner is null && _workflowEventQueue.TryDequeue(out queued)) return true;
            return _eventQueue.TryDequeue(out queued);
        }
        while (Next(out var queued))
        {
            drainedCount++;
            if (!ReferenceEquals(queued.Owner, _eventCts) || queued.Owner.IsCancellationRequested) continue;
            var item = queued.Event;
            OnChildInteractionEvent(item);
            if (item.Type is "persistent-pty.added" or "persistent-pty.removed" or "persistent-pty.updated")
            {
                OnPersistentPtyEvent(item); // Separate server-scoped inventory, including during message workflows.
                continue;
            }
            var backgroundCompletion = IsSelectedBackgroundChildCompletion(item);
            if (_messageHistoryWorkflowOwner is not null && (item.SessionId is null || item.SessionId == _selectedSessionId || backgroundCompletion))
            {
                // These events used to be discarded, forcing a post-action snapshot. Keep FIFO
                // ownership instead; a source/session replacement clears or rejects old events.
                _workflowEventQueue.Enqueue(queued);
                continue;
            }
            OnBrowserControlEvent(item); // Before selected-session reducer filtering; includes background tabs.
            OnAgentTreeEvent(item);
            count++;
            if (item.ReceivedAt < first) first = item.ReceivedAt;
            var id = item.SessionId;
            var repeatedStatusOnly = false;
            selectionChanged |= id == _selectedSessionId && item.Type is "session.agent.selected" or "session.model.selected" or "session.location.selected";
            if (id == _selectedSessionId && item.Type is "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted"
                && _eventReducer?.IsCurrentAssistantEvent(item) == false) continue;
            OnSessionInsightsEvent(item);
            refreshSessionList |= item.Type is "session.created" or "session.deleted" or "session.renamed";
            if (id is not null)
            {
                var old = _liveSessionStatuses.GetValueOrDefault(id);
                // V2 has native lifecycle events, NOT the legacy session.status API.
                var runningEvent = item.Type is "session.execution.started" or "session.step.started" or "session.retry.scheduled"
                    || item.Type.StartsWith("session.text.", StringComparison.Ordinal)
                    || item.Type.StartsWith("session.reasoning.", StringComparison.Ordinal)
                    || item.Type.StartsWith("session.tool.", StringComparison.Ordinal);
                var terminalEvent = item.Type is "session.idle" or "session.execution.interrupted" or "session.execution.failed" or "session.execution.succeeded";
                var working = runningEvent ? true
                    : item.Type is "session.idle" or "session.execution.interrupted" or "session.execution.failed" or "session.execution.succeeded" ? false : old.Working;
                if (item.Type == "session.status" && item.Data.TryGetProperty("status", out var status)
                    && status.TryGetProperty("type", out var kind))
                {
                    var statusType = kind.GetString();
                    runningEvent = statusType is "running" or "busy" or "retry";
                    terminalEvent = statusType == "idle";
                    if (runningEvent || terminalEvent) working = runningEvent;
                }
                if (runningEvent || terminalEvent)
                {
                    _liveSessionStatuses[id] = (working, ++_liveStatusVersion);
                    // Advance the REST race fence for every event, but don't republish identical UI state.
                    var statusChanged = old.Version == 0 || old.Working != working;
                    repeatedStatusOnly = !statusChanged && item.Type == "session.status";
                    statusesChanged |= statusChanged;
                    selectedStatusChanged |= statusChanged && id == _selectedSessionId;
                }
            }
            if (id is not null && id != _selectedSessionId)
            {
                // Child execution ends on its own session. Its launch part need not change, and
                // healthy SSE disables polling: fetch the parent's keyed completion/result once
                // per batch instead of leaving the background row's historical running metadata.
                refetch |= backgroundCompletion && !repeatedStatusOnly;
                continue;
            }
            // The same origin/history gates used by REST publication also fence streamed publication.
            if (_messageHistoryWorkflowOwner is not null) continue;
            changed |= _eventReducer?.Apply(item) == true;
            var direct = item.Type.StartsWith("session.text.", StringComparison.Ordinal)
                || item.Type.StartsWith("session.reasoning.", StringComparison.Ordinal)
                || item.Type.StartsWith("session.tool.", StringComparison.Ordinal);
            if (!direct && !repeatedStatusOnly && (item.Type.StartsWith("session.", StringComparison.Ordinal)
                || item.Type.StartsWith("permission.", StringComparison.Ordinal) || item.Type.StartsWith("form.", StringComparison.Ordinal)
                || item.Type.StartsWith("inbox.", StringComparison.Ordinal) || item.Type.StartsWith("todo.", StringComparison.Ordinal))) refetch = true;
            if (item.Type is "session.revert.staged" or "session.revert.cleared" or "session.revert.committed") _messageHistoryNeedsState = true;
        }
        if (_hostWindowVisible && (changed || selectedStatusChanged)) RefreshCurrentRetry();
        if (statusesChanged && _hostWindowVisible)
        {
            RefreshSessionStateUi();
        }
        if (selectedStatusChanged && _hostWindowVisible)
        {
            RefreshComposerSessionActivity();
            RefreshConversationSessionActivity();
        }
        if (refreshSessionList) _ = RefreshSessionsAsync();
        if (selectionChanged) RefreshAgentSelectorDisplay();
        _eventProjectionDirty |= changed;
        if (_eventProjectionDirty && _hostWindowVisible && _eventReducer is not null && _selectedSessionId is { } selected
            && _messageHistoryWorkflowOwner is null && !_messageHistoryNeedsState && !_sessionMissing)
        {
            _eventProjectionDirty = false;
            var follow = IsScrolledToBottom();
            var changes = _eventReducer.TakeChanges();
            var changedMessages = changes.Updates.Select(update => update.Message).ToArray();
            var incremental = changes.KnownTextPartsOnly && !_eventReconciliationPending && _messageHistoryBoundaryKnown
                && _messageRevertBoundary is null && !_isLoadingOlderMessages && _latestMessages.Count == _eventReducer.MessageCount
                && changes.Updates.All(update => update.Index >= 0 && update.Index < _latestMessages.Count
                    && _latestMessages[update.Index].Id == update.Message.Id)
                && _conversationView.TryApplyMessageChanges(changedMessages, _eventReducer, changes.BaseVersion, changes.Version);
            IReadOnlyList<OpenCodeMessage> window;
            if (incremental)
            {
                foreach (var update in changes.Updates) _latestMessages[update.Index] = update.Message;
                OnContextMessagesAccepted(_latestMessages, changedMessages);
                window = _latestMessages;
            }
            else
            {
                _latestMessages.Clear();
                _latestMessages.AddRange(_eventReducer.Messages);
                var history = MergeMessageHistory();
                // Merge appends the latest window unchanged; equal counts mean no older rows were added.
                var historyIsLatestWindow = history.Count == _latestMessages.Count;
                var acceptedHistory = ProjectMessageHistory(history);
                EnsureConversationImages();
                _conversationView.ApplyMessages(acceptedHistory);
                _conversationView.BindIncrementalSnapshot(_eventReducer, changes.Version);
                OnContextMessagesAccepted(acceptedHistory);
                window = historyIsLatestWindow ? acceptedHistory : ProjectMessageHistory(new List<OpenCodeMessage>(_latestMessages));
            }
            RefreshConversationSessionActivity();
            ObserveExecutionState(selected, window);
            NotifyViewAcknowledgementRendered(window, _hostWindowVisible);
            UpdateEmptyState();
            if (follow) ScrollToBottom(animated: false);
            Debug.WriteLine($"OpenCode.SSE apply received_utc={first:O} applied_utc={DateTimeOffset.UtcNow:O} latency_ms={(DateTimeOffset.UtcNow - first).TotalMilliseconds:F1} events={count}");
        }
        if (refetch) _ = RefreshMessagesAsync();
        OpenCodeEventDrainMetrics.Record(drainedCount, Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds);
    }

    private bool IsSelectedBackgroundChildCompletion(OpenCodeEvent item)
    {
        if (item.SessionId is not { } id || id == _selectedSessionId) return false;
        var terminal = item.Type is "session.idle" or "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted"
            || item.Type == "session.status" && item.Data.TryGetProperty("status", out var status)
                && status.TryGetProperty("type", out var kind) && kind.GetString() == "idle";
        if (!terminal) return false; // Never scan history for foreign text/tool deltas.
        if (_treeBinding is { } binding && IsTreeCurrent(binding)
            && _treeChildren.TryGetValue(id, out var child) && child.ParentId == _selectedSessionId) return true;
        if (_historyUnresolvedChildren.Contains(id)) return true;
        // Covers manual backgrounding and a child completing before the tree REST read finishes.
        return ProjectMessageHistory(MergeMessageHistory()).Any(message => message.Type == "assistant"
            && message.Content?.Any(part => part.Type == "tool" && part.Name == "subagent"
                && OpenCodeToolStates.ReadPart(part)?.Metadata is { ValueKind: JsonValueKind.Object } metadata
                && metadata.TryGetProperty("sessionID", out var target) && target.ValueKind == JsonValueKind.String
                && target.GetString() == id) == true);
    }

    partial void ApplyLiveSessionStatuses(Dictionary<string, OpenCodeSessionUiState> states)
    {
        foreach (var (id, value) in _liveSessionStatuses)
        {
            states.TryGetValue(id, out var previous);
            states[id] = new(id, value.Working, previous?.PendingAction, previous?.MutationProtected ?? false,
                previous?.Completion, previous?.ViewedWatermark, OpenCodeStateUnknownReason.None)
                { QuestionPending = previous?.QuestionPending };
        }
    }

    partial void OnAgentTreeEvent(OpenCodeEvent item);
    partial void OnSessionInsightsEvent(OpenCodeEvent item);
    partial void OnAgentTreeConnected();
    partial void OnAgentTreeReset();
    partial void OnAgentTreeActivityChanged();

    partial void RefreshConversationSessionActivity()
    {
        if (!_pageActive || !SourceReady || _sessionMissing) return;
        _conversationView.SetCurrentTurnActivity(_selectedSessionId is { } id
            && _liveSessionStatuses.TryGetValue(id, out var live) ? live.Working : null);
        if (_selectedSessionId is { } selected && _liveSessionStatuses.TryGetValue(selected, out var state) && !state.Working)
            _eventReducer?.ClearCurrentRetry();
        RefreshCurrentRetry();
    }

    private async Task RefreshListedSessionStatusesAsync()
    {
        if (_statusRefreshRunning || _client is not { } client || _source is not { } source || _pageCts is null || !SourceReady) return;
        _statusRefreshRunning = true;
        var generation = _pageGeneration;
        var version = _liveStatusVersion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token, source.RevocationToken);
        var ct = linked.Token;
        try
        {
            // One authoritative foreground snapshot covers every session, including unopened rows.
            Task<OpenCodeActiveSessionsResponse>? pending = null;
            source.TryExecute(ct, () => pending = client.GetActiveSessionsAsync(ct));
            if (pending is null) return;
            var response = await pending;
            if (ct.IsCancellationRequested || generation != _pageGeneration || !ReferenceEquals(client, _client)
                || !ReferenceEquals(source, _source) || source.Revision != _sourceRevision || !_pageActive) return;
            foreach (var id in _sessionItems.Keys.Concat(response.Data!.Keys).Concat(_liveSessionStatuses.Keys)
                .Concat(_selectedSessionId is { } selected ? new[] { selected } : Array.Empty<string>()).Distinct(StringComparer.Ordinal))
            {
                if (_liveSessionStatuses.TryGetValue(id, out var live) && live.Version > version) continue;
                if (response.Data.TryGetValue(id, out var active) && active.Type != "running") continue;
                _liveSessionStatuses[id] = (response.Data.ContainsKey(id), ++_liveStatusVersion);
            }
            RefreshSessionStateUi();
            RefreshComposerSessionActivity();
            RefreshConversationSessionActivity();
            OnAgentTreeActivityChanged();
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { /* Keep last successful foreground observation; retry at fallback. */ }
        finally
        {
            _statusRefreshRunning = false;
        }
    }
}
