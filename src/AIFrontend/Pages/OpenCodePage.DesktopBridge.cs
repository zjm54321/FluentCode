using System;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private async Task InitializeDesktopBridgeAsync(OpenCodePageSource source, OpenCodeClient client)
    {
        try
        {
            var state = await client.DesktopBridge.NegotiateAsync(client, null, source.RevocationToken);
            if (!_pageActive || source.IsRevoked || !ReferenceEquals(_source, source) || !ReferenceEquals(_client, client)) return;
            if (client.DesktopBridge.TakeNotice(state) is not { } notice) return;
            // Reuse the existing dismissible app notice. The service, not the global
            // message-text dismissal set, owns this notice's once-per-source scope.
            MessageBar.ActionButton = null;
            MessageBar.Severity = InfoBarSeverity.Informational;
            MessageBar.Title = "Desktop Bridge";
            MessageBar.Message = notice;
            MessageBar.IsOpen = true;
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException)
        {
            // Auth/network/protocol failures are not evidence of a missing plugin.
            // Dependent features retain their existing unavailable/error presentation.
        }
    }
}
