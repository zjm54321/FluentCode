using System;
using System.Collections.Generic;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentCode.Pages;

/// <summary>
/// 终端是原生子窗口，画在所有 XAML 之上。需要用户处理的提示与终端重叠时，对该终端持有 SuppressAirspace 租约
/// （保留最后一帧画面），提示移开或关闭后立即归还。被动提示从不要租约：见 OpenCodePage.Notices.Holds.cs。
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

    // 终端出现、消失或提示栈改变大小：重新判断被动提示要不要让开，以及谁需要租约。
    private void NoticeAirspace_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshNoticeLayer();

    private void OnNoticeAirspaceSourceChanged(DependencyObject sender, DependencyProperty property) => RefreshNoticeLayer();

    private void ReleaseNoticeAirspace()
    {
        foreach (var lease in _noticeAirspace.Values) lease.Dispose();
        _noticeAirspace.Clear();
    }

    /// <summary>当前显示着的终端及其在 PageRoot 里的矩形；终端浮层没打开时为空。</summary>
    private List<(OpenCodePersistentPtyRenderer Terminal, Rect Bounds)> NoticeTerminals()
    {
        var found = new List<(OpenCodePersistentPtyRenderer, Rect)>();
        if (!IsLoaded || PersistentPtyOverlayHost.Visibility != Visibility.Visible) return found;
        var pending = new Stack<DependencyObject>();
        pending.Push(PersistentPtyOverlayHost);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is OpenCodePersistentPtyRenderer terminal)
            {
                if (NoticeBoundsInPage(terminal, inset: false) is { } bounds) found.Add((terminal, bounds));
                continue;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++) pending.Push(VisualTreeHelper.GetChild(node, index));
        }
        return found;
    }

    private static bool NoticeOverlaps(Rect a, Rect b)
        => OpenCodeNoticePolicy.Overlaps(a.Left, a.Top, a.Right, a.Bottom, b.Left, b.Top, b.Right, b.Bottom);

    /// <summary>只有需要用户处理的提示才向终端要租约；被动提示不在这里出现（它们重叠时根本不显示）。</summary>
    private void UpdateNoticeAirspace()
    {
        var covered = new HashSet<OpenCodePersistentPtyRenderer>();
        if (NoticeLayer.Visibility == Visibility.Visible)
        {
            var terminals = NoticeTerminals();
            foreach (var (surface, kind) in new (FrameworkElement, OpenCodeNoticeKind)[]
                     {
                         (SshConnectionAlertSurface, OpenCodeNoticeKind.SshAlert),
                         (PermissionBarSurface, OpenCodeNoticeKind.Permission),
                         (MessageBarSurface, OpenCodeNoticeKind.Message),
                     })
            {
                if (terminals.Count == 0) break;
                if (surface.Visibility != Visibility.Visible || NoticeBoundsInPage(surface, inset: false) is not { } bounds) continue;
                foreach (var (terminal, area) in terminals)
                    if (OpenCodeNoticePolicy.TakesTerminal(kind, NoticeMessageErrorOrWarning, MessageBar.ActionButton is not null,
                            NoticeOverlaps(bounds, area)))
                        covered.Add(terminal);
            }
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
}
