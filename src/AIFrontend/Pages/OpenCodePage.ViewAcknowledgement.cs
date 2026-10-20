using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

/// <summary>
/// 纯策略：从“当前最新消息窗口”读取已渲染的最新 idle 水位。
/// 水位取 idle 消息 <see cref="OpenCodeMessageTime.Created"/>（Unix epoch 毫秒）——与
/// <c>OpenCodeNotificationMonitor</c> 以 <c>Session.Time.Idle == idle.Created</c> 关联同一服务端快照的
/// 语义一致；绝不额外 GET 最新水位（不以更大的服务端水位扩大 ACK）。无 WinUI / Notifications / App
/// 依赖，可隔离编译。
/// </summary>
internal static class OpenCodeViewAcknowledgementReader
{
    /// <summary>
    /// 返回窗口内最后一个 idle 消息的水位（旧→新顺序的最后一个 idle，见
    /// <see cref="OpenCodeExecutionStateReader.LatestIdle"/>）；窗口为空、无 idle、idle 无 <c>time</c>
    /// 或 <c>time.created</c> 非有限数值时返回 false（不猜测）。
    /// </summary>
    internal static bool TryGetRenderedIdleWatermark(IReadOnlyList<OpenCodeMessage>? window, out double watermark)
    {
        watermark = 0;
        var idle = OpenCodeExecutionStateReader.LatestIdle(window);
        if (idle?.Time is not { } time)
        {
            return false;
        }

        var created = time.Created;
        if (!double.IsFinite(created))
        {
            return false;
        }

        watermark = created;
        return true;
    }
}

/// <summary>
/// OpenCode 页“已渲染完成水位 ACK”逻辑（view）：在宿主确认“当前最新消息窗口已成功 Apply 且页面
/// 真正可见”时，从已渲染窗口的最新 idle 取水位，经 App 入口发起一次显式 view POST。
/// </summary>
/// <remarks>
/// <para>
/// 契约（docs/opencode-client-contract.md 第 17 节）：view 只确认已观察到的 idle 水位，不表示
/// Permission / Form 已读，也不是模型侧 ack；只发送单次 <c>POST api/session/{sid}/view</c>，仅 204 成功，
/// 取消 / 超时 / 断连可能已应用，绝不自动重试。本文件只提供宿主入口与最小状态：绝不新增网络客户端、
/// 绝不额外 GET 最新水位、绝不触碰 permission / form pending 与共享 gate 保护。
/// </para>
/// <para>
/// 水位来源：只取宿主传入的“当前最新消息窗口”内最后一个 idle 的 <c>time.created</c>；绝不读取会话
/// 信息的更新水位来放大 ACK。无已渲染 idle（或缺少有限 <c>time.created</c>）即不尝试。
/// </para>
/// <para>
/// 去重与重试：以 (来源身份, 会话, 水位) 为键（<strong>刻意不含 Revision</strong>，同 endpoint 换凭据不重置去重），
/// 每个键至多自动尝试一次；尝试键在发起 POST <strong>之前</strong>记录，因此返回 null（结果未知）也绝不因后续
/// 轮询 / 渲染而自动重试同一水位。切走会话 / 卸载 / 来源暂时为 null 再回来都不重置尝试键，故相同水位不会因
/// 清 UI 或换代而重发不确定 POST。尝试键有真实上限：满时停止新增自动 ACK，绝不逐出旧键（逐出会导致重试）。
/// </para>
/// <para>
/// 代际与线程：宿主入口一律在 UI 线程；App 入口要求 UI 线程发起，且其跨线程 fence 只读取本页
/// <see cref="ViewAcknowledgementLive"/> 这一<strong>不可变原子快照</strong>（volatile 引用整体替换），
/// 绝不读取 XAML、绝不取 App / 来源锁。页面 / 来源 / 会话失效时立即清空快照，使在途 fence 立即失败，
/// 并以链接来源撤销 + 页面 + 会话的操作令牌取消在途请求。
/// </para>
/// <para>
/// 可见性：要求页面处于活动导航态（<c>_pageActive</c>）、来源未撤销，并以<strong>实时</strong> XAML 属性
/// （已加载、未折叠、已挂 <c>XamlRoot</c>、具非零布局尺寸）判定页面可见，绝不仅凭缓存的 Loaded 标志推断；
/// 窗口级可见性（最小化 / 被遮挡等 XAML 无法判定的部分）由宿主经入口提供，fail-closed 默认 false——
/// 宿主未确认可见前不发起任何 ACK。所有宿主入口都要求 UI 线程调用，非 UI 调用一律直接拒绝（不排队）。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    /// <summary>
    /// 已尝试自动 ACK 的水位键：按 endpoint 身份保守去重，每个 (来源身份, 会话, 水位) 至多自动尝试一次。
    /// <strong>刻意不含 Revision</strong>：同 endpoint 换凭据（Revision 变化）不得重置去重，否则同一水位会因换代
    /// 被当作“新”键而重发结果未知的 POST。Revision 仅用于 fence（<see cref="ViewAcknowledgementOrigin"/>），
    /// 绝不作为“清未知授权”。
    /// </summary>
    private readonly record struct ViewAcknowledgementKey(string Identity, string SessionId, double Watermark);

    /// <summary>
    /// 某次 ACK 捕获的不可变起点：来源引用 / Revision / 来源代际 / 会话 / 会话代际 / 水位。
    /// fence 以逐项比较该起点与实时活动快照判定“选择 / 会话 / 可见性是否仍成立”。
    /// </summary>
    private readonly record struct ViewAcknowledgementOrigin(
        OpenCodePageSource Source, long Revision, int SourceGeneration, string SessionId, int SessionGeneration, double Watermark);

    /// <summary>
    /// 跨线程只读的不可变活动快照：由 UI 线程整体替换（volatile 引用写），后台 fence 只读该引用并逐项比较，
    /// 不读取 XAML、不取 App / 来源锁。null 表示无有效关联（来源未就绪 / 已失效 / 无选中会话）。
    /// </summary>
    private sealed record ViewAcknowledgementLive(
        OpenCodePageSource Source, long Revision, int SourceGeneration, string SessionId, int SessionGeneration, bool Visible);

    /// <summary>已自动尝试的水位键（仅 UI 线程访问）。</summary>
    private readonly HashSet<ViewAcknowledgementKey> _viewAcknowledgementAttempted = new();

    /// <summary>当前活动快照（仅 UI 线程写、后台 fence 读；volatile 保证可见性与原子引用替换）。</summary>
    private volatile ViewAcknowledgementLive? _viewAcknowledgementLive;

    /// <summary>宿主提供的窗口级可见性（最小化 / 被遮挡等 XAML 无法判定的部分）；fail-closed 默认 false。</summary>
    private bool _viewAcknowledgementWindowVisible;

    /// <summary>
    /// 自动尝试键上限（有界防御）。达到上限后<strong>停止新增</strong>自动 ACK，绝不逐出旧键——逐出会让被逐出的
    /// (来源, 会话, 水位) 之后再次出现时被当作未尝试而重发结果未知的 POST。代价是页面实例生命周期内最多自动
    /// ACK 该数量的不同水位；耗尽后仅保留显式入口，不再自动 ACK。这是有意的有界限制，不引入新的持久化架构。
    /// </summary>
    private const int ViewAcknowledgementAttemptCap = 512;

    /// <summary>
    /// 宿主入口（<strong>必须在 UI 线程调用</strong>）：当前最新消息窗口已成功 Apply。传入实际渲染窗口与
    /// 窗口级可见性；仅在“页面真正可见”（<c>_pageActive</c> + 来源未撤销 + 实时 XAML 属性 + 宿主窗口
    /// 可见性）时，从窗口内最新 idle 取水位，每个 (来源身份, 会话, 水位) 至多自动尝试一次，经 App 入口
    /// 发起一次 POST。结果未知绝不自动重试；不触碰 permission / form pending 与 gate 保护。
    /// </summary>
    /// <param name="appliedWindow">已成功 Apply 的当前最新消息窗口（服务端序列顺序，旧→新）。</param>
    /// <param name="windowVisible">宿主判定的窗口级可见性（最小化 / 被遮挡时为 false）。</param>
    internal void NotifyViewAcknowledgementRendered(IReadOnlyList<OpenCodeMessage> appliedWindow, bool windowVisible)
    {
        ArgumentNullException.ThrowIfNull(appliedWindow);
        if (!DispatcherQueue.HasThreadAccess)
        {
            // 本入口要求 UI 线程调用：非 UI 调用直接拒绝，绝不排队。排队会在 UI 线程上读取“当时”的
            // _source / _selectedSessionId，可能把调用方捕获的 A 窗口 ACK 到已切换的 B 上下文，且跨线程
            // 读取 XAML / 代际字段不安全。调用方须自行 marshal 到 UI 线程后再调用。
            return;
        }

        _viewAcknowledgementWindowVisible = windowVisible;
        PublishViewAcknowledgementLive();
        TryAcknowledgeRenderedWindow(appliedWindow);
    }

    /// <summary>
    /// 宿主入口（<strong>必须在 UI 线程调用</strong>）：更新窗口级可见性（最小化 / 还原 / 被遮挡），不发起
    /// ACK——下一次渲染完成再由 <see cref="NotifyViewAcknowledgementRendered"/> 尝试。用于使在途 fence
    /// 立即反映最新可见性。非 UI 调用直接拒绝，不排队：排队的旧可见性事件会在 UI 线程上覆盖新上下文的可见性。
    /// </summary>
    internal void UpdateViewAcknowledgementVisibility(bool windowVisible)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return;
        }

        _viewAcknowledgementWindowVisible = windowVisible;
        PublishViewAcknowledgementLive();
    }

    /// <summary>
    /// 宿主入口（<strong>必须在 UI 线程调用</strong>）：来源 / 会话 / 页面失效（切换会话、来源换代、卸载、
    /// 可见性失效）时清空活动快照并取消关联——在途 POST 的 fence 立即失败，操作令牌随来源 / 页面 / 会话取消。
    /// 已尝试水位集合<strong>一律保留</strong>：卸载 / 来源暂时为 null 后切回同一会话，相同水位绝不因清 UI 而
    /// 重发结果未知的 POST。非 UI 调用直接拒绝，不排队：排队的旧失效事件会清掉新上下文的活动快照。
    /// 绝不触碰 permission / form pending 与 gate 保护。
    /// </summary>
    internal void InvalidateViewAcknowledgement()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            return;
        }

        _viewAcknowledgementLive = null;
        _viewAcknowledgementWindowVisible = false;
    }

    /// <summary>
    /// 以<strong>实时</strong>页面 / XAML 属性判定页面是否真正可见：页面处于活动导航态（<c>_pageActive</c>，
    /// 缓存页面在切走后仍 <c>IsLoaded</c>）、已加载、未折叠、已挂 <c>XamlRoot</c> 且具非零布局尺寸。
    /// 绝不仅凭缓存的 Loaded 标志推断。
    /// </summary>
    private bool IsViewAcknowledgementPageVisible()
        => _pageActive
        && IsLoaded
        && Visibility == Microsoft.UI.Xaml.Visibility.Visible
        && XamlRoot is not null
        && ActualWidth > 0
        && ActualHeight > 0;

    /// <summary>
    /// 按当前页面状态整体替换活动快照（UI 线程）；来源未就绪 / 已撤销 / 无选中会话时置 null。
    /// 可见性要求 <c>_pageActive</c> + 来源未撤销 + 实时 XAML 可见 + 宿主窗口可见，全部满足才记 Visible。
    /// </summary>
    private void PublishViewAcknowledgementLive()
    {
        var source = _source;
        var sessionId = _selectedSessionId;
        _viewAcknowledgementLive = source is null || source.IsRevoked || sessionId is not { Length: > 0 }
            ? null
            : new ViewAcknowledgementLive(source, _sourceRevision, _sourceGeneration, sessionId, _sessionGeneration,
                IsViewAcknowledgementPageVisible() && _viewAcknowledgementWindowVisible);
    }

    private void TryAcknowledgeRenderedWindow(IReadOnlyList<OpenCodeMessage> window)
    {
        var source = _source;
        var sessionId = _selectedSessionId;
        if (source is null || source.IsRevoked || sessionId is not { Length: > 0 })
        {
            return;
        }

        var live = _viewAcknowledgementLive;
        if (live is not { Visible: true }
            || !ReferenceEquals(live.Source, source)
            || live.Revision != _sourceRevision
            || live.SourceGeneration != _sourceGeneration
            || live.SessionGeneration != _sessionGeneration
            || !string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
        {
            // 页面 / 来源 / 会话未处于可见且一致的代际：不尝试（也不占用尝试键，待可见后可再尝试）。
            return;
        }

        if (!OpenCodeViewAcknowledgementReader.TryGetRenderedIdleWatermark(window, out var watermark))
        {
            return;
        }

        var key = new ViewAcknowledgementKey(source.Identity, sessionId, watermark);
        if (_viewAcknowledgementAttempted.Count >= ViewAcknowledgementAttemptCap)
        {
            // 已达上限：停止新增自动 ACK（也绝不逐出旧键导致重试）。已存在的键同样不重试。
            return;
        }

        if (!_viewAcknowledgementAttempted.Add(key))
        {
            return; // 该 (来源身份, 会话, 水位) 已自动尝试过（含结果未知）：绝不自动重试。
        }

        var origin = new ViewAcknowledgementOrigin(
            source, _sourceRevision, _sourceGeneration, sessionId, _sessionGeneration, watermark);
        _ = AcknowledgeRenderedAsync(origin);
    }

    /// <summary>
    /// 经 App 入口发起一次显式 view POST：以捕获起点 + 跨线程只读活动快照 fence（App 在来源私锁内、
    /// POST 前后各调用一次）守护；操作令牌链接来源撤销 + 页面 + 会话取消。App 已在其内部收敛异常并
    /// 记录诊断，此处绝不重试，也不在结果未知时轮询重发。整个方法体（含令牌创建）都在异常安全边界内：
    /// 本方法是 fire-and-forget，任何逃逸异常都会变成未观察的任务异常。
    /// </summary>
    private async Task AcknowledgeRenderedAsync(ViewAcknowledgementOrigin origin)
    {
        try
        {
            var app = Application.Current as App;
            if (app is null)
            {
                return;
            }

            using var operationCts = CreateOperationToken(origin.Source);
            bool IsStillVisible()
            {
                var live = _viewAcknowledgementLive;
                return live is { Visible: true }
                    && ReferenceEquals(live.Source, origin.Source)
                    && live.Revision == origin.Revision
                    && live.SourceGeneration == origin.SourceGeneration
                    && live.SessionGeneration == origin.SessionGeneration
                    && string.Equals(live.SessionId, origin.SessionId, StringComparison.Ordinal);
            }

            await app.AcknowledgeOpenCodeSessionAsync(
                    origin.Source, origin.Revision, origin.SessionId, origin.Watermark, IsStillVisible, operationCts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // App 已在其内部收敛并记录诊断；结果未知绝不自动重试。CreateOperationToken 等本地失败也在此收敛，
            // 避免 fire-and-forget 任务产生未观察异常。
        }
    }
}
