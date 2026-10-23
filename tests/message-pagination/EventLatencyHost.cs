// Transport readiness and the dispatcher entry are controllable boundaries. The production
// RefreshMessagesAsync, PrepareEventSnapshot, OnEventStreamConnected, DrainEventQueue and
// active-session REST publisher execute verbatim; there is no CompletedTask readiness stub.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;
public sealed partial class OpenCodePage
{
    // Agent-tree UI is outside this source-extracted pagination fixture. Preserve optional hooks.
    partial void OnAgentTreeEvent(OpenCodeEvent item);
    partial void OnAgentTreeReset();
    partial void OnAgentTreeConnected();
    partial void OnAgentTreeActivityChanged();
    private CancellationTokenSource? _eventCts;
    private TaskCompletionSource? _eventReady;
    private bool _eventSnapshotBeforeReady, _eventReconciliationPending, _statusRefreshRunning;
    private readonly CancellationTokenSource _pageCts = new();
    private readonly ConcurrentQueue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _eventQueue = new();
    private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new();
    private long _liveStatusVersion;
    private readonly Dictionary<string, object> _sessionItems = new();
    internal int ActivityRefreshes;
    private void RefreshComposerSessionActivity() => ActivityRefreshes++;
    private void RefreshSessionStateUi() { }
    private Task RefreshSessionsAsync() => Task.CompletedTask;

    internal sealed record StreamBoundary(CancellationTokenSource Owner, TaskCompletionSource Ready);
    internal StreamBoundary TestBeginEvents()
    {
        _eventCts?.Cancel();
        _eventCts = new();
        _eventReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _eventReducer = new(_selectedSessionId!);
        _eventReducer.MergeSnapshot(_latestMessages, 0);
        _eventSnapshotBeforeReady = _eventReconciliationPending = false;
        _sessionItems[_selectedSessionId!] = new();
        return new(_eventCts, _eventReady);
    }
    internal void TestReady(StreamBoundary stream, bool reconnect = false)
    {
        stream.Ready.TrySetResult(); // The transport sets this before posting the actual UI callback.
        OnEventStreamConnected(stream.Owner, reconnect);
    }
    internal void TestReceive(StreamBoundary stream, OpenCodeEvent item)
    {
        _eventQueue.Enqueue((stream.Owner, item));
        DrainEventQueue();
    }
    internal void TestBuffer(StreamBoundary stream, OpenCodeEvent item) => _eventQueue.Enqueue((stream.Owner, item));
    internal Task TestActiveAsync() => RefreshListedSessionStatusesAsync();
    internal bool? TestForeground => _selectedSessionId is { } id && _liveSessionStatuses.TryGetValue(id, out var s) ? s.Working : null;
    internal bool TestRefreshBusy => _isRefreshing;
    internal bool TestReadyRepairPending => _eventReconciliationPending;
    internal bool? TestTurnActivity => _conversationView.TurnActivity;
    internal int TestTurnPublications => _conversationView.ActivityPublications;
    internal void TestUnlistSelected() => _sessionItems.Remove(_selectedSessionId!);
    internal void TestRequireBoundary() => _messageHistoryNeedsState = true;
    internal void TestSetStreamBoundary(string? boundary) => _messageRevertBoundary = boundary;
}
