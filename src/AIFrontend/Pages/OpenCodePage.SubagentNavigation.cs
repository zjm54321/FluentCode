using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

/// <summary>
/// OpenCode 页 subagent 只读子会话小窗与最大化导航：先以服务端真实祖先链复核归属再读取消息，
/// 仅最大化才重新复核并切换到子会话；关闭或失败保留当前会话，取消不报错误。
/// </summary>
/// <remarks>
/// <para>
/// 事件来源：<see cref="OpenCodeConversationView.SubagentSessionRequested"/>（工具部件
/// <see cref="OpenCodeToolPartView"/> 经会话视图转发）。参数只含父消息 id 与子会话 id（尚未创建时为空），
/// 属展示请求、不构成授权。处理器打开独立只读会话窗口；通过既有客户端 GET 单会话信息核验祖先链，
/// 再 GET 该子会话最近消息。每次请求与显示都复核来源/会话绑定；不为 sidebar 追加子会话。
/// 窗口的关闭/切换取消在途读取，最大化复用既有导航路径；事件订阅仍由页面主文件接线。
/// </para>
/// <para>
/// 归属复核：以点击时刻捕获的选中会话为“期望父会话”，调用
/// <see cref="OpenCodeSessionAncestry.IsAncestorAsync"/> 求证子会话的真实祖先链是否包含它；
/// 仅返回 true 才继续（fail-closed：无法确定、读取失败、环、超预算均视为不成立）。
/// 父消息 id 为空（契约外匿名消息）或子会话 id 未通过不透明校验时不导航并提示。逐条消息归属
/// 直接复用视图的真实 id 索引：<see cref="OpenCodeConversationView.ContainsRenderedMessage"/>
/// 按真实消息 id 查询当前已渲染快照（匿名/重复兜底 key 不参与匹配）。导航开始、每次 await 之后
/// 以及切换前都要求该查询为 true 且来源/会话上下文未变；视图生命周期
/// （切换会话即清空并退订全部部件，见 <see cref="OpenCodeConversationView.ClearMessages"/>）
/// 仅作补充证据，不再作为唯一依据。
/// </para>
/// <para>
/// 并发与代际：每次导航捕获来源对象 / Revision / 来源代际 / 页面代际 / 会话代际 / 选中会话 /
/// 客户端 / 会话视图与父消息 id；每次 await 之后统一复核（未取消、页面激活、上述捕获全部未变、
/// 来源未撤销且父消息仍在该会话视图的当前渲染快照内），
/// 任一不符即放弃，绝不把结果应用到已切换的上下文。请求令牌经 <c>CreateOperationToken</c>
/// 链接来源撤销 + 页面 + 会话取消；来源撤销 / 页面卸载 / 切换会话触发的取消一律静默返回。
/// <c>_subagentNavigationBusy</c> 防重复点击（同一时刻至多一次导航在途）。
/// </para>
/// <para>
/// 列表项：sidebar 仅保留主会话，不追加子会话。归属成立后同步列表选中态
/// （缺项清空高亮，抑制 SelectionChanged 二次进入），再携带点击时捕获的来源会话走既有
/// <c>SwitchToSessionAsync</c>，不复制其切换逻辑、不额外发起会话跟踪事件。
/// </para>
/// </remarks>
public sealed partial class OpenCodePage
{
    /// <summary>子会话导航在途标记：同一时刻至多一次导航，防止重复点击。</summary>
    private bool _subagentNavigationBusy;

    private OpenCodeChildChatWindow? _childChatWindow;

    /// <summary>
    /// <see cref="OpenCodeConversationView.SubagentSessionRequested"/> 的处理器：先校验 sender 正是本页
    /// 持有的会话视图实例，再打开只读子会话小窗；只有小窗最大化才转交既有校验导航路径。
    /// </summary>
    private async void OnSubagentSessionRequested(object? sender, OpenCodeSubagentSessionRequestedEventArgs e)
    {
        // 事件只应由本页会话视图转发（视图以 this 作 sender）。来源不符——例如视图被替换，
        // 或事件被外部重放——一律不导航：不能仅凭事件参数信任其归属。
        if (sender is not OpenCodeConversationView view || !ReferenceEquals(view, _conversationView))
        {
            return;
        }

        try
        {
            await ShowSubagentChatAsync(view, e.ParentMessageId, e.ChildSessionId);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_pageActive) ShowMessage(InfoBarSeverity.Error, "无法打开子会话", "子会话窗口加载失败，请重试。");
        }
    }

    private async Task ShowSubagentChatAsync(OpenCodeConversationView view, string parentMessageId, string childSessionId)
    {
        var source = _source;
        var client = _client;
        var selectedSessionId = _selectedSessionId;
        if (source is null || client is null || selectedSessionId is not { Length: > 0 }
            || !_pageActive || source.IsRevoked || parentMessageId.Length == 0
            || !view.ContainsRenderedMessage(parentMessageId)
            || view.XamlRoot is not { } root) return;

        _childChatWindow?.Close();
        OpenCodeToolDetailPopup.Shared.Close();
        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        using var operation = CreateOperationToken(source);
        var bindingToken = operation.Token;
        bool BindingCurrent() => IsSubagentNavigationCurrent(source, revision, sourceGeneration, client, pageGeneration,
            sessionGeneration, selectedSessionId, view, parentMessageId, bindingToken);
        var window = new OpenCodeChildChatWindow(root, bindingToken, BindingCurrent, async () =>
        {
            if (IsSubagentNavigationCurrent(source, revision, sourceGeneration, client, pageGeneration,
                sessionGeneration, selectedSessionId, view, parentMessageId, bindingToken))
                await NavigateToSubagentSessionAsync(view, parentMessageId, childSessionId);
        });
        _childChatWindow = window;
        var ct = window.Token;
        bool Current() => window.IsOpen && ReferenceEquals(window, _childChatWindow)
            && IsSubagentNavigationCurrent(source, revision, sourceGeneration, client, pageGeneration,
                sessionGeneration, selectedSessionId, view, parentMessageId, ct);
        var childAuthorized = false;
        if (!OpenCodeSubagentPresentation.IsOpaqueSessionId(childSessionId))
            window.ShowError("子会话尚未创建或未提供有效的会话编号。");
        else await OpenCodeChildChatReadSession.LoadAsync(Current,
            async token =>
            {
                if (!TryStartRead(source, revision, sourceGeneration, client, sessionGeneration, token,
                    () => OpenCodeSessionAncestry.IsAncestorAsync(client, childSessionId, selectedSessionId, token), out var task))
                    throw new OperationCanceledException(token);
                var authorized = await task!;
                childAuthorized = authorized && Current();
                return childAuthorized;
            },
            token =>
            {
                if (!TryStartRead(source, revision, sourceGeneration, client, sessionGeneration, token,
                    () => client.GetMessagesAsync(childSessionId, new OpenCodeMessageQuery { Order = OpenCodeSessionOrders.Desc, Limit = 100 }, token), out var task))
                    throw new OperationCanceledException(token);
                return task!;
            }, window.ShowMessages, window.ShowError, ct);
        // Keep the binding token alive and observe rendered-message removal after the initial read.
        try
        {
            while (Current()) await Task.Delay(200, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            window.Close();
            if (ReferenceEquals(_childChatWindow, window)) _childChatWindow = null;
        }
    }

    /// <summary>
    /// 子会话导航流程：同步前置校验（含父消息仍属当前渲染视图）→ 祖先链复核（逐级 GET 单会话信息）
    /// → 同步列表高亮并携带来源返回目标走既有切换流程。每次 await 后按捕获上下文
    /// 复核；失败保留当前会话并提示，取消静默返回。
    /// </summary>
    private async Task NavigateToSubagentSessionAsync(
        OpenCodeConversationView sourceView, string parentMessageId, string childSessionId)
    {
        if (_subagentNavigationBusy)
        {
            return;
        }

        _subagentNavigationBusy = true;
        try
        {
            var source = _source;
            var client = _client;
            var selectedSessionId = _selectedSessionId;
            if (source is null || client is null || selectedSessionId is not { Length: > 0 }
                || !_pageActive || source.IsRevoked)
            {
                return; // 来源未就绪/已撤销、无选中会话或页面未激活：无可复核的上下文，不导航。
            }

            if (parentMessageId.Length == 0)
            {
                // 契约外匿名消息（assistant 消息缺失 id）：无法复核归属，fail-closed。
                ShowMessage(
                    InfoBarSeverity.Warning,
                    "无法复核子会话归属",
                    "该工具部件未携带父消息 id（契约外匿名消息）；已保留当前会话，未执行导航。");
                return;
            }

            if (!sourceView.ContainsRenderedMessage(parentMessageId))
            {
                // 父消息已不在该视图的当前渲染快照（点击后视图被清空/重建，或该消息被窗口化移除）：
                // 无可复核的归属，fail-closed 静默放弃——属上下文已变，不改动当前会话、不提示。
                return;
            }

            if (!OpenCodeSubagentPresentation.IsOpaqueSessionId(childSessionId))
            {
                // 事件参数本应已通过不透明校验；此处防御性复核（与客户端 ses 前缀校验同语义）。
                ShowMessage(
                    InfoBarSeverity.Warning,
                    "子会话 id 无效",
                    "导航请求的子会话 id 未通过不透明校验；已保留当前会话，未执行导航。");
                return;
            }

            var revision = _sourceRevision;
            var sourceGeneration = _sourceGeneration;
            var pageGeneration = _pageGeneration;
            var sessionGeneration = _sessionGeneration;
            using var operationCts = CreateOperationToken(source);
            var ct = operationCts.Token;

            bool ContextCurrent() => IsSubagentNavigationCurrent(
                source, revision, sourceGeneration, client, pageGeneration, sessionGeneration,
                selectedSessionId, sourceView, parentMessageId, ct);

            if (!ContextCurrent())
            {
                return;
            }

            if (!TryStartRead(
                    source, revision, sourceGeneration, client, sessionGeneration, ct,
                    () => OpenCodeSessionAncestry.IsAncestorAsync(client, childSessionId, selectedSessionId, ct),
                    out var ancestryTask))
            {
                return; // 来源已撤销/已取消或上下文已变化：未起发任何请求。
            }

            bool isAncestor;
            try
            {
                isAncestor = await ancestryTask!;
            }
            catch (OperationCanceledException)
            {
                return; // 取消不报错误（来源撤销/页面卸载/切换会话）。
            }
            catch (Exception)
            {
                if (ContextCurrent())
                {
                    ShowMessage(
                        InfoBarSeverity.Error,
                        "无法复核子会话归属",
                        "复核归属时发生未预期错误；已保留当前会话，未执行导航。");
                }

                return;
            }

            if (!ContextCurrent())
            {
                return;
            }

            if (!isAncestor)
            {
                // fail-closed：无法确定归属（数据不一致、读取失败、环或超出追溯预算）一律不导航。
                ShowMessage(
                    InfoBarSeverity.Warning,
                    "无法确认子会话归属",
                    "服务端父子链复核未确认该子会话属于当前会话；已保留当前会话，未执行导航。");
                return;
            }

            // 切换前最后复核：父消息仍属于捕获会话视图的当前渲染快照，且来源/会话上下文未变，否则不切换。
            if (!ContextCurrent())
            {
                return;
            }

            // 归属成立：先同步列表选中态（缺项清空高亮，抑制 SelectionChanged 二次进入），
            // 再走既有切换流程（其内部负责消息拉取、会话跟踪与各代际防护）。
            SelectSessionItem(childSessionId);
            // 来源会话为点击时捕获的 selectedSessionId，不一定是直接父会话
            await SwitchToSessionAsync(childSessionId, new SessionReturnTarget(selectedSessionId, SessionReturnKind.VisitedOrigin));
        }
        catch (OperationCanceledException)
        {
            // 取消不报错误（兜底；常规路径已在各 await 处静默返回）。
        }
        catch (Exception)
        {
            ShowMessage(InfoBarSeverity.Error, "子会话导航失败", "子会话导航过程中发生未预期错误。");
        }
        finally
        {
            _subagentNavigationBusy = false;
        }
    }

    /// <summary>
    /// 子会话导航的统一 await 后复核：未取消、页面激活、来源对象/Revision/来源代际未变且未撤销、
    /// 客户端实例未变、页面代际与会话代际未变、选中会话仍为捕获值，且父消息仍属于捕获的会话视图
    /// 当前渲染快照（按真实消息 id 查询，匿名/重复兜底 key 不参与）。
    /// </summary>
    private bool IsSubagentNavigationCurrent(
        OpenCodePageSource source, long revision, int sourceGeneration, OpenCodeClient client,
        int pageGeneration, int sessionGeneration, string selectedSessionId,
        OpenCodeConversationView sourceView, string parentMessageId, CancellationToken ct)
        => !ct.IsCancellationRequested
        && pageGeneration == _pageGeneration
        && string.Equals(_selectedSessionId, selectedSessionId, StringComparison.Ordinal)
        && sourceView.ContainsRenderedMessage(parentMessageId)
        && IsCurrentBinding(source, revision, sourceGeneration, client, sessionGeneration);
}
