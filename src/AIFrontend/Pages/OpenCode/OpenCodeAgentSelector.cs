using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Agent/model presentation, UI-only agent selection and guarded application on the next send.
/// </summary>
/// <remarks>
/// OpenSelection opens an anchored ListView flyout and stores only a pending UI choice. The next send calls
/// ApplyPendingSelectionAsync before beginning local submission. Real mutations retain AgentSwitchCoordinator's
/// shared lease, server re-check, origin fence and explicit unknown recovery. Same-session generation changes keep
/// the pending choice; changing source/session clears it. UI methods run on the UI thread; origin callbacks read only
/// the atomically published immutable snapshot. The selector disposes only the client it created, never App's client.
/// </remarks>
internal sealed class OpenCodeAgentSelector : StackPanel
{
    private const string UnknownAgentText = "智能体：未知";
    private const string UnknownModelText = "模型：未知";

    // Switchable primary-agent snapshot; hidden/subagent-only agents never enter the flyout.
    private readonly List<OpenCodeAgentPickerRow> _rows = new();
    private readonly TextBlock _currentText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly TextBlock _separator = OpenCodeInteractionUi.StyledText("·", OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly TextBlock _modelText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly TextBlock _statusText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
    private readonly ProgressRing _progress = new() { Width = 12, Height = 12, IsActive = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusRow = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };

    private readonly InfoBar _recoveryBar = new()
    {
        Severity = InfoBarSeverity.Warning,
        IsOpen = false,
        IsClosable = false,
        Title = "智能体切换结果未知",
    };
    private readonly StackPanel _recoveryEntryPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly Button _recoveryEntryButton = new() { Content = "恢复 agent 未知状态…" };
    private readonly StackPanel _recoveryConfirmPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _recoveryText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
    private readonly Button _reconcileNoRetryButton = new() { Content = "解除（不重试）" };
    private readonly Button _reconcileAllowButton = new() { Content = "解除（允许之后重新切换）" };
    private readonly Button _recoveryCancelButton = new() { Content = "返回" };

    private readonly AgentSwitchCoordinator _coordinator = new(OpenCodeInteractionGate.Shared);

    // 当前上下文（UI 线程写入；Volatile 发布供 isOriginCurrent 回调跨线程只读）。
    private OpenCodeClient? _client;
    private AgentSwitchOrigin? _currentOrigin;
    private CancellationTokenSource? _contextCts;
    private Guid? _retainedUnknownOperationId;
    private string? _currentAgentId;
    private bool _busy;
    private bool _localSubmissionPending;
    private bool _dialogOpen;

    /// <summary>
    /// Open the picker (UI selection only); false when unavailable or already open.
    /// </summary>
    internal bool OpenSelection(FrameworkElement? anchor = null)
    {
        var origin = Volatile.Read(ref _currentOrigin);
        if (_dialogOpen || origin is null || _client is null || (anchor?.XamlRoot ?? XamlRoot) is null)
        {
            if (!_dialogOpen) SetStatus("智能体列表尚未就绪；请稍后再输入 /agents。");
            return false;
        }

        _ = ShowPickerAsync(origin, OpenCodePickerFlyout.Anchor(anchor?.XamlRoot ?? XamlRoot, anchor, "OpenCodeSidebarAgent"));
        return true;
    }

    private async Task ShowPickerAsync(AgentSwitchOrigin origin, FrameworkElement? anchor)
    {
        _dialogOpen = true;
        string? target;
        try
        {
            target = await OpenCodeAgentPickerDialog.ShowAsync(anchor?.XamlRoot ?? XamlRoot, _rows.ToArray(), CurrentAgentId,
                CurrentAgentDisplay, SwitchBlockedReason(), anchor).ConfigureAwait(true);
        }
        finally
        {
            _dialogOpen = false;
        }

        if (target is null)
        {
            return;
        }

        // 对话框期间来源/会话/选择代际（含本地提交开始）任一变化：不切换（零自动改选）。
        if (!IsOriginCurrent(origin))
        {
            if (Volatile.Read(ref _currentOrigin) is not null)
            {
                SetStatus("会话或选择已变化；本次未切换。");
            }

            return;
        }

        SelectPendingAgent(origin, target);
    }

    private void SelectPendingAgent(AgentSwitchOrigin origin, string target)
    {
        if (!IsOriginCurrent(origin) || SwitchBlockedReason() is not null || !_rows.Exists(r => r.Selectable && r.Id == target)) return;
        _pendingAgentId = target == _currentAgentId ? null : target;
        _currentText.Text = CurrentAgentDisplay ?? UnknownAgentText;
        ApplyCurrentAgentPresentation();
        SetStatus(HasPendingSelection ? "下一条消息生效" : string.Empty);
        UpdateControls();
    }

    // Called by the page BEFORE BeginLocalAgentSubmission. PromptBody carries only text (attached prompts add
    // files/contexts), not an agent: use the existing server switch and its lease/re-check, never invent a wire field.
    // False means the page must keep the draft and abort this send. No retry is performed here.
    internal async Task<bool> ApplyPendingSelectionAsync()
    {
        if (_pendingAgentId is not { } target) return true;
        var origin = Volatile.Read(ref _currentOrigin);
        if (origin is null || SwitchBlockedReason() is not null) return false;
        await RunSwitchAsync(origin, target).ConfigureAwait(true);
        return IsOriginCurrent(origin) && _pendingAgentId is null && _currentAgentId == target;
    }

    /// <summary>当前不允许发起切换的原因（对话框据此禁用“切换”）；可切换时为 null。</summary>
    private string? SwitchBlockedReason()
    {
        if (CurrentSessionInfo is { ParentId: not null }) return "子智能体会话仅供查看。";
        if (_retainedUnknownOperationId is not null) return "该会话存在未解除的智能体未知状态；请先在状态行下方显式解除。";
        if (_busy) return "上一次切换或解除仍在进行中。";
        if (_localSubmissionPending) return "本会话存在本地待提交输入；发送结束后再切换。";
        return null;
    }

    /// <summary>重新读取并刷新只读显示（例如 /models 已被接纳后）；忙碌或未就绪时不做任何事。</summary>
    internal void RefreshDisplay()
    {
        if (_busy || _client is null || Volatile.Read(ref _currentOrigin) is not { } origin)
        {
            return;
        }

        _ = ReloadAsync(origin, surfaceProtection: false);
    }

    internal OpenCodeAgentSelector()
    {
        Orientation = Orientation.Vertical;
        Spacing = 4;

        AutomationProperties.SetAutomationId(_currentText, "OpenCodeAgentSelectorCurrent");
        ToolTipService.SetToolTip(_currentText, "输入 /agents 切换智能体");
        AutomationProperties.SetAutomationId(_modelText, "OpenCodeCurrentModel");
        ToolTipService.SetToolTip(_modelText, "输入 /models 切换模型");
        AutomationProperties.SetAccessibilityView(_separator, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        foreach (var text in new[] { _currentText, _separator, _modelText })
        {
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        OpenCodeAgentPresentation.WrapName(_currentText);
        _modelText.MaxWidth = 260;

        _statusText.TextWrapping = TextWrapping.NoWrap;
        _statusText.TextTrimming = TextTrimming.CharacterEllipsis;
        _statusText.MaxWidth = 360;
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _statusRow.Children.Add(_progress);
        _statusRow.Children.Add(_statusText);
        AutomationProperties.SetAutomationId(_statusText, "OpenCodeAgentSelectorStatus");
        AutomationProperties.SetLiveSetting(_statusText, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        // 单行只读：智能体 · 模型 · （忙碌环 + 简短状态）。切换只经 /agents、/models。
        var line = new Grid { ColumnSpacing = 6, RowSpacing = 4 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        line.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(_separator, 1);
        Grid.SetColumn(_modelText, 2);
        Grid.SetRow(_statusRow, 1);
        Grid.SetColumnSpan(_statusRow, 3);
        // Identity entries now live in the sidebar card and composer. Keep only operation status
        // and the explicit recovery surface here, mounted in the overview drawer.
        line.Children.Add(_statusRow);

        AutomationProperties.SetAutomationId(_recoveryEntryButton, "OpenCodeAgentSelectorRecoveryEntry");
        AutomationProperties.SetName(_recoveryEntryButton, "恢复 agent 未知状态");
        _recoveryEntryButton.Click += (_, _) => OpenRecoveryConfirm();
        _recoveryEntryPanel.Children.Add(_recoveryEntryButton);

        AutomationProperties.SetAutomationId(_reconcileNoRetryButton, "OpenCodeAgentSelectorReconcileDisallow");
        _reconcileNoRetryButton.Click += (_, _) => _ = RunReconcileAsync();
        AutomationProperties.SetAutomationId(_reconcileAllowButton, "OpenCodeAgentSelectorReconcileAllow");
        _reconcileAllowButton.Click += (_, _) => _ = RunReconcileAsync();
        AutomationProperties.SetAutomationId(_recoveryCancelButton, "OpenCodeAgentSelectorReconcileCancel");
        _recoveryCancelButton.Click += (_, _) => HideRecoveryConfirm();
        var recoveryButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        recoveryButtons.Children.Add(_reconcileNoRetryButton);
        recoveryButtons.Children.Add(_reconcileAllowButton);
        recoveryButtons.Children.Add(_recoveryCancelButton);
        _recoveryConfirmPanel.Children.Add(recoveryButtons);

        // 未知状态恢复：状态行下方的 Warning 条（文案 + 入口 / 两种显式解除）。
        var recoveryContent = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        recoveryContent.Children.Add(_recoveryText);
        recoveryContent.Children.Add(_recoveryEntryPanel);
        recoveryContent.Children.Add(_recoveryConfirmPanel);
        _recoveryBar.Content = recoveryContent;
        AutomationProperties.SetAutomationId(_recoveryBar, "OpenCodeAgentSelectorRecovery");

        Children.Add(line);
        Children.Add(_recoveryBar);

        Visibility = Visibility.Collapsed;
        ResetUi();
    }

    /// <summary>切换/解除的终态报告（供宿主展示外壳提示；诊断性质，不含正文）。</summary>
    internal event EventHandler<OpenCodeAgentSwitchReportedEventArgs>? SwitchReported;

    /// <summary>当前 agent 的 id（未读取到时为 null）。</summary>
    private string? _pendingAgentId;
    internal bool HasPendingSelection => _pendingAgentId is not null;
    internal string? CurrentAgentId => _pendingAgentId ?? _currentAgentId;

    /// <summary>当前智能体显示名（列表中有则用名称，否则如实用 id；未读到时为 null）。</summary>
    internal string? CurrentAgentDisplay => string.IsNullOrEmpty(CurrentAgentId) ? null : DisplayName(CurrentAgentId);

    /// <summary>当前会话模型的只读显示（会话真实状态；未设置 / 未读到时为 null，绝不虚构）。</summary>
    internal string? CurrentModelDisplay { get; private set; }

    // Read-through presentation for other current-session surfaces; this selector remains the only
    // owner of the observed session selection. No second session read/cache is needed by the sidebar.
    internal OpenCodeModelRef? CurrentModel { get; private set; }
    // The exact, origin-fenced Session.Info read also identifies child sessions without a second request/cache.
    internal OpenCodeSessionInfo? CurrentSessionInfo { get; private set; }
    internal string? CurrentAgentColor => _rows.Find(row => row.Id == CurrentAgentId)?.Color;
    internal string? CurrentAgentDescription => _rows.Find(row => row.Id == CurrentAgentId)?.Description;
    internal event EventHandler? DisplayChanged;

    /// <summary>该会话是否有未解除的 agent 未知状态（有则切换按钮禁用，仅允许显式恢复）。</summary>
    internal bool HasUnresolvedUnknown => _retainedUnknownOperationId is not null;

    // ------------------------------------------------------------------
    // 生命周期入口（宿主接线面）
    // ------------------------------------------------------------------

    /// <summary>
    /// 设置/更新当前 source/revision/sessionId/selectionGeneration。任一变化即作废在途操作并重建
    /// 自有客户端与真实 agent 列表；同上下文且已就绪时为幂等无操作。selectionGeneration 由宿主在
    /// 选择上下文变化（含切走再切回）时递增。
    /// </summary>
    internal void Configure(OpenCodePageSource source, long revision, string sessionId, long selectionGeneration)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(sessionId) || !sessionId.StartsWith("ses", StringComparison.Ordinal))
        {
            Clear();
            return;
        }

        var current = Volatile.Read(ref _currentOrigin);
        if (current is not null
            && ReferenceEquals(current.Source, source)
            && current.Revision == revision
            && current.SelectionGeneration == selectionGeneration
            && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal)
            && _client is not null)
        {
            return; // 同上下文且已就绪：幂等。
        }

        // 同一会话仅代际变化（如本地提交开始）时保留已读到的只读显示，避免状态行闪烁；
        // 换会话 / 换来源一律清空，绝不把旧会话的智能体或模型显示给新会话。
        var keepDisplay = current is not null && ReferenceEquals(current.Source, source) && current.Revision == revision
            && string.Equals(current.SessionId, sessionId, StringComparison.Ordinal);
        var keptRows = keepDisplay ? _rows.ToArray() : Array.Empty<OpenCodeAgentPickerRow>();
        var keptAgentId = keepDisplay ? _currentAgentId : null;
        var keptPendingAgentId = keepDisplay ? _pendingAgentId : null;
        var keptAgent = keepDisplay ? _currentText.Text : string.Empty;
        var keptModel = keepDisplay ? _modelText.Text : string.Empty;
        var keptModelDisplay = keepDisplay ? CurrentModelDisplay : null;
        var keptModelRef = keepDisplay ? CurrentModel : null;
        var keptSessionInfo = keepDisplay ? CurrentSessionInfo : null;

        InvalidateContext();
        if (keepDisplay)
        {
            _rows.AddRange(keptRows);
            _currentAgentId = keptAgentId;
            _pendingAgentId = keptPendingAgentId;
            _currentText.Text = keptAgent;
            ApplyCurrentAgentPresentation();
            _modelText.Text = keptModel;
            CurrentModelDisplay = keptModelDisplay;
            CurrentModel = keptModelRef;
            CurrentSessionInfo = keptSessionInfo;
        }

        var origin = new AgentSwitchOrigin(source, revision, sessionId, selectionGeneration);
        _contextCts = new CancellationTokenSource();
        Volatile.Write(ref _currentOrigin, origin);
        Visibility = Visibility.Visible;

        OpenCodeClient client;
        try
        {
            client = source.CreateClient();
        }
        catch (InvalidOperationException)
        {
            // 创建期间来源被撤销：按等待处理（换代事件会再次到达并重读）。
            SetStatus("连接尚未就绪（来源已撤销）。");
            UpdateControls();
            return;
        }

        // 创建后复核：来源仍未被撤销、且本上下文仍为当前，才安装客户端。
        if (source.IsRevoked || !IsOriginCurrent(origin))
        {
            client.Dispose();
            SetStatus("连接尚未就绪。");
            UpdateControls();
            return;
        }

        _client = client;
        if (_currentText.Text.Length == 0)
        {
            _currentText.Text = "智能体：读取中…";
        }

        UpdateControls();
        _ = ReloadAsync(origin, surfaceProtection: true);
    }

    /// <summary>清空上下文：取消在途操作、丢弃迟到结果、释放自有客户端、清 UI。绝不释放 App 共享 client。</summary>
    internal void Clear()
    {
        InvalidateContext();
        _localSubmissionPending = false;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 宿主告知本会话是否有“尚未进入共享仲裁的本地排队提交”。为 true 时禁用切换按钮（避免与本地
    /// 提交竞态）；宿主须在入队时原子发布新的 selectionGeneration 使旧 origin 失效。
    /// </summary>
    internal void SetLocalSubmissionPending(bool pending)
    {
        _localSubmissionPending = pending;
        UpdateControls();
    }

    // ------------------------------------------------------------------
    // 加载真实状态
    // ------------------------------------------------------------------

    private async Task ReloadAsync(AgentSwitchOrigin origin, bool surfaceProtection)
    {
        var client = _client;
        if (client is null)
        {
            return;
        }

        var token = _contextCts?.Token ?? CancellationToken.None;
        try
        {
            var session = await client.GetSessionInfoAsync(origin.SessionId, token).ConfigureAwait(true);
            if (!IsOriginCurrent(origin))
            {
                return;
            }

            if (session.Id != origin.SessionId) return;
            CurrentSessionInfo = session;
            if (session.ParentId is not null) _pendingAgentId = null;
            var directory = session.Location?.Directory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                SetStatus("无法确定会话目录；未加载 agent 列表。");
                UpdateControls();
                return;
            }

            var agents = await client.ListAgentsAsync(directory, token).ConfigureAwait(true);
            if (!IsOriginCurrent(origin))
            {
                return;
            }

            var data = agents.Data ?? new List<OpenCodeAgentInfo>();
            ApplyAgents(data, session.Agent);
            ApplyModel(session.Model);

            if (surfaceProtection)
            {
                await SurfaceProtectionAsync(origin).ConfigureAwait(true);
                if (!IsOriginCurrent(origin))
                {
                    return;
                }
            }

            SetStatus(HasPendingSelection ? "下一条消息生效" : data.Count == 0 ? "该目录没有可切换的智能体。" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            // 上下文已变化/来源已撤销：不改 UI。
        }
        catch (OpenCodeApiException api) when (api.StatusCode == 404)
        {
            // 会话不存在/不可读：与旧列表扫描“未找到”路径同文案，不显示 agent。
            if (IsOriginCurrent(origin))
            {
                SetStatus("无法读取该会话状态；未显示 agent。");
            }
        }
        catch (ArgumentException)
        {
            // sessionId 未通过客户端前置校验（Configure 仅校验 ses 前缀）：同样视为不可读取。
            if (IsOriginCurrent(origin))
            {
                SetStatus("无法读取该会话状态；未显示 agent。");
            }
        }
        catch (OpenCodeException)
        {
            if (IsOriginCurrent(origin))
            {
                SetStatus("读取 agent 失败（协议或服务错误）。");
            }
        }
        catch (HttpRequestException)
        {
            if (IsOriginCurrent(origin))
            {
                SetStatus("读取 agent 失败（网络不可用）。");
            }
        }
        catch (ObjectDisposedException)
        {
            // 客户端在读取期间被上下文变化释放：丢弃。
        }
        catch (InvalidOperationException)
        {
            // 来源在读取期间被撤销或参数非法：丢弃。
        }

        if (IsOriginCurrent(origin))
        {
            // 读取失败：只读显示如实为“未知”（不保留占位，也不猜测）。
            if (_currentText.Text.Length == 0 || _currentText.Text == "智能体：读取中…")
            {
                _currentText.Text = UnknownAgentText;
            }

            if (_modelText.Text.Length == 0)
            {
                _modelText.Text = UnknownModelText;
            }

            UpdateControls();
        }
    }

    /// <summary>用真实列表刷新可选目标与只读显示；hidden/subagent-only 不进入候选。</summary>
    private void ApplyAgents(IReadOnlyList<OpenCodeAgentInfo> data, string? currentAgentId)
    {
        _rows.Clear();
        _currentAgentId = currentAgentId;
        if (_pendingAgentId == currentAgentId) _pendingAgentId = null;

        foreach (var agent in data)
        {
            if (agent is null || string.IsNullOrWhiteSpace(agent.Id) || string.IsNullOrWhiteSpace(agent.Name) || agent.Hidden)
            {
                continue;
            }

            if (string.Equals(agent.Mode, OpenCodeAgentModes.Subagent, StringComparison.Ordinal)) continue;
            _rows.Add(new OpenCodeAgentPickerRow(agent.Id, agent.Name,
                string.IsNullOrWhiteSpace(agent.Description) ? null : agent.Description.Trim(),
                true, null, agent.Color));
        }

        if (string.IsNullOrEmpty(CurrentAgentId))
        {
            _currentText.Text = UnknownAgentText;
        }
        else
        {
            // 当前 agent 不在列表（如 hidden）：如实显示 id。
            _currentText.Text = DisplayName(CurrentAgentId);
        }

        ApplyCurrentAgentPresentation();
    }

    private void ApplyCurrentAgentPresentation()
    {
        var selected = _rows.Find(row => row.Id == CurrentAgentId);
        OpenCodeAgentPresentation.Apply(_currentText, selected?.Color);
        ToolTipService.SetToolTip(_currentText, selected?.Description ?? "输入 /agents 切换智能体");
        AutomationProperties.SetHelpText(_currentText, selected?.Description ?? string.Empty);
        AutomationProperties.SetName(_currentText, string.IsNullOrEmpty(_currentAgentId) ? UnknownAgentText : "智能体：" + _currentText.Text);
    }

    /// <summary>只读模型显示（会话真实状态；变体附在后面）。未设置时显示“模型：未知”。</summary>
    private void ApplyModel(OpenCodeModelRef? model)
    {
        CurrentModel = model;
        if (model?.Id is not { Length: > 0 } id)
        {
            CurrentModelDisplay = null;
            _modelText.Text = UnknownModelText;
            AutomationProperties.SetName(_modelText, UnknownModelText);
            ToolTipService.SetToolTip(_modelText, "输入 /models 切换模型");
            return;
        }

        _modelText.Text = id + (model.Variant is { Length: > 0 } variant ? "（" + variant + "）" : string.Empty);
        CurrentModelDisplay = _modelText.Text;
        AutomationProperties.SetName(_modelText, "模型：" + _modelText.Text);
        ToolTipService.SetToolTip(_modelText, (model.ProviderId is { Length: > 0 } provider ? provider + " / " : string.Empty)
            + _modelText.Text + Environment.NewLine + "输入 /models 切换模型");
    }

    // ------------------------------------------------------------------
    // 显式切换
    // ------------------------------------------------------------------

    private async Task RunSwitchAsync(AgentSwitchOrigin expectedOrigin, string target)
    {
        if (_busy || _retainedUnknownOperationId is not null)
        {
            return;
        }

        var origin = Volatile.Read(ref _currentOrigin);
        if (origin is null || _client is null)
        {
            SetStatus("连接尚未就绪；未切换。");
            return;
        }

        if (!ReferenceEquals(origin, expectedOrigin))
        {
            SetStatus("会话或选择已变化；本次未切换。");
            return;
        }

        if (_localSubmissionPending)
        {
            SetStatus("本会话存在本地待提交输入；未切换。");
            return;
        }

        if (!_rows.Exists(r => r.Selectable && string.Equals(r.Id, target, StringComparison.Ordinal)))
        {
            SetStatus("该智能体不可切换；未切换。");
            return;
        }

        if (string.Equals(target, _currentAgentId, StringComparison.Ordinal))
        {
            SetStatus("当前已是该智能体。");
            return;
        }

        var operationId = Guid.NewGuid();
        _busy = true;
        UpdateControls();
        SetStatus("正在切换…");

        AgentSwitchResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(origin.Source.RevocationToken, _contextCts?.Token ?? CancellationToken.None);
            result = await _coordinator.SwitchAsync(origin, operationId, target, IsOriginCurrent, linked.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // coordinator 内部已收敛传输/取消/协议结果；逃逸异常发生在起发前（参数校验）。不推断成功。
            if (IsOriginCurrent(origin))
            {
                _busy = false;
                SetStatus("切换未发起（参数或状态校验失败）。");
                UpdateControls();
            }

            return;
        }

        if (!IsOriginCurrent(origin))
        {
            // 上下文/选择/来源已变化：零 UI/busy/unknown 写入（含 busy 复位——该位可能已属新上下文）。
            // Unknown 仍按 endpoint/session 存活于共享 gate；新上下文 Configure 时自行读回，绝不复制旧 operationId。
            return;
        }

        _busy = false;
        await ApplySwitchResultAsync(origin, result).ConfigureAwait(true);
        if (!IsOriginCurrent(origin))
        {
            // 结果应用期间上下文再次变化：不报告、不更新 UI。
            return;
        }

        RaiseReport(result);
        UpdateControls();
    }

    private async Task ApplySwitchResultAsync(AgentSwitchOrigin origin, AgentSwitchResult result)
    {
        switch (result.Status)
        {
            case AgentSwitchStatus.Applied:
                await ReloadAsync(origin, surfaceProtection: false).ConfigureAwait(true);
                if (!IsOriginCurrent(origin))
                {
                    return;
                }

                SetStatus(string.IsNullOrEmpty(result.ObservedAgentId)
                    ? "已切换（以重新读取的真实状态为准）。"
                    : "已切换为 " + DisplayName(result.ObservedAgentId) + "。");
                break;

            case AgentSwitchStatus.Blocked:
                SetStatus("当前会话状态不允许切换（可能有待处理请求、未决后台任务或正在执行）；未切换。");
                break;

            case AgentSwitchStatus.OriginLost:
                SetStatus("来源或选择已变化；本次未切换。");
                break;

            case AgentSwitchStatus.OutcomeUnknown:
                _retainedUnknownOperationId = result.OperationId;
                ShowRecovery("切换结果未知（可能已应用）；不会自动重试。请选择恢复方式：");
                break;

            case AgentSwitchStatus.ReconciliationRequired:
                var protectedId = await ReadProtectedOperationIdAsync(origin).ConfigureAwait(true);
                if (!IsOriginCurrent(origin))
                {
                    return;
                }

                if (protectedId is { } id)
                {
                    _retainedUnknownOperationId = id;
                    ShowRecovery("该会话已存在待处理的 agent 未知状态（可能来自其他入口）；请选择恢复方式：");
                }
                else
                {
                    SetStatus("会话受保护或另有操作进行中；请稍后再试。");
                }

                break;

            default:
                SetStatus("切换未完成（" + result.Status + "）；当前 agent 未改变。");
                break;
        }
    }

    // ------------------------------------------------------------------
    // 显式解除未知状态（绝不自动重试）
    // ------------------------------------------------------------------

    private async Task RunReconcileAsync()
    {
        if (_busy)
        {
            return;
        }

        var origin = Volatile.Read(ref _currentOrigin);
        var operationId = _retainedUnknownOperationId;
        if (origin is null || _client is null || operationId is null)
        {
            HideRecovery();
            SetStatus("当前没有可解除的未知状态。");
            UpdateControls();
            return;
        }

        _busy = true;
        HideRecoveryConfirm();
        UpdateControls();
        SetStatus("正在核对并解除未知状态…");

        AgentSwitchResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(origin.Source.RevocationToken, _contextCts?.Token ?? CancellationToken.None);
            result = await _coordinator.ReconcileAsync(origin, operationId.Value, IsOriginCurrent, linked.Token).ConfigureAwait(true);
        }
        catch (Exception)
        {
            if (IsOriginCurrent(origin))
            {
                _busy = false;
                SetStatus("解除失败（未预期错误）；状态未改变。");
                ShowRecovery("未知状态仍存在；请重新核对后再次显式解除。");
                UpdateControls();
            }

            return;
        }

        if (!IsOriginCurrent(origin))
        {
            // 上下文/选择/来源已变化：零 UI/busy/unknown 写入；Unknown 存活于共享 gate，由新上下文自行读回。
            return;
        }

        _busy = false;
        switch (result.Status)
        {
            case AgentSwitchStatus.Reconciled:
                _retainedUnknownOperationId = null;
                HideRecovery();
                await ReloadAsync(origin, surfaceProtection: false).ConfigureAwait(true);
                if (!IsOriginCurrent(origin))
                {
                    return;
                }

                SetStatus("已解除该未知状态（未重试；后续切换需重新发起）。");
                break;

            case AgentSwitchStatus.ReconciliationRequired:
                SetStatus("仍未满足解除条件；状态未改变。");
                ShowRecovery("未知状态仍存在；可稍后再次显式解除。");
                break;

            default:
                SetStatus("解除未完成（" + result.Status + "）；状态未改变。");
                ShowRecovery("未知状态仍存在；请重新核对后再次显式解除。");
                break;
        }

        if (!IsOriginCurrent(origin))
        {
            // 重读期间上下文再次变化：不报告、不更新 UI。
            return;
        }

        RaiseReport(result);
        UpdateControls();
    }

    /// <summary>在共享租约内读取精确的 agent 未知 operationId（只读诊断；busy 时不进入以避免与在途操作竞争）。</summary>
    private async Task<Guid?> ReadProtectedOperationIdAsync(AgentSwitchOrigin origin)
    {
        if (_busy)
        {
            return null;
        }

        var token = _contextCts?.Token ?? CancellationToken.None;
        try
        {
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(origin.Source.Identity, origin.SessionId, token).ConfigureAwait(true);
            return lease.GetAgentSwitchProtection()?.OperationId;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task SurfaceProtectionAsync(AgentSwitchOrigin origin)
    {
        if (_retainedUnknownOperationId is not null)
        {
            ShowRecovery("该会话存在未解除的 agent 未知状态；请显式解除。");
            return;
        }

        var id = await ReadProtectedOperationIdAsync(origin).ConfigureAwait(true);
        if (!IsOriginCurrent(origin))
        {
            return;
        }

        if (id is { } protectedId)
        {
            _retainedUnknownOperationId = protectedId;
            ShowRecovery("该会话存在未解除的 agent 未知状态（可能来自其他入口）；请显式解除。");
        }
    }

    // ------------------------------------------------------------------
    // UI 状态
    // ------------------------------------------------------------------

    private void InvalidateContext()
    {
        // 取消但不 Dispose：在途操作可能仍持有由该 CTS 派生的 linked token（与 source.Revoke 同理由）。
        var cts = _contextCts;
        _contextCts = null;
        cts?.Cancel();

        _client?.Dispose();
        _client = null;

        Volatile.Write(ref _currentOrigin, null);
        _retainedUnknownOperationId = null;
        _currentAgentId = null;
        _pendingAgentId = null;
        _busy = false;
        ResetUi();
        UpdateControls();
    }

    private void ResetUi()
    {
        _rows.Clear();
        _currentText.Text = string.Empty;
        OpenCodeAgentPresentation.Apply(_currentText, null);
        ToolTipService.SetToolTip(_currentText, "输入 /agents 切换智能体");
        AutomationProperties.SetHelpText(_currentText, string.Empty);
        _modelText.Text = string.Empty;
        CurrentModelDisplay = null;
        CurrentModel = null;
        CurrentSessionInfo = null;
        _statusText.Text = string.Empty;
        _progress.IsActive = false;
        HideRecoveryConfirm();
        _recoveryEntryPanel.Visibility = Visibility.Collapsed;
        SyncRecoveryBar();
        UpdateStatusVisibility();
    }

    /// <summary>Warning 条只在恢复入口或确认区可见时打开（两者都隐藏 = 无未知状态可处理）。</summary>
    private void SyncRecoveryBar()
        => _recoveryBar.IsOpen = _recoveryEntryPanel.Visibility == Visibility.Visible
            || _recoveryConfirmPanel.Visibility == Visibility.Visible;

    private void ShowRecovery(string detail)
    {
        _recoveryText.Text = detail;
        _recoveryEntryPanel.Visibility = Visibility.Visible;
        _recoveryConfirmPanel.Visibility = Visibility.Collapsed;
        SyncRecoveryBar();
    }

    private void OpenRecoveryConfirm()
    {
        if (_retainedUnknownOperationId is null)
        {
            _recoveryEntryPanel.Visibility = Visibility.Collapsed;
            SyncRecoveryBar();
            return;
        }

        _recoveryEntryPanel.Visibility = Visibility.Collapsed;
        _recoveryConfirmPanel.Visibility = Visibility.Visible;
        SyncRecoveryBar();
        UpdateControls();
    }

    private void HideRecoveryConfirm()
    {
        _recoveryConfirmPanel.Visibility = Visibility.Collapsed;
        if (_retainedUnknownOperationId is not null)
        {
            _recoveryEntryPanel.Visibility = Visibility.Visible;
        }

        SyncRecoveryBar();
    }

    private void HideRecovery()
    {
        _recoveryEntryPanel.Visibility = Visibility.Collapsed;
        _recoveryConfirmPanel.Visibility = Visibility.Collapsed;
        SyncRecoveryBar();
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
        UpdateStatusVisibility();
    }

    /// <summary>状态/进度行仅在确有状态文案或 busy（进度环）时占空间。</summary>
    private void UpdateStatusVisibility()
        => _statusRow.Visibility = _progress.IsActive || !string.IsNullOrEmpty(_statusText.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateControls()
    {
        var ready = Volatile.Read(ref _currentOrigin) is not null && _client is not null;
        var unknown = _retainedUnknownOperationId is not null;
        var confirmVisible = _recoveryConfirmPanel.Visibility == Visibility.Visible;

        _separator.Visibility = _currentText.Text.Length > 0 && _modelText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsActive = _busy;
        UpdateStatusVisibility();

        _recoveryEntryButton.IsEnabled = !_busy;
        _reconcileNoRetryButton.IsEnabled = !_busy;
        _reconcileAllowButton.IsEnabled = !_busy;
        _recoveryCancelButton.IsEnabled = !_busy;

        if (!unknown || !ready)
        {
            // 无未知状态，或当前上下文已失效（无 origin/client）：不提供恢复入口（恢复需要当前 origin）。
            HideRecovery();
        }
        else if (!confirmVisible)
        {
            _recoveryEntryPanel.Visibility = Visibility.Visible;
        }

        SyncRecoveryBar();
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseReport(AgentSwitchResult result)
        => SwitchReported?.Invoke(this, new OpenCodeAgentSwitchReportedEventArgs(
            result.OperationId, result.Status, result.ObservedAgentId, DescribeStatus(result.Status)));

    private static string DescribeStatus(AgentSwitchStatus status) => status switch
    {
        AgentSwitchStatus.Applied => "已切换 agent（以重新读取的真实状态为准）。",
        AgentSwitchStatus.Blocked => "当前状态不允许切换。",
        AgentSwitchStatus.OriginLost => "来源或选择已变化；未切换。",
        AgentSwitchStatus.OutcomeUnknown => "切换结果未知；未自动重试。",
        AgentSwitchStatus.ReconciliationRequired => "需要显式解除未知状态。",
        AgentSwitchStatus.Reconciled => "已解除未知状态（未重试）。",
        _ => "切换未完成。",
    };

    private string DisplayName(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return "未知智能体";
        }

        foreach (var row in _rows)
        {
            if (string.Equals(row.Id, agentId, StringComparison.Ordinal))
            {
                return OpenCodeAgentIcons.DisplayName(row.Name);
            }
        }

        return OpenCodeAgentIcons.DisplayName(agentId);
    }

    /// <summary>原子发布的 origin 是否仍为当前（回调可跨线程只读；不触 XAML/Dispatcher/App）。</summary>
    private bool IsOriginCurrent(AgentSwitchOrigin origin)
    {
        var current = Volatile.Read(ref _currentOrigin);
        return current is not null
            && ReferenceEquals(current.Source, origin.Source)
            && current.Revision == origin.Revision
            && current.SelectionGeneration == origin.SelectionGeneration
            && string.Equals(current.SessionId, origin.SessionId, StringComparison.Ordinal);
    }
}

/// <summary>切换/解除终态报告；供宿主展示外壳提示（诊断性质，不含正文）。</summary>
internal sealed class OpenCodeAgentSwitchReportedEventArgs : EventArgs
{
    internal OpenCodeAgentSwitchReportedEventArgs(Guid operationId, AgentSwitchStatus status, string? observedAgentId, string message)
    {
        OperationId = operationId;
        Status = status;
        ObservedAgentId = observedAgentId;
        Message = message;
    }

    /// <summary>本次显式操作的 operationId（解除时为用户确认的保留 operationId）。</summary>
    public Guid OperationId { get; }

    /// <summary>coordinator 返回的终态。</summary>
    public AgentSwitchStatus Status { get; }

    /// <summary>观测到的 agent id（仅 <c>Applied</c> 等有观察值时非 null；不是 POST 成功的因果证据）。</summary>
    public string? ObservedAgentId { get; }

    /// <summary>产品化摘要文案。</summary>
    public string Message { get; }
}
