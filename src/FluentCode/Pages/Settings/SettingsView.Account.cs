using System;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.Account;
using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

/// <summary>
/// 「账号与连接」分类：承载原有的唯一连接表单（<see cref="OpenCodeConnectionView"/>，内容不变）与“断开连接”。
/// 断开 = 清除唯一连接配置并删除其 DPAPI 凭据；主窗口随设置变化回到首次运行欢迎页。
/// </summary>
public sealed partial class SettingsView
{
    private OpenCodeConnectionView? _connectionView;
    private Button? _disconnectButton;

    private UIElement BuildAccountSection()
    {
        _connectionView = new OpenCodeConnectionView(_settingsService, _secretStore) { HorizontalAlignment = HorizontalAlignment.Left };
        _disconnectButton = new Button { Content = "断开连接…" };
        AutomationProperties.SetAutomationId(_disconnectButton, "SettingsOpenCodeDisconnect");
        _disconnectButton.Click += async (_, _) => await DisconnectOpenCodeAsync();

        var disconnect = new StackPanel { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        disconnect.Children.Add(new TextBlock { Text = "断开连接", FontSize = 16 });
        disconnect.Children.Add(CreateSubtleText("清除当前 OpenCode 连接与已保存的凭据，回到欢迎页重新添加。不会停止或修改 OpenCode 服务本身。"));
        disconnect.Children.Add(_disconnectButton);

        var panel = new StackPanel { Spacing = 24 };
        panel.Children.Add(_connectionView);
        panel.Children.Add((UIElement)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Height=\"1\" MaxWidth=\"520\" HorizontalAlignment=\"Stretch\" Background=\"{ThemeResource DividerStrokeColorDefaultBrush}\"/>"));
        panel.Children.Add(disconnect);
        UpdateDisconnectAvailability();
        return panel;
    }

    private void UpdateDisconnectAvailability()
    {
        if (_disconnectButton is null) return;
        _disconnectButton.IsEnabled = OpenCodeConnectionProfile.Describe(_settingsService.Current?.OpenCode).IsConfigured;
    }

    private async Task DisconnectOpenCodeAsync()
    {
        if (_connectionView is { IsBusy: true }) return;
        if (!await ConfirmAsync("断开 OpenCode 连接？", "将清除当前连接配置和已保存的凭据，并回到欢迎页。之后可重新添加。", "断开")) return;
        try
        {
            string? credential = _settingsService.Current?.OpenCode.CredentialId;
            await _settingsService.UpdateAsync(latest => latest with { OpenCode = new OpenCodeSettings() }, _lifetimeCts.Token);
            if (!string.IsNullOrEmpty(credential))
            {
                try { await _secretStore.RemoveAsync(credential, _lifetimeCts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ShowStatus(InfoBarSeverity.Warning, "已断开连接", "旧凭据密文未能删除：" + ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "断开连接失败", OpenCodeConnectionCommitter.Describe(ex) + "（现有连接未改动）");
        }
        UpdateDisconnectAvailability();
    }

    private void CloseAccountSection() => _connectionView?.Close();
}
