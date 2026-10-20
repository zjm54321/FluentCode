using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using AIFrontend.Services.Appearance;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// OpenCode 会话消息时间线的纯 C# 展示控件（<see cref="StackPanel"/> 子类）。
/// </summary>
/// <remarks>
/// <para>
/// 渲染范围（严格按已核实消息变体，不虚构内容）：
/// user 文本气泡；assistant 的 text / reasoning / tool 部件（工具用 <see cref="OpenCodeToolPartView"/>，
/// 思考用 <see cref="OpenCodeReasoningRowView"/>）；两段文本之间的全部思考 / 工具调用折叠为一个“处理过程”分组
/// （<see cref="OpenCodeToolGroupView"/>，“已处理 X 秒”；进行中展开、结束自动收起、历史默认收起；同一回合可跨多条 assistant 消息）；
/// idle 的回合级终态标注；synthetic / system / skill / shell / compaction / 切换标记的说明行；
/// 指令通知（“指令”行，详情列出指令文件）、异步完成通知（“终端完成”“子任务完成”“后台任务完成”）与插件内部触发消息
/// 只显示一行工具样式的紧凑行（<see cref="OpenCodeTimelineChipView"/>，全文在悬停详情中）；原始后台调用行随完成通知显示最终状态。
/// question 工具是“处理过程”分组内的普通工具行（“提问”，详情为问题与所选回答）；回答在输入区的提问遮罩中进行。
/// 用户消息气泡默认最多两行，更长时可用 chevron 展开（<see cref="OpenCodeUserBubbleView"/>）。
/// 无法识别的类型只显示类型名，不猜测含义。
/// </para>
/// <para>
/// 富文本：user 文本与 assistant 的 text 部件经 <see cref="OpenCodeRichTextPresenter"/>
/// 呈现真正的 Markdown 结构（标题/粗斜体/列表/引用/代码块/表格等）与安全 HTML 片段，
/// 使用原生 <c>RichTextBlock</c> 等控件（不派生控件、不创建 WebView）；工具部件仍为原生
/// <see cref="OpenCodeToolPartView"/>。部件/消息的元素身份、稳定 key 与增量 update 回调保持不变。
/// </para>
/// <para>
/// 增量更新（<see cref="ApplyMessages"/>）：按消息 id + JSON 指纹复用元素，
/// 只对变化的消息调用就地更新、对新增消息追加、对消失消息移除；
/// 元素在重排时不离开可视化树（Remove+Insert 同步完成），
/// 因此刷新不会重置滚动位置，也不会收起用户已展开的工具部件。
/// </para>
/// <para>
/// subagent 子任务（见 <see cref="OpenCodeSubagentPresentation"/>）：工具部件
/// （<c>name == "subagent"</c>）悬停详情显示调用预览，点击经 <see cref="SubagentSessionRequested"/>
/// 向上转发导航提示；带 <c>metadata.source == "subagent"</c> 完成 metadata 的
/// assistant / synthetic / system 消息显示「子任务结果」标记。事件只含不透明 ID，不授权、不发起请求；
/// 部件被移除、替换或清空时退订并 Dispose，不产生 stale callbacks。
/// </para>
/// </remarks>
public sealed class OpenCodeConversationView : StackPanel
{
    internal event EventHandler? BindingCleared;
    private sealed class SteerRow
    {
        internal required OpenCodeInboxItem Item;
        internal readonly StackPanel Element = new() { Spacing = 4, Margin = new Thickness(0, 6, 0, 6) };
        internal readonly StackPanel Content = new() { Spacing = 4 };
        internal readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        internal readonly OpenCodeRichTextPresenter Presenter = new();
        internal readonly List<Button> Buttons = new();
        internal string Signature = string.Empty;
        internal JsonElement? RenderedPayload;
    }
    private readonly Dictionary<string, SteerRow> _steerRows = new(StringComparer.Ordinal);
    private readonly List<string> _steerOrder = new();
    private readonly HashSet<string> _renderedUserMessageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deliveredSteerIds = new(StringComparer.Ordinal);
    private string? _steerSession;
    private bool _steerBusy;
    private Func<OpenCodeInboxItem, bool>? _canWithdrawSteer;
    private Func<OpenCodeInboxItem, bool>? _canChangeSteerDelivery;
    internal event EventHandler<string>? PendingSteerWithdrawRequested;
    internal event EventHandler<OpenCodePendingDeliveryRequest>? PendingSteerDeliveryChangeRequested;

    /// <summary>View-only projection of a fenced, accepted inbox snapshot. Never creates/adopts a history DTO or sends a prompt.
    /// Only exact user/steer entries of the supplied selected session appear; durable IDs deduplicate against actual user history.</summary>
    public void ApplyAdmittedSteer(string? sessionId, IReadOnlyList<OpenCodeInboxItem>? items,
        Func<OpenCodeInboxItem, bool>? canWithdraw = null, Func<OpenCodeInboxItem, bool>? canChangeDelivery = null)
    {
        if (_steerSession != sessionId) _deliveredSteerIds.Clear();
        _steerSession = sessionId;
        _canWithdrawSteer = canWithdraw;
        _canChangeSteerDelivery = canChangeDelivery;
        var retained = new HashSet<string>(StringComparer.Ordinal);
        _steerOrder.Clear();
        if (sessionId is not null && items is not null)
            foreach (var item in items)
            {
                if (item.SessionId != sessionId || !OpenCodeQueuedPromptsView.IsSteerUser(item) || item.Id is not { Length: > 0 } id || !retained.Add(id)) continue;
                _steerOrder.Add(id);
                if (!_steerRows.TryGetValue(id, out var row))
                {
                    row = new SteerRow { Item = item };
                    row.Content.Children.Add(row.Presenter.Element);
                    var bubble = new OpenCodeUserBubbleView(row.Content, delivered: false);
                    row.Element.Children.Add(bubble.Element);
                    row.Element.Children.Add(row.Actions);
                    AutomationProperties.SetAutomationId(row.Element, "OpenCodeAdmittedSteer." + id);
                    _steerRows[id] = row;
                }
                row.Item = item;
                UpdateSteerRow(row);
            }
        var stale = new List<string>();
        foreach (var pair in _steerRows) if (!retained.Contains(pair.Key)) { Children.Remove(pair.Value.Element); stale.Add(pair.Key); }
        foreach (var id in stale) _steerRows.Remove(id);
        ReconcileSteerRows();
    }

    public void SetPendingSteerBusy(bool busy)
    {
        _steerBusy = busy;
        foreach (var row in _steerRows.Values) foreach (var button in row.Buttons) button.IsEnabled = !busy;
    }

    private bool CurrentSteer(SteerRow row) => !_steerBusy && row.Item.SessionId == _steerSession
        && row.Item.Id is { } id && _steerRows.TryGetValue(id, out var current) && ReferenceEquals(current, row)
        && !_renderedUserMessageIds.Contains(id) && !_deliveredSteerIds.Contains(id) && ReferenceEquals(row.Element.Parent, this);

    private void UpdateSteerRow(SteerRow row)
    {
        var item = row.Item;
        var withdraw = _canWithdrawSteer?.Invoke(item) == true;
        var change = _canChangeSteerDelivery?.Invoke(item) == true;
        var signature = withdraw + "|" + change;
        var samePayload = row.RenderedPayload is { } previous && item.Payload is { } current
            ? JsonElement.DeepEquals(previous, current) : row.RenderedPayload is null && item.Payload is null;
        if (signature == row.Signature && samePayload) return;
        row.Signature = signature;
        row.RenderedPayload = item.Payload;
        var rawText = OpenCodeQueuedPromptsView.ReadText(item.Payload);
        row.Presenter.Render(rawText, OpenCodeRichTextStyle.Body);
        while (row.Content.Children.Count > 1) row.Content.Children.RemoveAt(1);
        if (item.Payload is { ValueKind: JsonValueKind.Object } payload)
            foreach (var attachmentKind in new[] { "files", "agents", "skills" })
                if (payload.TryGetProperty(attachmentKind, out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var attachment in attachments.EnumerateArray())
                    {
                        index++;
                        var name = attachment.ValueKind == JsonValueKind.Object && attachment.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                        var label = attachmentKind == "files" ? "附件" : attachmentKind == "agents" ? "智能体附件" : "技能附件";
                        row.Content.Children.Add(OpenCodeInteractionUi.StyledText(label + "：" + (string.IsNullOrWhiteSpace(name) ? index.ToString(CultureInfo.InvariantCulture) : name), OpenCodeInteractionUi.CaptionStyleKey, selectable: true));
                    }
                }
        if (string.IsNullOrEmpty(rawText) && row.Content.Children.Count == 1)
            row.Content.Children.Add(OpenCodeInteractionUi.StyledText("（空消息）", OpenCodeInteractionUi.CaptionStyleKey));
        row.Actions.Children.Clear(); row.Buttons.Clear();
        if (change)
        {
            var button = new Button { Content = "投递方式", IsEnabled = !_steerBusy };
            var menu = new MenuFlyout();
            var queue = new MenuFlyoutItem { Text = "当前回合结束后发送" };
            queue.Click += (_, _) => { if (CurrentSteer(row) && _canChangeSteerDelivery?.Invoke(row.Item) == true)
                PendingSteerDeliveryChangeRequested?.Invoke(this, new(row.Item.Id!, OpenCodeDeliveryModes.Queue)); };
            menu.Items.Add(queue); button.Flyout = menu;
            AutomationProperties.SetAutomationId(button, "OpenCodeAdmittedSteerDelivery." + item.Id);
            row.Actions.Children.Add(button); row.Buttons.Add(button);
        }
        if (withdraw)
        {
            var button = new Button { Content = "撤回", IsEnabled = !_steerBusy };
            button.Click += (_, _) => { if (CurrentSteer(row) && _canWithdrawSteer?.Invoke(row.Item) == true) PendingSteerWithdrawRequested?.Invoke(this, row.Item.Id!); };
            AutomationProperties.SetAutomationId(button, "OpenCodeAdmittedSteerWithdraw." + item.Id);
            row.Actions.Children.Add(button); row.Buttons.Add(button);
        }
        row.Actions.Visibility = withdraw || change ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReconcileSteerRows()
    {
        var projected = new HashSet<UIElement>();
        foreach (var row in _steerRows.Values) projected.Add(row.Element);
        foreach (var id in _steerOrder)
        {
            if (_renderedUserMessageIds.Contains(id)) _deliveredSteerIds.Add(id);
            if (_deliveredSteerIds.Contains(id)) Children.Remove(_steerRows[id].Element);
        }
        var index = 0;
        foreach (var element in Children) if (!projected.Contains(element)) index++;
        foreach (var id in _steerOrder)
        {
            var element = _steerRows[id].Element;
            if (_deliveredSteerIds.Contains(id)) continue;
            if (index >= Children.Count || !ReferenceEquals(Children[index], element))
            {
                Children.Remove(element);
                Children.Insert(Math.Min(index, Children.Count), element);
            }
            index++;
        }
    }

    private bool IsViewProjection(UIElement element)
    {
        foreach (var row in _steerRows.Values) if (ReferenceEquals(element, row.Element)) return true;
        return false;
    }
    private sealed class Entry
    {
        public required string Key { get; init; }
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>该消息在本次快照中对应的子任务标题（用于完成卡片；无匹配为 null）。</summary>
        public string? TaskTitle { get; set; }
        public required FrameworkElement Element { get; init; }
        public required Action<OpenCodeMessage> Update { get; init; }

        /// <summary>该条目是否为指令通知的安全呈现；呈现方式改变时据其整体替换旧条目。</summary>
        public bool IsInstructionNotice { get; init; }

        /// <summary>该条目是否为内部触发消息的紧凑提示行；呈现方式改变时据其整体替换旧条目。</summary>
        public bool IsInitiatorChip { get; init; }

        /// <summary>该条目是否为异步完成通知行；呈现方式改变时据其整体替换旧条目。</summary>
        public bool IsAsyncCompletion { get; init; }

        /// <summary>assistant 条目中工具调用所关联异步完成通知的签名（变化时就地更新以刷新原始调用行）。</summary>
        public string AsyncSignature { get; set; } = string.Empty;

        /// <summary>assistant 条目的处理过程分组状态（跨消息链计算用）；其他条目为 null。</summary>
        public AssistantState? Assistant { get; init; }

        /// <summary>移除该条目（或清空）时调用：退订事件并停用子部件回调；无资源时为 null。</summary>
        public Action? Dispose { get; init; }
    }

    private sealed class PartEntry
    {
        public required string Key { get; init; }
        public required string Kind { get; init; }
        public string Fingerprint { get; set; } = string.Empty;
        public required FrameworkElement Element { get; init; }

        /// <summary>就地更新；第二参数为当前 assistant 消息 id（仅工具部件的导航事件使用，可为 null），第三参数为关联的异步完成通知。</summary>
        public required Action<OpenCodeContentPart, string?, OpenCodeAsyncCompletion?> Update { get; init; }

        /// <summary>移除该部件时调用：退订事件并 Dispose 控制器；无资源时为 null。</summary>
        public Action? Dispose { get; init; }

        /// <summary>最近一次应用的部件快照（工具分组摘要据此计算）。</summary>
        public OpenCodeContentPart? LastPart { get; set; }

        /// <summary>最近一次应用的关联异步完成通知（工具行摘要 / 分组计数据此计算）；无则 null。</summary>
        public OpenCodeAsyncCompletion? LastCompletion { get; set; }

        /// <summary>是否为可参与分组的工具行（含 question 工具：普通工具行 + 隐藏的提问锚点）。</summary>
        public bool IsGroupableTool { get; init; }

        /// <summary>元素当前所在容器（消息部件面板或某个工具组的列表）；未入树为 null。</summary>
        public Panel? Host { get; set; }

        /// <summary>reasoning 部件的“思考”行（连续思考合并时只有段首的行入树）；其他部件为 null。</summary>
        public OpenCodeReasoningRowView? Reasoning { get; init; }

        /// <summary>是否属于“处理过程”分组（思考或普通工具调用）。</summary>
        public bool IsStepPart => IsGroupableTool || Reasoning is not null;
    }

    /// <summary>一条 assistant 消息中一段连续的处理过程（思考 / 工具），对应一个分组视图。</summary>
    private sealed class StepRun
    {
        public required OpenCodeToolGroupView Group { get; init; }

        public required AssistantState Owner { get; init; }

        public List<OpenCodeToolRowModel> Tools { get; } = new();

        public int ReasoningRows { get; set; }

        public double? StartedAt { get; set; }

        public double? EndedAt { get; set; }

        /// <summary>本段之前没有可见的文本 / 提问等内容（可与上一条消息末尾的段落连成一条链）。</summary>
        public bool StartsMessage { get; set; }

        /// <summary>本段之后没有可见的文本 / 提问等内容（可与下一条消息开头的段落连成一条链）。</summary>
        public bool EndsMessage { get; set; }

        /// <summary>本段是跨消息链的后续段（头部隐藏，跟随链首展开状态）。</summary>
        public bool IsFollower { get; set; }
    }

    /// <summary>assistant 条目的分组状态（由 <see cref="LayoutParts"/> 产生，<see cref="ApplyStepChains"/> 消费）。</summary>
    private sealed class AssistantState
    {
        public OpenCodeMessage? Message { get; set; }

        public List<StepRun> Runs { get; } = new();

        /// <summary>除分组外还有可见内容（文本、提问、未识别部件、错误、重试、子任务完成卡片）。</summary>
        public bool HasBoundary { get; set; }

        public bool CompletionShown { get; set; }

        public FrameworkElement? Element { get; set; }
    }

    /// <summary>用户在分组“已结束”时显式切换过的展开状态（按链首组 ID；跨刷新 / 分页保留，清空会话时重置）。</summary>
    private readonly Dictionary<string, bool> _groupExpansion = new(StringComparer.Ordinal);

    /// <summary>上一次计算时处于“进行中”的链首组 ID（用于识别 进入运行 / 运行结束 的转换）。</summary>
    private readonly HashSet<string> _activeGroups = new(StringComparer.Ordinal);

    /// <summary>用户在本次运行期间显式切换过的展开状态（运行结束时清除；运行期间优先于自动展开）。</summary>
    private readonly Dictionary<string, bool> _runExpansion = new(StringComparer.Ordinal);

    /// <summary>当前已渲染的 question 工具锚点（key = 消息 id + “\n” + 部件 id）。</summary>
    private readonly Dictionary<string, OpenCodeQuestionAnchorHost> _questionAnchors = new(StringComparer.Ordinal);

    private bool _questionAnchorsChanged;

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前快照中「真实消息 id」的索引（仅非空 <see cref="OpenCodeMessage.Id"/>；匿名消息的
    /// 兜底 key 与重复 id 的序号兜底 key 一律不进入）。供 <see cref="ContainsRenderedMessage"/>
    /// 做逐条消息归属查询；集合本身不对外暴露、只读查询不改变渲染状态。
    /// </summary>
    private readonly HashSet<string> _renderedMessageIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 本次快照由 <see cref="OpenCodeSubagentTaskTitles.Build"/> 重建的「完成消息 id → 前序工具描述」
    /// 映射（仅作完成卡片标题；只读消费，<see cref="ClearMessages"/> 清空）。标题算法不在此实现。
    /// </summary>
    private IReadOnlyDictionary<string, string> _subagentTaskTitles =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 本次快照中的异步完成通知（key = 关联的不透明 id：shellID / childID / 任务 id；后出现的覆盖先出现的）。
    /// 原始后台调用行据此显示最终状态；<see cref="ClearMessages"/> 清空。
    /// </summary>
    private Dictionary<string, OpenCodeAsyncCompletion> _asyncCompletions = new(StringComparer.Ordinal);

    /// <summary>创建空的消息时间线视图。</summary>
    public OpenCodeConversationView()
    {
        Orientation = Orientation.Vertical;
        Spacing = 12;
        Padding = new Thickness(0, 4, 0, 4);
    }

    /// <summary>
    /// 用户点击某工具部件（<c>name == "subagent"</c>）或子任务完成行时请求只读子会话窗口。
    /// 参数只含父消息 id 与通过不透明校验的子会话 id，属纯导航提示：不授权、不直接发起任何
    /// HTTP 或子会话查询。消费方须先确认来源 / 选中代际，并以 GET child info.parentID（或祖先链）
    /// 复核归属后才加载子会话消息。
    /// </summary>
    public event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested;

    /// <summary>question 工具锚点集合发生变化（新增 / 移除）；交互视图据此重新放置提问条目。</summary>
    internal event EventHandler? QuestionAnchorsChanged;

    /// <summary>
    /// 按 Form metadata.tool 的 messageID / id 查找时间线中对应 question 工具部件的锚点；未渲染时为 null。
    /// 只读查询，不改变渲染状态。
    /// </summary>
    internal OpenCodeQuestionAnchorHost? FindQuestionAnchor(string? messageId, string? partId)
        => !string.IsNullOrEmpty(messageId) && !string.IsNullOrEmpty(partId)
            && _questionAnchors.TryGetValue(AnchorKey(messageId, partId), out var host)
                ? host
                : null;

    private static string AnchorKey(string messageId, string partId) => messageId + "\n" + partId;

    private void RaiseQuestionAnchorsChangedIfNeeded()
    {
        if (!_questionAnchorsChanged)
        {
            return;
        }

        _questionAnchorsChanged = false;
        QuestionAnchorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空全部已渲染消息（切换会话或卸载时调用）；同时退订并停用全部子部件回调。</summary>
    public void ClearMessages()
    {
        BindingCleared?.Invoke(this, EventArgs.Empty);
        ApplyAdmittedSteer(null, null);
        _renderedUserMessageIds.Clear();
        foreach (var entry in _entries)
        {
            entry.Dispose?.Invoke();
        }

        Children.Clear();
        _entries.Clear();
        _byKey.Clear();
        _renderedMessageIds.Clear();
        _subagentTaskTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        _asyncCompletions = new Dictionary<string, OpenCodeAsyncCompletion>(StringComparer.Ordinal);
        _groupExpansion.Clear();
        _activeGroups.Clear();
        _runExpansion.Clear();
        if (_questionAnchors.Count > 0)
        {
            _questionAnchors.Clear();
            _questionAnchorsChanged = true;
        }

        RaiseQuestionAnchorsChangedIfNeeded();
    }

    /// <summary>
    /// 应用一次消息投影快照（调用方已按时间升序整理）。
    /// 增量更新：未变化的消息不触碰；已渲染元素尽量复用。
    /// </summary>
    /// <param name="messages">按展示顺序（旧→新）排列的消息投影。</param>
    public void ApplyMessages(IReadOnlyList<OpenCodeMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // 客户端安全化：不改动调用方集合，逐条投影为净化副本（顺序与数量不变）。
        var sanitizedMessages = new List<OpenCodeMessage>(messages.Count);
        foreach (var message in messages)
        {
            sanitizedMessages.Add(SanitizeSyntheticInstruction(OpenCodeInstructionNoticeProjection.SanitizeForClient(message)));
        }

        messages = sanitizedMessages;

        // 异步完成通知索引：原始后台调用行（终端 / 子任务）据此显示最终状态。
        var completions = new Dictionary<string, OpenCodeAsyncCompletion>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (!IsInstructionMessage(message)
                && OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out var completion)
                && completion.Key.Length > 0)
            {
                completions[completion.Key] = completion;
            }
        }

        _asyncCompletions = completions;

        // 完成卡片标题来源：按本次快照重建映射（只读消费；标题算法由 OpenCodeSubagentTaskTitles 提供）。
        _subagentTaskTitles = OpenCodeSubagentTaskTitles.Build(messages);

        var newEntries = new List<Entry>(messages.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var renderedIds = new HashSet<string>(StringComparer.Ordinal);
        var renderedUserIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Id is { Length: > 0 } messageId)
            {
                // 仅收真实 id；匿名兜底 key 与重复 id 序号兜底 key 不进入归属索引。
                renderedIds.Add(messageId);
                if (message.Type == OpenCodeMessageTypes.User) renderedUserIds.Add(messageId);
            }

            var key = BuildKey(message, i);
            if (!seenKeys.Add(key))
            {
                // 契约中 id 必填且唯一；出现重复时加序号兜底，保证元素一对一。
                key = key + "#" + i.ToString(CultureInfo.InvariantCulture);
            }

            var fingerprint = ComputeFingerprint(message);

            // 呈现方式改变（普通/subagent ↔ 指令通知 ↔ 内部触发提示行）时同 key 不能就地复用：旧条目须整体替换。
            // 先移除旧条目（Dispose → Children → _entries → _byKey），使末尾 retained 清理不会二次
            // Dispose，也不会因 _byKey.Remove(旧 key) 误删随后新建的同 key 条目。
            var isNotice = IsInstructionMessage(message);
            var isAsync = !isNotice && OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out _);
            var isInitiator = !isNotice && !isAsync && OpenCodeStepRunPresentation.IsInternalInitiator(message);
            if (_byKey.TryGetValue(key, out var replaced)
                && (replaced.IsInstructionNotice != isNotice || replaced.IsInitiatorChip != isInitiator
                    || replaced.IsAsyncCompletion != isAsync))
            {
                replaced.Dispose?.Invoke();
                Children.Remove(replaced.Element);
                _entries.Remove(replaced);
                _byKey.Remove(key);
            }

            if (_byKey.TryGetValue(key, out var existing))
            {
                // 变更检测同时比较消息指纹与「当前子任务标题」：两者任一变化都走就地更新，
                // 保证完成卡片标题随前序工具描述变化而刷新。
                var taskTitle = ResolveTaskTitle(message);
                var asyncSignature = ComputeAsyncSignature(message);
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                    !string.Equals(existing.TaskTitle, taskTitle, StringComparison.Ordinal) ||
                    !string.Equals(existing.AsyncSignature, asyncSignature, StringComparison.Ordinal))
                {
                    existing.Update(message);
                    existing.Fingerprint = fingerprint;
                }

                existing.TaskTitle = taskTitle;
                existing.AsyncSignature = asyncSignature;
                newEntries.Add(existing);
            }
            else
            {
                var entry = CreateEntry(key, message);
                entry.Fingerprint = fingerprint;
                entry.TaskTitle = ResolveTaskTitle(message);
                entry.AsyncSignature = ComputeAsyncSignature(message);
                _byKey[key] = entry;
                newEntries.Add(entry);
            }
        }

        // 移除不再出现的消息（例如窗口化拉取时旧消息滑出）。
        var retained = new HashSet<Entry>(newEntries);
        foreach (var entry in _entries)
        {
            if (!retained.Contains(entry))
            {
                entry.Dispose?.Invoke();
                Children.Remove(entry.Element);
                _byKey.Remove(entry.Key);
            }
        }

        // 对齐顺序：元素仅在必要时 Remove+Insert（同一 UI 线程同步完成，不触发中间布局）。
        for (var i = 0; i < newEntries.Count; i++)
        {
            var element = newEntries[i].Element;
            if (i < Children.Count && ReferenceEquals(Children[i], element))
            {
                continue;
            }

            Children.Remove(element);
            Children.Insert(Math.Min(i, Children.Count), element);
        }

        for (var index = Children.Count - 1; index >= newEntries.Count; index--)
            if (!IsViewProjection(Children[index])) Children.RemoveAt(index);

        _entries.Clear();
        _entries.AddRange(newEntries);

        // 真实 id 索引按本次快照整体重建，始终与已渲染条目对应（不在循环中半更新）。
        _renderedMessageIds.Clear();
        _renderedMessageIds.UnionWith(renderedIds);
        _renderedUserMessageIds.Clear();
        _renderedUserMessageIds.UnionWith(renderedUserIds);
        ApplyStepChains();
        RaiseQuestionAnchorsChangedIfNeeded();
    }

    /// <summary>
    /// 查询给定父消息 id 是否属于当前已渲染的消息快照：严格按真实消息 id 匹配
    /// （<see cref="OpenCodeMessage.Id"/>），匿名消息与重复 id 的兜底 key 一律不参与；
    /// 空 / null 一律返回 false。只读查询，不暴露内部集合、不改变任何渲染状态。
    /// </summary>
    /// <param name="messageId">待查询的消息 id（契约内为真实 <c>msg_</c> 前缀 id）。</param>
    internal bool ContainsRenderedMessage(string? messageId)
        => !string.IsNullOrEmpty(messageId) && _renderedMessageIds.Contains(messageId);

    /// <summary>
    /// 取该消息在当前快照中对应的子任务标题（用于完成卡片）；无匹配返回 null。
    /// 只读查询，不改变渲染状态。
    /// </summary>
    private string? ResolveTaskTitle(OpenCodeMessage message)
        => message.Id is { Length: > 0 } id && _subagentTaskTitles.TryGetValue(id, out var title)
            ? title
            : null;

    /// <summary>指令消息：system 指令通知（安全投影）或 synthetic 指令文件消息（metadata.instruction.paths）。</summary>
    private static bool IsInstructionMessage(OpenCodeMessage message)
        => OpenCodeInstructionNoticeProjection.TryGetInstructionsNotice(message, out _)
            || OpenCodeStepRunPresentation.TryGetInstructionPaths(message, out _);

    /// <summary>取指令消息的安全投影（system 通知原样投影；synthetic 指令只取路径列表，绝不取正文）。</summary>
    private static bool TryGetInstructionNotice(OpenCodeMessage message, out OpenCodeInstructionNotice notice)
    {
        if (OpenCodeInstructionNoticeProjection.TryGetInstructionsNotice(message, out notice))
        {
            return true;
        }

        if (OpenCodeStepRunPresentation.TryGetInstructionPaths(message, out var paths))
        {
            notice = new OpenCodeInstructionNotice(
                message.Id,
                paths.IsDefaultOrEmpty
                    ? "已加载指令"
                    : "已加载 " + paths.Length.ToString(CultureInfo.InvariantCulture) + " 个指令文件",
                paths,
                message.Time is { Created: > 0 } time ? time.Created : (double?)null);
            return true;
        }

        notice = null!;
        return false;
    }

    /// <summary>
    /// synthetic 指令消息的客户端安全化：只保留 id / type / time.created 与 metadata.instruction.paths，
    /// 正文（指令全文）与其余字段不进入指纹、呈现或任何视图。非指令消息原样返回（同一引用）。
    /// </summary>
    private static OpenCodeMessage SanitizeSyntheticInstruction(OpenCodeMessage message)
    {
        if (OpenCodeInstructionNoticeProjection.TryGetInstructionsNotice(message, out _)
            || !OpenCodeStepRunPresentation.TryGetInstructionPaths(message, out var paths))
        {
            return message;
        }

        return new OpenCodeMessage
        {
            Id = message.Id,
            Type = message.Type,
            Time = message.Time is { } time ? new OpenCodeMessageTime { Created = time.Created } : null,
            Metadata = JsonSerializer.SerializeToElement(new { instruction = new { paths } }),
        };
    }

    /// <summary>该部件关联的异步完成通知（按 state.metadata 中的不透明 id 匹配）；无则 null。</summary>
    private OpenCodeAsyncCompletion? ResolveCompletion(OpenCodeContentPart part)
    {
        if (_asyncCompletions.Count == 0 || part.Type != OpenCodePartTypes.Tool)
        {
            return null;
        }

        foreach (var key in OpenCodeStepRunPresentation.AsyncKeys(part))
        {
            if (_asyncCompletions.TryGetValue(key, out var completion))
            {
                return completion;
            }
        }

        return null;
    }

    private static string CompletionSignature(OpenCodeAsyncCompletion? completion)
        => completion is null
            ? string.Empty
            : completion.Key + "|" + completion.State + "|" + completion.ExitCode?.ToString(CultureInfo.InvariantCulture);

    /// <summary>assistant 消息中全部工具调用所关联异步完成通知的签名（非 assistant 为空串）。</summary>
    private string ComputeAsyncSignature(OpenCodeMessage message)
    {
        if (message.Type != OpenCodeMessageTypes.Assistant || message.Content is not { Count: > 0 } parts || _asyncCompletions.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (part is not null && ResolveCompletion(part) is { } completion)
            {
                builder.Append(CompletionSignature(completion)).Append(';');
            }
        }

        return builder.ToString();
    }

    private static string BuildKey(OpenCodeMessage message, int index) =>
        message.Id is { Length: > 0 } id
            ? id
            : "anon:" + index.ToString(CultureInfo.InvariantCulture) + ":" + (message.Type ?? "?");

    private static string ComputeFingerprint(OpenCodeMessage message)
    {
        try
        {
            return JsonSerializer.Serialize(message, OpenCodeJson.Options);
        }
        catch (Exception)
        {
            // 指纹仅用于变更检测；序列化失败时退化为“每次都更新”，不影响正确性。
            return Guid.NewGuid().ToString("N");
        }
    }

    private static string ComputeFingerprint(OpenCodeContentPart part)
    {
        try
        {
            return JsonSerializer.Serialize(part, OpenCodeJson.Options);
        }
        catch (Exception)
        {
            // 指纹仅用于变更检测；序列化失败时退化为“每次都更新”，不影响正确性。
            return Guid.NewGuid().ToString("N");
        }
    }

    private Entry CreateEntry(string key, OpenCodeMessage message)
    {
        // 指令通知优先：命中即只构造安全通知呈现，绝不先建 Synthetic/Assistant 正文或 Subagent 包装。
        if (TryGetInstructionNotice(message, out var notice))
        {
            return CreateInstructionNoticeEntry(key, notice);
        }

        // 异步完成通知（后台终端 / 子任务 / 插件后台任务）：工具样式的紧凑行，完整结果在详情中。
        if (OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out _))
        {
            return CreateAsyncCompletionEntry(key, message);
        }

        // 插件内部触发消息（SLIM_INTERNAL_INITIATOR 等标记）：只显示一行紧凑提示，全文仅在悬停详情中。
        if (OpenCodeStepRunPresentation.IsInternalInitiator(message))
        {
            return CreateInitiatorChipEntry(key, message);
        }

        switch (message.Type)
        {
            case OpenCodeMessageTypes.User:
                return CreateUserEntry(key, message);
            case OpenCodeMessageTypes.Assistant:
                return CreateAssistantEntry(key, message);
            case OpenCodeMessageTypes.Idle:
                return CreateIdleEntry(key, message);
            case OpenCodeMessageTypes.Synthetic:
            case OpenCodeMessageTypes.System:
                return CreateSyntheticEntry(key, message, DescribePlainText);
            case OpenCodeMessageTypes.Skill:
                return CreateCaptionEntry(key, message, DescribeSkill);
            case OpenCodeMessageTypes.Shell:
                return CreateShellEntry(key, message);
            case OpenCodeMessageTypes.Compaction:
                return CreateCompactionEntry(key, message);
            case OpenCodeMessageTypes.AgentSwitched:
                return CreateCaptionEntry(key, message,
                    m => "已切换 agent：" + (m.Agent ?? "（未提供）"));
            case OpenCodeMessageTypes.ModelSwitched:
                return CreateCaptionEntry(key, message, DescribeModelSwitch);
            case OpenCodeMessageTypes.LocationSwitched:
                return CreateCaptionEntry(key, message,
                    m => "已切换目录：" + (m.Location?.Directory ?? "（未提供）"));
            default:
                return CreateCaptionEntry(key, message,
                    m => "（未识别的消息类型：" + (m.Type ?? "未知") + "）");
        }
    }

    /// <summary>构造仅显示安全投影的指令通知条目；无 Dispose 资源，Update 再次取安全 notice 就地刷新。</summary>
    private static Entry CreateInstructionNoticeEntry(string key, OpenCodeInstructionNotice notice)
    {
        var view = new OpenCodeInstructionNoticeView(notice);
        return new Entry
        {
            Key = key,
            Element = view.Element,
            Update = m =>
            {
                if (TryGetInstructionNotice(m, out var updated))
                {
                    view.Update(updated);
                }
            },
            IsInstructionNotice = true,
        };
    }

    /// <summary>构造内部触发消息的紧凑提示行（图标 + 短标签；全文只在悬停详情中显示）。</summary>
    private static Entry CreateInitiatorChipEntry(string key, OpenCodeMessage message)
    {
        var chip = new OpenCodeTimelineChipView("OpenCodeInternalInitiator", "\uE8BD");

        void Apply(OpenCodeMessage m)
        {
            var properties = new List<OpenCodeToolProperty>
            {
                new(0, "来源", "oh-my-opencode-slim 插件（内部自动发送）", OpenCodeToolPropertyKind.Text),
                new(0, "消息类型", m.Type ?? "未知", OpenCodeToolPropertyKind.Code),
            };
            chip.Update(
                OpenCodeStepRunPresentation.InternalInitiatorLabel,
                OpenCodeStepRunPresentation.InternalInitiatorLabel + "（插件内部发送，悬停查看全文）",
                OpenCodeStepRunPresentation.InternalInitiatorLabel,
                properties,
                OpenCodeStepRunPresentation.DescribeInternalInitiatorText(m));
        }

        Apply(message);
        return new Entry
        {
            Key = key,
            Element = chip.Element,
            Update = Apply,
            IsInitiatorChip = true,
            Dispose = chip.Dispose,
        };
    }

    /// <summary>
    /// 异步完成通知行：名称“终端完成 / 子任务完成 / 后台任务完成”、目标为命令 / 任务、摘要为退出码；
    /// 详情为键值 + 结果正文；子任务完成的详情含“打开子会话”（只转发已通过不透明校验的 childID）。
    /// </summary>
    private Entry CreateAsyncCompletionEntry(string key, OpenCodeMessage message)
    {
        var chip = new OpenCodeTimelineChipView("OpenCodeAsyncCompletion", "\uE930");
        string? parentId = message.Id;
        string? childId = null;

        void OpenChild()
        {
            if (parentId is { Length: > 0 } messageId && childId is { } child)
            {
                SubagentSessionRequested?.Invoke(this, new OpenCodeSubagentSessionRequestedEventArgs(messageId, child));
            }
        }

        void Apply(OpenCodeMessage m)
        {
            parentId = m.Id;
            if (!OpenCodeStepRunPresentation.TryReadAsyncCompletion(m, out var completion))
            {
                return;
            }

            childId = completion.ChildSessionId is { } child && OpenCodeSubagentPresentation.IsOpaqueSessionId(child) ? child : null;
            var content = OpenCodeStepRunPresentation.DescribeAsyncCompletion(completion);
            if (childId is null && content.LinkText is not null)
            {
                content = content with { LinkText = null };
            }

            chip.UpdateRow(content, childId is null ? null : OpenChild);
        }

        Apply(message);
        return new Entry
        {
            Key = key,
            Element = chip.Element,
            Update = Apply,
            IsAsyncCompletion = true,
            Dispose = () =>
            {
                childId = null;
                chip.Dispose();
            },
        };
    }

    private Entry CreateUserEntry(string key, OpenCodeMessage message)
    {
        var presenter = new OpenCodeRichTextPresenter();
        presenter.Render(message.Text, OpenCodeRichTextStyle.Body);

        // 默认最多两行，更长时用 chevron 展开 / 收起（H14）；呈现器实例与 key 跨更新保持。
        var bubble = new OpenCodeUserBubbleView(presenter.Element, delivered: true);
        bubble.Element.Margin = new Thickness(0, 6, 0, 6);

        return new Entry
        {
            Key = key,
            Element = bubble.Element,
            Update = m => presenter.Render(m.Text, OpenCodeRichTextStyle.Body),
        };
    }

    private Entry CreateAssistantEntry(string key, OpenCodeMessage message)
    {
        var subagentMarker = CreateSubagentMarker(HorizontalAlignment.Left);
        var partsPanel = new StackPanel { Spacing = 8 };
        var errorBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            IsClosable = false,
            IsOpen = false,
        };
        var retryText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var root = new StackPanel
        {
            Spacing = 8,
            Children = { subagentMarker, partsPanel, errorBar, retryText },
        };

        // 完成卡片控制器包裹原内容 root：Element 身份稳定，普通内容/卡片切换与折叠状态由控制器负责；
        // 导航只转发已通过校验的 childID，空父消息 id 不发。
        var subagentCompletion = new OpenCodeSubagentCompletionView(
            root,
            childId =>
            {
                if (message.Id is { Length: > 0 } messageId)
                {
                    SubagentSessionRequested?.Invoke(
                        this,
                        new OpenCodeSubagentSessionRequestedEventArgs(messageId, childId));
                }
            });

        var partEntries = new List<PartEntry>();
        var groups = new Dictionary<string, OpenCodeToolGroupView>(StringComparer.Ordinal);
        var state = new AssistantState { Element = subagentCompletion.Element };

        void Apply(OpenCodeMessage m)
        {
            UpdateAssistant(m, partsPanel, partEntries, groups, state, subagentMarker, errorBar, retryText);
            // 旧消息级标记让位给完成卡片，避免重复展示；普通消息仍走完整原内容路径。
            subagentMarker.Visibility = Visibility.Collapsed;
            var completion = OpenCodeSubagentPresentation.TryReadCompletionMetadata(m.Metadata, out var info) ? info : null;
            state.CompletionShown = completion is not null;
            subagentCompletion.Update(completion, ResolveTaskTitle(m));
        }

        Apply(message);

        return new Entry
        {
            Key = key,
            Element = subagentCompletion.Element,
            Assistant = state,
            Update = Apply,
            Dispose = () =>
            {
                foreach (var partEntry in partEntries)
                {
                    partEntry.Dispose?.Invoke();
                }

                foreach (var group in groups.Values)
                {
                    group.Dispose();
                }

                subagentCompletion.Dispose();
            },
        };
    }

    private void UpdateAssistant(
        OpenCodeMessage message,
        StackPanel partsPanel,
        List<PartEntry> partEntries,
        Dictionary<string, OpenCodeToolGroupView> groups,
        AssistantState state,
        TextBlock subagentMarker,
        InfoBar errorBar,
        TextBlock retryText)
    {
        ApplySubagentMarker(subagentMarker, message);
        state.Message = message;

        var parts = message.Content ?? new List<OpenCodeContentPart>();
        var newEntries = new List<PartEntry>(parts.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var kind = part.Type ?? "?";
            var partKey = part.Id is { Length: > 0 } id
                ? id
                : kind + ":" + i.ToString(CultureInfo.InvariantCulture);
            if (!seenKeys.Add(partKey))
            {
                partKey = partKey + "#" + i.ToString(CultureInfo.InvariantCulture);
            }

            var completion = ResolveCompletion(part);
            var fingerprint = ComputeFingerprint(part) + "\n" + CompletionSignature(completion);

            PartEntry? existing = null;
            foreach (var candidate in partEntries)
            {
                if (candidate.Key == partKey)
                {
                    existing = candidate;
                    break;
                }
            }

            if (existing is not null && existing.Kind == kind)
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    existing.Update(part, message.Id, completion);
                    existing.Fingerprint = fingerprint;
                    existing.LastPart = part;
                    existing.LastCompletion = completion;
                }

                newEntries.Add(existing);
            }
            else
            {
                if (existing is not null)
                {
                    // 同一 id 的部件类型发生变化（罕见）：退订旧控制器后移除元素并重建。
                    existing.Dispose?.Invoke();
                    DetachPart(existing);
                    partEntries.Remove(existing);
                }

                var entry = CreatePartEntry(partKey, kind, part, message.Id, completion);
                entry.Fingerprint = fingerprint;
                entry.LastPart = part;
                entry.LastCompletion = completion;
                newEntries.Add(entry);
            }
        }

        // 移除消失的部件。
        var retained = new HashSet<PartEntry>(newEntries);
        foreach (var entry in partEntries)
        {
            if (!retained.Contains(entry))
            {
                entry.Dispose?.Invoke();
                DetachPart(entry);
            }
        }

        LayoutParts(message, partsPanel, newEntries, groups, state);

        partEntries.Clear();
        partEntries.AddRange(newEntries);
        state.HasBoundary |= message.Error is not null || message.Retry is not null;

        // 助手消息级错误（已核实的结构化错误字段；出现即如实展示，不推断请求成败）。
        if (message.Error is { } error)
        {
            var text = string.IsNullOrWhiteSpace(error.Message) ? "（服务端未提供错误详情）" : error.Message!;
            var meta = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(error.Type))
            {
                meta.Add("type=" + error.Type);
            }
            if (error.Status is { } statusCode)
            {
                meta.Add("HTTP " + statusCode.ToString(CultureInfo.InvariantCulture));
            }
            if (meta.Count > 0)
            {
                text += "（" + string.Join(", ", meta) + "）";
            }

            errorBar.Title = "助手错误";
            errorBar.Message = text;
            errorBar.IsOpen = true;
        }
        else
        {
            errorBar.IsOpen = false;
        }

        // 重试信息：仅陈述服务端已记录的字段，不解释为最终状态。
        if (message.Retry is { } retry)
        {
            var text = retry.Attempt is { } attempt
                ? "服务端记录了重试（第 " + attempt.ToString(CultureInfo.InvariantCulture) + " 次）"
                : "服务端记录了重试";
            if (!string.IsNullOrWhiteSpace(retry.Error?.Message))
            {
                text += "；最近错误：" + retry.Error!.Message;
            }

            retryText.Text = text;
            retryText.Visibility = Visibility.Visible;
        }
        else
        {
            retryText.Visibility = Visibility.Collapsed;
        }
    }

    private static void DetachPart(PartEntry entry)
    {
        if (entry.Host is { } host)
        {
            host.Children.Remove(entry.Element);
            entry.Host = null;
        }
    }

    /// <summary>
    /// 部件布局（F4 / F5）：两段可见内容（文本、提问锚点、未识别部件）之间的全部思考与普通工具调用放入一个
    /// “处理过程”分组（组 ID = 段内首个可见部件 key；任意数量）；连续的思考部件合并为一行“思考”。
    /// 其余部件直接位于部件面板。元素只在必要时 Remove+Insert，换容器前总是先离开旧容器。
    /// 分组摘要与展开状态在全部消息就绪后由 <see cref="ApplyStepChains"/> 统一计算（同一回合可跨多条 assistant 消息）。
    /// </summary>
    private void LayoutParts(
        OpenCodeMessage message,
        StackPanel partsPanel,
        List<PartEntry> entries,
        Dictionary<string, OpenCodeToolGroupView> groups,
        AssistantState state)
    {
        var layout = new List<UIElement>(entries.Count);
        var boundary = new List<bool>(entries.Count);
        var targets = new Dictionary<PartEntry, Panel?>();
        var runs = new List<(StepRun Run, List<UIElement> Items, int LayoutIndex)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var messageStreaming = message.Time is { Completed: null };

        var i = 0;
        while (i < entries.Count)
        {
            var entry = entries[i];
            if (!entry.IsStepPart)
            {
                layout.Add(entry.Element);
                boundary.Add(!(entry.Kind == OpenCodePartTypes.Text && string.IsNullOrWhiteSpace(entry.LastPart?.Text)));
                targets[entry] = partsPanel;
                i++;
                continue;
            }

            var start = i;
            while (i < entries.Count && entries[i].IsStepPart)
            {
                i++;
            }

            var runEndsParts = i == entries.Count;
            var shown = new List<PartEntry>();
            var tools = new List<OpenCodeToolRowModel>();
            var reasoningRows = 0;
            double? startedAt = null;
            double? endedAt = null;
            PartEntry? lead = null;
            var texts = new List<string?>();

            var leadInProgress = false;

            void FlushReasoning(bool atRunEnd)
            {
                if (lead?.Reasoning is { } row)
                {
                    row.Update(
                        OpenCodeStepRunPresentation.MergeReasoning(texts),
                        leadInProgress || (atRunEnd && runEndsParts && messageStreaming),
                        QualifyKey(message, lead));
                }

                lead = null;
                leadInProgress = false;
                texts.Clear();
            }

            for (var k = start; k < i; k++)
            {
                var part = entries[k];
                targets[part] = null;
                if (part.Reasoning is not null)
                {
                    var text = part.LastPart?.Text;

                    // S2：仍在生成的思考（消息未完成，且部件 time.completed 缺失；无时间字段时以“是消息最后一个部件”判断）
                    // 即使文本为空也立即显示“思考中…”行；已结束的空白思考（如仅有加密状态）不显示。
                    var inProgress = messageStreaming
                        && (part.LastPart?.Time is { Completed: null } || (part.LastPart?.Time is null && k == entries.Count - 1));
                    if (string.IsNullOrWhiteSpace(text) && !inProgress)
                    {
                        continue;
                    }

                    if (lead is null)
                    {
                        lead = part;
                        shown.Add(part);
                        reasoningRows++;
                    }

                    leadInProgress |= inProgress;
                    texts.Add(text);
                    continue;
                }

                FlushReasoning(atRunEnd: false);
                var snapshot = part.LastPart ?? new OpenCodeContentPart();
                tools.Add(OpenCodeToolRowPresentation.Build(snapshot, part.LastCompletion));
                shown.Add(part);
                if (snapshot.Time is { } time)
                {
                    if (OpenCodeStepRunPresentation.IsPlausibleTimestamp(time.Created))
                    {
                        startedAt = startedAt is { } s ? Math.Min(s, time.Created) : time.Created;
                    }

                    if (OpenCodeStepRunPresentation.IsPlausibleTimestamp(time.Completed))
                    {
                        endedAt = endedAt is { } e ? Math.Max(e, time.Completed!.Value) : time.Completed;
                    }
                }
            }

            FlushReasoning(atRunEnd: true);
            if (shown.Count == 0)
            {
                // 只有空白思考：不显示分组。
                continue;
            }

            // 组 ID 必须全局唯一（H1）：v2 的 text / reasoning 部件没有 id，其兜底 key（“reasoning:0”）在每条消息中重复，
            // 若直接作为组 ID，展开状态（按链首组 ID 记住）会被所有同 key 的组共享——点一个组会切换全部组。
            var groupId = QualifyKey(message, shown[0]);
            if (!groups.TryGetValue(groupId, out var group))
            {
                group = new OpenCodeToolGroupView(groupId, OnStepGroupToggled);
                groups[groupId] = group;
            }

            var run = new StepRun
            {
                Group = group,
                Owner = state,
                ReasoningRows = reasoningRows,
                StartedAt = startedAt,
                EndedAt = endedAt,
                StartsMessage = !boundary.Contains(true),
            };
            run.Tools.AddRange(tools);

            var items = new List<UIElement>(shown.Count);
            foreach (var part in shown)
            {
                items.Add(part.Element);
                targets[part] = group.ItemsHost;
            }

            used.Add(groupId);
            runs.Add((run, items, layout.Count));
            layout.Add(group.Element);
            boundary.Add(false);
        }

        // 段首 / 段尾判定与消息级时间兜底（思考部件没有时间戳：段在消息开头 / 结尾时用消息创建 / 完成时间）。
        state.Runs.Clear();
        foreach (var (run, _, layoutIndex) in runs)
        {
            run.EndsMessage = !boundary.GetRange(layoutIndex + 1, boundary.Count - layoutIndex - 1).Contains(true);
            if (run.StartsMessage && message.Time is { } messageTime
                && OpenCodeStepRunPresentation.IsPlausibleTimestamp(messageTime.Created))
            {
                run.StartedAt = run.StartedAt is { } s ? Math.Min(s, messageTime.Created) : messageTime.Created;
            }

            if (run.EndsMessage && message.Time?.Completed is { } completed
                && OpenCodeStepRunPresentation.IsPlausibleTimestamp(completed))
            {
                run.EndedAt = run.EndedAt is { } e ? Math.Max(e, completed) : completed;
            }

            state.Runs.Add(run);
        }

        state.HasBoundary = boundary.Contains(true);

        // 换容器（或不再显示）的部件先离开旧容器（测试宿主与 WinUI 都不允许一个元素同时有两个父级）。
        foreach (var entry in entries)
        {
            if (entry.Host is { } host && !ReferenceEquals(host, targets[entry]))
            {
                host.Children.Remove(entry.Element);
                entry.Host = null;
            }
        }

        // 不再使用的组：移出面板并停用。
        var stale = new List<string>();
        foreach (var pair in groups)
        {
            if (!used.Contains(pair.Key))
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var key in stale)
        {
            partsPanel.Children.Remove(groups[key].Element);
            groups[key].Dispose();
            groups.Remove(key);
        }

        foreach (var (run, items, _) in runs)
        {
            AlignChildren(run.Group.ItemsHost, items);
        }

        AlignChildren(partsPanel, layout);
        foreach (var entry in entries)
        {
            entry.Host = targets[entry];
        }
    }

    /// <summary>
    /// 部件在整个时间线中唯一的 key：有真实部件 id（工具调用 id、带 id 的思考）时原样使用；
    /// 否则（v2 的 text / reasoning 部件无 id，兜底 key 为“类型:序号”）加上所属消息 id 前缀。
    /// </summary>
    private static string QualifyKey(OpenCodeMessage message, PartEntry entry)
        => entry.LastPart?.Id is { Length: > 0 }
            ? entry.Key
            : (message.Id is { Length: > 0 } id ? id : "anon") + "/" + entry.Key;

    private void OnStepGroupToggled(string groupId, bool expanded)
    {
        // 运行期间的手动切换只作用于本次运行（结束时仍自动收起）；结束后的切换跨刷新记住。
        if (_activeGroups.Contains(groupId))
        {
            _runExpansion[groupId] = expanded;
        }
        else
        {
            _groupExpansion[groupId] = expanded;
        }

        ApplyStepChains();
    }

    /// <summary>
    /// 展开状态规则：进入运行（该链首组 ID 上次不在进行中）时自动展开；运行期间用户手动切换后以用户为准；
    /// 运行结束时自动收起（并清除本次运行的手动状态）；加载时已结束的历史分组默认收起，结束后的手动切换被记住。
    /// </summary>
    private bool ResolveExpansion(string leaderId, bool active, HashSet<string> nowActive)
    {
        var wasActive = _activeGroups.Contains(leaderId);
        if (active)
        {
            nowActive.Add(leaderId);
            if (!wasActive)
            {
                _runExpansion.Remove(leaderId);
                _groupExpansion.Remove(leaderId);
                return true;
            }

            return !_runExpansion.TryGetValue(leaderId, out var duringRun) || duringRun;
        }

        if (wasActive)
        {
            _runExpansion.Remove(leaderId);
            _groupExpansion.Remove(leaderId);
            return false;
        }

        return _groupExpansion.TryGetValue(leaderId, out var userExpanded) && userExpanded;
    }

    /// <summary>
    /// 跨消息计算处理过程分组：同一回合按步骤拆成多条 assistant 消息时，消息 A 末尾的段与紧随其后的消息 B 开头的段
    /// （两者之间没有可见文本 / 提问 / 其他消息）连成一条链，链首显示合并摘要（“已处理 X 秒”，进行中“处理中… N 秒”），
    /// 后续段隐藏头部并跟随链首的展开状态（进行中自动展开、结束自动收起、历史默认收起；规则见 ResolveExpansion）。
    /// 只剩隐藏的后续段（或什么都不显示）的 assistant 条目整体折叠，避免空行间距。
    /// </summary>
    private void ApplyStepChains()
    {
        var chains = new List<List<StepRun>>();
        List<StepRun>? open = null;
        var openLastIndex = -1;
        for (var index = 0; index < _entries.Count; index++)
        {
            var state = _entries[index].Assistant;
            if (state is null)
            {
                open = null;
                continue;
            }

            if (state.Runs.Count == 0)
            {
                if (state.HasBoundary || state.CompletionShown)
                {
                    open = null;
                }
                else if (open is not null)
                {
                    // 尚无可见内容的 assistant 消息（新步骤刚开始）：不打断链。
                    openLastIndex = index;
                }

                continue;
            }

            for (var r = 0; r < state.Runs.Count; r++)
            {
                var run = state.Runs[r];
                if (r == 0 && run.StartsMessage && !state.CompletionShown && open is not null)
                {
                    open.Add(run);
                }
                else
                {
                    open = new List<StepRun> { run };
                    chains.Add(open);
                }

                openLastIndex = index;
            }

            if (!state.Runs[^1].EndsMessage || state.CompletionShown)
            {
                open = null;
            }
        }

        // 回合仍在进行：链在快照末尾仍开放，且最后一条 assistant 消息尚未完成。
        List<StepRun>? activeChain = null;
        if (open is not null && openLastIndex == _entries.Count - 1
            && _entries[openLastIndex].Assistant?.Message?.Time is { Completed: null })
        {
            activeChain = open;
        }

        var now = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nowActive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in chains)
        {
            var tools = new List<OpenCodeToolRowModel>();
            var reasoningRows = 0;
            double? startedAt = null;
            double? endedAt = null;
            foreach (var run in chain)
            {
                tools.AddRange(run.Tools);
                reasoningRows += run.ReasoningRows;
                if (run.StartedAt is { } s)
                {
                    startedAt = startedAt is { } current ? Math.Min(current, s) : s;
                }

                if (run.EndedAt is { } e)
                {
                    endedAt = endedAt is { } current ? Math.Max(current, e) : e;
                }
            }

            var summary = OpenCodeStepRunPresentation.Build(
                tools,
                reasoningRows,
                startedAt,
                endedAt,
                ReferenceEquals(chain, activeChain),
                now);
            var leaderId = chain[0].Group.GroupId;
            var expanded = ResolveExpansion(leaderId, summary.IsActive, nowActive);
            for (var k = 0; k < chain.Count; k++)
            {
                chain[k].IsFollower = k > 0;
                chain[k].Group.Update(summary, expanded, headerVisible: k == 0);
            }
        }

        // 不再出现的进行中组（消息滑出窗口等）不保留运行状态。
        foreach (var stale in _activeGroups)
        {
            if (!nowActive.Contains(stale))
            {
                _runExpansion.Remove(stale);
            }
        }

        _activeGroups.Clear();
        _activeGroups.UnionWith(nowActive);

        foreach (var entry in _entries)
        {
            if (entry.Assistant is not { Element: { } element } state)
            {
                continue;
            }

            var hasVisibleRun = false;
            foreach (var run in state.Runs)
            {
                if (!run.IsFollower || run.Group.IsExpanded)
                {
                    hasVisibleRun = true;
                    break;
                }
            }

            var visible = state.HasBoundary || state.CompletionShown || hasVisibleRun;
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        ReconcileSteerRows();
    }

    private static void AlignChildren(Panel panel, List<UIElement> elements)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], element))
            {
                continue;
            }

            panel.Children.Remove(element);
            panel.Children.Insert(Math.Min(i, panel.Children.Count), element);
        }

        while (panel.Children.Count > elements.Count)
        {
            panel.Children.RemoveAt(panel.Children.Count - 1);
        }
    }

    private PartEntry CreatePartEntry(string partKey, string kind, OpenCodeContentPart part, string? parentMessageId, OpenCodeAsyncCompletion? completion)
    {
        switch (kind)
        {
            case OpenCodePartTypes.Text:
            {
                var presenter = new OpenCodeRichTextPresenter();
                presenter.Render(part.Text, OpenCodeRichTextStyle.Body);
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    Element = presenter.Element,
                    Update = (p, _, _) => presenter.Render(p.Text, OpenCodeRichTextStyle.Body),
                };
            }

            case OpenCodePartTypes.Reasoning:
            {
                // “思考”行（F4）：外观同工具行，完整文本在悬停详情中；连续思考由 LayoutParts 合并到段首行。
                var row = new OpenCodeReasoningRowView();
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    Element = row.Element,
                    Reasoning = row,
                    // 文本由 LayoutParts 按合并结果写入（LastPart 已由调用方更新）。
                    Update = (_, _, _) => { },
                    Dispose = row.Dispose,
                };
            }

            case OpenCodePartTypes.Tool when OpenCodeToolRowPresentation.IsQuestionTool(part):
            {
                // question 工具（G3 / D3）：“处理过程”分组内的普通工具行（“提问”，详情为问题 + 所选回答）。
                // 同时保留一个不可见的提问锚点：交互视图仍把匹配的待处理 Form 条目放入其中（不在时间线中显示独立卡片），
                // 回答在输入区的提问遮罩中进行；提交 / 取消 / 重查路径不变。
                var view = new OpenCodeToolPartView();
                view.Update(part, parentMessageId, completion);
                EventHandler<OpenCodeSubagentSessionRequestedEventArgs> questionHandler =
                    (_, e) => SubagentSessionRequested?.Invoke(this, e);
                view.SubagentSessionRequested += questionHandler;
                var host = new OpenCodeQuestionAnchorHost { Visibility = Visibility.Collapsed };
                host.UpdateFallback(OpenCodeToolRowPresentation.BuildQuestionFallback(part));
                var questionRoot = new StackPanel { Spacing = 0 };
                questionRoot.Children.Add(view.Element);
                questionRoot.Children.Add(host);
                string? anchorKey = null;
                if (parentMessageId is { Length: > 0 } anchorMessageId && part.Id is { Length: > 0 } anchorPartId)
                {
                    anchorKey = AnchorKey(anchorMessageId, anchorPartId);
                    _questionAnchors[anchorKey] = host;
                    _questionAnchorsChanged = true;
                }

                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    IsGroupableTool = true,
                    Element = questionRoot,
                    Update = (p, messageId, c) =>
                    {
                        view.Update(p, messageId, c);
                        host.UpdateFallback(OpenCodeToolRowPresentation.BuildQuestionFallback(p));
                    },
                    Dispose = () =>
                    {
                        view.SubagentSessionRequested -= questionHandler;
                        view.Dispose();
                        if (anchorKey is not null
                            && _questionAnchors.TryGetValue(anchorKey, out var registered)
                            && ReferenceEquals(registered, host))
                        {
                            _questionAnchors.Remove(anchorKey);
                            _questionAnchorsChanged = true;
                        }
                    },
                };
            }

            case OpenCodePartTypes.Tool:
            {
                var view = new OpenCodeToolPartView();
                view.Update(part, parentMessageId, completion);
                // subagent 工具行固定详情时的「打开子会话」只向上转发；本视图在移除/替换时退订并 Dispose（无 stale callbacks）。
                EventHandler<OpenCodeSubagentSessionRequestedEventArgs> handler =
                    (_, e) => SubagentSessionRequested?.Invoke(this, e);
                view.SubagentSessionRequested += handler;
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    IsGroupableTool = true,
                    // 入树的是工具行根元素（同一实例）；控制器由 Update 闭包持有，节点身份跨轮询保持。
                    Element = view.Element,
                    Update = (p, messageId, c) => view.Update(p, messageId, c),
                    Dispose = () =>
                    {
                        view.SubagentSessionRequested -= handler;
                        view.Dispose();
                    },
                };
            }

            default:
            {
                var text = new TextBlock
                {
                    FontSize = 12,
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                    Text = "（未识别的部件类型：" + (part.Type ?? "未知") + "）",
                };
                return new PartEntry
                {
                    Key = partKey,
                    Kind = kind,
                    Element = text,
                    Update = (_, _, _) => { },
                };
            }
        }
    }

    private Entry CreateCaptionEntry(string key, OpenCodeMessage message, Func<OpenCodeMessage, string> describe)
    {
        var text = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        text.Text = describe(message);

        return new Entry
        {
            Key = key,
            Element = text,
            Update = m => text.Text = describe(m),
        };
    }

    /// <summary>
    /// synthetic / system 说明行：附带「子任务结果」消息级标记（构造时折叠，仅完成 metadata 命中时显示）。
    /// </summary>
    private Entry CreateSyntheticEntry(string key, OpenCodeMessage message, Func<OpenCodeMessage, string> describe)
    {
        var subagentMarker = CreateSubagentMarker(HorizontalAlignment.Center);
        var text = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        var root = new StackPanel
        {
            Spacing = 2,
            Children = { subagentMarker, text },
        };

        // 完成卡片控制器包裹原内容 root；导航只转发已通过校验的 childID，空父消息 id 不发。
        var subagentCompletion = new OpenCodeSubagentCompletionView(
            root,
            childId =>
            {
                if (message.Id is { Length: > 0 } messageId)
                {
                    SubagentSessionRequested?.Invoke(
                        this,
                        new OpenCodeSubagentSessionRequestedEventArgs(messageId, childId));
                }
            });

        void Apply(OpenCodeMessage m)
        {
            text.Text = describe(m);
            // 旧消息级标记让位给完成卡片，避免重复展示。
            subagentMarker.Visibility = Visibility.Collapsed;
            subagentCompletion.Update(
                OpenCodeSubagentPresentation.TryReadCompletionMetadata(m.Metadata, out var info) ? info : null,
                ResolveTaskTitle(m));
        }

        Apply(message);
        return new Entry
        {
            Key = key,
            Element = subagentCompletion.Element,
            Update = Apply,
            Dispose = subagentCompletion.Dispose,
        };
    }

    /// <summary>创建「子任务结果」消息级标记（构造时折叠；同一实例跨增量更新）。</summary>
    private static TextBlock CreateSubagentMarker(HorizontalAlignment alignment) => new()
    {
        FontSize = 12,
        Opacity = 0.75,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = alignment,
        IsTextSelectionEnabled = true,
        Visibility = Visibility.Collapsed,
    };

    /// <summary>
    /// 按已核准形状（<c>metadata.source == "subagent"</c> 且 childID 通过不透明校验）更新标记；
    /// 不匹配（含 missing/malformed）时折叠，消息其余内容按通用路径原样展示。
    /// </summary>
    private static void ApplySubagentMarker(TextBlock marker, OpenCodeMessage message)
    {
        if (OpenCodeSubagentPresentation.TryReadCompletionMetadata(message.Metadata, out var completion))
        {
            marker.Text = OpenCodeSubagentPresentation.DescribeCompletionMarker(completion);
            marker.Visibility = Visibility.Visible;
        }
        else
        {
            marker.Visibility = Visibility.Collapsed;
        }
    }

    private Entry CreateShellEntry(string key, OpenCodeMessage message)
    {
        var caption = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        var outputText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        // 等宽字体跟随“外观”设置（实时更新），不硬编码字体名。
        AppFonts.Apply(outputText, AppFontSlot.Monospace);
        var outputExpander = new Expander
        {
            Header = new TextBlock { Text = "输出", FontSize = 12, Opacity = 0.75 },
            Content = new ScrollViewer
            {
                Content = outputText,
                MaxHeight = 240,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        var root = new StackPanel
        {
            Spacing = 6,
            Children = { caption, outputExpander },
        };

        void Apply(OpenCodeMessage m)
        {
            caption.Text = DescribeShell(m);
            var output = m.Output?.Output;
            if (string.IsNullOrEmpty(output))
            {
                outputExpander.Visibility = Visibility.Collapsed;
            }
            else
            {
                outputText.Text = output;
                outputExpander.Visibility = Visibility.Visible;
            }
        }

        Apply(message);
        return new Entry { Key = key, Element = root, Update = Apply };
    }

    private Entry CreateCompactionEntry(string key, OpenCodeMessage message)
    {
        var caption = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };
        var summaryText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var summaryExpander = new Expander
        {
            Header = new TextBlock { Text = "摘要", FontSize = 12, Opacity = 0.75 },
            Content = summaryText,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        var root = new StackPanel
        {
            Spacing = 6,
            Children = { caption, summaryExpander },
        };

        void Apply(OpenCodeMessage m)
        {
            caption.Text = DescribeCompaction(m);
            if (string.IsNullOrWhiteSpace(m.Summary))
            {
                summaryExpander.Visibility = Visibility.Collapsed;
            }
            else
            {
                summaryText.Text = m.Summary;
                summaryExpander.Visibility = Visibility.Visible;
            }
        }

        Apply(message);
        return new Entry { Key = key, Element = root, Update = Apply };
    }

    /// <summary>回合结束：居中分隔线 + “回合结束 · 成功 · 12:04”；可读名称保留“回合结束：成功”。</summary>
    private static Entry CreateIdleEntry(string key, OpenCodeMessage message)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(12, 0, 12, 0),
        };
        ApplyStyle(text, "CaptionSubtleTextBlockStyle");
        var leftLine = CreateSeparatorLine();
        var rightLine = CreateSeparatorLine();
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(leftLine);
        row.Children.Add(text);
        row.Children.Add(rightLine);
        SetIdleColumns(row, leftLine, text, rightLine);

        void Apply(OpenCodeMessage m)
        {
            text.Text = DescribeIdleSeparator(m);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, DescribeIdle(m).Trim('—', ' '));
        }

        Apply(message);
        return new Entry { Key = key, Element = row, Update = Apply };
    }

    private static string DescribeIdleSeparator(OpenCodeMessage message)
    {
        var outcome = DescribeIdle(message).Trim('—', ' ');
        const string prefix = "回合结束：";
        var label = outcome.StartsWith(prefix, StringComparison.Ordinal) ? outcome[prefix.Length..] : outcome;
        var text = "回合结束 · " + label;
        if (message.Time is { Created: > 0 } time)
        {
            try
            {
                text += " · " + DateTimeOffset.FromUnixTimeMilliseconds((long)time.Created).ToLocalTime()
                    .ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 时间超出范围时不显示时间。
            }
        }

        return text;
    }

    private static string DescribeIdle(OpenCodeMessage message)
    {
        var outcome = message.Outcome switch
        {
            OpenCodeSessionOutcomes.Succeeded => "成功",
            OpenCodeSessionOutcomes.Failed => "失败",
            OpenCodeSessionOutcomes.Interrupted => "已中断",
            null => "（未提供 outcome）",
            var other => "未识别 outcome：" + other,
        };

        // idle 是已核实的“回合级”终态边界，不代表任何单次 prompt 的请求级成败。
        return "— 回合结束：" + outcome + " —";
    }

    private static string DescribePlainText(OpenCodeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            return message.Text!;
        }

        return string.IsNullOrWhiteSpace(message.Description) ? "（无文本）" : message.Description!;
    }

    private static string DescribeSkill(OpenCodeMessage message)
    {
        var name = message.Name ?? message.Skill ?? "（未命名）";
        var text = string.IsNullOrWhiteSpace(message.Text) ? string.Empty : "：" + message.Text;
        return "技能调用：" + name + text;
    }

    private static string DescribeModelSwitch(OpenCodeMessage message)
    {
        var model = message.Model?.Id ?? "（未提供）";
        var provider = string.IsNullOrWhiteSpace(message.Model?.ProviderId)
            ? string.Empty
            : "（provider " + message.Model!.ProviderId + "）";
        return "已切换模型：" + model + provider;
    }

    private static string DescribeShell(OpenCodeMessage message)
    {
        var status = message.Status switch
        {
            OpenCodeShellStatuses.Running => "运行中",
            OpenCodeShellStatuses.Exited => "已退出",
            OpenCodeShellStatuses.Timeout => "超时",
            OpenCodeShellStatuses.Killed => "已终止",
            null => "（未提供状态）",
            var other => "未识别状态：" + other,
        };

        var text = "Shell：" + (message.Command ?? "（无命令）") + "（" + status + "）";

        // exit 的原始值可能是 number 或 "Infinity"/"-Infinity"/"NaN" 字符串，按原样展示。
        if (message.Exit is { } exit && exit.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            text += "，退出码 " + exit;
        }

        return text;
    }

    private static string DescribeCompaction(OpenCodeMessage message)
    {
        var status = message.Status switch
        {
            OpenCodeCompactionStatuses.Running => "进行中",
            OpenCodeCompactionStatuses.Completed => "已完成",
            OpenCodeCompactionStatuses.Failed => "失败",
            null => "（未提供状态）",
            var other => "未识别状态：" + other,
        };

        var reason = message.Reason switch
        {
            OpenCodeCompactionReasons.Auto => "自动",
            OpenCodeCompactionReasons.Manual => "手动",
            null => "（未提供原因）",
            var other => "未识别原因：" + other,
        };

        var text = "上下文压缩：" + status + "（原因：" + reason + "）";

        if (message.Status == OpenCodeCompactionStatuses.Failed &&
            !string.IsNullOrWhiteSpace(message.Error?.Message))
        {
            text += "；错误：" + message.Error!.Message;
        }

        return text;
    }

    private static void SetIdleColumns(Grid row, FrameworkElement left, FrameworkElement text, FrameworkElement right)
    {
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
    }

    private static Border CreateSeparatorLine()
    {
        var line = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyStyle(line, "OpenCodeDividerBorderStyle");
        return line;
    }

    /// <summary>应用内置/应用文本样式；缺失时不改变控件外观。</summary>
    internal static void ApplyStyle(FrameworkElement element, string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources && resources[key] is Style style)
            {
                element.Style = style;
            }
        }
        catch
        {
            // 样式不可用时保持默认外观。
        }
    }

    /// <summary>安全取主题画刷：键不存在时返回 null（不抛异常，控件回退到默认外观）。</summary>
    internal static Brush? TryBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources)
            {
                // 用索引器读取；键缺失抛 KeyNotFoundException，由外层 catch 兜底。
                // 不依赖 ResourceDictionary.TryGetValue 在不同 WinUI 版本中的可用性。
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

/// <summary>
/// 时间线中 question 工具部件的锚点（§4.1）：交互视图把匹配（metadata.tool.messageID / id）的待处理 Form 条目
/// 放入这里；没有条目时显示只读占位（标题 + 已回答 / 已跳过 / 等待回答）。本类不发送任何请求、不持有回调。
/// </summary>
internal sealed class OpenCodeQuestionAnchorHost : StackPanel
{
    private readonly Border _fallback;
    private readonly TextBlock _titleText;
    private readonly TextBlock _statusText;
    private UIElement? _card;
    private string _fallbackAccessibleName = "提问";

    internal OpenCodeQuestionAnchorHost()
    {
        Spacing = 4;
        _titleText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        OpenCodeConversationView.ApplyStyle(_titleText, "BodyStrongTextBlockStyle");
        _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        OpenCodeConversationView.ApplyStyle(_statusText, "CaptionSubtleTextBlockStyle");
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_titleText);
        panel.Children.Add(_statusText);
        _fallback = new Border
        {
            Child = panel,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(3, 0, 0, 0),
        };
        OpenCodeConversationView.ApplyStyle(_fallback, "OpenCodeQuestionFallbackChromeStyle");
        Children.Add(_fallback);
    }

    /// <summary>当前是否承载了一个可回答的提问条目。</summary>
    internal bool HasCard => _card is not null;

    internal void UpdateFallback(OpenCodeQuestionFallback fallback)
    {
        _titleText.Text = "提问：" + fallback.Title;
        _statusText.Text = fallback.StatusText;
        _fallbackAccessibleName = "提问：" + fallback.Title + "，" + fallback.StatusText;
        UpdateAccessibleName();
    }

    private void UpdateAccessibleName() => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,
        _card is null ? _fallbackAccessibleName : Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(_card));

    /// <summary>放入提问条目（调用方保证该元素已离开旧父级）；占位随之隐藏。</summary>
    internal void Attach(UIElement card)
    {
        if (ReferenceEquals(_card, card))
        {
            UpdateAccessibleName();
            return;
        }

        if (_card is not null)
        {
            Children.Remove(_card);
        }

        Children.Add(card);
        _card = card;
        _fallback.Visibility = Visibility.Collapsed;
        UpdateAccessibleName();
    }

    /// <summary>移出提问条目；占位恢复显示。</summary>
    internal void Detach(UIElement card)
    {
        if (!ReferenceEquals(_card, card))
        {
            return;
        }

        Children.Remove(card);
        _card = null;
        _fallback.Visibility = Visibility.Visible;
        UpdateAccessibleName();
    }
}
