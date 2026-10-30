using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Narrow layout: the 概况 footer card (working directory + OpenCode version · dot · ›) moves into the composer's
    // green connection dot. The very same Directory/Footer elements are re-parented (never duplicated), like
    // MoveExistingSidebarActions; wide layout restores them to the right dock card.
    private readonly StackPanel _composerOverviewHost = new() { Spacing = 6, Width = 280 };
    private Flyout? _composerOverviewFlyout;
    private bool _composerOverviewInDot;

    internal static string ComposerOverviewTip(string? directory, string version, bool connected)
        => (string.IsNullOrEmpty(directory) ? string.Empty : directory + "\n") + "OpenCode " + version + (connected ? " · 已连接" : string.Empty);

    private void InitializeComposerOverview()
    {
        _composerOverviewFlyout = new Flyout { Content = _composerOverviewHost, Placement = FlyoutPlacementMode.TopEdgeAlignedRight };
    }

    private void MoveRightFooterForLayout(bool wide)
    {
        if (_rightPanelMetadata is not { } view || _composerOverviewFlyout is null) return;
        var inDot = !wide;
        if (inDot == _composerOverviewInDot) return;
        _composerOverviewFlyout.Hide();
        ConnectionFlyout.Hide();
        _composerOverviewInDot = inDot;
        if (inDot)
        {
            RightPanelDirectorySlot.Children.Remove(view.Directory);
            RightPanelFooterSlot.Children.Remove(view.Footer);
            _composerOverviewHost.Children.Add(view.Directory);
            _composerOverviewHost.Children.Add(view.Footer);
        }
        else
        {
            _composerOverviewHost.Children.Clear();
            RightPanelDirectorySlot.Children.Add(view.Directory);
            RightPanelFooterSlot.Children.Add(view.Footer);
        }
        RefreshRightPanelFooter();
    }

    private void OpenRightPanelConnectionDetails()
    {
        if (_rightPanelMetadata is not { } view) return;
        if (!_composerOverviewInDot) { ConnectionFlyout.ShowAt(view.Footer); return; }
        // From the dot's overview: swap to the details flyout anchored on the same dot (no nested popup).
        _composerOverviewFlyout?.Hide();
        ConnectionFlyout.ShowAt(ComposerConnectionButton);
    }

    private void RefreshComposerOverviewTip(string? directory, string version, bool connected)
    {
        var tip = ComposerOverviewTip(directory, version, connected);
        ToolTipService.SetToolTip(ComposerConnectionButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ComposerConnectionButton, "概况：" + tip.Replace('\n', ' '));
    }
}
