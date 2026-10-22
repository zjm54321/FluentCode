// Synthetic surrounding page services; the accepted REST/SSE publication methods are extracted
// verbatim by run.ps1. Real: producer bodies, reducer, insights partial/controller/view/ring.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Optional left-sidebar hooks are declared outside the extracted publication bodies.
    partial void OnAgentTreeEvent(OpenCodeEvent item);
    partial void OnAgentTreeReset();
    private CancellationTokenSource? _eventCts;
    #pragma warning disable CS0414 // StopEventSync clears this; subscription startup is outside this publication fixture.
    private TaskCompletionSource? _eventReady;
    private bool _eventSnapshotBeforeReady, _eventReconciliationPending;
    #pragma warning restore CS0414
    private OpenCodeEventReducer? _eventReducer;
    private readonly ConcurrentQueue<(CancellationTokenSource Owner, OpenCodeEvent Event)> _eventQueue = new();
    private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new();
    private long _liveStatusVersion;
    private readonly List<OpenCodeMessage> _latestMessages = new();
    private long? _messageHistoryWorkflowOwner => null;
    private bool _messageHistoryNeedsState;
    private enum MessagePaginationChainState { None, Active, Exhausted, Stopped }
    private MessagePaginationChainState _messageChainState;
    private int _messageChainGeneration;
    private readonly HashSet<string> _messageUsedCursors = new();
    #pragma warning disable CS0414 // Only assigned by the extracted producer; cursor UI is a labelled double.
    private bool _messageCursorWarningShown;
    #pragma warning restore CS0414
    private string? _messageNextCursor;

    partial void CaptureContextMetadataVersion(ref long version);
    partial void OnContextSessionMetadata(OpenCodeModelRef? model, string directory, long version);
    partial void OnContextMessagesAccepted(IReadOnlyList<OpenCodeMessage> messages);

    // Cursor/scroll/notifications are explicit doubles, not native or complete pagination evidence.
    private void NoteRefreshWindowGap(List<OpenCodeMessage> messages) { }
    private void PreserveRolledOffLatestMessages(List<OpenCodeMessage> messages) { }
    private void RestartMessagePagination(string? cursor)
    {
        _messageNextCursor = cursor;
        _messageChainState = cursor is null ? MessagePaginationChainState.Exhausted : MessagePaginationChainState.Active;
    }
    private void UpdateLoadOlderMessagesUi() { }
    private List<OpenCodeMessage> ProjectMessageHistory(List<OpenCodeMessage> messages) => messages;
    private bool IsScrolledToBottom() => true;
    private void ScrollToBottom(bool animated) { }
    private void RefreshSessionStateUi() { }
    private void RefreshComposerSessionActivity() { } // Activity presentation is outside the header fixture.
    private void InvalidateDraftSourceEpoch() { } // Draft storage is outside this fixture.
    private void ResetHomeSource() { } // Home surface is outside this fixture.
    private void RefreshCurrentRetry() { } // Runtime feedback is outside this fixture.
    private void RefreshConversationSessionActivity() { }
    private void EnsureConversationImages() { } // Markdown image loading is outside this fixture.
    private void ResetRuntimeFeedback() { }
    private Task RefreshSessionsAsync() => Task.CompletedTask;
    private Task RefreshMessagesAsync() => Task.CompletedTask;
    private void ObserveExecutionState(string session, IReadOnlyList<OpenCodeMessage> messages) { }
    private void NotifyViewAcknowledgementRendered(IReadOnlyList<OpenCodeMessage> messages, bool visible) { }
    private bool _hostWindowVisible => true;

    internal int MergeCalls;
    internal bool ContextSurfacesClosed => !InsightsPane.IsPaneOpen && !_insightsView.ContextRing.Flyout.IsOpen;
    internal void ContextMetadata(OpenCodeModelRef model)
    {
        long version = -1;
        CaptureContextMetadataVersion(ref version);
        OnContextSessionMetadata(model, "C:/work", version);
    }
    internal void AcceptRest(params OpenCodeMessage[] messages) => ApplyRefreshedMessages(messages.ToList(), null, false);
    internal CancellationTokenSource BeginStream()
    {
        _eventCts?.Cancel();
        _eventCts = new();
        _eventReducer = new(_selectedSessionId!);
        _eventReducer.MergeSnapshot(_latestMessages, 0);
        return _eventCts;
    }
    internal void AcceptEvent(OpenCodeEvent message, CancellationTokenSource? owner = null)
    {
        _eventQueue.Enqueue((owner ?? _eventCts!, message));
        DrainEventQueue();
    }
}
