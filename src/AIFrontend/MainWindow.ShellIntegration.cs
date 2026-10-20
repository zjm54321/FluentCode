using System;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend;

/// <summary>
/// Shell 集成接线（App 事件订阅 / 顶部提示条 / 设置凭据刷新转发）。
/// </summary>
/// <remarks>
/// <para>线程契约：App 的全部公开事件（OpenCodeSourceChanged / OpenCodeNavigationRequested /
/// LocalNavigationRequested / OpenCodeInteractionProcessed / IntegrationDiagnosticChanged）由 App 经 UI dispatcher 投递；
/// 本窗口对 App 的 UI 线程方法（TrackOpenCodeSession / UpdateTaskbarTask / SetOpenCodeNavigationReady /
/// RefreshOpenCodeCredentials 等）的调用一律发生在这些 UI 回调或页面事件（均为 UI 线程）内。</para>
/// <para>顺序契约：构造期完成全部订阅，窗口 Activated 后再调用
/// <c>App.SetOpenCodeNavigationReady(true)</c>（先订 handler 再置 ready，App 随后才会投递排队中的
/// 结构化导航目标）；关闭时先置 false 再退订，并清空页面可信身份（禁止旧 client 身份继续操作）。
/// Local 完成导航不经该 ready 门控（App 不缓存未订阅的 Local 事件），订阅同样在构造期完成。</para>
/// </remarks>
public sealed partial class MainWindow
{
    private App? _app;
    private bool _shellInitialized;
    private bool _shellAppReady;
    private bool _shellClosing;

    /// <summary>App 的 UI 线程方法仅在窗口就绪（Activated 之后）且未进入关闭流程时可调用。</summary>
    private bool AppCallsAllowed => _app is not null && _shellAppReady && !_shellClosing;

    /// <summary>构造期调用：完成 App/窗口级订阅，注入初始 OpenCode 身份并读取诊断初始快照。</summary>
    private void InitializeShellIntegration()
    {
        if (_shellInitialized)
        {
            return;
        }

        _shellInitialized = true;
        _app = Application.Current as App;
        if (_app is null)
        {
            return; // 非预期宿主：不接线、不抛异常（本窗口仍可独立使用）。
        }

        _app.OpenCodeSourceChanged += App_OpenCodeSourceChanged;
        _app.OpenCodeNavigationRequested += App_OpenCodeNavigationRequested;
        _app.LocalNavigationRequested += App_LocalNavigationRequested;
        _app.OpenCodeInteractionProcessed += App_OpenCodeInteractionProcessed;
        _app.IntegrationDiagnosticChanged += App_IntegrationDiagnosticChanged;

        // 窗口 Activated 时 App 已设置 dispatcher（App 先构造窗口、设置 dispatcher，再 Activate）。
        this.Activated += ShellWindow_Activated;

        // 初始注入当前可信身份（含 null），并消费订阅前已有的诊断快照（Gap/ReadFailed 仍可见）。
        SyncOpenCodeEndpointIdentity();
        ShowLatestShellObservationFromSnapshot();
    }

    private void ShellWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_shellAppReady)
        {
            return;
        }

        this.Activated -= ShellWindow_Activated;
        _shellAppReady = true;

        if (_shellClosing || _app is null)
        {
            return;
        }

        // 先订 handler（构造期已完成）再置 ready：此后 App 才会向本窗口投递排队导航。
        try
        {
            _app.SetOpenCodeNavigationReady(true);
        }
        catch (InvalidOperationException)
        {
            // 非 UI 线程不应发生；不虚报 ready（App 侧有诊断记录）。
        }

        // Activated 之前若有遗漏的 SourceChanged/跟踪重试，此处幂等补一次。
        SyncOpenCodeEndpointIdentity();
        RetryTrackCurrentOpenCodeSession();
    }

    /// <summary>窗口关闭时调用：置 false、退订全部 App 事件并清空页面可信身份（禁止旧身份继续操作）。</summary>
    private void ShutdownShellIntegration()
    {
        if (!_shellInitialized)
        {
            return;
        }

        _shellInitialized = false;
        _shellClosing = true;

        if (_app is not null)
        {
            try
            {
                _app.SetOpenCodeNavigationReady(false);
            }
            catch (InvalidOperationException)
            {
                // App 已在关闭流程时不投递；置 false 失败不阻塞收尾。
            }

            _app.OpenCodeSourceChanged -= App_OpenCodeSourceChanged;
            _app.OpenCodeNavigationRequested -= App_OpenCodeNavigationRequested;
            _app.LocalNavigationRequested -= App_LocalNavigationRequested;
            _app.OpenCodeInteractionProcessed -= App_OpenCodeInteractionProcessed;
            _app.IntegrationDiagnosticChanged -= App_IntegrationDiagnosticChanged;
            _app = null;
        }

        ClearOpenCodeShellNavigation();
        _openCodeTrackedSessionId = null;
        _openCodeTrackedSessionEndpoint = null;
        ClearLocalShellNavigation();
        _openCodePageLoaded = false;

        // 关闭即清空页面可信身份：旧 client 身份不得继续操作（页面实例随窗口销毁，此处兜底静态值）。
        try
        {
            Pages.OpenCodePage.SetTrustedEndpointIdentity(null);
        }
        catch (ArgumentException)
        {
            // null 不会抛出；防御性兜底。
        }
    }

    // ---- App 事件（全部在 UI 线程回调） ----

    private void App_OpenCodeSourceChanged(object? sender, EventArgs e)
    {
        if (_shellClosing || _app is null)
        {
            return;
        }

        SyncOpenCodeEndpointIdentity();

        if (_shellAppReady)
        {
            RetryTrackCurrentOpenCodeSession();
        }
    }

    private void App_OpenCodeNavigationRequested(object? sender, NotificationNavigationEventArgs args)
    {
        if (_shellClosing || _app is null)
        {
            return;
        }

        QueueOpenCodeShellNavigation(args.Target, null);
    }

    /// <summary>
    /// Local 完成通知（Source=LocalApi + Kind=Completion）：定位到完成会话。
    /// 仅导航，不自动发送/重试任何消息；目标一律来自 App 的可信结构化事件，不逆解析摘要或原始激活参数。
    /// </summary>
    private void App_LocalNavigationRequested(object? sender, NotificationNavigationEventArgs args)
    {
        if (_shellClosing)
        {
            return;
        }

        NotificationNavigationTarget target = args.Target;
        if (target.Source != NotificationSource.LocalApi ||
            target.Kind != NotificationNavigationKind.Completion ||
            !NotificationArgumentCodec.IsValidRequestId(target.SessionId))
        {
            return; // 非 Local 完成目标或会话标识无效：不消费（不猜测、不逆解析）。
        }

        // 定位自身排队等待页面 Loaded 与会话列表加载完成；失败提示按 (目标, 原因) 去重（见 MainWindow.Local.cs）。
        _ = TryNavigateToLocalSessionAsync(target.SessionId);
    }

    private void App_IntegrationDiagnosticChanged(object? sender, AppIntegrationDiagnosticEventArgs args)
    {
        if (_shellClosing || _app is null)
        {
            return;
        }

        HandleOpenCodeObservation(args.Diagnostic);
    }

    // ---- 顶部提示条 ----

    /// <summary>显示/替换顶部提示条（产品文案；不含正文、凭据或内部协议细节）。</summary>
    private void ShowShellNotice(InfoBarSeverity severity, string title, string message)
    {
        if (_shellClosing)
        {
            return;
        }

        ShellNoticeBar.Severity = severity;
        ShellNoticeBar.Title = title;
        ShellNoticeBar.Message = message;
        ShellNoticeBar.IsOpen = true;
    }

    /// <summary>读取订阅前的诊断初始快照：仅取最近一条 Gap/ReadFailed 观察作为可见提示（不重放其他历史）。</summary>
    private void ShowLatestShellObservationFromSnapshot()
    {
        if (_app is null)
        {
            return;
        }

        var snapshot = _app.IntegrationDiagnostics;
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            if (snapshot[i].Observation is { } observation &&
                observation.Kind is OpenCodeNotificationObservationKind.Gap or OpenCodeNotificationObservationKind.ReadFailed)
            {
                ShowObservationNotice(observation);
                return;
            }
        }
    }

    // ---- 设置页：OpenCode 凭据真实保存后的显式刷新 ----

    private void SettingsPage_OpenCodeCredentialsChanged(object? sender, EventArgs e)
    {
        if (!AppCallsAllowed)
        {
            return;
        }

        // 同 CredentialId 的密文更新无法由配置差异推断：显式 force 重读，使后台 owner 与 UI 一致。
        try
        {
            _app!.RefreshOpenCodeCredentials();
        }
        catch (InvalidOperationException)
        {
            // 非 UI 线程不应发生；不虚报刷新。
        }
    }
}
