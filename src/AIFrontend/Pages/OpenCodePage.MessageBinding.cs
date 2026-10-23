using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

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
        _conversationView.ClearMessages();
        ResetMessagePaginationState();
        if (_source is { IsRevoked: false } source && _client is { } client)
            _messageBinding = new(source, _sourceRevision, _sourceGeneration, _pageGeneration, client, sessionId, directory);
        _firstMessagesLoading = true; // Before any observable binding callback / empty-state update.
        RestoreMessageWindow();
        UpdateEmptyState();
    }

    private void RetainMessageWindow()
    {
        if (!_messageWindowAccepted || _messageBinding is not { Directory: not null } binding
            || binding.Source.IsRevoked || !_messageHistoryBoundaryKnown || _messageHistoryNeedsState
            || _messageRevertBoundary is not null || _messageHistoryWorkflowOwner is not null) return;
        var rows = _olderMessages.Count + _latestMessages.Count;
        if (rows == 0 || rows > RetainedMessageRows) return;
        // Count serialized payload without keeping a second message/byte buffer. Oversized windows
        // stay fully available for live pagination but are not retained across bindings.
        var budget = new MessageWindowBudget();
        try
        {
            System.Text.Json.JsonSerializer.Serialize(budget, _olderMessages);
            System.Text.Json.JsonSerializer.Serialize(budget, _latestMessages);
        }
        catch (MessageWindowOverBudgetException) { return; }
        var bytes = budget.Length;
        _retainedMessageWindows.RemoveAll(slot => slot.Binding == binding);
        _retainedMessageWindows.Add(new(binding, _olderMessages, _latestMessages, _messageNextCursor,
            _messageChainState, new(_messageUsedCursors, StringComparer.Ordinal), _messageCursorWarningShown, _messageHistoryGap, bytes));
        while (_retainedMessageWindows.Count > RetainedMessageBindings
            || _retainedMessageWindows.Sum(slot => slot.Older.Count + slot.Latest.Count) > RetainedMessageRows
            || _retainedMessageWindows.Sum(slot => slot.Bytes) > RetainedMessageBytes)
            _retainedMessageWindows.RemoveAt(0);
    }

    private sealed class MessageWindowOverBudgetException : Exception { }
    private sealed class MessageWindowBudget : System.IO.Stream
    {
        private long _length;
        public override long Length => _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            _length += count;
            if (_length > RetainedMessageBytes) throw new MessageWindowOverBudgetException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
        _messageHistoryBoundaryKnown = true; // Saved windows have no rollback boundary.
        // Visual only: no reducer, execution observation, context publication or ACK.
        EnsureConversationImages();
        var visibleHistory = MergeMessageHistory();
        _conversationView.ApplyMessages(visibleHistory);
        NotifyViewAcknowledgementRendered(visibleHistory, _hostWindowVisible);
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
