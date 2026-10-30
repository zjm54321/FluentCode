namespace AIFrontend.Pages;

/// <summary>
/// OpenCode 页 view ACK 的宿主窗口可见性接线：MainWindow 经 <see cref="NotifyHostWindowVisibility"/>
/// 推送“窗口已激活且未最小化”的保守判定（fail-closed 默认 false，宿主未确认可见前不发起任何 ACK）。
/// </summary>
/// <remarks>
/// <para>恢复可见只请求当前真实 view 的布局/视口复核，不提交消息 DTO 水位；隐藏立即撤销在途 proof。
/// 每个 (来源身份, 会话, 水位) 仍至多自动尝试一次。</para>
/// <para>
/// 只覆盖窗口最小化与激活状态，<strong>不检测窗口被其他窗口遮挡</strong>（XAML / AppWindow 无法判定）。
/// 全部入口要求 UI 线程调用，非 UI 调用直接拒绝（与 ACK 入口同一契约）。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    /// <summary>宿主窗口级可见性（已激活且未最小化）；fail-closed 默认 false，仅宿主推送可置位。</summary>
    private bool _hostWindowVisible;
    partial void OnHostUiVisibilityChanged(bool visible);
    partial void UpdateForegroundNotificationSession();

    /// <summary>
    /// 宿主入口（<strong>必须在 UI 线程调用</strong>）：窗口级可见性变化（激活 / 失活 / 最小化 / 还原）。
    /// 恢复可见时复核当前 view，经新帧证明后才尝试；
    /// 置为不可见时只更新可见性、不发起 ACK。非 UI 调用直接拒绝，不排队。
    /// </summary>
    internal void NotifyHostWindowVisibility(bool windowVisible)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return;
        }

        _hostWindowVisible = windowVisible;
        OnHostUiVisibilityChanged(windowVisible);
        UpdateViewAcknowledgementVisibility(windowVisible); // Actual view review, never raw _latestMessages.
        UpdateForegroundNotificationSession();
    }
}
