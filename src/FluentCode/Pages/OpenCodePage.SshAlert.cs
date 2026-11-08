using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

/// <summary>
/// D6：SSH 安全提示（主机密钥变化 = Error，监听警告 = Warning）。原生 InfoBar，位于首页与会话布局之上，两种布局都可见；
/// 关闭时折叠（不留外边距）。密钥变化给出唯一操作“打开连接设置”，直达 设置 › 账号与连接；不在此处执行任何命令。
/// </summary>
public sealed partial class OpenCodePage
{
    private Button? _sshAlertAction;
    private bool _sshAlertWired;

    private void ShowSshAlert(OpenCodeConnectionStatus? status)
    {
        if (!_sshAlertWired)
        {
            _sshAlertWired = true;
            // 用户关闭后同样折叠，避免关闭的 InfoBar 仍占用外边距。
            SshConnectionAlert.Closed += (_, _) => SshConnectionAlert.Visibility = Visibility.Collapsed;
        }

        bool changedKey = status?.FailureKind == SshFailureKind.ChangedHostKey;
        bool open = changedKey || status?.Warning == true;
        if (!open)
        {
            SshConnectionAlert.IsOpen = false;
            SshConnectionAlert.Visibility = Visibility.Collapsed;
            return;
        }

        SshConnectionAlert.Severity = changedKey ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        SshConnectionAlert.Title = status!.Label;
        SshConnectionAlert.Message = status.Detail;
        SshConnectionAlert.ActionButton = changedKey ? SshAlertAction() : null;
        SshConnectionAlert.Visibility = Visibility.Visible;
        SshConnectionAlert.IsOpen = true;
    }

    private Button SshAlertAction()
    {
        if (_sshAlertAction is { } existing) return existing;
        var button = new Button { Content = "打开连接设置" };
        AutomationProperties.SetAutomationId(button, "OpenCodeSshConnectionAlertAction");
        button.Click += (_, _) =>
        {
            ShowSettingsTab();
            (_settingsContent as FluentCode.Pages.Settings.SettingsPage)?.View?.ShowAccountCategory();
        };
        return _sshAlertAction = button;
    }
}
