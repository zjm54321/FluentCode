using System;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private bool _billionDashboardConfirmationPending;
    private ContentDialog? _billionDashboardDialog;
    private void InvalidateBillionDashboardConfirmation() => _billionDashboardDialog?.Hide();
    private async Task OpenBillionDashboardAsync(OpenCodeBillionContext context)
    {
        if (_billionDashboardConfirmationPending || _insights is null || !IsCurrentInsightsBinding(context.Binding)
            || context.Binding.Source.IsRevoked || OpenCodeBillionPresentation.DashboardUri(_insights.Snapshot, context) is not { } uri) return;
        _billionDashboardConfirmationPending = true;
        try
        {
            var dialog = _billionDashboardDialog = new ContentDialog { XamlRoot = XamlRoot, Title = "打开本机 Billion 页面？",
                Content = new TextBlock { Text = "将在系统默认浏览器打开当前会话的 Billion 页面：\n" + uri.AbsoluteUri
                    + "\n该页面也可以浏览这个本机实例中的其他会话历史，不受当前会话范围限制。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                PrimaryButtonText = "在系统浏览器打开", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsCurrentInsightsBinding(context.Binding)
                || context.Binding.Source.IsRevoked || _insights is null
                || OpenCodeBillionPresentation.DashboardUri(_insights.Snapshot, context) != uri) return;
            if (!await Launcher.LaunchUriAsync(uri) && IsCurrentInsightsBinding(context.Binding))
                ShowMessage(InfoBarSeverity.Warning, "页面未打开", "系统默认浏览器未能打开本机 Billion 页面。没有更改浏览器或网络配置。");
        }
        catch (Exception)
        {
            if (IsCurrentInsightsBinding(context.Binding)) ShowMessage(InfoBarSeverity.Warning, "页面未打开", "未能打开本机 Billion 页面，请检查系统默认浏览器。");
        }
        finally { _billionDashboardDialog = null; _billionDashboardConfirmationPending = false; }
    }
}
