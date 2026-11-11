using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    // Page-owned visual windows, not execution/permission/ACK state. Lists change owners, not copies.
    private const int RetainedMessageBindings = 3;
    private const int RetainedMessageRows = 600;
    private const long RetainedMessageBytes = 2 * 1024 * 1024;
    private sealed record MessageBinding(OpenCodePageSource Source, long Revision, int SourceGeneration,
        int PageGeneration, OpenCodeClient Client, string SessionId, string? Directory);
    private sealed record MessageWindow(MessageBinding Binding, List<OpenCodeMessage> Older,
        List<OpenCodeMessage> Latest, string? Next, MessagePaginationChainState Chain,
        HashSet<string> Used, bool Warning, bool Gap, long Bytes);
    private readonly List<MessageWindow> _retainedMessageWindows = new();
    private MessageBinding? _messageBinding;
    private bool _messageWindowAccepted;
    private bool _firstMessagesLoading;
    private bool _messagesResponseEmpty;
    private string? _messagesLoadError;
    internal long MessageWindowSerializationCount { get; private set; }
    private long _messageWindowEstimatedBytes;

    private sealed class MessageRefreshOwner
    {
        internal MessageHistoryRead Read;
        internal MessageRefreshOwner(MessageHistoryRead read) => Read = read;
    }
    private MessageRefreshOwner? _messageRefreshOwner;

    private bool OwnsMessageRefresh(MessageRefreshOwner owner)
        => ReferenceEquals(_messageRefreshOwner, owner) && IsCurrentMessageHistoryRead(owner.Read);

    private void RevokeMessageRefresh()
    {
        _messageRefreshOwner = null;
        _isRefreshing = false;
        _refreshQueued = _refreshQueuedInitial = _refreshQueuedForceFollow = _refreshQueuedExplicit = false;
    }

    private void ResetHistoryWithinRefresh(string? boundary, bool keepChildEvidence = true)
    {
        var owner = _messageRefreshOwner;
        var queued = (_refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit);
        if (!keepChildEvidence) _historyUnresolvedChildren = System.Collections.Immutable.ImmutableArray<string>.Empty;
        ResetAcceptedHistoryProjection(boundary);
        // A synchronous authoritative correction changes this read's epoch, not its refresh owner.
        _messageRefreshOwner = owner;
        _isRefreshing = owner is not null;
        (_refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit) = queued;
    }

    private string? KnownSessionDirectory(string sessionId)
    {
        var directory = _sessionItems.TryGetValue(sessionId, out var info)
            ? info.Location?.Directory : null;
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    private bool IsSameMessageBinding(string sessionId, string? directory)
        => directory is not null && _messageBinding is { } binding && binding.Directory == directory
        && binding.SessionId == sessionId && ReferenceEquals(binding.Source, _source)
        && binding.Source is { IsRevoked: false } && binding.Revision == _sourceRevision
        && binding.SourceGeneration == _sourceGeneration && binding.PageGeneration == _pageGeneration
        && ReferenceEquals(binding.Client, _client);

    private void BeginSessionMessageBinding(string sessionId, string? directory)
    {
        RetainMessageWindow();
        _messageBinding = null; // Reset must not evict the outgoing transferred window.
        _olderMessages = new();
        _latestMessages = new();
        _messageWindowEstimatedBytes = 0;
        _conversationView.ClearMessages();
        ResetMessagePaginationState();
        if (_source is { IsRevoked: false } source && _client is { } client)
            _messageBinding = new(source, _sourceRevision, _sourceGeneration, _pageGeneration, client, sessionId, directory);
        _firstMessagesLoading = true; // Before any observable binding callback / empty-state update.
        RestoreMessageWindow();
        UpdateEmptyState();
    }

    private void ResumeMessageBinding()
    {
        // Same source/client only (the caller already ran ApplyCurrentSource). Rebase visual
        // ownership without clearing/reapplying rows or resetting the native ScrollViewer.
        if (_messageBinding is { } binding)
            _messageBinding = binding with { PageGeneration = _pageGeneration };
        for (var i = 0; i < _retainedMessageWindows.Count; i++)
        {
            var window = _retainedMessageWindows[i];
            _retainedMessageWindows[i] = window with { Binding = window.Binding with { PageGeneration = _pageGeneration } };
        }
    }

    private void RetainMessageWindow()
    {
        if (!_messageWindowAccepted || _messageBinding is not { Directory: not null } binding
            || binding.Source.IsRevoked || !_messageHistoryBoundaryKnown || _messageHistoryNeedsState
            || _messageRevertBoundary is not null || _messageHistoryWorkflowOwner is not null) return;
        var rows = _olderMessages.Count + _latestMessages.Count;
        if (rows == 0 || rows > RetainedMessageRows) return;
        // Acceptance maintains this estimate. Switching transfers buffers without serializing them.
        var bytes = _messageWindowEstimatedBytes;
        if (bytes > RetainedMessageBytes) return;
        _retainedMessageWindows.RemoveAll(slot => slot.Binding == binding);
        _retainedMessageWindows.Add(new(binding, _olderMessages, _latestMessages, _messageNextCursor,
            _messageChainState, new(_messageUsedCursors, StringComparer.Ordinal), _messageCursorWarningShown, _messageHistoryGap, bytes));
        while (_retainedMessageWindows.Count > RetainedMessageBindings
            || _retainedMessageWindows.Sum(slot => slot.Older.Count + slot.Latest.Count) > RetainedMessageRows
            || _retainedMessageWindows.Sum(slot => slot.Bytes) > RetainedMessageBytes)
            _retainedMessageWindows.RemoveAt(0);
    }

    private void UpdateMessageWindowBudget()
    {
        _messageWindowEstimatedBytes = 0;
        if (_olderMessages.Count + _latestMessages.Count > RetainedMessageRows)
        {
            _messageWindowEstimatedBytes = RetainedMessageBytes + 1;
            return;
        }
        foreach (var message in _olderMessages.Concat(_latestMessages))
        {
            if (message?.Id is not { Length: > 0 })
            {
                _messageWindowEstimatedBytes = RetainedMessageBytes + 1;
                return; // Invalid authoritative rows cannot become a retained visual window.
            }
            _messageWindowEstimatedBytes += _conversationView.EstimateMessageBytes(message);
            if (_messageWindowEstimatedBytes > RetainedMessageBytes) return;
        }
    }

    private void UpdateMessageWindowBudget(OpenCodeMessage previous, OpenCodeMessage accepted)
    {
        if (_messageWindowEstimatedBytes > RetainedMessageBytes) UpdateMessageWindowBudget();
        else _messageWindowEstimatedBytes += _conversationView.EstimateMessageBytes(accepted)
            - _conversationView.EstimateMessageBytes(previous);
    }

    private void RestoreMessageWindow()
    {
        _retainedMessageWindows.RemoveAll(slot => slot.Binding.Source.IsRevoked
            || !ReferenceEquals(slot.Binding.Source, _source) || slot.Binding.Revision != _sourceRevision
            || slot.Binding.SourceGeneration != _sourceGeneration || slot.Binding.PageGeneration != _pageGeneration
            || !ReferenceEquals(slot.Binding.Client, _client));
        if (_messageBinding is not { Directory: not null } binding) return;
        var index = _retainedMessageWindows.FindIndex(slot => slot.Binding == binding);
        if (index < 0) return;
        var window = _retainedMessageWindows[index];
        _retainedMessageWindows.RemoveAt(index);
        _olderMessages = window.Older;
        _latestMessages = window.Latest;
        _messageNextCursor = window.Next;
        _messageChainState = window.Chain;
        _messageUsedCursors.UnionWith(window.Used);
        _messageCursorWarningShown = window.Warning;
        _messageHistoryGap = window.Gap;
        _messageWindowEstimatedBytes = window.Bytes;
        _messageHistoryBoundaryKnown = true; // Saved windows have no rollback boundary.
        // Visual only: no reducer, execution observation, context publication or ACK.
        EnsureConversationImages();
        var visibleHistory = MergeMessageHistory();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _conversationView.ApplyMessages(visibleHistory);
        RecordRetainedSwitchApply(started);
        NotifyViewAcknowledgementRendered(visibleHistory, HostAttentionVisible);
        UpdateLoadOlderMessagesUi();
        UpdateEmptyState();
    }

    private void ResetMessageBindingState()
    {
        RevokeMessageRefresh();
        if (!_pageActive || _source is null || _source.IsRevoked)
        {
            _retainedMessageWindows.Clear();
            _messageBinding = null;
        }
        else if (_messageBinding is { } binding)
            _retainedMessageWindows.RemoveAll(slot => slot.Binding == binding);
        _messageWindowAccepted = false;
        _messageWindowEstimatedBytes = 0;
        _firstMessagesLoading = _selectedSessionId is not null;
        _messagesResponseEmpty = false;
        _messagesLoadError = null;
    }

    private void SetMessageLoadFailure(MessageRefreshOwner owner)
    {
        if (!OwnsMessageRefresh(owner)) return;
        _firstMessagesLoading = false;
        _messagesLoadError = "消息加载未完成，本页会自动重试。";
        UpdateEmptyState();
    }
}
