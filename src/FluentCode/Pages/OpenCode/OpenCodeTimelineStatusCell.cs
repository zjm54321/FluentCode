using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ArcPath = Microsoft.UI.Xaml.Shapes.Path;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 时间线行右侧的统一状态格（工具行 / 思考行 / 时间线芯片共用，前台与后台任务相同）：
/// 16×16 方格，进行中 = 强调色 2px 圆头 270° 弧，结束后可显示状态字形（✕ / ? 等，由宿主设置 <see cref="Icon"/>）。
/// </summary>
/// <remarks>
/// <para>弧由 B13 共享工作时钟的合成相位驱动（<see cref="OpenCodeWorkingMotion.RingExpression"/>，每 1.8 秒一圈），
/// 相位是全局绝对值：任何时刻开始、重新加载或换父级的行都落在同一角度，所有进行中的行与分组齿轮同相，
/// 不会出现某一行恰好处在“短弧”起步帧的情况（原生 ProgressRing 各自从 0 起步，同屏外观不一致）。</para>
/// <para>系统关闭动画（或应用内减少动画）时时钟停止，弧静止在 0°（仍是可辨认的“进行中”形状）。
/// 高对比度下行底为系统高亮色，弧改用高亮文字色以保持可见。宿主统一放在行根网格最后一列，外边距见 <see cref="CellMargin"/>。</para>
/// </remarks>
internal sealed class OpenCodeTimelineStatusCell : IOpenCodeWorkingClockSubscriber
{
    /// <summary>行根网格中状态格的外边距：右侧与行内边距相同（8），状态中心恒为行右缘 − 16。</summary>
    internal static readonly Thickness CellMargin = new(0, 0, OpenCodeTimelineRowMetrics.Padding, 0);

    private readonly Grid _cell;
    private readonly ArcPath _arc;
    private bool _running;
    private bool _loaded;
    private bool _animating;

    internal OpenCodeTimelineStatusCell()
    {
        var size = OpenCodeWorkingMotion.RingSize;
        Icon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _arc = new ArcPath
        {
            Data = ArcGeometry(),
            Width = size,
            Height = size,
            StrokeThickness = OpenCodeWorkingMotion.RingStroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.None,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAccessibilityView(_arc, AccessibilityView.Raw);
        _cell = new Grid
        {
            Width = size,
            Height = size,
            Margin = CellMargin,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _cell.Children.Add(Icon);
        _cell.Children.Add(_arc);
        ApplyArcBrush();
        _cell.ActualThemeChanged += (_, _) => ApplyArcBrush();
        _cell.Loaded += (_, _) => { _loaded = true; Update(); };
        _cell.Unloaded += (_, _) =>
        {
            if (_cell.IsLoaded) return; // The old parent's unload can follow the new Loaded.
            _loaded = false;
            Update();
        };
    }

    /// <summary>放入行根网格的元素（固定 16×16，带统一外边距）。</summary>
    internal FrameworkElement Element => _cell;

    /// <summary>结束态状态字形（宿主设置字形 / 颜色 / 可见性；进行中时由宿主隐藏）。</summary>
    internal FontIcon Icon { get; }

    internal bool IsRunning => _running;

    /// <summary>切换进行中弧；值不变时不做任何事（不重置动画）。</summary>
    internal void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        _arc.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    /// <summary>宿主释放：离开共享时钟。</summary>
    internal void Dispose()
    {
        _running = false;
        _loaded = false;
        _arc.Visibility = Visibility.Collapsed;
        Update();
    }

    void IOpenCodeWorkingClockSubscriber.OnWorkingClockStateChanged(OpenCodeWorkingClock clock) => Render();

    private static PathGeometry ArcGeometry()
    {
        var start = OpenCodeWorkingMotion.RingArcPoint(0);
        var end = OpenCodeWorkingMotion.RingArcPoint(OpenCodeWorkingMotion.RingSweepDegrees);
        var radius = OpenCodeWorkingMotion.RingRadius;
        var arc = new ArcSegment
        {
            Point = new Point(end.X, end.Y),
            Size = new Size(radius, radius),
            IsLargeArc = OpenCodeWorkingMotion.RingSweepDegrees > 180,
            SweepDirection = SweepDirection.Clockwise,
        };
        var figure = new PathFigure
        {
            StartPoint = new Point(start.X, start.Y),
            IsClosed = false,
            IsFilled = false,
            Segments = new PathSegmentCollection { arc },
        };
        return new PathGeometry { Figures = new PathFigureCollection { figure } };
    }

    private void ApplyArcBrush()
    {
        var highContrast = false;
        try { highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
        catch { }
        // One tone for every running row, foreground or background: "in progress" is accent; background-ness is the
        // "后台" tag and status text. Caution/critical stay reserved for needs-action and failure.
        _arc.Style = OpenCodeThemedXaml.BrushStyle("Path", ("Stroke", highContrast
            ? "SystemColorHighlightTextColorBrush" : OpenCodeToolTheme.Accent));
    }

    private void Update()
    {
        var clock = OpenCodeWorkingClock.Shared;
        if (_running && _loaded)
        {
            OpenCodeWorkingClockComposition.EnsureInitialized();
            if (!clock.Join(this)) Render();
        }
        else
        {
            clock.Leave(this);
            Render();
        }
    }

    private void Render()
    {
        var clock = OpenCodeWorkingClock.Shared;
        if (clock.IsJoined(this) && clock.IsRunning) StartMotion();
        else StopMotion();
    }

    private void StartMotion()
    {
        if (_animating) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(_arc);
            var compositor = visual.Compositor;
            var center = (float)(OpenCodeWorkingMotion.RingSize / 2);
            visual.CenterPoint = new Vector3(center, center, 0);
            var rotation = compositor.CreateExpressionAnimation(OpenCodeWorkingMotion.RingExpression);
            rotation.SetReferenceParameter("clock", OpenCodeWorkingClockComposition.PhaseSet(compositor));
            visual.StartAnimation(nameof(Visual.RotationAngleInDegrees), rotation);
            _animating = true;
        }
        catch
        {
            // Composition unavailable: keep the static arc (decoration only; status text still reads "运行中").
            _animating = true;
            StopMotion();
        }
    }

    /// <summary>静态外观：弧回到 0°（减少动画 / 不在进行中 / 已卸载）。</summary>
    private void StopMotion()
    {
        if (!_animating) return;
        _animating = false;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(_arc);
            visual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            visual.RotationAngleInDegrees = 0;
        }
        catch
        {
        }
    }
}
