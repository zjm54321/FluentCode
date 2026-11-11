namespace FluentCode.Pages;

/// <summary>
/// 宿主分开推送在屏可见性与激活状态：可见 = 在屏（焦点无关），驱动渲染 / 时钟 / 轮询；
/// ACK / 前台通知另需激活。两个信号均 fail-closed 默认 false。
/// </summary>
/// <remarks>
/// <para>恢复用户注意只请求当前真实 view 的布局/视口复核，不提交消息 DTO 水位；隐藏或失活立即撤销在途 proof。
/// 未确认回执按有限退避重试，再次恢复注意时重新允许尝试；已确认水位不重复提交。</para>
/// <para>
/// 只覆盖窗口最小化、托盘隐藏、实际可见与激活状态，<strong>不检测窗口被其他窗口遮挡</strong>（XAML / AppWindow 无法判定）。
/// 全部入口要求 UI 线程调用，非 UI 调用直接拒绝（与 ACK 入口同一契约）。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    /// <summary>宿主在屏可见性（未最小化 / 未藏托盘 / 实际可见，焦点无关）；fail-closed 默认 false。</summary>
    private bool _hostWindowVisible;
    /// <summary>宿主激活状态；fail-closed 默认 false，不决定渲染是否继续。</summary>
    private bool _hostActivated;
    internal bool HostAttentionVisible => _hostWindowVisible && _hostActivated;
    partial void OnHostUiVisibilityChanged(bool visible);
    partial void UpdateForegroundNotificationSession();

    /// <summary>
    /// 宿主入口（<strong>必须在 UI 线程调用</strong>）：在屏可见性变化（隐藏 / 最小化 / 还原，焦点无关）。
    /// 恢复在屏时继续 UI 工作；仅同时已激活时复核当前 view。隐藏立即撤销 proof。非 UI 调用直接拒绝，不排队。
    /// </summary>
    internal void NotifyHostWindowVisibility(bool windowVisible)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return;
        }

        _hostWindowVisible = windowVisible;
        OnHostUiVisibilityChanged(windowVisible);
        UpdateViewAcknowledgementVisibility(HostAttentionVisible); // Actual view review, never raw _latestMessages.
        UpdateForegroundNotificationSession();
    }

    /// <summary>激活 / 失活只重算用户注意与回执，不暂停在屏渲染。必须在 UI 线程调用。</summary>
    internal void NotifyHostWindowActivation(bool activated)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return;
        }

        _hostActivated = activated;
        UpdateViewAcknowledgementVisibility(HostAttentionVisible);
        UpdateForegroundNotificationSession();
    }
}
