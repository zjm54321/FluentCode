using System;

namespace FluentCode.Motion;

/// <summary>贝塞尔缓动的两个控制点（与 CSS cubic-bezier(x1, y1, x2, y2) 同义）。</summary>
internal readonly record struct MotionCurve(float X1, float Y1, float X2, float Y2);

/// <summary>
/// Fluent 2 动效 token 与“减少动画”判定。纯逻辑、不依赖 WinUI，便于离线单元测试；
/// 合成层/XAML 侧的执行在 <see cref="FluentMotion"/>。
/// </summary>
internal static class FluentMotionTokens
{
    /// <summary>悬停填充、小控件状态（Fluent “Faster”）。</summary>
    public const int FasterMs = 83;
    /// <summary>退出、收起、小元素进场（Fluent “Fast”）。</summary>
    public const int FastMs = 167;
    /// <summary>大多数进场与位置移动（Fluent “Normal”）。</summary>
    public const int NormalMs = 250;
    /// <summary>浮窗卡片、页面（Fluent “Slow”）。</summary>
    public const int SlowMs = 333;
    /// <summary>连接动画/启动（Fluent “Slower”）。</summary>
    public const int SlowerMs = 500;

    /// <summary>进场：FastOutSlowIn / Decelerate，cubic-bezier(0, 0, 0, 1)。</summary>
    public static readonly MotionCurve Decelerate = new(0f, 0f, 0f, 1f);
    /// <summary>退场：Accelerate，cubic-bezier(1, 0, 1, 1)。</summary>
    public static readonly MotionCurve Accelerate = new(1f, 0f, 1f, 1f);
    /// <summary>屏内位置移动：Point-to-point，cubic-bezier(0.55, 0.55, 0, 1)。</summary>
    public static readonly MotionCurve PointToPoint = new(0.55f, 0.55f, 0f, 1f);

    /// <summary>设计期全局慢放倍数的环境变量（例如 5 = 慢放五倍，用于录屏检查）。</summary>
    public const string SlowMotionVariable = "FC_MOTION_SLOWMO";

    /// <summary>解析慢放倍数：非法、非正或缺省时为 1；上限 20，避免误设导致界面“卡住”。</summary>
    public static double ParseSlowMotion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 1;
        if (!double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var factor)) return 1;
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return 1;
        return Math.Min(factor, 20);
    }

    /// <summary>按慢放倍数换算时长（毫秒取整，至少 1ms）。</summary>
    public static TimeSpan Scale(int milliseconds, double slowMotion)
        => TimeSpan.FromMilliseconds(Math.Max(1, Math.Round(Math.Max(0, milliseconds) * (slowMotion > 0 ? slowMotion : 1))));
}

/// <summary>
/// “减少动画”判定：系统“动画效果”关闭时一律关闭；应用内设置只能进一步减少，不能在系统关闭时强制打开。
/// </summary>
internal static class FluentMotionPolicy
{
    public static bool Resolve(bool systemAnimationsEnabled, bool userReduceMotion)
        => systemAnimationsEnabled && !userReduceMotion;

    /// <summary>
    /// 动效短路：未启用时同步执行 <paramref name="instant"/>（直接到最终状态，含完成回调）并返回 false；
    /// 启用时执行 <paramref name="animate"/>，其抛出异常时退回 <paramref name="instant"/>，保证状态始终落定。
    /// </summary>
    public static bool Run(bool enabled, Action instant, Action animate)
    {
        if (!enabled)
        {
            instant();
            return false;
        }

        try
        {
            animate();
            return true;
        }
        catch (Exception error)
        {
            Failed?.Invoke(error);
            instant();
            return false;
        }
    }

    /// <summary>诊断钩子：动画路径抛出异常（已退回直接落定）时调用。默认无。</summary>
    public static Action<Exception>? Failed { get; set; }
}

/// <summary>
/// 每个元素一份的动画代际：新动画开始时递增；旧动画的完成回调只在代际未变时生效，
/// 防止快速连点“出现→消失→出现”时旧的消失回调把元素折叠掉。
/// </summary>
internal sealed class MotionGeneration
{
    private int _value;

    public int Current => _value;

    public int Next() => ++_value;

    public bool IsCurrent(int generation) => generation == _value;
}
