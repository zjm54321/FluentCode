using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using OpenCodeExecutionState = AIFrontend.Pages.OpenCode.OpenCodeExecutionState;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Dispatching;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace AIFrontend.Pages;

/// <summary>
/// OpenCode 页：左侧会话列表（新会话 / 刷新）、中央待处理交互卡片（权限 / 表单，可操作）、
/// 消息时间线（用户 / 助手文本与工具部件）、底部自增高输入与单按钮（发送 / 停止）。
/// </summary>
/// <remarks>
/// <para>
/// 数据来源为 App 发布的 <see cref="OpenCodePageSource"/>：页面直接订阅 <c>App.OpenCodeSourceChanged</c>，
/// 事件仅提示重读 <c>App.CurrentOpenCodePageSource</c>；本页不读取设置或凭据、不自行构造可提交客户端。
/// 每个来源代际（引用 + Revision；同 identity 换凭据也会换代）由页面经 <c>CreateClient()</c> 创建
/// 自有客户端并负责释放（绝不释放 App monitor 的客户端）；来源为 null/已撤销时只显示等待，
/// 立即取消全部操作、增加来源与会话代际，绝不使用匿名回退。
/// 本页不实现设置编辑：未就绪时只显示说明，并通过 <see cref="SettingsRequested"/> 事件
/// 把设置入口交给宿主接线。
/// </para>
/// <para>
/// 已核实语义（见 docs/opencode-client-contract.md 第 7 节）在本页的落实：
/// prompt 回执仅表示“已接受并排程”（成功后才清空输入；错误一律保留输入）；
/// 待投递状态经 inbox 条目跟踪（条目消失 = 已投递，投递 ≠ 完成）；
/// 停止使用 InterruptAsync 并只转述服务端 interrupted 结果；
/// 权限请求与表单可在卡片中操作：提交前重新查询 pending 并比对指纹，
/// 仅服务端 204 视为成功；超时/连接中断/未知状态一律按“结果未知”处理，绝不自动重发；
/// 消息通过不重叠、可取消的定时刷新与手动刷新获取，绝不根据 idle /
/// 无变化 / 单条助手消息推断请求级成功，也不发送任何“完成”通知。
/// </para>
/// <para>
/// 生命周期：Loaded 时加载设置并启动轮询；Unloaded 时停止轮询、取消在途请求并释放客户端。
/// 切换会话会递增 generation 并取消旧会话的 CancellationTokenSource，
/// 迟到的旧响应在应用前一律丢弃，不会污染当前页面。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage : Page
{
    private enum ConnectionState
    {
        NotConfigured,
        Unknown,
        Connected,
        Failed,
    }

    /// <summary>
    /// 整会话恢复确认快照：第一阶段（核对并确认）捕获的完整绑定（来源引用 + Revision + 来源代际 +
    /// 会话 + 会话代际）与精确来源键（RequestId + Kind）。第二阶段执行前必须逐项与实时状态一致，
    /// 否则不执行任何解除、重新核对并再次确认；旧确认绝不因面板迟到回调而复活。
    /// </summary>
    private readonly record struct SessionRecoveryOrigin(
        OpenCodePageSource Source,
        long Revision,
        int SourceGeneration,
        string SessionId,
        int SessionGeneration,
        string RequestId,
        NotificationNavigationKind Kind);

    private const double ScrollBottomThreshold = 48;

    /// <summary>滚动到真实底部后的有界复核次数：吸收迟到的异步布局增高（富文本/工具部件）；绝不使用计时器循环。</summary>
    private const int MaxScrollSettlePasses = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(12);

    private readonly OpenCodeConversationView _conversationView = new();
    private readonly Dictionary<string, ListViewItem> _sessionItems = new(StringComparer.Ordinal);
    private readonly List<string> _pendingAckIds = new();
    private readonly OpenCodeInteractionsView _interactionsView = new();
    private readonly OpenCodeSessionRecoveryPanel _sessionRecoveryPanel = new();
    private readonly StackPanel _interactionsHost = new() { Orientation = Orientation.Vertical, Spacing = 8 };

    // 每会话的回合级执行状态去重锚点（idle 消息 id）与“已报告 Working”标记。
    private readonly Dictionary<string, string?> _lastIdleBySession = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workingSessions = new(StringComparer.Ordinal);

    // 可信来源身份与跨页面实例的导航排队（App/MainWindow 经公开入口注入）。
    private static readonly object NavigationSync = new();
    private static readonly Queue<(NotificationNavigationTarget Target, NotificationInteractionResult? Result)> PendingNavigation = new();
    private static readonly HashSet<NotificationNavigationTarget> QueuedNavigation = new();
    private static string? _trustedEndpointIdentity;
    private static bool _navigationDraining;

    // 当前 App 发布的来源能力（引用 + Revision + 来源代际共同界定代际；同 identity 换凭据也会换代）。
    private OpenCodePageSource? _source;
    private long _sourceRevision;
    private int _sourceGeneration;

    // 本页自持客户端（由 _source.CreateClient() 创建；页面负责释放，绝不释放 App monitor 的客户端）。
    private OpenCodeClient? _client;

    // 本会话可能存在“拒绝结果未知”的整会话保护（页面侧提示；仅影响恢复入口显示，不构成授权）。
    private bool _sessionProtectionHint;

    // 用户已确认过的整会话保护快照（绑定 + 精确来源键；第二步执行前逐项复核，绝不复活旧确认）。
    private SessionRecoveryOrigin? _recoveryOrigin;

    private DispatcherQueueTimer? _pollTimer;
    private CancellationTokenSource? _pageCts;
    private CancellationTokenSource? _sessionCts;

    private int _pageGeneration;
    private int _sessionGeneration;
    private bool _pageActive;
    private bool _isRefreshing;
    private bool _refreshQueued;
    private bool _refreshQueuedInitial;
    private bool _refreshQueuedForceFollow;
    private bool _refreshQueuedExplicit;
    private bool _isSending;
    private bool _suppressSessionSelection;
    private bool _isComposing;
    private bool _shiftDown;
    private bool _enterDown;
    private bool _sessionMissing;
    private bool _deliveredNoticeShown;
    private bool _interactionBusy;
    private int _mutationEpoch;
    private DateTimeOffset _lastCompositionEndedAt = DateTimeOffset.MinValue;

    private string? _selectedSessionId;
    private ConnectionState _connection = ConnectionState.Unknown;

    /// <summary>
    /// 复用 Local / Settings 的应用级共享服务（供 Frame.Navigate(typeof(OpenCodePage)) 使用）。
    /// </summary>
    public OpenCodePage()
        : this(NormalPage.SharedSettingsService, NormalPage.SharedSecretStore)
    {
    }

    /// <summary>
    /// 使用指定的共享服务实例构造页面。
    /// </summary>
    /// <param name="settingsService">共享的设置服务。</param>
    /// <param name="secretStore">共享的 DPAPI 凭据存储。</param>
    public OpenCodePage(SettingsService settingsService, DpapiSecretStore secretStore)
    {
        // 构造签名保留给既有宿主；本页不再用这两个服务读取设置或凭据（连接一律来自 App 发布的来源）。
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(secretStore);
        _homeSettings = settingsService;

        this.InitializeComponent();
        InitializeNoticeDismissal();

        _conversationView.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetAutomationId(_conversationView, "OpenCodeMessageList");
        AutomationProperties.SetName(_conversationView, "消息时间线");
        MessageHost.Children.Add(_conversationView);
        InitializeAgentSelector();
        InitializeComposer();
        InitializeHomePage();
        InitializeComposerOverlays();
        InitializeQuestionOverlay();

        _interactionsHost.Children.Add(_interactionsView);
        _interactionsHost.Children.Add(_sessionRecoveryPanel);
        InteractionsScroll.Content = _interactionsHost;
        _interactionsView.PermissionDecisionRequested += (_, e) => _ = SubmitPermissionDecisionAsync(e.Request, e.Decision);
        _interactionsView.PermissionRecheckRequested += (_, e) => _ = RecheckPermissionAsync(e.RequestId);
        _interactionsView.FormSubmitRequested += (_, e) => _ = SubmitFormAnswerAsync(e.Form, e.Answer);
        _interactionsView.FormCancelRequested += (_, e) => _ = SubmitFormCancelAsync(e.Form);
        _interactionsView.FormRecheckRequested += (_, e) => _ = RecheckFormAsync(e.FormId);
        _interactionsView.ResolutionRequested += (_, e) => _ = ResolveInteractionAsync(e.IsForm, e.Id, e.Retry);
        InitializeInteractionAnchors();
        _sessionRecoveryPanel.RecoveryRequested += (_, _) => _ = BeginSessionRecoveryAsync();
        _sessionRecoveryPanel.DecisionRequested += (_, e) => _ = CompleteSessionRecoveryAsync(e.AllowRetry);

        // IME 组合状态经 TextCompositionStarted/Ended 跟踪（代码挂钩，避免 XAML 事件签名解析差异）。
        Composer.TextCompositionStarted += Composer_TextCompositionStarted;
        Composer.TextCompositionEnded += Composer_TextCompositionEnded;

        // Enter 拦截走 PreviewKeyDown（隧道阶段，先于原生 TextBox 的 AcceptsReturn 换行处理；
        // handledEventsToo 保证上游先行置位时仍能收到）。XAML 侧 KeyDown 保留为共享判定的幂等兜底。
        Composer.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(Composer_PreviewKeyDown),
            handledEventsToo: true);

        // 焦点离开后组合/修饰键状态不可能继续成立：清除跟踪值，避免事件缺失导致的粘滞误判。
        Composer.LostFocus += Composer_LostFocus;

        // 单按钮与单/多行布局：内容变化刷新按钮模式；高度变化（行数/窗口宽度）切换按钮位置。
        Composer.TextChanged += Composer_TextChanged;

        this.Loaded += OnPageLoaded;
        this.Unloaded += OnPageUnloaded;
        InitializeSessionStateUi();
        InitializeSessionInsights();
        InitializeSidebars();
        InitializeRightPanelMetadata();
        InitializeWslQuickAccess();

        UpdateConnectionStatus();
        UpdateComposerButtons();
        UpdateEmptyState();
    }

    /// <summary>
    /// 设置入口请求：页面处于“未配置 / 设置不可用”状态且用户点击“打开设置”时触发。
    /// 宿主（MainWindow / App）可订阅此事件导航到设置界面；无订阅时页面只显示说明，不虚构设置页。
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// 请求宿主跟踪某会话（选择 / 新建 / 发送）。只含会话 ID 与结构化原因，不含正文或凭据。
    /// 宿主应据此调用 App 的 <c>TrackOpenCodeSession</c>；本页不直接引用 App。
    /// </summary>
    public event EventHandler<OpenCodeSessionTrackedEventArgs>? SessionTracked;

    /// <summary>
    /// 回合级执行状态变化。<see cref="OpenCodeExecutionState.Working"/> 由“输入已接纳并排程”产生；
    /// 终态仅由已核实的 idle 回合终态投影产生（绝不把 prompt 回执当完成）。
    /// </summary>
    public event EventHandler<OpenCodeExecutionStateChangedEventArgs>? ExecutionStateChanged;

    /// <summary>
    /// <c>NavigateToNotificationAsync</c> 的结构化处理结果（供 App 消费；含 NeedsApp/Gap/ReadFailed 等可见状态）。
    /// </summary>
    public event EventHandler<OpenCodeNavigationResultEventArgs>? NavigationHandled;

    /// <summary>
    /// 可信来源身份发生变化（宿主调用 <see cref="SetTrustedEndpointIdentity"/> 且值实际改变时触发）。
    /// 页面据此重置共享仲裁相关的卡片状态；不改变后端、不发送请求。
    /// </summary>
    public static event EventHandler? TrustedEndpointIdentityChanged;

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageActive)
        {
            return;
        }

        _pageActive = true;
        _pageGeneration++;
        _pageCts = new CancellationTokenSource();
        BindComposer();
        TrustedEndpointIdentityChanged += OpenCodePage_TrustedEndpointIdentityChanged;
        SubscribeAppSourceChanged();
        AttachConversationEvents();
        AttachViewAcknowledgementEvents();

        try
        {
            // App 事件仅提示重读属性：Loaded 后立即读取当前来源（null/已撤销 → 显示等待）。
            ApplyCurrentSource();
        }
        catch (Exception)
        {
            if (_pageActive)
            {
                ShowMessage(InfoBarSeverity.Error, "初始化失败", "读取应用提供的 OpenCode 连接时发生未预期错误；可重新进入本页重试。");
            }
        }
        finally
        {
            if (_pageActive)
            {
                StartPolling();
                UpdateComposerButtons();
                Composer.Focus(FocusState.Programmatic);
                _ = TryDrainNavigationQueueAsync();
            }
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_pageActive)
        {
            return;
        }

        SuspendDraftSlotForPageUnload();
        _pageActive = false;
        _hostWindowVisible = false;
        UpdateViewAcknowledgementVisibility(false);
        DetachViewAcknowledgementEvents();
        _pageGeneration++;
        _sessionGeneration++;
        BindComposer();
        ClearSessionReturnContext();

        // 卸载即失效：在途 view ACK 的 fence 立即失败（helper 保留尝试键，重进同会话绝不重发）。
        InvalidateViewAcknowledgement();

        _pollTimer?.Stop();
        _pollTimer = null;
        StopEventSync();

        var pageCts = _pageCts;
        _pageCts = null;
        pageCts?.Cancel();

        CancelSessionOperations();
        TrustedEndpointIdentityChanged -= OpenCodePage_TrustedEndpointIdentityChanged;
        UnsubscribeAppSourceChanged();
        _source = null;
        _sourceRevision = 0;
        _recoveryOrigin = null;
        DisposeOwnClient();

        _conversationView.ClearMessages();
        DetachConversationEvents();
        ClearAgentSelector();
        _interactionsView.ClearAll();
        _sessionRecoveryPanel.HideConfirm();
        _sessionRecoveryPanel.HideEntry();
        _sessionProtectionHint = false;
        _mutationEpoch++;
        _interactionBusy = false;
        _lastIdleBySession.Clear();
        _workingSessions.Clear();
        PermissionBar.IsOpen = false;
        _sessionItems.Clear();
        SessionList.Items.Clear();
        ResetSessionPaginationState();
        ResetMessagePaginationState();
        _suppressSessionSelection = false;

        // _selectedSessionId / _pendingAckIds 保留：重新进入本页后恢复选中并继续跟踪待投递条目。
    }

    // ------------------------------------------------------------------
    // 来源绑定（App 发布 OpenCodePageSource；页面自持独立客户端）
    // ------------------------------------------------------------------

    /// <summary>来源与页面客户端是否就绪（未撤销）。</summary>
    private bool SourceReady => _source is { IsRevoked: false } && _client is not null;

    /// <summary>
    /// App 来源变化：事件仅提示重读 <c>App.CurrentOpenCodePageSource</c>；同引用同 Revision 的事件不触发任何重置。
    /// </summary>
    private void App_OpenCodeSourceChanged(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyCurrentSource);
            return;
        }

        ApplyCurrentSource();
    }

    private void SubscribeAppSourceChanged()
    {
        try
        {
            if (Application.Current is App app)
            {
                app.OpenCodeSourceChanged += App_OpenCodeSourceChanged;
            }
        }
        catch
        {
            // 无 App 实例（设计器/测试宿主）：保持等待状态，不伪造来源。
        }
    }

    private void UnsubscribeAppSourceChanged()
    {
        try
        {
            if (Application.Current is App app)
            {
                app.OpenCodeSourceChanged -= App_OpenCodeSourceChanged;
            }
        }
        catch
        {
        }
    }

    private static OpenCodePageSource? ReadAppSource()
    {
        try
        {
            return (Application.Current as App)?.CurrentOpenCodePageSource;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 重读 App 当前来源并按“引用 + Revision”判断代际：未变化则无操作；null/已撤销 → 立即失效
    /// （取消全部操作、增加来源与会话代际、释放自有客户端）并显示等待；新来源 → 失效旧代际后
    /// 创建本页自有客户端并加载会话。绝不读取设置/凭据、绝不匿名回退。
    /// </summary>
    private void ApplyCurrentSource()
    {
        if (!_pageActive)
        {
            return;
        }

        var source = ReadAppSource();
        if (ReferenceEquals(source, _source) && (source is null ? _draftEpochSource is null
            : !source.IsRevoked && source.Revision == _sourceRevision && _client is not null))
        {
            return;
        }

        if (source is null || source.IsRevoked)
        {
            InvalidateForSourceChange();
            ShowSourceWaiting();
            return;
        }

        OpenCodeClient client;
        try
        {
            client = source.CreateClient();
        }
        catch (InvalidOperationException)
        {
            // 创建期间来源被撤销：按等待处理（换代事件会再次到达并重读）。
            InvalidateForSourceChange(retireDraftSource: !IsDraftSourceCurrent(ReadAppSource()));
            ShowSourceWaiting();
            return;
        }

        // 创建后复核：来源仍是 App 当前来源且未被撤销，才把本页绑定切到该代际。
        if (source.IsRevoked || !ReferenceEquals(ReadAppSource(), source))
        {
            client.Dispose();
            InvalidateForSourceChange(retireDraftSource: !IsDraftSourceCurrent(ReadAppSource()));
            ShowSourceWaiting();
            return;
        }

        InvalidateForSourceChange(retireDraftSource: !IsDraftSourceCurrent(source));
        _source = source;
        _sourceRevision = source.Revision;
        _client = client;
        EnsureDraftSourceEpoch(source);
        BeginHomeSource();
        StartEventSync();
        BindComposer();
        if (_newSessionDraftActive) EnsureBlankPreferences();
        ShowWorkspace();
        _ = ReadServerInfoAsync(); // B2 footer: once per installed source, never polled.
        _ = RefreshSessionsAsync();
        _ = TryDrainNavigationQueueAsync();
    }

    /// <summary>
    /// 来源代际失效（null / 已撤销 / 安装新代际前）：取消页面级与会话级操作、增加来源与会话代际、
    /// 释放本页自有客户端、清空旧代际的会话选中与交互卡片；旧请求的迟到结果不会污染新代际。
    /// </summary>
    private void InvalidateForSourceChange(bool retireDraftSource = true)
    {
        InvalidateViewAcknowledgement();
        if (retireDraftSource) InvalidateDraftSourceEpoch();
        ResetHomeSource();
        StopEventSync();
        _sourceGeneration++;
        ClearSessionReturnContext();
        _pageGeneration++;
        _source = null;
        _sourceRevision = 0;
        BindComposer();
        var pageCts = _pageCts;
        _pageCts = new CancellationTokenSource();
        pageCts?.Cancel();
        _sessionGeneration++;
        BindComposer();

        // 来源换代/撤销：view ACK 活动快照立即失效（尝试键保留，绝不重发结果未知的 POST）。
        InvalidateViewAcknowledgement();
        CancelSessionOperations();
        _mutationEpoch++;
        _interactionBusy = false;
        _sessionProtectionHint = false;
        _recoveryOrigin = null;

        _source = null;
        _sourceRevision = 0;
        DisposeOwnClient();

        _sessionItems.Clear();
        SessionList.Items.Clear();
        ResetSessionPaginationState();
        ResetMessagePaginationState();
        if (retireDraftSource) _selectedSessionId = null;
        _sessionMissing = false;
        BindComposer();
        _pendingAckIds.Clear();
        _deliveredNoticeShown = false;

        _conversationView.ClearMessages();
        ClearAgentSelector();
        _interactionsView.ClearAll();
        _sessionRecoveryPanel.HideConfirm();
        _sessionRecoveryPanel.HideEntry();
        _lastIdleBySession.Clear();
        _workingSessions.Clear();
        PermissionBar.IsOpen = false;
        MessageBar.IsOpen = false;
        // 保存地址不是连接成功；仅新客户端收到真实服务响应后才标记 Connected。
        SetConnectionState(ConnectionState.Unknown);

        UpdateWaitingText();
        UpdateEmptyState();
        UpdateComposerButtons();
        UpdateInteractionsSection();
    }

    /// <summary>释放本页自持客户端（绝不释放 App monitor 的客户端）。</summary>
    private void DisposeOwnClient()
    {
        var client = _client;
        _client = null;
        BindComposer();
        client?.Dispose();
    }

    /// <summary>来源未就绪（App 尚未提供或已撤销）：显示等待面板；绝不使用匿名回退或自行读取凭据。</summary>
    private void ShowSourceWaiting()
    {
        ShowNotConfigured(
            "OpenCode 连接尚未就绪",
            "等待应用提供 OpenCode 连接（本页不再自行读取设置或凭据）。若已在设置中填写 openCode.baseUrl，稍候会自动加载；也可打开设置检查配置后返回本页。");
    }

    // ------------------------------------------------------------------
    // 会话列表
    // ------------------------------------------------------------------

    private async Task RefreshSessionsAsync()
    {
        if (!SourceReady || !_pageActive)
        {
            return;
        }

        var client = _client;
        if (client is null)
        {
            return;
        }

        var generation = _pageGeneration;
        var ct = _pageCts?.Token ?? CancellationToken.None;

        // 刷新开始即递增列表代际并暂停旧分页（隐藏旧游标、保留可见列表）：在途续页响应与该次
        // 刷新的旧首屏响应自此失效；仅当本次新首屏成功后才由 RestartSessionPagination 重建分页。
        var epoch = BeginSessionRefresh();
        RefreshSessionsButton.IsEnabled = false;
        var homeReadAccepted = false;

        try
        {
            OpenCodeSessionsResponse response;
            try
            {
                response = await client.ListSessionsAsync(
                    new OpenCodeSessionListQuery { Order = OpenCodeSessionOrders.Desc, Limit = 50, ParentFilter = OpenCodeSessionParentFilter.Roots },
                    ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeApiException ex)
            {
                if (generation == _pageGeneration && epoch == _sessionListEpoch)
                {
                    HandleApiException(ex, "会话列表");
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                if (generation == _pageGeneration && epoch == _sessionListEpoch)
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "无法连接 OpenCode 服务", "会话列表加载失败；请确认服务地址与服务状态。");
                }

                return;
            }
            catch (OpenCodeTimeoutException)
            {
                if (generation == _pageGeneration && epoch == _sessionListEpoch)
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "会话列表超时", "客户端 30 秒内未完成请求。");
                }

                return;
            }
            catch (OpenCodeProtocolException)
            {
                if (generation == _pageGeneration && epoch == _sessionListEpoch)
                {
                    ShowMessage(InfoBarSeverity.Error, "响应不符合契约", "会话列表响应不符合已核实的 V2 契约。");
                }

                return;
            }

            // 并发刷新代际保护：页面/列表代际任一变化即丢弃本次响应，旧响应绝不覆盖新列表。
            if (generation != _pageGeneration || !_pageActive || epoch != _sessionListEpoch)
            {
                return;
            }

            SetConnectionState(ConnectionState.Connected);

            homeReadAccepted = true;
            var target = RebuildSessionList(response.Data ?? new List<OpenCodeSessionInfo>());
            _ = RefreshListedSessionStatusesAsync();
            RefreshSessionStateUi();
            // 新首屏成功：以本次响应的 cursor.next 重建分页；首屏空 data 视为正常结束
            // （即使携带 next 也不进入续页）。
            RestartSessionPagination((response.Data?.Count ?? 0) > 0 ? response.Cursor?.Next : null);
            if (target is not null)
            {
                await SwitchToSessionAsync(target);
            }
        }
        catch (Exception)
        {
            if (generation == _pageGeneration && _pageActive && epoch == _sessionListEpoch)
            {
                ShowMessage(InfoBarSeverity.Error, "刷新会话失败", "发生未预期错误；请稍后重试。");
            }
        }
        finally
        {
            // UI 恢复同样按代际校验：已被更新的刷新取代时，按钮由那次刷新负责恢复。
            if (_pageActive && epoch == _sessionListEpoch)
            {
                RefreshSessionsButton.IsEnabled = true;
                _homeSessionsMessage = homeReadAccepted ? null : "会话列表未能刷新；仅显示之前已加载的会话，可重试刷新。";
                QueueHomeProjection();
            }
        }
    }

    private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSessionsAsync();
    }

    private async void NewSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SourceReady || !_pageActive || HasPendingNativeAction() || _isSending || _composerPickerPending) return;
        var source = _source;
        var client = _client;
        if (source is null || client is null) return;
        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        var sessionId = _selectedSessionId;
        bool Owns() => _pageActive && ReferenceEquals(_source, source) && ReferenceEquals(_client, client)
            && revision == _sourceRevision && sourceGeneration == _sourceGeneration && generation == _pageGeneration
            && sessionGeneration == _sessionGeneration && sessionId == _selectedSessionId;
        using var cancellation = CreateOperationToken(source);
        await CreateNativeSessionAsync(source, client, Owns, Owns, cancellation.Token);
    }

    /// <summary>
    /// 主会话（root）判定：session 与 Id 均非空，且 ParentId 严格为 null；
    /// 空字符串 ParentId 视为非法值，不当作 root（会话列表仅接纳 root）。
    /// </summary>
    private static bool IsRootSession(OpenCodeSessionInfo? session) =>
        session?.Id is { Length: > 0 } && session.ParentId is null;

    /// <summary>重建会话列表；返回需要切换到（并重新拉取消息）的会话 id（无则 null）。</summary>
    private string? RebuildSessionList(List<OpenCodeSessionInfo> sessions)
    {
        // 清空/填充/选中同步全程抑制选择事件，避免重建过程被当作切换；结束后恢复进入前的值。
        var previousSuppress = _suppressSessionSelection;
        _suppressSessionSelection = true;
        try
        {
            _sessionItems.Clear();
            SessionList.Items.Clear();

            foreach (var session in sessions)
            {
                if (!IsRootSession(session) || session.Id is not { Length: > 0 } id || _sessionItems.ContainsKey(id))
                {
                    continue;
                }

                var item = CreateSessionItem(session);
                _sessionItems[id] = item;
                SessionList.Items.Add(item);
            }

            if (SessionList.Items.Count == 0)
            {
                SessionsHint.Text = "当前页没有主会话，可继续加载或创建。";
                SessionsHint.Visibility = Visibility.Visible;
            }
            else
            {
                SessionsHint.Text = string.Empty;
                SessionsHint.Visibility = Visibility.Collapsed;
            }

            if (_selectedSessionId is { } selected)
            {
                // viewed 会话不要求出现在本页列表：命中才同步选中，缺项则清空列表选中并保持 viewed ID 不变。
                if (_sessionItems.TryGetValue(selected, out var existing))
                {
                    SessionList.SelectedItem = existing;
                    SessionList.ScrollIntoView(existing);
                }
                else
                {
                    SessionList.SelectedItem = null;
                }

                // 页面重新加载后（无会话级 CTS）需要重新拉取该会话的消息。
                return !_homeActive && _sessionCts is null ? selected : null;
            }

            // 空白“新会话”标签显示中：不自动选中（用户明确选择或创建后才进入会话）。
            if (!_homeActive && !_newSessionDraftActive &&
                SessionList.Items.Count > 0 &&
                SessionList.Items[0] is ListViewItem { Tag: string firstId } &&
                _sessionItems.TryGetValue(firstId, out var firstItem))
            {
                // 无选中时自动选中最近更新的会话（列表按 order=desc 返回）。
                SessionList.SelectedItem = firstItem;
                SessionList.ScrollIntoView(firstItem);
                return firstId;
            }

            // An empty catalog does not manufacture a blank draft. Home is the native fallback tab.
            if (!_homeActive && !_newSessionDraftActive && _selectedSessionId is null && SessionList.Items.Count == 0)
            {
                ShowHomePage();
            }

            return null;
        }
        finally
        {
            _suppressSessionSelection = previousSuppress;
        }
    }

    private static ListViewItem CreateSessionItem(OpenCodeSessionInfo session)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(session.Title) ? "（未命名会话）" : session.Title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // 会话 ID 行：外观（内置 Caption 样式 + 次要文字色，ID 移入 Tooltip）由 OpenCodePage.Tabs.cs 在项加入列表时套用；
        // 不再用 Opacity 降低对比。
        var subtitle = new TextBlock
        {
            Text = session.Id ?? string.Empty,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var item = new ListViewItem
        {
            Tag = session.Id,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 2, Children = { title, subtitle } },
        };
        HomeSessionMetadata.Add(item, session);
        AutomationProperties.SetName(item, title.Text + "（" + (session.Id ?? string.Empty) + "）");
        return item;
    }

    private void InsertSessionItemFirst(OpenCodeSessionInfo session)
    {
        if (!IsRootSession(session) || session.Id is not { Length: > 0 } id)
        {
            return;
        }

        if (_sessionItems.TryGetValue(id, out var existing))
        {
            SessionList.Items.Remove(existing);
            _sessionItems.Remove(id);
        }

        var item = CreateSessionItem(session);
        _sessionItems[id] = item;
        SessionList.Items.Insert(0, item);
        SessionsHint.Visibility = Visibility.Collapsed;
    }

    private void SelectSessionItem(string sessionId)
    {
        var previousSuppress = _suppressSessionSelection;
        _suppressSessionSelection = true;
        try
        {
            if (_sessionItems.TryGetValue(sessionId, out var item))
            {
                SessionList.SelectedItem = item;
                SessionList.ScrollIntoView(item);
            }
            else
            {
                SessionList.SelectedItem = null;
            }
        }
        finally
        {
            _suppressSessionSelection = previousSuppress;
        }
    }

    private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSessionSelection)
        {
            return;
        }

        if (SessionList.SelectedItem is ListViewItem { Tag: string sessionId })
        {
            await SwitchToSessionAsync(sessionId);
        }
    }

    private async Task SwitchToSessionAsync(string sessionId, SessionReturnTarget? returnTarget = null, Action? onBound = null, string? directory = null,
        bool forceDraftTransition = false)
    {
        if (!TryPrepareDraftSlotNavigation())
        {
            if (!forceDraftTransition) { RestoreDraftSlotTabSelection(); return; }
            ForceSuspendDraftSlot("会话已创建，但输入尚未完成同步。内容已只读保留，请复制需要的内容后明确丢弃；不会自动发送。");
        }
        if (string.IsNullOrWhiteSpace(directory)) directory = null;
        directory ??= KnownSessionDirectory(sessionId);
        if (IsSameMessageBinding(sessionId, directory) && _sessionCts is not null)
        {
            OnSessionContextSelected(sessionId);
            BindComposer();
            return;
        }

        InvalidateViewAcknowledgement();
        ClearSessionReturnContext();
        _homeActive = false;
        _selectedSessionId = sessionId;
        _sessionMissing = false;
        _sessionGeneration++;
        BeginSessionMessageBinding(sessionId, directory);
        BindComposer();
        onBound?.Invoke(); // Capture the new binding before refresh or externally observable callbacks.

        // 会话切换：view ACK 活动快照立即失效；新窗口 Apply 后再按新上下文重新尝试。
        InvalidateViewAcknowledgement();
        CancelSessionOperations();
        _sessionCts = new CancellationTokenSource();
        RestartSessionEventSync(sessionId);
        InstallSessionReturnContext(returnTarget);

        EnsureConversationImages();
        _interactionsView.ClearAll();
        // 切换会话即切换整会话恢复的归属：清除旧会话的提示、确认快照与确认面板（并复位面板忙碌态），
        // 防止旧确认残留被用于当前会话；即使面板异步回调迟到，也因快照绑定不符而不生效。
        _sessionProtectionHint = false;
        ConfigureAgentSelectorForSession(sessionId);
        _recoveryOrigin = null;
        _sessionRecoveryPanel.HideConfirm();
        _sessionRecoveryPanel.SetBusy(false, null);
        _mutationEpoch++;
        _interactionBusy = false;
        _pendingAckIds.Clear();
        _deliveredNoticeShown = false;
        PermissionBar.IsOpen = false;
        UpdateWaitingText();
        UpdateEmptyState();
        UpdateComposerButtons();
        UpdateInteractionsSection();

        RaiseSessionTracked(sessionId, OpenCodeSessionTrackReason.Selected);
        await RefreshMessagesAsync(initial: true);
    }

    private void CancelSessionOperations()
    {
        var cts = _sessionCts;
        _sessionCts = null;
        // 仅取消、不释放：在途操作可能仍持有该 token 的注册，取消后由 GC 回收。
        cts?.Cancel();
    }

    // ------------------------------------------------------------------
    // 消息刷新（定时 + 手动；不重叠、可取消、防旧响应污染）
    // ------------------------------------------------------------------

    private void StartPolling()
    {
        if (_pollTimer is null)
        {
            _pollTimer = DispatcherQueue.CreateTimer();
            _pollTimer.Interval = PollInterval;
            _pollTimer.IsRepeating = true;
            _pollTimer.Tick += PollTimer_Tick;
        }

        _pollTimer.Start();
    }

    private void PollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_pageActive || _isRefreshing)
        {
            return;
        }

        _ = RefreshMessagesAsync();
        _ = RefreshListedSessionStatusesAsync();
    }

    private async void RefreshMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        // 显式刷新：唯一可重建“正常结束/异常停止”消息分页链的入口（分页提示中的恢复路径）。
        await RefreshMessagesAsync(explicitRefresh: true);
    }

    private async Task RefreshMessagesAsync(bool initial = false, bool forceFollow = false, bool explicitRefresh = false)
    {
        if (!SourceReady || _selectedSessionId is null || !_pageActive || _sessionMissing)
        {
            return;
        }

        var client = _client;
        if (client is null)
        {
            return;
        }

        if (_messageHistoryWorkflowOwner is not null) return; // No publication before the full send/handoff ends.
        if (_isRefreshing)
        {
            // 不重叠：标记一次补跑并保留本轮意图（首次加载/显式跟随不因排队丢失）。
            _refreshQueued = true;
            _refreshQueuedInitial |= initial;
            _refreshQueuedForceFollow |= forceFollow;
            _refreshQueuedExplicit |= explicitRefresh;
            return;
        }

        _isRefreshing = true;
        var generation = _sessionGeneration;
        var sessionId = _selectedSessionId;
        var historyRead = CaptureMessageHistoryRead(client, sessionId);
        var refreshOwner = new MessageRefreshOwner(historyRead);
        _messageRefreshOwner = refreshOwner;
        _messagesLoadError = null;
        if (!_messageWindowAccepted) _firstMessagesLoading = true;
        UpdateEmptyState();
        var ct = _sessionCts?.Token ?? CancellationToken.None;

        try
        {
            var eventReducer = _eventReducer;
            var eventVersion = eventReducer?.Version ?? 0;
            PrepareEventSnapshot(); // Buffered events drained here must win along with subsequent inflight events.
            OpenCodeSessionMessagesResponse response;
            try
            {
                // Start both reads in the same turn; state is still authoritative for publication.
                // A synchronous state correction may already invalidate the original read, in which
                // case only the corrected read below is sent. No network/state await precedes GET.
                using var speculativeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var boundaryRead = _messageHistoryNeedsState ? RefreshHistoryBoundaryAsync(historyRead, ct) : null;
                var messageRead = historyRead;
                var pendingMessages = IsCurrentMessageHistoryRead(messageRead)
                    ? StartMessageHistoryRead(messageRead,
                        new OpenCodeMessageQuery { Order = OpenCodeSessionOrders.Desc, Limit = 100 }, speculativeCts.Token)
                    : null;
                if (pendingMessages is not null)
                {
                    // State failure/correction can abandon this task. Observe late faults without
                    // awaiting a transport that ignores cancellation or touching the page/owner.
                    _ = pendingMessages.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                try
                {
                    if (boundaryRead is not null)
                    {
                        var refreshed = await boundaryRead;
                        if (refreshed is null) return;
                        historyRead = refreshed;
                        refreshOwner.Read = historyRead;
                        ct = _sessionCts?.Token ?? CancellationToken.None;
                    }
                    if (pendingMessages is null || messageRead != historyRead)
                    {
                        // Directory resolution/correction or rollback reset changes read identity.
                        // Never relabel an in-flight snapshot with the new directory/epoch.
                        speculativeCts.Cancel();
                        pendingMessages = StartMessageHistoryRead(historyRead,
                            new OpenCodeMessageQuery { Order = OpenCodeSessionOrders.Desc, Limit = 100 }, ct);
                    }
                    response = await pendingMessages;
                }
                finally
                {
                    speculativeCts.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
                SetMessageLoadFailure(refreshOwner);
                return;
            }
            catch (OpenCodeApiException ex)
            {
                SetMessageLoadFailure(refreshOwner);
                if (OwnsMessageRefresh(refreshOwner) && IsCurrentMessageHistoryRead(historyRead))
                {
                    HandleApiException(ex, "消息刷新");
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                SetMessageLoadFailure(refreshOwner);
                if (OwnsMessageRefresh(refreshOwner) && IsCurrentMessageHistoryRead(historyRead))
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "无法连接 OpenCode 服务", "消息刷新失败（本页会自动重试）。");
                }

                return;
            }
            catch (OpenCodeTimeoutException)
            {
                SetMessageLoadFailure(refreshOwner);
                if (OwnsMessageRefresh(refreshOwner) && IsCurrentMessageHistoryRead(historyRead))
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "消息刷新超时", "客户端 30 秒内未完成本次请求（本页会自动重试）。");
                }

                return;
            }
            catch (OpenCodeProtocolException)
            {
                SetMessageLoadFailure(refreshOwner);
                if (OwnsMessageRefresh(refreshOwner) && IsCurrentMessageHistoryRead(historyRead))
                {
                    ShowMessage(InfoBarSeverity.Error, "响应不符合契约", "消息响应不符合已核实的 V2 契约；已保留当前已渲染内容。");
                }

                return;
            }

            // 防旧 response 污染：会话已切换或页面已卸载时丢弃本次结果。
            if (!OwnsMessageRefresh(refreshOwner) || !IsCurrentMessageHistoryRead(historyRead))
            {
                return;
            }

            SetConnectionState(ConnectionState.Connected);

            // 阅读位置：以“追加前”的实时位置判定是否跟随（尊重用户在本次 await 期间的上翻）；
            // initial（会话首次完整加载）与 forceFollow（用户主动发送）显式定位底部；
            // 折叠工具/富文本、窗宽变化不经此路径，不触碰用户阅读位置。
            var followBottom = initial || forceFollow || IsScrolledToBottom();
            var orderedMessages = OrderMessages(response.Data);
            // A same-session authoritative boundary read can reset the reducer without losing this
            // valid history read. Every event in that replacement reducer is newer than its reset.
            var mergeVersion = ReferenceEquals(eventReducer, _eventReducer) ? eventVersion : 0;
            orderedMessages = _eventReducer?.MergeSnapshot(orderedMessages, mergeVersion) ?? orderedMessages;
            ApplyRefreshedMessages(orderedMessages, response.Cursor?.Next, explicitRefresh);
            if (!OwnsMessageRefresh(refreshOwner) || !IsCurrentMessageHistoryRead(historyRead)) return;
            _firstMessagesLoading = false;
            _messagesResponseEmpty = response.Data is { Count: 0 } && orderedMessages.Count == 0;
            _messageWindowAccepted = true;
            _messagesLoadError = null;
            RetainHistoryChildEvidence(sessionId, MergeMessageHistory());
            var projectedWindow = ProjectMessageHistory(orderedMessages);
            ObserveExecutionState(sessionId, projectedWindow);

            // 最新窗口已成功 Apply（真实渲染窗口，非历史 prepend）：向 view ACK 入口提交该窗口与宿主
            // 窗口可见性；仅在页面/窗口真正可见时按 (来源, 会话, 水位) 去重尝试一次，绝不额外 GET 水位。
            if (!OwnsMessageRefresh(refreshOwner) || !IsCurrentMessageHistoryRead(historyRead)) return;
            NotifyViewAcknowledgementRendered(projectedWindow, _hostWindowVisible);
            UpdateEmptyState();

            if (followBottom)
            {
                ScrollToBottom(animated: !(initial || forceFollow));
            }

            await RefreshAuxiliaryAsync(client, sessionId, generation, ct, () => OwnsMessageRefresh(refreshOwner) && IsCurrentMessageHistoryRead(historyRead));
        }
        catch (Exception)
        {
            SetMessageLoadFailure(refreshOwner);
            if (OwnsMessageRefresh(refreshOwner) && IsCurrentMessageHistoryRead(historyRead))
            {
                ShowMessage(InfoBarSeverity.Error, "消息刷新失败", "发生未预期错误；已保留当前已渲染内容。");
            }
        }
        finally
        {
            if (ReferenceEquals(_messageRefreshOwner, refreshOwner))
            {
                _messageRefreshOwner = null;
                _isRefreshing = false;
                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    var queuedInitial = _refreshQueuedInitial;
                    var queuedForceFollow = _refreshQueuedForceFollow;
                    var queuedExplicit = _refreshQueuedExplicit;
                    _refreshQueuedInitial = false;
                    _refreshQueuedForceFollow = false;
                    _refreshQueuedExplicit = false;
                    if (_pageActive && _selectedSessionId is not null && !_sessionMissing)
                    {
                        _ = RefreshMessagesAsync(queuedInitial, queuedForceFollow, queuedExplicit);
                    }
                }
            }
        }
    }

    /// <summary>待处理权限/表单卡片与 inbox 待投递跟踪；失败不影响消息刷新的连接状态。</summary>
    private async Task RefreshAuxiliaryAsync(OpenCodeClient client, string sessionId, int generation, CancellationToken ct, Func<bool> historyCurrent)
    {
        IReadOnlyList<OpenCodePermissionRequest>? permissions = null;
        IReadOnlyList<OpenCodeFormInfo>? forms = null;

        try
        {
            permissions = await client.GetPendingPermissionsAsync(sessionId, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OpenCodeException)
        {
            // 获取失败：不推断“无等待”，保留现有卡片（提交前总会重新查询，陈旧展示不会导致误提交）。
            if (historyCurrent())
            {
                PermissionBar.IsOpen = false;
            }
        }

        if (!historyCurrent())
        {
            return;
        }

        try
        {
            forms = await client.GetPendingFormsAsync(sessionId, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OpenCodeException)
        {
            // 同上：保留现有表单卡片。
        }

        if (!historyCurrent())
        {
            return;
        }

        if (permissions is not null)
        {
            // 待处理权限改由输入区遮罩逐个呈现（重设计 v3 §5）；提示条只保留给查询/提交错误。
            // 一次成功的查询仍关闭此前的错误提示（与原行为一致）。
            PermissionBar.IsOpen = false;
        }

        var externallyRemoved = _interactionsView.Apply(permissions, forms);
        _ = externallyRemoved;
        UpdateInteractionsSection();

        try
        {
            var inbox = await client.GetInboxAsync(sessionId, ct);
            if (!historyCurrent())
            {
                return;
            }

            // 排队条（重设计 v3 §9.1）：复用本次只读查询结果，不额外请求。
            _queuedInboxSessionId = sessionId;
            _queuedInbox = inbox;

            var stillPending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in inbox)
            {
                if (item.Id is { Length: > 0 } id)
                {
                    stillPending.Add(id);
                }
            }

            // 已核实语义：条目仍出现在 inbox = 尚未投递；不再出现 = 已投递到时间线。
            var removed = _pendingAckIds.RemoveAll(id => !stillPending.Contains(id));
            if (removed > 0 && _pendingAckIds.Count == 0)
            {
                _deliveredNoticeShown = true;
            }

            UpdateWaitingText();
        }
        catch (OperationCanceledException)
        {
        }
        catch (OpenCodeException)
        {
            // inbox 查询失败不改变任何状态；下一轮刷新重试。
        }
    }

    /// <summary>把服务端返回的 order=desc（最新在前）反转为展示顺序（旧→新）。</summary>
    private static List<OpenCodeMessage> OrderMessages(List<OpenCodeMessage>? data)
    {
        var result = new List<OpenCodeMessage>();
        if (data is null)
        {
            return result;
        }

        for (var i = data.Count - 1; i >= 0; i--)
        {
            result.Add(data[i]);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 发送 / 停止
    // ------------------------------------------------------------------

    private async void SendStopButton_Click(object sender, RoutedEventArgs e)
    {
        // Flush observed input before choosing Send versus Stop, not merely in the send branch.
        // Clean empty drafts remain eligible for explicit Stop; held/composing input cannot route.
        if (!_pageActive || _composerView is null || !_composerView.PrepareSubmission() || HasPendingNativeAction()) return;
        // 单按钮：仅“无草稿且当前选中会话确有权威活动状态（包括其他终端）”执行停止；其余一律走发送
        // （空白输入由既有入口拒绝；Enter 路径同样不经过停止分支）。
        if (ShouldStopSelectedSession())
        {
            await InterruptSelectedSessionAsync();
            return;
        }

        RequestComposerSubmission();
    }

    /// <summary>
    /// 单按钮停止语义：无草稿、当前选中会话存在且处于本页已观察到的 Working（回合执行中）才成立；
    /// idle / Unknown / 无会话一律不成立——绝不凭推测假 Working，也绝不对非选中会话发起中断。
    /// </summary>
    private bool ShouldStopSelectedSession()
    {
        return _client is not null
            && !_sessionMissing
            && _pageActive
            && !_isSending
            && !_composerPickerPending
            && _messageHistoryWorkflowOwner is null
            && NativeSendStopDisplayAllowed()
            && _selectedSessionId is { } selected
            && _composerController is not null
            && !GetComposerSnapshot().HasDraft
            && !GetComposerSnapshot().IsBusy
            && IsComposerSessionWorking(selected);
    }


    /// <summary>
    /// 中断当前选中会话（单按钮停止分支）：只捕获点击时的选中会话 ID，绝不涉及根会话/家族/其他会话；
    /// 只转述服务端 interrupted 结果，不推断执行状态、不自动重发。
    /// </summary>
    private async Task InterruptSelectedSessionAsync()
    {
        if (!SourceReady || !ShouldStopSelectedSession() || _composerView?.PrepareSubmission() != true)
        {
            return;
        }

        var client = _client;
        var binding = _composerBinding;
        if (client is null || binding is null || _composerController is null)
        {
            return;
        }

        var sessionId = binding.SessionId;
        if (!string.Equals(sessionId, _selectedSessionId, StringComparison.Ordinal)) return;
        var historyEpoch = _messageHistoryEpoch;
        var submitted = _composerController.Snapshot.Token;
        var input = _composerView.InputEvidence;
        if (!input.IsClean) return;
        using var cancellation = CreateOperationToken(binding.Source);
        var ct = cancellation.Token;
        _isSending = true;
        var owner = BeginLocalAgentSubmission();
        bool Owns() => OwnsComposerSubmission(binding, owner);
        bool CanStart() => Owns() && historyEpoch == _messageHistoryEpoch
            && _messageHistoryWorkflowOwner is null && IsComposerSessionWorking(sessionId);
        var operationId = Guid.NewGuid();
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sessionId + ":" + binding.Revision + ":" + historyEpoch)));
        SendStopButton.IsEnabled = false;
        OpenCodeInteractionGate.Lease? interruptLease = null;
        try
        {
            var lease = interruptLease = await OpenCodeInteractionGate.Shared.EnterAsync(binding.Source.Identity, sessionId, ct);
            if (!CanStart() || !lease.CanControlSession()) return;
            Task<bool>? pending = null;
            binding.Source.TryExecute(ct, () =>
            {
                if (CanStart() && !HasPendingNativeAction() && _composerController.Snapshot.Token == submitted
                    && OpenCodeComposerInputEvidence.StillCurrent(input, _composerView.InputEvidence)
                    && !_composerController.Snapshot.HasDraft && !_isComposing
                    && lease.CanControlSession())
                {
                    var protectedMutation = false;
                    BeginComposerInterruptProtection(lease, operationId, fingerprint, ref protectedMutation);
                    if (!protectedMutation) return;
                    pending = client.InterruptGuardedAsync(sessionId, start => { start(); return true; }, ct);
                }
            });
            if (pending is null) return;
            var interrupted = await pending;
            // Only a verified boolean response settles the exact operation. Cancellation, transport errors,
            // source/session loss and malformed responses leave gate protection intact (no automatic retry).
            binding.Source.TryExecute(CancellationToken.None, () =>
            {
                if (Owns()) lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Succeeded);
            });
            lease.Dispose();
            if (!Owns() || binding.Source.IsRevoked)
            {
                return;
            }

            SetConnectionState(ConnectionState.Connected);

            // 只转述服务端已核实的返回语义，不推断执行状态。
            if (interrupted)
            {
                ShowMessage(InfoBarSeverity.Informational, "已中断", "服务端确认已中断当前执行（interrupted=true）。");
            }
            else
            {
                ShowMessage(InfoBarSeverity.Informational, "无可中断的执行", "服务端报告当前空闲（interrupted=false）。");
            }

            await RefreshMessagesAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (OpenCodeSessionControlException ex)
        {
            if (ex.VerifiedPreEffect && interruptLease is { } lease)
            {
                binding.Source.TryExecute(CancellationToken.None, () =>
                {
                    if (Owns() && lease.GetSessionControlProtection() is { } protection
                        && protection.OperationId == operationId && protection.Kind == SessionControlKind.Interrupt)
                        lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Failed);
                });
            }
            if (Owns() && !binding.Source.IsRevoked)
                HandleApiException(new OpenCodeApiException(ex.StatusCode, null, null, "Session interrupt request failed."), "中断");
        }
        catch (OpenCodeApiException ex)
        {
            if (Owns() && !binding.Source.IsRevoked)
            {
                HandleApiException(ex, "中断");
            }
        }
        catch (OpenCodeTransportException)
        {
            if (Owns() && !binding.Source.IsRevoked)
            {
                SetConnectionState(ConnectionState.Failed);
                ShowMessage(InfoBarSeverity.Error, "无法连接 OpenCode 服务", "中断结果未知；不会自动重试。");
            }
        }
        catch (OpenCodeTimeoutException)
        {
            if (Owns() && !binding.Source.IsRevoked)
            {
                ShowMessage(InfoBarSeverity.Error, "中断超时", "客户端 30 秒内未收到中断结果。");
            }
        }
        catch (OpenCodeProtocolException)
        {
            if (Owns() && !binding.Source.IsRevoked)
            {
                ShowMessage(InfoBarSeverity.Error, "响应不符合契约", "中断响应不符合已核实的 V2 契约。");
            }
        }
        catch (Exception)
        {
            if (Owns() && !binding.Source.IsRevoked)
            {
                ShowMessage(InfoBarSeverity.Error, "中断失败", "发生未预期错误。");
            }
        }
        finally
        {
            interruptLease?.Dispose();
            if (EndLocalAgentSubmission(owner))
            {
                _isSending = false;
                if (_pageActive) UpdateComposerButtons();
            }
        }
    }

    // ------------------------------------------------------------------
    // 权限 / 表单交互（提交前重新查询、指纹校验；结果未知不自动重发）
    // ------------------------------------------------------------------

    /// <summary>捕获的会话上下文是否仍然有效：页面存活、会话代际未变、客户端实例未变。</summary>
    private bool IsCurrentSession(int generation, OpenCodeClient client)
        => _pageActive && generation == _sessionGeneration && ReferenceEquals(client, _client);

    /// <summary>action 内使用的纯字段比较：只比较本页捕获的字段，不访问 App、不取来源锁。</summary>
    private bool IsCurrentBindingFields(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration)
        => _pageActive
        && ReferenceEquals(source, _source)
        && revision == _sourceRevision
        && sourceGeneration == _sourceGeneration
        && ReferenceEquals(client, _client)
        && sessionGeneration == _sessionGeneration;

    /// <summary>完整绑定复核（含来源未撤销）；只允许在 action 之外调用。</summary>
    private bool IsCurrentBinding(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration)
        => IsCurrentBindingFields(source, revision, sourceGeneration, client, sessionGeneration) && !source.IsRevoked;

    /// <summary>await 后的统一复核：cancellation 或任一绑定分量变化即为丢失。</summary>
    private bool BindingLost(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, CancellationToken ct)
        => ct.IsCancellationRequested || !IsCurrentBinding(source, revision, sourceGeneration, client, sessionGeneration);

    /// <summary>操作令牌：来源撤销 + 页面 + 会话；任一取消即取消（每入口独立创建并释放）。</summary>
    private CancellationTokenSource CreateOperationToken(OpenCodePageSource source)
        => CancellationTokenSource.CreateLinkedTokenSource(
            source.RevocationToken,
            _pageCts?.Token ?? CancellationToken.None,
            _sessionCts?.Token ?? CancellationToken.None);

    /// <summary>
    /// 经来源 TryExecute 起发一次读取（GET）：action 内复核捕获上下文后调用 read 并取得 Task；
    /// 返回 false 表示来源已撤销/已取消或上下文已变化——未起发任何请求。
    /// </summary>
    private bool TryStartRead<T>(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, CancellationToken ct, Func<Task<T>> read, out Task<T>? task)
    {
        Task<T>? started = null;
        if (!source.TryExecute(ct, () =>
        {
            if (!IsCurrentBindingFields(source, revision, sourceGeneration, client, sessionGeneration))
            {
                return;
            }

            started = read();
        }))
        {
            task = null;
            return false;
        }

        task = started;
        return started is not null;
    }

    /// <summary>
    /// 单条未知解除（affectsSession:false，绝不动会话级保护）：经来源 TryExecute 原子化执行；
    /// 返回 false 表示未执行（来源已撤销/已取消/上下文变化或保护状态已变化）。
    /// </summary>
    private bool TryResolveUnknown(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, CancellationToken ct,
        OpenCodeInteractionGate.Lease lease, string id, NotificationNavigationKind kind, bool allowRetry)
        => TryResolve(source, revision, sourceGeneration, client, sessionGeneration, ct,
            () => lease.ResolveUnknown(id, kind, allowRetry, affectsSession: false));

    /// <summary>整会话来源键解除：精确来源键 + Permission + 仍为 unknown 的校验由 gate 执行。</summary>
    private bool TryResolveSessionUnknown(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, CancellationToken ct,
        OpenCodeInteractionGate.Lease lease, string id, NotificationNavigationKind kind, bool allowRetry)
        => TryResolve(source, revision, sourceGeneration, client, sessionGeneration, ct,
            () => lease.ResolveSessionUnknown(id, kind, allowRetry));

    private bool TryResolve(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, CancellationToken ct, Action resolve)
    {
        try
        {
            return source.TryExecute(ct, () =>
            {
                if (!IsCurrentBindingFields(source, revision, sourceGeneration, client, sessionGeneration))
                {
                    return;
                }

                resolve();
            });
        }
        catch (InvalidOperationException)
        {
            // 保护状态已变化（如会话级保护需要整会话恢复）：未执行任何解除。
            ShowMessage(InfoBarSeverity.Warning, "保护状态已变化", "未执行解除；请重新核对当前状态后再试。");
            return false;
        }
    }

    /// <summary>
    /// 非 unknown 的 lease 结果写入（Expired / Failed / Succeeded）：仅在捕获来源的 TryExecute 私锁内、
    /// 且完整绑定字段一致时才调用 <c>lease.CompleteMutation</c>；返回 false 表示 action 未被调用、
    /// 绑定已失效或 gate 已拒绝写入——此时旧 lease 一律保持 OutcomeUnknown（绝不写
    /// Failed / Expired / Succeeded，绝不自动解锁）。completed 只在真正写入后为 true，action 早退不算写入。
    /// action 内不取 App 锁、不做 UI、不 await。
    /// </summary>
    private bool TryCompleteMutation(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, CancellationToken ct,
        OpenCodeInteractionGate.Lease lease, NotificationInteractionStatus status)
    {
        var completed = false;
        try
        {
            source.TryExecute(ct, () =>
            {
                if (!IsCurrentBindingFields(source, revision, sourceGeneration, client, sessionGeneration))
                {
                    return;
                }

                lease.CompleteMutation(status);
                completed = true;
            });
        }
        catch (InvalidOperationException)
        {
            // gate 无进行中的 mutation 或状态已变化：未写入任何结果。
            return false;
        }

        return completed;
    }

    private static bool ContainsRequest(IReadOnlyList<OpenCodePermissionRequest> permissions, string requestId)
    {
        foreach (var permission in permissions)
        {
            if (string.Equals(permission?.Id, requestId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>提交/查询忙碌态：同时作用于交互卡片与整会话恢复面板。</summary>
    private void SetInteractionBusy(bool isForm, string id, bool busy, string label)
    {
        _interactionsView.SetSubmitting(isForm, id, busy, label);
        _sessionRecoveryPanel.SetBusy(busy, label);
    }

    /// <summary>移除一个由本页操作结算的交互卡片（G1：正常完成回答/权限决定后不再显示瞬态“服务器已接收…”提示条；真正错误仍走 ShowMessage）。</summary>
    private void SettleInteraction(bool isForm, string id, string title, string message)
    {
        _ = title;
        _ = message;
        _interactionsView.RemoveSettled(isForm, id);
        UpdateInteractionsSection();
    }

    private void UpdateInteractionsSection()
    {
        RefreshComposerView(); // Shared gate has no change event; this path also runs after polling/settlement.
        // 权限请求改由输入区遮罩逐个呈现（重设计 v3 §5）；交互面板只统计其余条目（表单等）。
        var count = Math.Max(0, _interactionsView.Count - SyncComposerOverlays());
        InteractionsHeader.Text = count > 0 ? "待处理交互（" + count + "）" : "会话交互恢复";

        if (!_sessionRecoveryPanel.IsShowingConfirm)
        {
            // 入口不再常驻：仅在当前有效 source/session 下，向 gate 做一次只读探查（不创建状态、不阻塞）。
            // 探查成功才据实更新本地提示；探查失败（mutex 忙/标识非法）保留已有提示，绝不解释为“无保护”。
            // 因此切走再切回、或由 toast 产生的保护也能被重新发现，不依赖可能丢失的本地提示。
            if (SourceReady && _pageActive && _selectedSessionId is not null && !_sessionMissing)
            {
                var identity = _source!.Identity;
                if (OpenCodeInteractionGate.Shared.TryPeekSessionProtection(identity, _selectedSessionId, out var protection))
                {
                    _sessionProtectionHint = protection is not null;
                }

                if (_sessionProtectionHint)
                {
                    _sessionRecoveryPanel.ShowEntry(
                        "此前有一次“拒绝”的结果未知，可能已影响本会话其他待处理请求。点击可核对并解除（不会自动重发）。");
                }
                else
                {
                    _sessionRecoveryPanel.HideEntry();
                }
            }
            else
            {
                _sessionRecoveryPanel.HideEntry();
            }
        }

        // 先完成入口显隐，再按最终 IsShowing 与卡片数统一决定父容器可见性（避免首次显示时祖先仍 Collapsed）。
        InteractionsSection.Visibility = count > 0 || _sessionRecoveryPanel.IsShowing
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>重新查询失败（非 404/409）：不发送任何提交，保留现有卡片。</summary>
    private void ShowQueryFailure(string operation, OpenCodeException ex)
    {
        if (ex is OpenCodeApiException api)
        {
            ShowMessage(InfoBarSeverity.Error, "重新查询失败", DescribeApiError(api, operation));
        }
        else if (ex is OpenCodeTimeoutException)
        {
            ShowMessage(InfoBarSeverity.Error, "重新查询超时", "客户端 30 秒内未完成重新查询；未发送后续提交。");
        }
        else if (ex is OpenCodeProtocolException)
        {
            ShowMessage(InfoBarSeverity.Error, "响应不符合契约", "重新查询响应不符合已核实的 V2 契约；未发送后续提交。");
        }
        else
        {
            ShowMessage(InfoBarSeverity.Error, "无法连接 OpenCode 服务", "重新查询失败；未发送后续提交。");
        }
    }

    private static string DecisionLabel(string decision) => decision switch
    {
        OpenCodePermissionDecisions.Once => "允许一次",
        OpenCodePermissionDecisions.Always => "始终允许",
        OpenCodePermissionDecisions.Reject => "拒绝",
        _ => decision,
    };

    private static string DecisionBusyLabel(string decision) => decision switch
    {
        OpenCodePermissionDecisions.Once => "正在提交“允许一次”…",
        OpenCodePermissionDecisions.Always => "正在提交“始终允许”…",
        OpenCodePermissionDecisions.Reject => "正在提交“拒绝”…",
        _ => "正在提交…",
    };

    /// <summary>
    /// 提交权限决定：先按 id 重新查询 pending，指纹不一致则不提交并刷新卡片；
    /// 仅服务端 204 视为成功；404/409 视为过期；400/401/403 视为被拒绝；
    /// 其余状态、超时、连接中断与非 204 的 2xx 一律按“结果未知”处理，绝不自动重发。
    /// 全流程绑定捕获的来源（引用 + Revision + 来源代际）与页面/会话代际；GET、Begin+起发与
    /// 结果应用都在捕获来源的 TryExecute 私锁内复核后执行，来源撤销先赢时零起发。
    /// </summary>
    private async Task SubmitPermissionDecisionAsync(OpenCodePermissionRequest displayed, string decision)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked)
        {
            return;
        }

        var requestId = displayed.Id ?? string.Empty;
        if (requestId.Length == 0
            || !string.Equals(displayed.SessionId, sessionId, StringComparison.Ordinal)
            || decision is not (OpenCodePermissionDecisions.Once
                or OpenCodePermissionDecisions.Always
                or OpenCodePermissionDecisions.Reject))
        {
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;
        var isReject = decision == OpenCodePermissionDecisions.Reject;

        _interactionBusy = true;
        SetInteractionBusy(false, requestId, true, DecisionBusyLabel(decision));
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；未提交。");
                return;
            }

            var activeLease = lease!;

            if (activeLease.GetProtectionStatus(requestId, NotificationNavigationKind.Permission) is { } protection)
            {
                HandleProtectedPermissionAtSubmit(requestId, protection, activeLease.GetSessionProtection() is not null);
                return;
            }

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetPendingPermissionAsync(sessionId, requestId, ct), out var freshTask))
            {
                // 来源已撤销/已取消或上下文已变化：未起发任何请求。
                return;
            }

            OpenCodePermissionRequest fresh;
            try
            {
                // 提交前重新查询 pending：绝不把旧快照当作授权依据。
                fresh = await freshTask!;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeApiException ex) when (ex.StatusCode is 404 or 409)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    SettleInteraction(false, requestId, "权限请求已过期",
                        "该权限请求已不存在或已处理（HTTP " + ex.StatusCode + "）；已从列表移除，未发送决定。");
                }

                return;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("权限重新查询", ex);
                }

                return;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return;
            }

            if (!string.Equals(
                    OpenCodeInteractionFingerprint.ForPermission(fresh),
                    OpenCodeInteractionFingerprint.ForPermission(displayed),
                    StringComparison.Ordinal))
            {
                // 范围变化：不提交旧决定，刷新卡片让用户重新确认。
                _interactionsView.ReplacePermission(fresh);
                ShowMessage(InfoBarSeverity.Warning, "权限请求已变化",
                    "服务端返回的权限范围与显示时不同；已刷新卡片，请核对后重新决定。");
                return;
            }

            // 唯一一次 mutation：action 内复核捕获上下文 → BeginMutation → 仅“起发”客户端调用；
            // 退出 source 私锁后才 await（Revoke 与起发同锁线性化）。
            Task? mutationTask = null;
            var began = false;
            var started = false;
            try
            {
                var executed = source.TryExecute(ct, () =>
                {
                    if (!IsCurrentBindingFields(source, revision, sourceGeneration, client, generation))
                    {
                        return;
                    }

                    // Reject 会结算同会话其他 pending：整个会话进入未知保护，直至显式整会话恢复。
                    activeLease.BeginMutation(requestId, NotificationNavigationKind.Permission, affectsSession: isReject);
                    began = true;
                    mutationTask = client.ReplyPermissionAsync(sessionId, fresh, decision, message: null, cancellationToken: ct);
                    started = true;
                });

                if (!executed || !started || mutationTask is null)
                {
                    // 来源已撤销/已取消或上下文已变化：未起发 mutation，也绝不调用 CompleteMutation。
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // 与通知入口竞争：保护已生效，绝不重发。
                _interactionsView.MarkPermissionBlocked(requestId,
                    "共享仲裁保护已生效（另一入口正在处理或结果未知）；未发送决定，请点击“重新查询状态”核实。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁保护生效", "另一入口正在处理该请求或结果未知；本页未提交。");
                return;
            }

            try
            {
                // 使用刚查询的 DTO；成功仅当 204（客户端强制），绝不自动重试。
                await mutationTask;
            }
            catch (OperationCanceledException)
            {
                if (began)
                {
                    // 决定可能已送出：保留 OutcomeUnknown 保护，绝不自动重发。
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                    HandleUnknownPermissionOutcome(source, revision, sourceGeneration, client, generation,
                        requestId, decision, "已取消（决定可能已送出）");
                }

                return;
            }
            catch (OpenCodeApiException ex)
            {
                if (ex.StatusCode is not (404 or 409 or 400 or 401 or 403))
                {
                    // 其他状态无法确认是否已应用：按“结果未知”处理，不自动重发。
                    if (began)
                    {
                        activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                    }

                    HandleUnknownPermissionOutcome(source, revision, sourceGeneration, client, generation,
                        requestId, decision, "HTTP " + ex.StatusCode);
                    return;
                }

                // 明确结局（过期 / 被拒绝）：仅在捕获绑定仍完整一致时写入；绑定失效、来源撤销或取消
                // 一律不写 Failed/Expired，旧 lease 保持 OutcomeUnknown（绝不误删记录、绝不误清会话保护）。
                var completed = began && TryCompleteMutation(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, ex.StatusCode is 404 or 409
                        ? NotificationInteractionStatus.Expired
                        : NotificationInteractionStatus.Failed);
                if (!completed)
                {
                    return;
                }

                if (ex.StatusCode is 404 or 409)
                {
                    if (isReject)
                    {
                        // 明确结局已清除会话保护标记：同步移除恢复入口。
                        _sessionProtectionHint = false;
                    }

                    SettleInteraction(false, requestId, "权限请求已过期",
                        "该权限请求已不存在或已处理（HTTP " + ex.StatusCode + "）；决定未提交。");
                    UpdateInteractionsSection();
                }
                else
                {
                    if (isReject)
                    {
                        _sessionProtectionHint = false;
                        UpdateInteractionsSection();
                    }

                    ShowMessage(InfoBarSeverity.Error, "权限决定被拒绝",
                        DescribeApiError(ex, "权限决定") + "决定未提交，可核对后重试。");
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownPermissionOutcome(source, revision, sourceGeneration, client, generation,
                    requestId, decision, "连接中断");
                return;
            }
            catch (OpenCodeTimeoutException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownPermissionOutcome(source, revision, sourceGeneration, client, generation,
                    requestId, decision, "超时");
                return;
            }
            catch (OpenCodeProtocolException)
            {
                // 非 204 的 2xx：服务端已响应但未按契约明确成功，结果不可确认。
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownPermissionOutcome(source, revision, sourceGeneration, client, generation,
                    requestId, decision, "响应不符合契约");
                return;
            }

            if (!began || !TryCompleteMutation(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, NotificationInteractionStatus.Succeeded))
            {
                // 提交期间来源换代/会话切换/来源撤销：结果不归属当前代际；旧 lease 保持 OutcomeUnknown，
                // 不更新 UI、绝不自动重发。
                return;
            }

            SettleInteraction(false, requestId, "权限决定已提交",
                "服务端已明确接受（HTTP 204）：" + DecisionLabel(decision) + "。");
            if (isReject)
            {
                // 拒绝会连带处理本会话其他 pending：其消失不再单独提示（是否仍在待处理以重新查询为准）。
                _sessionProtectionHint = false;
                UpdateInteractionsSection();
                HandleRejectSideEffects(generation, client, requestId, invalidateOthers: false);
            }

            await RefreshMessagesAsync();
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "权限操作失败", "发生未预期错误；未自动重发。");
            }
        }
        finally
        {
            // 已 Begin 但未 Complete 的 mutation 由 gate 保留为 OutcomeUnknown（绝不自动解锁）。
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(false, requestId, false, string.Empty);
            }
        }
    }

    /// <summary>
    /// 进入共享交互仲裁：返回 (Ok, Cancelled, Lease, Error)。
    /// <para>一律 fail-closed：等待租约期间来源（引用 + Revision + 来源代际）/ 会话 / client 任一变化，
    /// 均返回 <c>Ok=false</c> 且 <c>Lease=null</c>——绝不返回 <c>Ok=true</c> 且 <c>Lease=null</c>，
    /// 从而杜绝“无租约即允许 POST/DELETE”。身份取自捕获来源的 <see cref="OpenCodePageSource.Identity"/>；
    /// 静态身份提示（旧 SetTrustedEndpointIdentity）绝不参与授权。</para>
    /// </summary>
    private async Task<(bool Ok, bool Cancelled, OpenCodeInteractionGate.Lease? Lease, string? Error)> EnterGateAsync(
        OpenCodePageSource source, long revision, int sourceGeneration, OpenCodeClient client,
        string sessionId, int generation, CancellationToken cancellationToken)
    {
        OpenCodeInteractionGate.Lease lease;
        try
        {
            lease = await OpenCodeInteractionGate.Shared.EnterAsync(source.Identity, sessionId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (false, true, null, null);
        }
        catch (ArgumentException)
        {
            return (false, false, null, "来源或会话标识非法；未进入共享仲裁。");
        }
        catch (InvalidOperationException)
        {
            return (false, false, null, "共享仲裁容量已满（fail-closed）；请在应用侧处理后重试。");
        }

        // 等待租约期间来源可能撤销/换代、页面可能切换会话或重建客户端：任一变化一律拒绝提交。
        if (!IsCurrentBinding(source, revision, sourceGeneration, client, generation))
        {
            lease.Dispose();
            return (false, false, null, "等待共享仲裁期间来源或会话已变化；已拒绝提交（fail-closed），未发送任何请求。");
        }

        return (true, false, lease, null);
    }

    /// <summary>提交时发现该权限请求已受共享保护：绝不重复提交，按保护状态给出明确处置。</summary>
    private void HandleProtectedPermissionAtSubmit(string requestId, NotificationInteractionStatus protection, bool sessionProtected)
    {
        switch (protection)
        {
            case NotificationInteractionStatus.OutcomeUnknown:
                if (sessionProtected)
                {
                    // 整会话保护（拒绝结果未知）：单卡入口不适用，指向专门恢复入口。
                    _sessionProtectionHint = true;
                    UpdateInteractionsSection();
                    _interactionsView.MarkPermissionBlocked(requestId,
                        "共享仲裁保护：本会话存在“拒绝结果未知”的整会话保护；未重复提交，请在“整会话恢复”中核对处理。");
                    ShowMessage(InfoBarSeverity.Warning, "本会话受整会话保护",
                        "此前“拒绝”的结果未知，本会话其他请求暂不能提交；请在“整会话恢复”入口核对处理。");
                }
                else
                {
                    _interactionsView.MarkPermissionBlocked(requestId,
                        "共享仲裁保护：结果未知（可能已由通知入口提交，或此前提交结果不确定）；未重复提交，请点击“重新查询状态”核实。");
                    ShowMessage(InfoBarSeverity.Warning, "该请求受共享保护",
                        "另一入口可能已提交该决定或结果未知；本页未重复提交，请点击“重新查询状态”核实。");
                }

                _ = RefreshMessagesAsync();
                break;
            case NotificationInteractionStatus.Duplicate:
            case NotificationInteractionStatus.Expired:
                _interactionsView.Suppress(false, requestId);
                UpdateInteractionsSection();
                break;
            default:
                _interactionsView.MarkPermissionBlocked(requestId,
                    "共享仲裁保护生效（" + protection + "）；未提交，请在应用侧核实后重试。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁保护生效", "未提交该决定。");
                break;
        }
    }

    /// <summary>提交时发现该表单已受共享保护：绝不重复提交，按保护状态给出明确处置。</summary>
    private void HandleProtectedFormAtSubmit(string formId, NotificationInteractionStatus protection, bool sessionProtected)
    {
        switch (protection)
        {
            case NotificationInteractionStatus.OutcomeUnknown:
                if (sessionProtected)
                {
                    // 整会话保护（拒绝结果未知）：单卡入口不适用，指向专门恢复入口。
                    _sessionProtectionHint = true;
                    UpdateInteractionsSection();
                    _interactionsView.MarkFormBlocked(formId,
                        "共享仲裁保护：本会话存在“拒绝结果未知”的整会话保护；未重复提交，请在“整会话恢复”中核对处理。");
                    ShowMessage(InfoBarSeverity.Warning, "本会话受整会话保护",
                        "此前“拒绝”的结果未知，本会话其他请求暂不能提交；请在“整会话恢复”入口核对处理。");
                }
                else
                {
                    _interactionsView.MarkFormBlocked(formId,
                        "共享仲裁保护：结果未知（可能已由通知入口提交，或此前提交结果不确定）；未重复提交，请点击“重新查询状态”核实。");
                    ShowMessage(InfoBarSeverity.Warning, "该表单受共享保护",
                        "另一入口可能已提交该表单或结果未知；本页未重复提交，请点击“重新查询状态”核实。");
                }

                _ = RefreshMessagesAsync();
                break;
            case NotificationInteractionStatus.Duplicate:
            case NotificationInteractionStatus.Expired:
                _interactionsView.Suppress(true, formId);
                UpdateInteractionsSection();
                break;
            default:
                _interactionsView.MarkFormBlocked(formId,
                    "共享仲裁保护生效（" + protection + "）；未提交，请在应用侧核实后重试。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁保护生效", "未提交该表单。");
                break;
        }
    }

    private void HandleUnknownPermissionOutcome(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, string requestId, string decision, string reason)
    {
        if (!IsCurrentBinding(source, revision, sourceGeneration, client, sessionGeneration))
        {
            return;
        }

        _interactionsView.MarkPermissionBlocked(requestId,
            "结果未知（" + reason + "）：服务端可能已应用该决定；不会自动重发。请点击“重新查询状态”核实后再决定。");
        ShowMessage(InfoBarSeverity.Error, "权限决定结果未知",
            "提交过程中" + reason + "，无法确认决定是否已生效；本页不会自动重发。");
        if (decision == OpenCodePermissionDecisions.Reject)
        {
            // 拒绝的未知结果同时保护整个会话：显示专门的整会话恢复入口。
            _sessionProtectionHint = true;
            UpdateInteractionsSection();
            HandleRejectSideEffects(sessionGeneration, client, requestId, invalidateOthers: true);
        }

        _ = RefreshMessagesAsync();
    }

    private void HandleRejectSideEffects(int generation, OpenCodeClient client, string requestId, bool invalidateOthers)
    {
        if (!IsCurrentSession(generation, client))
        {
            return;
        }

        // 拒绝会拒绝同会话其他 pending（即使结果未知也保守处理）：
        // 其他条目的消失不再单独提示；结果未知时其操作入口一并作废，等待重新查询。
        _interactionsView.MarkOtherPermissionsSettled(requestId);
        if (invalidateOthers)
        {
            _interactionsView.InvalidateOtherPermissions(requestId,
                "本会话的“拒绝”操作可能已处理本请求（即使结果未知）；不会自动重发。请点击“重新查询状态”核实后再决定。");
        }
    }

    /// <summary>
    /// 提交表单回答：先 GetFormAsync 重新查询，状态必须仍为 pending 且指纹未变化；
    /// 使用刚查询的 DTO 调用客户端；成功仅当 204；其余按过期/被拒绝/结果未知分别处理。
    /// 全流程绑定捕获的来源（引用 + Revision + 来源代际）与页面/会话代际；GET、Begin+起发与
    /// 结果应用都在捕获来源的 TryExecute 私锁内复核后执行，来源撤销先赢时零起发。
    /// </summary>
    private async Task SubmitFormAnswerAsync(OpenCodeFormInfo displayed, IReadOnlyDictionary<string, JsonElement> answer)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked)
        {
            return;
        }

        var formId = displayed.Id ?? string.Empty;
        if (formId.Length == 0
            || !string.Equals(displayed.SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(true, formId, true, "正在提交回答…");
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；未提交。");
                return;
            }

            var activeLease = lease!;

            if (activeLease.GetProtectionStatus(formId, NotificationNavigationKind.Form) is { } protection)
            {
                HandleProtectedFormAtSubmit(formId, protection, activeLease.GetSessionProtection() is not null);
                return;
            }

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetFormAsync(sessionId, formId, ct), out var freshTask))
            {
                return;
            }

            OpenCodeFormDetail fresh;
            try
            {
                fresh = await freshTask!;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeApiException ex) when (ex.StatusCode is 404 or 409)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    SettleInteraction(true, formId, "表单已过期",
                        "该表单已不存在或已处理（HTTP " + ex.StatusCode + "）；已从列表移除，未发送回答。");
                }

                return;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("表单重新查询", ex);
                }

                return;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return;
            }

            if (fresh.State?.Status != OpenCodeFormStatuses.Pending)
            {
                SettleInteraction(true, formId, "表单已结算",
                    "该表单状态已不是 pending（" + (fresh.State?.Status ?? "未知") + "）；未发送回答。");
                return;
            }

            if (!string.Equals(
                    OpenCodeInteractionFingerprint.ForForm(fresh),
                    OpenCodeInteractionFingerprint.ForForm(displayed),
                    StringComparison.Ordinal))
            {
                _interactionsView.ReplaceForm(fresh);
                ShowMessage(InfoBarSeverity.Warning, "表单已变化",
                    "服务端返回的表单字段与显示时不同；已重置卡片输入，请重新填写确认后再提交。");
                return;
            }

            // 唯一一次 mutation：action 内复核捕获上下文 → BeginMutation → 仅“起发”客户端调用；
            // 退出 source 私锁后才 await（Revoke 与起发同锁线性化）。
            Task? mutationTask = null;
            var began = false;
            var started = false;
            try
            {
                var executed = source.TryExecute(ct, () =>
                {
                    if (!IsCurrentBindingFields(source, revision, sourceGeneration, client, generation))
                    {
                        return;
                    }

                    activeLease.BeginMutation(formId, NotificationNavigationKind.Form);
                    began = true;
                    mutationTask = client.ReplyFormAsync(sessionId, fresh, answer, ct);
                    started = true;
                });

                if (!executed || !started || mutationTask is null)
                {
                    // 来源已撤销/已取消或上下文已变化：未起发 mutation，也绝不调用 CompleteMutation。
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                _interactionsView.MarkFormBlocked(formId,
                    "共享仲裁保护已生效（另一入口正在处理或结果未知）；未发送回答，请点击“重新查询状态”核实。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁保护生效", "另一入口正在处理该表单或结果未知；本页未提交。");
                return;
            }

            try
            {
                await mutationTask;
            }
            catch (OperationCanceledException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                    HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId,
                        "已取消（回答可能已送出）");
                }

                return;
            }
            catch (OpenCodeApiException ex)
            {
                if (ex.StatusCode is not (404 or 409 or 400 or 401 or 403))
                {
                    // 其他状态无法确认是否已应用：按“结果未知”处理，不自动重发。
                    if (began)
                    {
                        activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                    }

                    HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId,
                        "HTTP " + ex.StatusCode);
                    return;
                }

                // 明确结局（过期 / 被拒绝）：仅在捕获绑定仍完整一致时写入；绑定失效、来源撤销或取消
                // 一律不写 Failed/Expired，旧 lease 保持 OutcomeUnknown。
                var completed = began && TryCompleteMutation(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, ex.StatusCode is 404 or 409
                        ? NotificationInteractionStatus.Expired
                        : NotificationInteractionStatus.Failed);
                if (!completed)
                {
                    return;
                }

                if (ex.StatusCode is 404 or 409)
                {
                    SettleInteraction(true, formId, "表单已过期",
                        "该表单已不存在或已处理（HTTP " + ex.StatusCode + "）；回答未提交。");
                }
                else
                {
                    ShowMessage(InfoBarSeverity.Error, "回答被拒绝",
                        DescribeApiError(ex, "表单回答") + "可修正后重试（服务端仍是最终校验者）。");
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId, "连接中断");
                return;
            }
            catch (OpenCodeTimeoutException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId, "超时");
                return;
            }
            catch (OpenCodeProtocolException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId, "响应不符合契约");
                return;
            }

            if (!began || !TryCompleteMutation(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, NotificationInteractionStatus.Succeeded))
            {
                // 提交期间来源换代/会话切换/来源撤销：结果不归属当前代际；旧 lease 保持 OutcomeUnknown，
                // 不更新 UI、绝不自动重发。
                return;
            }

            SettleInteraction(true, formId, "表单回答已提交", "服务端已明确接受（HTTP 204）。");
            await RefreshMessagesAsync();
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "表单操作失败", "发生未预期错误；未自动重发。");
            }
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(true, formId, false, string.Empty);
            }
        }
    }

    /// <summary>
    /// 取消表单：先重新查询且状态必须仍为 pending（取消与字段内容无关，不做指纹比对），
    /// 使用刚查询的 DTO 调用 DELETE；成功仅当 204。
    /// 全流程绑定捕获的来源（引用 + Revision + 来源代际）与页面/会话代际；GET、Begin+起发与
    /// 结果应用都在捕获来源的 TryExecute 私锁内复核后执行，来源撤销先赢时零起发。
    /// </summary>
    private async Task SubmitFormCancelAsync(OpenCodeFormInfo displayed)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked)
        {
            return;
        }

        var formId = displayed.Id ?? string.Empty;
        if (formId.Length == 0
            || !string.Equals(displayed.SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(true, formId, true, "正在取消表单…");
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；未提交。");
                return;
            }

            var activeLease = lease!;

            if (activeLease.GetProtectionStatus(formId, NotificationNavigationKind.Form) is { } protection)
            {
                HandleProtectedFormAtSubmit(formId, protection, activeLease.GetSessionProtection() is not null);
                return;
            }

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetFormAsync(sessionId, formId, ct), out var freshTask))
            {
                return;
            }

            OpenCodeFormDetail fresh;
            try
            {
                fresh = await freshTask!;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeApiException ex) when (ex.StatusCode is 404 or 409)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    SettleInteraction(true, formId, "表单已过期",
                        "该表单已不存在或已处理（HTTP " + ex.StatusCode + "）；已从列表移除，未发送取消。");
                }

                return;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("表单重新查询", ex);
                }

                return;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return;
            }

            if (fresh.State?.Status != OpenCodeFormStatuses.Pending)
            {
                SettleInteraction(true, formId, "表单已结算",
                    "该表单状态已不是 pending（" + (fresh.State?.Status ?? "未知") + "）；未发送取消。");
                return;
            }

            // 唯一一次 mutation：action 内复核捕获上下文 → BeginMutation → 仅“起发”客户端调用；
            // 退出 source 私锁后才 await（Revoke 与起发同锁线性化）。
            Task? mutationTask = null;
            var began = false;
            var started = false;
            try
            {
                var executed = source.TryExecute(ct, () =>
                {
                    if (!IsCurrentBindingFields(source, revision, sourceGeneration, client, generation))
                    {
                        return;
                    }

                    activeLease.BeginMutation(formId, NotificationNavigationKind.Form);
                    began = true;
                    mutationTask = client.CancelFormAsync(sessionId, fresh, message: null, cancellationToken: ct);
                    started = true;
                });

                if (!executed || !started || mutationTask is null)
                {
                    // 来源已撤销/已取消或上下文已变化：未起发 mutation，也绝不调用 CompleteMutation。
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                _interactionsView.MarkFormBlocked(formId,
                    "共享仲裁保护已生效（另一入口正在处理或结果未知）；未发送取消，请点击“重新查询状态”核实。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁保护生效", "另一入口正在处理该表单或结果未知；本页未提交。");
                return;
            }

            try
            {
                await mutationTask;
            }
            catch (OperationCanceledException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                    HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId,
                        "已取消（取消可能已送出）");
                }

                return;
            }
            catch (OpenCodeApiException ex)
            {
                if (ex.StatusCode is not (404 or 409 or 400 or 401 or 403))
                {
                    // 其他状态无法确认是否已应用：按“结果未知”处理，不自动重发。
                    if (began)
                    {
                        activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                    }

                    HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId,
                        "HTTP " + ex.StatusCode);
                    return;
                }

                // 明确结局（过期 / 被拒绝）：仅在捕获绑定仍完整一致时写入；绑定失效、来源撤销或取消
                // 一律不写 Failed/Expired，旧 lease 保持 OutcomeUnknown。
                var completed = began && TryCompleteMutation(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, ex.StatusCode is 404 or 409
                        ? NotificationInteractionStatus.Expired
                        : NotificationInteractionStatus.Failed);
                if (!completed)
                {
                    return;
                }

                if (ex.StatusCode is 404 or 409)
                {
                    SettleInteraction(true, formId, "表单已过期",
                        "该表单已不存在或已处理（HTTP " + ex.StatusCode + "）；取消未提交。");
                }
                else
                {
                    ShowMessage(InfoBarSeverity.Error, "取消表单被拒绝", DescribeApiError(ex, "取消表单"));
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId, "连接中断");
                return;
            }
            catch (OpenCodeTimeoutException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId, "超时");
                return;
            }
            catch (OpenCodeProtocolException)
            {
                if (began)
                {
                    activeLease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
                }

                HandleUnknownFormOutcome(source, revision, sourceGeneration, client, generation, formId, "响应不符合契约");
                return;
            }

            if (!began || !TryCompleteMutation(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, NotificationInteractionStatus.Succeeded))
            {
                // 提交期间来源换代/会话切换/来源撤销：结果不归属当前代际；旧 lease 保持 OutcomeUnknown，
                // 不更新 UI、绝不自动重发。
                return;
            }

            SettleInteraction(true, formId, "表单已取消", "服务端已明确接受取消（HTTP 204）。");
            await RefreshMessagesAsync();
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "表单操作失败", "发生未预期错误；未自动重发。");
            }
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(true, formId, false, string.Empty);
            }
        }
    }

    private void HandleUnknownFormOutcome(OpenCodePageSource source, long revision, int sourceGeneration,
        OpenCodeClient client, int sessionGeneration, string formId, string reason)
    {
        if (!IsCurrentBinding(source, revision, sourceGeneration, client, sessionGeneration))
        {
            return;
        }

        _interactionsView.MarkFormBlocked(formId,
            "结果未知（" + reason + "）：服务端可能已应用该操作；不会自动重发。请点击“重新查询状态”核实后再决定。");
        ShowMessage(InfoBarSeverity.Error, "表单操作结果未知",
            "提交过程中" + reason + "，无法确认是否已生效；本页不会自动重发。");
        _ = RefreshMessagesAsync();
    }

    /// <summary>显式重新查询权限状态（阻断态恢复入口）；共享保护与指纹一并核对。
    /// 存在整会话保护时转交“整会话恢复”入口（绝不在此误清会话级保护）。</summary>
    private async Task RecheckPermissionAsync(string requestId)
    {
        if (await RecheckPermissionCoreAsync(requestId))
        {
            await BeginSessionRecoveryAsync();
        }
    }

    private async Task<bool> RecheckPermissionCoreAsync(string requestId)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked || string.IsNullOrEmpty(requestId))
        {
            return false;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(false, requestId, true, "正在重新查询…");
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return false;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；未重新查询。");
                return false;
            }

            var activeLease = lease!;

            if (activeLease.GetSessionProtection() is not null)
            {
                // 整会话保护：本入口不处理单卡，转交专门恢复（含整会话影响确认）。
                _sessionProtectionHint = true;
                UpdateInteractionsSection();
                return true;
            }

            var protection = activeLease.GetProtectionStatus(requestId, NotificationNavigationKind.Permission);
            if (protection is NotificationInteractionStatus.Duplicate or NotificationInteractionStatus.Expired)
            {
                _interactionsView.Suppress(false, requestId);
                UpdateInteractionsSection();
                ShowMessage(InfoBarSeverity.Informational, "权限请求已结算",
                    protection == NotificationInteractionStatus.Duplicate
                        ? "共享仲裁显示该请求此前已被接受（HTTP 204）；已从列表移除。"
                        : "共享仲裁显示该请求已结算或过期；已从列表移除。");
                return false;
            }

            if (protection == NotificationInteractionStatus.NeedsApp)
            {
                _interactionsView.MarkPermissionBlocked(requestId,
                    "共享仲裁容量已满（fail-closed）；未重新查询，请在应用侧处理后重试。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁容量已满", "未重新查询；请在应用侧处理后重试。");
                return false;
            }

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetPendingPermissionAsync(sessionId, requestId, ct), out var freshTask))
            {
                return false;
            }

            OpenCodePermissionRequest fresh;
            try
            {
                fresh = await freshTask!;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (OpenCodeApiException ex) when (ex.StatusCode is 404 or 409)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    SettleInteraction(false, requestId, "权限请求已过期",
                        "该权限请求已不存在或已处理（HTTP " + ex.StatusCode + "）；已从列表移除。");
                }

                return false;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("权限重新查询", ex);
                }

                return false;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return false;
            }

            if (protection == NotificationInteractionStatus.OutcomeUnknown)
            {
                // 此前结果未知且服务端仍待处理：必须由用户显式确认后才能解除保护（绝不自动重发）。
                _interactionsView.ReplacePermission(fresh);
                if (_interactionsView.ShowResolutionConfirm(false, requestId,
                    "该权限请求仍待处理，但此前提交结果未知（可能已生效）。重新决定可能导致重复生效；" +
                    "请选择“仍要重新决定”或“视为已结算”。"))
                {
                    ShowMessage(InfoBarSeverity.Warning, "需要显式确认",
                        "该请求仍待处理且此前结果未知；本页不会自动重发，请在卡片内确认后再决定。");
                }
                else
                {
                    ShowMessage(InfoBarSeverity.Warning, "需要显式确认",
                        "该请求仍待处理且此前结果未知；卡片已不可用，请刷新后重试（本页不会自动重发）。");
                }

                return false;
            }

            var changed = _interactionsView.ApplyRecheckedPermission(fresh);
            ShowMessage(InfoBarSeverity.Informational, "重新查询完成",
                changed
                    ? "该权限请求仍待处理，但范围已变化；已刷新卡片，请核对后重新决定。"
                    : "该权限请求仍待处理；已重新核实，请核对后重新决定（本页不会自动重发）。");
            _ = RefreshMessagesAsync();
            return false;
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "重新查询失败", "发生未预期错误。");
            }

            return false;
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(false, requestId, false, string.Empty);
            }
        }
    }

    /// <summary>显式重新查询表单状态（阻断态恢复入口）；共享保护与指纹一并核对。
    /// 存在整会话保护时转交“整会话恢复”入口（绝不在此误清会话级保护）。</summary>
    private async Task RecheckFormAsync(string formId)
    {
        if (await RecheckFormCoreAsync(formId))
        {
            await BeginSessionRecoveryAsync();
        }
    }

    private async Task<bool> RecheckFormCoreAsync(string formId)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked || string.IsNullOrEmpty(formId))
        {
            return false;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(true, formId, true, "正在重新查询…");
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return false;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；未重新查询。");
                return false;
            }

            var activeLease = lease!;

            if (activeLease.GetSessionProtection() is not null)
            {
                // 整会话保护：本入口不处理单卡，转交专门恢复（含整会话影响确认）。
                _sessionProtectionHint = true;
                UpdateInteractionsSection();
                return true;
            }

            var protection = activeLease.GetProtectionStatus(formId, NotificationNavigationKind.Form);
            if (protection is NotificationInteractionStatus.Duplicate or NotificationInteractionStatus.Expired)
            {
                _interactionsView.Suppress(true, formId);
                UpdateInteractionsSection();
                ShowMessage(InfoBarSeverity.Informational, "表单已结算",
                    protection == NotificationInteractionStatus.Duplicate
                        ? "共享仲裁显示该表单此前已被接受（HTTP 204）；已从列表移除。"
                        : "共享仲裁显示该表单已结算或过期；已从列表移除。");
                return false;
            }

            if (protection == NotificationInteractionStatus.NeedsApp)
            {
                _interactionsView.MarkFormBlocked(formId,
                    "共享仲裁容量已满（fail-closed）；未重新查询，请在应用侧处理后重试。");
                ShowMessage(InfoBarSeverity.Warning, "共享仲裁容量已满", "未重新查询；请在应用侧处理后重试。");
                return false;
            }

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetFormAsync(sessionId, formId, ct), out var freshTask))
            {
                return false;
            }

            OpenCodeFormDetail fresh;
            try
            {
                fresh = await freshTask!;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (OpenCodeApiException ex) when (ex.StatusCode is 404 or 409)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    SettleInteraction(true, formId, "表单已过期",
                        "该表单已不存在或已处理（HTTP " + ex.StatusCode + "）；已从列表移除。");
                }

                return false;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("表单重新查询", ex);
                }

                return false;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return false;
            }

            if (fresh.State?.Status != OpenCodeFormStatuses.Pending)
            {
                SettleInteraction(true, formId, "表单已结算",
                    "该表单状态已不是 pending（" + (fresh.State?.Status ?? "未知") + "）；已从列表移除。");
                return false;
            }

            if (protection == NotificationInteractionStatus.OutcomeUnknown)
            {
                // 此前结果未知且服务端仍 pending：必须由用户显式确认后才能解除保护（绝不自动重发）。
                _interactionsView.ReplaceForm(fresh);
                if (_interactionsView.ShowResolutionConfirm(true, formId,
                    "该表单仍待处理，但此前提交结果未知（可能已生效）。重新提交可能导致重复生效；" +
                    "请选择“仍要重新决定”或“视为已结算”。"))
                {
                    ShowMessage(InfoBarSeverity.Warning, "需要显式确认",
                        "该表单仍待处理且此前结果未知；本页不会自动重发，请在卡片内确认后再处理。");
                }
                else
                {
                    ShowMessage(InfoBarSeverity.Warning, "需要显式确认",
                        "该表单仍待处理且此前结果未知；卡片已不可用，请刷新后重试（本页不会自动重发）。");
                }

                return false;
            }

            var changed = _interactionsView.ApplyRecheckedForm(fresh);
            ShowMessage(InfoBarSeverity.Informational, "重新查询完成",
                changed
                    ? "该表单仍待处理，但内容已变化；已重置输入，请重新填写确认后再提交。"
                    : "该表单仍待处理；已重新核实，可继续填写或提交。");
            _ = RefreshMessagesAsync();
            return false;
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "重新查询失败", "发生未预期错误。");
            }

            return false;
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(true, formId, false, string.Empty);
            }
        }
    }

    /// <summary>
    /// 阻断态“必要确认”的执行：用户显式选择“仍要重新决定”(retry=true) 或“视为已结算”(retry=false)。
    /// 先在共享租约内做整会话权威重读；无整会话保护时按精确来源键解除单条未知（affectsSession:false，
    /// 绝不误清会话级保护）；存在整会话保护（拒绝结果未知）时转交“整会话恢复”入口。
    /// 绝不自动重发，绝不自动批准。
    /// </summary>
    private async Task ResolveInteractionAsync(bool isForm, string id, bool retry)
    {
        if (await ResolveInteractionCoreAsync(isForm, id, retry))
        {
            await BeginSessionRecoveryAsync();
        }
    }

    private async Task<bool> ResolveInteractionCoreAsync(bool isForm, string id, bool retry)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked || string.IsNullOrEmpty(id))
        {
            return false;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        var kind = isForm ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(isForm, id, true, retry ? "正在解除保护…" : "正在标记为已结算…");
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return false;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；保护未解除。");
                return false;
            }

            var activeLease = lease!;

            if (activeLease.GetSessionProtection() is not null)
            {
                // 整会话保护存在：转交专门恢复（其自带“整会话影响”确认），绝不在此误清会话级保护。
                _sessionProtectionHint = true;
                UpdateInteractionsSection();
                return true;
            }

            // 整会话权威重读：确认目标是否仍待处理（不依赖单条 id 的既有假设）。
            OpenCodePermissionRequest? freshPermission = null;
            OpenCodeFormDetail? freshForm = null;
            try
            {
                if (isForm)
                {
                    if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                            () => client.GetPendingFormsAsync(sessionId, ct), out var formsTask))
                    {
                        return false;
                    }

                    var forms = await formsTask!;
                    if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
                    {
                        return false;
                    }

                    foreach (var form in forms)
                    {
                        if (string.Equals(form?.Id, id, StringComparison.Ordinal))
                        {
                            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                                    () => client.GetFormAsync(sessionId, id, ct), out var detailTask))
                            {
                                return false;
                            }

                            freshForm = await detailTask!;
                            break;
                        }
                    }
                }
                else
                {
                    if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                            () => client.GetPendingPermissionsAsync(sessionId, ct), out var permissionsTask))
                    {
                        return false;
                    }

                    var permissions = await permissionsTask!;
                    foreach (var permission in permissions)
                    {
                        if (string.Equals(permission?.Id, id, StringComparison.Ordinal))
                        {
                            freshPermission = permission;
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (OpenCodeApiException ex) when (ex.StatusCode is 404 or 409)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    SettleInteraction(isForm, id, isForm ? "表单已过期" : "权限请求已过期",
                        "该请求已不存在或已处理（HTTP " + ex.StatusCode + "）；已从列表移除。");
                }

                return false;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure(isForm ? "表单整会话重新查询" : "权限整会话重新查询", ex);
                }

                return false;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return false;
            }

            if (freshForm is not null && freshForm.State?.Status != OpenCodeFormStatuses.Pending)
            {
                // 表单已不是 pending：按“不再待处理”处理（其保护记录若存在，走下方精确结算）。
                freshForm = null;
            }

            var protection = activeLease.GetProtectionStatus(id, kind);

            if (freshPermission is null && freshForm is null)
            {
                // 目标已不再待处理：绝不声称保护“自动消失”。仅在确有单条未知记录且用户已显式确认时，
                // 经来源 TryExecute 按精确来源键记录为“已结算”（allowRetry:false），否则仅移除卡片。
                if (protection == NotificationInteractionStatus.OutcomeUnknown)
                {
                    if (!TryResolveUnknown(source, revision, sourceGeneration, client, generation, ct,
                            activeLease, id, kind, allowRetry: false))
                    {
                        return false;
                    }

                    if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
                    {
                        return false;
                    }

                    _interactionsView.Suppress(isForm, id);
                    UpdateInteractionsSection();
                    ShowMessage(InfoBarSeverity.Informational, "请求已不再待处理",
                        "整会话重新查询显示该请求已不在待处理列表；已按“已结算”记录（不会自动重发）。");
                    return false;
                }

                if (protection is NotificationInteractionStatus.Duplicate or NotificationInteractionStatus.Expired)
                {
                    _interactionsView.Suppress(isForm, id);
                    UpdateInteractionsSection();
                    ShowMessage(InfoBarSeverity.Informational, "请求已结算",
                        "共享仲裁显示该请求已结算或已被接受；已从列表移除。");
                    return false;
                }

                SettleInteraction(isForm, id, isForm ? "表单已结算" : "权限请求已结算",
                    "整会话重新查询显示该请求已不再待处理；已从列表移除（未写入任何保护记录）。");
                return false;
            }

            if (protection is NotificationInteractionStatus.Duplicate or NotificationInteractionStatus.Expired)
            {
                _interactionsView.Suppress(isForm, id);
                UpdateInteractionsSection();
                ShowMessage(InfoBarSeverity.Informational, "请求已结算",
                    "共享仲裁显示该请求已结算或已被接受；已从列表移除。");
                return false;
            }

            if (protection == NotificationInteractionStatus.OutcomeUnknown)
            {
                // 单条未知：整会话已核对且用户已显式确认；按精确来源键解除（affectsSession:false）。
                if (!TryResolveUnknown(source, revision, sourceGeneration, client, generation, ct,
                        activeLease, id, kind, allowRetry: retry))
                {
                    return false;
                }

                if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
                {
                    return false;
                }

                if (retry)
                {
                    if (isForm && freshForm is not null)
                    {
                        _interactionsView.ApplyRecheckedForm(freshForm);
                    }
                    else if (!isForm && freshPermission is not null)
                    {
                        _interactionsView.ApplyRecheckedPermission(freshPermission);
                    }

                    ShowMessage(InfoBarSeverity.Warning, "已解除保护",
                        "该请求仍待处理，请核对后重新决定（可能重复生效）。本页不会自动重发。");
                }
                else
                {
                    _interactionsView.Suppress(isForm, id);
                    UpdateInteractionsSection();
                    ShowMessage(InfoBarSeverity.Informational, "已视为结算",
                        "该请求已按已结算处理，不再提交；若服务端仍返回，请以重新查询结果为准。");
                }

                _ = RefreshMessagesAsync();
                return false;
            }

            // 无共享未知记录：纯本地阻断态恢复；用户选择“视为已结算”时仅移除卡片，不写保护记录。
            if (retry)
            {
                if (isForm && freshForm is not null)
                {
                    _interactionsView.ApplyRecheckedForm(freshForm);
                }
                else if (!isForm && freshPermission is not null)
                {
                    _interactionsView.ApplyRecheckedPermission(freshPermission);
                }

                ShowMessage(InfoBarSeverity.Informational, "重新查询完成",
                    "该请求仍待处理；已重新核实，可继续处理（本页不会自动重发）。");
            }
            else
            {
                _interactionsView.Suppress(isForm, id);
                UpdateInteractionsSection();
                ShowMessage(InfoBarSeverity.Informational, "已从列表移除",
                    "该请求仍待处理但已从本页移除；未写入任何共享保护记录。");
            }

            return false;
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "解除保护失败", "发生未预期错误；保护状态未被解除。");
            }

            return false;
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(isForm, id, false, string.Empty);
            }
        }
    }

    // ------------------------------------------------------------------
    // 整会话恢复（显式用户入口；拒绝结果未知的专用闭环）
    // ------------------------------------------------------------------

    /// <summary>
    /// 整会话恢复（第一步，显式用户入口；入口在会话有效时常驻显示，不依赖 pending 或提示）：
    /// 在共享租约内读取整会话保护来源（GetSessionProtection），再读取完整权威 pending 权限集合
    /// （列表端点无分页游标 → 单次 GET 即完整集合，不猜测分页），最后展示“整会话影响”确认并保存
    /// 绑定快照（来源引用 + Revision + 来源代际 + 会话 + 会话代际 + 精确来源键）。
    /// 不发送任何 mutation；没有保护时如实提示；每 await 后复核绑定与取消。
    /// </summary>
    private async Task BeginSessionRecoveryAsync()
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked)
        {
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(false, string.Empty, true, "正在核对整会话状态…");
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；未执行任何解除。");
                return;
            }

            var activeLease = lease!;

            var origin = activeLease.GetSessionProtection();
            if (origin is null)
            {
                _sessionProtectionHint = false;
                _recoveryOrigin = null;
                _sessionRecoveryPanel.HideConfirm();
                UpdateInteractionsSection();
                ShowMessage(InfoBarSeverity.Informational, "无需整会话恢复",
                    "当前没有“拒绝结果未知”的整会话保护；如单张卡片阻断，请使用卡片上的“重新查询状态”。");
                return;
            }

            var originValue = origin.Value;
            _sessionProtectionHint = true;

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetPendingPermissionsAsync(sessionId, ct), out var permissionsTask))
            {
                return;
            }

            IReadOnlyList<OpenCodePermissionRequest> permissions;
            try
            {
                permissions = await permissionsTask!;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("整会话权限核对", ex);
                }

                return;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return;
            }

            var originStillPending = ContainsRequest(permissions, originValue.RequestId);
            // 确认快照：绑定（来源引用 + Revision + 来源代际 + 会话 + 会话代际）与精确来源键；
            // 第二步执行前逐项复核，任何一项变化都必须重新核对并再次确认。
            _recoveryOrigin = new SessionRecoveryOrigin(source, revision, sourceGeneration, sessionId, generation,
                originValue.RequestId, originValue.Kind);
            _sessionRecoveryPanel.ShowConfirm(
                originStillPending
                    ? "核对结果：此前“拒绝”的原始请求仍待处理，提交可能已生效；解除本会话保护后可重新决定（可能重复生效）。本页不会自动重发。"
                    : "核对结果：此前“拒绝”的原始请求已不再待处理（可能已处理）。解除本会话保护后将按“已结算”记录，不可重试。",
                allowRetry: originStillPending);
            UpdateInteractionsSection();
            ShowMessage(InfoBarSeverity.Informational, "请确认整会话影响",
                "该操作影响整个会话的共享保护，请在确认区选择后继续（不会自动重发）。");
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "整会话核对失败", "发生未预期错误；未执行任何解除。");
            }
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(false, string.Empty, false, string.Empty);
            }
        }
    }

    /// <summary>
    /// 整会话恢复（第二步，用户显式确认后）：在同一租约内重新读取保护来源与完整权威 pending 集合；
    /// 仅当确认快照（完整绑定 + 精确来源键）与实时状态逐项一致时，经捕获来源的 TryExecute 调用
    /// ResolveSessionUnknown（精确来源键 + Permission + 仍为未知的校验由 gate 执行）；任何不一致都不执行
    /// 解除、重新核对并再次确认（旧确认绝不复活）。allowRetry 仅在用户显式接受“可能重复生效”
    /// 且原请求仍待处理时为 true；来源已消失一律 allowRetry:false（已结算、不可重试）。
    /// </summary>
    private async Task CompleteSessionRecoveryAsync(bool allowRetry)
    {
        var source = _source;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (source is null || client is null || sessionId is null || !_pageActive || _sessionMissing
            || _interactionBusy || source.IsRevoked)
        {
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        var epoch = _mutationEpoch;
        var snapshot = _recoveryOrigin;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        _interactionBusy = true;
        SetInteractionBusy(false, string.Empty, true, "正在解除整会话保护…");
        OpenCodeInteractionGate.Lease? lease = null;
        var requeueConfirm = false;
        try
        {
            var gate = await EnterGateAsync(source, revision, sourceGeneration, client, sessionId, generation, ct);
            if (gate.Cancelled)
            {
                return;
            }

            lease = gate.Lease;
            if (!gate.Ok || lease is null)
            {
                ShowMessage(InfoBarSeverity.Error, "共享仲裁不可用", gate.Error ?? "无法进入共享交互仲裁；保护未解除。");
                return;
            }

            var activeLease = lease!;

            var origin = activeLease.GetSessionProtection();
            if (origin is null)
            {
                _sessionProtectionHint = false;
                _recoveryOrigin = null;
                _sessionRecoveryPanel.HideConfirm();
                UpdateInteractionsSection();
                ShowMessage(InfoBarSeverity.Informational, "保护已解除",
                    "整会话保护已不存在（可能已由其他入口处理）；未执行任何写入。");
                return;
            }

            var originValue = origin.Value;

            // 第二阶段必须同时匹配：确认快照的完整绑定（来源引用 + Revision + 来源代际 + 会话 + 会话代际）
            // 与实时精确来源键（RequestId + Kind）。任一不符（含会话已切换、来源已换代、快照已被清除）
            // 都不执行解除、重新核对并再次确认——旧确认绝不复活。
            if (snapshot is not { } expected
                || !ReferenceEquals(expected.Source, source)
                || expected.Revision != revision
                || expected.SourceGeneration != sourceGeneration
                || !string.Equals(expected.SessionId, sessionId, StringComparison.Ordinal)
                || expected.SessionGeneration != generation
                || !string.Equals(expected.RequestId, originValue.RequestId, StringComparison.Ordinal)
                || expected.Kind != originValue.Kind)
            {
                requeueConfirm = true;
                return;
            }

            if (!TryStartRead(source, revision, sourceGeneration, client, generation, ct,
                    () => client.GetPendingPermissionsAsync(sessionId, ct), out var permissionsTask))
            {
                return;
            }

            IReadOnlyList<OpenCodePermissionRequest> permissions;
            try
            {
                permissions = await permissionsTask!;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeException ex)
            {
                if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
                {
                    ShowQueryFailure("整会话权限核对", ex);
                }

                return;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return;
            }

            var originStillPending = ContainsRequest(permissions, originValue.RequestId);
            var effectiveRetry = allowRetry && originStillPending;

            if (!TryResolveSessionUnknown(source, revision, sourceGeneration, client, generation, ct,
                    activeLease, originValue.RequestId, originValue.Kind, effectiveRetry))
            {
                // 保护状态已变化：不写入；重新核对。
                requeueConfirm = true;
                return;
            }

            if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
            {
                return;
            }

            _sessionProtectionHint = false;
            _recoveryOrigin = null;
            _sessionRecoveryPanel.HideConfirm();
            _interactionsView.Suppress(false, originValue.RequestId);
            UpdateInteractionsSection();
            ShowMessage(InfoBarSeverity.Informational,
                effectiveRetry ? "已解除整会话保护" : "已按已结算处理",
                effectiveRetry
                    ? "本会话保护已解除；原“拒绝”请求仍待处理，请核对后重新决定（可能重复生效）。本页不会自动重发。"
                    : "本会话保护已解除，并按“已结算”记录（不可重试、不会自动重发）。");
            _ = RefreshMessagesAsync();
        }
        catch (Exception)
        {
            if (IsCurrentBinding(source, revision, sourceGeneration, client, generation))
            {
                ShowMessage(InfoBarSeverity.Error, "解除整会话保护失败", "发生未预期错误；保护状态未被解除。");
            }
        }
        finally
        {
            lease?.Dispose();
            if (epoch == _mutationEpoch)
            {
                _interactionBusy = false;
                SetInteractionBusy(false, string.Empty, false, string.Empty);
            }
        }

        if (requeueConfirm)
        {
            ShowMessage(InfoBarSeverity.Warning, "保护状态已变化",
                "整会话保护来源或确认绑定与确认时不同；未执行解除，请重新核对后再确认。");
            await BeginSessionRecoveryAsync();
        }
    }

    // ------------------------------------------------------------------
    // 输入框：Enter 发送 / Shift+Enter 换行 / IME 组合不误发
    // ------------------------------------------------------------------

    private void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift)
        {
            _shiftDown = true;
            return;
        }

        // 兜底：若 PreviewKeyDown 路径未接管（框架差异），仍在 KeyDown 阶段执行同一判定。
        TryHandleComposerEnter(e);
    }

    /// <summary>
    /// 隧道阶段拦截（先于原生 TextBox 的 AcceptsReturn 换行处理）：Enter 发送不会被原生换行抢先。
    /// </summary>
    private void Composer_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        TryHandleComposerEnter(e);
    }

    /// <summary>
    /// Enter 发送统一判定（PreviewKeyDown 主路径与 KeyDown 兜底共用；重复触发幂等，不会重复提交）：
    /// Shift+Enter 换行、IME 组合中的候选确认、组合刚结束的确认键一律不发送且不置 Handled。
    /// </summary>
    private void TryHandleComposerEnter(KeyRoutedEventArgs e)
    {
        if (!_pageActive || _composerView is null || e.Handled) return;
        var decision = _composerView.HandleEditorKey(e, IsShiftDown(), _isComposing,
            DateTimeOffset.UtcNow - _lastCompositionEndedAt, _enterDown, _isSending || _composerPickerPending);
        if (decision.LatchEnter) _enterDown = true;
        if (decision.Action == OpenCodeComposerKeyAction.Submit) RequestComposerSubmission();
    }

    /// <summary>
    /// 焦点离开：组合与修饰键状态不可能继续成立，清除跟踪值，避免事件缺失导致的粘滞误判。
    /// </summary>
    private void Composer_LostFocus(object sender, RoutedEventArgs e)
    {
        _isComposing = false;
        _shiftDown = false;
        _enterDown = false;
    }

    private void Composer_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Shift)
        {
            _shiftDown = false;
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            _enterDown = false;
        }
    }

    private void Composer_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _isComposing = true;
    }

    private void Composer_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _isComposing = false;
        _lastCompositionEndedAt = DateTimeOffset.UtcNow;
    }

    private bool IsShiftDown()
    {
        try
        {
            // 物理状态优先（元数据已核实存在：Microsoft.InteractiveExperiences 投影 / Microsoft.UI.winmd）。
            var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
            return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }
        catch
        {
            // 极端情况下 API 不可用：回退到 KeyDown/KeyUp 跟踪值。
            return _shiftDown;
        }
    }

    private void Composer_TextChanged(object sender, TextChangedEventArgs e)
    {
        _composerView?.NotifyEditorTextChanged();
        // 草稿变化直接决定单按钮模式（发送 / 停止 / 禁用）。
        UpdateComposerButtons();
    }

    // ------------------------------------------------------------------
    // 滚动
    // ------------------------------------------------------------------

    private bool IsScrolledToBottom()
    {
        return MessageScroll.ScrollableHeight - MessageScroll.VerticalOffset <= ScrollBottomThreshold;
    }

    /// <summary>
    /// 滚动到消息底部：捕获滚动原点（页面/会话代际 + 选中会话）后经有界排队执行；
    /// 排队回调晚于会话切换/页面卸载到达时按原点复核，绝不把旧滚动作用到新会话或已卸载页面。
    /// </summary>
    private void ScrollToBottom(bool animated)
    {
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        var sessionId = _selectedSessionId;
        TryScrollToBottom(pageGeneration, sessionGeneration, sessionId, animated, pass: 0);
    }

    /// <summary>
    /// 有界滚动（绝不使用计时器/无限循环）：同步完成挂起布局后读取真实 ScrollableHeight 作为目标
    /// 偏移（不依赖 double.MaxValue 的钳制行为）；随后至多复核 <see cref="MaxScrollSettlePasses"/> 次，
    /// 吸收迟到的异步布局增高（富文本/工具部件在后续帧才完成测量）。
    /// </summary>
    private void TryScrollToBottom(int pageGeneration, int sessionGeneration, string? sessionId, bool animated, int pass)
    {
        if (!IsScrollContextCurrent(pageGeneration, sessionGeneration, sessionId))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            // 排队期间可能已切换会话/卸载页面：回调内再次复核原点。
            if (!IsScrollContextCurrent(pageGeneration, sessionGeneration, sessionId))
            {
                return;
            }

            MessageScroll.UpdateLayout();
            var target = MessageScroll.ScrollableHeight;
            MessageScroll.ChangeView(null, target, null, !animated);

            if (pass >= MaxScrollSettlePasses)
            {
                return;
            }

            // 下一帧复核：可滚动范围仍在增长（迟到布局）才补滚一次，否则结束。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsScrollContextCurrent(pageGeneration, sessionGeneration, sessionId))
                {
                    return;
                }

                if (MessageScroll.ScrollableHeight > target + 0.5)
                {
                    TryScrollToBottom(pageGeneration, sessionGeneration, sessionId, animated, pass + 1);
                }
            });
        });
    }

    /// <summary>滚动原点是否仍有效：页面存活、页面/会话代际未变、且仍是同一选中会话。</summary>
    private bool IsScrollContextCurrent(int pageGeneration, int sessionGeneration, string? sessionId)
    {
        return _pageActive
            && pageGeneration == _pageGeneration
            && sessionGeneration == _sessionGeneration
            && string.Equals(sessionId, _selectedSessionId, StringComparison.Ordinal);
    }

    private void MessageScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateScrollToBottomButton();
    }

    private void UpdateScrollToBottomButton()
    {
        var hasContent = _conversationView.Children.Count > 0;
        var atBottom = IsScrolledToBottom();
        ScrollToBottomButton.Visibility = hasContent && !atBottom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollToBottom(animated: false);
    }

    // ------------------------------------------------------------------
    // 状态与提示
    // ------------------------------------------------------------------

    private void HandleApiException(OpenCodeApiException ex, string operation)
    {
        // HTTP 错误响应说明服务可达：只陈述“请求被拒绝”，不改写为连接失败。
        SetConnectionState(ConnectionState.Connected);
        if (ex.StatusCode == 404)
        {
            InvalidateViewAcknowledgement();
            _sessionMissing = true;
            BindComposer();
            ClearAgentSelector();
            UpdateEmptyState();
            UpdateComposerButtons();
            UpdateInteractionsSection();
        }

        ShowMessage(InfoBarSeverity.Error, operation + "被拒绝", DescribeApiError(ex, operation));
    }

    private static string DescribeApiError(OpenCodeApiException ex, string operation)
    {
        var hint = ex.StatusCode switch
        {
            400 => "请求未通过服务端校验",
            401 => "未授权（服务要求有效的部署凭据，或凭据无效）",
            404 => "会话不存在（可能已在服务端被删除）",
            409 => "冲突（会话可能正忙）",
            _ => "服务端拒绝",
        };
        return operation + "请求返回 HTTP " + ex.StatusCode + "（" + hint + "）。";
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        if (IsRuntimeNoticeDismissed(title, message)) return;
        MessageBar.ActionButton = null; // 撤回“复制内容”等一次性操作按钮不跨消息残留。
        MessageBar.Severity = severity;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    private void ShowNotConfigured(string title, string detail)
    {
        NotConfiguredTitle.Text = title;
        NotConfiguredDetail.Text = detail;
        NotConfiguredPanel.Visibility = _draftRecoveryPending ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceGrid.Visibility = _draftRecoveryPending ? Visibility.Visible : Visibility.Collapsed;
        if (_draftRecoveryPending)
        {
            _homeActive = false; _newSessionDraftActive = false;
            VisualStateManager.GoToState(this, "NewSessionLayout", false);
            SetBlankComposerPlacement(true); // Existing editor, readonly/copyable; no second recovery surface.
        }
        SetConnectionState(ConnectionState.NotConfigured);
        UpdateComposerButtons();
    }

    private void ShowWorkspace()
    {
        NotConfiguredPanel.Visibility = Visibility.Collapsed;
        WorkspaceGrid.Visibility = Visibility.Visible;
        if (_connection == ConnectionState.NotConfigured)
        {
            SetConnectionState(ConnectionState.Unknown);
        }

        UpdateComposerButtons();
    }

    private void SetConnectionState(ConnectionState state)
    {
        if (_connection == state)
        {
            return;
        }

        _connection = state;
        UpdateConnectionStatus();
    }

    private void UpdateConnectionStatus()
    {
        RefreshWslQuickAccess();
        string text;
        string brushKey;
        string tooltip;

        switch (_connection)
        {
            case ConnectionState.NotConfigured:
                text = "尚未就绪";
                brushKey = "TextFillColorTertiaryBrush";
                tooltip = "等待应用提供 OpenCode 连接（不会使用匿名回退）";
                break;
            case ConnectionState.Connected:
                text = "已连接";
                brushKey = "SystemFillColorSuccessBrush";
                tooltip = "最近一次请求已收到服务响应（不代表执行完成）";
                break;
            case ConnectionState.Failed:
                text = "连接失败";
                brushKey = "SystemFillColorCriticalBrush";
                tooltip = "最近一次请求失败；详情见提示条";
                break;
            default:
                text = "尚未连接";
                brushKey = "TextFillColorTertiaryBrush";
                tooltip = "已配置，但尚无收到服务响应的请求";
                break;
        }

        ConnectionStatusText.Text = text;
        // ThemeResource 样式（XamlReader 构造、按键缓存）：主题切换时由框架重取，不是一次性画刷。
        if (OpenCodeInsightsTheme.Dot(brushKey) is { } dotStyle)
        {
            ConnectionDot.Style = dotStyle;
        }

        _connectionStatusDetail = tooltip;
        ToolTipService.SetToolTip(ConnectionStatusButton, tooltip + "。点按查看服务状态、MCP、插件与 LSP");
        AutomationProperties.SetName(ConnectionStatusButton, "连接状态：" + text);
        RenderConnectionFlyoutIfOpen();
    }

    private void UpdateComposerButtons()
    {
        if (_composerController is null) return;
        var hasClient = _client is not null;
        // The blank "新会话" tab has no session yet; its Send creates one first (no Stop: never Working).
        var hasSession = _pageActive && hasClient && (_selectedSessionId is not null || _newSessionDraftActive) && !_sessionMissing;
        var snapshot = GetComposerSnapshot();
        var isWorking = hasSession && _selectedSessionId is { } selected && IsComposerSessionWorking(selected);

        // 权限遮罩盖住输入区时输入框仍留在可视树中（保留草稿/输入法状态），仅禁用（重设计 v3 §5）。
        Composer.IsEnabled = _pageActive && (_draftRecoveryPending || hasClient && !ComposerCoveredByOverlay);

        // 单按钮禁用矩阵：有草稿 → 发送（发送中禁用防重复提交）；无草稿且当前选中会话
        // 处于本页已观察到的 Working → 停止；idle / Unknown / 无会话 / 会话缺失一律禁用。
        var route = OpenCodeComposerInputPolicy.Route(snapshot.SelectedCommand is not null, snapshot.Attachments.Count, snapshot.Text);
        var buttons = OpenCodeComposerInputPolicy.Buttons(hasSession && snapshot.IsActive, snapshot.HasDraft,
            isWorking, _isSending, snapshot.IsBusy, route, snapshot.CanSubmit);
        SendStopButton.IsEnabled = (buttons.CanSend || buttons.CanStop) && NativeSendStopDisplayAllowed()
            && !ComposerCoveredByOverlay;
        SendProgress.IsActive = buttons.ShowProgress;
        SetSendStopMode(OpenCodeComposerSendPresentation.Map(buttons, isWorking), snapshot.HasDraft);
        UpdateComposerSecondaryActions(isWorking, buttons.CanSend && SendStopButton.IsEnabled, snapshot);
    }

    private void UpdateEmptyState()
    {
        RefreshRightPanelMetadata();
        RefreshWslQuickAccess();
        string? text = null;
        if (_selectedSessionId is null)
        {
            text = "请选择或新建会话。";
        }
        else if (_sessionMissing)
        {
            text = "该会话在服务端不存在（HTTP 404）；请从左侧选择其他会话。";
        }
        else if (_conversationView.Children.Count == 0)
        {
            text = _messagesLoadError ?? (_firstMessagesLoading ? "正在加载消息…"
                : _messagesResponseEmpty ? "该会话暂无消息。"
                : "当前没有可显示的消息，历史状态将随刷新更新。");
        }

        EmptyStateText.Text = text ?? string.Empty;
        EmptyStateText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateScrollToBottomButton();
    }

    private void UpdateWaitingText()
    {
        RefreshSessionStateUi();
        UpdateQueuedPrompts();
        if (_pendingAckIds.Count > 0)
        {
            WaitingText.Text = "已接受输入，等待投递到会话时间线（" + _pendingAckIds.Count +
                " 项）；投递与执行结果以消息时间线为准。";
            WaitingText.Visibility = Visibility.Visible;
        }
        else if (_deliveredNoticeShown)
        {
            WaitingText.Text = "输入已投递到会话时间线（投递不代表执行完成；结果以消息为准）。";
            WaitingText.Visibility = Visibility.Visible;
        }
        else
        {
            WaitingText.Text = string.Empty;
            WaitingText.Visibility = Visibility.Collapsed;
        }
    }

    private void RaiseSessionTracked(string sessionId, OpenCodeSessionTrackReason reason)
    {
        if (!_pageActive || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        var handler = SessionTracked;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new OpenCodeSessionTrackedEventArgs(sessionId, reason));
        }
        catch
        {
            // 订阅者异常不影响本页（宿主负责其自身异常处理）。
        }
    }

    /// <summary>输入已接纳并排程：回合进入 Working（绝非完成）。</summary>
    private void MarkWorking(string sessionId)
    {
        if (!_pageActive || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        _workingSessions.Add(sessionId);
        RaiseExecutionState(new OpenCodeExecutionStateChangedEventArgs(sessionId, OpenCodeExecutionState.Working, null));
    }

    /// <summary>
    /// 从消息投影读取回合级终态（仅 idle）：同一会话首次观察到的 idle 仅作基线（不报），
    /// 其后出现的新 idle id 且 outcome 已核实才报告终态；绝不把 prompt 回执当完成。
    /// </summary>
    private void ObserveExecutionState(string sessionId, IReadOnlyList<OpenCodeMessage> orderedMessages)
    {
        if (!_pageActive || string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        var idle = OpenCodeExecutionStateReader.LatestIdle(orderedMessages);
        if (_historyExecutionRebaseline && sessionId == _selectedSessionId)
        {
            // Reset history is not a new terminal event. Preserve newly published Working until
            // the accepted user message is actually witnessed in authoritative server order.
            // Exact message identity only: no text/timestamp/lexical-ID correlation and no claim
            // that a later round-level idle is a per-prompt success response.
            var promptIndex = -1;
            var idleIndex = -1;
            for (var i = 0; i < orderedMessages.Count; i++)
            {
                if (_historyAcceptedPromptId is { } prompt && orderedMessages[i].Type == OpenCodeMessageTypes.User
                    && orderedMessages[i].Id == prompt) promptIndex = i;
                if (ReferenceEquals(orderedMessages[i], idle)) idleIndex = i;
            }
            if (!_workingSessions.Contains(sessionId) || promptIndex < 0 || idleIndex < promptIndex)
            {
                if (idle?.Id is { Length: > 0 } historical) _lastIdleBySession[sessionId] = historical;
                if (!_workingSessions.Contains(sessionId) || promptIndex >= 0)
                {
                    _historyExecutionRebaseline = false;
                    _historyAcceptedPromptId = null;
                }
                return;
            }
            // The first fresh window can already include the accepted user and its following
            // completed round. Do not discard that genuine terminal merely to seed a baseline.
            _historyExecutionRebaseline = false;
            _historyAcceptedPromptId = null;
            _lastIdleBySession.Remove(sessionId);
        }
        if (idle is null || idle.Id is not { Length: > 0 } idleId)
        {
            return;
        }

        _lastIdleBySession.TryGetValue(sessionId, out var previousIdleId);
        if (string.Equals(previousIdleId, idleId, StringComparison.Ordinal))
        {
            return;
        }

        var wasWorking = _workingSessions.Remove(sessionId);
        // Polling can observe the terminal after the send/interrupt handler's finally has
        // already rendered Stop. Recompute from the cleared flag, without settling any
        // submission protection or treating a receipt as completion.
        if (wasWorking && sessionId == _selectedSessionId) UpdateComposerButtons();
        var hadBaseline = previousIdleId is not null;
        _lastIdleBySession[sessionId] = idleId;

        var mapped = OpenCodeExecutionStateReader.MapOutcome(idle.Outcome);
        if (mapped is null || (!wasWorking && !hadBaseline))
        {
            // 未核实 outcome，或本页在该会话首次观察到的 idle（可能是历史终态）：不猜测、不报告。
            return;
        }

        RaiseExecutionState(new OpenCodeExecutionStateChangedEventArgs(sessionId, mapped.Value, idleId));
    }

    private void RaiseExecutionState(OpenCodeExecutionStateChangedEventArgs args)
    {
        var handler = ExecutionStateChanged;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch
        {
            // 订阅者异常不影响本页。
        }
    }

    // ------------------------------------------------------------------
    // 通知导航整合（App 来源 + 页面公开导航入口；入口只定位、绝不授权）
    // ------------------------------------------------------------------

    /// <summary>
    /// 兼容旧宿主的来源身份<strong>导航提示</strong>（由 MainWindow 注入的不透明代际，如 <c>ep-&lt;hex&gt;</c>）。
    /// 仅用于提示“可能已换代、可重读来源并重试排队导航”；<strong>绝不授权任何客户端、绝不参与来源绑定判断</strong>。
    /// 页面提交一律以 App 发布的 <see cref="OpenCodePageSource"/>（引用 + Revision）为准。
    /// </summary>
    public static string? TrustedEndpointIdentity
    {
        get { lock (NavigationSync) return _trustedEndpointIdentity; }
    }

    /// <summary>
    /// 由 App/MainWindow 注入当前来源身份提示（配置换代时同步更新；null/空白表示清除）。
    /// 仅记录提示值并触发一次“重读 App 来源 + 重试排队导航”；<strong>不切换后端、不发送任何请求、
    /// 不更新来源绑定、不授权任何客户端</strong>。非法（非不透明标识）身份抛出
    /// <see cref="ArgumentException"/>（不静默接受）。
    /// </summary>
    public static void SetTrustedEndpointIdentity(string? endpointIdentity)
    {
        var normalized = string.IsNullOrWhiteSpace(endpointIdentity) ? null : endpointIdentity;
        if (normalized is not null && !IsValidEndpointIdentity(normalized))
        {
            throw new ArgumentException(
                "可信来源身份必须是 1-128 个 ASCII 字母/数字/'.'/'_'/':'/'-' 的不透明标识，不能是 URL 或含凭据的字符串。",
                nameof(endpointIdentity));
        }

        bool changed;
        lock (NavigationSync)
        {
            changed = !string.Equals(_trustedEndpointIdentity, normalized, StringComparison.Ordinal);
            _trustedEndpointIdentity = normalized;
        }

        if (!changed)
        {
            return;
        }

        var handler = TrustedEndpointIdentityChanged;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(null, EventArgs.Empty);
            }
            catch
            {
                // 订阅者异常不影响身份切换。
            }
        }
    }

    private static bool IsValidEndpointIdentity(string identity)
    {
        if (identity.Length is < 1 or > 128)
        {
            return false;
        }

        foreach (var c in identity)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == ':' || c == '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 通知导航入口：按目标定位会话并聚焦 Permission/Form 卡片（Completion 为滚动时间线）。
    /// 仅用于定位与聚焦，<strong>绝不授权、绝不 POST、绝不切换后端</strong>；来源身份不匹配则拒绝。
    /// 页面/身份未就绪时目标入队，待就绪后处理；结构化结果经 <see cref="NavigationHandled"/> 暴露。
    /// </summary>
    public Task NavigateToNotificationAsync(NotificationNavigationTarget target, NotificationInteractionResult? result = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Source != NotificationSource.OpenCode)
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.Refused, "非 OpenCode 来源的导航目标；已拒绝。", target);
            return Task.CompletedTask;
        }

        if (result is not null
            && ((result.EndpointIdentity is { } rid && !string.Equals(rid, target.EndpointIdentity, StringComparison.Ordinal))
                || (result.SessionId is { } rsid && !string.Equals(rsid, target.SessionId, StringComparison.Ordinal))
                || (result.RequestId is { } rrid && !string.Equals(rrid, target.RequestId, StringComparison.Ordinal))))
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.Refused, "导航结果与目标不一致；已拒绝，不定位、不提交。", target);
            return Task.CompletedTask;
        }

        var source = _source;
        if (source is { IsRevoked: false }
            && !string.Equals(source.Identity, target.EndpointIdentity, StringComparison.Ordinal))
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.Refused, "目标来源与当前连接来源不匹配（可能已换代）；已拒绝。", target);
            return Task.CompletedTask;
        }

        if (!_pageActive || !SourceReady)
        {
            if (EnqueueNavigation(target, result))
            {
                RaiseNavigationHandled(OpenCodeNavigationStatus.Queued,
                    "OpenCode 连接尚未就绪：导航目标已排队。", target);
            }
            else
            {
                RaiseNavigationHandled(OpenCodeNavigationStatus.NeedsApp, "导航队列已满；未排队，请稍后重试。", target);
            }

            return Task.CompletedTask;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => _ = ProcessNavigationTargetAsync(target, result));
            return Task.CompletedTask;
        }

        return ProcessNavigationTargetAsync(target, result);
    }

    private static bool EnqueueNavigation(NotificationNavigationTarget target, NotificationInteractionResult? result)
    {
        lock (NavigationSync)
        {
            if (!QueuedNavigation.Add(target))
            {
                return true; // 相同目标已在队列/处理中。
            }

            if (PendingNavigation.Count >= 64)
            {
                QueuedNavigation.Remove(target);
                return false;
            }

            PendingNavigation.Enqueue((target, result));
            return true;
        }
    }

    private async Task TryDrainNavigationQueueAsync()
    {
        if (!_pageActive || !SourceReady)
        {
            return;
        }

        lock (NavigationSync)
        {
            if (_navigationDraining)
            {
                return;
            }

            _navigationDraining = true;
        }

        try
        {
            while (true)
            {
                (NotificationNavigationTarget Target, NotificationInteractionResult? Result) item;
                lock (NavigationSync)
                {
                    if (PendingNavigation.Count == 0 || !_pageActive || !SourceReady)
                    {
                        return;
                    }

                    item = PendingNavigation.Dequeue();
                    QueuedNavigation.Remove(item.Target);
                }

                await ProcessNavigationTargetAsync(item.Target, item.Result);
            }
        }
        finally
        {
            lock (NavigationSync)
            {
                _navigationDraining = false;
            }
        }
    }

    private async Task ProcessNavigationTargetAsync(NotificationNavigationTarget target, NotificationInteractionResult? result)
    {
        if (!_pageActive)
        {
            EnqueueNavigation(target, result);
            return;
        }

        var source = _source;
        if (source is null || source.IsRevoked)
        {
            // 来源未就绪：目标重新入队，待来源就绪后再处理。
            EnqueueNavigation(target, result);
            return;
        }

        if (!string.Equals(source.Identity, target.EndpointIdentity, StringComparison.Ordinal))
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.Refused, "目标来源与当前连接来源不匹配（可能已换代）；未定位。", target);
            return;
        }

        var client = _client;
        if (client is null)
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.NeedsApp, "本页连接尚未就绪：无法定位目标会话。", target);
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var generation = _sessionGeneration;
        using var operationCts = CreateOperationToken(source);
        var ct = operationCts.Token;

        OpenCodeSessionInfo? located;
        try
        {
            located = await LocateSessionAsync(client, target.SessionId, ct);
        }
        catch (OperationCanceledException)
        {
            if (!_pageActive)
            {
                EnqueueNavigation(target, result);
            }

            return;
        }
        catch (OpenCodeException ex)
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.ReadFailed, DescribeNavigationReadFailure(ex), target);
            return;
        }

        if (BindingLost(source, revision, sourceGeneration, client, generation, ct))
        {
            EnqueueNavigation(target, result);
            return;
        }

        if (located is null)
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.Gap,
                "目标会话未在本次查找范围（有界分页）内找到；这不代表它一定不存在，请手动在会话列表中选择。", target);
            return;
        }

        var navigationPageGeneration = _pageGeneration;
        int navigationSessionGeneration;
        try
        {
            InsertSessionItemFirst(located);
            navigationSessionGeneration = await NavigateToSessionAsync(
                target.SessionId,
                CreateVerifiedParentTarget(located, target.SessionId));
        }
        catch (OperationCanceledException)
        {
            if (!_pageActive)
            {
                EnqueueNavigation(target, result);
            }

            return;
        }
        catch (OpenCodeException ex)
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.ReadFailed, DescribeNavigationReadFailure(ex), target);
            return;
        }

        // 本次导航的完整生效证明：页面代际、viewed 快照修订、返回的会话代际与来源/客户端绑定须全部匹配。
        if (navigationPageGeneration != _pageGeneration
            || !string.Equals(_selectedSessionId, target.SessionId, StringComparison.Ordinal)
            || !IsCurrentBinding(source, revision, sourceGeneration, client, navigationSessionGeneration))
        {
            return;
        }

        if (_sessionMissing)
        {
            RaiseNavigationHandled(OpenCodeNavigationStatus.ReadFailed, "目标会话在服务端不存在（HTTP 404）。", target);
            return;
        }

        if (target.Kind == NotificationNavigationKind.Completion)
        {
            ScrollToBottom(animated: false);
            ApplyNavigationResult(result, target);
            RaiseNavigationHandled(OpenCodeNavigationStatus.Succeeded, "已定位会话（Completion：仅定位，不代表完成）。", target);
            return;
        }

        var isForm = target.Kind == NotificationNavigationKind.Form;
        if (!_interactionsView.TryFocusCard(isForm, target.RequestId))
        {
            ApplyNavigationResult(result, target);
            RaiseNavigationHandled(OpenCodeNavigationStatus.TargetNotFound,
                isForm
                    ? "目标表单当前不处于待处理状态（可能已结算）；未提交任何回答。"
                    : "目标权限请求当前不处于待处理状态（可能已结算）；未提交任何决定。", target);
            return;
        }

        ApplyNavigationResult(result, target);
        RaiseNavigationHandled(OpenCodeNavigationStatus.Succeeded,
            isForm ? "已定位并聚焦表单卡片。" : "已定位并聚焦权限卡片。", target);
    }

    /// <summary>
    /// 按目标会话定位：先取最近一页，再沿 cursor.next 分页（有界：初始一页 + 至多 8 页 × 100），
    /// 不依赖“前 50 条”。预算耗尽或列表到底均返回 null，由调用方按“未在本次查找范围找到、请手动选择”提示。
    /// </summary>
    private static async Task<OpenCodeSessionInfo?> LocateSessionAsync(
        OpenCodeClient client, string sessionId, CancellationToken ct)
    {
        var page = await client.ListSessionsAsync(
            new OpenCodeSessionListQuery { Order = OpenCodeSessionOrders.Desc, Limit = 100 }, ct);

        for (var pages = 1; ; pages++)
        {
            foreach (var session in page.Data ?? new List<OpenCodeSessionInfo>())
            {
                if (string.Equals(session?.Id, sessionId, StringComparison.Ordinal))
                {
                    return session;
                }
            }

            var cursor = page.Cursor?.Next;
            if (string.IsNullOrEmpty(cursor) || pages >= 8)
            {
                return null;
            }

            page = await client.ListSessionsAsync(new OpenCodeSessionListQuery { Cursor = cursor, Limit = 100 }, ct);
        }
    }

    /// <summary>导航用强制会话切换：重置消息与卡片并刷新（保留输入草稿，不触碰 Composer）。</summary>
    private async Task<int> NavigateToSessionAsync(string sessionId, SessionReturnTarget? returnTarget = null)
    {
        InvalidateViewAcknowledgement();
        ClearSessionReturnContext();
        _selectedSessionId = sessionId;
        _sessionMissing = false;
        _sessionGeneration++;
        BeginSessionMessageBinding(sessionId, KnownSessionDirectory(sessionId));
        BindComposer();

        // 通知/子会话导航的强制切换同样使 view ACK 活动快照立即失效（新窗口 Apply 后再尝试）。
        InvalidateViewAcknowledgement();
        CancelSessionOperations();
        _sessionCts = new CancellationTokenSource();
        InstallSessionReturnContext(returnTarget);
        var sessionGeneration = _sessionGeneration;
        SelectSessionItem(sessionId);

        EnsureConversationImages();
        _interactionsView.ClearAll();
        // 同 SwitchToSessionAsync：切换会话即清除旧会话的恢复提示、确认快照与确认面板。
        _sessionProtectionHint = false;
        ConfigureAgentSelectorForSession(sessionId);
        _recoveryOrigin = null;
        _sessionRecoveryPanel.HideConfirm();
        _sessionRecoveryPanel.SetBusy(false, null);
        _mutationEpoch++;
        _interactionBusy = false;
        _pendingAckIds.Clear();
        _deliveredNoticeShown = false;
        PermissionBar.IsOpen = false;
        UpdateWaitingText();
        UpdateEmptyState();
        UpdateComposerButtons();
        UpdateInteractionsSection();

        RaiseSessionTracked(sessionId, OpenCodeSessionTrackReason.Selected);
        await RefreshMessagesAsync(initial: true);
        return sessionGeneration;
    }

    private static string DescribeNavigationReadFailure(OpenCodeException ex) => ex switch
    {
        OpenCodeTimeoutException => "定位目标会话超时（客户端 30 秒内未完成）；未定位。",
        OpenCodeProtocolException => "定位响应不符合已核实的 V2 契约；未定位。",
        OpenCodeApiException api => "定位请求被拒绝（HTTP " + api.StatusCode + "）；未定位。",
        _ => "无法连接 OpenCode 服务；未定位。",
    };

    /// <summary>
    /// 应用随导航传入的通知入口结果：<see cref="NotificationInteractionStatus.Succeeded"/> 只表示
    /// 服务器已接受该决定（HTTP 204），<strong>绝不是 AI 完成</strong>；未知结果保持保护、绝不自动重发。
    /// </summary>
    private void ApplyNavigationResult(NotificationInteractionResult? result, NotificationNavigationTarget target)
    {
        if (result is null)
        {
            return;
        }

        var isForm = target.Kind == NotificationNavigationKind.Form;
        switch (result.Status)
        {
            case NotificationInteractionStatus.Succeeded:
            case NotificationInteractionStatus.Expired:
            case NotificationInteractionStatus.Duplicate:
                _interactionsView.Suppress(isForm, target.RequestId);
                UpdateInteractionsSection();
                break;
            case NotificationInteractionStatus.OutcomeUnknown:
                if (isForm)
                {
                    _interactionsView.MarkFormBlocked(target.RequestId,
                        "共享仲裁保护：结果未知（可能已提交）；不会自动重发，请点击“重新查询状态”核实。");
                }
                else if (target.Kind == NotificationNavigationKind.Permission)
                {
                    _interactionsView.MarkPermissionBlocked(target.RequestId,
                        "共享仲裁保护：结果未知（可能已提交）；不会自动重发，请点击“重新查询状态”核实。");
                }

                ShowMessage(InfoBarSeverity.Warning, "提交结果未知",
                    "通知入口的提交结果未知；本页不会自动重发，请在卡片中“重新查询状态”核实。");
                break;
            case NotificationInteractionStatus.NeedsApp:
                ShowMessage(InfoBarSeverity.Informational, "请在应用内处理",
                    "通知入口未能处理该交互（NeedsApp）；请在本页卡片中核对后处理。");
                break;
            case NotificationInteractionStatus.Failed:
            case NotificationInteractionStatus.InvalidInput:
            case NotificationInteractionStatus.Cancelled:
            case NotificationInteractionStatus.Unavailable:
                ShowMessage(InfoBarSeverity.Informational, "通知入口未提交",
                    "通知入口未完成该交互（" + result.Status + "）；请在卡片中核对后处理。");
                break;
            default:
                // Prepared / Submitted / Navigate 等：仅提示，绝不据此推断成功或自动提交。
                ShowMessage(InfoBarSeverity.Informational, "通知入口状态",
                    "通知入口状态：" + result.Status + "；本页未自动提交。");
                break;
        }
    }

    private void RaiseNavigationHandled(OpenCodeNavigationStatus status, string detail, NotificationNavigationTarget? target)
    {
        var handler = NavigationHandled;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new OpenCodeNavigationResultEventArgs(status, detail, target));
        }
        catch
        {
            // 订阅者异常不影响本页。
        }
    }

    private void OpenCodePage_TrustedEndpointIdentityChanged(object? sender, EventArgs e)
    {
        // 兼容旧宿主注入的导航提示：只重读 App 当前来源并重试排队导航；
        // 该静态身份绝不用于授权任何客户端、绝不更新本页来源绑定。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_pageActive)
            {
                return;
            }

            ApplyCurrentSource();
            _ = TryDrainNavigationQueueAsync();
        });
    }

    private void SettingsRequestButton_Click(object sender, RoutedEventArgs e)
    {
        var handler = SettingsRequested;
        if (handler is null)
        {
            ShowMessage(
                InfoBarSeverity.Informational,
                "设置入口尚未接入",
                "宿主可通过 OpenCodePage.SettingsRequested 事件接线到设置界面；当前请直接编辑 settings.json 中的 openCode.baseUrl。");
            return;
        }

        handler(this, EventArgs.Empty);
    }

    /// <summary>安全取主题画刷：键不存在时返回 null（不抛异常，控件回退默认外观）。</summary>
    private static Brush? TryBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources)
            {
                // 用索引器读取；键缺失抛 KeyNotFoundException，由外层 catch 兜底。
                return resources[key] as Brush;
            }
        }
        catch
        {
            // 资源字典在极端时机（关闭中）可能不可用；回退默认外观即可。
        }

        return null;
    }
}
