using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private void UpdateSessionReturnUi()
    {
        if (SessionReturnButton is null)
        {
            return;
        }

        var label = SessionReturnLabel;
        var visible = !string.IsNullOrEmpty(label);
        SessionReturnButton.Content = label;
        AutomationProperties.SetName(SessionReturnButton, label);
        SessionReturnButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SessionReturnButton.IsEnabled = CanReturnToSession;
        SessionReturnButton.Margin = visible ? new Thickness(0, 0, 8, 0) : new Thickness(0);
    }

    private async void SessionReturnButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var task = ReturnToSessionAsync();
            UpdateSessionReturnUi();
            await task;
        }
        finally
        {
            UpdateSessionReturnUi();
        }
    }
}
