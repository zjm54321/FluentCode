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
    private Button? _pairButton;

    /// <summary>主页面安全提示的“打开连接设置”：切到「账号与连接」分类（若已在其他分类）。</summary>
    internal void ShowAccountCategory()
    {
        int index = _categories.FindIndex(static c => c.Title == "账号与连接");
        if (index >= 0) SelectCategory(index);
    }

    private UIElement BuildAccountSection()
    {
        _connectionView = new OpenCodeConnectionView(_settingsService, _secretStore) { HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 640 };
        _disconnectButton = new Button { Content = "断开连接…" };
        AutomationProperties.SetAutomationId(_disconnectButton, "SettingsOpenCodeDisconnect");
        AutomationProperties.SetName(_disconnectButton, "断开 OpenCode 连接");
        _disconnectButton.Click += async (_, _) => await DisconnectOpenCodeAsync();

        _pairButton = new Button { Content = "生成链接…" };
        AutomationProperties.SetAutomationId(_pairButton, "SettingsOpenCodePairDevice");
        AutomationProperties.SetName(_pairButton, "生成配对链接和二维码");
        _pairButton.Click += async (_, _) => await PairDeviceAsync();

        // 连接表单与欢迎页共用；这里不再手工套一层 Border 卡片，改用与其余分类同一套 SettingsCard 行：
        // 标题行 “OpenCode 连接” + 整行铺满的表单（保持表单始终可见，不做点击展开）。
        var form = SettingsCards.Row("\uE77B", "OpenCode 连接", _connectionView);
        AutomationProperties.SetAutomationId(form, "SettingsOpenCodeConnectionCard");
        var panel = new StackPanel { Spacing = SettingsCards.GroupSpacing };
        panel.Children.Add(SettingsCards.Group(null, form));
        panel.Children.Add(SettingsCards.Group(null,
            SettingsCards.Card("\uE8EA", "连接其他设备", "生成一次性链接和二维码，让手机、另一台电脑或浏览器连到这个 OpenCode 服务", _pairButton)));
        panel.Children.Add(SettingsCards.Group(null,
            SettingsCards.Card("\uE8D8", "断开连接", "清除连接与已保存的凭据，不影响 OpenCode 服务本身", _disconnectButton)));
        UpdateDisconnectAvailability();
        return panel;
    }

    private void UpdateDisconnectAvailability()
    {
        if (_disconnectButton is null) return;
        bool configured = OpenCodeConnectionProfile.Describe(_settingsService.Current?.OpenCode).IsConfigured;
        _disconnectButton.IsEnabled = configured;
        if (_pairButton is not null) _pairButton.IsEnabled = configured;
    }

    /// <summary>只在服务已连接时打开；连接尚未就绪时说明原因，不发请求。</summary>
    private async Task PairDeviceAsync()
    {
        if (_pairButton is null || _connectionView is { IsBusy: true }) return;
        if (OpenCodePairDeviceDialog.CurrentSource() is null)
        {
            ShowStatus(InfoBarSeverity.Warning, "还没有连上 OpenCode", "连接成功后才能生成配对链接。");
            return;
        }

        await OpenCodePairDeviceDialog.ShowAsync(_pairButton, _settingsService.Current?.OpenCode.Kind ?? OpenCodeConnectionKind.Remote);
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
