namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    // UI-only work. The app-owned notification monitor remains independent of this lifecycle.
    partial void OnHostUiVisibilityChanged(bool visible)
    {
        if (visible && _pageActive)
        {
            if (_eventProjectionDirty) DrainEventQueue();
            RefreshConversationSessionActivity();
            RefreshComposerSessionActivity();
            RefreshSessionStateUi();
            _homeChromeTimer?.Start();
            _pollTimer?.Start();
        }
        else
        {
            _homeChromeTimer?.Stop();
            _pollTimer?.Stop();
        }
        _browserStrip?.SetHostVisible(visible && _pageActive);
    }
}
