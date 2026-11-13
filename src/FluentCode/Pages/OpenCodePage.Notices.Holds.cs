using System;
using System.Collections.Generic;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentCode.Pages;

/// <summary>
/// 被动提示（没有按钮的“信息”消息、“状态未知”胶囊）绝不冻结终端：如果它出现的位置会盖到终端，
/// 就先不显示、不要租约、也不计时；等不再重叠（终端关了，或位置让开了）再出现，从那时开始计时。
/// 判断规则在 OpenCodeNoticePolicy；这里只负责算出“它会出现在哪里”。
/// </summary>
public sealed partial class OpenCodePage
{
    private bool _noticeMessageHeld;
    private bool _noticeHintHeld;
    // 最近一次真实量到的尺寸；还没显示过时用控件的常见尺寸。
    private double _noticeMessageHeight = 48;
    private double _noticeHintHeight = 30;
    private double _noticeHintWidth = 220;

    private bool NoticeMessageErrorOrWarning
        => MessageBar.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning;

    private static double NoticeSlotHeight(FrameworkElement surface)
        => surface.Visibility == Visibility.Visible && surface.ActualHeight > 0 ? surface.ActualHeight : 48;

    private static bool NoticeReachesTerminal(List<(OpenCodePersistentPtyRenderer Terminal, Rect Bounds)> terminals, Rect slot)
    {
        foreach (var (_, area) in terminals)
            if (NoticeOverlaps(slot, area)) return true;
        return false;
    }

    /// <summary>
    /// 按固定顺序推算消息条和胶囊将要占的位置（与 NoticeStack 的排法一致：顶部居中、间距同 Spacing），
    /// 再问规则它们要不要让开。已经显示的用真实高度，被让开的用上次量到的高度，所以结果不会来回跳。
    /// </summary>
    private void UpdateNoticeHolds(bool ssh, bool permission, bool messageWanted, bool hintWanted)
    {
        bool messageHeld = false, hintHeld = false;
        var terminals = messageWanted || hintWanted ? NoticeTerminals() : null;
        if (terminals is { Count: > 0 } && PageRoot.ActualWidth > 0)
        {
            if (MessageBarSurface.Visibility == Visibility.Visible && MessageBarSurface.ActualHeight > 0)
                _noticeMessageHeight = MessageBarSurface.ActualHeight;
            if (SessionStateHintSurface.Visibility == Visibility.Visible && SessionStateHintSurface.ActualHeight > 0)
            {
                _noticeHintHeight = SessionStateHintSurface.ActualHeight;
                _noticeHintWidth = SessionStateHintSurface.ActualWidth;
            }

            var pad = NoticeStack.Padding;
            double page = PageRoot.ActualWidth;
            double width = Math.Max(0, Math.Min(NoticeLayer.MaxWidth, page) - pad.Left - pad.Right);
            double top = SessionTabs.ActualHeight + NoticeLayer.Margin.Top + pad.Top;
            if (ssh) top += NoticeSlotHeight(SshConnectionAlertSurface) + NoticeStack.Spacing;
            if (permission) top += NoticeSlotHeight(PermissionBarSurface) + NoticeStack.Spacing;
            if (messageWanted)
            {
                var slot = new Rect((page - width) / 2, top, width, _noticeMessageHeight);
                messageHeld = OpenCodeNoticePolicy.IsHeldBack(OpenCodeNoticeKind.Message, NoticeMessageErrorOrWarning,
                    MessageBar.ActionButton is not null, NoticeReachesTerminal(terminals, slot));
                if (!messageHeld) top += _noticeMessageHeight + NoticeStack.Spacing;
            }
            if (hintWanted)
            {
                double pill = Math.Min(width, _noticeHintWidth);
                var slot = new Rect((page - pill) / 2, top, pill, _noticeHintHeight);
                hintHeld = OpenCodeNoticePolicy.IsHeldBack(OpenCodeNoticeKind.StateHint, false, false,
                    NoticeReachesTerminal(terminals, slot));
            }
        }

        if (messageHeld != _noticeMessageHeld)
        {
            _noticeMessageHeld = messageHeld;
            RearmNoticeMessageTimer(); // 让开时停表；重新出现时从头计时。
        }
        if (hintHeld != _noticeHintHeld)
        {
            _noticeHintHeld = hintHeld;
            if (hintHeld) _noticeHintTimer?.Stop();
            else if (_noticeHintRevealed)
            {
                StartNoticeHintTimer(NoticeHintStay);
                FrameworkElementAutomationPeer.CreatePeerForElement(SessionStateHint)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
    }
}
