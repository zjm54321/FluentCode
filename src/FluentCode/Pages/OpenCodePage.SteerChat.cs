using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private MessageHistoryRead? _steerReceiptBinding;
    private readonly Dictionary<string, OpenCodeInboxItem> _steerReceipts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _steerReceiptsSeenInInbox = new(StringComparer.Ordinal);
    // Local admission proves ownership even before/after pending-ack reconciliation; it never proves pending state.
    private readonly HashSet<string> _ownedInboxReceipts = new(StringComparer.Ordinal);

    private void ClearSteerReceipts()
    {
        _steerReceiptBinding = null;
        _steerReceipts.Clear();
        _steerReceiptsSeenInInbox.Clear();
        _ownedInboxReceipts.Clear();
    }

    private bool OwnsAdmittedInboxItem(string inboxId)
        => _steerReceiptBinding is { } binding && IsCurrentMessageHistoryRead(binding) && _ownedInboxReceipts.Contains(inboxId);

    private void ForgetInboxReceipt(string inboxId)
    {
        _steerReceipts.Remove(inboxId);
        _steerReceiptsSeenInInbox.Remove(inboxId);
        _ownedInboxReceipts.Remove(inboxId);
    }

    // Called by the real durable admission publisher, after the existing operation/source checks.
    // This is an inbox-shaped VIEW projection only: raw history, retry and execution stay unchanged.
    partial void OnComposerPromptAdmitted(string sessionId, OpenCodeInboxUser receipt)
    {
        if (!_pageActive || !SourceReady || _client is not { } client || _selectedSessionId != sessionId
            || receipt.SessionId != sessionId || receipt.Type != OpenCodeInboxItemTypes.User
            || receipt.Delivery is not (OpenCodeDeliveryModes.Steer or OpenCodeDeliveryModes.Queue)
            || receipt.Id is not { Length: > 0 } id) return;
        if (_steerReceiptBinding is { } old && !IsCurrentMessageHistoryRead(old)) ClearSteerReceipts();
        _steerReceiptBinding = CaptureMessageHistoryRead(client, sessionId);
        _ownedInboxReceipts.Add(id);
        if (receipt.Delivery != OpenCodeDeliveryModes.Steer) return; // Queue receipts provide ownership only, not a chat projection.
        _steerReceipts[id] = new OpenCodeInboxItem
        {
            Id = id, SessionId = receipt.SessionId, Time = receipt.Time, Type = receipt.Type, Delivery = receipt.Delivery,
            Payload = receipt.Payload is null ? null : JsonSerializer.SerializeToElement(receipt.Payload, OpenCodeJson.Options),
        };
        UpdateQueuedPrompts();
    }

    private IReadOnlyList<OpenCodeInboxItem>? ProjectSteerReceipts(IReadOnlyList<OpenCodeInboxItem>? accepted)
    {
        if (_steerReceiptBinding is not { } binding || !IsCurrentMessageHistoryRead(binding))
        {
            ClearSteerReceipts();
            return accepted;
        }
        var result = accepted?.ToList() ?? new List<OpenCodeInboxItem>();
        _ownedInboxReceipts.RemoveWhere(_conversationView.ContainsRenderedMessage);
        var byId = new HashSet<string>(result.Where(item => item.Id is not null).Select(item => item.Id!), StringComparer.Ordinal);
        foreach (var pair in _steerReceipts.ToArray())
        {
            if (byId.Contains(pair.Key)) _steerReceiptsSeenInInbox.Add(pair.Key);
            if (_conversationView.ContainsRenderedMessage(pair.Key)
                || _steerReceiptsSeenInInbox.Contains(pair.Key) && !byId.Contains(pair.Key))
            {
                _steerReceipts.Remove(pair.Key);
                _steerReceiptsSeenInInbox.Remove(pair.Key);
            }
            else if (!byId.Contains(pair.Key)) result.Add(pair.Value);
        }
        return result;
    }
}
