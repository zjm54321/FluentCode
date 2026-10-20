using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    /// <summary>已完成验证的返回目标来源类型。</summary>
    private enum SessionReturnKind
    {
        /// <summary>已通过祖先验证的来源会话。</summary>
        VisitedOrigin,

        /// <summary>已通过父边验证的上级会话。</summary>
        VerifiedParent,
    }

    /// <summary>
    /// 会话返回目标。仅可由已完成祖先/父边验证的调用点创建，
    /// 不得由 sidebar 快照或通知文本推断。
    /// </summary>
    private sealed record SessionReturnTarget(string SessionId, SessionReturnKind Kind);

    /// <summary>
    /// 安装返回上下文时捕获的不可变绑定快照；用于后续复核返回目标是否仍然有效。
    /// 不保存正文、控件、ACK 水位或 gate 状态。
    /// </summary>
    private sealed record SessionReturnContext(
        OpenCodePageSource Source,
        long Revision,
        int SourceGeneration,
        int PageGeneration,
        OpenCodeClient Client,
        string ViewedSessionId,
        int SessionGeneration,
        string TargetSessionId,
        SessionReturnKind Kind);

    private SessionReturnContext? _sessionReturnContext;

    /// <summary>返回 GET 的占用标记；旧 finally 不得清除新操作建立的上下文。</summary>
    private SessionReturnContext? _sessionReturnAttempt;

    /// <summary>仅清除两个返回上下文字段；不触碰视图、ACK 水位或 gate 状态。</summary>
    private void ClearSessionReturnContext()
    {
        _sessionReturnContext = null;
        _sessionReturnAttempt = null;
        UpdateSessionReturnUi();
    }

    /// <summary>
    /// 在当前绑定就绪且目标合法时安装返回上下文。调用方必须在切换同步段建立新的
    /// session generation/CTS 之后、开始异步加载之前调用。不推断 parent，不读取
    /// sidebar，不 await，也不做任何重试或换模型。
    /// </summary>
    private void InstallSessionReturnContext(SessionReturnTarget? target)
    {
        ClearSessionReturnContext();

        if (target is null || !_pageActive || _sessionCts is null)
        {
            return;
        }

        var source = _source;
        var client = _client;
        var viewedSessionId = _selectedSessionId;
        if (source is null || client is null || viewedSessionId is null)
        {
            return;
        }

        if (!IsCurrentBinding(source, _sourceRevision, _sourceGeneration, client, _sessionGeneration))
        {
            return;
        }

        if (!OpenCodeSubagentPresentation.IsOpaqueSessionId(viewedSessionId)
            || !OpenCodeSubagentPresentation.IsOpaqueSessionId(target.SessionId)
            || string.Equals(viewedSessionId, target.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        _sessionReturnContext = new SessionReturnContext(
            source,
            _sourceRevision,
            _sourceGeneration,
            _pageGeneration,
            client,
            viewedSessionId,
            _sessionGeneration,
            target.SessionId,
            target.Kind);
        UpdateSessionReturnUi();
    }

    /// <summary>复核给定上下文是否为当前上下文实例，且其绑定与目标仍然有效。</summary>
    private bool IsSessionReturnContextCurrent(SessionReturnContext? context)
    {
        if (context is null || !ReferenceEquals(context, _sessionReturnContext))
        {
            return false;
        }

        if (context.PageGeneration != _pageGeneration)
        {
            return false;
        }

        if (!string.Equals(context.ViewedSessionId, _selectedSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OpenCodeSubagentPresentation.IsOpaqueSessionId(context.TargetSessionId)
            || string.Equals(context.ViewedSessionId, context.TargetSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        return IsCurrentBinding(
            context.Source,
            context.Revision,
            context.SourceGeneration,
            context.Client,
            context.SessionGeneration);
    }

    /// <summary>存在有效且未被返回 GET 占用的上下文时为 true。</summary>
    private bool CanReturnToSession =>
        IsSessionReturnContextCurrent(_sessionReturnContext)
        && !ReferenceEquals(_sessionReturnContext, _sessionReturnAttempt);

    /// <summary>返回入口的显示文案；无有效上下文时为空。</summary>
    private string SessionReturnLabel
    {
        get
        {
            var context = _sessionReturnContext;
            if (!IsSessionReturnContextCurrent(context))
            {
                return string.Empty;
            }

            return context!.Kind == SessionReturnKind.VisitedOrigin
                ? "返回来源会话"
                : "返回上级会话";
        }
    }

    private async Task ReturnToSessionAsync()
    {
        var context = _sessionReturnContext;
        if (context is null || !IsSessionReturnContextCurrent(context))
        {
            return;
        }

        if (ReferenceEquals(_sessionReturnAttempt, context))
        {
            return;
        }

        _sessionReturnAttempt = context;

        CancellationTokenSource? operationCts = null;
        try
        {
            operationCts = CreateOperationToken(context.Source);
            var ct = operationCts.Token;

            if (!IsSessionReturnContextCurrent(context)
                || !ReferenceEquals(_sessionReturnAttempt, context)
                || ct.IsCancellationRequested)
            {
                return;
            }

            var targetId = context.TargetSessionId;
            var client = context.Client;

            if (!TryStartRead(
                    context.Source,
                    context.Revision,
                    context.SourceGeneration,
                    client,
                    context.SessionGeneration,
                    ct,
                    () => client.GetSessionInfoAsync(targetId, ct),
                    out Task<OpenCodeSessionInfo>? readTask))
            {
                return;
            }

            if (readTask is null)
            {
                return;
            }

            OpenCodeSessionInfo? info;
            try
            {
                info = await readTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                info = null;
            }

            if (!IsSessionReturnContextCurrent(context)
                || !ReferenceEquals(_sessionReturnAttempt, context)
                || ct.IsCancellationRequested)
            {
                return;
            }

            if (info is null || !string.Equals(info.Id, targetId, StringComparison.Ordinal))
            {
                ShowMessage(
                    InfoBarSeverity.Warning,
                    SessionReturnLabel,
                    "返回目标不可用，已保留当前会话");
                return;
            }

            if (!IsSessionReturnContextCurrent(context)
                || !ReferenceEquals(_sessionReturnAttempt, context)
                || ct.IsCancellationRequested)
            {
                return;
            }

            SelectSessionItem(targetId);
            var nextTarget = CreateVerifiedParentTarget(info, targetId);

            // Switch 会取消旧 token 并消费旧 context；
            // await 后不再用旧 token 判失败或回滚。
            await SwitchToSessionAsync(targetId, nextTarget).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 取消静默，不因 Switch 正常取消旧 token 判失败。
        }
        catch (Exception)
        {
            if (IsSessionReturnContextCurrent(context)
                && ReferenceEquals(_sessionReturnAttempt, context))
            {
                ShowMessage(
                    InfoBarSeverity.Warning,
                    SessionReturnLabel,
                    "返回目标不可用，已保留当前会话");
            }
        }
        finally
        {
            operationCts?.Dispose();
            if (ReferenceEquals(_sessionReturnAttempt, context))
            {
                _sessionReturnAttempt = null;
            }
        }
    }

    /// <summary>
    /// 仅由当前绑定复核后的真实 GET/info 调用：childInfo.Id 必须精确等于
    /// expectedChildId，且 ParentId 是合法非自身会话 ID；否则返回 null。
    /// 不得使用旧 sidebar 数据或通知文本。
    /// </summary>
    private static SessionReturnTarget? CreateVerifiedParentTarget(
        OpenCodeSessionInfo childInfo,
        string expectedChildId)
    {
        if (childInfo is null || string.IsNullOrEmpty(expectedChildId))
        {
            return null;
        }

        if (!string.Equals(childInfo.Id, expectedChildId, StringComparison.Ordinal))
        {
            return null;
        }

        var parentId = childInfo.ParentId;
        if (string.IsNullOrEmpty(parentId)
            || !OpenCodeSubagentPresentation.IsOpaqueSessionId(parentId)
            || string.Equals(parentId, childInfo.Id, StringComparison.Ordinal))
        {
            return null;
        }

        return new SessionReturnTarget(parentId, SessionReturnKind.VerifiedParent);
    }
}
