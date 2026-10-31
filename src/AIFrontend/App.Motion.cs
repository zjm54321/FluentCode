using AIFrontend.Motion;
using AIFrontend.Services.Configuration;

namespace AIFrontend;

public partial class App
{
    /// <summary>在 UI 线程接上动效开关：系统“动画效果”由 FluentMotion 自己订阅，这里只同步应用内“减少动画”设置。</summary>
    private static void InitializeMotion()
    {
        FluentMotion.EnsureInitialized();
        var settings = Pages.NormalPage.SharedSettingsService;
        FluentMotion.SetUserReduceMotion(settings.Current?.ReduceMotion ?? false);
        settings.Changed += OnMotionSettingsChanged;
        // Live transcript rows: user bubbles rise 16px with 0.98→1 scale from bottom-right (250ms); other rows rise 8px (167ms).
        Pages.OpenCode.OpenCodeConversationView.LiveEntranceAnimator = (element, isUser) =>
        {
            if (!FluentMotion.Enabled) return;
            if (isUser)
                FluentMotion.Animate(element, 0f, 1f, new System.Numerics.Vector3(0, 16, 0), System.Numerics.Vector3.Zero, 0.98f, 1f,
                    FluentMotion.Normal, FluentMotionTokens.Decelerate, null, null, new System.Numerics.Vector2(1f, 1f));
            else
                FluentMotion.SlideFadeIn(element, new System.Numerics.Vector3(0, 8, 0), FluentMotion.Fast);
        };
    }

    // SettingsService.Changed 可能来自后台线程；SetUserReduceMotion 内部会切回 UI 线程。
    private static void OnMotionSettingsChanged(object? sender, AppSettingsChangedEventArgs args)
        => FluentMotion.SetUserReduceMotion(args.Settings.ReduceMotion);
}
