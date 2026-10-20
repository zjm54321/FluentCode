using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.Protocols;
using AIFrontend.Services.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.Local;

/// <summary>
/// Local 聊天视图（可嵌入 <see cref="UserControl"/>）：左侧会话列表、右侧模型选择/转录/多行输入。
/// </summary>
/// <remarks>
/// 定位（重要）：Local 是<strong>API 客户端</strong>，通过 <see cref="ProtocolChatClient"/> 调用已配置
/// provider 的接口，<strong>不</strong>声称或执行任何本地推理。
/// <para>挂载：父页面构造本控件（推荐传入共享的 <see cref="SettingsService"/> /
/// <see cref="DpapiSecretStore"/> / <see cref="LocalSessionStore"/> 实例）并加入可视树即可；
/// 本控件在 <c>Loaded</c> 时自行加载设置与会话。卸载时父页面应调用 <see cref="ShutdownAsync"/>。</para>
/// <para>仅做 UI 编排：不新增网络/通用存储服务；配置、凭据、会话分别由上述三个共享服务持久化，
/// 三个协议由 <see cref="ProtocolChatClient"/> 实现。</para>
/// <para>响应式：按本控件实际宽度切换布局——宽窗会话列内联 + 横向工具栏；较窄时工具栏纵向重排；
/// 更窄时会话列改为原生 <see cref="SplitView"/> 覆盖层 + 工具栏“会话”入口（不隐藏得无法选择）。
/// 断点与测量依据见 <c>docs/local-ui-contract.md</c> §13。</para>
/// </remarks>
public sealed class LocalChatView : UserControl
{
    private const string ModeMarkdown = TranscriptDocument.MarkdownOption;
    private const string ModeHtml = TranscriptDocument.HtmlOption;

    // 响应式断点（按本控件实际宽度，DIP；测量依据见 docs/local-ui-contract.md §13）：
    // 右侧横向工具栏最小宽实测约 619（UIA 捕获 899 − 280），加固定 280 会话列 = 899 → 取 900；
    // 工具栏纵向重排后右侧最小宽约 290（模型行 26+10+220 + 内边距 32）→ 280 + 290 ≈ 570，取 600。
    private const double HorizontalToolbarMinWidth = 900;
    private const double SessionsInlineMinWidth = 600;

    private readonly SettingsService _settingsService;
    private readonly DpapiSecretStore _secretStore;
    private readonly LocalSessionStore _sessionStore;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SafeContentRenderer _renderer = new();

    private readonly object _sync = new();
    private readonly List<LocalSession> _sessions = new();
    private readonly List<DisplayMessage> _display = new();

    private AppSettings _settings = AppSettings.Empty;
    private LocalSession? _current;
    private string? _currentSessionId;
    private DisplayMessage? _streamAssistant;
    private string? _streamingSessionId;
    private CancellationTokenSource? _cts;
    private Task? _streamTask;
    private string? _selectedEffort;
    private TranscriptRenderMode _mode = TranscriptRenderMode.Markdown;

    private volatile bool _renderRequested;
    private CancellationTokenSource? _renderLoopCts;
    private bool _webViewUnavailable;
    private bool _suppressSelection;
    private bool _sendInProgress;

    // 生命周期：ShutdownAsync 取消后，尚未完成的可 await 激活调用会随之取消。
    private readonly CancellationTokenSource _lifetimeCts = new();

    // 当前一次设置/会话加载 Task（UI 线程访问）：OnLoaded 与 TryActivateSessionAsync 复用同一实例，
    // 绝不并发重复 Load。未开始加载时为 null。
    private Task? _loadTask;

    // 原生控件（纯 C# 组合）。
    private readonly ListView _sessionList = new();
    private readonly ComboBox _modelCombo = new();
    private readonly ComboBox _effortCombo = new();
    private readonly ComboBox _modeCombo = new();
    private readonly TextBox _input = new();
    private readonly Button _sendButton = new();
    private readonly ProgressRing _busy = new();
    private readonly InfoBar _info = new();
    private readonly TranscriptWebViewHost _webHost = new();
    private readonly PlainTranscriptView _plainView = new();
    private readonly Grid _transcriptHost = new();
    private readonly Border _guidance = new();
    private readonly SplitView _splitView = new();
    private readonly StackPanel _toolbar = new();
    private readonly Button _sessionsToggle = new();

    // 响应式布局状态：最近一次应用的“会话列并列”态；null = 尚未应用（首次由 SizeChanged/Loaded 校正）。
    private bool? _sessionsInline;

    /// <summary>空配置时用户点击“打开设置”触发；父页面据此导航到设置 UI。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// 一轮请求<strong>真实成功</strong>完成时触发：仅当协议返回 Completed 且结束原因为
    /// <see cref="ChatFinishReason.Stop"/>。错误/取消/工具/截断均不触发。
    /// </summary>
    /// <remarks>本控件不注册或发送任何系统通知；此事件仅供父页面后续接线。</remarks>
    public event EventHandler<LocalTaskCompletedEventArgs>? TaskCompleted;

    /// <summary>
    /// 一轮执行的<strong>结构化状态</strong>事件：先发 <see cref="LocalTaskStatus.Working"/>，
    /// 随后<strong>恰好一次</strong>终态（Completed / Failed / Cancelled）。
    /// </summary>
    /// <remarks>
    /// 语义与去重：
    /// <list type="bullet">
    /// <item>每次执行使用当轮 <c>SourceRequestId</c>（GUID("N")）与 <c>SessionId</c>；
    /// 同一次执行的所有状态事件复用同一 <c>SourceRequestId</c>，每次新执行都是新 ID。</item>
    /// <item>Working 在请求开始时发出；终态在收尾（<c>finally</c>）发出，覆盖凭据读取失败、
    /// 协议错误、传输中断、工具/长度/过滤终止、结束原因未知、用户取消等<strong>全部</strong>路径——
    /// 任何异常或取消都会终结 Working，绝不滞留。</item>
    /// <item>载荷只含结构化状态与不透明 ID，<strong>不含</strong>消息正文、错误文本或任何凭据。</item>
    /// <item>本事件不注册也不发送任何系统通知；<see cref="TaskCompleted"/> 的成功条件（仅 Completed + Stop）
    /// 与去重语义保持不变，二者不互相替代。</item>
    /// </list>
    /// </remarks>
    public event EventHandler<LocalTaskStatusEventArgs>? TaskStatusChanged;

    /// <summary>用默认数据目录创建三个共享服务（推荐父页面改用带参构造以共享实例）。</summary>
    public LocalChatView()
        : this(new SettingsService(), new DpapiSecretStore(), new LocalSessionStore())
    {
    }

    /// <summary>用外部提供的共享服务实例创建（推荐）。</summary>
    public LocalChatView(
        SettingsService settingsService,
        DpapiSecretStore secretStore,
        LocalSessionStore sessionStore)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _dispatcherQueue = DispatcherQueue;

        BuildUi();

        _settingsService.Changed += OnSettingsChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        SizeChanged += OnSizeChanged;

        ApplySessionsPanelBackground();
    }

    /// <summary>当前会话 ID；无会话时为 null。</summary>
    public string? CurrentSessionId => _currentSessionId;

    /// <summary>
    /// 通知/外部定位：按会话 ID 选中已加载会话（校验通过才切换），<strong>不覆盖输入框未发送草稿</strong>。
    /// </summary>
    /// <remarks>
    /// 仅在 UI 线程调用。校验：ID 非空且存在于已加载会话集合（Ordinal 精确匹配）。
    /// 校验失败返回 <c>false</c>，不切换、不抛异常；成功返回 <c>true</c> 并按
    /// <see cref="SelectSessionAsync"/> 选中（切换前停旧流，行为与手动选择一致）。
    /// 本方法<strong>不</strong>读取、清空或改写 <c>_input</c>，未发送草稿原样保留。
    /// </remarks>
    public bool TryActivateSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        bool exists;
        lock (_sync)
        {
            exists = _sessions.Any(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        }

        if (!exists)
        {
            return false;
        }

        if (!string.Equals(sessionId, _currentSessionId, StringComparison.Ordinal))
        {
            _ = SelectSessionAsync(sessionId);
        }

        return true;
    }

    /// <summary>
    /// 可 await 的通知/外部定位：等待设置与会话的<strong>实际加载真正完成</strong>后，再按会话 ID 选中已加载会话。
    /// </summary>
    /// <remarks>
    /// 复用当前进行中的同一 <see cref="RefreshAsync"/> 加载 Task（绝不并发重复 Load）：加载完成即会话集合已刷新。
    /// 返回 <c>true</c> 仅当选中成功；<c>false</c> 表示加载已真正完成但会话不存在（或被 <see cref="ShutdownAsync"/> 取消）。
    /// 取消（关闭/页面卸载）经 <paramref name="cancellationToken"/> 或生命周期取消抛出
    /// <see cref="OperationCanceledException"/>——调用方据此把“取消”与“确实不存在”区分开，不把取消当成功。
    /// 须在 UI 线程调用；不读取、清空或改写未发送草稿。
    /// </remarks>
    public async Task<bool> TryActivateSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        await EnsureLoadStartedAsync().WaitAsync(linked.Token).ConfigureAwait(true);
        linked.Token.ThrowIfCancellationRequested();

        return TryActivateSession(sessionId);
    }

    /// <summary>
    /// 重新加载设置与会话并刷新 UI（UI 线程调用）。设置 <c>Changed</c> 事件也会自动刷新。
    /// </summary>
    /// <remarks>记录本次加载 Task 供 <see cref="TryActivateSessionAsync"/> 复用；UI 线程访问，不并发。</remarks>
    public Task RefreshAsync()
    {
        Task task = RefreshCoreAsync();
        _loadTask = task;
        return task;
    }

    private async Task RefreshCoreAsync()
    {
        AppSettings settings;
        try
        {
            settings = await _settingsService.LoadAsync();
        }
        catch (SettingsCorruptedException ex)
        {
            ShowInfo("设置文件损坏，未加载（原文件保留）。请修复或移走：" + (ex.FilePath ?? "settings.json"), error: true);
            settings = _settingsService.Current ?? AppSettings.Empty;
        }
        catch (Exception ex)
        {
            ShowInfo("加载设置失败：" + ex.GetType().Name, error: true);
            settings = _settingsService.Current ?? AppSettings.Empty;
        }

        ApplySettings(settings);
        await LoadSessionsAsync();
    }

    /// <summary>
    /// 取消进行中的流、<strong>等待其真正终结</strong>（含终态事件投递完成）、停止渲染循环并释放转录 WebView2；卸载时由父页面调用。
    /// </summary>
    /// <remarks>
    /// 顺序（关键）：先取消生命周期（使在途 <see cref="TryActivateSessionAsync"/> 退出），再同步解除本控件自身订阅，
    /// 然后取消并等待在途流收尾——<strong>父页面对本控件事件（TaskCompleted / TaskStatusChanged）的订阅尚未解除</strong>，
    /// 故取消产生的终态仍能经订阅链转发；随后以 dispatcher 栅栏确保已入队的终态回调全部执行完毕，本方法才返回。
    /// 返回后父页面方可安全退订并移除本控件。重复调用幂等。
    /// </remarks>
    public async Task ShutdownAsync()
    {
        if (!_lifetimeCts.IsCancellationRequested)
        {
            _lifetimeCts.Cancel();
        }

        _settingsService.Changed -= OnSettingsChanged;
        ActualThemeChanged -= OnActualThemeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;

        await StopStreamingAsyncCoreAsync(closeWebView: true).ConfigureAwait(true);
        await DrainDispatcherAsync().ConfigureAwait(true);
    }

    /// <summary>复用当前加载 Task；尚未开始加载时启动一次（UI 线程，幂等，不并发重复 Load）。</summary>
    private Task EnsureLoadStartedAsync() => _loadTask ?? RefreshAsync();

    /// <summary>
    /// dispatcher 栅栏：入队一个回调并等待其执行，确保此前已入队的终态/完成回调均已投递，
    /// 使订阅链可在本方法返回后安全退订（避免取消终态丢失）。
    /// </summary>
    private Task DrainDispatcherAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() => completion.TrySetResult()))
        {
            completion.TrySetResult();
        }

        return completion.Task;
    }

    // ------------------------------------------------------------------ 设置应用

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        // Changed 无线程亲和保证；事件不带锁，且这里不重新 LoadAsync（避免 Changed 自激循环）。
        AppSettings snapshot = _settingsService.Current ?? e.Settings;
        _dispatcherQueue.TryEnqueue(() => ApplySettings(snapshot));
    }

    private void ApplySettings(AppSettings settings)
    {
        _settings = settings ?? AppSettings.Empty;

        bool configured = _settings.Providers.Count > 0 && _settings.Models.Count > 0;
        _guidance.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;

        SyncModelCombo();
        SyncEffortCombo();
        UpdateSendEnabled();
        RenderCore();
    }

    private void SyncModelCombo()
    {
        _suppressSelection = true;
        try
        {
            _modelCombo.Items.Clear();
            _modelCombo.Items.Add(new ComboBoxItem { Content = "（未选择，跟随全局）", Tag = null });

            var ordered = _settings.Models
                .Where(m => m is not null)
                .OrderBy(m => ProviderDisplayName(m.ProviderId), StringComparer.Ordinal)
                .ThenBy(m => m.ModelId, StringComparer.Ordinal);

            ModelRef? effective = _current?.Model ?? _settings.SelectedModel;
            int selectedIndex = 0;
            int index = 1;
            foreach (ModelDescriptor model in ordered)
            {
                var reference = new ModelRef(model.ProviderId, model.ModelId);
                var item = new ComboBoxItem
                {
                    Content = DisplayModel(model),
                    Tag = reference,
                };
                _modelCombo.Items.Add(item);

                if (effective is { } eff && eff.ProviderId == reference.ProviderId && eff.ModelId == reference.ModelId)
                {
                    selectedIndex = index;
                }

                index++;
            }

            _modelCombo.SelectedIndex = selectedIndex;
            _modelCombo.IsEnabled = !IsStreaming && _settings.Models.Count > 0;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void SyncEffortCombo()
    {
        bool previous = _suppressSelection;
        _suppressSelection = true;
        try
        {
            _effortCombo.Items.Clear();
            _effortCombo.Items.Add(new ComboBoxItem { Content = "默认（不发送）", Tag = null });

            ModelDescriptor? model = ResolveEffectiveModel(out _, out _);
            var reasoning = model?.Capabilities.Reasoning;
            bool supported = model is not null
                && model.Capabilities.Source != CapabilitySource.Unknown
                && reasoning is { } rc
                && rc.EffortValues.Count > 0;

            int selectedIndex = 0;
            if (supported && reasoning is { } caps)
            {
                int index = 1;
                foreach (string effort in caps.EffortValues)
                {
                    _effortCombo.Items.Add(new ComboBoxItem { Content = effort, Tag = effort });
                    if (_selectedEffort is { } current && string.Equals(current, effort, StringComparison.Ordinal))
                    {
                        selectedIndex = index;
                    }

                    index++;
                }
            }

            if (!supported)
            {
                _selectedEffort = null;
            }

            _effortCombo.SelectedIndex = selectedIndex;
            _effortCombo.IsEnabled = supported && !IsStreaming;
        }
        finally
        {
            _suppressSelection = previous;
        }
    }

    private void UpdateSendEnabled()
    {
        bool configured = _settings.Providers.Count > 0 && _settings.Models.Count > 0;
        _sendButton.IsEnabled = IsStreaming || configured;
    }

    private string ProviderDisplayName(string providerId) =>
        _settings.FindProvider(providerId)?.DisplayName ?? providerId;

    private string DisplayModel(ModelDescriptor model) =>
        (model.DisplayName ?? model.ModelId) + " · " + ProviderDisplayName(model.ProviderId);

    private ModelDescriptor? ResolveEffectiveModel(out ProviderEndpoint? provider, out string? error)
    {
        provider = null;
        error = null;

        ModelRef? reference = _current?.Model ?? _settings.SelectedModel;
        if (reference is not { } referenceValue)
        {
            error = "尚未选择模型：请选择模型，或打开设置添加 provider 与模型。";
            return null;
        }

        ModelDescriptor? model = _settings.FindModel(referenceValue);
        if (model is null)
        {
            error = "会话引用的模型已不存在，请改选模型。";
            return null;
        }

        provider = _settings.FindProvider(model.ProviderId);
        if (provider is null)
        {
            error = "模型所属 provider 已不存在，请改选模型。";
            return null;
        }

        return model;
    }

    private ReasoningRequest? BuildReasoning(ModelDescriptor model)
    {
        if (_selectedEffort is not { } effort)
        {
            return null;
        }

        ModelCapabilities capabilities = model.Capabilities;
        if (capabilities.Source == CapabilitySource.Unknown || capabilities.Reasoning is not { } reasoning)
        {
            // 能力未知：绝不默认发送任何可选思考参数。
            return null;
        }

        if (!reasoning.SupportsEffort(effort))
        {
            return null;
        }

        return new ReasoningRequest { Effort = effort };
    }

    // ------------------------------------------------------------------ 会话

    private async Task LoadSessionsAsync()
    {
        IReadOnlyList<LocalSession> list;
        try
        {
            list = await _sessionStore.ListAsync();
        }
        catch (LocalSessionStoreException ex)
        {
            ShowInfo("加载会话失败（存在损坏的会话文件，原文件保留）：" + (ex.FilePath ?? string.Empty), error: true);
            return;
        }
        catch (Exception ex)
        {
            ShowInfo("加载会话失败：" + ex.GetType().Name, error: true);
            return;
        }

        lock (_sync)
        {
            _sessions.Clear();
            _sessions.AddRange(list);
        }

        if (_currentSessionId is null || !list.Any(s => string.Equals(s.Id, _currentSessionId, StringComparison.Ordinal)))
        {
            LocalSession? first = list.FirstOrDefault();
            _current = first;
            _currentSessionId = first?.Id;
            if (first is not null)
            {
                BuildDisplayFromSession(first);
            }
            else
            {
                lock (_sync)
                {
                    _display.Clear();
                }
            }
        }

        RebuildSessionList();
        SyncModelCombo();
        SyncEffortCombo();
        RenderCore();
        UpdateSendEnabled();
    }

    private async Task NewSessionAsync()
    {
        await StopStreamingAsyncCoreAsync(closeWebView: false);

        ModelRef? model = _settings.SelectedModel;
        if (_modelCombo.SelectedItem is ComboBoxItem item && item.Tag is ModelRef reference)
        {
            model = reference;
        }

        LocalSession session = LocalSession.CreateNew(title: null, model: model);
        try
        {
            session = await _sessionStore.SaveAsync(session);
        }
        catch (Exception ex)
        {
            ShowInfo("新建会话失败：" + ex.GetType().Name, error: true);
            return;
        }

        UpsertSession(session);
        _current = session;
        _currentSessionId = session.Id;
        BuildDisplayFromSession(session);
        RebuildSessionList();
        SyncModelCombo();
        SyncEffortCombo();
        RenderCore();
        UpdateSendEnabled();
    }

    private async Task SelectSessionAsync(string sessionId)
    {
        if (string.Equals(sessionId, _currentSessionId, StringComparison.Ordinal))
        {
            return;
        }

        // 切换会话前先停流并等待其收尾（部分内容落到旧会话），避免跨会话污染。
        await StopStreamingAsyncCoreAsync(closeWebView: false);

        LocalSession? session;
        lock (_sync)
        {
            session = _sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
        }

        if (session is null)
        {
            return;
        }

        _current = session;
        _currentSessionId = session.Id;
        BuildDisplayFromSession(session);
        SyncModelCombo();
        SyncEffortCombo();
        RenderCore();
        UpdateSendEnabled();
    }

    private void BuildDisplayFromSession(LocalSession session)
    {
        lock (_sync)
        {
            _display.Clear();
            foreach (ChatMessage message in session.Messages)
            {
                _display.Add(DisplayMessage.From(message));
            }
        }
    }

    private void UpsertSession(LocalSession session)
    {
        lock (_sync)
        {
            int index = _sessions.FindIndex(s => string.Equals(s.Id, session.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _sessions[index] = session;
            }
            else
            {
                _sessions.Add(session);
            }

            _sessions.Sort(static (a, b) =>
            {
                int byTime = b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
            });
        }
    }

    private void RebuildSessionList()
    {
        _suppressSelection = true;
        try
        {
            _sessionList.Items.Clear();

            List<LocalSession> snapshot;
            lock (_sync)
            {
                snapshot = new List<LocalSession>(_sessions);
            }

            FrameworkElement? toSelect = null;
            foreach (LocalSession session in snapshot)
            {
                FrameworkElement element = BuildSessionItem(session);
                element.Tag = session.Id;
                _sessionList.Items.Add(element);

                if (string.Equals(session.Id, _currentSessionId, StringComparison.Ordinal))
                {
                    toSelect = element;
                }
            }

            if (toSelect is not null)
            {
                _sessionList.SelectedItem = toSelect;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private static FrameworkElement BuildSessionItem(LocalSession session)
    {
        string title = session.Title ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            ChatMessage? firstUser = session.Messages
                .FirstOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Content));
            title = firstUser is null ? "新会话" : Truncate(firstUser.Content, 24);
        }

        string subtitle = session.UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
            + " · " + session.Messages.Count.ToString(CultureInfo.InvariantCulture) + " 条";

        return new StackPanel
        {
            Spacing = 2,
            Padding = new Thickness(4, 6, 4, 6),
            Children =
            {
                new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.7 },
            },
        };
    }

    // ------------------------------------------------------------------ 发送 / 流式

    private async Task SendAsync()
    {
        if (IsStreaming)
        {
            return;
        }

        if (_current is null)
        {
            await NewSessionAsync();
            if (_current is null)
            {
                ShowInfo("请先新建会话。", error: true);
                return;
            }
        }

        ModelDescriptor? model = ResolveEffectiveModel(out ProviderEndpoint? provider, out string? resolveError);
        if (model is null || provider is null)
        {
            ShowInfo(resolveError ?? "无法发送：模型或 provider 未就绪。", error: true);
            return;
        }

        string text = _input.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowInfo("请输入消息内容。");
            return;
        }

        // 历史角色校验：Local 仅支持文本历史（System/User/Assistant），明确拒绝工具相关内容。
        IReadOnlyList<ChatMessage>? history = TryProjectHistory(_current.Messages, out string? historyError);
        if (history is null)
        {
            ShowInfo(historyError ?? "会话历史不受支持。", error: true);
            return;
        }

        LocalSession current = _current;
        _input.Text = string.Empty;

        ChatMessage userMessage = ChatMessage.User(text);
        LocalSession working = current with { Messages = current.Messages.Append(userMessage).ToArray() };
        _current = working;
        UpsertSession(working);

        var assistantDisplay = new DisplayMessage(ChatRole.Assistant);
        lock (_sync)
        {
            _display.Add(new DisplayMessage(ChatRole.User) { Content = userMessage.Content });
            _display.Add(assistantDisplay);
            _streamAssistant = assistantDisplay;
            _streamingSessionId = working.Id;
        }

        RebuildSessionList();
        RenderCore();

        // 用户消息先落盘（不阻塞 UI 线程：await，不 .Result）。
        // 全部校验已在上面完成；此处置位“发送进行中”，跨 await 阻止重复发送（流开始前 _streamTask 仍为 null）。
        _sendInProgress = true;
        await PersistSessionAsync(working, "保存用户消息失败");

        if (_lifetimeCts.IsCancellationRequested)
        {
            // 视图已卸载/关闭（ShutdownAsync 已取消）：不再发起网络请求，避免创建无人收尾的“无主任务”。
            _sendInProgress = false;
            return;
        }

        var requestMessages = new List<ChatMessage>(history) { userMessage };
        string requestId = Guid.NewGuid().ToString("N");

        _cts = new CancellationTokenSource();
        StartRenderLoop();
        SetStreaming(true);

        _streamTask = RunStreamAsync(provider, model, requestMessages, requestId, working.Id, _cts.Token);
        try
        {
            await _streamTask;
        }
        finally
        {
            OnStreamEnded();
        }
    }

    private static IReadOnlyList<ChatMessage>? TryProjectHistory(IReadOnlyList<ChatMessage> messages, out string? error)
    {
        error = null;
        var projected = new List<ChatMessage>(messages.Count);

        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.Tool || message.ToolCalls is { Count: > 0 })
            {
                error = "当前会话包含工具调用/工具结果消息；Local 仅支持纯文本历史，本轮未发送。";
                return null;
            }

            if (message.Role is ChatRole.System or ChatRole.User or ChatRole.Assistant)
            {
                projected.Add(message);
            }
        }

        return projected;
    }

    private async Task RunStreamAsync(
        ProviderEndpoint provider,
        ModelDescriptor model,
        IReadOnlyList<ChatMessage> messages,
        string requestId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        string? apiKey = null;
        bool success = false;
        ChatFinishReason? terminal = null;

        // Working：本轮执行开始（含凭据读取前的失败路径也会经 finally 终结，不滞留 Working）。
        RaiseTaskStatus(requestId, sessionId, LocalTaskStatus.Working);

        try
        {
            // 解析凭据；失败时明确报错，绝不静默当作“无密钥”。密钥不写日志、不进异常消息。
            try
            {
                apiKey = provider.CredentialId is { } credentialId
                    ? await _secretStore.GetAsync(credentialId, CancellationToken.None).ConfigureAwait(false)
                    : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetAssistantStatus(sessionId, "读取凭据失败（" + ex.GetType().Name + "）。", isError: true);
                return;
            }

            var request = new ChatRequest
            {
                Model = model,
                Messages = messages,
                Reasoning = BuildReasoning(model),
            };

            try
            {
                using var client = new ProtocolChatClient(provider, apiKey);
                await foreach (ChatStreamEvent evt in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (terminal is not null)
                    {
                        // 去重：终态之后不再处理任何事件。
                        break;
                    }

                    switch (evt.Kind)
                    {
                        case ChatStreamEventKind.Text:
                            AppendAssistantText(sessionId, evt.Text);
                            break;
                        case ChatStreamEventKind.Reasoning:
                            AppendAssistantReasoning(sessionId, evt.Text);
                            break;
                        case ChatStreamEventKind.ToolCall:
                            SetAssistantStatus(sessionId, "收到工具调用；Local 仅支持纯文本，本轮不作为回答成功。", isError: true);
                            break;
                        case ChatStreamEventKind.Completed:
                            terminal = evt.FinishReason ?? ChatFinishReason.Unknown;
                            success = terminal == ChatFinishReason.Stop;
                            if (!success)
                            {
                                SetAssistantStatus(sessionId, DescribeFinish(terminal.Value), isError: true);
                            }

                            break;
                        case ChatStreamEventKind.Incomplete:
                            terminal = evt.FinishReason ?? ChatFinishReason.Unknown;
                            SetAssistantStatus(sessionId, DescribeFinish(terminal.Value), isError: true);
                            break;
                        case ChatStreamEventKind.Error:
                            terminal = evt.FinishReason ?? ChatFinishReason.ProviderError;
                            SetAssistantStatus(sessionId, "错误：" + (evt.ErrorMessage ?? "未知错误")
                                + (evt.ErrorCode is { } code ? "（" + code + "）" : string.Empty), isError: true);
                            break;
                    }
                }

                if (terminal is null)
                {
                    terminal = ChatFinishReason.Unknown;
                    SetAssistantStatus(sessionId, DescribeFinish(ChatFinishReason.Unknown), isError: true);
                }
            }
            catch (OperationCanceledException)
            {
                terminal = ChatFinishReason.Cancelled;
                SetAssistantStatus(sessionId, DescribeFinish(ChatFinishReason.Cancelled), isError: true);
            }
            catch (ProtocolClientException ex)
            {
                terminal = ChatFinishReason.ProviderError;
                SetAssistantStatus(
                    sessionId,
                    "请求失败（" + ex.Code + (ex.StatusCode is { } status ? " / HTTP " + status.ToString(CultureInfo.InvariantCulture) : string.Empty) + "）。",
                    isError: true);
            }
            catch (Exception ex)
            {
                terminal = ChatFinishReason.Unknown;
                SetAssistantStatus(sessionId, "请求失败：" + ex.GetType().Name + "。", isError: true);
            }
        }
        finally
        {
            // 尽早释放明文引用（.NET string 无法可靠擦除）；不记录也不持久化密钥。
            apiKey = null;

            await PersistAssistantAsync(sessionId).ConfigureAwait(false);

            // 终态：恰好一次，终结 Working。成功=Completed；取消=Cancelled；其余
            // （协议错误/传输中断/工具/长度/内容过滤/结束原因未知/凭据读取失败等）=Failed。
            LocalTaskStatus terminalStatus = success
                ? LocalTaskStatus.Completed
                : terminal == ChatFinishReason.Cancelled
                    ? LocalTaskStatus.Cancelled
                    : LocalTaskStatus.Failed;
            RaiseTaskStatus(requestId, sessionId, terminalStatus);

            if (success)
            {
                var args = new LocalTaskCompletedEventArgs(requestId, sessionId);
                _dispatcherQueue.TryEnqueue(() => TaskCompleted?.Invoke(this, args));
            }
        }
    }

    private async Task PersistAssistantAsync(string sessionId)
    {
        LocalSession? session;
        ChatMessage assistant;
        lock (_sync)
        {
            if (_streamAssistant is null
                || !string.Equals(_streamingSessionId, sessionId, StringComparison.Ordinal)
                || _current is null
                || !string.Equals(_current.Id, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            session = _current;
            assistant = ChatMessage.Assistant(_streamAssistant.Content, _streamAssistant.ReasoningText);
        }

        // 空回答且未成功时不写空助手消息（仅保留展示层状态）。
        if (assistant.Content.Length == 0 && string.IsNullOrEmpty(assistant.ReasoningText))
        {
            return;
        }

        LocalSession updated = session with { Messages = session.Messages.Append(assistant).ToArray() };
        lock (_sync)
        {
            if (_current is not null && string.Equals(_current.Id, sessionId, StringComparison.Ordinal))
            {
                _current = updated;
            }
        }

        UpsertSession(updated);

        try
        {
            LocalSession persisted = await _sessionStore.SaveAsync(updated).ConfigureAwait(false);
            lock (_sync)
            {
                if (_current is not null && string.Equals(_current.Id, sessionId, StringComparison.Ordinal))
                {
                    _current = persisted;
                }
            }

            UpsertSession(persisted);
        }
        catch (Exception ex)
        {
            _dispatcherQueue.TryEnqueue(() =>
                ShowInfo("保存助手回复失败：" + ex.GetType().Name + "（磁盘状态可能未更新）。", error: true));
        }
    }

    private async Task PersistSessionAsync(LocalSession session, string failureLabel)
    {
        try
        {
            LocalSession persisted = await _sessionStore.SaveAsync(session);
            lock (_sync)
            {
                if (_current is not null && string.Equals(_current.Id, persisted.Id, StringComparison.Ordinal))
                {
                    _current = persisted;
                }
            }

            UpsertSession(persisted);
        }
        catch (Exception ex)
        {
            ShowInfo(failureLabel + "：" + ex.GetType().Name, error: true);
        }
    }

    private void AppendAssistantText(string sessionId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_sync)
        {
            if (_streamAssistant is null || !string.Equals(_streamingSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            _streamAssistant.Content += text;
        }

        _renderRequested = true;
    }

    private void AppendAssistantReasoning(string sessionId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_sync)
        {
            if (_streamAssistant is null || !string.Equals(_streamingSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            _streamAssistant.ReasoningText = (_streamAssistant.ReasoningText ?? string.Empty) + text;
        }

        _renderRequested = true;
    }

    private void SetAssistantStatus(string sessionId, string text, bool isError)
    {
        lock (_sync)
        {
            if (_streamAssistant is null || !string.Equals(_streamingSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            _streamAssistant.StatusText = text;
            _streamAssistant.StatusIsError = isError;
        }

        _renderRequested = true;
    }

    /// <summary>把结构化状态事件 marshal 到 UI 线程后抛出；载荷仅含状态与不透明 ID。</summary>
    private void RaiseTaskStatus(string requestId, string sessionId, LocalTaskStatus status)
    {
        var args = new LocalTaskStatusEventArgs(requestId, sessionId, status);
        _dispatcherQueue.TryEnqueue(() => TaskStatusChanged?.Invoke(this, args));
    }

    private static string DescribeFinish(ChatFinishReason reason) => reason switch
    {
        ChatFinishReason.Stop => "已完成。",
        ChatFinishReason.Length => "回答因达到长度上限被截断（未完成）。",
        ChatFinishReason.ToolCalls => "模型请求工具调用，Local 不支持（未完成）。",
        ChatFinishReason.ContentFilter => "回答被内容过滤中断（未完成）。",
        ChatFinishReason.Cancelled => "已取消；保留已生成的部分内容（未完成）。",
        ChatFinishReason.TransportError => "传输中断（未完成）。",
        ChatFinishReason.ProviderError => "服务端错误（未完成）。",
        _ => "结束原因未知（未完成）。",
    };

    private bool IsStreaming => _streamTask is not null;

    private void SetStreaming(bool streaming)
    {
        _busy.IsActive = streaming;
        _busy.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
        _sendButton.Content = streaming ? "停止" : "发送";
        _input.IsEnabled = !streaming;
        _modeCombo.IsEnabled = !streaming;
        _modelCombo.IsEnabled = !streaming && _settings.Models.Count > 0;
        _effortCombo.IsEnabled = !streaming && EffortSupported();
        UpdateSendEnabled();
    }

    private bool EffortSupported()
    {
        ModelDescriptor? model = ResolveEffectiveModel(out _, out _);
        return model is not null
            && model.Capabilities.Source != CapabilitySource.Unknown
            && model.Capabilities.Reasoning is { } reasoning
            && reasoning.EffortValues.Count > 0;
    }

    private async Task StopStreamingAsyncCoreAsync(bool closeWebView)
    {
        CancellationTokenSource? cts = _cts;
        Task? task = _streamTask;
        if (cts is not null && task is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // 取消失败不致命。
            }

            try
            {
                await task;
            }
            catch
            {
                // RunStreamAsync 内部已兜底；此处仅确保收尾。
            }

            OnStreamEnded();
        }

        if (closeWebView)
        {
            _webHost.Close();
        }
    }

    private void OnStreamEnded()
    {
        if (_streamTask is null)
        {
            return;
        }

        _streamTask = null;
        _sendInProgress = false;
        _cts?.Dispose();
        _cts = null;

        StopRenderLoop();

        lock (_sync)
        {
            _streamingSessionId = null;
            _streamAssistant = null;
        }

        SetStreaming(false);
        RebuildSessionList();
        RenderCore();
    }

    // ------------------------------------------------------------------ 渲染

    private void StartRenderLoop()
    {
        _renderLoopCts = new CancellationTokenSource();
        _ = RenderLoopAsync(_renderLoopCts.Token);
    }

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                if (_renderRequested)
                {
                    _renderRequested = false;
                    _dispatcherQueue.TryEnqueue(RenderCore);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常结束。
        }
    }

    private void StopRenderLoop()
    {
        try
        {
            _renderLoopCts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        _renderLoopCts?.Dispose();
        _renderLoopCts = null;
        _renderRequested = false;
    }

    private void RenderCore()
    {
        List<DisplayMessage> snapshot;
        lock (_sync)
        {
            snapshot = new List<DisplayMessage>(_display);
        }

        if (_webViewUnavailable)
        {
            _plainView.Visibility = Visibility.Visible;
            _webHost.Visibility = Visibility.Collapsed;
            _plainView.SetMessages(snapshot);
            return;
        }

        _plainView.Visibility = Visibility.Collapsed;
        _webHost.Visibility = Visibility.Visible;

        string html = TranscriptDocument.Build(
            _renderer, snapshot, _mode, ActualTheme == ElementTheme.Dark, out bool truncated);
        _ = _webHost.SetHtmlAsync(html);

        if (truncated)
        {
            ShowInfo("转录内容超过单文档长度上限，较早的消息未显示。");
        }
    }

    private void OnWebViewUnavailable(object? sender, EventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _webViewUnavailable = true;
            ShowInfo(
                _webHost.UnavailableReason
                    + " 已降级为纯文本显示：Markdown/HTML 未渲染，内容仍可阅读。",
                error: true);
            RenderCore();
        });
    }

    // ------------------------------------------------------------------ 事件处理

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 与 TryActivateSessionAsync 复用同一加载 Task：若激活已先一步启动加载，此处不再重复 Load。
        _ = EnsureLoadStartedAsync();

        // 首次可见时按实际宽度校正一次（SizeChanged 正常也会触发；此处兜底初始态）。
        ApplyResponsiveLayout(ActualWidth);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 卸载时取消进行中的流（部分内容已落到所属会话），但不销毁 WebView2（生命周期归父页面）。
        _sendInProgress = false;
        _ = StopStreamingAsyncCoreAsync(closeWebView: false);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplySessionsPanelBackground();
        RenderCore();
    }

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        if (_sessionList.SelectedItem is FrameworkElement element && element.Tag is string sessionId)
        {
            _ = SelectSessionAsync(sessionId);

            // 覆盖态：选中会话后收起覆盖层，露出聊天区；并列态无覆盖层，无需处理。
            if (_sessionsInline is false)
            {
                _splitView.IsPaneOpen = false;
            }
        }
    }

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _current is null)
        {
            SyncEffortCombo();
            return;
        }

        ModelRef? reference = _modelCombo.SelectedItem is ComboBoxItem item && item.Tag is ModelRef value
            ? value
            : (ModelRef?)null;

        LocalSession updated = _current with { Model = reference };
        _current = updated;
        UpsertSession(updated);
        SyncEffortCombo();
        RenderCore();

        try
        {
            LocalSession persisted = await _sessionStore.SaveAsync(updated);
            lock (_sync)
            {
                if (_current is not null && string.Equals(_current.Id, persisted.Id, StringComparison.Ordinal))
                {
                    _current = persisted;
                }
            }
        }
        catch (Exception ex)
        {
            ShowInfo("保存会话模型失败：" + ex.GetType().Name, error: true);
        }

        RebuildSessionList();
    }

    private void OnEffortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        _selectedEffort = _effortCombo.SelectedItem is ComboBoxItem item ? item.Tag as string : null;
    }

    private void OnModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        _mode = _modeCombo.SelectedIndex == 1 ? TranscriptRenderMode.Html : TranscriptRenderMode.Markdown;
        RenderCore();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (IsStreaming)
        {
            await StopStreamingAsyncCoreAsync(closeWebView: false);
            return;
        }

        if (_sendInProgress)
        {
            return;
        }

        await SendAsync();
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        // Shift+Enter 插入换行；单独 Enter 发送。
        if (IsShiftDown())
        {
            return;
        }

        e.Handled = true;
        OnSendClick(sender, e);
    }

    private static bool IsShiftDown()
    {
        try
        {
            Windows.UI.Core.CoreVirtualKeyStates state =
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }
        catch
        {
            // 无法判定修饰键时按“未按下 Shift”处理（Enter 发送）。
            return false;
        }
    }

    private void ShowInfo(string message, bool error = false)
    {
        _info.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        _info.Message = message;
        _info.IsOpen = true;
    }

    private static string Truncate(string value, int max)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "…";
    }

    // ------------------------------------------------------------------ 响应式布局

    /// <summary>
    /// 按本控件实际宽度应用响应式布局（纯尺寸生命周期，不改动会话/流式/渲染逻辑）：
    /// 宽窗（≥ <see cref="HorizontalToolbarMinWidth"/>）会话列 Inline 并列 + 横向工具栏；
    /// 中窗（≥ <see cref="SessionsInlineMinWidth"/>）会话列仍并列，工具栏纵向重排；
    /// 窄窗会话列改为 <see cref="SplitView"/> Overlay 覆盖层（默认收起）+ 工具栏“会话”入口按钮。
    /// 断点与测量依据见 docs/local-ui-contract.md §13。
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return; // 尚未获得有效宽度（刚创建/隐藏中）：保持现状，等下一次布局。
        }

        _toolbar.Orientation = width >= HorizontalToolbarMinWidth ? Orientation.Horizontal : Orientation.Vertical;

        bool inlineSessions = width >= SessionsInlineMinWidth;
        if (_sessionsInline == inlineSessions)
        {
            return;
        }

        _sessionsInline = inlineSessions;
        _splitView.DisplayMode = inlineSessions ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        _splitView.IsPaneOpen = inlineSessions;
        _sessionsToggle.Visibility = inlineSessions ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    private void OnSessionsToggleClick(object sender, RoutedEventArgs e)
    {
        _splitView.IsPaneOpen = !_splitView.IsPaneOpen;
    }

    /// <summary>
    /// 覆盖态会话面板底色：只取 WinUI 主题画刷（不硬编码颜色、不换皮），保证覆盖层上的会话可读；
    /// 键缺失时保持透明（与内联态一致），不抛异常。主题切换时重新解析（见 OnActualThemeChanged）。
    /// </summary>
    private void ApplySessionsPanelBackground()
    {
        if (_splitView.Pane is not Panel pane)
        {
            return;
        }

        if (Application.Current?.Resources is not { } resources)
        {
            return;
        }

        foreach (string key in new[]
        {
            "SolidBackgroundFillColorBaseBrush",
            "LayerFillColorDefaultBrush",
            "CardBackgroundFillColorDefaultBrush",
        })
        {
            if (resources.TryGetValue(key, out object value) && value is Brush brush)
            {
                pane.Background = brush;
                return;
            }
        }
    }

    // ------------------------------------------------------------------ 视觉树

    private void BuildUi()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAutomationId(this, "LocalChatView");

        // 原生 SplitView 承载会话列：宽窗 Inline 并列（与原固定 280 列等价），窄窗 Overlay 覆盖，
        // 由工具栏“会话”入口按钮开合（见 ApplyResponsiveLayout）。
        _splitView.OpenPaneLength = 280;
        _splitView.DisplayMode = SplitViewDisplayMode.Inline;
        _splitView.IsPaneOpen = true;
        _splitView.LightDismissOverlayMode = LightDismissOverlayMode.On;
        _splitView.Pane = BuildLeftPanel();
        _splitView.Content = BuildRightPanel();

        Content = _splitView;
    }

    private Grid BuildLeftPanel()
    {
        var newButton = new Button
        {
            Content = "新建",
            MinWidth = 64,
        };
        AutomationProperties.SetAutomationId(newButton, "LocalNewSessionButton");
        newButton.Click += (_, _) => _ = NewSessionAsync();

        var header = new Grid { Padding = new Thickness(12, 12, 12, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "会话",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(newButton, 1);
        header.Children.Add(title);
        header.Children.Add(newButton);

        _sessionList.SelectionMode = ListViewSelectionMode.Single;
        _sessionList.SelectionChanged += OnSessionSelectionChanged;
        AutomationProperties.SetAutomationId(_sessionList, "LocalSessionList");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_sessionList, 1);
        grid.Children.Add(header);
        grid.Children.Add(_sessionList);
        return grid;
    }

    private Grid BuildRightPanel()
    {
        _toolbar.Orientation = Orientation.Horizontal;
        _toolbar.Spacing = 10;
        _toolbar.Padding = new Thickness(16, 12, 16, 8);

        // 窄窗入口（仅覆盖态可见）：开合会话覆盖层；有名称、可聚焦，会话列表不被隐藏得无法选择。
        _sessionsToggle.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = "", FontSize = 14 },
                new TextBlock { Text = "会话", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        _sessionsToggle.Visibility = Visibility.Collapsed;
        _sessionsToggle.Click += OnSessionsToggleClick;
        AutomationProperties.SetAutomationId(_sessionsToggle, "LocalSessionsToggleButton");
        AutomationProperties.SetName(_sessionsToggle, "会话列表");
        _toolbar.Children.Add(_sessionsToggle);

        // 三组「标签 + 控件」：宽窗横向排列（与原布局一致）；窄窗整组纵向重排（左对齐、不拉伸）。
        var modelGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        modelGroup.Children.Add(ToolbarLabel("模型"));
        _modelCombo.MinWidth = 220;
        _modelCombo.SelectionChanged += OnModelSelectionChanged;
        AutomationProperties.SetAutomationId(_modelCombo, "LocalModelCombo");
        modelGroup.Children.Add(_modelCombo);
        _toolbar.Children.Add(modelGroup);

        var effortGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        effortGroup.Children.Add(ToolbarLabel("推理强度"));
        _effortCombo.MinWidth = 130;
        _effortCombo.SelectionChanged += OnEffortSelectionChanged;
        AutomationProperties.SetAutomationId(_effortCombo, "LocalEffortCombo");
        effortGroup.Children.Add(_effortCombo);
        _toolbar.Children.Add(effortGroup);

        var modeGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _modeCombo.MinWidth = 110;
        _modeCombo.Items.Add(ModeMarkdown);
        _modeCombo.Items.Add(ModeHtml);
        _modeCombo.SelectedIndex = 0;
        _modeCombo.SelectionChanged += OnModeSelectionChanged;
        AutomationProperties.SetAutomationId(_modeCombo, "LocalRenderModeCombo");
        AutomationProperties.SetName(_modeCombo, "渲染模式");
        modeGroup.Children.Add(_modeCombo);

        _busy.IsActive = false;
        _busy.Visibility = Visibility.Collapsed;
        _busy.Width = 20;
        _busy.Height = 20;
        _busy.VerticalAlignment = VerticalAlignment.Center;
        modeGroup.Children.Add(_busy);
        _toolbar.Children.Add(modeGroup);

        _transcriptHost.Children.Add(_webHost);
        _transcriptHost.Children.Add(_plainView);
        _plainView.Visibility = Visibility.Collapsed;
        _webHost.RenderUnavailable += OnWebViewUnavailable;

        BuildGuidance();
        _transcriptHost.Children.Add(_guidance);

        _input.PlaceholderText = "输入消息，Enter 发送，Shift+Enter 换行";
        _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap;
        _input.MinHeight = 64;
        _input.MaxHeight = 160;
        _input.KeyDown += OnInputKeyDown;
        AutomationProperties.SetAutomationId(_input, "LocalInput");

        _sendButton.Content = "发送";
        _sendButton.MinWidth = 88;
        _sendButton.VerticalAlignment = VerticalAlignment.Bottom;
        _sendButton.Click += OnSendClick;
        AutomationProperties.SetAutomationId(_sendButton, "LocalSendButton");

        var inputGrid = new Grid { Padding = new Thickness(16, 8, 16, 16), ColumnSpacing = 10 };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(_sendButton, 1);
        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(_sendButton);

        _info.IsClosable = true;
        _info.IsOpen = false;
        _info.Severity = InfoBarSeverity.Informational;
        AutomationProperties.SetAutomationId(_info, "LocalInfoBar");

        var bottom = new StackPanel { Spacing = 4 };
        bottom.Children.Add(_info);
        bottom.Children.Add(inputGrid);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_toolbar, 0);
        Grid.SetRow(_transcriptHost, 1);
        Grid.SetRow(bottom, 2);
        grid.Children.Add(_toolbar);
        grid.Children.Add(_transcriptHost);
        grid.Children.Add(bottom);
        return grid;
    }

    private void BuildGuidance()
    {
        var openSettings = new Button { Content = "打开设置" };
        AutomationProperties.SetAutomationId(openSettings, "LocalOpenSettingsButton");
        openSettings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _guidance.Visibility = Visibility.Collapsed;
        _guidance.HorizontalAlignment = HorizontalAlignment.Center;
        _guidance.VerticalAlignment = VerticalAlignment.Center;
        _guidance.Padding = new Thickness(24);
        _guidance.Child = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 460,
            Children =
            {
                new FontIcon
                {
                    Glyph = "",
                    FontSize = 26,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "尚未配置任何 provider 与模型",
                    FontSize = 18,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Local 是 API 客户端：请先在设置中添加 provider（BaseURL/协议/密钥）与模型，再回到此处选择模型开始对话。",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                    TextAlignment = TextAlignment.Center,
                },
                openSettings,
            },
        };
    }

    private static TextBlock ToolbarLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0.8,
        FontSize = 13,
    };
}
