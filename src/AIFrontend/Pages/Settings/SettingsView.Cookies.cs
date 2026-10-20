using System;
using System.Threading.Tasks;
using AIFrontend.Pages.Web;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

/// <summary>SettingsView 的浏览器 Cookie 清理分区（三个独立按钮）。</summary>
public sealed partial class SettingsView
{
    private UIElement BuildCookiesSection()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(CreateSubtleText(
            "清除各官网环境（独立 WebView2 用户数据目录）的浏览器 cookie。仅清除 cookie，" +
            "不等同于清除全部站点数据：缓存、本地存储、站点权限等仍会保留，且不会删除用户数据目录。"));

        _clearChatGptButton = CreateCookieButton("清除 ChatGPT Cookies", WebProvider.ChatGPT, "SettingsClearChatGptCookies");
        _clearGeminiButton = CreateCookieButton("清除 Gemini Cookies", WebProvider.Gemini, "SettingsClearGeminiCookies");
        _clearClaudeButton = CreateCookieButton("清除 Claude Cookies", WebProvider.Claude, "SettingsClearClaudeCookies");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_clearChatGptButton);
        row.Children.Add(_clearGeminiButton);
        row.Children.Add(_clearClaudeButton);
        panel.Children.Add(row);

        _cookieStatus = CreateSubtleText(_clearCookiesAsync is null
            ? "当前不可用：未接入 Cookie 清理回调（清除按钮已禁用，不虚构已清理）。"
            : "已接入清理回调：点击将清除对应官网环境的浏览器 cookie（只清 cookie，不删除用户数据目录）。");
        panel.Children.Add(_cookieStatus);

        return panel;
    }

    private Button CreateCookieButton(string text, WebProvider provider, string automationId)
    {
        var button = new Button
        {
            Content = text,
            Tag = provider,
            IsEnabled = _clearCookiesAsync is not null,
        };
        button.Click += OnClearCookiesClick;
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private void SetCookieStatus(string message) => _cookieStatus.Text = message;

    private async void OnClearCookiesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WebProvider provider } || _clearCookiesAsync is null)
        {
            SetCookieStatus("未接入清理回调，无法执行。");
            return;
        }

        string name = WebProviderInfo.GetDisplayName(provider);
        bool confirmed = await ConfirmAsync(
            $"清除 {name} Cookies",
            $"将清除 {name} 站点环境的全部 cookie。这只清除 cookie，不等同于清除全部站点数据" +
            "（缓存、本地存储、站点权限等仍保留）。清除后可能需要重新登录。",
            "清除");
        if (!confirmed)
        {
            return;
        }

        SetCookieStatus($"正在清除 {name} cookies…");
        try
        {
            // 回调在 UI 线程调用（本处理程序已处于 UI 线程）；不得依赖 NormalPage 处于已加载态。
            await _clearCookiesAsync(provider);
            if (_unloaded)
            {
                return;
            }

            SetCookieStatus($"已清除 {name} cookies。");
            ShowStatus(InfoBarSeverity.Success, $"已清除 {name} cookies", "仅清除 cookie；其他站点数据未受影响。");
        }
        catch (Exception ex)
        {
            if (_unloaded)
            {
                return;
            }

            // 只展示安全类型 / HRESULT，不输出可能含请求头或内部细节的异常文本。
            string reason = DescribeSafeError(ex);
            SetCookieStatus($"清除 {name} cookies 失败：{reason}");
            ShowStatus(InfoBarSeverity.Error, $"清除 {name} cookies 失败", reason);
        }
    }
}
