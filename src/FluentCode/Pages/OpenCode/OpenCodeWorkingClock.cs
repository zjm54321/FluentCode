using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FluentCode.Pages.OpenCode;

/// <summary>加入共享工作时钟的指示器：时钟运行/减少动画状态变化时收到通知（UI 线程）。</summary>
internal interface IOpenCodeWorkingClockSubscriber
{
    void OnWorkingClockStateChanged(OpenCodeWorkingClock clock);
}

/// <summary>
/// B13：标签页 working 不定进度指示器的唯一主动画时钟（纯逻辑，不依赖 WinUI，便于测试与 B17 复用）。
/// </summary>
/// <remarks>
/// <para>
/// 相位是“绝对”的：<c>phase = (单调时间戳 mod 周期) / 周期</c>，取自进程级 <see cref="Stopwatch"/>。
/// 因此任何指示器在任何时刻加入都直接落在当前全局相位上，不存在“从 0 起步”；
/// 加入/离开只改变订阅集合，不会通知或重置其他订阅者。
/// </para>
/// <para>
/// 时钟仅在“至少一个已加载且处于 working 的订阅者 + 系统允许动画”时运行（<see cref="IsRunning"/>）；
/// 没有订阅者（含标签栏卸载导致全部离开）或启用减少动画时停止，渲染层据此停掉合成动画，零空转。
/// 时钟完全由本地时间驱动，不发起任何网络请求。
/// </para>
/// </remarks>
internal sealed class OpenCodeWorkingClock
{
    /// <summary>一个完整流光周期（左 → 右扫过一次）。</summary>
    internal static readonly TimeSpan DefaultPeriod = TimeSpan.FromMilliseconds(1800);

    /// <summary>进程内共享实例（所有标签页同一时钟；仅在 UI 线程使用）。</summary>
    internal static OpenCodeWorkingClock Shared { get; } = new(Stopwatch.GetTimestamp, Stopwatch.Frequency, DefaultPeriod);

    private readonly Func<long> _timestamp;
    private readonly long _periodTicks;
    private readonly HashSet<IOpenCodeWorkingClockSubscriber> _subscribers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Action<DateTimeOffset>> _secondSubscribers = new();
    private bool _motionEnabled = true;
    private bool _hostVisible = true;

    internal OpenCodeWorkingClock(Func<long> timestamp, long frequency, TimeSpan period)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
        Period = period;
        _periodTicks = Math.Max(1, (long)(period.TotalSeconds * frequency));
    }

    internal TimeSpan Period { get; }

    internal int SubscriberCount => _subscribers.Count;

    /// <summary>系统“显示动画”设置；false 时所有指示器降级为静态柔和提示且时钟停止。</summary>
    internal bool MotionEnabled => _motionEnabled;

    /// <summary>是否需要渲染层驱动帧（有订阅者且允许动画）。</summary>
    internal bool IsRunning => _subscribers.Count > 0 && _motionEnabled && _hostVisible;

    /// <summary>启动/停止次数（诊断与测试：证明加入/离开不会重启正在运行的时钟）。</summary>
    internal int StartCount { get; private set; }
    internal int StopCount { get; private set; }

    /// <summary>运行状态翻转时触发（参数为新的 <see cref="IsRunning"/>）。</summary>
    internal event Action<bool>? RunningChanged;

    // Semantic countdowns continue with reduced motion; only visible owners subscribe.
    internal bool SecondsRunning => _secondSubscribers.Count > 0 && _hostVisible;
    internal event Action<bool>? SecondsRunningChanged;
    internal event Action<bool>? HostVisibilityChanged;
    internal bool HostVisible => _hostVisible;

    internal void JoinSeconds(Action<DateTimeOffset> subscriber)
    {
        if (!_secondSubscribers.Add(subscriber)) return;
        if (_secondSubscribers.Count == 1 && _hostVisible) SecondsRunningChanged?.Invoke(true);
        if (_hostVisible) subscriber(DateTimeOffset.UtcNow);
    }

    internal void LeaveSeconds(Action<DateTimeOffset> subscriber)
    {
        if (_secondSubscribers.Remove(subscriber) && _secondSubscribers.Count == 0 && _hostVisible)
            SecondsRunningChanged?.Invoke(false);
    }

    internal void TickSeconds(DateTimeOffset now)
    {
        if (!_hostVisible) return;
        foreach (var subscriber in new List<Action<DateTimeOffset>>(_secondSubscribers))
            if (_secondSubscribers.Contains(subscriber)) subscriber(now);
    }

    /// <summary>当前全局归一化相位 [0, 1)。</summary>
    internal double Phase => PhaseAt(_timestamp());

    internal double PhaseAt(long timestamp)
    {
        var t = timestamp % _periodTicks;
        if (t < 0) t += _periodTicks;
        return (double)t / _periodTicks;
    }

    internal bool IsJoined(IOpenCodeWorkingClockSubscriber subscriber) => _subscribers.Contains(subscriber);

    internal void SetHostVisible(bool visible)
    {
        if (_hostVisible == visible) return;
        var running = IsRunning;
        var seconds = SecondsRunning;
        _hostVisible = visible;
        RaiseRunningIfChanged(running);
        if (seconds != SecondsRunning) SecondsRunningChanged?.Invoke(SecondsRunning);
        HostVisibilityChanged?.Invoke(visible);
        foreach (var subscriber in new List<IOpenCodeWorkingClockSubscriber>(_subscribers))
            if (_subscribers.Contains(subscriber)) subscriber.OnWorkingClockStateChanged(this);
        if (visible) TickSeconds(DateTimeOffset.UtcNow);
    }

    /// <summary>加入时钟：只通知加入者本身（让它在当前全局相位上起步），不打扰其他订阅者。</summary>
    internal bool Join(IOpenCodeWorkingClockSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var wasRunning = IsRunning;
        if (!_subscribers.Add(subscriber)) return false;
        RaiseRunningIfChanged(wasRunning);
        subscriber.OnWorkingClockStateChanged(this);
        return true;
    }

    /// <summary>离开时钟：最后一个离开时时钟停止；其他订阅者不受影响。</summary>
    internal bool Leave(IOpenCodeWorkingClockSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var wasRunning = IsRunning;
        if (!_subscribers.Remove(subscriber)) return false;
        RaiseRunningIfChanged(wasRunning);
        return true;
    }

    /// <summary>更新系统动画设置；变化时通知所有订阅者切换动态/静态外观。</summary>
    internal void SetMotionEnabled(bool enabled)
    {
        if (_motionEnabled == enabled) return;
        var wasRunning = IsRunning;
        _motionEnabled = enabled;
        RaiseRunningIfChanged(wasRunning);
        foreach (var subscriber in new List<IOpenCodeWorkingClockSubscriber>(_subscribers))
        {
            if (_subscribers.Contains(subscriber)) subscriber.OnWorkingClockStateChanged(this);
        }
    }

    /// <summary>
    /// 宽度无关的流光位置：高亮段宽 = <paramref name="segmentFraction"/> × 轨道宽，
    /// 相位 0 时完全位于轨道左侧之外、相位 1 时完全移出右侧；返回高亮段左边缘相对轨道的 X。
    /// 同一相位下 <c>offset / trackWidth</c> 与宽度无关，不同宽度的标签齐步。
    /// </summary>
    internal static double HighlightOffset(double phase, double trackWidth, double segmentFraction)
        => (phase * (1 + segmentFraction) - segmentFraction) * trackWidth;

    /// <summary>与 <see cref="HighlightOffset"/> 等价的合成表达式（clock.Phase 来自共享属性集，s = 段比例，w = 轨道宽）。</summary>
    internal const string HighlightOffsetExpression = "Vector3((clock.Phase * (1 + s) - s) * w, 0, 0)";

    private void RaiseRunningIfChanged(bool wasRunning)
    {
        var running = IsRunning;
        if (running == wasRunning) return;
        if (running) StartCount++; else StopCount++;
        RunningChanged?.Invoke(running);
    }
}
