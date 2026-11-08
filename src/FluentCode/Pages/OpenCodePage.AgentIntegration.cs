using System;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

/// <summary>
/// OpenCode 页的 Agent 选择/切换控件接入与子会话导航事件接线。本文件只做最小的事件/生命周期接线：
/// 不改 <see cref="OpenCodeAgentSelector"/> 内部、不改客户端/协调器/后台/ACK 文件、不新增请求通道。
/// </summary>
/// <remarks>
/// <para>
/// 子会话导航：把本页会话视图的 <see cref="OpenCodeConversationView.SubagentSessionRequested"/> 接到
/// 既有处理器 <see cref="OnSubagentSessionRequested"/>（处理器在 OpenCodePage.SubagentNavigation.cs，
/// 本文件不复制其逻辑）。订阅/退订与页面 Loaded/Unloaded 对称，并由 <c>_pageActive</c> 守卫，
/// 绝不重复订阅；卸载时退订并清空控件上下文。
/// </para>
/// <para>
/// Agent 选择控件：把内部控件挂到会话级宿主，并在来源/会话上下文变化时 Configure/Clear。
/// selectionGeneration 在“切换会话（含切走再切回）”与“本地提交开始”时递增，使控件内旧 origin 失效
/// （仅禁用按钮不足以阻止在途 switch）；本地提交期间经
/// <see cref="OpenCodeAgentSelector.SetLocalSubmissionPending"/> 同步 pending，发送结束复位。
/// </para>
/// <para>
/// SwitchReported 只在报告仍属当前来源/会话时展示产品化提示；未知/需解除结果不改写任何保护状态，
/// 保护事实仍以共享 gate 为准（绝不据此推断切换成功）。旧事件绝不污染新 UI。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    private readonly OpenCodeAgentSelector _agentSelector = new();

    partial void RefreshAgentSelectorDisplay()
    {
        if (_pageActive && SourceReady && ReferenceEquals(_agentSelectorSource, _source)
            && _agentSelectorRevision == _sourceRevision && _agentSelectorSessionId == _selectedSessionId
            && _agentSelectorGeneration == _agentSelectionGeneration)
            _agentSelector.RefreshDisplay();
    }

    /// <summary>本页发布的 agent 选择代际：每次选择上下文变化 / 本地提交开始递增，作废控件内在途 origin。</summary>
    private long _agentSelectionGeneration;

    /// <summary>本地提交 operation 序号（Begin 领取递增）与当前活跃提交 token（0 = 无活跃提交）。</summary>
    private long _localSubmissionSequence;
    private long _activeLocalSubmissionToken;

    // 当前活跃提交的 owner 快照（Begin 捕获、End 全字段复核）：token 之外还绑定来源/会话/代际，
    // 使会话切换、新提交或来源换代后的旧提交绝不复位新上下文拥有的 pending/_isSending/UI。
    private OpenCodePageSource? _localSubmissionSource;
    private long _localSubmissionRevision;
    private string? _localSubmissionSessionId;
    private int _localSubmissionSessionGeneration;
    private long _localSubmissionAgentSelectionGeneration;

    // 最近一次 Configure 时控件所处的来源/会话身份（供 SwitchReported 守卫：仅当前上下文才应用提示）。
    private OpenCodePageSource? _agentSelectorSource;
    private long _agentSelectorRevision;
    private string? _agentSelectorSessionId;
    private long _agentSelectorGeneration;

    /// <summary>构造期接线：挂载控件并订阅其终态报告（页面自持控件，单次订阅）。</summary>
    partial void OnSessionContextSelected(string sessionId);

    private void InitializeAgentSelector()
    {
        AutomationProperties.SetAutomationId(_agentSelector, "OpenCodeAgentSelector");
        AutomationProperties.SetName(_agentSelector, "当前智能体与模型");
        _agentSelector.SwitchReported += OnAgentSwitchReported;
        AgentSelectorHost.Children.Add(_agentSelector);
    }

    /// <summary>Loaded 接线：订阅会话视图的子会话导航请求（由 <c>_pageActive</c> 守卫防重复）。</summary>
    private void AttachConversationEvents()
    {
        _conversationView.SubagentSessionRequested += OnSubagentSessionRequested;
        _conversationView.TerminalRequested += OpenTerminalView;
        _conversationView.NativeUiAssistantRequested += OnNativeUiAssistantRequested;
        _conversationView.NativeUiBrowserRequested += OnNativeUiBrowserRequested;
    }

    /// <summary>Unloaded 清理：退订会话视图事件，避免重复订阅与迟到回调。</summary>
    private void DetachConversationEvents()
    {
        _conversationView.SubagentSessionRequested -= OnSubagentSessionRequested;
        _conversationView.TerminalRequested -= OpenTerminalView;
        _conversationView.NativeUiAssistantRequested -= OnNativeUiAssistantRequested;
        _conversationView.NativeUiBrowserRequested -= OnNativeUiBrowserRequested;
    }

    /// <summary>
    /// 会话上下文变化（选中会话切换，含切走再切回）：递增选择代际并重新 Configure 控件；
    /// 来源未就绪或无有效会话时 Clear。
    /// </summary>
    private void ConfigureAgentSelectorForSession(string sessionId)
    {
        OnSessionContextSelected(sessionId); // L2: any real selection leaves the blank "新会话" layout.
        if (!SourceReady || _source is not { } source
            || sessionId.Length == 0 || !sessionId.StartsWith("ses", StringComparison.Ordinal))
        {
            ClearAgentSelector();
            return;
        }

        // 会话切换（含切走再切回）显式作废旧提交 owner 并同步页面发送状态：旧提交的 finally
        // 自此无权触碰新上下文，也不会以旧 _isSending 锁死新会话的发送。
        InvalidateLocalSubmissionOwner();

        _agentSelectionGeneration++;
        _agentSelectorSource = source;
        _agentSelectorRevision = _sourceRevision;
        _agentSelectorSessionId = sessionId;
        _agentSelectorGeneration = _agentSelectionGeneration;

        // 先 Clear 复位 pending/清旧 UI，再以新代际 Configure 安装新上下文（旧 origin 自此失效）。
        _agentSelector.Clear();
        _agentSelector.Configure(source, _sourceRevision, sessionId, _agentSelectionGeneration, TranscriptFirstFrame);
    }

    /// <summary>清空控件上下文并作废其身份（来源撤销 / 页面卸载 / 会话缺失）：绝不以旧事件污染新 UI。</summary>
    private void ClearAgentSelector()
    {
        // 上下文清空（来源撤销 / 页面卸载 / 会话缺失）同样显式作废提交 owner 并同步发送状态。
        InvalidateLocalSubmissionOwner();

        _agentSelectorSource = null;
        _agentSelectorRevision = 0;
        _agentSelectorSessionId = null;
        _agentSelectorGeneration = 0;
        _agentSelector.Clear();
    }

    /// <summary>
    /// 本地提交开始：领取本次提交的 operation token（End 仅清自己拥有的状态），原子发布新的
    /// selectionGeneration 作废在途 switch（仅禁用按钮不足以阻止在途 switch），并置 pending
    /// 使切换按钮在提交期间保持禁用。
    /// </summary>
    private long BeginLocalAgentSubmission()
    {
        // 先建立新 selection（递增选择代际并 Configure 控件，作废在途 switch），再捕获 owner 快照：
        // 顺序颠倒会让“建立新 selection”被当成上下文变化而误清刚创建的 owner。
        if (_selectedSessionId is { Length: > 0 } sessionId && SourceReady && _source is { } source
            && sessionId.StartsWith("ses", StringComparison.Ordinal))
        {
            _agentSelectionGeneration++;
            _agentSelectorSource = source;
            _agentSelectorRevision = _sourceRevision;
            _agentSelectorSessionId = sessionId;
            _agentSelectorGeneration = _agentSelectionGeneration;
            _agentSelector.Configure(source, _sourceRevision, sessionId, _agentSelectionGeneration);
        }

        var token = ++_localSubmissionSequence;
        _activeLocalSubmissionToken = token;
        _localSubmissionSource = _source;
        _localSubmissionRevision = _sourceRevision;
        _localSubmissionSessionId = _selectedSessionId;
        _localSubmissionSessionGeneration = _sessionGeneration;
        _localSubmissionAgentSelectionGeneration = _agentSelectionGeneration;

        _agentSelector.SetLocalSubmissionPending(true);
        return token;
    }

    /// <summary>
    /// 本地提交结束：仅当 token 与来源/会话/代际 owner 快照全部仍当前时才复位 pending
    /// （恢复切换按钮可用性判定）并交还所有权；迟到的旧提交（已切会话 / 已有新提交 / 来源换代）
    /// 零操作——绝不复位新提交拥有的 pending 或按钮。
    /// </summary>
    private bool EndLocalAgentSubmission(long token)
    {
        if (!IsLocalSubmissionOwnerCurrent(token))
        {
            return false;
        }

        _activeLocalSubmissionToken = 0;
        _localSubmissionSource = null;
        _localSubmissionRevision = 0;
        _localSubmissionSessionId = null;
        _localSubmissionSessionGeneration = 0;
        _localSubmissionAgentSelectionGeneration = 0;
        _agentSelector.SetLocalSubmissionPending(false);
        return true;
    }

    /// <summary>起发前 owner 复核：token 与来源/会话/代际快照全部仍为当前提交才为 true（只读，无副作用）。</summary>
    private bool IsLocalSubmissionOwnerCurrent(long token)
        => token != 0
        && _activeLocalSubmissionToken == token
        && ReferenceEquals(_localSubmissionSource, _source)
        && _localSubmissionRevision == _sourceRevision
        && string.Equals(_localSubmissionSessionId, _selectedSessionId, StringComparison.Ordinal)
        && _localSubmissionSessionGeneration == _sessionGeneration
        && _localSubmissionAgentSelectionGeneration == _agentSelectionGeneration;

    /// <summary>
    /// 作废当前提交 owner 并同步页面发送状态（Configure 会话切换 / Clear 上下文清空时调用）：
    /// 旧提交自此绝不复位新上下文状态；此处同步复位 pending 与 _isSending——旧 finally 已无权复位，
    /// 若不在此复位，旧提交的 _isSending 会锁死新会话的发送。
    /// </summary>
    private void InvalidateLocalSubmissionOwner()
    {
        _activeLocalSubmissionToken = 0;
        _localSubmissionSource = null;
        _localSubmissionRevision = 0;
        _localSubmissionSessionId = null;
        _localSubmissionSessionGeneration = 0;
        _localSubmissionAgentSelectionGeneration = 0;

        _agentSelector.SetLocalSubmissionPending(false);
        if (_isSending)
        {
            _isSending = false;
            RefreshComposerView();
            if (_pageActive)
            {
                UpdateComposerButtons();
            }
        }
    }

    /// <summary>
    /// 控件切换 / 解除终态报告：仅当控件仍配置在本页当前来源/会话时展示产品化提示。
    /// 未知 / 需解除结果不改写保护状态（保护事实以共享 gate 为准，绝不推断成功）。
    /// </summary>
    private void OnAgentSwitchReported(object? sender, OpenCodeAgentSwitchReportedEventArgs e)
    {
        if (!ReferenceEquals(sender, _agentSelector))
        {
            return;
        }

        // 报告不带来源/会话身份，故以“最近 Configure 身份 vs 实时上下文”判定是否仍属当前上下文；
        // 上下文已变化（切走 / 换源 / 卸载）一律丢弃，绝不污染新 UI。
        if (_agentSelectorSource is null
            || !ReferenceEquals(_agentSelectorSource, _source)
            || _agentSelectorRevision != _sourceRevision
            || !string.Equals(_agentSelectorSessionId, _selectedSessionId, StringComparison.Ordinal)
            || _agentSelectorGeneration != _agentSelectionGeneration)
        {
            return;
        }

        var severity = e.Status switch
        {
            AgentSwitchStatus.Applied or AgentSwitchStatus.Reconciled => InfoBarSeverity.Success,
            AgentSwitchStatus.OriginLost => InfoBarSeverity.Informational,
            AgentSwitchStatus.OutcomeUnknown or AgentSwitchStatus.ReconciliationRequired => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Warning,
        };
        if (severity == InfoBarSeverity.Success) Motion.ActionFeedback.Announce(_agentSelector, e.Message);
        else ShowMessage(severity, "Agent 切换", e.Message);
        RefreshComposerView();

        if (e.Status is AgentSwitchStatus.OutcomeUnknown or AgentSwitchStatus.ReconciliationRequired)
        {
            // 后台保护刷新：会话可能已进入共享 gate 保护，刷新会话交互/恢复入口显示；
            // 保护事实仍以 gate 为准（控件自身负责读取并呈现 agent 未知状态），绝不据此推断成功。
            UpdateInteractionsSection();
        }
    }
}
