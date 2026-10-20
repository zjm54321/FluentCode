using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using Microsoft.Windows.AppNotifications;

namespace AIFrontend.Services.Notifications;

public sealed partial class CompletionNotificationService
{
    private readonly NotificationDisplayLedger _displayLedger;
    private readonly Dictionary<string, TrustedOpenCodeNotificationSource> _notificationSources = new(StringComparer.Ordinal);
    private readonly Func<uint, Task> _removeNotification;
    private readonly Func<NotificationInteractionPlan, NotificationNativeReceipt> _submitInteraction;

    /// <summary>Marks only the exact session's current display entries read under the service lock,
    /// then removes their native IDs outside it. Never retires actions/navigation, changes the gate,
    /// ACKs children, or sends a backend receipt. Failure does not restore unread state.</summary>
    public Task<NotificationRemovalResult> AcknowledgeSessionAsync(NotificationSource source, string endpointIdentity, string sessionId)
    {
        if (source is not (NotificationSource.LocalApi or NotificationSource.OpenCode)
            || !NotificationArgumentCodec.IsValidRequestId(sessionId)
            || (source == NotificationSource.LocalApi ? endpointIdentity != LocalCompletionEndpointIdentity
                : !NotificationArgumentCodec.IsValidRequestId(endpointIdentity)))
            throw new ArgumentException("Invalid notification session scope.");
        (NotificationDisplayLedger.Entry[] Entries, int Acknowledged) snapshot;
        lock (_gate)
            snapshot = _displayLedger.Snapshot(e => e.Key.Scope == new NotificationScope(source, endpointIdentity, sessionId), true);
        return RemoveSnapshotAsync(snapshot.Entries, snapshot.Acknowledged);
    }

    internal bool TryBindNotificationSource(TrustedOpenCodeNotificationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_activationDisposing || _status == NotificationServiceStatus.Disposed) return false;
            if (_notificationSources.TryGetValue(source.Identity, out var current)) return ReferenceEquals(current, source);
            if (_notificationSources.Count >= MaxDedupeEntries || !_interactions.TryBindSource(source)) return false;
            _notificationSources.Add(source.Identity, source);
            return true;
        }
    }

    internal bool IsCurrentNotificationSource(TrustedOpenCodeNotificationSource source)
    {
        lock (_gate) return !_interactionsSuspended && !_activationDisposing && _status == NotificationServiceStatus.Initialized
            && _notificationSources.TryGetValue(source.Identity, out var current) && ReferenceEquals(current, source);
    }

    /// <summary>Fresh same-source server view covers known idle completions only. It never retires
    /// permission/form actions or their pending displays; those require a fresh complete pending read.</summary>
    internal Task<NotificationRemovalResult> ReconcileViewedCompletionsAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, double viewed, IReadOnlyList<OpenCodeMessage> freshMessages)
    {
        if (!double.IsFinite(viewed) || !NotificationArgumentCodec.IsValidRequestId(sessionId))
            throw new ArgumentException("Invalid server view watermark.");
        var covered = freshMessages.Where(m => m is not null && m.Type == "idle" && m.Outcome == "succeeded"
            && NotificationArgumentCodec.IsValidRequestId(m.Id) && m.Time is not null
            && double.IsFinite(m.Time.Created) && m.Time.Created <= viewed).Select(m => m.Id!).ToHashSet(StringComparer.Ordinal);
        (NotificationDisplayLedger.Entry[] Entries, int Acknowledged) snapshot;
        lock (_gate)
        {
            if (!IsCurrentNotificationSource(source)) return Task.FromResult(new NotificationRemovalResult(0, 0, 0, 0, 0));
            snapshot = _displayLedger.Snapshot(e => e.SourceGeneration == source.Generation
                && e.Key.Scope == new NotificationScope(NotificationSource.OpenCode, source.Identity, sessionId)
                && e.Key.Kind == NotificationNavigationKind.Completion && covered.Contains(e.Key.ItemId), true);
        }
        return RemoveSnapshotAsync(snapshot.Entries, snapshot.Acknowledged);
    }

    /// <summary>Only a successful complete same-owner pending query may call this method. Failed/incomplete
    /// reads, gaps or budget exhaustion MUST NOT be represented as empty sets. Rechecks live source before
    /// action retirement. Does not resolve any gate OutcomeUnknown or permission whole-session protection.</summary>
    internal Task<NotificationRemovalResult> ReconcilePendingAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, NotificationNavigationKind kind, IReadOnlySet<string> pendingIds)
    {
        ArgumentNullException.ThrowIfNull(pendingIds);
        if (kind is not (NotificationNavigationKind.Permission or NotificationNavigationKind.Form)
            || !NotificationArgumentCodec.IsValidRequestId(sessionId) || pendingIds.Count > MaxDedupeEntries
            || pendingIds.Any(id => !NotificationArgumentCodec.IsValidRequestId(id)))
            throw new ArgumentException("Invalid complete pending snapshot.");
        // Freeze caller-owned collections before crossing the native-removal boundary.
        var pending = pendingIds.ToHashSet(StringComparer.Ordinal);
        NotificationDisplayLedger.Entry[] entries;
        lock (_gate)
        {
            if (!IsCurrentNotificationSource(source) || !_interactions.ReconcilePending(source, sessionId, kind, pending))
                return Task.FromResult(new NotificationRemovalResult(0, 0, 0, 0, 0));
            entries = _displayLedger.Snapshot(e => e.SourceGeneration == source.Generation
                && e.Key.Scope == new NotificationScope(NotificationSource.OpenCode, source.Identity, sessionId)
                && e.Key.Kind == kind && !pending.Contains(e.Key.ItemId), false).Entries;
        }
        return RemoveSnapshotAsync(entries, 0);
    }

    // A fresh form detail or definitive activation result settles only this item, not the whole pending set.
    internal Task<NotificationRemovalResult> ReconcileSettledAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, NotificationNavigationKind kind, string itemId)
    {
        if (kind is not (NotificationNavigationKind.Permission or NotificationNavigationKind.Form)
            || !NotificationArgumentCodec.IsValidRequestId(sessionId) || !NotificationArgumentCodec.IsValidRequestId(itemId))
            throw new ArgumentException("Invalid settled item.");
        NotificationDisplayLedger.Entry[] entries;
        lock (_gate)
        {
            if (!IsCurrentNotificationSource(source)) return Task.FromResult(new NotificationRemovalResult(0, 0, 0, 0, 0));
            _interactions.RetireSettled(source, sessionId, kind, itemId);
            entries = _displayLedger.Snapshot(e => e.SourceGeneration == source.Generation
                && e.Key == new NotificationItemKey(new(NotificationSource.OpenCode, source.Identity, sessionId), kind, itemId), false).Entries;
        }
        return RemoveSnapshotAsync(entries, 0);
    }

    private async Task<NotificationRemovalResult> RemoveSnapshotAsync(NotificationDisplayLedger.Entry[] entries, int acknowledged)
    {
        int attempted = 0, removed = 0, failed = 0, missing = 0;
        foreach (var entry in entries)
        {
            if (entry.Native.Id == 0) { missing++; continue; }
            attempted++;
            try
            {
                await _removeNotification(entry.Native.Id).ConfigureAwait(false);
                lock (_gate) _displayLedger.MarkRemoved(entry);
                removed++;
            }
            catch { failed++; } // Counts only; never log native payloads or undo read/action retirement.
        }
        return new(acknowledged, attempted, removed, failed, missing);
    }

    private static async Task RemoveNativeNotificationAsync(uint id)
        => await AppNotificationManager.Default.RemoveByIdAsync(id);

    private static NotificationNativeReceipt ShowNativeNotification(AppNotification notification)
    {
        notification.Tag = Guid.NewGuid().ToString("N");
        notification.Group = Guid.NewGuid().ToString("N");
        AppNotificationManager.Default.Show(notification);
        return new(notification.Id, notification.Tag, notification.Group);
    }

    private void RecordDisplayLocked(NotificationNavigationTarget target, NotificationNativeReceipt receipt, Guid generation)
        => _displayLedger.Add(new(new(target.Source, target.EndpointIdentity, target.SessionId), target.Kind, target.RequestId), receipt, generation);
}
