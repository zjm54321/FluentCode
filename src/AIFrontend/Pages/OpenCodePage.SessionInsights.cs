using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Automation;

namespace AIFrontend.Pages;

/// <summary>
/// 会话概况接线：标签栏右侧“概况”开关 + 右侧概况面板（重设计 v3 §10 + Wave 3 F10；SelectorBar 分区：概况 / Billion。
/// 概况 = 上下文参考圆环（点击看当前上下文、用量计费、ACP/Billion 状态）+ to do；MCP/插件/LSP 由页头连接状态 Flyout 显示，
/// 复用同一次读取结果）。读取与投影全部在
/// <see cref="OpenCodeSessionInsightsController"/>，本文件只负责捕获页面绑定、生命周期与渲染。
/// </summary>
/// <remarks>
/// <para>
/// 常驻面板或抽屉可见时自动加载；当前会话事件在安静窗口后合并刷新，不定时轮询。
/// 上下文与 to do 复用已接纳消息即时本地更新；to do 按需独立补读历史。Billion 健康/详情保持单独确认并点击读取，
/// 地址元数据经当前来源和已核实会话目录发现。换会话取消排队读取并重新加载可见内容。
/// </para>
/// <para>
/// 代际：绑定 = 来源引用 + Revision + 来源代际 + 页面客户端 + 页面/会话代际 + 选中会话 ID；
/// 令牌经 <c>CreateOperationToken</c> 链接来源撤销、页面与会话令牌。换源 / 换会话 / 卸载时
/// <see cref="ResetSessionInsights"/> 取消并清空，迟到结果不会重新出现。
/// </para>
/// <para>
/// 页头的工作进度条、会话行 spinner 与提醒点保持 Gate 1 含义，本面板的读取状态只显示在面板内部。
/// </para>
/// <para>
/// Billion 地址：面板事件携带渲染时的 <see cref="OpenCodeBillionContext"/>；这里只捕获当前完整绑定并与之一起
/// 交给控制器，绝不调用 BindBillionContext 为到达的事件换取新授权。编辑在输入时立即转发（使旧确认/在途读取失效）。
/// 真实换源（来源被移除，或来源代际/引用/Revision 变化）时即使没有选中会话也清除地址与确认；只换会话保留。
/// 地址只在内存中，手动编辑是可选覆盖；发现地址不等于同意读取记录。不持久化、不扫描端口。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    private OpenCodeSessionInsightsController? _insights;
    private readonly OpenCodeSessionInsightsView _insightsView = new();
    private bool _insightsVisible;
    private bool _insightsRefreshNeeded;
    private CancellationTokenSource? _insightsRefreshCts;
    private Task? _insightsLoadTask;
    private OpenCodeInsightsBinding? _insightsLoadBinding;

    // 上次在重置回调中观察到的来源生命周期标记（与是否选中会话无关）。
    private OpenCodePageSource? _insightsObservedSource;
    private long _insightsObservedRevision;
    private int _insightsObservedSourceGeneration;

    /// <summary>构造期接线（单次）。</summary>
    private void InitializeSessionInsights()
    {
        _insights = new OpenCodeSessionInsightsController(IsCurrentInsightsBinding);
        _insights.Changed += (_, _) => RenderSessionInsights();
        _insightsView.BillionReadRequested += (_, context) => _ = ReadBillionInsightsAsync(context);
        _insightsView.BillionOriginEdited += (_, edit) =>
        {
            // 在 TextChanging 内同步执行：只转发、不渲染（控制器发布的快照由视图排队显示）。
            edit.Accepted = TryCaptureInsightsBinding(out var binding)
                && _insights.SetBillionOrigin(binding!, edit.Context, edit.Candidate);
        };
        _insightsView.BillionConfirmationChanged += (_, request) =>
        {
            if (!TryCaptureInsightsBinding(out var binding)
                || !_insights.SetBillionConfirmed(binding!, request.Confirmed, request.Context))
            {
                // 过期或无效：按控制器当前快照还原复选框。
                RenderSessionInsights();
            }
        };
        // wave5 G5：页头上下文圆环（“概况”按钮左侧）。打开其 Flyout 与打开概况面板同一读取时机：当前会话尚无结果才读取一次。
        _insightsView.ContextDetailsOpening += (_, _) => OpenContextDetails();
        // ACP 组“设置 Billion 地址”：视图已切到 Billion 分区，这里打开右侧面板（与点“概况”开关相同的路径）。
        _insightsView.BillionSetupRequested += (_, _) =>
        {
            if (SessionInsightsButton.IsEnabled)
            {
                SessionInsightsButton.IsChecked = true;
                InsightsPane.IsPaneOpen = true;
                OpenSessionInsightsPane();
            }
        };
        ContextRingHost.Child = _insightsView.ContextRing.Root;
        // 视图只构造一次、只挂接一次到右侧面板宿主（此后不再换父元素：Billion 文本框焦点约束）。
        InsightsPaneHost.Child = _insightsView;
        RenderSessionInsights();
    }

    /// <summary>
    /// 换源 / 换会话 / 卸载：取消在途读取、清空概况与 Billion 结果；可见面板随后加载新绑定。经
    /// <c>ResetMessagePaginationState</c> 的分部扩展点触发（该方法在四个换代入口都会调用），
    /// 不改动已冻结的切换/导航方法体。
    /// </summary>
    partial void OnMessageHistoryReset() => ResetSessionInsights();

    partial void CaptureContextMetadataVersion(ref long version) => version = _insights?.ContextMetadataVersion ?? -1;

    partial void OnContextMessagesAccepted(IReadOnlyList<OpenCodeMessage> messages)
    {
        OnAgentTreeMessages(messages);
        OnRuntimeMessagesAccepted(messages);
        if (_insights is { } insights && TryCaptureInsightsBinding(out var binding))
        {
            insights.UpdateContext(binding!, messages, IsLoadedMessageHistoryComplete(), CaptureTodoHistory());
            if (_insightsVisible) _ = EnsureSessionTodoAsync(insights, binding!);
            if (_insightsVisible && !insights.HasResultFor(binding!)) OpenSessionInsightsPane();
        }
    }

    partial void OnRuntimeMessagesAccepted(IReadOnlyList<OpenCodeMessage> messages);
    partial void OnAgentTreeMessages(IReadOnlyList<OpenCodeMessage> messages);
    partial void OnAgentTreeHistoryReset();

    partial void OnContextSessionMetadata(OpenCodeModelRef? model, string directory, long version)
    {
        if (_insights is { } insights && TryCaptureInsightsBinding(out var binding))
            _ = RefreshContextMetadataAsync(insights, binding!, model, directory, version);
    }

    private async Task RefreshContextMetadataAsync(OpenCodeSessionInsightsController insights, OpenCodeInsightsBinding binding,
        OpenCodeModelRef? model, string directory, long version)
    {
        using var operation = CreateOperationToken(binding.Source);
        await insights.UpdateContextMetadataAsync(binding, model, directory, version, operation.Token);
    }

    private void ResetSessionInsights()
    {
        CancelScheduledInsightsRefresh();
        _insightsRefreshNeeded = false;
        _insightsLoadTask = null;
        _insightsLoadBinding = null;
        OnAgentTreeHistoryReset();
        var clearOrigin = ObserveInsightsSourceLifetime();
        if (clearOrigin)
        {
            _insightsView.ForgetBillionOriginDraft();
        }

        _insights?.Reset(clearBillionOrigin: clearOrigin);
        // Selection/source installation finishes in the current UI turn. Read only the live binding afterwards.
        if (_insightsVisible) DispatcherQueue.TryEnqueue(() =>
        {
            if (_insightsVisible && TryCaptureInsightsBinding(out _)) OpenSessionInsightsPane();
        });
    }

    private void OnSessionInsightsVisibilityChanged(bool visible)
    {
        var wasVisible = _insightsVisible;
        _insightsVisible = visible;
        if (visible && (!wasVisible || (_insights is { } insights && TryCaptureInsightsBinding(out var binding)
            && !insights.HasResultFor(binding!)))) OpenSessionInsightsPane();
        else if (!visible) CancelScheduledInsightsRefresh();
    }

    partial void OnSessionInsightsEvent(OpenCodeEvent item)
    {
        if (item.SessionId is { } todoSession && (item.Type.StartsWith("session.revert.", StringComparison.Ordinal)
            || (item.Type == "session.tool.success" && (todoSession != _selectedSessionId
                || (item.Data.TryGetProperty("name", out var name)
                    && name.ValueKind == System.Text.Json.JsonValueKind.String && name.GetString() == OpenCodeTodoReplay.ToolId)))))
            _insights?.InvalidateTodo(todoSession);
        if (item.SessionId != _selectedSessionId || item.SessionId is null
            || !TryCaptureInsightsBinding(out var binding)) return;
        if (!item.Type.StartsWith("session.", StringComparison.Ordinal)
            && !item.Type.StartsWith("message.", StringComparison.Ordinal)
            && !item.Type.StartsWith("todo.", StringComparison.Ordinal)) return;
        _insightsRefreshNeeded = true;
        if (!_insightsVisible) return;
        CancelScheduledInsightsRefresh();
        var cts = new CancellationTokenSource();
        _insightsRefreshCts = cts;
        _ = RefreshSessionInsightsAfterQuietAsync(binding!, cts);
    }

    private void CancelScheduledInsightsRefresh()
    {
        _insightsRefreshCts?.Cancel();
        _insightsRefreshCts = null;
    }

    private async Task RefreshSessionInsightsAfterQuietAsync(OpenCodeInsightsBinding binding, CancellationTokenSource owner)
    {
        try
        {
            // Trailing debounce: a continuous stream restarts this delay, never a periodic full read.
            await Task.Delay(600, owner.Token);
            // Long gaps between streaming chunks are not completion. Keep local usage/to do current,
            // but wait for a real idle/terminal status event before re-reading the expensive sections.
            if (_liveSessionStatuses.TryGetValue(binding.SessionId, out var live) && live.Working) return;
            if (_insightsLoadBinding == binding && _insightsLoadTask is { IsCompleted: false } running)
                await running;
            if (owner.IsCancellationRequested || !ReferenceEquals(owner, _insightsRefreshCts)
                || !_insightsVisible || !IsCurrentInsightsBinding(binding) || binding.Source.IsRevoked) return;
            _insightsRefreshNeeded = false;
            await LoadSessionInsightsAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(owner, _insightsRefreshCts)) _insightsRefreshCts = null;
            owner.Dispose();
        }
    }

    /// <summary>
    /// 记录来源生命周期标记并返回是否为真实换源/移除。顺序依据：InvalidateForSourceChange 先递增来源代际并清空
    /// _source，再经 ResetMessagePaginationState 到达此处，新来源随后才安装；卸载清空 _source 但不递增代际。
    /// 因此：来源为空 → 清除；代际变化 → 清除；同代际内由空变为已安装（换源后的首次重置）不重复清除；
    /// 同代际内引用或 Revision 变化（未来可能的直接替换）→ 清除。只换会话时三者不变 → 保留。
    /// </summary>
    private bool ObserveInsightsSourceLifetime()
    {
        var source = _source;
        var clear = source is null
            || _sourceGeneration != _insightsObservedSourceGeneration
            || (_insightsObservedSource is not null
                && (!ReferenceEquals(_insightsObservedSource, source) || _insightsObservedRevision != _sourceRevision));
        _insightsObservedSource = source;
        _insightsObservedRevision = source is null ? 0 : _sourceRevision;
        _insightsObservedSourceGeneration = _sourceGeneration;
        return clear;
    }

    /// <summary>纯字段比较（可在来源锁内调用）：不访问 App、不 await。</summary>
    private bool IsCurrentInsightsBinding(OpenCodeInsightsBinding binding)
        => _pageActive
        && ReferenceEquals(binding.Source, _source)
        && binding.Revision == _sourceRevision
        && binding.SourceGeneration == _sourceGeneration
        && ReferenceEquals(binding.Client, _client)
        && binding.PageGeneration == _pageGeneration
        && binding.SessionGeneration == _sessionGeneration
        && string.Equals(binding.SessionId, _selectedSessionId, StringComparison.Ordinal);

    private bool TryCaptureInsightsBinding(out OpenCodeInsightsBinding? binding)
    {
        binding = null;
        if (!_pageActive || !SourceReady || _source is not { } source || _client is not { } client
            || _selectedSessionId is not { Length: > 0 } sessionId || _sessionMissing)
        {
            return false;
        }

        binding = new OpenCodeInsightsBinding(source, _sourceRevision, _sourceGeneration, client,
            _pageGeneration, _sessionGeneration, sessionId);
        return true;
    }

    private string DescribeInsightsUnavailable()
        => !SourceReady ? "OpenCode 连接尚未就绪。"
            : _selectedSessionId is null ? "请先选择一个会话。"
            : _sessionMissing ? "当前会话已不存在。"
            : "暂时无法读取。";

    /// <summary>可见右侧面板或上下文详情：自动初始化，布局重复通知不重新读取。</summary>
    private void OpenSessionInsightsPane()
    {
        if (_insights is null)
        {
            return;
        }

        if (!TryCaptureInsightsBinding(out var binding))
        {
            var clearOrigin = ObserveInsightsSourceLifetime();
            if (clearOrigin)
            {
                _insightsView.ForgetBillionOriginDraft();
            }

            _insights.Reset(null, DescribeInsightsUnavailable(), clearOrigin);
            return;
        }

        // Visibility changes are frequent during layout; reuse an in-flight or already current result.
        if (!_insights.HasResultFor(binding!) || (_insightsRefreshNeeded && _insightsRefreshCts is null))
        {
            _insightsRefreshNeeded = false;
            _ = LoadSessionInsightsAsync();
        }
        else
        {
            RenderSessionInsights();
        }
    }

    /// <summary>上下文详情也可初始化概况；常驻圆环的本地上下文参考不依赖点击。</summary>
    private void OpenContextDetails() => OpenSessionInsightsPane();

    private Task LoadSessionInsightsAsync()
    {
        if (_insights is not { } insights)
        {
            return Task.CompletedTask;
        }

        if (!TryCaptureInsightsBinding(out var binding))
        {
            var clearOrigin = ObserveInsightsSourceLifetime();
            if (clearOrigin) _insightsView.ForgetBillionOriginDraft();
            insights.Reset(null, DescribeInsightsUnavailable(), clearOrigin);
            return Task.CompletedTask;
        }

        if (_insightsLoadBinding == binding && _insightsLoadTask is { IsCompleted: false } running) return running;
        _insightsLoadBinding = binding;
        return _insightsLoadTask = LoadSessionInsightsCoreAsync(insights, binding!);
    }

    private async Task LoadSessionInsightsCoreAsync(OpenCodeSessionInsightsController insights, OpenCodeInsightsBinding binding)
    {
        using var operation = CreateOperationToken(binding!.Source);
        try
        {
            // 同次 LoadAsync 复用可信 Session.Info.Location 发起一次元数据发现；不会读取 Billion 健康/详情或代替确认。
            // 复用已加载、已净化的消息（旧→新）；只有分页链正常结束且无缺口才算历史完整。
            await insights.LoadAsync(binding, MergeMessageHistory(), IsLoadedMessageHistoryComplete(), operation.Token,
                CaptureTodoHistory());
        }
        catch (Exception)
        {
            // 控制器已把未完成分区标为“无法确认”；不在页面弹出原始错误。
        }
    }

    /// <summary>
    /// to do 派生所需的同源历史证据：分页链异常停止 = 读取失败；撤回位置只有在 Session 状态已确认
    /// （且不待重新确认）时才算已知。消息本身不在此裁剪，由 <see cref="OpenCodeTodoReplay.Derive"/> 按官方规则裁剪。
    /// </summary>
    private OpenCodeTodoHistory CaptureTodoHistory()
        => new(_messageChainState == MessagePaginationChainState.Stopped,
            _messageHistoryBoundaryKnown && !_messageHistoryNeedsState, _messageRevertBoundary);

    private async Task EnsureSessionTodoAsync(OpenCodeSessionInsightsController insights, OpenCodeInsightsBinding binding)
    {
        using var operation = CreateOperationToken(binding.Source);
        await insights.EnsureTodoHistoryAsync(binding, operation.Token);
    }

    /// <param name="renderedContext">点击时界面显示的上下文；原样交给控制器，不用新绑定替换。</param>
    private async Task ReadBillionInsightsAsync(OpenCodeBillionContext renderedContext)
    {
        if (_insights is not { } insights || !TryCaptureInsightsBinding(out var binding))
        {
            RenderSessionInsights();
            return;
        }

        using var operation = CreateOperationToken(binding!.Source);
        try
        {
            await insights.ReadBillionAsync(binding, operation.Token, renderedContext);
        }
        catch (Exception)
        {
            // 连接器只返回白名单状态；任何意外都不把原始信息带到界面。
        }
    }

    private void RenderSessionInsights()
    {
        if (_insights is not { } insights)
        {
            return;
        }

        var snapshot = insights.Snapshot;
        // Changed carries no saved payload: always render the latest snapshot, and reject a late source/session owner.
        // LoadAsync/its discovery continuation preserve this UI caller's synchronization context; View owns queued visuals.
        if (snapshot.BillionContext is { } context
            && (!IsCurrentInsightsBinding(context.Binding) || context.Binding.Source.IsRevoked)) return;
        _insightsView.Render(snapshot);
        var description = OpenCodeSessionInsightsView.DescribeEntry(snapshot);
        // wave5 G5：图标按钮的 Tooltip 固定为“概况”；采样说明只作为无障碍帮助文本。
        AutomationProperties.SetHelpText(SessionInsightsButton, description);
        OnSessionInsightsRendered();
    }

    /// <summary>概况渲染后的扩展点：页头连接详情 Flyout（OpenCodePage.ConnectionFlyout.cs）在打开时据最新快照重建 MCP / 插件。</summary>
    partial void OnSessionInsightsRendered();

}
