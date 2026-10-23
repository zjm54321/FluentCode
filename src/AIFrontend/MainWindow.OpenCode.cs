using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend;

/// <summary>
/// Shell 集成：OpenCode 可信身份注入、会话跟踪、通知导航与监视诊断。
/// </summary>
/// <remarks>
/// <para>全部成员仅在 UI 线程访问（App 事件经 UI dispatcher 投递、页面事件在 UI 线程抛出）。
/// 本窗口不创建通知服务、不订阅旧 NotificationActivated、不发送任何 POST；
/// 导航只调用页面公开入口 <c>NavigateToNotificationAsync</c>（定位/聚焦，绝不授权），
/// 且目标一律来自 App 的可信结构化事件（绝不从原始激活参数构造）。</para>
/// </remarks>
public sealed partial class MainWindow
{
    private const int OpenCodeShellNavigationCapacity = 64;

    // 会话跟踪（页面 SessionTracked → App.TrackOpenCodeSession；App 未 ready 时待身份就绪后重试）。
    private string? _openCodeTrackedSessionId;
    private string? _openCodeTrackedSessionEndpoint;

    // 可信身份同步（含 null；记录上次同步值以识别真实换代）。
    private string? _lastSyncedEndpointIdentity;

    // 导航缓冲（结构化目标；未 page-ready 时缓冲；有界、按目标去重）。
    private readonly List<(NotificationNavigationTarget Target, NotificationInteractionResult? Result)> _pendingOpenCodeNavigation = new();
    private readonly HashSet<NotificationNavigationTarget> _pendingOpenCodeNavigationSeen = new();
    private bool _openCodeNavigationFlushing;
    private bool _openCodePageLoaded;

    // view ACK 的宿主窗口可见性（fail-closed 默认 false）：窗口已激活且未最小化才为 true；
    // 不检测窗口被其他窗口遮挡的情形（XAML/AppWindow 无法判定），也不虚构“完全可见”。
    private bool _openCodeWindowActivated;
    private bool _openCodeWindowVisible;

    // 监视 pending 快照（按会话记录上次集合；仅对新增项提示/映射，消失项不做状态推断）。
    private readonly Dictionary<string, (HashSet<string> Permissions, HashSet<string> Forms)> _lastPendingBySession = new(StringComparer.Ordinal);

    // ---- 可信身份注入 ----

    /// <summary>
    /// 把 App 当前 OpenCode 端点身份（含 null）同步给页面静态入口；
    /// 身份实际变化时标记旧端点执行的 freshness（不清除、不伪造终态），并重置执行/pending 映射（禁止跨 endpoint 混用）。
    /// </summary>
    private void SyncOpenCodeEndpointIdentity()
    {
        if (_app is null)
        {
            return;
        }

        string? current = _app.OpenCodeEndpointIdentity;
        bool changed = !string.Equals(_lastSyncedEndpointIdentity, current, StringComparison.Ordinal);

        try
        {
            Pages.OpenCodePage.SetTrustedEndpointIdentity(current);
        }
        catch (ArgumentException)
        {
            // App 身份为合法不透明标识；不静默接受非法值，此处仅防御。
        }

        if (changed && _lastSyncedEndpointIdentity is not null)
        {
            MarkOpenCodeEndpointTasksStale();
            _lastPendingBySession.Clear();
        }

        _lastSyncedEndpointIdentity = current;
    }

    // ---- 会话跟踪 ----

    private void OpenCodePage_SessionTracked(object? sender, OpenCodeSessionTrackedEventArgs e)
    {
        if (_shellClosing || _app is null || string.IsNullOrEmpty(e.SessionId))
        {
            return;
        }

        // 记录“当前可信 session”及记录时的身份（可能为 null=App 尚未就绪），待身份就绪后重试。
        _openCodeTrackedSessionId = e.SessionId;
        _openCodeTrackedSessionEndpoint = _app.OpenCodeEndpointIdentity;
        TryTrackOpenCodeSession(e.SessionId);
    }

    private void OpenCodePage_SessionRetired(object? sender, OpenCodeSessionRetiredEventArgs e)
    {
        if (!AppCallsAllowed || !ReferenceEquals(e.Source, _app!.CurrentOpenCodePageSource) || e.Source.IsRevoked) return;
        if (_openCodeTrackedSessionId == e.SessionId)
        {
            _openCodeTrackedSessionId = null;
            _openCodeTrackedSessionEndpoint = null;
        }
        _app.UntrackOpenCodeSession(e.Source, e.SessionId);
    }

    private void RetryTrackCurrentOpenCodeSession()
    {
        string? session = _openCodeTrackedSessionId;
        if (_app is null || string.IsNullOrEmpty(session))
        {
            return;
        }

        string? current = _app.OpenCodeEndpointIdentity;
        if (current is null)
        {
            return; // 仍未就绪：保留记录，等待下一次 OpenCodeSourceChanged。
        }

        if (_openCodeTrackedSessionEndpoint is { } recorded && !string.Equals(recorded, current, StringComparison.Ordinal))
        {
            // 换 endpoint 不继承旧会话（禁止跨 endpoint 混用）。
            _openCodeTrackedSessionId = null;
            _openCodeTrackedSessionEndpoint = null;
            return;
        }

        TryTrackOpenCodeSession(session);
    }

    private void TryTrackOpenCodeSession(string sessionId)
    {
        if (!AppCallsAllowed)
        {
            return;
        }

        try
        {
            _app!.TrackOpenCodeSession(sessionId);
        }
        catch (ArgumentException)
        {
            // 页面契约保证会话 ID 合法；非法时不重试、不缓存。
        }
        catch (InvalidOperationException)
        {
            // 监视容量已满（fail-closed）：不伪造跟踪成功。
        }
    }

    // ---- 通知导航（App 事件 → 切页 → 页面公开入口） ----

    private void QueueOpenCodeShellNavigation(NotificationNavigationTarget target, NotificationInteractionResult? result)
    {
        if (_shellClosing || _app is null || target.Source != NotificationSource.OpenCode)
        {
            return;
        }

        if (!string.Equals(target.EndpointIdentity, _app.OpenCodeEndpointIdentity, StringComparison.Ordinal))
        {
            ShowShellNotice(InfoBarSeverity.Warning, "导航目标已过期", "该目标来自此前的连接配置；未打开。");
            return;
        }

        if (!_pendingOpenCodeNavigationSeen.Add(target))
        {
            return; // 同一结构化目标已在缓冲/交付中。
        }

        if (_pendingOpenCodeNavigation.Count >= OpenCodeShellNavigationCapacity)
        {
            var oldest = _pendingOpenCodeNavigation[0];
            _pendingOpenCodeNavigation.RemoveAt(0);
            _pendingOpenCodeNavigationSeen.Remove(oldest.Target);
            ShowShellNotice(InfoBarSeverity.Warning, "待打开的导航过多", "较早的目标已被丢弃；请在 OpenCode 页面手动选择会话。");
        }

        _pendingOpenCodeNavigation.Add((target, result));
        EnsureOpenCodePageForNavigation();
    }

    private void EnsureOpenCodePageForNavigation()
    {
        if (ContentFrame.Content is Pages.OpenCodePage)
        {
            _ = FlushPendingOpenCodeNavigationAsync();
            return;
        }

        // 设置页存在未保存输入时，Frame.Navigating 会把本次导航转为页内确认；目标保留在缓冲，待其完成后重试。
        if (ContentFrame.Content is Pages.Settings.SettingsPage { HasUnsavedChanges: true })
        {
            ShowShellNotice(InfoBarSeverity.Warning, "设置中有未保存的修改", "完成或放弃设置修改后，将打开目标会话。");
        }

        NavigateToTag("OpenCode");

        if (ContentFrame.Content is Pages.OpenCodePage)
        {
            _ = FlushPendingOpenCodeNavigationAsync();
        }
    }

    private void OpenCodePage_ShellLoaded(object sender, RoutedEventArgs e)
    {
        _openCodePageLoaded = true;
        if (sender is Pages.OpenCodePage page)
        {
            // Loaded 后补推一次（attach 与 Loaded 的先后顺序不保证）；与可见性订阅推送幂等。
            page.NotifyHostWindowVisibility(_openCodeWindowVisible);
        }

        _ = FlushPendingOpenCodeNavigationAsync();
    }

    /// <summary>把缓冲中的结构化目标按序交付给页面公开导航入口（页面未就绪/身份未注入时由页面自身排队）。</summary>
    private async Task FlushPendingOpenCodeNavigationAsync()
    {
        if (_shellClosing || _openCodeNavigationFlushing || !_openCodePageLoaded)
        {
            return;
        }

        if (ContentFrame.Content is not Pages.OpenCodePage page)
        {
            return;
        }

        _openCodeNavigationFlushing = true;
        try
        {
            while (!_shellClosing && _pendingOpenCodeNavigation.Count > 0 && ReferenceEquals(ContentFrame.Content, page))
            {
                var item = _pendingOpenCodeNavigation[0];
                _pendingOpenCodeNavigation.RemoveAt(0);
                _pendingOpenCodeNavigationSeen.Remove(item.Target);

                if (_app is null || !string.Equals(item.Target.EndpointIdentity, _app.OpenCodeEndpointIdentity, StringComparison.Ordinal))
                {
                    ShowShellNotice(InfoBarSeverity.Warning, "导航目标已过期", "该目标来自此前的连接配置；未打开。");
                    continue;
                }

                try
                {
                    await page.NavigateToNotificationAsync(item.Target, item.Result);
                }
                catch (Exception)
                {
                    // 页面在内部收敛异常并经 NavigationHandled 报告；此处兜底避免未观察任务。
                }
            }
        }
        finally
        {
            _openCodeNavigationFlushing = false;
        }
    }

    private void ClearOpenCodeShellNavigation()
    {
        _pendingOpenCodeNavigation.Clear();
        _pendingOpenCodeNavigationSeen.Clear();
    }

    private void OpenCodePage_NavigationHandled(object? sender, OpenCodeNavigationResultEventArgs e)
    {
        if (_shellClosing)
        {
            return;
        }

        switch (e.Status)
        {
            case OpenCodeNavigationStatus.Queued:
            case OpenCodeNavigationStatus.Succeeded:
                break; // 页面已排队/已定位并聚焦（页面自身已有结果提示）。
            case OpenCodeNavigationStatus.NeedsApp:
                ShowShellNotice(InfoBarSeverity.Warning, "需要在应用内处理",
                    "请在 OpenCode 页面选择会话并重新验证；本次未自动提交任何内容。");
                break;
            case OpenCodeNavigationStatus.Gap:
                ShowShellNotice(InfoBarSeverity.Warning, "未找到目标会话",
                    "目标会话不在当前可读取范围内（不代表一定不存在）；未打开。");
                break;
            case OpenCodeNavigationStatus.ReadFailed:
                ShowShellNotice(InfoBarSeverity.Warning, "暂时无法打开目标",
                    "读取目标会话失败（网络或服务不可用）；未提交任何内容。");
                break;
            case OpenCodeNavigationStatus.TargetNotFound:
                ShowShellNotice(InfoBarSeverity.Informational, "目标请求已不在等待",
                    "该请求当前不处于待处理状态（可能已在别处处理）；未提交任何内容。");
                break;
            case OpenCodeNavigationStatus.Refused:
                ShowShellNotice(InfoBarSeverity.Warning, "导航目标已过期",
                    "目标来源与当前连接不一致（可能已更换配置）；未打开。");
                break;
        }
    }

    // ---- 交互结果（toast 点击 / 监视准备结果） ----

    private void App_OpenCodeInteractionProcessed(object? sender, NotificationInteractionEventArgs args)
    {
        if (_shellClosing || _app is null)
        {
            return;
        }

        NotificationInteractionResult result = args.Result;

        // 再核对一次来源（App 已过滤；禁止旧来源结果驱动当前 UI）。
        if (result.EndpointIdentity is { } endpoint && !string.Equals(endpoint, _app.OpenCodeEndpointIdentity, StringComparison.Ordinal))
        {
            return;
        }

        HandleOpenCodeInteractionTaskbar(result);
        // Toast replies remain background-only. Explicit body clicks use NavigationRequested;
        // failures have a follow-up toast instead of changing pages or showing a shell notice.
    }

    private void HandleOpenCodeInteractionTaskbar(NotificationInteractionResult result)
    {
        string? session = result.SessionId;
        if (result.Status != NotificationInteractionStatus.OutcomeUnknown || string.IsNullOrEmpty(session))
        {
            return;
        }

        // Session gate protection is independent of any execution ID and survives completion/view.
        _app?.RefreshOpenCodeSessionProtection(session);
    }

    // ---- 监视观察（Gap / ReadFailed / PendingSnapshot） ----

    private void HandleOpenCodeObservation(AppIntegrationDiagnostic diagnostic)
    {
        OpenCodeNotificationObservation? observation = diagnostic.Observation;
        if (observation is null)
        {
            return; // 交互结果经 OpenCodeInteractionProcessed 处理，避免重复。
        }

        if (_app is null || !string.Equals(observation.EndpointIdentity, _app.OpenCodeEndpointIdentity, StringComparison.Ordinal))
        {
            return; // 旧来源观察不驱动当前 UI。
        }

        switch (observation.Kind)
        {
            case OpenCodeNotificationObservationKind.Gap:
                ShowObservationNotice(observation);
                MarkOpenCodeSessionObservationsStale(observation.SessionId);
                break;
            case OpenCodeNotificationObservationKind.ReadFailed:
                ShowObservationNotice(observation);
                MarkOpenCodeSessionObservationsStale(observation.SessionId);
                break;
            case OpenCodeNotificationObservationKind.PendingSnapshot:
                HandlePendingSnapshot(observation);
                break;
            default:
                break; // Baseline / Messages / Interaction 不在此提示。
        }
    }

    private void ShowObservationNotice(OpenCodeNotificationObservation observation)
    {
        switch (observation.Kind)
        {
            case OpenCodeNotificationObservationKind.Gap:
                ShowShellNotice(InfoBarSeverity.Warning, "会话同步出现缺口",
                    "暂时无法完整核对会话状态；不会把历史记录当作完成。可在 OpenCode 页面重新进入会话以恢复同步。");
                break;
            case OpenCodeNotificationObservationKind.ReadFailed:
                ShowShellNotice(InfoBarSeverity.Warning, "会话状态读取失败",
                    "网络或服务暂时不可用，将自动重试；相关任务的状态未被改写。");
                break;
        }
    }

    /// <summary>
    /// pending 快照按会话记录上次集合（G1：不再弹出“有等待处理的请求”顶部提示条；待处理状态已由输入区权限/提问遮罩、
    /// 标签栏注意色工作环与左侧导航栏 InfoBadge 直接呈现）。
    /// </summary>
    private void HandlePendingSnapshot(OpenCodeNotificationObservation observation)
    {
        if (observation.SessionId.Length == 0)
        {
            return;
        }

        var permissions = observation.PendingPermissionIds ?? Array.Empty<string>();
        var forms = observation.PendingFormIds ?? Array.Empty<string>();

        if (!_lastPendingBySession.ContainsKey(observation.SessionId) && _lastPendingBySession.Count >= 64)
        {
            _lastPendingBySession.Clear(); // 有界：超限重置基线（下一次快照重新建立）。
        }

        var nextPermissions = new HashSet<string>(permissions, StringComparer.Ordinal);
        var nextForms = new HashSet<string>(forms, StringComparer.Ordinal);
        _lastPendingBySession[observation.SessionId] = (nextPermissions, nextForms);
    }

    // Page events may request tracking, but never create or settle execution IDs.

    private void OpenCodePage_ExecutionStateChanged(object? sender, OpenCodeExecutionStateChangedEventArgs e)
    {
        if (_shellClosing || _app is null || string.IsNullOrEmpty(e.SessionId))
        {
            return;
        }

        TryTrackOpenCodeSession(e.SessionId);
    }

    // ---- 窗口可见性（view ACK 宿主输入：激活 / 最小化生命周期） ----

    /// <summary>
    /// 构造期调用：订阅窗口激活（<see cref="Window.Activated"/>）与 AppWindow presenter 变化，
    /// 维护 view ACK 的宿主窗口可见性（复用既有 Window/AppWindow 生命周期，不新建窗口事件通道）。
    /// 判定保守：激活且未最小化才为可见；不检测被其他窗口遮挡的情形。
    /// </summary>
    private void InitializeOpenCodeWindowVisibilityTracking()
    {
        this.Activated += ShellWindow_VisibilityChanged;
        AppWindow.Changed += ShellAppWindow_Changed;
        InitializeOpenCodeNavBadge();
    }

    /// <summary>窗口关闭时调用：保守置不可见（在途 view ACK fence 立即失败），再解除订阅。</summary>
    private void ShutdownOpenCodeWindowVisibilityTracking()
    {
        this.Activated -= ShellWindow_VisibilityChanged;
        AppWindow.Changed -= ShellAppWindow_Changed;
        PushOpenCodeWindowVisibility(false);
        ShutdownOpenCodeNavBadge();
    }

    /// <summary>窗口激活状态变化（含最小化路径的失活）：失活即保守不可见；其余按 presenter 状态重算。</summary>
    private void ShellWindow_VisibilityChanged(object sender, WindowActivatedEventArgs args)
    {
        _openCodeWindowActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateOpenCodeWindowVisibility();
    }

    /// <summary>AppWindow presenter 变化（最小化 / 还原等）：重算窗口可见性。</summary>
    private void ShellAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            UpdateOpenCodeWindowVisibility();
        }
    }

    /// <summary>
    /// 重算并推送窗口可见性：激活且未最小化才为 true（fail-closed）；presenter 状态无法判定时
    /// 保守按不可见处理。只推送给当前 OpenCode 页；非当前页时仅记录，页面挂接 / Loaded 时补推。
    /// </summary>
    private void UpdateOpenCodeWindowVisibility()
    {
        bool minimized = false;
        try
        {
            minimized = AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized;
        }
        catch
        {
            // 无法判定窗口状态：保守视为不可见（fail-closed），并清除激活标记避免陈旧为 true。
            _openCodeWindowActivated = false;
        }

        PushOpenCodeWindowVisibility(_openCodeWindowActivated && !minimized);
    }

    /// <summary>记录并把窗口可见性推送给当前 OpenCode 页（非当前页不推送，挂接时补推）。</summary>
    private void PushOpenCodeWindowVisibility(bool visible)
    {
        _openCodeWindowVisible = visible;
        if (ContentFrame.Content is Pages.OpenCodePage page)
        {
            page.NotifyHostWindowVisibility(visible);
        }
    }

    // ---- 导航项 InfoBadge（重设计 v3 L9：OpenCode 有需要处理的会话） ----

    private App? _navBadgeApp;
    private bool _navBadgeRefreshQueued;

    /// <summary>
    /// 构造期（在 <c>InitializeShellIntegration</c> 之后）订阅 App 会话 UI 状态与来源变化，驱动 OpenCode 导航项的原生 InfoBadge。
    /// 只读：不发请求、不改任何会话状态；事件负载不直接使用，统一经 <see cref="App.GetOpenCodeSessionUiSnapshot"/> 在 UI 线程按当前来源复核后读取。
    /// </summary>
    private void InitializeOpenCodeNavBadge()
    {
        if (_navBadgeApp is not null || _app is not { } app)
        {
            return;
        }

        _navBadgeApp = app;
        app.OpenCodeSessionUiStateChanged += App_OpenCodeNavBadgeChanged;
        app.OpenCodeSourceChanged += App_OpenCodeNavBadgeChanged;
        QueueOpenCodeNavBadgeRefresh();
    }

    private void ShutdownOpenCodeNavBadge()
    {
        if (_navBadgeApp is { } app)
        {
            app.OpenCodeSessionUiStateChanged -= App_OpenCodeNavBadgeChanged;
            app.OpenCodeSourceChanged -= App_OpenCodeNavBadgeChanged;
            _navBadgeApp = null;
        }

        ApplyOpenCodeNavBadge(0, 0);
    }

    private void App_OpenCodeNavBadgeChanged(object? sender, EventArgs e)
    {
        if (_shellClosing || !ReferenceEquals(sender, _navBadgeApp))
        {
            return;
        }

        QueueOpenCodeNavBadgeRefresh();
    }

    /// <summary>合并到下一次 UI 调度（来源换代与状态事件常成串到达）。</summary>
    private void QueueOpenCodeNavBadgeRefresh()
    {
        if (_navBadgeRefreshQueued || DispatcherQueue is not { } queue)
        {
            return;
        }

        _navBadgeRefreshQueued = true;
        if (!queue.TryEnqueue(() =>
        {
            _navBadgeRefreshQueued = false;
            RefreshOpenCodeNavBadge();
        }))
        {
            _navBadgeRefreshQueued = false;
        }
    }

    private void RefreshOpenCodeNavBadge()
    {
        int pending = 0, unread = 0;
        if (!_shellClosing && _navBadgeApp is { } app && app.CurrentOpenCodePageSource is { IsRevoked: false } source)
        {
            try
            {
                if (app.GetOpenCodeSessionUiSnapshot(source, source.Revision) is { } snapshot)
                {
                    foreach (var state in snapshot.Sessions)
                    {
                        if (state.PendingAction == true) pending++;
                        else if (state.IsUnread) unread++;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 非 App 线程 / 关闭中：按“无提示”处理，不伪造状态。
                pending = unread = 0;
            }
        }

        ApplyOpenCodeNavBadge(pending, unread);
    }

    /// <summary>pending &gt; 0：数字徽标（等待处理的会话数）；仅有未读完成：圆点（Value = -1）；否则隐藏。</summary>
    private void ApplyOpenCodeNavBadge(int pending, int unread)
    {
        if (NavOpenCodeBadge is not { } badge)
        {
            return;
        }

        string description;
        if (pending > 0)
        {
            badge.Value = pending;
            badge.Visibility = Visibility.Visible;
            description = "OpenCode：" + pending + " 个会话等待你处理" + (unread > 0 ? "，另有 " + unread + " 个会话有未查看的结果" : "");
        }
        else if (unread > 0)
        {
            badge.Value = -1;
            badge.Visibility = Visibility.Visible;
            description = "OpenCode：" + unread + " 个会话有未查看的结果";
        }
        else
        {
            badge.Visibility = Visibility.Collapsed;
            description = "OpenCode";
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(badge,
            pending > 0 || unread > 0 ? description : "OpenCode 没有需要处理的会话");
        ToolTipService.SetToolTip(NavOpenCode, description);
    }
}
