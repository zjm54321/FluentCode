using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace AIFrontend.Motion;

/// <summary>
/// 全应用统一的 Fluent 动效执行层。所有动画只在合成层动 Opacity / Translation / Scale（不做布局动画），
/// 并统一遵守“减少动画”：系统“动画效果”关闭或应用内选择减少时，每个辅助方法都同步落到最终状态
/// （含完成回调），不会留下半透明或偏移的残留。必须在 UI 线程调用。
/// </summary>
internal static class FluentMotion
{
    public static readonly TimeSpan Faster = TimeSpan.FromMilliseconds(FluentMotionTokens.FasterMs);
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(FluentMotionTokens.FastMs);
    public static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(FluentMotionTokens.NormalMs);
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(FluentMotionTokens.SlowMs);
    public static readonly TimeSpan Slower = TimeSpan.FromMilliseconds(FluentMotionTokens.SlowerMs);

    private static readonly double SlowMotion = FluentMotionTokens.ParseSlowMotion(
        Environment.GetEnvironmentVariable(FluentMotionTokens.SlowMotionVariable));
    private static readonly ConditionalWeakTable<UIElement, MotionGeneration> Generations = new();
    private static readonly ConditionalWeakTable<Compositor, Dictionary<MotionCurve, CompositionEasingFunction>> Easings = new();
    private static readonly ConditionalWeakTable<UIElement, Dictionary<string, CompositionAnimation>> Animations = new();
    private static readonly List<(WeakReference<DependencyObject> Owner, Action<bool> Apply)> Bindings = new();

    private static UISettings? _uiSettings;
    private static DispatcherQueue? _dispatcher;
    private static bool _initialized;
    private static bool _systemEnabled = true;
    private static bool _userReduce;

    /// <summary>当前是否播放动效（系统开启且应用内未选择减少）。</summary>
    public static bool Enabled
    {
        get
        {
            EnsureInitialized();
            return FluentMotionPolicy.Resolve(_systemEnabled, _userReduce);
        }
    }

    /// <summary>系统“动画效果”是否开启（设置页用来说明“系统已关闭”）。</summary>
    public static bool SystemEnabled
    {
        get
        {
            EnsureInitialized();
            return _systemEnabled;
        }
    }

    /// <summary><see cref="Enabled"/> 变化时在 UI 线程触发。</summary>
    public static event EventHandler? EnabledChanged;

    /// <summary>首次使用时创建唯一的 <see cref="UISettings"/> 并订阅系统设置变化（之后不再新建）。</summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            _uiSettings = new UISettings();
            _systemEnabled = _uiSettings.AnimationsEnabled;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _uiSettings.AnimationsEnabledChanged += (_, _) => Post(() =>
                {
                    bool before = FluentMotionPolicy.Resolve(_systemEnabled, _userReduce);
                    try { _systemEnabled = _uiSettings?.AnimationsEnabled ?? true; }
                    catch { _systemEnabled = true; }
                    RaiseIfChanged(before);
                });
            }
        }
        catch
        {
            // 读取失败时按“允许动画”处理（与历史 MainWindow.SystemAnimationsEnabled 的回退一致）。
            _systemEnabled = true;
        }
    }

    /// <summary>应用内“减少动画”设置（只能减少）。可从任意线程调用。</summary>
    public static void SetUserReduceMotion(bool reduce) => Post(() =>
    {
        EnsureInitialized();
        bool before = FluentMotionPolicy.Resolve(_systemEnabled, _userReduce);
        _userReduce = reduce;
        RaiseIfChanged(before);
    });

    private static void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }

    private static void RaiseIfChanged(bool before)
    {
        bool now = FluentMotionPolicy.Resolve(_systemEnabled, _userReduce);
        if (before == now) return;
        for (int i = Bindings.Count - 1; i >= 0; i--)
        {
            if (!Bindings[i].Owner.TryGetTarget(out _)) { Bindings.RemoveAt(i); continue; }
            try { Bindings[i].Apply(now); }
            catch { }
        }
        try { EnabledChanged?.Invoke(null, EventArgs.Empty); }
        catch { }
    }

    /// <summary>
    /// 让某个对象的动效配置（ThemeTransition 集合、隐式 Transition 等）跟随 <see cref="Enabled"/>：
    /// 立即按当前值应用一次，之后每次变化重新应用；对象被回收后自动解除（弱引用）。
    /// </summary>
    public static void Bind(DependencyObject owner, Action<bool> apply)
    {
        apply(Enabled);
        Bindings.Add((new WeakReference<DependencyObject>(owner), apply));
    }

    /// <summary>按慢放倍数换算后的时长。</summary>
    public static TimeSpan Duration(TimeSpan duration)
        => FluentMotionTokens.Scale((int)Math.Round(duration.TotalMilliseconds), SlowMotion);

    /// <summary>给 Storyboard/KeyFrame 使用的同一套控制点。</summary>
    public static KeySpline Spline(MotionCurve curve) => new()
    {
        ControlPoint1 = new Windows.Foundation.Point(curve.X1, curve.Y1),
        ControlPoint2 = new Windows.Foundation.Point(curve.X2, curve.Y2),
    };

    /// <summary>每个 Compositor 每条曲线缓存一个缓动对象。</summary>
    public static CompositionEasingFunction Ease(Compositor compositor, MotionCurve curve)
    {
        var map = Easings.GetOrCreateValue(compositor);
        if (!map.TryGetValue(curve, out var easing))
        {
            easing = compositor.CreateCubicBezierEasingFunction(new Vector2(curve.X1, curve.Y1), new Vector2(curve.X2, curve.Y2));
            map[curve] = easing;
        }
        return easing;
    }

    /// <summary>导航过渡：减少动画时返回 Suppress（直接切换）。</summary>
    public static NavigationTransitionInfo PageEntrance()
        => Enabled ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo();

    /// <summary>平级“深入”（例如进入设置）：从右侧滑入；返回时从左侧。</summary>
    public static NavigationTransitionInfo PageSlide(bool fromRight)
        => Enabled
            ? new SlideNavigationTransitionInfo { Effect = fromRight ? SlideNavigationTransitionEffect.FromRight : SlideNavigationTransitionEffect.FromLeft }
            : new SuppressNavigationTransitionInfo();

    /// <summary>83ms 的背景画刷过渡（自绘可点击元素的悬停/按下填充）。</summary>
    public static BrushTransition HoverFill() => new() { Duration = Duration(Faster) };

    /// <summary>
    /// 统一的进出场：从 (fromOpacity, fromTranslation, fromScale) 动到 (toOpacity, toTranslation, toScale)。
    /// 只动 XAML 合成外观属性；不取得 handout Visual，避免与控件/主题的 Scale 等外观属性冲突。
    /// 同一元素上较新的调用会取代旧调用，旧调用的 <paramref name="completed"/> 不会再触发。
    /// 未启用动画时同步设置最终值并调用 <paramref name="completed"/>。
    /// </summary>
    public static void Animate(
        UIElement element,
        float? fromOpacity, float toOpacity,
        Vector3? fromTranslation, Vector3 toTranslation,
        float? fromScale, float toScale,
        TimeSpan duration, MotionCurve curve,
        Action? completed = null,
        TimeSpan? delay = null,
        Vector2? scaleOrigin = null)
    {
        var generation = Generations.GetOrCreateValue(element);
        int token = generation.Next();
        void Finish()
        {
            if (!generation.IsCurrent(token)) return;
            completed?.Invoke();
        }

        void Instant()
        {
            try
            {
                Stop(element, "Opacity");
                Stop(element, "Scale");
                element.Opacity = toOpacity;
                element.Scale = new Vector3(toScale, toScale, 1);
                if (fromTranslation is not null || toTranslation != Vector3.Zero || HasTranslation(element))
                {
                    Stop(element, "Translation");
                    element.Translation = toTranslation;
                }
            }
            catch { }
            Finish();
        }

        FluentMotionPolicy.Run(Enabled, Instant, () =>
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            var easing = Ease(compositor, curve);
            var length = Duration(duration);
            var wait = delay is { } d && d > TimeSpan.Zero ? Duration(d) : TimeSpan.Zero;
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Target = "Opacity";
            if (fromOpacity is { } fo) opacity.InsertKeyFrame(0f, fo);
            opacity.InsertKeyFrame(1f, toOpacity, easing);
            Configure(opacity, length, wait);
            Start(element, opacity);

            if (fromTranslation is not null || toTranslation != Vector3.Zero || HasTranslation(element))
            {
                var translation = compositor.CreateVector3KeyFrameAnimation();
                translation.Target = "Translation";
                if (fromTranslation is { } ft) translation.InsertKeyFrame(0f, ft);
                translation.InsertKeyFrame(1f, toTranslation, easing);
                Configure(translation, length, wait);
                Start(element, translation);
            }

            if (fromScale is not null || toScale != 1f || element.Scale != Vector3.One)
            {
                var origin = scaleOrigin ?? new Vector2(0.5f, 0.5f);
                var size = element is FrameworkElement fe ? new Vector2((float)fe.ActualWidth, (float)fe.ActualHeight) : element.ActualSize;
                element.CenterPoint = new Vector3(size.X * origin.X, size.Y * origin.Y, 0);
                if ((size.X <= 0 || size.Y <= 0) && element is FrameworkElement pending)
                {
                    // 刚插入、尚未布局的元素：首次得到尺寸时再定缩放原点（同一帧内完成，不影响动画）。
                    void Sized(object sender, SizeChangedEventArgs args)
                    {
                        pending.SizeChanged -= Sized;
                        element.CenterPoint = new Vector3((float)args.NewSize.Width * origin.X, (float)args.NewSize.Height * origin.Y, 0);
                    }
                    pending.SizeChanged += Sized;
                }
                var scale = compositor.CreateVector3KeyFrameAnimation();
                scale.Target = "Scale";
                if (fromScale is { } fs) scale.InsertKeyFrame(0f, new Vector3(fs, fs, 1));
                scale.InsertKeyFrame(1f, new Vector3(toScale, toScale, 1), easing);
                Configure(scale, length, wait);
                Start(element, scale);
            }

            batch.Completed += (_, _) => Finish();
            batch.End();
        });
    }

    private static void Configure(KeyFrameAnimation animation, TimeSpan duration, TimeSpan delay)
    {
        animation.Duration = duration;
        if (delay > TimeSpan.Zero)
        {
            animation.DelayTime = delay;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
    }

    private static void Start(UIElement element, CompositionAnimation animation)
    {
        Stop(element, animation.Target);
        element.StartAnimation(animation);
        Animations.GetOrCreateValue(element)[animation.Target] = animation;
    }

    private static void Stop(UIElement element, string property)
    {
        if (Animations.TryGetValue(element, out var animations) && animations.Remove(property, out var animation))
            element.StopAnimation(animation);
    }

    private static bool HasTranslation(UIElement element) => element.Translation != Vector3.Zero;

    /// <summary>取消正在进行的动画并把元素放回静止状态（不透明、无偏移、原尺寸），同步完成。</summary>
    public static void Settle(UIElement element) => Snap(element, 1f);

    /// <summary>取消正在进行的动画，并同步把合成层不透明度设为 <paramref name="opacity"/>（无偏移、原尺寸）；旧动画的完成回调作废。</summary>
    public static void Snap(UIElement element, float opacity)
    {
        Generations.GetOrCreateValue(element).Next();
        try
        {
            Stop(element, "Opacity");
            Stop(element, "Scale");
            Stop(element, "Translation");
            element.Opacity = opacity;
            element.Scale = Vector3.One;
            element.Translation = Vector3.Zero;
        }
        catch { }
    }

    // ---- 常用组合 ----

    /// <summary>淡入（默认 167ms 减速）。</summary>
    public static void FadeIn(UIElement element, TimeSpan? duration = null, float fromOpacity = 0f, Action? completed = null)
        => Animate(element, fromOpacity, 1f, null, Vector3.Zero, null, 1f, duration ?? Fast, FluentMotionTokens.Decelerate, completed);

    /// <summary>淡出（默认 83ms 加速）。结束后调用 <paramref name="completed"/>（例如折叠/移除）。</summary>
    public static void FadeOut(UIElement element, TimeSpan? duration = null, Action? completed = null)
        => Animate(element, null, 0f, null, Vector3.Zero, null, 1f, duration ?? Faster, FluentMotionTokens.Accelerate, completed);

    /// <summary>从偏移 <paramref name="from"/> 升起并淡入（默认 250ms 减速）。</summary>
    public static void SlideFadeIn(UIElement element, Vector3 from, TimeSpan? duration = null, TimeSpan? delay = null, Action? completed = null)
        => Animate(element, 0f, 1f, from, Vector3.Zero, null, 1f, duration ?? Normal, FluentMotionTokens.Decelerate, completed, delay);

    /// <summary>滑向 <paramref name="to"/> 并淡出（默认 167ms 加速）。</summary>
    public static void SlideFadeOut(UIElement element, Vector3 to, TimeSpan? duration = null, Action? completed = null)
        => Animate(element, null, 0f, null, to, null, 1f, duration ?? Fast, FluentMotionTokens.Accelerate, completed);

    /// <summary>从 <paramref name="fromScale"/> 放大并淡入；<paramref name="origin"/> 为相对元素尺寸的缩放原点（0..1）。</summary>
    public static void ScaleFadeIn(UIElement element, float fromScale, Vector2? origin = null, TimeSpan? duration = null, Vector3? fromTranslation = null, Action? completed = null)
        => Animate(element, 0f, 1f, fromTranslation, Vector3.Zero, fromScale, 1f, duration ?? Fast, FluentMotionTokens.Decelerate, completed, null, origin);

    /// <summary>缩小到 <paramref name="toScale"/> 并淡出（默认 83ms 加速）。</summary>
    public static void ScaleFadeOut(UIElement element, float toScale, Vector2? origin = null, TimeSpan? duration = null, Action? completed = null)
        => Animate(element, null, 0f, null, Vector3.Zero, null, toScale, duration ?? Faster, FluentMotionTokens.Accelerate, completed, null, origin);

    /// <summary>显示：先设为 Visible，再从 <paramref name="from"/> 滑入淡入。已可见且未在退出时不重播。</summary>
    public static void Show(UIElement element, Vector3 from, TimeSpan? duration = null, float fromScale = 1f)
    {
        bool wasHidden = element.Visibility != Visibility.Visible || IsHiding(element);
        SetHiding(element, false);
        element.Visibility = Visibility.Visible;
        if (!wasHidden) return;
        Animate(element, 0f, 1f, from, Vector3.Zero, fromScale == 1f ? null : fromScale, 1f, duration ?? Fast, FluentMotionTokens.Decelerate);
    }

    /// <summary>隐藏：滑向 <paramref name="to"/> 淡出，结束后（且期间没有再次显示）才折叠。</summary>
    public static void Hide(UIElement element, Vector3 to, TimeSpan? duration = null, Action? completed = null)
    {
        if (element.Visibility != Visibility.Visible)
        {
            completed?.Invoke();
            return;
        }
        if (IsHiding(element)) return;
        SetHiding(element, true);
        Animate(element, null, 0f, null, to, null, 1f, duration ?? Faster, FluentMotionTokens.Accelerate, () =>
        {
            SetHiding(element, false);
            element.Visibility = Visibility.Collapsed;
            Settle(element);
            completed?.Invoke();
        });
    }

    private static readonly ConditionalWeakTable<UIElement, object> Hiding = new();

    private static bool IsHiding(UIElement element) => Hiding.TryGetValue(element, out _);

    private static void SetHiding(UIElement element, bool hiding)
    {
        if (hiding) Hiding.AddOrUpdate(element, new object());
        else Hiding.Remove(element);
    }

    /// <summary>
    /// 按下缩放（Fluent 只用于卡片/磁贴/大按钮）：按下 83ms 缩到 <paramref name="pressed"/>，松开 167ms 回到 1。
    /// 用 UIElement.Scale 外观属性加 ScaleTransition（与 <see cref="ChipEntrance"/> 同一套，不碰 handout Visual），
    /// 只改渲染、不改布局尺寸；命中测试按布局边界进行，缩放期间命中区不变。以 handledEventsToo 监听且不标记 Handled、
    /// 不捕获指针，因此按钮点击与触摸平移照常（平移开始时 PointerCaptureLost/Canceled 立即回弹）。减少动画时不缩放。
    /// </summary>
    public static void PressScale(FrameworkElement element, float pressed = 0.97f)
    {
        void Set(float value, TimeSpan duration)
        {
            if (!Enabled) { element.ScaleTransition = null; element.Scale = Vector3.One; return; }
            element.ScaleTransition = new Vector3Transition { Duration = Duration(duration) };
            element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            element.Scale = new Vector3(value, value, 1);
        }
        void Down(object sender, PointerRoutedEventArgs args) => Set(pressed, Faster);
        void Up(object sender, PointerRoutedEventArgs args) { if (element.Scale != Vector3.One) Set(1f, Fast); }
        element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Down), true);
        element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Up), true);
        element.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Up), true);
        element.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Up), true);
        element.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(Up), true);
    }

    /// <summary>
    /// 绕 <paramref name="center"/>（元素坐标）从 <paramref name="fromDegrees"/> 转回 0°（默认 250ms Point-to-point），
    /// 用于指针/表盘这类“位置在屏内移动”的小图形。只动合成外观属性 Rotation；减少动画时直接落定。
    /// </summary>
    public static void RotateIn(UIElement element, float fromDegrees, Vector2 center, TimeSpan? duration = null)
    {
        if (!Enabled || Math.Abs(fromDegrees) < 0.01f) return;
        try
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            element.CenterPoint = new Vector3(center, 0);
            var turn = compositor.CreateScalarKeyFrameAnimation();
            turn.Target = "Rotation";
            turn.InsertKeyFrame(0f, fromDegrees);
            turn.InsertKeyFrame(1f, 0f, Ease(compositor, FluentMotionTokens.PointToPoint));
            turn.Duration = Duration(duration ?? Normal);
            Start(element, turn);
        }
        catch { }
    }

    /// <summary>
    /// 小块/chip 的出现（167ms，Scale 0.9→1 加淡入，原点在中心），并让悬停上浮（UIElement.Translation）以 83ms 补间。
    /// 只用 UIElement 的 Opacity/Scale/Translation 外观属性及其隐式 Transition（同一元素不混用 handout Visual），
    /// 适合本身已经使用 <see cref="UIElement.Translation"/> 的元素。减少动画时直接显示且不挂 Transition。
    /// </summary>
    public static void ChipEntrance(FrameworkElement element, float fromScale = 0.9f)
    {
        Bind(element, enabled =>
        {
            element.OpacityTransition = enabled ? new ScalarTransition { Duration = Duration(Fast) } : null;
            element.ScaleTransition = enabled ? new Vector3Transition { Duration = Duration(Fast) } : null;
            element.TranslationTransition = enabled ? new Vector3Transition { Duration = Duration(Faster) } : null;
        });
        if (!Enabled) return;
        element.Opacity = 0;
        element.Scale = new Vector3(fromScale, fromScale, 1);
        void Appear(object sender, RoutedEventArgs args)
        {
            element.Loaded -= Appear;
            element.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
            element.Opacity = 1;
            element.Scale = Vector3.One;
        }
        element.Loaded += Appear;
    }

    /// <summary>
    /// 由 XAML 的合成层重定位过渡补间布局位置变化；不取得元素 Visual，保留 chip 的 Scale/Translation 外观属性。
    /// 减少动画时移除过渡。应在元素首次布局之后调用，避免首帧从原点飞入。
    /// </summary>
    public static void EnableImplicitOffset(UIElement element)
    {
        if (RepositionBound.TryGetValue(element, out _)) return;
        RepositionBound.Add(element, new object());
        Bind(element, enabled =>
        {
            element.Transitions = enabled
                ? new TransitionCollection { new RepositionThemeTransition() }
                : new TransitionCollection();
        });
    }

    private static readonly ConditionalWeakTable<UIElement, object> RepositionBound = new();

    /// <summary>
    /// 给元素设置会跟随“减少动画”的 ThemeTransition 集合：启用时用 <paramref name="factory"/> 的集合，减少时清空。
    /// </summary>
    public static void BindChildrenTransitions(Microsoft.UI.Xaml.Controls.Panel panel, Func<TransitionCollection> factory)
        => Bind(panel, enabled => panel.ChildrenTransitions = enabled ? factory() : new TransitionCollection());

    /// <summary>同上，作用于 ContentControl/ContentPresenter/Border 的 ContentTransitions（经 setter 传入）。</summary>
    public static void BindTransitions(DependencyObject owner, Action<TransitionCollection> set, Func<TransitionCollection> factory)
        => Bind(owner, enabled => set(enabled ? factory() : new TransitionCollection()));
}
