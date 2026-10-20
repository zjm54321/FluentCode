using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

/// <summary>
/// OpenCode 页消息历史分页（“加载更早消息”）：沿刷新的 desc 首页 next 游标向更老分页；
/// 旧历史与刷新窗口按 id 合并去重、保持服务端序列顺序，刷新窗口滚出的已加载条目并入历史块保留；
/// 预插（prepend）不跳底、保持阅读位置。
/// </summary>
/// <remarks>
/// <para>
/// 已核实契约（docs/opencode-client-contract.md 第 7 / 11 节）：GET api/session/{sessionID}/message
/// 的 cursor 为 opaque，不解析内容；cursor 与 order 不可同传（客户端在发请求前拒绝）。首屏由既有
/// 的 <c>RefreshMessagesAsync</c> 以 Order=Desc、Limit=100 获取；续页只发送 Cursor + Limit（不带
/// Order；type 与首屏保持一致——首屏无 type 过滤，续页亦不带）。
/// </para>
/// <para>
/// 结束语义与已核实的会话分页一致：短页不表示结束；空 data 或缺失 next 视为正常结束（隐藏按钮，
/// 绝不声称“已加载完整”）。异常游标：next 与本次请求游标相同或命中已用游标集合（重复/循环），以及
/// 续页被服务端以 HTTP 400 拒绝（InvalidCursorError / InvalidRequestError；客户端不解析错误体）——
/// 一律安全停止并提示。非法游标：next 存在但为空串/纯空白/含控制字符/超长
/// （<see cref="MessageCursorMaxLength"/>，明确标注为页面侧防御预算而非服务端契约）同样安全停止并
/// 提示；仅 null 表示缺失（正常结束）。链状态显式记录（未建立/有效/正常结束/异常停止）：正常结束
/// 与异常停止都不会被普通轮询复活，仅用户显式“刷新消息”可重新开始（提示中的恢复路径真实有效）。
/// </para>
/// <para>
/// 合并语义：显示列表 = 旧历史块（“加载更早消息”累计的旧页 + 刷新窗口滚出的已加载条目）+
/// 最近一次刷新窗口；各段保持服务端序列顺序，重叠 id 保留刷新窗口内的副本（内容就地更新），
/// 绝不使用时间戳或 id 猜测顺序。轮询刷新只替换最近窗口并保留滚出条目，绝不删除已经加载的
/// 旧历史；分页链仅在未建立或未推进（未成功消费任何游标）时随刷新重建 next，已推进的链不受
/// 轮询影响；非法游标告警在链内只提示一次（约 2 秒轮询不重复弹条）。
/// </para>
/// <para>
/// 状态隔离：本分页路径不调用 ObserveExecutionState / RaiseSessionTracked，不触碰工作集合、
/// 等待文本与完成通知——加载历史绝不产生新的完成通知或虚假回合状态，也绝不把旧 idle 覆盖为
/// 最新执行状态（执行状态观察只由刷新路径以其最新窗口进行，本文件从不喂入旧页）。
/// </para>
/// <para>
/// 并发与代际：请求携带会话令牌（会话切换/来源失效/页面卸载即取消）；响应应用前必须同时通过
/// 页面代际、会话代际与选中会话校验；分页链另带链代际——刷新重建链时递增，在途续页响应若链代际
/// 不符则只合并其已到达的旧页内容（该页恒老于当前窗口与已加载历史），绝不推进/覆盖新链游标，
/// 也不弹出属于已作废链的停止提示。会话切换/来源失效/卸载调用
/// <see cref="ResetMessagePaginationState"/> 重置游标、已用游标、链状态/代际、两段消息缓存与在途标记。
/// prepend 后按可滚动高度增量恢复阅读位置（有界复核，用户已滚动则不争夺）；首次进入与主动发送
/// 的既有跟随（滚底）逻辑不变。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    // Independent from ordinary cursor-chain rebuilds: an accepted rollback changes the SAME session.
    private long _messageHistoryEpoch;
    private long? _messageHistoryWorkflowOwner;
    private bool _messageHistoryNeedsState = true;
    private bool _messageHistoryBoundaryKnown;
    private string? _messageRevertBoundary;
    private bool _historyExecutionRebaseline;
    private string? _historyAcceptedPromptId;
    private ImmutableArray<string> _historyUnresolvedChildren = ImmutableArray<string>.Empty;
    private sealed record MessageHistoryRead(OpenCodePageSource? Source, long Revision, int SourceGeneration,
        OpenCodeClient Client, int PageGeneration, int SessionGeneration, string SessionId, long Epoch);

    private MessageHistoryRead CaptureMessageHistoryRead(OpenCodeClient client, string sessionId)
        => new(_source, _sourceRevision, _sourceGeneration, client, _pageGeneration, _sessionGeneration, sessionId, _messageHistoryEpoch);

    private bool IsCurrentMessageHistoryRead(MessageHistoryRead read)
        => IsCurrentMessagePage(read.PageGeneration, read.SessionGeneration, read.SessionId)
        && _messageHistoryWorkflowOwner is null
        && read.Epoch == _messageHistoryEpoch && ReferenceEquals(read.Source, _source)
        && read.Source is { IsRevoked: false } && read.Revision == _sourceRevision
        && read.SourceGeneration == _sourceGeneration && ReferenceEquals(read.Client, _client) && !_sessionMissing;

    private Task<OpenCodeSessionMessagesResponse> StartMessageHistoryRead(MessageHistoryRead read,
        OpenCodeMessageQuery query, CancellationToken ct)
    {
        Task<OpenCodeSessionMessagesResponse>? pending = null;
        read.Source?.TryExecute(ct, () =>
        {
            if (IsCurrentMessageHistoryRead(read)) pending = read.Client.GetMessagesAsync(read.SessionId, query, ct);
        });
        return pending ?? Task.FromCanceled<OpenCodeSessionMessagesResponse>(new CancellationToken(true));
    }

    private async Task<MessageHistoryRead?> RefreshHistoryBoundaryAsync(MessageHistoryRead read, CancellationToken ct)
    {
        Task<SessionControlState>? pending = null;
        read.Source?.TryExecute(ct, () =>
        {
            if (IsCurrentMessageHistoryRead(read)) pending = read.Client.GetSessionControlStateAsync(read.SessionId, ct);
        });
        if (pending is null) return null;
        var state = await pending;
        if (!IsCurrentMessageHistoryRead(read)) return null;
        var boundary = state.Revert?.MessageId;
        if (_messageHistoryBoundaryKnown && _messageRevertBoundary != boundary)
        {
            // A committed/cleared/moved boundary changes the meaning of retained rows. Rebuild,
            // rather than unmasking a cached committed tail through ordinary pagination retention.
            var acceptedPrompt = _historyAcceptedPromptId;
            ResetAcceptedHistoryProjection(boundary);
            _historyAcceptedPromptId = acceptedPrompt;
            read = CaptureMessageHistoryRead(read.Client, read.SessionId);
        }
        _messageRevertBoundary = boundary;
        _messageHistoryBoundaryKnown = true;
        _messageHistoryNeedsState = false;
        return read;
    }

    private void BeginMessageHistoryWorkflow(long owner)
    {
        _messageHistoryEpoch++; // Before confirmation/preflight/transport/handoff, not after receipt.
        _messageHistoryWorkflowOwner = owner;
        _isLoadingOlderMessages = false;
        InvalidateViewAcknowledgement(); // Attempt keys survive; pre-workflow ACK starts must not revive.
        CancelSessionOperations();
        _sessionCts = new CancellationTokenSource();
    }

    private void EndMessageHistoryWorkflow(long owner, bool reset)
    {
        if (_messageHistoryWorkflowOwner != owner) return;
        if (reset)
        {
            ResetAcceptedHistoryProjection(_messageRevertBoundary, _messageHistoryBoundaryKnown);
            _messageHistoryNeedsState = true; // Neither prompt acceptance nor command204 proves stage empty.
        }
        else if (_messageRevertBoundary is not null)
        {
            // A previously known local stage may have been cleared by another client during consent.
            // Preserve ordinary buffers, but don't keep projecting that obsolete boundary indefinitely.
            _messageHistoryNeedsState = true;
            _historyExecutionRebaseline = true;
            _historyAcceptedPromptId = null;
        }
        _messageHistoryWorkflowOwner = null;
        OnMessageHistoryWorkflowEnded();
    }

    partial void OnMessageHistoryWorkflowEnded();

    private List<OpenCodeMessage> ProjectMessageHistory(List<OpenCodeMessage> ordered)
    {
        if (!_messageHistoryBoundaryKnown || _messageHistoryNeedsState) return new();
        if (_messageRevertBoundary is not { } boundary) return ordered;
        var index = ordered.FindIndex(message => message.Id == boundary);
        // A missing boundary in a partial window cannot establish which side its rows are on.
        return index < 0 ? new() : ordered.GetRange(0, index);
    }

    private void RetainHistoryChildEvidence(string sessionId, List<OpenCodeMessage> ordered)
    {
        var window = OpenCodeExecutionProjection.CreateWindow(sessionId, ordered, true,
            IsLoadedMessageHistoryComplete(), true);
        _historyUnresolvedChildren = OpenCodeExecutionProjection.Evaluate(new(sessionId, window,
            false, false, false, false, false, false, false, false, _historyUnresolvedChildren)).KnownUnresolvedChildSessionIds;
    }
    /// <summary>续页 limit（与首屏一致；续页只带 cursor + limit，绝不带 order）。</summary>
    private const int MessagePageSize = 100;

    /// <summary>
    /// 页面侧对 opaque cursor 的防御上限（客户端未定义数值上限、服务端契约不承诺上限）：
    /// 明确标注为页面防御预算（与会话分页一致），只做边界防呆，避免异常超长游标进入 URL；
    /// 不解析 cursor 内容。
    /// </summary>
    private const int MessageCursorMaxLength = 4096;

    /// <summary>当前续页游标；null = 没有更多（按钮隐藏）。由刷新（链未建立或未推进时）与每次成功续页推进。</summary>
    private string? _messageNextCursor;

    /// <summary>
    /// 分页链状态：显式记录“未建立/有效/正常结束/异常停止”，普通轮询绝不复活已结束或已停止的链
    /// （不能仅凭已用游标数量判定——空页/非法游标路径不会加入已用游标集合）。
    /// </summary>
    private MessagePaginationChainState _messageChainState = MessagePaginationChainState.None;

    /// <summary>分页链代际：刷新重建链（或重置/空窗口清链）时递增；在途续页响应据此判定不得推进新链。</summary>
    private int _messageChainGeneration;

    /// <summary>自最近一次链重建起已成功消费的游标；用于检测重复/循环游标，并判定有效链是否已推进。</summary>
    private readonly HashSet<string> _messageUsedCursors = new(StringComparer.Ordinal);

    /// <summary>非法游标告警去重（轮询下同一链只提示一次）；游标合法或缺失时复位。</summary>
    private bool _messageCursorWarningShown;

    /// <summary>“加载更早消息”在途标记：阻止重复点击，并驱动按钮禁用与“加载中…”文案。</summary>
    private bool _isLoadingOlderMessages;

    /// <summary>旧历史块（服务端序列顺序，旧→新）：经“加载更早消息”累计的旧页 + 刷新窗口滚出保留的条目；刷新路径只追加滚出条目、从不删除。</summary>
    private readonly List<OpenCodeMessage> _olderMessages = new();

    /// <summary>最近一次成功刷新的消息窗口（服务端序列顺序，旧→新）；每次刷新整体替换（滚出条目先并入历史块）。</summary>
    private readonly List<OpenCodeMessage> _latestMessages = new();

    /// <summary>
    /// 只读的缺口标记（Phase 2 概况用，不改变分页/渲染/指纹行为）：刷新窗口无法证明有序向前连续时，
    /// 保留的旧消息可能已失效，已加载历史不再视为完整；仅会话切换/来源失效/卸载的硬重置清除。
    /// </summary>
    private bool _messageHistoryGap;

    /// <summary>cursor.next 的分类：仅 Missing 表示正常结束；Invalid 表示存在但不可用。</summary>
    private enum MessageCursorKind
    {
        /// <summary>cursor.next 为 null：缺失（正常的“没有更多”）。</summary>
        Missing,

        /// <summary>cursor.next 为可用的不透明游标。</summary>
        Valid,

        /// <summary>cursor.next 存在但非法（空串/纯空白/含控制字符/超长）。</summary>
        Invalid,
    }

    /// <summary>
    /// 分页链状态：None = 未建立（初始/重置/空窗口后，下一次非空刷新可重建）；Active = 链有效
    /// （_messageNextCursor 可用，可能已推进或未推进）；Exhausted = 正常结束（空页/缺失 next）；
    /// Stopped = 异常停止（非法/重复/循环游标或 HTTP 400）。后两者仅用户显式刷新可重建。
    /// </summary>
    private enum MessagePaginationChainState
    {
        /// <summary>链未建立。</summary>
        None,

        /// <summary>链有效。</summary>
        Active,

        /// <summary>正常结束（空页/缺失 next）。</summary>
        Exhausted,

        /// <summary>异常停止（非法/重复/循环游标或 HTTP 400）。</summary>
        Stopped,
    }

    /// <summary>
    /// 分类服务端返回的 cursor.next（判定与会话分页完全一致）：仅 null 表示缺失；存在但为空串、
    /// 纯空白、含控制字符或超长一律判为非法（安全停止并提示）；合法的 opaque 游标原样保留。
    /// </summary>
    private static MessageCursorKind ClassifyMessageCursor(string? raw, out string? cursor)
    {
        cursor = null;
        if (raw is null)
        {
            return MessageCursorKind.Missing;
        }

        if (raw.Length == 0 || raw.Length > MessageCursorMaxLength)
        {
            return MessageCursorKind.Invalid;
        }

        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return MessageCursorKind.Invalid;
            }
        }

        cursor = raw;
        return MessageCursorKind.Valid;
    }

    /// <summary>
    /// 会话切换/来源失效/页面卸载时重置：清游标、已用游标、告警标记、在途标记与两段消息缓存，
    /// 并刷新按钮 UI。
    /// </summary>
    private void ResetMessagePaginationState()
    {
        ResetMessagePaginationCore();
        UpdateLoadOlderMessagesUi();
        OnMessageHistoryReset();
    }

    /// <summary>
    /// 已加载消息被硬重置（会话切换 / 来源失效 / 页面卸载，均经本方法）后的可选扩展点：Phase 2 会话概况
    /// 据此取消并清空基于旧会话的结果（实现见 OpenCodePage.SessionInsights.cs；未实现时编译为空）。
    /// </summary>
    partial void OnMessageHistoryReset();

    /// <summary>分页硬重置核心：清游标、链状态与全部缓存；递增链代际使在途续页失效；不动视图、不刷 UI。</summary>
    private void ResetMessagePaginationCore()
    {
        OnEventMessageHistoryReset();
        _messageHistoryEpoch++;
        _messageHistoryWorkflowOwner = null;
        _messageHistoryNeedsState = true;
        _messageHistoryBoundaryKnown = false;
        _messageRevertBoundary = null;
        _historyExecutionRebaseline = false;
        _historyAcceptedPromptId = null;
        _historyUnresolvedChildren = ImmutableArray<string>.Empty;
        _messageNextCursor = null;
        _messageChainState = MessagePaginationChainState.None;
        _messageChainGeneration++;
        _messageUsedCursors.Clear();
        _messageCursorWarningShown = false;
        _isLoadingOlderMessages = false;
        _olderMessages.Clear();
        _latestMessages.Clear();
        _messageHistoryGap = false;
        _olderMessagesLastObservedOffset = double.NaN;
    }

    partial void OnEventMessageHistoryReset();

    /// <summary>
    /// 已加载消息是否覆盖完整历史（Phase 2 上下文参考 / Todo 回放的 historyComplete）：仅当分页链已正常结束
    /// （首屏无 next 或续页到达终点）、没有续页在途、且相邻刷新窗口之间没有缺口时为 true；
    /// 未建立 / 仍有游标 / 异常停止一律 false，绝不在存在缺口时宣称完整。
    /// </summary>
    private bool IsLoadedMessageHistoryComplete()
        => _messageChainState == MessagePaginationChainState.Exhausted
        && !_isLoadingOlderMessages
        && !_messageHistoryGap
        && _latestMessages.Count > 0;

    /// <summary>
    /// 刷新成功后的窗口应用：替换最近窗口（旧窗口中滚出的已加载条目并入历史块保留），与旧历史块
    /// 合并去重后交给视图（视图按 id 增量渲染与内容就地更新）。分页链仅在未建立或未推进（未成功
    /// 消费任何游标）时以本次响应的 next 重建；正常结束/异常停止的链只由显式刷新重建；已推进的
    /// 链不受轮询影响。空窗口视为“会话当前无消息”：链回到未建立（已停止/已结束的链保持原状，
    /// 轮询绝不复活），待窗口非空时再由后续刷新重建。
    /// 不触碰执行状态、等待文本与通知；滚动跟随由刷新路径既有逻辑处理。
    /// </summary>
    /// <param name="explicitRefresh">true = 用户显式“刷新消息”：可重建正常结束/异常停止的链（提示中的恢复路径）。</param>
    private void ApplyRefreshedMessages(List<OpenCodeMessage> latestAsc, string? nextCursor, bool explicitRefresh)
    {
        NoteRefreshWindowGap(latestAsc);
        PreserveRolledOffLatestMessages(latestAsc);

        _latestMessages.Clear();
        _latestMessages.AddRange(latestAsc);
        _conversationView.ApplyMessages(ProjectMessageHistory(MergeMessageHistory()));

        if (latestAsc.Count == 0)
        {
            if (_messageChainState is not (MessagePaginationChainState.Exhausted or MessagePaginationChainState.Stopped))
            {
                _messageChainGeneration++;
                _messageUsedCursors.Clear();
                _messageCursorWarningShown = false;
                _messageChainState = MessagePaginationChainState.None;
                _messageNextCursor = null;
                UpdateLoadOlderMessagesUi();
            }

            return;
        }

        var restart = _messageChainState switch
        {
            MessagePaginationChainState.None => true,
            MessagePaginationChainState.Active => _messageUsedCursors.Count == 0,
            _ => explicitRefresh,
        };

        if (restart)
        {
            RestartMessagePagination(nextCursor);
        }
    }

    /// <summary>
    /// 只读观察：仅允许旧窗口的连续后缀成为新窗口的前缀，再追加新 id（不推测时间戳/id 顺序）。
    /// 回退、尾部/内部删除、重排、无效/重复 id 或非空变空均使完整性存疑；普通刷新/重启游标不清除。
    /// 不改变既有窗口保留、合并、游标或滚动行为。
    /// </summary>
    private void NoteRefreshWindowGap(List<OpenCodeMessage> incoming)
    {
        if (_messageHistoryGap)
        {
            return;
        }

        if (incoming.Count == 0)
        {
            _messageHistoryGap = _latestMessages.Count > 0 || _olderMessages.Count > 0;
            return;
        }

        if (!TryGetObservedWindowIds(incoming, out var incomingIds)
            || !TryGetObservedWindowIds(_latestMessages, out var previousIds))
        {
            _messageHistoryGap = true;
            return;
        }

        if (_latestMessages.Count == 0)
        {
            return;
        }

        // A shared id alone is insufficient: the old tail must survive, unchanged in order.
        var start = _latestMessages.FindIndex(message =>
            string.Equals(message.Id, incoming[0].Id, StringComparison.Ordinal));
        var overlap = _latestMessages.Count - start;
        if (start < 0 || incoming.Count < overlap)
        {
            _messageHistoryGap = true;
            return;
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            if (i < overlap
                ? !string.Equals(_latestMessages[start + i].Id, incoming[i].Id, StringComparison.Ordinal)
                : previousIds.Contains(incoming[i].Id!))
            {
                _messageHistoryGap = true;
                return;
            }
        }

        // An already retained older id cannot be certified as a newly appended message.
        foreach (var message in _olderMessages)
        {
            if (message?.Id is { } id && incomingIds.Contains(id) && !previousIds.Contains(id))
            {
                _messageHistoryGap = true;
                return;
            }
        }
    }

    private static bool TryGetObservedWindowIds(List<OpenCodeMessage> messages, out HashSet<string> ids)
    {
        ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var id = message?.Id;
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
            {
                return false;
            }

            foreach (var character in id)
            {
                if (char.IsWhiteSpace(character) || char.IsControl(character))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// 保留刷新窗口滚出的已加载条目：旧窗口中不在新窗口内的条目按原顺序并入历史块尾部——它们比
    /// 历史块全部条目更新、比新窗口全部条目更老（两窗口均为服务端序列的连续降序窗口，随新消息向
    /// 新滑动），拼接顺序即服务端顺序，绝不按时间戳或 id 重排。空新窗口不保留（视为会话无消息，
    /// 与替换窗口的既有语义一致）；无有效 id 的条目无法参与去重，防御性跳过（契约 id 必填）。
    /// </summary>
    private void PreserveRolledOffLatestMessages(List<OpenCodeMessage> incoming)
    {
        if (incoming.Count == 0 || _latestMessages.Count == 0)
        {
            return;
        }

        var incomingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in incoming)
        {
            if (message?.Id is { Length: > 0 } id)
            {
                incomingIds.Add(id);
            }
        }

        foreach (var message in _latestMessages)
        {
            if (message?.Id is not { Length: > 0 } id || incomingIds.Contains(id))
            {
                continue;
            }

            _olderMessages.Add(message);
        }
    }

    /// <summary>
    /// 合并两段消息（旧历史块（含刷新窗口滚出的保留条目）+ 最近窗口）为显示顺序：逐段保持服务端序列顺序；
    /// 与最近窗口重叠的 id 保留窗口内（更新）的副本——重叠项位于旧块尾部与窗口头部，
    /// 拼接顺序即服务端顺序；无有效 id 的旧历史条目无法参与去重，防御性跳过（契约 id 必填）。
    /// 绝不按时间戳或 id 排序。
    /// </summary>
    private List<OpenCodeMessage> MergeMessageHistory()
    {
        var latestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in _latestMessages)
        {
            if (message?.Id is { Length: > 0 } id)
            {
                latestIds.Add(id);
            }
        }

        var merged = new List<OpenCodeMessage>(_olderMessages.Count + _latestMessages.Count);
        foreach (var message in _olderMessages)
        {
            if (message?.Id is not { Length: > 0 } id || latestIds.Contains(id))
            {
                continue;
            }

            merged.Add(message);
        }

        merged.AddRange(_latestMessages);
        return merged;
    }

    /// <summary>
    /// 以给定 cursor.next（可为 null）重建分页链：递增链代际并清空已用游标（在途续页响应自此只合并
    /// 内容、不再推进新链）。仅 null 表示缺失（正常结束 → Exhausted）；存在但非法
    /// （空串/纯空白/控制字符/超长）安全停止并提示（同一链只提示一次 → Stopped），绝不解析 opaque
    /// 内容；合法游标 → Active。
    /// </summary>
    private void RestartMessagePagination(string? nextCursor)
    {
        _messageChainGeneration++;
        _messageUsedCursors.Clear();

        var kind = ClassifyMessageCursor(nextCursor, out var cursor);
        _messageNextCursor = kind == MessageCursorKind.Valid ? cursor : null;
        if (kind != MessageCursorKind.Invalid)
        {
            _messageCursorWarningShown = false;
        }

        _messageChainState = kind switch
        {
            MessageCursorKind.Valid => MessagePaginationChainState.Active,
            MessageCursorKind.Missing => MessagePaginationChainState.Exhausted,
            _ => MessagePaginationChainState.Stopped,
        };

        UpdateLoadOlderMessagesUi();
        if (kind == MessageCursorKind.Invalid && !_messageCursorWarningShown)
        {
            _messageCursorWarningShown = true;
            ShowInvalidMessageCursorNotice();
        }
    }

    /// <summary>非法分页游标的统一提示：不声称完整，可经“刷新消息”重新开始（清 next、隐藏按钮）。</summary>
    private void ShowInvalidMessageCursorNotice() =>
        StopMessagePaginationWithNotice(
            "消息分页游标异常",
            "服务端返回了不可用的分页游标（空串/空白/控制字符或超长）；已安全停止继续加载。" +
            "历史可能不完整，可点击“刷新消息”重新开始。");

    /// <summary>
    /// 自动加载阈值（F3）：会话时间线滚动到距顶部不超过该距离（DIP）时自动加载更早消息。
    /// </summary>
    internal const double OlderMessagesAutoLoadThreshold = 200;

    /// <summary>上一次观察到的时间线纵向偏移（判定“向上滚动进入阈值”；NaN = 尚未观察）。</summary>
    private double _olderMessagesLastObservedOffset = double.NaN;

    /// <summary>
    /// 刷新分页状态的呈现（F3：不再有“加载更早消息”按钮）：hasMore = 有可用 next 且会话/页面有效；
    /// canLoad = 另外没有在途续页、没有历史工作流且边界已知；loading = 续页在途（顶部小进度环）。
    /// 呈现由 <see cref="OnOlderMessagesUiChanged"/> 完成（实现见 OpenCodePage.MessageAutoLoad.cs）。
    /// </summary>
    private void UpdateLoadOlderMessagesUi()
    {
        var hasMore = _pageActive && !_sessionMissing && _messageNextCursor is not null;
        OnOlderMessagesUiChanged(hasMore, hasMore && CanAutoLoadOlderMessages(), _isLoadingOlderMessages);
    }

    /// <summary>分页呈现扩展点（顶部进度环、自动续载复核）；单独编译本文件的测试可自行实现以观察状态。</summary>
    partial void OnOlderMessagesUiChanged(bool hasMore, bool canLoad, bool loading);

    /// <summary>当前是否可以开始一次自动续页（与 <see cref="LoadOlderMessagesAsync"/> 的前置条件一致；单飞）。</summary>
    private bool CanAutoLoadOlderMessages()
        => _pageActive && !_sessionMissing && _messageNextCursor is not null && !_isLoadingOlderMessages
            && _messageHistoryWorkflowOwner is null && !_messageHistoryNeedsState;

    /// <summary>
    /// 纯判定（F3）：仅当用户“向上”滚动（偏移减小）且进入顶部阈值时自动加载——首次进入/发送后的滚底、
    /// 预插后的位置补偿（偏移增大）都不会触发；尚未观察过偏移时以进入阈值为准。
    /// </summary>
    internal static bool ShouldAutoLoadOlderMessages(double offset, double previousOffset, double scrollableHeight,
        double threshold = OlderMessagesAutoLoadThreshold)
        => scrollableHeight > 0
            && offset <= threshold
            && (double.IsNaN(previousOffset) || offset < previousOffset - 0.5);

    /// <summary>
    /// 时间线 ViewChanged（含惯性中间帧）：记录偏移，满足 <see cref="ShouldAutoLoadOlderMessages"/> 时触发续页。
    /// 单飞由 <see cref="LoadOlderMessagesAsync"/> 的在途标记保证；游标/链/代际安全语义全部沿用。
    /// </summary>
    private void OnMessageScrollViewChangedForHistory()
    {
        var offset = MessageScroll.VerticalOffset;
        var previous = _olderMessagesLastObservedOffset;
        _olderMessagesLastObservedOffset = offset;
        if (CanAutoLoadOlderMessages() && ShouldAutoLoadOlderMessages(offset, previous, MessageScroll.ScrollableHeight))
        {
            _ = LoadOlderMessagesAsync();
        }
    }

    /// <summary>
    /// 续页：仅 cursor + limit（不带 order；type 与首屏一致为无过滤）。空 data / 缺失 next 正常结束
    /// （Exhausted）；非法/重复/循环游标与 HTTP 400 安全停止并提示（Stopped）；两者都不会被普通轮询
    /// 复活。新条目按 id 去重后前置到旧历史块，预插保持阅读位置；响应应用前做页面代际 + 会话代际 +
    /// 选中会话校验，并以链代际判定是否仍可推进游标——链已被刷新重建时只合并旧页内容、绝不混合
    /// 分页链；失败路径不改变已渲染内容。
    /// </summary>
    private async Task LoadOlderMessagesAsync()
    {
        if (!SourceReady || !_pageActive || _isLoadingOlderMessages
            || _messageHistoryWorkflowOwner is not null || _messageHistoryNeedsState)
        {
            return;
        }

        var cursor = _messageNextCursor;
        var client = _client;
        var sessionId = _selectedSessionId;
        if (_sessionMissing || cursor is null || client is null || sessionId is null)
        {
            UpdateLoadOlderMessagesUi();
            return;
        }

        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        var chainGeneration = _messageChainGeneration;
        var historyRead = CaptureMessageHistoryRead(client, sessionId);
        var ct = _sessionCts?.Token ?? CancellationToken.None;

        _isLoadingOlderMessages = true;
        UpdateLoadOlderMessagesUi();

        try
        {
            OpenCodeSessionMessagesResponse response;
            try
            {
                // 续页只带 cursor + limit：不带 order（与 cursor 互斥），type 与首屏一致（首屏无过滤，续页亦无）。
                response = await StartMessageHistoryRead(historyRead,
                    new OpenCodeMessageQuery { Cursor = cursor, Limit = MessagePageSize },
                    ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeApiException ex)
            {
                if (IsCurrentMessageHistoryRead(historyRead))
                {
                    if (ex.StatusCode == 400)
                    {
                        // 续页 400（InvalidCursorError / InvalidRequestError；错误体不解析）＝游标不可继续：
                        // 重试同一游标没有意义，安全停止并提示（不声称完整），用户可刷新重新开始。
                        // 仅当链仍当前时停止；已被刷新重建的链不受旧链 400 影响（也不弹出误导提示）。
                        SetConnectionState(ConnectionState.Connected);
                        if (chainGeneration == _messageChainGeneration)
                        {
                            StopMessagePaginationWithNotice(
                                "消息分页已停止",
                                "服务端拒绝了续页请求（HTTP 400，可能为 InvalidCursorError）；已停止继续加载。" +
                                "历史可能不完整，可点击“刷新消息”重新开始。");
                        }
                    }
                    else
                    {
                        HandleApiException(ex, "加载更早消息");
                        if (ex.StatusCode == 404 && chainGeneration == _messageChainGeneration)
                        {
                            // 会话已在服务端消失：分页不再可用；错误提示由 HandleApiException 给出，
                            // 此处只清游标并标记停止（按钮随 _sessionMissing 隐藏），不覆盖错误提示。
                            _messageNextCursor = null;
                            _messageChainState = MessagePaginationChainState.Stopped;
                            UpdateLoadOlderMessagesUi();
                        }
                    }
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                if (IsCurrentMessageHistoryRead(historyRead))
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "无法连接 OpenCode 服务", "加载更早消息失败；可稍后重试。");
                }

                return;
            }
            catch (OpenCodeTimeoutException)
            {
                if (IsCurrentMessageHistoryRead(historyRead))
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "加载更早消息超时", "客户端 30 秒内未完成请求；可稍后重试。");
                }

                return;
            }
            catch (OpenCodeProtocolException)
            {
                if (IsCurrentMessageHistoryRead(historyRead))
                {
                    ShowMessage(InfoBarSeverity.Error, "响应不符合契约", "消息续页响应不符合已核实的 V2 契约；已停止本次加载。");
                }

                return;
            }

            if (!IsCurrentMessageHistoryRead(historyRead))
            {
                return;
            }

            SetConnectionState(ConnectionState.Connected);

            var data = response.Data ?? new List<OpenCodeMessage>();
            var nextKind = ClassifyMessageCursor(response.Cursor?.Next, out var next);

            // 链代际：本次响应属于发起时的链。刷新在途期间重建链（递增链代际）后，本响应的旧页内容
            // 仍安全可合并（该页恒老于新窗口与已加载历史），但绝不推进/覆盖新链的游标与状态，
            // 也不弹出属于已作废链的停止提示。
            var chainCurrent = chainGeneration == _messageChainGeneration;

            // 新条目按 id 去重后前置到旧历史块；至少一条新增才重排视图（保持阅读位置）。
            // 无论链是否仍当前都合并（不丢弃已到达的已加载内容）；空 data 无条目可合并。
            if (data.Count > 0 && PrependOlderMessages(OrderMessages(data)) > 0)
            {
                ApplyOlderPageAndPreserveScroll();
            }

            if (!chainCurrent)
            {
                return;
            }

            // 空 data：契约规定正常结束（即使携带 next 也不再继续，避免空页循环）。
            if (data.Count == 0)
            {
                _messageNextCursor = null;
                _messageChainState = MessagePaginationChainState.Exhausted;
                UpdateLoadOlderMessagesUi();
                return;
            }

            // next 存在但非法（空串/纯空白/控制字符/超长）：不解析 opaque 内容，安全停止并提示。
            if (nextKind == MessageCursorKind.Invalid)
            {
                ShowInvalidMessageCursorNotice();
                return;
            }

            if (next is null)
            {
                // 缺失 next：正常结束；不断言“已加载完整”。
                _messageNextCursor = null;
                _messageChainState = MessagePaginationChainState.Exhausted;
                UpdateLoadOlderMessagesUi();
                return;
            }

            // next 与本次请求游标相同（重复）或命中历史游标（循环）＝服务端游标异常：
            // 安全停止并提示，绝不声称完整。
            if (string.Equals(next, cursor, StringComparison.Ordinal) || _messageUsedCursors.Contains(next))
            {
                StopMessagePaginationWithNotice(
                    "消息分页游标异常",
                    "服务端返回了重复的分页游标，继续加载可能造成循环；已安全停止。" +
                    "历史可能不完整，可点击“刷新消息”重新开始。");
                return;
            }

            _messageUsedCursors.Add(cursor);
            _messageNextCursor = next;
            _messageChainState = MessagePaginationChainState.Active;
            UpdateLoadOlderMessagesUi();
        }
        catch (Exception)
        {
            if (IsCurrentMessageHistoryRead(historyRead))
            {
                ShowMessage(InfoBarSeverity.Error, "加载更早消息失败", "发生未预期错误；请稍后重试。");
            }
        }
        finally
        {
            if (IsCurrentMessageHistoryRead(historyRead))
            {
                _isLoadingOlderMessages = false;
                UpdateLoadOlderMessagesUi();
            }
        }
    }

    /// <summary>
    /// 把续页条目（已按展示顺序旧→新）按 id 去重后前置到旧历史块；返回新增条数（0 = 全部命中
    /// 已加载 id）。去重集合 = 旧历史块 ∪ 最近窗口的已加载 id；无有效 id 的条目无法去重，跳过
    /// （契约 id 必填且唯一）。
    /// </summary>
    private int PrependOlderMessages(List<OpenCodeMessage> fetchedAsc)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in _olderMessages)
        {
            if (message?.Id is { Length: > 0 } id)
            {
                known.Add(id);
            }
        }

        foreach (var message in _latestMessages)
        {
            if (message?.Id is { Length: > 0 } id)
            {
                known.Add(id);
            }
        }

        var prepend = new List<OpenCodeMessage>(fetchedAsc.Count);
        foreach (var message in fetchedAsc)
        {
            if (message?.Id is not { Length: > 0 } id || !known.Add(id))
            {
                continue;
            }

            prepend.Add(message);
        }

        if (prepend.Count == 0)
        {
            return 0;
        }

        // 续页条目全部老于当前旧历史块（游标沿序列向更老推进），按服务端顺序整体前插。
        _olderMessages.InsertRange(0, prepend);
        return prepend.Count;
    }

    /// <summary>
    /// 应用前置后的显示列表并保持阅读位置：记录预插前的偏移与可滚动高度，应用后按“新增的
    /// 可滚动高度”平移偏移，使视口内可见内容不变（不跳底也不跳顶）；随后有界复核吸收迟到布局
    /// （富文本/工具部件的异步测量），用户一旦自行滚动即不争夺。
    /// </summary>
    private void ApplyOlderPageAndPreserveScroll()
    {
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        var sessionId = _selectedSessionId;
        var historyEpoch = _messageHistoryEpoch;
        var offsetBefore = MessageScroll.VerticalOffset;
        var heightBefore = MessageScroll.ScrollableHeight;

        _conversationView.ApplyMessages(ProjectMessageHistory(MergeMessageHistory()));

        MessageScroll.UpdateLayout();
        var target = offsetBefore + (MessageScroll.ScrollableHeight - heightBefore);
        MessageScroll.ChangeView(null, target, null, disableAnimation: true);
        SettlePrependScroll(pageGeneration, sessionGeneration, sessionId, historyEpoch, offsetBefore, heightBefore, target, pass: 0);
    }

    /// <summary>
    /// 有界复核预插滚动（绝不使用计时器/无限循环）：下一帧若可滚动高度继续增长且用户仍停在
    /// 我们设置的偏移上，则按同一增量修正；用户已滚动、上下文失效或达到复核上限即停止。
    /// </summary>
    private void SettlePrependScroll(
        int pageGeneration,
        int sessionGeneration,
        string? sessionId,
        long historyEpoch,
        double offsetBefore,
        double heightBefore,
        double target,
        int pass)
    {
        if (pass >= MaxScrollSettlePasses)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsScrollContextCurrent(pageGeneration, sessionGeneration, sessionId) || historyEpoch != _messageHistoryEpoch)
            {
                return;
            }

            // 用户已自行滚动（当前偏移偏离我们设置的目标）＝不争夺阅读位置。
            if (Math.Abs(MessageScroll.VerticalOffset - target) > 0.5)
            {
                return;
            }

            var next = offsetBefore + (MessageScroll.ScrollableHeight - heightBefore);
            if (next <= target + 0.5)
            {
                return;
            }

            MessageScroll.ChangeView(null, next, null, disableAnimation: true);
            SettlePrependScroll(pageGeneration, sessionGeneration, sessionId, historyEpoch, offsetBefore, heightBefore, next, pass + 1);
        });
    }

    /// <summary>安全停止分页：清 next、标记链为 Stopped（轮询不复活）并给出不声称完整的警告提示；可经“刷新消息”重新开始。</summary>
    private void StopMessagePaginationWithNotice(string title, string message)
    {
        _messageNextCursor = null;
        _messageChainState = MessagePaginationChainState.Stopped;
        UpdateLoadOlderMessagesUi();
        ShowMessage(InfoBarSeverity.Warning, title, message);
    }

    /// <summary>响应应用前的代际校验：页面仍激活，且页面代际、会话代际与选中会话均未变化。</summary>
    private bool IsCurrentMessagePage(int pageGeneration, int sessionGeneration, string? sessionId) =>
        _pageActive
        && pageGeneration == _pageGeneration
        && sessionGeneration == _sessionGeneration
        && string.Equals(sessionId, _selectedSessionId, StringComparison.Ordinal);
}
