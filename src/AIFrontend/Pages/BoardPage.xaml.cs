using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class BoardPage : Page
{
    public BoardPage()
    {
        this.InitializeComponent();
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }
}
