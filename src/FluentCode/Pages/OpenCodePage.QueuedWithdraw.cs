using System;
using System.Collections.Generic;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FluentCode.Pages;

/// <summary>
/// 排队消息“撤回”（重设计 v3 §9.1 跟进）：一次 DELETE + 新鲜 inbox/消息复查（见 <see cref="OpenCodeQueuedPromptWithdrawal"/>）。
/// 只有确认未投递才把文字放回输入框，且仅当输入框为空；否则提供“复制内容”。绝不自动重发。
/// </summary>
public sealed partial class OpenCodePage
{
    private bool _queuedWithdrawInFlight;

    private void InitializeQueuedWithdraw()
        => _queuedPromptsView.WithdrawRequested += QueuedPromptsView_WithdrawRequested;

    /// <summary>只对本页发出、仍在跟踪的用户输入显示“撤回”（不撤回其他客户端的排队工作）。</summary>
    private bool CanWithdrawQueued(OpenCodeInboxItem item)
        => item.Type == OpenCodeInboxItemTypes.User && !_queuedWithdrawInFlight && item.Id is { } id && _pendingAckIds.Contains(id)
        && _selectedSessionId is { } sessionId && OpenCodeQueuedPromptWithdrawal.IsWithdrawableId(sessionId, id);

    private async void QueuedPromptsView_WithdrawRequested(object? sender, string inboxId)
    {
        if (_queuedWithdrawInFlight || !_pageActive || _source is not { IsRevoked: false } source
            || _client is not { } client || _selectedSessionId is not { } sessionId
            || !string.Equals(_queuedInboxSessionId, sessionId, StringComparison.Ordinal) || !_pendingAckIds.Contains(inboxId))
        {
            return;
        }

        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        bool StillSame() => _pageActive && !source.IsRevoked && ReferenceEquals(_source, source) && _sourceRevision == revision
            && _sourceGeneration == sourceGeneration
            && ReferenceEquals(_client, client) && _pageGeneration == pageGeneration && _sessionGeneration == sessionGeneration
            && string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal);

        _queuedWithdrawInFlight = true;
        _queuedPromptsView.IsWithdrawBusy = true;
        var feedback = _queuedPromptsView.WithdrawButton(inboxId);
        if (feedback is not null) Motion.ActionFeedback.Busy(feedback);
        OpenCodeQueuedWithdrawResult result;
        try
        {
            using var operation = CreateOperationToken(source);
            result = await OpenCodeQueuedPromptWithdrawal.WithdrawAsync(client, sessionId, inboxId, source, StillSame, operation.Token);
        }
        catch (OperationCanceledException)
        {
            // 页面/会话已切换：DELETE 可能已发出，结果未知；不恢复、不重试。
            result = new(OpenCodeQueuedWithdrawOutcome.Uncertain, null, false, null);
        }
        finally
        {
            _queuedWithdrawInFlight = false;
            _queuedPromptsView.IsWithdrawBusy = false;
        }

        var same = StillSame();
        if (!same)
        {
            if (feedback is not null) Motion.ActionFeedback.Reset(feedback);
            return; // Never publish old-source/session results into a replacement page.
        }
        if (result.Outcome == OpenCodeQueuedWithdrawOutcome.Withdrawn)
        {
            if (feedback is not null)
            {
                Motion.ActionFeedback.Success(feedback, "消息已撤回，未发送");
                if (feedback.IsLoaded) await System.Threading.Tasks.Task.Delay(Motion.FluentMotion.Duration(Motion.FluentMotion.Fast));
                if (!StillSame()) return;
            }
            else Motion.ActionFeedback.Announce(_queuedPromptsView, "消息已撤回，未发送");
        }
        else if (feedback is not null) Motion.ActionFeedback.Failure(feedback, "撤回未确认", "请查看提示并核对对话记录");
        if (same && result.FreshInbox is { } fresh)
        {
            // 复用新鲜 inbox：已不在的条目不再跟踪（与轮询的“已不在 = 不再待投递”一致）。
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in fresh)
            {
                if (item?.Id is { Length: > 0 } id) present.Add(id);
            }

            _queuedInboxSessionId = sessionId;
            _queuedInbox = fresh;
            _pendingAckIds.RemoveAll(id => !present.Contains(id));
            UpdateQueuedPrompts();
            UpdateWaitingText();
            UpdateComposerButtons();
        }

        switch (result.Outcome)
        {
            case OpenCodeQueuedWithdrawOutcome.Withdrawn:
                PublishWithdrawnText(result.Text ?? string.Empty, result.HadAttachments, same);
                break;
            case OpenCodeQueuedWithdrawOutcome.StillQueued:
                ShowMessage(InfoBarSeverity.Warning, "撤回未生效", "这条消息仍在排队，稍后会按原方式发送。");
                break;
            case OpenCodeQueuedWithdrawOutcome.NotWithdrawable:
                ShowMessage(InfoBarSeverity.Informational, "无法撤回", "这一条不是可撤回的文字消息；未做任何更改。");
                break;
            default:
                // Delivered 与 Uncertain 对用户同一结论：不恢复、不重发。
                ShowMessage(InfoBarSeverity.Warning, "可能已发送，未恢复到输入框",
                    "无法确认这条消息已被撤回（它可能刚好已经发送）。为避免重复，未放回输入框，也不会重新发送；请查看对话记录。");
                break;
        }
    }

    private void PublishWithdrawnText(string text, bool hadAttachments, bool samePage)
    {
        var attachmentNote = hadAttachments ? "原消息中的附件未恢复，如需请重新添加。" : string.Empty;
        if (samePage && TryRestoreWithdrawnText(text))
        {
            if (hadAttachments) ShowMessage(InfoBarSeverity.Warning, "附件需要重新添加", attachmentNote);
            return;
        }

        // 输入框已有内容（或已切换会话/正在输入法组合）：不覆盖，提供复制。
        ShowMessage(InfoBarSeverity.Warning, "撤回内容尚未恢复",
            (samePage ? "输入框中已有内容，未覆盖；" : "已切换会话，未放回输入框；")
            + "可复制撤回的内容。" + attachmentNote);
        var copy = new Button { Content = "复制内容" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(copy, "OpenCodeQueuedWithdrawCopy");
        copy.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                Motion.ActionFeedback.Success(copy, "已复制撤回的内容");
            }
            catch (Exception)
            {
                Motion.ActionFeedback.Failure(copy, "复制失败", "请稍后重试");
            }
        };
        MessageBar.ActionButton = copy;
    }

    /// <summary>仅当输入框为空（无文字、命令、附件）、未在输入法组合且绑定当前会话时写入；否则返回 false。</summary>
    private bool TryRestoreWithdrawnText(string text)
    {
        if (_composerController is not { } controller || _composerView is not { } view || _isComposing || _isSending
            || ComposerCoveredByOverlay || text.Length == 0)
        {
            return false;
        }

        var snapshot = controller.Snapshot;
        if (!snapshot.IsActive || snapshot.HasDraft || snapshot.Attachments.Count > 0 || snapshot.SelectedCommand is not null
            || snapshot.IsBusy || !view.PrepareClientTextApplication())
        {
            return false;
        }

        snapshot = controller.Snapshot; // Prepare 可能刚刷新了编辑器输入；以最新快照判定。
        if (snapshot.HasDraft || !string.IsNullOrEmpty(snapshot.Text))
        {
            return false;
        }

        controller.SetText(snapshot.Token, text);
        if (controller.Snapshot.Text != text)
        {
            return false;
        }

        view.RequestRender();
        return true;
    }
}
