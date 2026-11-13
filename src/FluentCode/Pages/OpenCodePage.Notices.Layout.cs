using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

/// <summary>浮动提示层的位置与高度：让开窗口级提示条（ShellNoticeBar），并且永远不盖住输入区。</summary>
public sealed partial class OpenCodePage
{
    private const double NoticeMinHeight = 96;
    private const double NoticeBottomGap = 8;
    private InfoBar? _shellNoticeBar;
    private long _shellNoticeOpenToken;

    private void InitializeNoticeBounds()
    {
        Loaded += Notices_Loaded;
        Unloaded += Notices_Unloaded;
        PageRoot.SizeChanged += Notices_BoundsChanged;
        InteractionsSection.SizeChanged += Notices_BoundsChanged;
        ComposerQueueHost.SizeChanged += Notices_BoundsChanged;
        ComposerCell.SizeChanged += Notices_BoundsChanged;
    }

    // 窗口级提示条属于 MainWindow：只读地找到它并观察，页面卸载时摘掉回调，不让它反过来持有页面。
    private void Notices_Loaded(object sender, RoutedEventArgs e)
    {
        // 计时器只在页面加载期间存在：这里按当前状态重新计时。
        RearmNoticeMessageTimer();
        ArmNoticeHint();
        if (_shellNoticeBar is not null) return;
        _shellNoticeBar = (XamlRoot?.Content as FrameworkElement)?.FindName("ShellNoticeBar") as InfoBar;
        if (_shellNoticeBar is null) return;
        _shellNoticeOpenToken = _shellNoticeBar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnNoticeSourceChanged);
        _shellNoticeBar.SizeChanged += Notices_BoundsChanged;
        UpdateNoticeBounds();
    }

    private void Notices_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) return;
        StopNoticeTimers();
        if (_shellNoticeBar is null) return;
        _shellNoticeBar.UnregisterPropertyChangedCallback(InfoBar.IsOpenProperty, _shellNoticeOpenToken);
        _shellNoticeBar.SizeChanged -= Notices_BoundsChanged;
        _shellNoticeBar = null;
    }

    private void Notices_BoundsChanged(object sender, SizeChangedEventArgs e) => UpdateNoticeBounds();

    /// <summary>
    /// 顶部：窗口级提示条打开时排在它下面。高度：封顶到“交互区 / 排队条 / 输入区”里最靠上的那一个之上，
    /// 超出的部分在层内滚动。首页没有输入区，封顶到页面底部。只改浮层自己的 Margin / MaxHeight。
    /// </summary>
    private void UpdateNoticeBounds()
    {
        if (NoticeLayer.Visibility != Visibility.Visible || PageRoot.ActualHeight <= 0) return;
        double rowTop = SessionTabs.ActualHeight;
        double top = 0;
        if (_shellNoticeBar is { IsOpen: true, ActualHeight: > 0 } shell)
            top = Math.Max(0, NoticeEdge(shell, shell.ActualHeight) - rowTop);

        double limit = PageRoot.ActualHeight;
        foreach (var blocker in new FrameworkElement[] { InteractionsSection, ComposerQueueHost, ComposerCell })
        {
            if (blocker.Visibility != Visibility.Visible || blocker.ActualHeight <= 0) continue;
            if (SessionLayoutRoot.Visibility != Visibility.Visible) break;
            limit = Math.Min(limit, NoticeEdge(blocker, 0));
        }

        double height = Math.Max(NoticeMinHeight, limit - rowTop - top - NoticeBottomGap);
        if (NoticeLayer.Margin.Top != top) NoticeLayer.Margin = new Thickness(0, top, 0, 0);
        if (NoticeLayer.MaxHeight != height) NoticeLayer.MaxHeight = height;
    }

    /// <summary>元素内某个纵向位置在 PageRoot 里的 Y；元素不在树上时返回页面底部（即不限制）。</summary>
    private double NoticeEdge(FrameworkElement element, double y)
    {
        try { return element.TransformToVisual(PageRoot).TransformPoint(new Windows.Foundation.Point(0, y)).Y; }
        catch (ArgumentException) { return PageRoot.ActualHeight; }
    }
}
