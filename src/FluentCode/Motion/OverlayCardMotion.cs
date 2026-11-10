using System;
using System.Numerics;
using Microsoft.UI.Xaml;

namespace FluentCode.Motion;

/// <summary>
/// 浮窗卡片（浏览器卡、终端卡）的“从原位置展开”：卡片以触发按钮为缩放原点，Scale 0.92→1、淡入、
/// 朝触发点方向偏移 12px→0（333ms 减速）；后面的遮罩同步淡入。关闭时 Scale→0.96 加淡出（167ms 加速），
/// 遮罩同步淡出，结束后才回调（由调用方折叠宿主）。全部在合成层进行；卡片本身已用的
/// UIElement.Translation（阴影 Z 抬升）在内层 presenter 上，这里只动外层容器的 handout Visual，二者不混用。
/// </summary>
internal sealed class OverlayCardMotion
{
    private const float OpenScale = 0.92f;
    private const float CloseScale = 0.96f;
    private const float Travel = 12f;
    private readonly UIElement _smoke;
    private readonly FrameworkElement _card;
    private int _generation;

    internal OverlayCardMotion(UIElement smoke, FrameworkElement card)
    {
        _smoke = smoke;
        _card = card;
    }

    /// <summary>当前是否在播放关闭动画（宿主仍可见，但逻辑上已关闭）。</summary>
    internal bool IsClosing { get; private set; }

    /// <summary>
    /// 打开。应在宿主刚设为 Visible 时调用：先把卡片与遮罩置为透明，等到本轮布局完成（卡片尺寸、位置有效）再起动画。
    /// <paramref name="quick"/> 用于浮窗之间切换（250ms 缩短版）。<paramref name="completed"/> 在入场结束（或被取代/减少动画时立即）调用。
    /// </summary>
    internal void Open(FrameworkElement? origin, bool quick, Action? completed)
    {
        int generation = ++_generation;
        ReportSuperseded();
        IsClosing = false;
        SetInteractive(true);
        if (!FluentMotion.Enabled)
        {
            FluentMotion.Settle(_card);
            FluentMotion.Settle(_smoke);
            completed?.Invoke();
            return;
        }

        FluentMotion.Snap(_card, 0f);
        FluentMotion.Snap(_smoke, 0f);

        bool started = false;
        void Start()
        {
            if (started) return;
            started = true;
            _card.LayoutUpdated -= OnLayout;
            if (generation != _generation)
            {
                completed?.Invoke();
                return;
            }
            var (pivot, from) = Pivot(origin);
            var duration = quick ? FluentMotion.Normal : FluentMotion.Slow;
            FluentMotion.Animate(_smoke, 0f, 1f, null, Vector3.Zero, null, 1f, duration, FluentMotionTokens.Decelerate);
            // 入场被新的打开/关闭取代时也要回调：调用方用它归还终端 HWND 的显示租约。
            bool reported = false;
            void Report() { if (!reported) { reported = true; completed?.Invoke(); } }
            FluentMotion.Animate(_card, 0f, 1f, from, Vector3.Zero, OpenScale, 1f, duration, FluentMotionTokens.Decelerate, Report, null, pivot);
            _superseded = Report;
        }
        void OnLayout(object? sender, object args) => Start();
        _card.LayoutUpdated += OnLayout;
        // 布局本轮已有效（宿主早已可见）时 LayoutUpdated 可能不再触发：低优先级兜底。
        _card.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Start);
    }

    private Action? _superseded;

    /// <summary>关闭：动画结束后调用 <paramref name="completed"/>；期间若再次打开，则不会调用。</summary>
    internal void Close(Action completed)
    {
        int generation = ++_generation;
        ReportSuperseded();
        IsClosing = true;
        // A closing card is logically gone: it must never take input while it fades. If the exit's completion is ever
        // lost (superseded/never-ending batch), an invisible Opacity-0 smoke would otherwise cover the whole page and
        // swallow every click in the transcript until something else collapsed the host.
        SetInteractive(false);
        void Done()
        {
            if (generation != _generation || !IsClosing) return;
            IsClosing = false;
            SetInteractive(true);
            completed();
            FluentMotion.Settle(_card);
            FluentMotion.Settle(_smoke);
        }
        if (!FluentMotion.Enabled)
        {
            Done();
            return;
        }
        var (pivot, _) = Pivot(null);
        FluentMotion.Animate(_smoke, null, 0f, null, Vector3.Zero, null, 1f, FluentMotion.Fast, FluentMotionTokens.Accelerate);
        FluentMotion.Animate(_card, null, 0f, null, Vector3.Zero, null, CloseScale, FluentMotion.Fast, FluentMotionTokens.Accelerate, Done, null, pivot);
        // Watchdog: finish the close even if the composition batch never reports completion.
        var watchdog = _card.DispatcherQueue.CreateTimer();
        watchdog.Interval = FluentMotion.Duration(FluentMotion.Fast) + TimeSpan.FromMilliseconds(400);
        watchdog.IsRepeating = false;
        void OnTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            watchdog.Stop();
            watchdog.Tick -= OnTick;
            Done();
        }
        watchdog.Tick += OnTick;
        watchdog.Start();
    }

    /// <summary>立即落定（不播放动画），例如浮窗之间直接切换或页面释放。</summary>
    internal void Reset()
    {
        ++_generation;
        ReportSuperseded();
        IsClosing = false;
        SetInteractive(true);
        FluentMotion.Settle(_card);
        FluentMotion.Settle(_smoke);
    }

    private void SetInteractive(bool interactive)
    {
        _smoke.IsHitTestVisible = interactive;
        _card.IsHitTestVisible = interactive;
    }

    private void ReportSuperseded()
    {
        var report = _superseded;
        _superseded = null;
        report?.Invoke();
    }

    // 缩放原点 = 触发元素中心在卡片内的相对位置（夹在卡片范围内）；起始偏移指向触发点。
    private (Vector2 Pivot, Vector3 From) Pivot(FrameworkElement? origin)
    {
        var pivot = new Vector2(0.5f, 0.5f);
        var from = new Vector3(0, Travel, 0);
        double width = _card.ActualWidth, height = _card.ActualHeight;
        if (origin is null || width <= 0 || height <= 0 || !origin.IsLoaded || origin.Visibility != Visibility.Visible) return (pivot, from);
        try
        {
            var point = origin.TransformToVisual(_card).TransformPoint(new Windows.Foundation.Point(origin.ActualWidth / 2, origin.ActualHeight / 2));
            pivot = new Vector2((float)Math.Clamp(point.X / width, 0, 1), (float)Math.Clamp(point.Y / height, 0, 1));
            var direction = new Vector2((float)(point.X - width / 2), (float)(point.Y - height / 2));
            if (direction.LengthSquared() > 1)
            {
                direction = Vector2.Normalize(direction) * Travel;
                from = new Vector3(direction.X, direction.Y, 0);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return (pivot, from);
    }
}
