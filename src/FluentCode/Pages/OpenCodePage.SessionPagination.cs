using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

/// <summary>
/// OpenCode 页会话列表分页：续页只追加去重；重新读取保留已加载深度，所有目录/状态读取共用单航班。
/// </summary>
/// <remarks>
/// <para>
/// 已核实契约（docs/opencode-integration.md）：GET api/session 的 cursor 为 opaque，
/// 不解析内容；cursor 与 order 不可同传（客户端在发请求前拒绝）。首屏由 RefreshSessionsAsync 以
/// Order=Desc、Limit=50 获取并保存其 cursor.next；续页只发送 Cursor + Limit（不带 Order）。
/// </para>
/// <para>
/// 结束语义：响应 cursor.next 在每个非空页（包括最后一个非空页）都可能存在，短页不表示结束；
/// 空 data 或缺失 next 视为正常结束（隐藏按钮，绝不声称“已加载完整”）。
/// 异常游标：next 与本次请求游标相同或命中已用游标集合（重复/循环），以及续页被服务端以
/// HTTP 400 拒绝（InvalidCursorError / InvalidRequestError；客户端不解析错误体）——一律安全停止
/// 并提示，同样不声称完整。
/// 非法游标：cursor.next 存在但为空串/纯空白/含控制字符/超长时同样安全停止并提示（不解析 opaque
/// 内容）；仅 null 表示缺失（正常结束）——首屏保存 next 与每次续页推进使用同一判定。
/// </para>
/// <para>
/// 并发与代际：请求携带 _pageCts 令牌（来源切换/页面卸载即取消）；响应应用前必须同时通过
/// 页面代际（_pageActive + _pageGeneration）与列表代际（_sessionListEpoch）校验，刷新开始后
/// 到达的旧首屏/旧续页响应一律丢弃；刷新在开始时即递增列表代际并暂停旧游标（隐藏按钮、保留
/// 可见列表），仅当本次新首屏成功后才重新建立分页；_isLoadingMoreSessions 阻止重复点击并驱动
/// 按钮禁用。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    partial void OnSessionCatalogChanged();
    /// <summary>续页 limit（与首屏一致；续页只带 cursor + limit，绝不带 order）。</summary>
    private const int SessionPageSize = 50;
    // First-page/depth reconciliation, keyed Home metadata, foreground status and paging all
    // acquire this instance-owned gate. A source replacement cancels waiters before they can read.
    private readonly SemaphoreSlim _sessionCatalogReadGate = new(1, 1);
    private Task? _sessionCatalogRefreshTask;
    private Task<bool>? _sessionCatalogStatusFlight;
    private int _sessionCatalogRefreshGeneration;
    private OpenCodePageSource? _sessionCatalogRefreshSource;
    private OpenCodeClient? _sessionCatalogRefreshClient;
    private long _sessionCatalogAcceptedReads;
    private long _sessionCatalogEventVersion;
    private long _sessionCatalogReadVersion;
    private readonly Dictionary<string, long> _sessionCatalogEventVersions = new(StringComparer.Ordinal);

    private sealed class SessionCatalogReadLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private async Task<SessionCatalogReadLease?> EnterSessionCatalogReadAsync(OpenCodeClient client, CancellationToken ct)
    {
        await _sessionCatalogReadGate.WaitAsync(ct);
        if (ct.IsCancellationRequested || !_pageActive || !_hostWindowVisible || !SourceReady || !ReferenceEquals(client, _client))
        {
            _sessionCatalogReadGate.Release();
            return null;
        }
        return new(_sessionCatalogReadGate);
    }

    /// <summary>Re-read the already loaded page depth, then verify displaced old roots by exact ID.
    /// Newly updated/created rows must not evict a previously loaded older row just because it moved
    /// beyond the refreshed cursor. No full-server scan, message reads or selection side effects.</summary>
    private async Task<OpenCodeSessionsResponse> ReadLoadedSessionDepthAsync(OpenCodeClient client, CancellationToken ct)
    {
        var source = _source;
        var revision = _sourceRevision;
        var generation = _sourceGeneration;
        bool Current() => !ct.IsCancellationRequested && _hostWindowVisible && _pageActive && source is { IsRevoked: false }
            && ReferenceEquals(source, _source) && ReferenceEquals(client, _client)
            && source.Revision == revision && revision == _sourceRevision && generation == _sourceGeneration;
        var retained = _sessionItems.Keys.ToArray();
        var pages = Math.Max(1, (retained.Length + SessionPageSize - 1) / SessionPageSize);
        var rows = new Dictionary<string, OpenCodeSessionInfo>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        OpenCodeSessionsResponse response = new();
        string? cursor = null;
        _sessionCatalogReadVersion = _sessionCatalogEventVersion;
        for (var page = 0; page < pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException(ct);
            Task<OpenCodeSessionsResponse>? pending = null;
            source!.TryExecute(ct, () => pending = client.ListSessionsAsync(new()
            {
                Order = cursor is null ? OpenCodeSessionOrders.Desc : null,
                Cursor = cursor, Limit = SessionPageSize, ParentFilter = OpenCodeSessionParentFilter.Roots,
            }, ct));
            if (pending is null) throw new OperationCanceledException(ct);
            response = await pending;
            foreach (var info in response.Data ?? new())
                if (IsRootSession(info) && info.Time?.Archived is null) rows[info.Id!] = info;
            if ((response.Data?.Count ?? 0) == 0) { response = response with { Cursor = null }; break; }
            var kind = ClassifySessionCursor(response.Cursor?.Next, out var next);
            if (kind != SessionCursorKind.Valid) break; // RestartSessionPagination owns invalid/missing UI.
            if (!used.Add(next!)) throw new OpenCodeProtocolException("会话列表重新读取遇到重复游标。");
            cursor = next;
        }
        foreach (var id in retained.Where(id => !rows.ContainsKey(id)))
        {
            ct.ThrowIfCancellationRequested();
            if (!Current()) throw new OperationCanceledException(ct);
            try
            {
                Task<OpenCodeSessionInfo>? pending = null;
                source!.TryExecute(ct, () => pending = client.GetSessionInfoAsync(id, ct));
                if (pending is null) throw new OperationCanceledException(ct);
                var info = await pending;
                if (IsRootSession(info) && info.Time?.Archived is null) rows[id] = info;
            }
            catch (OpenCodeApiException error) when (error.StatusCode == 404) { } // A real deletion, not paging absence.
        }
        return response with { Data = rows.Values.ToList() };
    }

    private void ApplySessionCatalogRead(List<OpenCodeSessionInfo> sessions)
    {
        var accepted = new Dictionary<string, OpenCodeSessionInfo>(StringComparer.Ordinal);
        foreach (var info in sessions)
        {
            if (!IsRootSession(info) || info.Time?.Archived is not null) continue;
            var id = info.Id!;
            if (_sessionCatalogEventVersions.GetValueOrDefault(id) > _sessionCatalogReadVersion)
            {
                // An event that arrived during REST keeps its newer local row (or deletion).
                // Home's dirty-ID queue will re-read metadata; REST cannot resurrect a tombstone.
                if (_sessionItems.TryGetValue(id, out var current)) accepted[id] = current;
            }
            else accepted.TryAdd(id, info); // Keep the existing first-wins policy for a duplicated catalog ID.
        }
        foreach (var (id, info) in _sessionItems)
            if (_sessionCatalogEventVersions.GetValueOrDefault(id) > _sessionCatalogReadVersion) accepted[id] = info;
        _sessionItems.Clear();
        foreach (var (id, info) in accepted) _sessionItems[id] = info;
        _sessionCatalogAcceptedReads++;
    }

    /// <summary>
    /// 页面侧对 opaque cursor 的防御上限（客户端未定义数值上限）：只做边界防呆，避免异常超长游标
    /// 进入 URL；不解析 cursor 内容。
    /// </summary>
    private const int SessionCursorMaxLength = 4096;

    /// <summary>当前续页游标；null = 没有更多（按钮隐藏）。由首屏响应与每次成功续页推进。</summary>
    private string? _sessionNextCursor;

    /// <summary>自最近一次重建起已成功消费的游标；用于检测服务端返回重复/循环游标。</summary>
    private readonly HashSet<string> _sessionUsedCursors = new(StringComparer.Ordinal);

    /// <summary>
    /// 会话列表代际：每次列表重建开始（刷新开始/来源切换/卸载）递增；在途续页与旧首屏响应的
    /// 代际不符即丢弃。
    /// </summary>
    private int _sessionListEpoch;

    /// <summary>“加载更多”在途标记：阻止重复点击，并驱动按钮禁用与“加载中…”文案。</summary>
    private bool _isLoadingMoreSessions;

    /// <summary>cursor.next 的分类：仅 Missing 表示正常结束；Invalid 表示存在但不可用。</summary>
    private enum SessionCursorKind
    {
        /// <summary>cursor.next 为 null：缺失（正常的“没有更多”）。</summary>
        Missing,

        /// <summary>cursor.next 为可用的不透明游标。</summary>
        Valid,

        /// <summary>cursor.next 存在但非法（空串/纯空白/含控制字符/超长）。</summary>
        Invalid,
    }

    /// <summary>
    /// 分类服务端返回的 cursor.next：仅 null 表示缺失；存在但为空串、纯空白、含控制字符或超长
    /// 一律判为非法（安全停止并提示）；合法的 opaque 游标原样保留、绝不解析其内容。
    /// </summary>
    private static SessionCursorKind ClassifySessionCursor(string? raw, out string? cursor)
    {
        cursor = null;
        if (raw is null)
        {
            return SessionCursorKind.Missing;
        }

        if (raw.Length == 0 || raw.Length > SessionCursorMaxLength)
        {
            return SessionCursorKind.Invalid;
        }

        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return SessionCursorKind.Invalid;
            }
        }

        cursor = raw;
        return SessionCursorKind.Valid;
    }

    /// <summary>来源切换/页面卸载时重置分页：递增列表代际，清游标与历史，使在途请求响应失效。</summary>
    private void ResetSessionPaginationState()
    {
        ResetSessionPaginationCore();
        UpdateLoadMoreSessionsUi();
    }

    /// <summary>
    /// 刷新开始：递增列表代际（作废在途续页与旧首屏响应）并暂停旧分页（隐藏旧游标，保留可见列表）。
    /// 返回本次刷新应校验的列表代际；仅当本次首屏成功后由 <see cref="RestartSessionPagination"/> 重建分页。
    /// </summary>
    private int BeginSessionRefresh()
    {
        ResetSessionPaginationCore();
        UpdateLoadMoreSessionsUi();
        return _sessionListEpoch;
    }

    /// <summary>
    /// 分页硬重置核心：递增列表代际、清历史游标与 next、清在途标记；不动可见列表、不刷 UI。
    /// </summary>
    private void ResetSessionPaginationCore()
    {
        _sessionListEpoch++;
        _sessionUsedCursors.Clear();
        _sessionNextCursor = null;
        _isLoadingMoreSessions = false;
        // Event metadata fences belong to the source, not a cosmetic reload. Reset only after the
        // actual catalog was cleared (source replacement), never while a depth re-read is starting.
        if (_sessionItems.Count == 0)
        {
            _sessionCatalogEventVersions.Clear();
            _sessionCatalogReadVersion = _sessionCatalogEventVersion;
        }
    }

    /// <summary>
    /// 以给定 cursor.next（可为 null）重新开始分页；刷新成功整体重建列表后调用。仅 null 表示缺失
    /// （正常结束）；存在但非法（空串/纯空白/控制字符/超长）安全停止并提示，绝不解析 opaque 内容。
    /// 不递增列表代际（调用方已在其刷新开始时递增）。
    /// </summary>
    private void RestartSessionPagination(string? nextCursor)
    {
        _sessionUsedCursors.Clear();
        _isLoadingMoreSessions = false;
        var kind = ClassifySessionCursor(nextCursor, out var cursor);
        _sessionNextCursor = kind == SessionCursorKind.Valid ? cursor : null;
        UpdateLoadMoreSessionsUi();
        if (kind == SessionCursorKind.Invalid)
        {
            ShowInvalidSessionCursorNotice();
        }
    }

    /// <summary>非法分页游标的统一提示：不声称完整，可经刷新重新开始。</summary>
    private void ShowInvalidSessionCursorNotice() =>
        ShowMessage(
            InfoBarSeverity.Warning,
            "会话分页游标异常",
            "服务端返回了不可用的分页游标（空串/空白/控制字符或超长）；已安全停止继续加载。" +
            "列表可能不完整，可点击“刷新会话列表”重新开始。");

    /// <summary>刷新“加载更多”按钮：无 next 或页面未激活时隐藏；在途时禁用并显示“加载中…”。</summary>
    private void UpdateLoadMoreSessionsUi()
    {
        OnSessionCatalogChanged();
    }

    /// <summary>
    /// 续页：仅 cursor + limit（不带 order）。按 session id 追加去重、绝不切换选中；
    /// 按契约处理正常结束与异常游标；响应应用前做页面代际 + 列表代际双重校验。
    /// </summary>
    private async Task LoadMoreSessionsAsync()
    {
        if (!SourceReady || !_pageActive || !_hostWindowVisible || _isLoadingMoreSessions)
        {
            return;
        }

        var cursor = _sessionNextCursor;
        var client = _client;
        if (cursor is null || client is null)
        {
            UpdateLoadMoreSessionsUi();
            return;
        }

        var generation = _pageGeneration;
        var epoch = _sessionListEpoch;
        var source = _source;
        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_pageCts?.Token ?? CancellationToken.None,
            source?.RevocationToken ?? CancellationToken.None, _homeActive ? _homeLiveCancellation?.Token ?? CancellationToken.None : CancellationToken.None);
        var ct = linked.Token;
        bool CurrentPage() => IsCurrentSessionPage(generation, epoch) && !ct.IsCancellationRequested
            && ReferenceEquals(source, _source) && ReferenceEquals(client, _client)
            && revision == _sourceRevision && sourceGeneration == _sourceGeneration;

        _isLoadingMoreSessions = true;
        UpdateLoadMoreSessionsUi();

        try
        {
            using var flight = await EnterSessionCatalogReadAsync(client, ct);
            if (flight is null || !CurrentPage()) return;
            _sessionCatalogReadVersion = _sessionCatalogEventVersion;
            OpenCodeSessionsResponse response;
            try
            {
                response = await client.ListSessionsAsync(
                    new OpenCodeSessionListQuery { Cursor = cursor, Limit = SessionPageSize, ParentFilter = OpenCodeSessionParentFilter.Roots },
                    ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (OpenCodeApiException ex)
            {
                if (CurrentPage())
                {
                    if (ex.StatusCode == 400)
                    {
                        // 续页 400（InvalidCursorError / InvalidRequestError；错误体不解析）＝游标不可继续：
                        // 重试同一游标没有意义，安全停止并提示（不声称完整），用户可刷新重新开始。
                        SetConnectionState(ConnectionState.Connected);
                        StopSessionPaginationWithNotice(
                            "会话分页已停止",
                            "服务端拒绝了续页请求（HTTP 400，可能为 InvalidCursorError）；已停止继续加载。" +
                            "列表可能不完整，可点击“刷新会话列表”重新开始。");
                    }
                    else
                    {
                        HandleApiException(ex, "加载更多会话");
                    }
                }

                return;
            }
            catch (OpenCodeTransportException)
            {
                if (CurrentPage())
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "无法连接 OpenCode 服务", "加载更多会话失败；可稍后重试。");
                }

                return;
            }
            catch (OpenCodeTimeoutException)
            {
                if (CurrentPage())
                {
                    SetConnectionState(ConnectionState.Failed);
                    ShowMessage(InfoBarSeverity.Error, "加载更多会话超时", "客户端 30 秒内未完成请求；可稍后重试。");
                }

                return;
            }
            catch (OpenCodeProtocolException)
            {
                if (CurrentPage())
                {
                    ShowMessage(InfoBarSeverity.Error, "响应不符合契约", "会话列表续页响应不符合已核实的 V2 契约；已停止本次加载。");
                }

                return;
            }

            if (!CurrentPage())
            {
                return;
            }

            SetConnectionState(ConnectionState.Connected);

            var data = response.Data ?? new List<OpenCodeSessionInfo>();
            var nextKind = ClassifySessionCursor(response.Cursor?.Next, out var next);

            // 空 data：契约规定正常结束（即使携带 next 也不再继续，避免空页循环）。
            if (data.Count == 0)
            {
                _sessionNextCursor = null;
                UpdateLoadMoreSessionsUi();
                return;
            }

            AppendSessionItems(data);

            // next 存在但非法（空串/纯空白/控制字符/超长）：不解析 opaque 内容，安全停止并提示。
            if (nextKind == SessionCursorKind.Invalid)
            {
                StopSessionPaginationWithNotice(
                    "会话分页游标异常",
                    "服务端返回了不可用的分页游标（空串/空白/控制字符或超长）；已安全停止继续加载。" +
                    "列表可能不完整，可点击“刷新会话列表”重新开始。");
                return;
            }

            if (next is null)
            {
                // 缺失 next：正常结束；不断言“已加载完整”。
                _sessionNextCursor = null;
                UpdateLoadMoreSessionsUi();
                return;
            }

            // next 与本次请求游标相同（重复）或命中历史游标（循环）＝服务端游标异常：
            // 安全停止并提示，绝不声称完整。
            if (string.Equals(next, cursor, StringComparison.Ordinal) || _sessionUsedCursors.Contains(next))
            {
                StopSessionPaginationWithNotice(
                    "会话分页游标异常",
                    "服务端返回了重复的分页游标，继续加载可能造成循环；已安全停止。" +
                    "列表可能不完整，可点击“刷新会话列表”重新开始。");
                return;
            }

            _sessionUsedCursors.Add(cursor);
            _sessionNextCursor = next;
            UpdateLoadMoreSessionsUi();
        }
        catch (Exception)
        {
            if (CurrentPage())
            {
                ShowMessage(InfoBarSeverity.Error, "加载更多会话失败", "发生未预期错误；请稍后重试。");
            }
        }
        finally
        {
            if (IsCurrentSessionPage(generation, epoch))
            {
                _isLoadingMoreSessions = false;
                UpdateLoadMoreSessionsUi();
            }
        }
    }

    /// <summary>按 session id 追加去重（已存在或 id 无效的条目跳过）；不改变选中、不滚动、不切换会话。</summary>
    private void AppendSessionItems(List<OpenCodeSessionInfo> sessions)
    {
        foreach (var session in sessions)
        {
            if (!IsRootSession(session) || session?.Id is not { Length: > 0 } id || _sessionItems.ContainsKey(id)
                || _sessionCatalogEventVersions.GetValueOrDefault(id) > _sessionCatalogReadVersion || session.Time?.Archived is not null)
            {
                continue;
            }

            _sessionItems[id] = session;
        }
        OnSessionCatalogChanged();
        RefreshSessionStateUi();
    }

    /// <summary>安全停止分页：清 next（按钮隐藏）并给出不声称完整的警告提示；可经刷新重新开始。</summary>
    private void StopSessionPaginationWithNotice(string title, string message)
    {
        _sessionNextCursor = null;
        UpdateLoadMoreSessionsUi();
        ShowMessage(InfoBarSeverity.Warning, title, message);
    }

    /// <summary>响应应用前的双重代际校验：页面仍激活，且页面代际与列表代际均未变化。</summary>
    private bool IsCurrentSessionPage(int generation, int epoch) =>
        _pageActive && generation == _pageGeneration && epoch == _sessionListEpoch;
}
