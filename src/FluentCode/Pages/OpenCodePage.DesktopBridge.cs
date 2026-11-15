using System;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    // The hello answer of the installed source. The connection details show its state; nothing here gates a feature
    // (model favourites and Billion discovery ask the bridge themselves).
    private (OpenCodePageSource Source, OpenCodeClient Client, OpenCodeDesktopBridgeState State)? _desktopBridge;

    private OpenCodeDesktopBridgeState? CurrentDesktopBridge()
        => _desktopBridge is { } bridge && !bridge.Source.IsRevoked && ReferenceEquals(bridge.Source, _source)
            && ReferenceEquals(bridge.Client, _client) ? bridge.State : null;

    private async Task InitializeDesktopBridgeAsync(OpenCodePageSource source, OpenCodeClient client)
    {
        try
        {
            var state = await client.DesktopBridge.NegotiateAsync(client, null, source.RevocationToken);
            if (!_pageActive || source.IsRevoked || !ReferenceEquals(_source, source) || !ReferenceEquals(_client, client)) return;
            _desktopBridge = (source, client, state);
            RenderConnectionFlyoutIfOpen();
            ShowDesktopBridgeNoticeIfDue();
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException)
        {
            // Auth/network/protocol failures are not evidence of a missing plugin: the row stays "无法确认".
            // Dependent features retain their existing unavailable/error presentation.
        }
    }

    /// <summary>
    /// One notice per server and state in a run of the app (the ledger is keyed by the endpoint identity, so a
    /// reconnect to the same server stays quiet). Waits for both answers: the bridge's, and the product read that
    /// decides whether this is something to fix (OpenCode QoL) or information about optional features (any other server).
    /// </summary>
    private void ShowDesktopBridgeNoticeIfDue()
    {
        if (!_pageActive || _source is not { } source || CurrentDesktopBridge() is not { } state) return;
        var info = CurrentServerInfo();
        if (info.State is not (OpenCodeInsightState.Loaded or OpenCodeInsightState.Failed)) return;
        if (OpenCodeDesktopBridgeGuidance.Notice(state, info.Product) is not { } notice
            || !OpenCodeDesktopBridgeNoticeLedger.Shared.TryTake(source.Identity, state.Status)) return;
        Button? details = null;
        if (notice.Actionable)
        {
            details = new Button { Content = OpenCodeDesktopBridgeGuidance.DetailsAction };
            AutomationProperties.SetAutomationId(details, "OpenCodeDesktopBridgeNoticeDetails");
            details.Click += (_, _) => OpenDesktopBridgeDetails();
        }
        // Reuse the existing app notice: informational, never an error. Without an action it fades by itself.
        MessageBar.ActionButton = details;
        MessageBar.Severity = InfoBarSeverity.Informational;
        MessageBar.Title = notice.Title;
        MessageBar.Message = notice.Message;
        MessageBar.IsOpen = true;
    }

    /// <summary>Notice → connection details, opened on the bridge's page (same flyout and anchors as the status button).</summary>
    private void OpenDesktopBridgeDetails()
    {
        if (!_pageActive) return;
        static bool Shown(FrameworkElement? element)
            => element is { Visibility: Visibility.Visible, ActualWidth: > 0, XamlRoot: not null };
        // Wide session layout: the footer row; narrow: the composer dot; Home and blank pages: the header status button.
        FrameworkElement? anchor = !_composerOverviewInDot && Shown(_rightPanelMetadata?.Footer) ? _rightPanelMetadata!.Footer
            : Shown(ComposerConnectionButton) ? ComposerConnectionButton
            : Shown(ConnectionStatusButton) ? ConnectionStatusButton : null;
        if (anchor is null && !ConnectionFlyout.IsOpen) return; // nowhere to show it: keep the notice
        MessageBar.IsOpen = false;
        _connectionDetailKey = OpenCodeConnectionFlyout.BridgeKey;
        if (ConnectionFlyout.IsOpen) RenderConnectionFlyout();
        else ConnectionFlyout.ShowAt(anchor!);
    }
}
