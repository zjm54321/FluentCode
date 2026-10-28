using System;
using System.Threading.Tasks;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

public sealed partial class SettingsView
{
    private bool _browserDirty, _browserSaving;
    private readonly ComboBox _browserEngine = new() { Header = "浏览器引擎", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _browserConnection = new() { Header = "Chrome 连接方式", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _chromeExecutable = new() { Header = "Chrome 程序路径（可选）", PlaceholderText = "留空自动查找 Chrome；或填写完整 .exe 路径" };
    private readonly TextBox _chromeEndpoint = new() { Header = "本机调试端点", PlaceholderText = "http://127.0.0.1:9222" };
    private readonly StackPanel _chromeSettings = new() { Spacing = 12 };
    private readonly StackPanel _chromeAttach = new() { Spacing = 8 };
    private readonly Button _browserSave = new() { Content = "保存浏览器设置", HorizontalAlignment = HorizontalAlignment.Left };

    private UIElement BuildBrowserSection()
    {
        var panel = new StackPanel { Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(CreateSubtleText("WebView2 在应用内显示；Chrome 在独立窗口中运行，应用内只显示真实截图预览。服务器文件与 Mermaid 预览仍使用应用内渲染器。"));
        _browserEngine.Items.Add("内嵌 WebView2"); _browserEngine.Items.Add("外部 Chrome · 独立窗口");
        _browserConnection.Items.Add("由应用启动（推荐）"); _browserConnection.Items.Add("连接已启动的 Chrome（高级）");
        panel.Children.Add(_browserEngine); panel.Children.Add(_chromeSettings);
        _chromeSettings.Children.Add(_browserConnection); _chromeSettings.Children.Add(_chromeExecutable); _chromeSettings.Children.Add(_chromeAttach);
        _chromeAttach.Children.Add(_chromeEndpoint);
        _chromeAttach.Children.Add(CreateSubtleText("仅连接你信任的本机调试端点；不允许远程地址或重定向。请使用非默认的独立用户目录，按下面的参数启动 Chrome。应用只控制自己创建的窗口/标签，不接管其他标签页。"));
        var command = new TextBox { Header = "Chrome 启动示例（可复制；程序路径请按实际位置替换）", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            Text = "chrome.exe --user-data-dir=\"C:\\Chrome-OpenCode-Private\" --remote-debugging-port=9222 --remote-debugging-address=127.0.0.1 --enable-automation --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp" };
        AutomationProperties.SetName(command, "连接已有 Chrome 的必需启动参数"); _chromeAttach.Children.Add(command);
        panel.Children.Add(CreateSubtleText("保存不会切换正在运行的浏览器。已有会话请关闭会话标签页后重新打开，或在新会话中使用新设置；仅关闭网页不会重建工作区。此配置不包含 OpenCode 登录凭据。"));
        panel.Children.Add(_browserSave);
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
        try
        {
            if (!await SaveAsync(settings => settings with { Browser = browser }, "浏览器设置已保存，仅对新建或重新打开的工作区生效。")) return false;
            _browserDirty = false; ApplyBrowserToUi(); return true;
        }
        finally { _browserSaving = false; _browserSave.IsEnabled = true; }
    }
}
