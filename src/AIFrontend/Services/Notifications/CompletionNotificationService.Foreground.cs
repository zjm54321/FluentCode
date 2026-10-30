using System;
using System.Threading.Tasks;

namespace AIFrontend.Services.Notifications;

public sealed partial class CompletionNotificationService
{
    private sealed record ForegroundSession(object Owner, NotificationScope Scope, Guid Generation);
    private ForegroundSession? _foregroundSession;

    /// <summary>UI owner publishes its current selected/visible session. No XAML is read by
    /// notification workers. The same lock serializes updates with the final native dispatch.</summary>
    internal Task<NotificationRemovalResult> UpdateOpenCodeForegroundSession(object owner, string? endpoint, string? session, bool foreground)
    {
        ArgumentNullException.ThrowIfNull(owner);
        NotificationDisplayLedger.Entry[] entries;
        int acknowledged;
        lock (_gate)
        {
            if (!foreground)
            {
                if (ReferenceEquals(_foregroundSession?.Owner, owner)) _foregroundSession = null;
                return Task.FromResult(new NotificationRemovalResult(0, 0, 0, 0, 0));
            }
            if (_interactionsSuspended || _activationDisposing || _status == NotificationServiceStatus.Disposed
                || endpoint is null || session is null || !NotificationArgumentCodec.IsValidRequestId(session)
                || !_notificationSources.TryGetValue(endpoint, out var source))
                return Task.FromResult(new NotificationRemovalResult(0, 0, 0, 0, 0));
            var scope = new NotificationScope(NotificationSource.OpenCode, endpoint, session);
            var current = new ForegroundSession(owner, scope, source.Generation);
            if (_foregroundSession == current) return Task.FromResult(new NotificationRemovalResult(0, 0, 0, 0, 0));
            _foregroundSession = current;
            // Local foreground consumption, NOT a server viewed watermark or settled permission.
            // Do not retire actions, resolve uncertain mutations or acknowledge a child session.
            (entries, acknowledged) = _displayLedger.Snapshot(e => e.SourceGeneration == source.Generation && e.Key.Scope == scope, true);
        }
        return RemoveSnapshotAsync(entries, acknowledged);
    }

    private bool IsForegroundOpenCodeSessionLocked(string endpoint, string session)
        => _foregroundSession is { } foreground && foreground.Scope == new NotificationScope(NotificationSource.OpenCode, endpoint, session)
        && _notificationSources.TryGetValue(endpoint, out var source) && foreground.Generation == source.Generation;
}
