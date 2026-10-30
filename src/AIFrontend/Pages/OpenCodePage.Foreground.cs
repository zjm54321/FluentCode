using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Opaque owner token: the service never retains/reads a XAML object on a worker thread.
    private readonly object _foregroundNotificationOwner = new();
    private bool _foregroundTrackingInitialized, _publishedForegroundVisible;
    private OpenCodePageSource? _publishedForegroundSource;
    private string? _publishedForegroundSession;

    partial void UpdateForegroundNotificationSession()
    {
        if (!DispatcherQueue.HasThreadAccess) return;
        if (!_foregroundTrackingInitialized)
        {
            _foregroundTrackingInitialized = true;
            // Self-owned subscriptions also cover silent tab changes and collapsed cached pages.
            SessionTabs.SelectionChanged += (_, _) => UpdateForegroundNotificationSession();
            SizeChanged += (_, _) => UpdateForegroundNotificationSession();
            RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateForegroundNotificationSession());
        }
        var selected = SessionTabs.SelectedItem as TabViewItem;
        var session = selected?.Tag as string;
        var foreground = _hostWindowVisible && _pageActive && IsLoaded && Visibility == Visibility.Visible
            && XamlRoot is not null && ActualWidth > 0 && ActualHeight > 0 && !_draftRecoveryPending
            && _source is { IsRevoked: false } && session is not null
            && _sessionTabs.TryGetValue(session, out var entry) && ReferenceEquals(entry.Item, selected);
        if (ReferenceEquals(_publishedForegroundSource, _source) && _publishedForegroundSession == session
            && _publishedForegroundVisible == foreground) return;
        _publishedForegroundSource = _source;
        _publishedForegroundSession = session;
        _publishedForegroundVisible = foreground;
        if (Application.Current is App app)
            app.UpdateOpenCodeForegroundSession(_foregroundNotificationOwner, _source, session, foreground);
    }
}
