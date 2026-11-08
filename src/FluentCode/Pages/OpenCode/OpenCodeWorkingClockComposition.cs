using System;
using System.Collections.Generic;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// B13：共享工作时钟的合成层驱动。每个 Compositor 只有一个属性集，含两个相位：<c>Phase</c>（流光 / 齿轮 / 省略号，
/// 时钟周期）与 <c>RingPhase</c>（原生进度环，<see cref="OpenCodeWorkingClock.RingPeriod"/>），各由一个线性、无限循环的
/// 关键帧动画推进；所有指示器的表达式都引用同一属性集，因此同向、同速、同相由构造保证。
/// 时钟停止时停掉主动画（不再有任何帧驱动）；重新启动时把动画进度对齐到时钟的绝对相位。
/// </summary>
internal static class OpenCodeWorkingClockComposition
{
    private const string PhaseProperty = "Phase";
    private const string RingPhaseProperty = "RingPhase";
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
        set.InsertScalar(RingPhaseProperty, (float)Clock.RingPhase);
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
        Loop(compositor, set, PhaseProperty, Clock.Period, Clock.Phase);
        Loop(compositor, set, RingPhaseProperty, OpenCodeWorkingClock.RingPeriod, Clock.RingPhase);
    }

    private static void Loop(Compositor compositor, CompositionPropertySet set, string property, TimeSpan period, double phase)
    {
        var linear = compositor.CreateLinearEasingFunction();
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, 0f, linear);
        animation.InsertKeyFrame(1f, 1f, linear);
        animation.Duration = period;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        set.StartAnimation(property, animation);
        // 对齐绝对相位：重新启动（例如全部完成后又有新任务）也落在同一条全局时间线上。
        if (set.TryGetAnimationController(property) is { } controller)
        {
            controller.Progress = (float)phase;
        }
    }

    private static void Stop(CompositionPropertySet set)
    {
        set.StopAnimation(PhaseProperty);
        set.InsertScalar(PhaseProperty, (float)Clock.Phase);
        set.StopAnimation(RingPhaseProperty);
        set.InsertScalar(RingPhaseProperty, (float)Clock.RingPhase);
    }
}
