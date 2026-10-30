using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // The tree's own return button lives in the left drawer. A child session hides the composer, and with it the
    // narrow-layout drawer toggle, so whenever that drawer is not resident and open the footer carries the same action.
    internal static bool ShowChildFooterReturn(bool childSession, bool hasVerifiedParent, bool drawerResidentAndOpen)
        => childSession && hasVerifiedParent && !drawerResidentAndOpen;

    private void InitializeChildReturn()
    {
        SessionPane.RegisterPropertyChangedCallback(SplitView.IsPaneOpenProperty, (_, _) => RefreshChildReturnButton());
        SessionPane.RegisterPropertyChangedCallback(SplitView.DisplayModeProperty, (_, _) => RefreshChildReturnButton());
    }

    private void RefreshChildReturnButton()
    {
        RefreshChildBrowserButton();
        var verified = _treeParentTarget is not null && _treeBinding is { } binding && IsTreeCurrent(binding);
        var drawer = SessionPane.DisplayMode == SplitViewDisplayMode.Inline && SessionPane.IsPaneOpen;
        ChildReturnButton.Visibility = ShowChildFooterReturn(IsChildComposerSession && _pageActive && !_sessionMissing, verified, drawer)
            ? Visibility.Visible : Visibility.Collapsed;
        ChildReturnButton.IsEnabled = !_treeParentReturnBusy;
    }

    // Same verified-parent route as the tree button: no separate navigation or ancestry guess.
    private void ChildReturnButton_Click(object sender, RoutedEventArgs e) => _ = ReturnFromAgentTreeAsync();
}
