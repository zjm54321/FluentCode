using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 时间线行右侧的统一状态格（工具行 / 思考行 / 时间线芯片共用，前台与后台任务相同）：
/// 16×16 方格，进行中 = 系统默认不定进度环（与左侧子智能体列表同一个 14 像素环），结束后可显示状态字形
/// （✕ / ? 等，由宿主设置 <see cref="Icon"/>）。
/// </summary>
/// <remarks>
/// <para>环由 <see cref="OpenCodeProgressRingSync"/> 创建：外观完全是控件默认，只是播放进度接到共享工作时钟，
/// 所以同屏所有进行中的行与左侧子智能体列表的环节奏、相位一致；任何时刻开始、重新加载或换父级的行都直接落在当前全局相位上。</para>
/// <para>系统关闭动画（或应用内减少动画）时环静止在一段弧上（仍是可辨认的“进行中”）。环只在进行中时创建，
/// 结束即移除，历史行不持有进度环。高对比度下进行中的行底为系统高亮色，环改用高亮文字色以保持可见。
/// 宿主统一放在行根网格最后一列，外边距见 <see cref="CellMargin"/>。</para>
/// </remarks>
internal sealed class OpenCodeTimelineStatusCell
{
    /// <summary>行根网格中状态格的外边距：右侧与行内边距相同（8），状态中心恒为行右缘 − 16。</summary>
    internal static readonly Thickness CellMargin = new(0, 0, OpenCodeTimelineRowMetrics.Padding, 0);

    private readonly Grid _cell;
    private ProgressRing? _ring;
    private bool _running;

    internal OpenCodeTimelineStatusCell()
    {
        var size = OpenCodeWorkingMotion.StatusCellSize;
        Icon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _cell = new Grid
        {
            Width = size,
            Height = size,
            Margin = CellMargin,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _cell.Children.Add(Icon);
        _cell.ActualThemeChanged += (_, _) => ApplyRingBrush();
    }

    /// <summary>放入行根网格的元素（固定 16×16，带统一外边距）。</summary>
    internal FrameworkElement Element => _cell;

    /// <summary>结束态状态字形（宿主设置字形 / 颜色 / 可见性；进行中时由宿主隐藏）。</summary>
    internal FontIcon Icon { get; }

    internal bool IsRunning => _running;

    /// <summary>切换进行中环；值不变时不做任何事（不重建环、不打断播放）。</summary>
    internal void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        if (running)
        {
            // Created on demand (centred in the cell by the default style): settled and history rows never hold a ring.
            _ring = OpenCodeProgressRingSync.Create();
            ApplyRingBrush();
            _cell.Children.Add(_ring);
        }
        else if (_ring is { } ring)
        {
            _ring = null;
            ring.IsActive = false; // Leaves the shared clock even if the row is already out of the tree.
            _cell.Children.Remove(ring);
        }
    }

    /// <summary>宿主释放：移除进度环（随之离开共享时钟）。</summary>
    internal void Dispose() => SetRunning(false);

    private void ApplyRingBrush()
    {
        if (_ring is not { } ring) return;
        var highContrast = false;
        try { highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { }
        // One tone for every running row, foreground or background: the ring's default accent, exactly as in the sidebar.
        // Background-ness is the "后台" tag and status text; caution/critical stay reserved for needs-action and failure.
        // High contrast fills a running row with the system highlight, so the ring takes the highlight text color there.
        if (highContrast) OpenCodeToolTheme.Apply(ring, OpenCodeToolTheme.Ring("SystemColorHighlightTextColorBrush"));
        else ring.ClearValue(FrameworkElement.StyleProperty);
    }
}
