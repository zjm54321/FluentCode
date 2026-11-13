// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

// The InfoBar base constructor keeps its stock default style key; only its automation peer is replaced.
// Source: https://github.com/microsoft/microsoft-ui-xaml, branch winui3/release/2.6.5,
// src/controls/dev/InfoBar/InfoBar.cpp:23-36 and src/controls/dev/InfoBar/InfoBar.idl:58-60.
internal sealed partial class NativeUiErrorInfoBar : InfoBar
{
    private bool _announced;

    internal void Announce(string title, string message)
    {
        if (_announced) return;
        _announced = true;
        var text = "错误，" + title + (message.Length is > 0 and <= 200 ? "，" + message : "");
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                AutomationNotificationProcessing.CurrentThenMostRecent, text, "NativeUiErrorInfoBarOpened");
        }
        catch (Exception)
        {
            // 无障碍通知失败不影响错误卡片本身。
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ErrorPeer(this);

    private sealed partial class ErrorPeer : FrameworkElementAutomationPeer
    {
        internal ErrorPeer(NativeUiErrorInfoBar owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;
        protected override string GetClassNameCore() => "InfoBar";
    }
}
