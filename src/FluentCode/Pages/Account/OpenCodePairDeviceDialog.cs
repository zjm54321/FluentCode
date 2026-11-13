using System;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Account;

/// <summary>“连接其他设备”对话框的唯一入口（设置 › 账号与连接、连接浮层共用）。同一时间只开一个。</summary>
internal static class OpenCodePairDeviceDialog
{
    private static bool s_open;

    /// <summary>当前是否有可用于配对的已连接服务。</summary>
    internal static OpenCodePageSource? CurrentSource()
        => (Application.Current as App)?.CurrentOpenCodePageSource is { IsRevoked: false } source ? source : null;

    internal static async Task ShowAsync(FrameworkElement anchor, OpenCodeConnectionKind kind)
    {
        if (s_open || anchor.XamlRoot is null || CurrentSource() is not { } source) return;
        var connection = kind switch
        {
            OpenCodeConnectionKind.Wsl => OpenCodePairingConnection.Wsl,
            OpenCodeConnectionKind.Ssh => OpenCodePairingConnection.Ssh,
            _ => OpenCodePairingConnection.Remote,
        };
        var view = new OpenCodePairDeviceView(source, connection, anchor.XamlRoot.RasterizationScale);
        var dialog = new ContentDialog
        {
            XamlRoot = anchor.XamlRoot,
            Title = "连接其他设备",
            Content = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
            RequestedTheme = anchor.ActualTheme,
        };
        AutomationProperties.SetAutomationId(dialog, "OpenCodePairDeviceDialog");
        var queue = anchor.DispatcherQueue;
        // 连接被替换或断开：配对码属于旧连接，直接关掉。
        using var revoked = source.RevocationToken.Register(() => queue.TryEnqueue(() => { try { dialog.Hide(); } catch (Exception) { } }));
        s_open = true;
        try
        {
            _ = view.StartAsync();
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // 已有其他对话框打开等情况：不显示，也不留下配对码。
        }
        finally
        {
            view.Close();
            dialog.Content = null;
            s_open = false;
        }
    }
}
