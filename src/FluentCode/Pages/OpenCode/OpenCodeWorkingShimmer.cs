using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// B13：共享工作时钟的合成层驱动。每个 Compositor 只有一个属性集 <c>Phase</c>，由一个线性、无限循环的
/// 关键帧动画推进；所有流光指示器的表达式都引用同一属性集，因此同向、同速、同相由构造保证。
/// 时钟停止时停掉主动画（不再有任何帧驱动）；重新启动时把动画进度对齐到时钟的绝对相位。
/// </summary>
internal static class OpenCodeWorkingClockComposition
{
    private const string PhaseProperty = "Phase";
    private static readonly Dictionary<Compositor, CompositionPropertySet> PhaseSets = new();
    private static bool _hooked;
    private static DispatcherQueue? _dispatcher;
    private static DispatcherQueueTimer? _secondsTimer;

    private static OpenCodeWorkingClock Clock => OpenCodeWorkingClock.Shared;

    /// <summary>首次使用时接上运行状态与系统“显示动画”设置（之后由系统事件更新）。</summary>
    internal static void EnsureInitialized()
    {
        if (_hooked) return;
        _hooked = true;
        Clock.RunningChanged += OnRunningChanged;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Clock.SecondsRunningChanged += OnSecondsRunningChanged;
        OnSecondsRunningChanged(Clock.SecondsRunning);
        // 系统“动画效果”与应用内“减少动画”统一由 FluentMotion 判定（单一 UISettings 实例），事件已在 UI 线程。
        Clock.SetMotionEnabled(FluentCode.Motion.FluentMotion.Enabled);
        FluentCode.Motion.FluentMotion.EnabledChanged += (_, _) =>
        {
            try { Clock.SetMotionEnabled(FluentCode.Motion.FluentMotion.Enabled); }
            catch { }
        };
    }

    /// <summary>取得（必要时创建）该 Compositor 上的共享相位属性集；时钟运行中则立即在当前全局相位上接续。</summary>
    internal static CompositionPropertySet PhaseSet(Compositor compositor)
    {
        if (PhaseSets.TryGetValue(compositor, out var set)) return set;
        set = compositor.CreatePropertySet();
        set.InsertScalar(PhaseProperty, (float)Clock.Phase);
        PhaseSets[compositor] = set;
        if (Clock.IsRunning) Start(compositor, set);
        return set;
    }

    private static void OnRunningChanged(bool running)
    {
        foreach (var pair in PhaseSets)
        {
            try
            {
                if (running) Start(pair.Key, pair.Value);
                else Stop(pair.Value);
            }
            catch
            {
                // 合成对象随窗口关闭失效时只影响装饰动画，不影响会话状态。
            }
        }
    }

    private static void OnSecondsRunningChanged(bool running)
    {
        if (!running) { _secondsTimer?.Stop(); return; }
        if (_secondsTimer is null && _dispatcher is { } queue)
        {
            _secondsTimer = queue.CreateTimer();
            _secondsTimer.Interval = TimeSpan.FromSeconds(1);
            _secondsTimer.IsRepeating = true;
            _secondsTimer.Tick += (_, _) => Clock.TickSeconds(DateTimeOffset.UtcNow);
        }
        _secondsTimer?.Start();
    }

    private static void Start(Compositor compositor, CompositionPropertySet set)
    {
        var linear = compositor.CreateLinearEasingFunction();
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0f, linear);
        animation.InsertKeyFrame(1f, 1f, linear);
        animation.Duration = Clock.Period;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        set.StartAnimation(PhaseProperty, animation);
        // 对齐绝对相位：重新启动（例如全部完成后又有新任务）也落在同一条全局时间线上。
        if (set.TryGetAnimationController(PhaseProperty) is { } controller)
        {
            controller.Progress = (float)Clock.Phase;
        }
    }

    private static void Stop(CompositionPropertySet set)
    {
        set.StopAnimation(PhaseProperty);
        set.InsertScalar(PhaseProperty, (float)Clock.Phase);
    }
}

/// <summary>
/// B13：标签页头部 working 流光（不定进度，非百分比）。挂在标题下方的 2px 细轨，一段柔边高亮
/// 按共享时钟的归一化相位从左扫到右；不同标题宽度按比例齐步。
/// </summary>
/// <remarks>
/// <para>不参与布局：宿主是标签头 StackPanel 末尾的零宽 Canvas（左外边距抵消 Spacing），轨道绘制在标题下方，
/// 标签宽度与高度（A6）保持不变；头部结构仍是 [环, 未读点, 标题]。</para>
/// <para>只有“已加载 + working”时加入共享时钟；卸载（标签栏卸载、标签移除）即离开，最后一个离开时时钟停机。
/// 系统关闭动画时降级为静态柔和细线。</para>
/// </remarks>
internal sealed class OpenCodeWorkingShimmer : IOpenCodeWorkingClockSubscriber
{
    private const double SegmentFraction = 0.38;
    private const double BarHeight = 2;
    private const double BarGap = 3;
    private const double TrackOpacity = 0.2;
    private const double StaticOpacity = 0.55;

    private readonly StackPanel _header;
    private readonly Canvas _host;
    private readonly Grid _track;
    private readonly Border _trackFill;
    private readonly Grid _highlight;
    private readonly RectangleGeometry _clip = new();
    private Visual? _highlightVisual;
    private bool _working;
    private bool _loaded;
    private bool _animating;
    private double _width;

    internal OpenCodeWorkingShimmer(StackPanel header)
    {
        _header = header;
        var accent = OpenCodeToolTheme.Border(OpenCodeToolTheme.Accent);
        _trackFill = new Border { Style = accent, Opacity = TrackOpacity, CornerRadius = new CornerRadius(1) };
        _highlight = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        // 柔边：两侧淡、中间实，近似渐变而仍跟随主题强调色（ThemeResource）。
        for (var column = 0; column < 3; column++)
        {
            var part = new Border { Style = accent, Opacity = column == 1 ? 1 : 0.45, CornerRadius = new CornerRadius(1) };
            Grid.SetColumn(part, column);
            _highlight.Children.Add(part);
        }

        _track = new Grid { Height = BarHeight, Clip = _clip, IsHitTestVisible = false, Children = { _trackFill, _highlight } };
        _host = new Canvas
        {
            Width = 0,
            Margin = new Thickness(-header.Spacing, 0, 0, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Children = { _track },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_host, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        header.Children.Add(_host);
        header.SizeChanged += (_, _) => Layout();
        _host.Loaded += (_, _) => { _loaded = true; Update(); };
        _host.Unloaded += (_, _) =>
        {
            if (_host.IsLoaded) return; // A newer parent already loaded this same tab header.
            _loaded = false;
            Update();
        };
    }

    internal bool IsWorking => _working;

    /// <summary>由真实会话状态驱动：仅 Working（非等待处理）时显示流光。</summary>
    internal void SetWorking(bool working)
    {
        if (_working == working) return;
        _working = working;
        _host.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        Update();
    }

    void IOpenCodeWorkingClockSubscriber.OnWorkingClockStateChanged(OpenCodeWorkingClock clock) => Render();

    private void Update()
    {
        var clock = OpenCodeWorkingClock.Shared;
        if (_working && _loaded)
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
        var moving = clock.IsJoined(this) && clock.IsRunning && _width > 0;
        _trackFill.Opacity = clock.MotionEnabled ? TrackOpacity : StaticOpacity;
        if (moving) StartHighlight();
        else StopHighlight();
    }

    private void Layout()
    {
        var width = Math.Max(0, _header.ActualWidth);
        _width = width;
        Canvas.SetLeft(_track, -width);
        Canvas.SetTop(_track, _header.ActualHeight + BarGap);
        _track.Width = width;
        _highlight.Width = width * SegmentFraction;
        _highlight.Height = BarHeight;
        _clip.Rect = new Rect(0, 0, width, BarHeight);
        // 宽度变化：以新宽度重新绑定表达式（相位仍来自共享时钟，不会从 0 重来，也不影响其他标签）。
        StopHighlight();
        Render();
    }

    private void StartHighlight()
    {
        if (_animating) return;
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_highlight, true);
            _highlightVisual ??= ElementCompositionPreview.GetElementVisual(_highlight);
            var compositor = _highlightVisual.Compositor;
            var expression = compositor.CreateExpressionAnimation(OpenCodeWorkingClock.HighlightOffsetExpression);
            expression.SetReferenceParameter("clock", OpenCodeWorkingClockComposition.PhaseSet(compositor));
            expression.SetScalarParameter("s", (float)SegmentFraction);
            expression.SetScalarParameter("w", (float)_width);
            _highlightVisual.StartAnimation("Translation", expression);
            _highlight.Visibility = Visibility.Visible;
            _animating = true;
        }
        catch
        {
            StopHighlight();
        }
    }

    private void StopHighlight()
    {
        _highlight.Visibility = Visibility.Collapsed;
        if (!_animating) return;
        _animating = false;
        try { _highlightVisual?.StopAnimation("Translation"); }
        catch { }
    }
}
