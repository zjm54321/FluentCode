using System;
using FluentCode.Pages.Account;
using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentCode;

public sealed partial class MainWindow
{
    private ContentDialog? _connectionDialog;

    private void UpdateAccountConnectionSummary()
    {
        var summary = OpenCodeConnectionProfile.Describe(FluentCode.Services.Configuration.AppServices.Settings.Current?.OpenCode);
        AccountConnectionSummary.Text = summary.DisplayText;
        NavAccount.Content = "用户";
        ToolTipService.SetToolTip(NavAccount, "OpenCode · " + summary.DisplayText);
    }

    private async void ConnectOpenCode_Click(object sender, RoutedEventArgs e)
    {
        if (_connectionDialog is not null) return;
        FlyoutBase.GetAttachedFlyout(NavAccount)?.Hide();
        var view = new OpenCodeConnectionView(FluentCode.Services.Configuration.AppServices.Settings, FluentCode.Services.Configuration.AppServices.Secrets);
        var dialog = new ContentDialog
        {
            Title = "连接 OpenCode",
            Content = view,
            CloseButtonText = "关闭",
            XamlRoot = RootElement.XamlRoot,
            RequestedTheme = RootElement.ActualTheme,
        };
        _connectionDialog = dialog;
        dialog.Closing += (_, args) => { if (view.IsBusy) args.Cancel = true; };
        try { await dialog.ShowAsync(); }
        finally
        {
            view.Close();
            _connectionDialog = null;
            UpdateAccountConnectionSummary();
        }
    }
}
