using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Constructor/ShowMessage hooks belong to the leased main page; see runtime wiring handoff.
    private void InitializeNoticeDismissal()
        => MessageBar.Closed += OnRuntimeNoticeClosed;

    private void OnRuntimeNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
            OpenCodeNoticeDismissals.RecordUserDismissal(sender.Title ?? string.Empty, sender.Message ?? string.Empty);
    }

    private bool IsRuntimeNoticeDismissed(string title, string message)
        => OpenCodeNoticeDismissals.IsDismissed(title, message);
}
