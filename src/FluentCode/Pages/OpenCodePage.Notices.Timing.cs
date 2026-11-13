using System;
using System.Numerics;
using FluentCode.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages;

/// <summary>
/// 浮动提示的停留规则：错误、警告和带操作按钮的提示一直留到用户关闭；普通信息提示自己收起；
/// “状态未知”只在持续一小会儿之后才出现，并且自己消失。
/// </summary>
public sealed partial class OpenCodePage
{
    private static readonly TimeSpan NoticeHintDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan NoticeHintStay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan NoticeBusyRetry = TimeSpan.FromSeconds(2);
    private DispatcherTimer? _noticeMessageTimer;
    private DispatcherTimer? _noticeHintTimer;
    private bool _noticeMessagePointerOver;
    private bool _noticeHintRevealed;
    private bool _noticeHintExpired;
    private string? _noticeHintSessionId;
    private bool _noticeLayerWasShown;

    private void InitializeNoticeTiming()
    {
        foreach (var property in new[] { InfoBar.IsOpenProperty, InfoBar.SeverityProperty, InfoBar.TitleProperty,
                     InfoBar.MessageProperty, InfoBar.ActionButtonProperty })
            MessageBar.RegisterPropertyChangedCallback(property, OnNoticeMessageChanged);
        MessageBarSurface.PointerEntered += NoticeMessage_PointerEntered;
        MessageBarSurface.PointerExited += NoticeMessage_PointerExited;
        SessionStateHint.RegisterPropertyChangedCallback(TextBlock.TextProperty, OnNoticeHintChanged);
        SessionStateHint.RegisterPropertyChangedCallback(VisibilityProperty, OnNoticeHintChanged);
    }

    private void StopNoticeTimers()
    {
        if (_noticeMessageTimer is { } message) { message.Stop(); message.Tick -= NoticeMessageTimer_Tick; }
        if (_noticeHintTimer is { } hint) { hint.Stop(); hint.Tick -= NoticeHintTimer_Tick; }
        _noticeMessageTimer = _noticeHintTimer = null;
    }

    // ---- 消息条：只有“信息”级且没有操作按钮的才自动收起 ----

    private void OnNoticeMessageChanged(DependencyObject sender, DependencyProperty property) => RearmNoticeMessageTimer();

    private void NoticeMessage_PointerEntered(object sender, PointerRoutedEventArgs e) => _noticeMessagePointerOver = true;

    private void NoticeMessage_PointerExited(object sender, PointerRoutedEventArgs e) => _noticeMessagePointerOver = false;

    private void RearmNoticeMessageTimer()
    {
        _noticeMessageTimer?.Stop();
        if (!IsLoaded || !MessageBar.IsOpen || MessageBar.Severity != InfoBarSeverity.Informational
            || MessageBar.ActionButton is not null) return;
        // 按字数给阅读时间：6 秒起，每 12 个字多 1 秒，最多 12 秒。
        int length = (MessageBar.Title?.Length ?? 0) + (MessageBar.Message?.Length ?? 0);
        StartNoticeMessageTimer(TimeSpan.FromSeconds(Math.Clamp(6 + length / 12, 6, 12)));
    }

    private void StartNoticeMessageTimer(TimeSpan interval)
    {
        if (_noticeMessageTimer is null)
        {
            _noticeMessageTimer = new DispatcherTimer();
            _noticeMessageTimer.Tick += NoticeMessageTimer_Tick;
        }
        _noticeMessageTimer.Interval = interval;
        _noticeMessageTimer.Start();
    }

    private void NoticeMessageTimer_Tick(object? sender, object e)
    {
        _noticeMessageTimer?.Stop();
        if (!MessageBar.IsOpen || MessageBar.Severity != InfoBarSeverity.Informational || MessageBar.ActionButton is not null) return;
        // 鼠标停在上面或键盘焦点在里面时不收起，过一会儿再看。
        if (_noticeMessagePointerOver || IsNoticeFocusWithin(MessageBarSurface))
        {
            StartNoticeMessageTimer(NoticeBusyRetry);
            return;
        }
        // 程序关闭（不是关闭按钮），所以不会写入“不再提示”的记忆。
        MessageBar.IsOpen = false;
    }

    private bool IsNoticeFocusWithin(DependencyObject root)
    {
        if (XamlRoot is null) return false;
        for (var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, root)) return true;
        return false;
    }

    // ---- “状态未知”胶囊：持续 0.6 秒以上才出现（切换会话时不闪），停留 6 秒后自己消失 ----

    private void OnNoticeHintChanged(DependencyObject sender, DependencyProperty property) => ArmNoticeHint();

    /// <summary>会话状态每次应用后调用：换了会话就重新计时，即使文字没变。</summary>
    partial void OnSessionStateHintApplied()
    {
        if (string.Equals(_noticeHintSessionId, _selectedSessionId, StringComparison.Ordinal)) return;
        _noticeHintSessionId = _selectedSessionId;
        ArmNoticeHint();
    }

    private void ArmNoticeHint()
    {
        _noticeHintTimer?.Stop();
        _noticeHintRevealed = _noticeHintExpired = false;
        if (IsLoaded && SessionStateHint.Visibility == Visibility.Visible) StartNoticeHintTimer(NoticeHintDelay);
        RefreshNoticeLayer();
    }

    private void StartNoticeHintTimer(TimeSpan interval)
    {
        if (_noticeHintTimer is null)
        {
            _noticeHintTimer = new DispatcherTimer();
            _noticeHintTimer.Tick += NoticeHintTimer_Tick;
        }
        _noticeHintTimer.Interval = interval;
        _noticeHintTimer.Start();
    }

    private void NoticeHintTimer_Tick(object? sender, object e)
    {
        _noticeHintTimer?.Stop();
        if (_noticeHintRevealed)
        {
            _noticeHintRevealed = false;
            _noticeHintExpired = true;
        }
        else if (!_noticeHintExpired && SessionStateHint.Visibility == Visibility.Visible)
        {
            _noticeHintRevealed = true;
            StartNoticeHintTimer(NoticeHintStay);
        }
        RefreshNoticeLayer();
        if (_noticeHintRevealed)
            FrameworkElementAutomationPeer.CreatePeerForElement(SessionStateHint)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    // ---- 入场：整层从上方 12 像素处落下并淡入一次；系统或应用内关闭动画时直接出现 ----

    private void PlayNoticeEntrance(bool shown)
    {
        if (shown && !_noticeLayerWasShown) FluentMotion.SlideFadeIn(NoticeStack, new Vector3(0, -12, 0), FluentMotion.Fast);
        _noticeLayerWasShown = shown;
    }
}
