using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private bool CanStopChildSession()
        => IsChildComposerSession && _pageActive && SourceReady && !_sessionMissing && !_isSending
            && _selectedSessionId is { } selected && IsComposerSessionWorking(selected)
            && NativeSendStopDisplayAllowed();

    private void RefreshChildStopButton()
    {
        ChildStopHost.Visibility = IsChildComposerSession && _pageActive && !_sessionMissing
            && _selectedSessionId is { } selected && IsComposerSessionWorking(selected)
            ? Visibility.Visible : Visibility.Collapsed;
        ChildStopButton.IsEnabled = CanStopChildSession();
        ChildStopLabel.Text = _isSending ? "正在停止…" : "停止子智能体";
    }

    private async void ChildStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanStopChildSession()) await InterruptSelectedSessionAsync(childStop: true);
    }
}
