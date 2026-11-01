using System;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private bool _browserDirty, _browserSaving;
    private readonly ComboBox _browserEngine = SettingsCards.Sized(new ComboBox());
    private readonly ComboBox _browserConnection = SettingsCards.Sized(new ComboBox());
    private readonly TextBox _chromeExecutable = SettingsCards.Sized(new TextBox { PlaceholderText = "留空自动查找" });
    private readonly TextBox _chromeEndpoint = SettingsCards.Sized(new TextBox { PlaceholderText = "http://127.0.0.1:9222" });
    private readonly StackPanel _chromeSettings = new() { Spacing = SettingsCards.RowSpacing };
    private readonly StackPanel _chromeAttach = new() { Spacing = SettingsCards.RowSpacing };
    private readonly Button _browserSave = new() { Content = "保存" };

    private UIElement BuildBrowserSection()
    {
        _browserEngine.Items.Add("内嵌 WebView2"); _browserEngine.Items.Add("外部 Chrome（独立窗口）");
        _browserConnection.Items.Add("由应用启动"); _browserConnection.Items.Add("连接已启动的 Chrome");
        AutomationProperties.SetName(_browserEngine, "浏览器引擎");
        AutomationProperties.SetName(_browserConnection, "Chrome 连接方式");
        AutomationProperties.SetName(_chromeExecutable, "Chrome 程序路径");
        AutomationProperties.SetName(_chromeEndpoint, "本机调试端点");
        var command = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            Text = "chrome.exe --user-data-dir=\"C:\\Chrome-OpenCode-Private\" --remote-debugging-port=9222 --remote-debugging-address=127.0.0.1 --enable-automation --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp" };
        AutomationProperties.SetName(command, "连接已有 Chrome 的必需启动参数");

        _chromeSettings.Children.Add(SettingsCards.Card("\uE71B", "Chrome 连接方式", null, _browserConnection));
        _chromeSettings.Children.Add(SettingsCards.Card("\uE8B7", "Chrome 程序路径", null, _chromeExecutable));
        _chromeAttach.Children.Add(SettingsCards.Card("\uE968", "本机调试端点", "只允许本机地址；请用独立的用户目录启动 Chrome", _chromeEndpoint));
        _chromeAttach.Children.Add(SettingsCards.Expander("\uE756", "Chrome 启动参数", "复制后按实际程序路径替换", null, SettingsCards.SubContent(command)));
        _chromeSettings.Children.Add(_chromeAttach);

        var panel = new StackPanel { Spacing = SettingsCards.GroupSpacing };
        panel.Children.Add(SettingsCards.Group("会话浏览器",
            SettingsCards.Card("\uE774", "浏览器引擎", "WebView2 在应用内显示；Chrome 在独立窗口中运行", _browserEngine),
            _chromeSettings));
        panel.Children.Add(SettingsCards.Group(null,
            SettingsCards.Card("\uE74E", "保存浏览器设置", "仅对新建或重新打开的会话工作区生效；仅关闭网页不会重建工作区", _browserSave)));
        _browserEngine.SelectionChanged += (_, _) => BrowserDraftChanged();
        _browserConnection.SelectionChanged += (_, _) => BrowserDraftChanged();
        _chromeExecutable.TextChanged += (_, _) => BrowserDraftChanged(); _chromeEndpoint.TextChanged += (_, _) => BrowserDraftChanged();
        _browserSave.Click += async (_, _) => await SaveBrowserAsync();
        AutomationProperties.SetAutomationId(_browserEngine, "SettingsBrowserEngine");
        AutomationProperties.SetAutomationId(_browserSave, "SettingsBrowserSave");
        ApplyBrowserToUi(); return panel;
    }
    private void BrowserDraftChanged()
    {
        UpdateBrowserVisibility(); if (!_suppressUiEvents) _browserDirty = true;
        _browserSave.IsEnabled = _browserDirty && !_browserSaving;
    }
    private void UpdateBrowserVisibility()
    {
        _chromeSettings.Visibility = _browserEngine.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _chromeAttach.Visibility = _browserConnection.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _chromeExecutable.IsEnabled = _browserConnection.SelectedIndex != 1;
    }
    private void ApplyBrowserToUi()
    {
        if (_browserDirty) return;
        var suppress = _suppressUiEvents; _suppressUiEvents = true;
        try
        {
            var browser = _settings?.Browser ?? new BrowserSettings();
            _browserEngine.SelectedIndex = browser.Engine == BrowserEngine.Chrome ? 1 : 0;
            _browserConnection.SelectedIndex = browser.ChromeConnection == BrowserChromeConnection.Attach ? 1 : 0;
            _chromeExecutable.Text = browser.ChromeExecutablePath ?? ""; _chromeEndpoint.Text = browser.ChromeEndpoint ?? "";
            UpdateBrowserVisibility();
            _browserSave.IsEnabled = false;
        }
        finally { _suppressUiEvents = suppress; }
    }
    private async Task<bool> SaveBrowserAsync()
    {
        if (_browserSaving) return false;
        var browser = new BrowserSettings { Engine = _browserEngine.SelectedIndex == 1 ? BrowserEngine.Chrome : BrowserEngine.WebView2,
            ChromeConnection = _browserConnection.SelectedIndex == 1 ? BrowserChromeConnection.Attach : BrowserChromeConnection.Managed,
            ChromeExecutablePath = string.IsNullOrWhiteSpace(_chromeExecutable.Text) ? null : _chromeExecutable.Text.Trim(),
            ChromeEndpoint = string.IsNullOrWhiteSpace(_chromeEndpoint.Text) ? null : _chromeEndpoint.Text.Trim() };
        if (browser.ValidationError() is { } reason) { ShowStatus(InfoBarSeverity.Error, "浏览器设置未保存", reason); return false; }
        _browserSaving = true; _browserSave.IsEnabled = false;
        ActionFeedback.Busy(_browserSave);
        try
        {
            if (!await SaveAsync(settings => settings with { Browser = browser })) { ActionFeedback.Failure(_browserSave, "浏览器设置未保存"); return false; }
            _browserDirty = false; ApplyBrowserToUi();
            ActionFeedback.Success(_browserSave, "已保存浏览器设置");
            return true;
        }
        finally { _browserSaving = false; _browserSave.IsEnabled = _browserDirty; }
    }
}
