using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend;

/// <summary>
/// Shell 集成：Local 会话的公开安全导航入口与 App.LocalNavigationRequested 消费接线。
/// </summary>
/// <remarks>
/// <para>仅通过 <see cref="NormalPage.TryActivateLocalSessionAsync"/>（等待设置/会话<strong>实际加载真正完成</strong>后校验会话映射）定位；
/// 不从原始激活参数构造目标、不逆解析通知摘要、不覆盖未发送草稿、不伪造成功。同步
/// <see cref="NormalPage.TryActivateLocalSession"/> 保留兼容，但公开导航一律走可 await 版本。
/// 会话 ID 必须来自运行期可信映射：App 事件 <c>LocalNavigationRequested</c> 的
/// <c>e.Target.SessionId</c>（完成事件运行期绑定）或本窗口 SourceRequestId → SessionId 观察。</para>
/// <para>App 事件订阅/退订在 <c>MainWindow.ShellIntegration</c> 分部（构造期订阅；关闭时退订并回收未消费目标）；
/// 本分部负责真实 ready（页面 Loaded + Local 加载完成）后的定位、按页面 generation/关闭 CTS 校验，
/// 以及按 (会话, 原因) 去重的失败提示。不使用轮询式重试：慢磁盘刷新由等待真实加载完成覆盖，不设重试预算。</para>
/// </remarks>
public sealed partial class MainWindow
{
    private static readonly TimeSpan LocalActivationLoadTimeout = TimeSpan.FromSeconds(8);

    // 失败提示去重键（同一目标同一原因只提示一次；成功定位或关闭时清除）。
    private const string LocalNavigationNoticeSettingsDraft = "settings-draft";
    private const string LocalNavigationNoticePageUnavailable = "page-unavailable";
    private const string LocalNavigationNoticeSessionNotFound = "session-not-found";

    // Local 导航生命周期：关闭/收尾时取消，使在途“等待 Local 加载完成”调用明确失败（不无限等待、不伪造成功）。
    private readonly CancellationTokenSource _localNavigationLifetime = new();

    private string? _pendingLocalSessionId;
    private string? _localNavigationNoticeSessionId;
    private string? _localNavigationNoticeReason;

    /// <summary>
    /// 公开安全导航（UI 线程）：切换到“普通界面”的 Local 标签并选中指定会话。
    /// 等待页面 Loaded 与 Local 设置/会话的<strong>实际加载真正完成</strong>后再定位（复用视图同一加载 Task，不并发重复 Load）。
    /// 返回 true 仅当定位已真实完成；<c>false</c> 表示加载已完成但会话不存在（或页面未就绪）；关闭/切页取消返回 false。
    /// 失败按 (目标, 原因) 给出一次可见提示（同一目标同一原因不重复提示），绝不把“ready 完成”当作“定位成功”。
    /// </summary>
    public async Task<bool> TryNavigateToLocalSessionAsync(string sessionId)
    {
        if (_shellClosing || string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        _pendingLocalSessionId = sessionId;

        bool settingsDraft = false;
        if (ContentFrame.Content is not NormalPage)
        {
            settingsDraft = ContentFrame.Content is Pages.Settings.SettingsPage { HasUnsavedChanges: true };
            if (settingsDraft)
            {
                // 不丢输入：导航被既有离开保护拦下，需用户确认后再次触发；此处仅如实提示。
                ShowLocalNavigationNoticeOnce(sessionId, LocalNavigationNoticeSettingsDraft,
                    InfoBarSeverity.Warning, "设置中有未保存的修改", "完成或放弃设置修改后，可再次打开目标会话。");
            }

            NavigateToTag("Normal");
        }

        if (ContentFrame.Content is not NormalPage page)
        {
            // 导航被离开保护拦下或未完成：不虚构定位；草稿提示已给出时不重复提示。
            if (!settingsDraft)
            {
                ShowLocalNavigationNoticeOnce(sessionId, LocalNavigationNoticePageUnavailable,
                    InfoBarSeverity.Warning, "暂时无法打开目标会话", "页面尚未就绪，请稍后重试。");
            }

            return false;
        }

        if (!await WaitForNormalPageReadyAsync(page))
        {
            ShowLocalNavigationNoticeOnce(sessionId, LocalNavigationNoticePageUnavailable,
                InfoBarSeverity.Warning, "暂时无法打开目标会话", "页面尚未就绪，请稍后重试。");
            return false;
        }

        if (_shellClosing || !ReferenceEquals(ContentFrame.Content, page))
        {
            return false; // 页面 generation 已变：不再向新页面写入任何状态。
        }

        bool activated;
        try
        {
            activated = await page.TryActivateLocalSessionAsync(sessionId, _localNavigationLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return false; // 关闭/切页取消：明确失败，不伪造定位成功。
        }

        if (!activated)
        {
            // ready 已真正完成仍无该会话：具体提示“未找到”，而不是含糊的“尚未加载完成”。
            if (!_shellClosing && ReferenceEquals(ContentFrame.Content, page))
            {
                ShowLocalNavigationNoticeOnce(sessionId, LocalNavigationNoticeSessionNotFound,
                    InfoBarSeverity.Warning, "未找到目标会话", "该会话可能已被删除，或不再可用。");
            }

            return false;
        }

        if (string.Equals(_pendingLocalSessionId, sessionId, StringComparison.Ordinal))
        {
            _pendingLocalSessionId = null; // 条件清除：不丢弃更新目标的待定位请求。
        }

        ClearLocalNavigationNoticeState();
        return true;
    }

    /// <summary>
    /// 公开安全导航（UI 线程）：按运行期执行 ID（Local SourceRequestId）定位会话。
    /// 未知 ID（如冷启动、本进程未观察过的执行）返回 false，绝不猜测或逆解析摘要。
    /// </summary>
    public Task<bool> TryNavigateToLocalCompletionAsync(string sourceRequestId)
    {
        if (string.IsNullOrEmpty(sourceRequestId))
        {
            return Task.FromResult(false);
        }

        string? sessionId = FindLocalSessionByRequestId(sourceRequestId);
        return sessionId is null ? Task.FromResult(false) : TryNavigateToLocalSessionAsync(sessionId);
    }

    /// <summary>关闭/收尾：取消在途定位、回收未消费的待定位目标与提示去重状态。</summary>
    private void ClearLocalShellNavigation()
    {
        if (!_localNavigationLifetime.IsCancellationRequested)
        {
            _localNavigationLifetime.Cancel();
        }

        _pendingLocalSessionId = null;
        ClearLocalNavigationNoticeState();
    }

    /// <summary>
    /// 按 (目标会话, 原因) 去重的可见失败提示：重复点击允许再次定位，但不重复提示同一原因。
    /// 成功定位或关闭时经 <see cref="ClearLocalNavigationNoticeState"/> 清除。
    /// </summary>
    private void ShowLocalNavigationNoticeOnce(string sessionId, string reason, InfoBarSeverity severity, string title, string message)
    {
        if (string.Equals(_localNavigationNoticeSessionId, sessionId, StringComparison.Ordinal) &&
            string.Equals(_localNavigationNoticeReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        _localNavigationNoticeSessionId = sessionId;
        _localNavigationNoticeReason = reason;
        ShowShellNotice(severity, title, message);
    }

    private void ClearLocalNavigationNoticeState()
    {
        _localNavigationNoticeSessionId = null;
        _localNavigationNoticeReason = null;
    }

    private async Task<bool> WaitForNormalPageReadyAsync(NormalPage page)
    {
        if (page.IsLoaded)
        {
            return true;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, page))
            {
                completion.TrySetResult(true);
            }
        }

        page.Loaded += OnLoaded;
        try
        {
            Task completed = await Task.WhenAny(completion.Task, Task.Delay(LocalActivationLoadTimeout));
            return completed == completion.Task;
        }
        finally
        {
            page.Loaded -= OnLoaded;
        }
    }
}
