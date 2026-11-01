using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    internal Image ThemeSmokeHomeWordmark => _homeWordmark;
    internal double ThemeSmokePageWidth => PageRoot.ActualWidth;
    internal bool ThemeSmokeIsWide => _sidebarLayout.IsWide;
    internal Button ThemeSmokeFooter => _rightPanelMetadata?.Footer
        ?? throw new InvalidOperationException("Theme refresh regression: real page footer was not initialized");

    // Isolated, unconfigured native renderer fixture only. Exercise the real Home/dock hosts without
    // creating a client, endpoint, session or replacement wordmark. Production configuration policy is unchanged.
    internal void ShowThemeSmokeSurface(bool home)
    {
        if (Environment.GetEnvironmentVariable("FC_THEME_REFRESH_TEST") != "1"
            || Environment.GetEnvironmentVariable("FC_STARTUP_SMOKE_ROOT") is not { Length: > 0 }
            || SourceReady || _source is not null)
            throw new InvalidOperationException("Theme refresh fixture requires an isolated unconfigured page");

        NotConfiguredPanel.Visibility = Visibility.Collapsed;
        WorkspaceGrid.Visibility = Visibility.Visible;
        _homeActive = home;
        if (!VisualStateManager.GoToState(this, home ? "HomeLayout" : "SessionLayout", false))
            throw new InvalidOperationException("Theme refresh regression: real page layout state is missing");
        PageRoot.UpdateLayout();
        _sidebarLayout = _sidebarLayout.Resize(PageRoot.ActualWidth);
        ApplySidebarLayout();
        PageRoot.UpdateLayout();
    }

    internal FrameworkElement ShowThemeSmokeNarrowFooter()
    {
        if (Environment.GetEnvironmentVariable("FC_THEME_REFRESH_TEST") != "1"
            || _sidebarLayout.IsWide || !_composerOverviewInDot || _composerOverviewFlyout is null
            || !_composerOverviewHost.Children.Contains(ThemeSmokeFooter))
            throw new InvalidOperationException($"Theme refresh regression: real narrow footer was not moved into its overview flyout: wide={_sidebarLayout.IsWide}, inDot={_composerOverviewInDot}, child={_composerOverviewHost.Children.Contains(ThemeSmokeFooter)}, parent={ThemeSmokeFooter.Parent?.GetType().Name}");
        _composerOverviewFlyout.ShowAt(ComposerConnectionButton);
        return _composerOverviewHost;
    }

    internal void MountThemeSmokeNarrowFooter(Canvas renderHost)
    {
        if (Environment.GetEnvironmentVariable("FC_THEME_REFRESH_TEST") != "1"
            || _sidebarLayout.IsWide || !_composerOverviewHost.IsLoaded || _composerOverviewFlyout is null
            || !_composerOverviewHost.Children.Contains(ThemeSmokeFooter))
            throw new InvalidOperationException("Theme refresh regression: the production narrow flyout must be loaded before capture");
        _composerOverviewFlyout.Hide();
        _composerOverviewFlyout.Content = null;
        Canvas.SetLeft(_composerOverviewHost, 24); Canvas.SetTop(_composerOverviewHost, 320);
        renderHost.Children.Add(_composerOverviewHost);
    }

    internal void RestoreThemeSmokeNarrowFooter(Canvas renderHost)
    {
        renderHost.Children.Remove(_composerOverviewHost);
        _composerOverviewFlyout!.Content = _composerOverviewHost;
    }
}
