using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 系统默认的不定进度环（WinUI <see cref="ProgressRing"/>：默认模板、描边比例与弧线伸长 → 收缩动画均不改），
/// 只把它的播放进度接到 B13 共享工作时钟，使同屏所有环（时间线右侧状态格、左侧子智能体列表、详情里的进行中行）节奏、相位一致。
/// </summary>
/// <remarks>
/// <para>原生环每次加载 / 激活都会让模板里的 AnimatedVisualPlayer 从 0 开始循环播放（<c>PlayAsync(0, 1, looped)</c>），
/// 所以同屏的环各走各的相位。这里只用公开 API：播放器一开始播放（<see cref="AnimatedVisualPlayer.IsPlaying"/> = true）就
/// <see cref="AnimatedVisualPlayer.Pause"/> 这次播放（之后窗口隐藏 / 恢复也不会再自行续播），并把该播放动画控制器的
/// Progress 绑定到共享属性集的 <c>RingPhase</c>（与默认动画内部“进度绑定动画”相同的做法）。所有环引用同一属性集，
/// 周期与系统默认相同（<see cref="OpenCodeWorkingClock.RingPeriod"/>），任何时刻加入的环直接落在当前全局相位上，零新增计时器。</para>
/// <para>系统关闭动画（或应用内减少动画）以及窗口隐藏时时钟停止，环停在 <see cref="OpenCodeWorkingMotion.RingStaticProgress"/>
/// 这一帧（静止的弧，不是空白），不再产生帧。颜色仍由控件 Foreground 决定（默认强调色；宿主可按高对比度覆盖）。</para>
/// </remarks>
internal sealed class OpenCodeProgressRingSync : IOpenCodeWorkingClockSubscriber
{
    private const string ProgressProperty = "Progress";

    private readonly ProgressRing _ring;
    private AnimatedVisualPlayer? _player;
    private long _playingToken;
    private bool _loaded;

    private OpenCodeProgressRingSync(ProgressRing ring)
    {
        _ring = ring;
        ring.Loaded += (_, _) =>
        {
            _loaded = true;
            HookPlayer();
            Update();
        };
        ring.Unloaded += (_, _) =>
        {
            if (ring.IsLoaded) return; // The old parent's unload can follow the new Loaded.
            _loaded = false;
            Update();
        };
        ring.RegisterPropertyChangedCallback(ProgressRing.IsActiveProperty, (_, _) => Update());
        ring.RegisterPropertyChangedCallback(ProgressRing.IsIndeterminateProperty, (_, _) => Update());
    }

    /// <summary>
    /// 新建一个已接入共享时钟的默认不定进度环（默认直径 = 左侧子智能体列表的 14 像素）。
    /// 环本身是无障碍树中的内容元素，因此总带名称。
    /// </summary>
    internal static ProgressRing Create(double size = OpenCodeWorkingMotion.RingSize, string accessibleName = OpenCodeWorkingMotion.RingAccessibleName)
    {
        var ring = new ProgressRing { IsActive = true, Width = size, Height = size, MinWidth = 0, MinHeight = 0 };
        AutomationProperties.SetName(ring, accessibleName);
        _ = new OpenCodeProgressRingSync(ring); // Kept alive by the ring's own event handlers.
        return ring;
    }

    void IOpenCodeWorkingClockSubscriber.OnWorkingClockStateChanged(OpenCodeWorkingClock clock) => Bind();

    /// <summary>只在“已加载 + 激活 + 不定模式”时加入时钟；否则离开（最后一个离开时时钟停机）。</summary>
    private void Update()
    {
        var clock = OpenCodeWorkingClock.Shared;
        if (_loaded && _ring.IsActive && _ring.IsIndeterminate)
        {
            OpenCodeWorkingClockComposition.EnsureInitialized();
            if (!clock.Join(this)) Bind(); // A new join notifies (and binds) by itself.
        }
        else
        {
            clock.Leave(this);
        }
    }

    /// <summary>找到模板里的播放器并监听它的每一次开始播放（模板在 Loaded 时已应用；必要时强制应用）。</summary>
    private void HookPlayer()
    {
        var player = FindPlayer(_ring);
        if (player is null && _ring.ApplyTemplate()) player = FindPlayer(_ring);
        if (ReferenceEquals(player, _player)) return;
        _player?.UnregisterPropertyChangedCallback(AnimatedVisualPlayer.IsPlayingProperty, _playingToken);
        _player = player;
        if (player is not null)
        {
            _playingToken = player.RegisterPropertyChangedCallback(AnimatedVisualPlayer.IsPlayingProperty, (_, _) => Bind());
        }
    }

    private static AnimatedVisualPlayer? FindPlayer(DependencyObject parent, int depth = 0)
    {
        if (depth > 3) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is AnimatedVisualPlayer player) return player;
            if (FindPlayer(child, depth + 1) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>把当前这次原生播放接到共享相位（时钟运行中），或停在静止帧（减少动画 / 窗口隐藏 / 未加入）。</summary>
    private void Bind()
    {
        if (_player is not { IsPlaying: true } player) return; // Inactive / unloaded: the native ring shows nothing to sync.
        AnimationController? controller = null;
        try
        {
            controller = player.ProgressObject.TryGetAnimationController(ProgressProperty);
            if (controller is null) return;
            player.Pause();
            var clock = OpenCodeWorkingClock.Shared;
            if (clock.IsJoined(this) && clock.IsRunning)
            {
                var compositor = controller.Compositor;
                var progress = compositor.CreateExpressionAnimation(OpenCodeWorkingMotion.RingProgressExpression);
                progress.SetReferenceParameter("clock", OpenCodeWorkingClockComposition.PhaseSet(compositor));
                controller.StartAnimation(ProgressProperty, progress);
            }
            else
            {
                controller.StopAnimation(ProgressProperty);
                controller.Progress = (float)OpenCodeWorkingMotion.RingStaticProgress;
            }
        }
        catch
        {
            // Composition unavailable: fall back to the ring's own (unsynced) playback; status text is unaffected.
            try
            {
                controller?.StopAnimation(ProgressProperty);
                player.Resume();
            }
            catch
            {
            }
        }
    }
}
