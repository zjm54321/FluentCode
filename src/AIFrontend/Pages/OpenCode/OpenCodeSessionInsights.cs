using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Billion;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>一个概况分区的显示状态；无法确认/失败与“成功但为空”严格分开。</summary>
public enum OpenCodeInsightState
{
    /// <summary>本次尚未读取（打开概况或点击刷新才读取）。</summary>
    NotLoaded,

    /// <summary>正在读取。</summary>
    Loading,

    /// <summary>已读取，数据完整（仍只是采样时刻的快照）。</summary>
    Loaded,

    /// <summary>已读取，但只有部分数据或可能过时；已知数字按“部分”展示。</summary>
    Partial,

    /// <summary>当前条件下不提供（例如插件未确认启用），不是失败。</summary>
    Unavailable,

    /// <summary>读取失败或无法确认；不能当作空结果。</summary>
    Failed,
}

/// <summary>分区结果：Value 仅在 Loaded/Partial（及部分 Unavailable 投影）时有意义；Note 为固定中文说明（不含原始错误体）。</summary>
public sealed record OpenCodeInsightSection<T>(OpenCodeInsightState State, T? Value, string Note)
    where T : class
{
    public static OpenCodeInsightSection<T> NotLoaded { get; } = new(OpenCodeInsightState.NotLoaded, null, "尚未读取");

    public static OpenCodeInsightSection<T> Loading { get; } = new(OpenCodeInsightState.Loading, null, "正在读取…");
}

/// <summary>
/// 一次读取所绑定的完整页面上下文：来源引用 + Revision + 来源代际 + 页面客户端 + 页面/会话代际 + 选中会话 ID。
/// 起发前（来源锁内）与每次 await 之后都必须逐项与页面实时状态一致，否则结果丢弃。
/// </summary>
public sealed record OpenCodeInsightsBinding(
    OpenCodePageSource Source,
    long Revision,
    int SourceGeneration,
    OpenCodeClient Client,
    int PageGeneration,
    int SessionGeneration,
    string SessionId);

/// <summary>费用统计范围：只有核实到根会话后才统计“整个会话树”，绝不把子树当成整段对话。</summary>
public sealed record OpenCodeCostScope(string RootSessionId, bool SelectedIsRoot, int AncestorReads);

/// <summary>Core 累计估算 + 所选会话自身的行（子会话时单列，不另加到总额）。</summary>
public sealed record OpenCodeCostView(
    UsageTreeSummary Summary, OpenCodeCostScope Scope, OpenCodeSessionUsage? SelectedOwn, DateTimeOffset SampledAt);

/// <summary>当前模型目录中的一个价格档位（USD / 百万 token）；派生倍率仅限同一档位。</summary>
public sealed record OpenCodePricingTier(
    int CostIndex, OpenCodeModelCostTier? Tier, double? Input, double? Output, double? CacheRead, double? CacheWrite,
    OpenCodeDerivedCacheMultipliers? Multipliers);

/// <summary>当前模型（来自 Session.Info.model）的目录价目；不自动选择档位、不重算历史费用。</summary>
public sealed record OpenCodeModelPricing(
    string ProviderId, string ModelId, string? Variant, string? Name, long? ContextLimit,
    ImmutableArray<OpenCodePricingTier> Tiers);

public enum OpenCodeBillionPhase
{
    /// <summary>未读取（默认；绝不自动读取）。</summary>
    Idle,

    /// <summary>已有合法地址，但用户尚未确认其与当前来源的关联；未读取 Billion 健康或记录。</summary>
    NeedsConfirmation,

    Loading,

    /// <summary>已取得本会话的数值记录。</summary>
    Loaded,

    /// <summary>本机 Billion 可达，但没有这个会话 ID 的记录。</summary>
    NoRecord,

    /// <summary>本机 Billion 不可达、响应异常或无法确认。</summary>
    Failed,

    MissingOrigin,
    InvalidOrigin,
}

/// <summary>In-memory user selection; health does not establish the OpenCode host association.</summary>
public enum OpenCodeBillionOriginState { Missing, Invalid, Unconfirmed, Confirmed }

/// <summary>Render-time event fence. Forward this unchanged alongside the freshly captured page binding.</summary>
public sealed record OpenCodeBillionContext(OpenCodeInsightsBinding Binding, long OriginGeneration, long RequestEpoch);

/// <summary>Billion 显示状态；只携带连接器的白名单 DTO（无 handoff/摘要/标题等正文字段）。</summary>
public sealed record OpenCodeBillionView(
    OpenCodeBillionPhase Phase, string? SessionId, BillionHealth? Health, BillionSessionStats? Stats, string Note)
{
    /// <summary>Captured origin actually used for this read, never relabeled from a later selection.</summary>
    public string? Origin { get; init; }

    public static OpenCodeBillionView Idle { get; } = new(OpenCodeBillionPhase.Idle, null, null, null,
        "只在你点击“读取本机记录”时读取一次，不会自动刷新。");
}

/// <summary>概况面板的不可变快照；每次状态变化整体替换。</summary>
public sealed record OpenCodeSessionInsightsSnapshot(
    string? SessionId,
    string? UnavailableReason,
    bool Loading,
    DateTimeOffset? SampledAt,
    bool HistoryComplete,
    OpenCodeInsightSection<OpenCodeContextEstimateResult> Context,
    OpenCodeInsightSection<OpenCodeCostView> Cost,
    OpenCodeInsightSection<OpenCodeModelPricing> Pricing,
    OpenCodeInsightSection<OpenCodeCapabilitySnapshot> Capabilities,
    OpenCodeInsightSection<OpenCodeTodoSnapshot> Todo,
    OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot> Children,
    OpenCodeBillionView Billion,
    bool BillionConfirmed)
{
    public string? BillionOrigin { get; init; }
    public OpenCodeBillionOriginState BillionOriginState { get; init; }
    public OpenCodeBillionContext? BillionContext { get; init; }
    public string BillionOriginNote { get; init; } = "打开概况或刷新时通过当前 OpenCode 来源发现地址；也可手动覆盖，不猜测默认端口。";
    public OpenCodeBillionDiscoveryResult? BillionDiscovery { get; init; }
    public bool BillionDiscoveryLoading { get; init; }
    /// <summary>所选会话撤回点之前的 user 消息数；历史或撤回边界未确认时不伪造 0。</summary>
    public int? Turns { get; init; }

    public static OpenCodeSessionInsightsSnapshot Empty(string? sessionId, string? unavailableReason) => new(
        sessionId, unavailableReason, false, null, false,
        OpenCodeInsightSection<OpenCodeContextEstimateResult>.NotLoaded,
        OpenCodeInsightSection<OpenCodeCostView>.NotLoaded,
        OpenCodeInsightSection<OpenCodeModelPricing>.NotLoaded,
        OpenCodeInsightSection<OpenCodeCapabilitySnapshot>.NotLoaded,
        OpenCodeInsightSection<OpenCodeTodoSnapshot>.NotLoaded,
        OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot>.NotLoaded,
        OpenCodeBillionView.Idle, false);
}

/// <summary>
/// 会话概况的读取与投影（无 WinUI 依赖，页面与离线测试共用同一实现）。
/// </summary>
/// <remarks>
/// <para>
/// 只有显式调用 <see cref="LoadAsync"/>（打开概况 / 点击刷新）才读取：Session.Info、受限父链核实、
/// 整树费用、模型目录、MCP/插件目录；子会话与 active 仅在 OMO 服务器插件已核实时读取。
/// 同次显式加载在已核实的会话目录发起一次 Billion 地址元数据 RPC；手动覆盖优先，不自动重试。
/// Billion 只在 <see cref="ReadBillionAsync"/>（单独点击且已确认当前来源的地址）时读取 health + 单会话 detail。
    /// 上下文另由已接纳消息本地更新；每个绑定最多自动读取一次模型目录，不读取费用/能力/Billion。
    /// 本类没有计时器或后台轮询；to do 使用已接纳消息本地派生，配置开关仅在显式加载时读取。
/// </para>
/// <para>
/// 代际：每次读取捕获 <see cref="OpenCodeInsightsBinding"/>，经来源 TryExecute 起发，await 后以
/// “最新请求序号 + 页面绑定”双重复核；<see cref="Reset"/>（换源 / 换会话 / 卸载）取消在途读取并清空结果，
/// 迟到结果不会重新出现，旧请求的 finally 也无权改写新请求的加载状态。
/// </para>
/// </remarks>
public sealed class OpenCodeSessionInsightsController
{
    /// <summary>父链核实的最大读取次数（与 <see cref="OpenCodeSessionAncestry.MaxDepth"/> 一致）。</summary>
    public const int MaxAncestorReads = OpenCodeSessionAncestry.MaxDepth;

    /// <summary>直接子会话列表：每页条数与最多页数（超出即标记不完整）。</summary>
    public const int ChildPageSize = 50;
    public const int MaxChildPages = 10;

    private readonly Func<OpenCodeInsightsBinding, bool> _isCurrentFields;
    private readonly Func<string, BillionContextClient> _createBillionClient;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _billionCts;
    private CancellationTokenSource? _discoveryCts;
    private long _loadId;
    private long _billionId;
    private OpenCodeInsightsBinding? _binding;
    private OpenCodePageSource? _originSource;
    private long _originRevision;
    private long _originGeneration;
    private string? _origin;
    private OpenCodeBillionOriginState _originState;
    private bool _originManuallyChosen;
    private OpenCodeBillionDiscoveryResult? _discovery;
    private bool _discoveryLoading;
    private OpenCodeMessage[] _contextMessages = Array.Empty<OpenCodeMessage>();
    private bool _contextHistoryComplete;
    private bool _contextHasMessages;
    private OpenCodeTodoHistory _todoHistory = OpenCodeTodoHistory.NoRevert;
    private OpenCodeTodoConfig _todoConfig = OpenCodeTodoConfig.Unknown;
    private OpenCodeModelRef? _contextModel;
    private string? _contextDirectory;
    private OpenCodeModelInfo[]? _contextModels;
    private OpenCodeModelInfo? _contextModelInfo;
    private bool _contextCatalogStarted;
    private CancellationTokenSource? _contextCts;
    private long _contextCatalogVersion;
    private long _contextModelVersion;
    private double _contextSwitchCreated;
    private string? _contextSwitchId;
    private double _contextLatestModelCreated;
    // A same-session accepted-history reset clears usage, not an already initialized catalog.
    // Keep at most the last binding; switching source/session cannot reuse it.
    private OpenCodeInsightsBinding? _retainedContextCatalogBinding;
    private string? _retainedContextDirectory;
    private OpenCodeModelInfo[]? _retainedContextModels;
    private bool _retainedContextCatalogStarted;

    /// <param name="isCurrentFields">
    /// 纯字段比较（可在来源锁内调用，不得访问 App、不得 await）：页面存活、来源引用/Revision/来源代际、
    /// 客户端实例、页面/会话代际与选中会话 ID 是否仍与 binding 一致。
    /// </param>
    /// <param name="createBillionClient">接收不可变规范化地址的工厂（每次点击新建并释放；不得读取可变 UI 地址）。</param>
    public OpenCodeSessionInsightsController(
        Func<OpenCodeInsightsBinding, bool> isCurrentFields, Func<string, BillionContextClient>? createBillionClient = null)
    {
        _isCurrentFields = isCurrentFields ?? throw new ArgumentNullException(nameof(isCurrentFields));
        _createBillionClient = createBillionClient ?? (origin => new BillionContextClient(origin));
        Snapshot = OpenCodeSessionInsightsSnapshot.Empty(null, null);
    }

    public OpenCodeSessionInsightsSnapshot Snapshot { get; private set; }

    /// <summary>快照替换后触发（在调用 LoadAsync/ReadBillionAsync 的同步上下文上）。</summary>
    public event EventHandler? Changed;

    // Read-through only: share the existing fenced Model.Info catalog, never fetch or infer levels.
    internal OpenCodeModelInfo? CurrentModelInfo(OpenCodeInsightsBinding binding, OpenCodeModelRef model)
        => OwnsContext(binding) && _contextModels?.Where(m => m.Id == model.Id && m.ProviderId == model.ProviderId).ToArray()
            is { Length: 1 } matches ? matches[0] : null;

    /// <summary>OpenCode 读取起发次数（离线证据：Reset/换代后无迟到起发）。</summary>
    public int OpenCodeReadsStarted { get; private set; }

    /// <summary>Billion 请求起发次数（离线证据：没有未经点击的读取）。</summary>
    public int BillionReadsStarted { get; private set; }
    /// <summary>Metadata attempts only (unsupported local association may return without HTTP).</summary>
    public int BillionDiscoveryAttemptsStarted { get; private set; }

    /// <summary>模型元数据读取起点的栅栏；晚于它的已接纳模型切换优先于旧响应。</summary>
    public long ContextMetadataVersion => _contextModelVersion;

    /// <summary>只更新本地上下文参考。调用方已核实 binding、历史投影及消息顺序；无网络/发现/费用读取。</summary>
    public void UpdateContext(OpenCodeInsightsBinding binding, IReadOnlyList<OpenCodeMessage> orderedMessages, bool historyComplete,
        OpenCodeTodoHistory? todoHistory = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(orderedMessages);
        if (!IsCurrent(binding)) return;
        AdoptBinding(binding);
        bool changed = false;
        binding.Source.TryExecute(CancellationToken.None, () =>
        {
            if (!OwnsContext(binding)) return;
            CaptureContextMessages(orderedMessages, historyComplete);
            if (todoHistory is not null) _todoHistory = todoHistory;
            changed = SetContextSnapshot();
            if (todoHistory is not null)
            {
                _todoHistory = todoHistory;
                var scope = Snapshot.Todo.Value?.Scope ?? new OpenCodeCapabilityScope(binding.Source.Identity,
                    binding.Revision, _contextDirectory, DateTimeOffset.UtcNow);
                var todo = BuildTodo(scope);
                var before = Snapshot.Todo.Value;
                var after = todo.Value!;
                if (before is null || before.State != after.State || before.Complete != after.Complete
                    || before.SourceMessageId != after.SourceMessageId || before.Reason != after.Reason
                    || before.Capability != after.Capability || !before.Items.SequenceEqual(after.Items))
                {
                    var capabilities = Snapshot.Capabilities.Value;
                    SetSnapshot(Snapshot with
                    {
                        Todo = todo,
                        Capabilities = capabilities is null ? Snapshot.Capabilities
                            : Snapshot.Capabilities with { Value = capabilities with { Todo = after.Capability } },
                    });
                    changed = true;
                }
            }
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 复用页面已有的权威 SessionControlState 模型/目录。每绑定最多一次模型目录初始化；后续消息更新为零 HTTP。
    /// 显式概况加载仍可刷新缓存目录；目录/模型响应始终用最新已接纳消息重算，不恢复读取起点的 usage。
    /// </summary>
    public async Task UpdateContextMetadataAsync(OpenCodeInsightsBinding binding, OpenCodeModelRef? model, string directory,
        long expectedVersion, CancellationToken outer)
    {
        if (outer.IsCancellationRequested || !IsCurrent(binding)) return;
        AdoptBinding(binding);
        bool changed = false;
        binding.Source.TryExecute(outer, () =>
        {
            if (!OwnsContext(binding) || expectedVersion != _contextModelVersion) return;
            SetContextModel(model, directory);
            changed = SetContextSnapshot();
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        if (!OwnsContext(binding) || _contextCatalogStarted || string.IsNullOrWhiteSpace(_contextDirectory)) return;
        _contextCatalogStarted = true; // Includes failed/cancelled attempts: no per-publication retries.
        var location = _contextDirectory;
        var version = _contextCatalogVersion;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer, binding.Source.RevocationToken);
        _contextCts = cts;
        Task<OpenCodeModelsResponse>? pending = null;
        try
        {
            binding.Source.TryExecute(cts.Token, () =>
            {
                if (OwnsContext(binding)) pending = binding.Client.ListModelsAsync(location, cts.Token);
            });
            if (pending is null) return;
            OpenCodeReadsStarted++;
            var models = await pending;
            changed = false;
            binding.Source.TryExecute(cts.Token, () =>
            {
                if (!OwnsContext(binding) || version != _contextCatalogVersion || location != _contextDirectory) return;
                _contextModels = models.Data?.ToArray();
                MatchContextModel();
                SetContextSnapshot();
                changed = true; // Catalog consumers (effort gauge) also need changes without a context-limit change.
            });
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { /* Keep a truthful limit-unavailable reference; explicit refresh may refine it. */ }
        finally
        {
            if (ReferenceEquals(_contextCts, cts)) _contextCts = null;
        }
    }

    private bool OwnsContext(OpenCodeInsightsBinding binding)
        => _binding is { } owned && SameScope(owned, binding) && IsCurrent(binding);

    private void CaptureContextMessages(IReadOnlyList<OpenCodeMessage> messages, bool complete)
    {
        OpenCodeMessage? latestSwitch = null;
        double latestModelCreated = _contextLatestModelCreated;
        foreach (var message in messages)
        {
            if (message?.Model is null || message.Time?.Created is not > 0) continue;
            if (message.Type is OpenCodeMessageTypes.Assistant or OpenCodeMessageTypes.ModelSwitched)
                latestModelCreated = Math.Max(latestModelCreated, message.Time.Created);
            if (message.Type == OpenCodeMessageTypes.ModelSwitched
                && (latestSwitch is null || message.Time.Created >= latestSwitch.Time!.Created)) latestSwitch = message;
        }
        if (latestSwitch is not null && latestSwitch.Time!.Created >= _contextSwitchCreated
            && latestSwitch.Id != _contextSwitchId)
        {
            // The initial window may contain switches older than authoritative Session.Info.
            if (_contextHasMessages && latestSwitch.Time.Created >= _contextLatestModelCreated)
            {
                _contextModel = latestSwitch.Model;
                _contextModelVersion++;
                MatchContextModel();
            }
            _contextSwitchCreated = latestSwitch.Time.Created;
            _contextSwitchId = latestSwitch.Id;
        }
        _contextMessages = messages.ToArray();
        _contextLatestModelCreated = latestModelCreated;
        _contextHistoryComplete = complete;
        _contextHasMessages = true;
    }

    private void SetContextModel(OpenCodeModelRef? model, string? directory)
    {
        _contextModel = model;
        _contextModelVersion++;
        if (_contextDirectory != directory)
        {
            _contextDirectory = directory;
            _contextModels = null;
            _contextCatalogVersion++;
        }
        MatchContextModel();
    }

    private void MatchContextModel()
    {
        var matches = _contextModels?.Where(model => model.Id == _contextModel?.Id
            && model.ProviderId == _contextModel?.ProviderId).ToArray();
        _contextModelInfo = matches is { Length: 1 } ? matches[0] : null;
    }

    private OpenCodeInsightSection<OpenCodeContextEstimateResult> BuildContext()
    {
        var estimate = OpenCodeContextEstimate.Estimate(_binding!.SessionId, ContextMessagesBeforeRevert(),
            _contextModel, _contextModelInfo, _contextHistoryComplete);
        return new(estimate.ReferenceTokens is not null && !estimate.Stale ? OpenCodeInsightState.Loaded : OpenCodeInsightState.Partial,
            estimate, estimate.Label);
    }

    // Official v2.1.1 yr: context never uses a message at/after revert; a missing boundary
    // is unavailable, not a license to reuse usage from the reverted suffix.
    private OpenCodeMessage[] ContextMessagesBeforeRevert()
    {
        if (!_todoHistory.RevertKnown || _todoHistory.ReadFailed) return Array.Empty<OpenCodeMessage>();
        if (_todoHistory.RevertMessageId is not { } revert) return _contextMessages;
        var index = Array.FindIndex(_contextMessages, message => message.Id == revert);
        return index < 0 ? Array.Empty<OpenCodeMessage>() : _contextMessages.Take(index).ToArray();
    }

    private int? ContextTurns() => !_contextHasMessages || !_contextHistoryComplete
        || !_todoHistory.RevertKnown || _todoHistory.ReadFailed
        || (_todoHistory.RevertMessageId is { } revert && !_contextMessages.Any(message => message.Id == revert))
        ? null : ContextMessagesBeforeRevert().Where(message => message.Type == OpenCodeMessageTypes.User)
            .Select(message => message.Id).Distinct(StringComparer.Ordinal).Count();

    private bool SetContextSnapshot()
    {
        var context = BuildContext();
        var before = Snapshot.Context.Value;
        var after = context.Value!;
        var turns = ContextTurns();
        if (Snapshot.Context.State == context.State && before is not null
            && before.CurrentModel == after.CurrentModel && before.LatestAssistantUsage == after.LatestAssistantUsage
            && before.ContextLimit == after.ContextLimit && before.StaleReasons.SequenceEqual(after.StaleReasons)
            && Snapshot.HistoryComplete == _contextHistoryComplete && Snapshot.Turns == turns) return false;
        SetSnapshot(Snapshot with { Context = context, HistoryComplete = _contextHistoryComplete, Turns = turns });
        return true; // Text-only deltas retain inputs without rebuilding the header/flyout/insights view.
    }

    private void ClearContext()
    {
        _todoHistory = OpenCodeTodoHistory.NoRevert;
        _todoConfig = OpenCodeTodoConfig.Unknown;
        CancelAndForget(ref _contextCts);
        _contextCatalogVersion++;
        _contextMessages = Array.Empty<OpenCodeMessage>();
        _contextHasMessages = false;
        _contextHistoryComplete = false;
        _contextModel = null;
        _contextDirectory = null;
        _contextModels = null;
        _contextModelInfo = null;
        _contextCatalogStarted = false;
        _contextSwitchCreated = 0;
        _contextSwitchId = null;
        _contextLatestModelCreated = 0;
    }

    /// <summary>当前快照是否已属于该 binding 且已有采样结果或正在读取（打开概况时据此决定是否读取）。</summary>
    public bool HasResultFor(OpenCodeInsightsBinding binding)
        => _binding is { } owned && SameScope(owned, binding) && (Snapshot.Loading || Snapshot.SampledAt is not null);

    /// <summary>换源 / 换会话 / 卸载：取消全部在途读取，清空结果与 Billion 记录；迟到结果一律丢弃。</summary>
    /// <param name="clearBillionOrigin">
    /// 来源生命周期变化时设为 true：即使新来源没有选中会话、无法创建完整 binding，也撤销私有地址与确认。
    /// 同来源的会话切换保留默认 false，仅清结果、不清已确认的地址。
    /// </param>
    public void Reset(string? sessionId = null, string? unavailableReason = null, bool clearBillionOrigin = false)
    {
        if (_binding is not null && !clearBillionOrigin)
        {
            _retainedContextCatalogBinding = _binding;
            _retainedContextDirectory = _contextDirectory;
            _retainedContextModels = _contextModels;
            _retainedContextCatalogStarted = _contextCatalogStarted;
        }
        else if (clearBillionOrigin)
        {
            _retainedContextCatalogBinding = null;
            _retainedContextModels = null;
            _retainedContextDirectory = null;
            _retainedContextCatalogStarted = false;
        }
        _contextModelVersion++;
        ClearContext();
        _loadId++;
        _billionId++;
        if (clearBillionOrigin)
        {
            _originGeneration++;
            _originSource = null;
            _originRevision = 0;
            _origin = null;
            _originState = OpenCodeBillionOriginState.Missing;
            _originManuallyChosen = false;
        }
        CancelDiscovery(clearResult: true);
        CancelAndForget(ref _loadCts);
        CancelAndForget(ref _billionCts);
        _binding = null;
        Publish(OpenCodeSessionInsightsSnapshot.Empty(sessionId, unavailableReason));
    }

    /// <summary>
    /// Bind a current page scope without any HTTP. The renderer must retain Snapshot.BillionContext.
    /// </summary>
    public OpenCodeBillionContext? BindBillionContext(OpenCodeInsightsBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsCurrent(binding)) return null;
        AdoptBinding(binding);
        return Snapshot.BillionContext;
    }

    /// <summary>Every accepted edit/commit revokes consent and clears results immediately, even for the same origin.</summary>
    public bool SetBillionOrigin(OpenCodeInsightsBinding binding, OpenCodeBillionContext renderedContext, string? candidate)
    {
        if (!MatchesBillionContext(binding, renderedContext)) return false;
        _originGeneration++;
        _billionId++;
        _origin = null;
        _originManuallyChosen = !string.IsNullOrWhiteSpace(candidate);
        _originState = string.IsNullOrWhiteSpace(candidate) ? OpenCodeBillionOriginState.Missing
            : BillionContextClient.TryNormalizeOrigin(candidate, out _origin)
                ? OpenCodeBillionOriginState.Unconfirmed : OpenCodeBillionOriginState.Invalid;
        CancelDiscovery(clearResult: true);
        CancelAndForget(ref _billionCts);
        Publish(Snapshot with { Billion = OpenCodeBillionView.Idle });
        return true;
    }

    /// <summary>Missing render context fails closed; old Page callers remain source-compatible but cannot grant consent.</summary>
    public bool SetBillionConfirmed(OpenCodeInsightsBinding binding, bool confirmed, OpenCodeBillionContext? renderedContext = null)
    {
        if (!MatchesBillionContext(binding, renderedContext) || _origin is null) return false;
        _billionId++;
        _originState = confirmed ? OpenCodeBillionOriginState.Confirmed : OpenCodeBillionOriginState.Unconfirmed;
        CancelDiscovery(clearResult: false);
        CancelAndForget(ref _billionCts);
        Publish(Snapshot with { Billion = OpenCodeBillionView.Idle });
        return true;
    }

    public bool IsBillionConfirmed(OpenCodeInsightsBinding binding)
        => OwnsOrigin(binding) && _origin is not null && _originState == OpenCodeBillionOriginState.Confirmed
        && !binding.Source.IsRevoked;

    /// <summary>
    /// 显式读取（打开 / 刷新）。<paramref name="orderedMessages"/> 必须是该会话已加载的、已净化的消息（旧→新）；
    /// <paramref name="historyComplete"/> 仅在分页链正常结束且无缺口时为 true。
    /// </summary>
    public async Task LoadAsync(
        OpenCodeInsightsBinding binding, IReadOnlyList<OpenCodeMessage> orderedMessages, bool historyComplete,
        CancellationToken outer, OpenCodeTodoHistory? todoHistory = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(orderedMessages);
        todoHistory ??= OpenCodeTodoHistory.NoRevert;
        if (!IsCurrent(binding))
        {
            return;
        }

        AdoptBinding(binding);
        var id = ++_loadId;
        // Capture BEFORE any await/publish callback: a later origin edit/confirmation must win over this load.
        var discoveryContext = Snapshot.BillionContext;
        CancelDiscovery(clearResult: false);
        CancelAndForget(ref _loadCts);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _loadCts = cts;
        var ct = cts.Token;
        var messages = orderedMessages.ToArray(); // 起发时快照：之后的轮询不改变本次投影输入。
        CaptureContextMessages(messages, historyComplete);
        _todoHistory = todoHistory;
        var contextModelVersion = _contextModelVersion;

        // 刷新时保留上次结果（仍标注采样时间），只在从未读取过的分区显示“读取中”。
        Publish(Snapshot with
        {
            Loading = true,
            UnavailableReason = null,
            Context = Keep(Snapshot.Context),
            Cost = Keep(Snapshot.Cost),
            Pricing = Keep(Snapshot.Pricing),
            Capabilities = Keep(Snapshot.Capabilities),
            Todo = Keep(Snapshot.Todo),
            Children = Keep(Snapshot.Children),
        });

        Task discoveryTask = Task.CompletedTask;
        try
        {
            // 1) 当前会话信息：目录（模型目录/插件的位置）、当前模型、父会话。
            var infoRead = await ReadAsync(id, binding, ct, () => binding.Client.GetSessionInfoAsync(binding.SessionId, ct));
            if (infoRead.Lost) return;
            var info = infoRead.Value;
            var directory = info?.Location?.Directory is { Length: > 0 } dir ? dir : null;
            discoveryTask = DiscoverForLoadAsync(id, binding, discoveryContext, directory, ct);

            // 2) 费用范围：受限父链核实（精确 GetSessionInfo，逐级向上；环/失败/超限 = 无法确认，不统计）。
            string? root = null;
            var ancestorReads = 0;
            string? rootFailure = info is null ? "无法读取当前会话信息，未统计费用。" : null;
            if (info is not null)
            {
                if (info.ParentId is null)
                {
                    root = binding.SessionId;
                }
                else
                {
                    var visited = new HashSet<string>(StringComparer.Ordinal) { binding.SessionId };
                    var current = info.ParentId;
                    while (true)
                    {
                        if (!visited.Add(current))
                        {
                            rootFailure = "父会话链出现循环，无法确认主会话；未统计费用，以免把子树当成整段对话。";
                            break;
                        }

                        if (ancestorReads >= MaxAncestorReads)
                        {
                            rootFailure = "父会话层级超过读取上限，无法确认主会话；未统计费用。";
                            break;
                        }

                        ancestorReads++;
                        var parentId = current;
                        var parentRead = await ReadAsync(id, binding, ct, () => binding.Client.GetSessionInfoAsync(parentId, ct));
                        if (parentRead.Lost) return;
                        if (parentRead.Value is not { } parent)
                        {
                            rootFailure = "读取父会话失败，无法确认主会话；未统计费用，以免把子树当成整段对话。";
                            break;
                        }

                        if (parent.ParentId is null)
                        {
                            root = parentId;
                            break;
                        }

                        current = parent.ParentId;
                    }
                }
            }

            // 3) 相互独立的只读请求：整树费用、模型目录、MCP、插件。每个都经来源 TryExecute 起发。
            var treeTask = root is null ? null
                : StartRead(id, binding, ct, () => OpenCodeUsageService.ReadTreeAsync(binding.Client, root, null, ct,
                    action => TryStartLoad(id, binding, ct, action)));
            var modelsTask = directory is null ? null
                : StartRead(id, binding, ct, () => binding.Client.ListModelsAsync(directory, ct));
            var mcpTask = directory is null ? null
                : StartRead(id, binding, ct, () => binding.Client.GetMcpServersAsync(directory, ct));
            var pluginsTask = directory is null ? null
                : StartRead(id, binding, ct, () => binding.Client.GetPluginsAsync(directory, ct));
            // 原生 to do 开关（experimental.todo）；只读、与其它只读请求同源同代际。
            var todoConfigTask = directory is null ? null
                : StartRead(id, binding, ct, async () => (bool?)await binding.Client.GetExperimentalTodoEnabledAsync(directory, ct));

            var tree = await AwaitOptionalAsync(treeTask, wanted: root is not null);
            var models = await AwaitOptionalAsync(modelsTask, wanted: directory is not null);
            var mcp = await AwaitOptionalAsync(mcpTask, wanted: directory is not null);
            var plugins = await AwaitOptionalAsync(pluginsTask, wanted: directory is not null);
            var todoConfig = await AwaitOptionalAsync(todoConfigTask, wanted: directory is not null);
            if (tree.Lost || models.Lost || mcp.Lost || plugins.Lost || todoConfig.Lost || !IsOwner(id, binding)) return;

            var sampledAt = DateTimeOffset.UtcNow;
            var cost = BuildCost(binding.SessionId, root, ancestorReads, rootFailure, tree, sampledAt);
            var (pricing, _) = BuildPricing(info, models, directory);
            var scope = new OpenCodeCapabilityScope(binding.Source.Identity, binding.Revision, directory, sampledAt);
            var capabilities = OpenCodeCapabilityStatus.Create(scope,
                directory is null ? OpenCodeCapabilityRead<OpenCodeMcpServer>.NotObserved() : ToRead(mcp),
                directory is null ? OpenCodeCapabilityRead<OpenCodePluginInfo>.NotObserved() : ToRead(plugins));
            var capabilitySection = new OpenCodeInsightSection<OpenCodeCapabilitySnapshot>(
                capabilities.Complete ? OpenCodeInsightState.Loaded : OpenCodeInsightState.Partial, capabilities,
                directory is null ? "无法确认会话目录，MCP / 插件状态未查询。"
                    : capabilities.Complete ? "采样时刻的公开状态目录。" : "部分状态未能确认；未确认项不代表未安装或未连接。");

            // 4) 上下文参考 / Todo：只复用已加载消息；历史未取全时如实标记。
            if (info is not null && contextModelVersion == _contextModelVersion) SetContextModel(info.Model, directory);
            if (directory == _contextDirectory && models.Value?.Data is { } catalog)
            {
                _contextModels = catalog.ToArray();
                MatchContextModel();
                _contextCatalogStarted = true;
                _contextCatalogVersion++; // An older automatic catalog completion cannot replace this explicit read.
            }
            var context = BuildContext(); // Latest accepted usage, never the old explicit-load input snapshot.
            // 官方原生 to do：最新已完成 todowrite（撤回点之前）；不再由插件能力门控，也不回退插件来源。
            _todoConfig = todoConfig.Value switch
                {
                    true => OpenCodeTodoConfig.Enabled,
                    false => OpenCodeTodoConfig.Disabled,
                    null => OpenCodeTodoConfig.Unknown,
                };
            // A message/revert publication received during the HTTP read always wins over its starting input.
            var todo = BuildTodo(scope);
            capabilities = capabilities with { Todo = todo.Value!.Capability };
            capabilitySection = capabilitySection with { Value = capabilities };

            var omoAvailable = capabilities.Omo.Availability == OpenCodeCapabilityAvailability.Available;
            Publish(Snapshot with
            {
                SampledAt = sampledAt,
                HistoryComplete = _contextHistoryComplete,
                Turns = ContextTurns(),
                Context = context,
                Cost = cost,
                Pricing = pricing,
                Capabilities = capabilitySection,
                Todo = todo,
                Children = omoAvailable ? OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot>.Loading : Snapshot.Children,
            });

            // 5) 子会话状态：仅 OMO 服务器插件已核实时读取直接子会话 + 同源 active 快照。
            OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot> children;
            if (!omoAvailable)
            {
                var projected = OpenCodeSubagentStatus.ProjectChildren(
                    binding.SessionId, Array.Empty<OpenCodeSessionInfo>(), false, null, false, capabilities);
                children = new(capabilities.Omo.Availability == OpenCodeCapabilityAvailability.Unknown
                    ? OpenCodeInsightState.Failed : OpenCodeInsightState.Unavailable, projected, projected.Reason);
            }
            else
            {
                var rows = new List<OpenCodeSessionInfo>();
                var childrenComplete = false;
                string? cursor = null;
                var used = new HashSet<string>(StringComparer.Ordinal);
                for (var page = 0; page < MaxChildPages; page++)
                {
                    var query = new OpenCodeSessionListQuery
                    {
                        ParentFilter = OpenCodeSessionParentFilter.Children,
                        ParentId = binding.SessionId,
                        Limit = ChildPageSize,
                        Order = cursor is null ? OpenCodeSessionOrders.Desc : null,
                        Cursor = cursor,
                    };
                    var pageRead = await ReadAsync(id, binding, ct, () => binding.Client.ListSessionsAsync(query, ct));
                    if (pageRead.Lost) return;
                    if (pageRead.Value?.Data is not { } data) break; // 失败：不完整，绝不当作“没有子会话”。
                    if (data.Count == 0) { childrenComplete = true; break; }
                    rows.AddRange(data);
                    var next = pageRead.Value.Cursor?.Next;
                    if (next is null) { childrenComplete = true; break; }
                    if (next.Length == 0 || !used.Add(next)) break; // 重复/异常游标：停止并标记不完整。
                    cursor = next;
                }

                var activeRead = await ReadAsync(id, binding, ct, () => binding.Client.GetActiveSessionsAsync(ct));
                if (activeRead.Lost) return;
                IReadOnlyCollection<string>? active = activeRead.Value?.Data?.Keys.ToArray();
                var projected = OpenCodeSubagentStatus.ProjectChildren(
                    binding.SessionId, rows, childrenComplete, active, active is not null, capabilities);
                children = new(projected.Complete ? OpenCodeInsightState.Loaded : OpenCodeInsightState.Partial,
                    projected, projected.Reason);
            }

            await discoveryTask;
            if (!IsOwner(id, binding)) return;
            Publish(Snapshot with { Loading = false, Children = children });
        }
        catch (OperationCanceledException)
        {
            // 被 Reset / 新请求 / 来源撤销取消：新所有者负责状态，这里不写任何东西。
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
            }

            cts.Dispose();

            // 意外异常时仍由“当前所有者”收尾；旧请求不能清掉新请求的加载中状态。
            if (Snapshot.Loading && IsOwner(id, binding))
            {
                Publish(Snapshot with
                {
                    Loading = false,
                    Context = Settle(Snapshot.Context),
                    Cost = Settle(Snapshot.Cost),
                    Pricing = Settle(Snapshot.Pricing),
                    Capabilities = Settle(Snapshot.Capabilities),
                    Todo = Settle(Snapshot.Todo),
                    Children = Settle(Snapshot.Children),
                });
            }
        }
    }

    /// <summary>
    /// 单独点击“读取本机记录”：先 health，后所选会话 detail。须先选择地址并确认其与当前来源的关联；
    /// 不转发任何 OpenCode 凭据，不读取配置/会话枚举，数字不与 Core 费用合并。
    /// 缺少渲染时上下文时不读取；不得在事件到达后用新页面上下文替代旧渲染上下文。
    /// </summary>
    public async Task ReadBillionAsync(OpenCodeInsightsBinding binding, CancellationToken outer,
        OpenCodeBillionContext? renderedContext = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!MatchesBillionContext(binding, renderedContext))
        {
            return;
        }

        if (!IsBillionConfirmed(binding))
        {
            Publish(Snapshot with
            {
                Billion = new(_originState switch
                {
                    OpenCodeBillionOriginState.Missing => OpenCodeBillionPhase.MissingOrigin,
                    OpenCodeBillionOriginState.Invalid => OpenCodeBillionPhase.InvalidOrigin,
                    _ => OpenCodeBillionPhase.NeedsConfirmation,
                }, binding.SessionId, null, null, OriginNote()),
            });
            return;
        }

        var selection = new BillionSelection(_origin!, _originGeneration);
        var id = ++_billionId;
        CancelDiscovery(clearResult: false);
        CancelAndForget(ref _billionCts);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _billionCts = cts;
        var ct = cts.Token;
        Publish(Snapshot with { Billion = new(OpenCodeBillionPhase.Loading, binding.SessionId, null, null, "正在读取本机 Billion…")
            { Origin = selection.Origin } });

        BillionContextClient? client = null;
        try
        {
            if (!IsBillionOwner(id, binding, selection) || ct.IsCancellationRequested) return;
            client = _createBillionClient(selection.Origin);
            if (!string.Equals(client.Origin, selection.Origin, StringComparison.Ordinal))
                throw new ArgumentException("Billion client factory did not use the captured origin.", "baseAddress");
            var healthTask = StartBillionRead(id, binding, selection, ct, () => client.GetHealthAsync(ct));
            if (healthTask is null) return;
            var health = await healthTask;
            if (!IsBillionOwner(id, binding, selection)) return;
            if (!health.Available)
            {
                Publish(Snapshot with
                {
                    Billion = new(OpenCodeBillionPhase.Failed, binding.SessionId, null, null,
                        "本机 Billion 不可达或未就绪；没有读取会话记录。") { Origin = selection.Origin },
                });
                return;
            }

            var detailTask = StartBillionRead(id, binding, selection, ct, () => client.GetSessionDetailAsync(binding.SessionId, ct));
            if (detailTask is null) return;
            var stats = await detailTask;
            if (!IsBillionOwner(id, binding, selection)) return;
            var view = stats.Status switch
            {
                BillionSessionStatus.Available => new OpenCodeBillionView(OpenCodeBillionPhase.Loaded, binding.SessionId, health, stats,
                    "本机 Billion 按同一会话 ID 记录的数值；与上方 Core 费用分开统计，两者不能相加。"),
                BillionSessionStatus.NoRecord => new OpenCodeBillionView(OpenCodeBillionPhase.NoRecord, binding.SessionId, health, null,
                    "本机 Billion 没有这个会话 ID 的记录：该会话可能没有经它转发，或两边的会话 ID 不同。"),
                BillionSessionStatus.Malformed => new OpenCodeBillionView(OpenCodeBillionPhase.Failed, binding.SessionId, health, null,
                    "本机 Billion 的响应无法识别，或记录不属于这个会话 ID；未显示任何数字。"),
                _ => new OpenCodeBillionView(OpenCodeBillionPhase.Failed, binding.SessionId, health, null,
                    "本机 Billion 暂时无法提供该会话记录。"),
            };
            Publish(Snapshot with { Billion = view with { Origin = selection.Origin } });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException error)
        {
            if (IsBillionOwner(id, binding, selection))
            {
                Publish(Snapshot with
                {
                    Billion = new(OpenCodeBillionPhase.Failed, binding.SessionId, null, null,
                        error.ParamName == "sessionId" ? "该会话 ID 不能用于本机 Billion 查询。"
                            : "无法为所选 Billion 地址创建连接；未显示会话记录。") { Origin = selection.Origin },
                });
            }
        }
        finally
        {
            client?.Dispose();
            if (ReferenceEquals(_billionCts, cts))
            {
                _billionCts = null;
            }

            cts.Dispose();
            if (Snapshot.Billion.Phase == OpenCodeBillionPhase.Loading && IsBillionOwner(id, binding, selection))
            {
                Publish(Snapshot with
                {
                    Billion = new(OpenCodeBillionPhase.Failed, binding.SessionId, null, null, "本机 Billion 读取未完成。")
                        { Origin = selection.Origin },
                });
            }
        }
    }

    // ------------------------------------------------------------------

    private bool IsDiscoveryOwner(long loadId, OpenCodeInsightsBinding binding, OpenCodeBillionContext? expected)
        => IsOwner(loadId, binding) && MatchesBillionContext(binding, expected) && !_originManuallyChosen;

    /// <summary>Uses the Session.Info directory already read by this exact LoadAsync, never a host filesystem guess.
    /// Every network start/application is source-fenced; Changed and cancellation callbacks run outside that lock.</summary>
    private async Task DiscoverForLoadAsync(long loadId, OpenCodeInsightsBinding binding,
        OpenCodeBillionContext? expected, string? directory, CancellationToken outer)
    {
        if (!IsDiscoveryOwner(loadId, binding, expected)) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer, binding.Source.RevocationToken);
        _discoveryCts = cts;
        var ct = cts.Token;
        _discoveryLoading = true;
        Publish(Snapshot);
        try
        {
            Task<OpenCodeBillionDiscoveryResult>? pending = null;
            binding.Source.TryExecute(ct, () =>
            {
                if (!IsDiscoveryOwner(loadId, binding, expected)) return;
                BillionDiscoveryAttemptsStarted++;
                pending = directory is null
                    ? Task.FromResult(new OpenCodeBillionDiscoveryResult(OpenCodeBillionDiscoveryStatus.InvalidLocation))
                    : binding.Client.DiscoverBillionOriginAsync(directory, ct);
            });
            if (pending is null) return;
            var result = await pending;
            ApplyDiscovery(loadId, binding, expected, result, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is OpenCodeException or ArgumentException or InvalidOperationException)
        {
            ApplyDiscovery(loadId, binding, expected, new(OpenCodeBillionDiscoveryStatus.TransportUnavailable), ct);
        }
        finally
        {
            if (ReferenceEquals(_discoveryCts, cts))
            {
                _discoveryCts = null;
                if (_discoveryLoading)
                    ApplyDiscovery(loadId, binding, expected, new(OpenCodeBillionDiscoveryStatus.Cancelled), CancellationToken.None);
            }
            cts.Dispose();
        }
    }

    private void ApplyDiscovery(long loadId, OpenCodeInsightsBinding binding, OpenCodeBillionContext? expected,
        OpenCodeBillionDiscoveryResult result, CancellationToken ct)
    {
        bool applied = false;
        CancellationTokenSource? oldRead = null;
        binding.Source.TryExecute(ct, () =>
        {
            if (!IsDiscoveryOwner(loadId, binding, expected)) return;
            _discovery = result;
            _discoveryLoading = false;
            if (result.Status == OpenCodeBillionDiscoveryStatus.Ready)
            {
                if (!BillionContextClient.TryNormalizeOrigin(result.Origin, out var origin))
                    _discovery = new(OpenCodeBillionDiscoveryStatus.Malformed);
                else if (_origin != origin)
                {
                    _origin = origin;
                    _originState = OpenCodeBillionOriginState.Unconfirmed; // Metadata is NEVER consent.
                    _originGeneration++;
                    _billionId++;
                    oldRead = _billionCts;
                    _billionCts = null;
                    SetSnapshot(Snapshot with { Billion = OpenCodeBillionView.Idle });
                }
                // Equal automatic origin preserves existing consent/result and their epoch.
            }
            SetSnapshot(Snapshot); // Update metadata/note without emitting a callback under Source.TryExecute.
            applied = true;
        });
        try { oldRead?.Cancel(); } catch (ObjectDisposedException) { }
        if (applied) Changed?.Invoke(this, EventArgs.Empty); // Renderer reads the latest snapshot, not a saved one.
    }

    private void CancelDiscovery(bool clearResult)
    {
        CancelAndForget(ref _discoveryCts);
        _discoveryLoading = false;
        if (clearResult) _discovery = null;
    }

    private readonly record struct ReadResult<T>(T? Value, bool Lost, OpenCodeException? Error);

    private static OpenCodeInsightSection<T> Keep<T>(OpenCodeInsightSection<T> section) where T : class
        => section.Value is null && section.State is OpenCodeInsightState.NotLoaded ? OpenCodeInsightSection<T>.Loading : section;

    private static OpenCodeInsightSection<T> Settle<T>(OpenCodeInsightSection<T> section) where T : class
        => section.State == OpenCodeInsightState.Loading ? new(OpenCodeInsightState.Failed, null, "读取未完成，无法确认。") : section;

    private async Task<ReadResult<T>> ReadAsync<T>(long id, OpenCodeInsightsBinding binding, CancellationToken ct, Func<Task<T>> read)
    {
        var result = await AwaitAsync(StartRead(id, binding, ct, read));
        return result.Lost || !IsOwner(id, binding) ? new(default, true, null) : result;
    }

    /// <summary>树内每次 GET 也使用此边界；不增加面板的逻辑读取计数。</summary>
    private bool TryStartLoad(long id, OpenCodeInsightsBinding binding, CancellationToken ct, Action read)
    {
        var started = false;
        return binding.Source.TryExecute(ct, () =>
        {
            if (!IsOwner(id, binding)) return;
            read();
            started = true;
        }) && started;
    }

    /// <summary>经来源 TryExecute 起发：锁内复核纯字段与请求序号；未起发返回 null。</summary>
    private Task<T>? StartRead<T>(long id, OpenCodeInsightsBinding binding, CancellationToken ct, Func<Task<T>> read)
    {
        Task<T>? started = null;
        if (!TryStartLoad(id, binding, ct, () => started = read()))
        {
            return null;
        }

        if (started is not null)
        {
            OpenCodeReadsStarted++;
        }

        return started;
    }

    private sealed record BillionSelection(string Origin, long Generation);

    private Task<T>? StartBillionRead<T>(long id, OpenCodeInsightsBinding binding, BillionSelection selection,
        CancellationToken ct, Func<Task<T>> read)
    {
        Task<T>? started = null;
        binding.Source.TryExecute(ct, () =>
        {
            if (!IsBillionOwner(id, binding, selection)) return;
            started = read();
            BillionReadsStarted++;
        });
        return started;
    }

    private static async Task<ReadResult<T>> AwaitAsync<T>(Task<T>? task)
    {
        if (task is null)
        {
            return new(default, true, null);
        }

        try
        {
            return new(await task, false, null);
        }
        catch (OperationCanceledException)
        {
            return new(default, true, null);
        }
        catch (OpenCodeException error)
        {
            return new(default, false, error);
        }
        catch (ArgumentException)
        {
            return new(default, false, new OpenCodeProtocolException("请求参数无效。"));
        }
    }

    /// <summary>wanted=false 表示前提不足而有意不起发（不是绑定丢失）；wanted=true 且未起发则视为丢失。</summary>
    private static async Task<ReadResult<T>> AwaitOptionalAsync<T>(Task<T>? task, bool wanted)
        => wanted ? await AwaitAsync(task) : new(default, false, null);

    private static OpenCodeInsightSection<OpenCodeCostView> BuildCost(
        string selectedId, string? root, int ancestorReads, string? rootFailure,
        ReadResult<OpenCodeUsageSnapshot> tree, DateTimeOffset sampledAt)
    {
        if (root is null)
        {
            return new(OpenCodeInsightState.Failed, null, rootFailure ?? "无法确认主会话，未统计费用。");
        }

        if (tree.Value is not { } snapshot)
        {
            return new(OpenCodeInsightState.Failed, null, "费用读取失败；没有可显示的数字（不代表费用为零）。");
        }

        var summary = snapshot.Summary;
        var own = summary.Sessions.FirstOrDefault(row => row.SessionId == selectedId);
        var view = new OpenCodeCostView(summary, new(root, root == selectedId, ancestorReads), own, sampledAt);
        return new(summary.Incomplete ? OpenCodeInsightState.Partial : OpenCodeInsightState.Loaded, view,
            summary.Incomplete ? "部分数据：以下只是已读取部分的合计，实际费用可能更高。" : summary.Label);
    }

    private static (OpenCodeInsightSection<OpenCodeModelPricing> Section, OpenCodeModelInfo? Model) BuildPricing(
        OpenCodeSessionInfo? info, ReadResult<OpenCodeModelsResponse> models, string? directory)
    {
        if (info is null)
        {
            return (new(OpenCodeInsightState.Failed, null, "无法读取当前会话信息，当前模型未知。"), null);
        }

        if (info.Model is not { Id: { Length: > 0 } modelId, ProviderId: { Length: > 0 } providerId } current)
        {
            return (new(OpenCodeInsightState.Failed, null, "会话没有提供当前模型，无法匹配价目与上下文上限。"), null);
        }

        if (directory is null)
        {
            return (new(OpenCodeInsightState.Failed, null, "无法确认会话目录，模型目录未查询。"), null);
        }

        if (models.Value?.Data is not { } list)
        {
            return (new(OpenCodeInsightState.Failed, null, "模型目录读取失败。"), null);
        }

        var matches = list.Where(model => model.Id == modelId && model.ProviderId == providerId).ToArray();
        if (matches.Length != 1)
        {
            return (new(OpenCodeInsightState.Failed, null,
                matches.Length == 0 ? "模型目录中没有当前模型，无法显示价目或上下文上限。" : "模型目录中当前模型重复，无法确认价目。"), null);
        }

        var model = matches[0];
        var tiers = ImmutableArray.CreateBuilder<OpenCodePricingTier>();
        if (model.Cost is { } costs)
        {
            for (var index = 0; index < costs.Count; index++)
            {
                var cost = costs[index];
                if (cost is null) continue;
                tiers.Add(new(index, cost.Tier, cost.Input, cost.Output, cost.Cache?.Read, cost.Cache?.Write,
                    OpenCodeUsageAggregator.DeriveCacheMultipliers(model, index)));
            }
        }

        var pricing = new OpenCodeModelPricing(providerId, modelId, current.Variant, model.Name,
            model.Limit?.Context is > 0 ? model.Limit.Context : null, tiers.ToImmutable());
        return (new(tiers.Count == 0 ? OpenCodeInsightState.Partial : OpenCodeInsightState.Loaded, pricing,
            tiers.Count == 0 ? "模型目录未提供价格（不代表免费）。" : "当前模型目录价，仅供参考；历史费用不会按此重算。"), model);
    }

    private static OpenCodeCapabilityRead<T> ToRead<T>(ReadResult<IReadOnlyList<T>> read)
        => read.Value is { } items ? OpenCodeCapabilityRead<T>.Success(items)
            : read.Error is { } error ? OpenCodeCapabilityRead<T>.Failed(error)
            : OpenCodeCapabilityRead<T>.NotObserved();

    private OpenCodeInsightSection<OpenCodeTodoSnapshot> BuildTodo(OpenCodeCapabilityScope scope)
    {
        var todo = OpenCodeTodoReplay.Derive(_binding!.SessionId, _contextMessages, _contextHistoryComplete,
            _todoHistory, _todoConfig, scope);
        return new(todo.State switch
        {
            OpenCodeTodoState.ReadFailure => OpenCodeInsightState.Failed,
            OpenCodeTodoState.Disabled => OpenCodeInsightState.Unavailable,
            _ when !todo.Complete => OpenCodeInsightState.Partial,
            _ => OpenCodeInsightState.Loaded,
        }, todo, todo.Reason);
    }

    private void AdoptBinding(OpenCodeInsightsBinding binding)
    {
        if (_binding is { } owned && SameScope(owned, binding))
        {
            return;
        }

        // 新范围：旧会话/来源的任何结果与在途读取都不得残留。
        _loadId++;
        _billionId++;
        ClearContext();
        if (_retainedContextCatalogBinding is { } retained && SameScope(retained, binding))
        {
            _contextDirectory = _retainedContextDirectory;
            _contextModels = _retainedContextModels;
            _contextCatalogStarted = _retainedContextCatalogStarted;
        }
        _retainedContextCatalogBinding = null;
        _retainedContextModels = null;
        _retainedContextDirectory = null;
        _retainedContextCatalogStarted = false;
        CancelDiscovery(clearResult: true);
        CancelAndForget(ref _loadCts);
        CancelAndForget(ref _billionCts);
        if (!OwnsOrigin(binding))
        {
            _originGeneration++;
            _originSource = binding.Source;
            _originRevision = binding.Revision;
            _origin = null;
            _originState = OpenCodeBillionOriginState.Missing;
            _originManuallyChosen = false;
        }
        _binding = binding;
        Publish(OpenCodeSessionInsightsSnapshot.Empty(binding.SessionId, null));
    }

    private bool IsCurrent(OpenCodeInsightsBinding binding) => !binding.Source.IsRevoked && _isCurrentFields(binding);

    private bool IsOwner(long id, OpenCodeInsightsBinding binding)
        => id == _loadId && _binding is { } owned && SameScope(owned, binding) && IsCurrent(binding);

    private bool OwnsOrigin(OpenCodeInsightsBinding binding)
        => ReferenceEquals(_originSource, binding.Source) && _originRevision == binding.Revision;

    private bool MatchesBillionContext(OpenCodeInsightsBinding binding, OpenCodeBillionContext? context)
        => context is not null && IsCurrent(binding) && _binding is { } owned && SameScope(owned, binding)
        && SameScope(context.Binding, binding) && context.OriginGeneration == _originGeneration
        && context.RequestEpoch == _billionId && OwnsOrigin(binding);

    private bool IsBillionOwner(long id, OpenCodeInsightsBinding binding, BillionSelection selection)
        => IsCurrent(binding) && id == _billionId && _binding is { } owned && SameScope(owned, binding)
        && selection.Generation == _originGeneration && selection.Origin == _origin && IsBillionConfirmed(binding);

    private string OriginNote()
    {
        var selection = _originState switch
        {
            OpenCodeBillionOriginState.Invalid => "地址无效：只接受 HTTP 本机根地址，不接受凭据、路径、查询或片段。未读取 Billion。",
            OpenCodeBillionOriginState.Unconfirmed => "请确认此地址与当前 OpenCode 来源的关联；未确认时不读取 Billion 健康或记录。",
            OpenCodeBillionOriginState.Confirmed => "关联由你确认；健康检查或同名会话记录不能证明这是当前 OpenCode 的活动引擎。",
            _ => "尚未取得地址；打开概况或刷新时自动发现，也可手动覆盖。不会猜测默认端口。",
        };
        if (_originManuallyChosen) return "使用手动覆盖地址；自动发现不会替换你的编辑。 " + selection;
        if (_discoveryLoading) return "正在通过当前 OpenCode 来源和会话位置发现地址；未读取 Billion 健康或记录。 " + selection;
        var discovery = _discovery?.Status switch
        {
            OpenCodeBillionDiscoveryStatus.Ready => "从当前 OpenCode 进程声明中检测到 Billion 地址；尚未验证健康状态或引擎归属。",
            OpenCodeBillionDiscoveryStatus.NotReady => "当前 OpenCode 进程尚未声明可用的 Billion 地址。",
            OpenCodeBillionDiscoveryStatus.Disabled => "当前 OpenCode 进程声明 Billion 已禁用。",
            OpenCodeBillionDiscoveryStatus.UnsupportedSource => "当前 OpenCode 地址不是受支持的本机回环来源，不能把远程回环地址关联到本机。",
            OpenCodeBillionDiscoveryStatus.InvalidLocation => "无法确认当前会话位置，未发送地址发现请求。",
            OpenCodeBillionDiscoveryStatus.UnsupportedProtocol => "地址发现协议版本不受支持；未采用返回地址。",
            OpenCodeBillionDiscoveryStatus.Malformed => "地址发现响应不符合约定；未采用返回地址。",
            OpenCodeBillionDiscoveryStatus.HttpError => $"当前来源未提供可用发现结果（HTTP {_discovery.HttpStatus}）；可能不支持此新接口，未猜测地址。",
            OpenCodeBillionDiscoveryStatus.Timeout => "地址发现超时；没有自动重试。",
            OpenCodeBillionDiscoveryStatus.TransportUnavailable => "当前来源的地址发现暂不可用；没有自动重试。",
            OpenCodeBillionDiscoveryStatus.Cancelled => "本次地址发现已取消，未采用迟到结果。",
            _ => "",
        };
        return discovery.Length == 0 ? selection : discovery + " " + selection;
    }

    private static bool SameScope(OpenCodeInsightsBinding a, OpenCodeInsightsBinding b)
        => ReferenceEquals(a.Source, b.Source) && a.Revision == b.Revision && a.SourceGeneration == b.SourceGeneration
        && ReferenceEquals(a.Client, b.Client) && a.PageGeneration == b.PageGeneration
        && a.SessionGeneration == b.SessionGeneration && string.Equals(a.SessionId, b.SessionId, StringComparison.Ordinal);

    private static void CancelAndForget(ref CancellationTokenSource? cts)
    {
        var old = cts;
        cts = null;
        try { old?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void Publish(OpenCodeSessionInsightsSnapshot snapshot)
    {
        SetSnapshot(snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetSnapshot(OpenCodeSessionInsightsSnapshot snapshot)
    {
        Snapshot = _binding is { } binding ? snapshot with
        {
            BillionOrigin = _origin,
            BillionOriginState = _originState,
            BillionOriginNote = OriginNote(),
            BillionDiscovery = _discovery,
            BillionDiscoveryLoading = _discoveryLoading,
            BillionConfirmed = IsBillionConfirmed(binding),
            BillionContext = new(binding, _originGeneration, _billionId),
        } : snapshot;
    }
}
