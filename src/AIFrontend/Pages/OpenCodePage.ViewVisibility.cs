namespace AIFrontend.Pages;

/// <summary>
/// OpenCode 页 view ACK 的宿主窗口可见性接线：MainWindow 经 <see cref="NotifyHostWindowVisibility"/>
/// 推送“窗口已激活且未最小化”的保守判定（fail-closed 默认 false，宿主未确认可见前不发起任何 ACK）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>OpenCodePage.ViewAcknowledgement.cs</c> 的入口配合：刷新 Apply 路径经
/// <c>NotifyViewAcknowledgementRendered</c> 传入本文件的 <c>_hostWindowVisible</c>；窗口恢复可见时
/// 重提交“已渲染的最新窗口”（<c>_latestMessages</c>，与刷新 Apply 的窗口同源，绝不额外 GET 水位），
/// 由 ACK 内部按 (来源身份, 会话, 水位) 去重，绝不重发已尝试水位。最小化 / 失活只更新可见性
/// （不发起 ACK），使在途 fence 立即反映最新可见性。
/// </para>
/// <para>
/// 只覆盖窗口最小化与激活状态，<strong>不检测窗口被其他窗口遮挡</strong>（XAML / AppWindow 无法判定）。
/// 全部入口要求 UI 线程调用，非 UI 调用直接拒绝（与 ACK 入口同一契约）。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    /// <summary>宿主窗口级可见性（已激活且未最小化）；fail-closed 默认 false，仅宿主推送可置位。</summary>
    private bool _hostWindowVisible;

    /// <summary>
    /// 宿主入口（<strong>必须在 UI 线程调用</strong>）：窗口级可见性变化（激活 / 失活 / 最小化 / 还原）。
    /// 置位后立即发布活动快照；恢复可见时重提交已渲染的最新窗口（内部去重，绝不重发已尝试水位）；
    /// 置为不可见时只更新可见性、不发起 ACK。非 UI 调用直接拒绝，不排队。
    /// </summary>
    internal void NotifyHostWindowVisibility(bool windowVisible)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return;
        }

        _hostWindowVisible = windowVisible;
        if (windowVisible)
        {
            NotifyViewAcknowledgementRendered(_latestMessages, true);
        }
        else
        {
            UpdateViewAcknowledgementVisibility(false);
        }
    }
}
