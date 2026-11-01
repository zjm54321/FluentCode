using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Motion;

// ActionFeedback — usage (all calls on the UI thread; no layout shift; reduce-motion aware via FluentMotion):
//   Button/ContentControl acting control:
//     ActionFeedback.Success(button, "已复制路径");                    // ✓ morph 167ms, hold 1.5s, restore; SR notification
//     ActionFeedback.Failure(button, "复制失败", "剪贴板被占用");         // ✕ (critical brush), detail in tooltip 2.5s
//     ActionFeedback.Busy(button); … ActionFeedback.Success/Failure(…)  // async ops: ring while running (Reset to cancel)
//     await ActionFeedback.RunAsync(button, ct => DoAsync(ct), "已保存", "保存失败", ex => SafeText(ex)); // Busy→Success/Failure in one call
//   Only one icon inside a wider button (e.g. "ID ⧉" row): ActionFeedback.Success(fontIcon, button, "已复制会话 ID").
//   No dedicated control (context-menu copy etc.): ActionFeedback.Announce(anyElement, "已复制") — no visible text.
//   Removing a row (delete/archive): ActionFeedback.AnimateOut(row, () => list.Remove(item)).
//   Re-entrancy: if (ActionFeedback.Current(button) == ActionFeedback.State.Busy) return;
//   Keep InfoBar for errors the user must act on, and confirmations for destructive actions.

/// <summary>
/// 原位操作反馈（替代“已复制 / 已保存”之类的文字、横幅与提示条）：执行操作的按钮本身短暂变成结果图标——
/// 忙碌：进度环；成功：✓（E73E）；失败：✕ 状态色，详情放在工具提示里。约 1.5 秒（失败 2.5 秒）后恢复原内容。
/// 结果叠在原内容之上、原内容只做透明处理，所以按钮尺寸不变、不引起布局移动。
/// 动画使用 FluentMotion（Fast 167ms，缩放 + 淡入），减少动画时直接切换。
/// 读屏：通过 AutomationPeer.RaiseNotificationEvent 播报结果文字。
/// </summary>
internal static class ActionFeedback
{
    internal const string SuccessGlyph = "\uE73E";
    internal const string FailureGlyph = "\uE711";
    /// <summary>✓ 停留时长（仅截图测试会临时加长）。</summary>
    internal static TimeSpan SuccessHold { get; set; } = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan FailureHold = TimeSpan.FromMilliseconds(2500);

    internal enum State { Idle, Busy, Success, Failure }

    private static readonly Vector2 Center = new(0.5f, 0.5f);

    private sealed class Slot
    {
        public object? OriginalContent;
        public double OriginalOpacity = 1;
        public object? OriginalToolTip;
        public Grid? Host;
        public UIElement? Overlay;
        public DispatcherQueueTimer? Timer;
        public State State;
    }

    private static readonly ConditionalWeakTable<ContentControl, Slot> s_slots = new();

    /// <summary>当前反馈状态（测试与按钮防重入使用）。</summary>
    internal static State Current(ContentControl control) => s_slots.TryGetValue(control, out var slot) ? slot.State : State.Idle;

    /// <summary>当前显示的结果图标字形（无反馈时为 null）。</summary>
    internal static string? CurrentGlyph(ContentControl control)
        => s_slots.TryGetValue(control, out var slot) && slot.Overlay is FontIcon icon ? icon.Glyph : null;

    /// <summary>操作进行中：按钮内容换成小进度环（尺寸不变）。</summary>
    internal static void Busy(ContentControl control)
    {
        var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsHitTestVisible = false };
        Show(control, ring, State.Busy, hold: null, toolTip: null);
    }

    /// <summary>操作成功：✓ 图标原位出现，约 1.5 秒后恢复；读屏播报 <paramref name="announcement"/>。</summary>
    internal static void Success(ContentControl control, string announcement)
    {
        var icon = Icon(control, SuccessGlyph, "SystemFillColorSuccessBrush");
        Show(control, icon, State.Success, SuccessHold, toolTip: null);
        Announce(control, announcement);
    }

    /// <summary>操作失败：✕ 图标（状态色）原位出现，详情放在工具提示中，约 2.5 秒后恢复。</summary>
    internal static void Failure(ContentControl control, string announcement, string? detail = null)
    {
        var icon = Icon(control, FailureGlyph, "SystemFillColorCriticalBrush");
        Show(control, icon, State.Failure, FailureHold, toolTip: detail ?? announcement);
        Announce(control, announcement + (detail is null ? string.Empty : "：" + detail));
    }

    // ---- 图标级反馈：按钮里除图标外还有文字（如会话 ID 行）时，只把那个图标换成结果字形 ----

    private sealed class GlyphSlot
    {
        public string Glyph = string.Empty;
        public object? Foreground;
        public bool Active;
        public DispatcherQueueTimer? Timer;
    }

    private static readonly ConditionalWeakTable<FontIcon, GlyphSlot> s_glyphs = new();

    /// <summary>只把 <paramref name="icon"/> 换成 ✓，<paramref name="anchor"/> 用于读屏播报。</summary>
    internal static void Success(FontIcon icon, UIElement anchor, string announcement)
    {
        SwapGlyph(icon, SuccessGlyph, "SystemFillColorSuccessBrush", SuccessHold);
        Announce(anchor, announcement);
    }

    /// <summary>只把 <paramref name="icon"/> 换成 ✕（状态色）。</summary>
    internal static void Failure(FontIcon icon, UIElement anchor, string announcement)
    {
        SwapGlyph(icon, FailureGlyph, "SystemFillColorCriticalBrush", FailureHold);
        Announce(anchor, announcement);
    }

    internal static void Reset(FontIcon icon)
    {
        if (!s_glyphs.TryGetValue(icon, out var slot) || !slot.Active) return;
        slot.Timer?.Stop();
        RestoreGlyph(icon, slot);
    }

    private static void SwapGlyph(FontIcon icon, string glyph, string brushKey, TimeSpan hold)
    {
        var slot = s_glyphs.GetValue(icon, static _ => new GlyphSlot());
        slot.Timer?.Stop();
        if (!slot.Active)
        {
            slot.Glyph = icon.Glyph;
            slot.Foreground = icon.ReadLocalValue(IconElement.ForegroundProperty);
            slot.Active = true;
        }
        icon.Glyph = glyph;
        icon.Foreground = Services.Appearance.ThemeResources.Brush(icon, brushKey);
        FluentMotion.ScaleFadeIn(icon, 0.6f, Center, FluentMotion.Fast);
        if (icon.DispatcherQueue is not { } queue) return;
        slot.Timer ??= queue.CreateTimer();
        slot.Timer.Interval = hold;
        slot.Timer.IsRepeating = false;
        slot.Timer.Tick -= OnGlyphTick;
        slot.Timer.Tick += OnGlyphTick;
        void OnGlyphTick(DispatcherQueueTimer timer, object _)
        {
            timer.Stop();
            timer.Tick -= OnGlyphTick;
            RestoreGlyph(icon, slot);
        }
        slot.Timer.Start();
    }

    private static void RestoreGlyph(FontIcon icon, GlyphSlot slot)
    {
        icon.Glyph = slot.Glyph;
        if (slot.Foreground == DependencyProperty.UnsetValue) icon.ClearValue(IconElement.ForegroundProperty);
        else icon.SetValue(IconElement.ForegroundProperty, slot.Foreground);
        slot.Active = false;
    }

    /// <summary>立即恢复原内容（取消进行中的反馈）。</summary>
    internal static void Reset(ContentControl control)
    {
        if (!s_slots.TryGetValue(control, out var slot)) return;
        slot.Timer?.Stop();
        Restore(control, slot);
    }

    /// <summary>忙碌 → 成功 / 失败的完整流程。返回操作是否成功；取消时直接恢复原内容。</summary>
    internal static async System.Threading.Tasks.Task<bool> RunAsync(
        ContentControl control,
        Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> action,
        string success,
        string failure,
        Func<Exception, string?>? describe = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (Current(control) == State.Busy) return false;
        Busy(control);
        try
        {
            await action(cancellationToken);
            Success(control, success);
            return true;
        }
        catch (OperationCanceledException)
        {
            Reset(control);
            return false;
        }
        catch (Exception ex)
        {
            Failure(control, failure, describe?.Invoke(ex));
            return false;
        }
    }

    /// <summary>行项目移出（删除 / 归档）：淡出 + 轻微收缩后调用 <paramref name="removed"/>；减少动画时立即移除。</summary>
    internal static void AnimateOut(UIElement row, Action removed)
    {
        row.IsHitTestVisible = false;
        FluentMotion.ScaleFadeOut(row, 0.96f, Center, FluentMotion.Fast, removed);
    }

    /// <summary>只播报、不显示（没有专门按钮的操作，如右键菜单“复制”）。</summary>
    internal static void Announce(UIElement anchor, string text)
    {
        try
        {
            var peer = FrameworkElementAutomationPeer.FromElement(anchor) ?? FrameworkElementAutomationPeer.CreatePeerForElement(anchor);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.ImportantMostRecent, text, "FluentCodeActionFeedback");
        }
        catch
        {
            // 无障碍通知失败不影响操作本身。
        }
    }

    private static FontIcon Icon(ContentControl control, string glyph, string brushKey)
    {
        double size = control.Content is FontIcon original ? original.FontSize
            : control.Content is FrameworkElement { ActualHeight: > 0 and < 20 } small ? small.ActualHeight : 16;
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = Math.Clamp(size, 12, 20),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Foreground = Services.Appearance.ThemeResources.Brush(control, brushKey);
        return icon;
    }

    private static void Show(ContentControl control, FrameworkElement overlay, State state, TimeSpan? hold, string? toolTip)
    {
        var slot = s_slots.GetValue(control, static _ => new Slot());
        slot.Timer?.Stop();
        if (slot.Host is null)
        {
            // 原内容留在原位（只是透明），结果叠在其上：尺寸不变。
            slot.OriginalContent = control.Content;
            slot.OriginalToolTip = ToolTipService.GetToolTip(control);
            var host = new Grid();
            control.Content = host;
            if (slot.OriginalContent is UIElement element) { slot.OriginalOpacity = element.Opacity; host.Children.Add(element); }
            else if (slot.OriginalContent is not null) host.Children.Add(new TextBlock { Text = slot.OriginalContent.ToString(), VerticalAlignment = VerticalAlignment.Center });
            slot.Host = host;
        }

        if (slot.Overlay is not null) slot.Host.Children.Remove(slot.Overlay);
        if (slot.Host.Children.Count > 0) slot.Host.Children[0].Opacity = 0;
        overlay.HorizontalAlignment = HorizontalAlignment.Center;
        overlay.VerticalAlignment = VerticalAlignment.Center;
        slot.Host.Children.Add(overlay);
        slot.Overlay = overlay;
        slot.State = state;
        if (toolTip is not null) ToolTipService.SetToolTip(control, toolTip);
        if (state != State.Busy) FluentMotion.ScaleFadeIn(overlay, 0.6f, Center, FluentMotion.Fast);

        if (hold is { } duration && control.DispatcherQueue is { } queue)
        {
            slot.Timer ??= queue.CreateTimer();
            slot.Timer.Interval = duration;
            slot.Timer.IsRepeating = false;
            slot.Timer.Tick -= OnTick;
            slot.Timer.Tick += OnTick;
            void OnTick(DispatcherQueueTimer timer, object _)
            {
                timer.Stop();
                timer.Tick -= OnTick;
                Restore(control, slot);
            }
            slot.Timer.Start();
        }
    }

    private static void Restore(ContentControl control, Slot slot)
    {
        if (slot.Host is { } host)
        {
            host.Children.Clear();
            control.Content = slot.OriginalContent;
            if (slot.OriginalContent is UIElement element) element.Opacity = slot.OriginalOpacity;
        }
        if (slot.State == State.Failure) ToolTipService.SetToolTip(control, slot.OriginalToolTip);
        slot.Host = null;
        slot.Overlay = null;
        slot.OriginalContent = null;
        slot.State = State.Idle;
    }
}
