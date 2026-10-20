using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Services.Notifications;

public sealed record NotificationScope(NotificationSource Source, string EndpointIdentity, string SessionId);
public sealed record NotificationItemKey(NotificationScope Scope, NotificationNavigationKind Kind, string ItemId);

/// <summary>Local read state only. RemovedCount means successful Notification Center removal calls,
/// not banner dismissal or a backend read receipt. MissingNativeIdCount could not be removed.</summary>
public sealed record NotificationRemovalResult(int AcknowledgedCount, int RemovalAttemptCount,
    int RemovedCount, int FailedCount, int MissingNativeIdCount);

internal sealed record NotificationNativeReceipt(uint Id, string Tag, string Group);

/// <summary>Pure bounded display policy, separate from action and navigation capabilities.
/// Owner serializes all access; snapshots contain only existing entries, never future notifications.</summary>
internal sealed class NotificationDisplayLedger(TimeProvider clock)
{
    internal sealed class Entry(NotificationItemKey key, NotificationNativeReceipt native, Guid generation,
        long sequence, DateTimeOffset expires)
    {
        internal NotificationItemKey Key { get; } = key;
        internal NotificationNativeReceipt Native { get; } = native;
        internal Guid SourceGeneration { get; } = generation;
        internal long Sequence { get; } = sequence;
        internal DateTimeOffset Expires { get; } = expires;
        internal bool Unread { get; set; } = true;
        internal bool Removed { get; set; }
    }

    private readonly List<Entry> _entries = new();
    private long _sequence;

    internal void Add(NotificationItemKey key, NotificationNativeReceipt native, Guid generation)
    {
        Prune();
        while (_entries.Count >= CompletionNotificationService.MaxDedupeEntries) _entries.RemoveAt(0);
        _entries.Add(new(key, native, generation, ++_sequence, clock.GetUtcNow() + CompletionNotificationService.DedupeWindow));
    }

    internal (Entry[] Entries, int Acknowledged) Snapshot(Func<Entry, bool> predicate, bool acknowledge)
    {
        Prune();
        var selected = _entries.Where(predicate).ToArray();
        int acknowledged = 0;
        if (acknowledge)
            foreach (var entry in selected)
            {
                if (entry.Unread) acknowledged++;
                entry.Unread = false;
            }
        return (selected.Where(e => !e.Removed).ToArray(), acknowledged);
    }

    internal void MarkRemoved(Entry entry) => entry.Removed = true;
    internal void Clear() => _entries.Clear();
    private void Prune() => _entries.RemoveAll(e => e.Expires <= clock.GetUtcNow());
}
