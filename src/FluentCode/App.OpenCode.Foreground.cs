using FluentCode.Services.Notifications;

namespace FluentCode;

public partial class App
{
    /// <summary>UI thread publishes current visibility; background native dispatch reads only
    /// the service's locked immutable scope. Old page/source callbacks cannot replace a new owner.</summary>
    internal void UpdateOpenCodeForegroundSession(object owner, OpenCodePageSource? source, string? session, bool foreground)
    {
        RequireAppThread();
        lock (_integrationSync)
        {
            var current = foreground && !_closing && source is { IsRevoked: false }
                && ReferenceEquals(source, _openCodePageSource);
            if (foreground && !current) return;
            ApplySessionUiViewedSession(owner, source, session, current);
            _ = _completionNotifications?.UpdateOpenCodeForegroundSession(owner, source?.Identity, session, current);
        }
        PublishSessionUiChanges();
    }
}
