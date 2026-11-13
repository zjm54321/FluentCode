using System;
using System.Collections.Generic;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentCode.Pages;

/// <summary>
/// 终端是原生子窗口，画在所有 XAML 之上。提示与终端重叠时，对该终端持有 SuppressAirspace 租约
/// （保留最后一帧画面），提示移开或关闭后立即归还。浏览器是合成层承载的，XAML 本来就能盖住它。
/// </summary>
public sealed partial class OpenCodePage
{
    private readonly Dictionary<OpenCodePersistentPtyRenderer, IDisposable> _noticeAirspace = new();

    private void InitializeNoticeAirspace()
    {
        NoticeStack.SizeChanged += NoticeAirspace_SizeChanged;
        PersistentPtyOverlayHost.SizeChanged += NoticeAirspace_SizeChanged;
        PersistentPtyOverlayHost.RegisterPropertyChangedCallback(VisibilityProperty, OnNoticeAirspaceSourceChanged);
    }

    private void NoticeAirspace_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateNoticeAirspace();

    private void OnNoticeAirspaceSourceChanged(DependencyObject sender, DependencyProperty property) => UpdateNoticeAirspace();

    private void ReleaseNoticeAirspace()
    {
        foreach (var lease in _noticeAirspace.Values) lease.Dispose();
        _noticeAirspace.Clear();
    }

    private void UpdateNoticeAirspace()
    {
        if (!IsLoaded || NoticeLayer.Visibility != Visibility.Visible
            || PersistentPtyOverlayHost.Visibility != Visibility.Visible
            || NoticeBoundsInPage(NoticeStack, inset: true) is not { } notices)
        {
            ReleaseNoticeAirspace();
            return;
        }

        var covered = new HashSet<OpenCodePersistentPtyRenderer>();
        var pending = new Stack<DependencyObject>();
        pending.Push(PersistentPtyOverlayHost);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is OpenCodePersistentPtyRenderer terminal)
            {
                if (NoticeBoundsInPage(terminal, inset: false) is { } bounds && NoticeRectsOverlap(notices, bounds)) covered.Add(terminal);
                continue;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++) pending.Push(VisualTreeHelper.GetChild(node, index));
        }

        foreach (var held in new List<OpenCodePersistentPtyRenderer>(_noticeAirspace.Keys))
            if (!covered.Contains(held) && _noticeAirspace.Remove(held, out var lease)) lease.Dispose();
        foreach (var terminal in covered)
            if (!_noticeAirspace.ContainsKey(terminal)) _noticeAirspace[terminal] = terminal.SuppressAirspace(snapshot: true);
    }

    /// <summary>元素在 PageRoot 里的矩形；inset 时去掉提示栈四周留给阴影的空白。不在树上或没有尺寸时为 null。</summary>
    private Rect? NoticeBoundsInPage(FrameworkElement element, bool inset)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        try
        {
            var origin = element.TransformToVisual(PageRoot).TransformPoint(new Point(0, 0));
            var pad = inset ? NoticeStack.Padding : new Thickness(0);
            double width = element.ActualWidth - pad.Left - pad.Right, height = element.ActualHeight - pad.Top - pad.Bottom;
            return width <= 0 || height <= 0 ? null : new Rect(origin.X + pad.Left, origin.Y + pad.Top, width, height);
        }
        catch (ArgumentException) { return null; }
    }

    private static bool NoticeRectsOverlap(Rect a, Rect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
}
