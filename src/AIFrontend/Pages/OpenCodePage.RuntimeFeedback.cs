using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private string? _backgroundAttempt;
    private MessageHistoryRead? _backgroundBinding;
    private CancellationTokenSource? _toolDetailBindingCts;

    private void ResetToolDetailBinding()
    {
        _conversationView.ConfigureToolDetail = null;
        _toolDetailBindingCts?.Cancel();
        _toolDetailBindingCts?.Dispose();
        _toolDetailBindingCts = null;
    }
    private OpenCodeMessageRetry? _publishedRetry;
    private string? _publishedRetryMessageId;

    private void ResetRuntimeFeedback()
    {
        _backgroundAttempt = null;
        _backgroundBinding = null;
        ResetToolDetailBinding();
        _publishedRetry = null;
        _publishedRetryMessageId = null;
        _eventReducer?.ClearCurrentRetry();
        _conversationView.ClearCurrentRetry();
        _conversationView.SetBackgroundAction(null);
    }

    private void RefreshCurrentRetry()
    {
        // Only event-owned state participates. Never reserialize/merge the full history on this hot path.
        var current = _pageActive && SourceReady && !_sessionMissing && _messageHistoryWorkflowOwner is null
            && !_messageHistoryNeedsState && _eventReducer?.IsCurrentRetryVisible(_messageRevertBoundary) == true;
        var retry = current ? _eventReducer?.CurrentRetry : null;
        var retryId = current ? _eventReducer?.CurrentRetryMessageId : null;
        if (Equals(retry, _publishedRetry) && retryId == _publishedRetryMessageId) return;
        _publishedRetry = retry;
        _publishedRetryMessageId = retryId;
        _conversationView.SetCurrentRetry(retry);
    }

    partial void OnRuntimeMessagesAccepted(IReadOnlyList<OpenCodeMessage> messages)
    {
        if (!_pageActive || !SourceReady || _sessionMissing || _messageHistoryNeedsState
            || _messageHistoryWorkflowOwner is not null || _selectedSessionId is not { } sessionId || _client is not { } client)
        {
            if (_backgroundBinding is not null)
            {
                _backgroundBinding = null;
                ResetToolDetailBinding();
                _conversationView.SetBackgroundAction(null);
            }
            RefreshCurrentRetry();
            return;
        }
        // ResetRuntimeFeedback invalidates this cache on every existing ClearMessages/reset path.
        // New row controllers inherit the conversation field; deltas need no all-entry re-registration.
        if (_backgroundBinding is null || !IsCurrentMessageHistoryRead(_backgroundBinding))
        {
            var read = CaptureMessageHistoryRead(client, sessionId);
            _backgroundBinding = read;
            ResetToolDetailBinding();
            if (read.Source is { } detailSource)
            {
                _toolDetailBindingCts = CreateOperationToken(detailSource);
                var bindingToken = _toolDetailBindingCts.Token;
                _conversationView.ConfigureToolDetail = (options, parent, rowCanRead) =>
                    OpenCodeToolDetailBuiltins.BindLiveChildOutput(options, client,
                        child => !bindingToken.IsCancellationRequested && IsCurrentMessageHistoryRead(read)
                            && _conversationView.ContainsRenderedMessage(parent) && rowCanRead(child), bindingToken);
            }
            _conversationView.SetBackgroundAction((messageId, part) => _ = BackgroundForegroundToolsAsync(read, messageId, part));
        }
        RefreshCurrentRetry();
    }

    private bool IsCurrentBackgroundTarget(MessageHistoryRead read, string messageId, OpenCodeContentPart part)
    {
        if (!IsCurrentMessageHistoryRead(read) || _messageHistoryNeedsState || _isSending || HasPendingNativeAction()
            || part.Id is null) return false;
        var history = ProjectMessageHistory(MergeMessageHistory());
        var index = history.FindIndex(message => message.Id == messageId && message.Type == "assistant");
        if (index < 0 || history.Skip(index + 1).Any(message => message.Type is "user" or "assistant")) return false;
        var matches = history[index].Content?.Where(candidate => candidate.Id == part.Id).ToList();
        var current = matches?.Count == 1 ? matches[0] : null;
        if (current is null || JsonSerializer.Serialize(current, OpenCodeJson.Options) != JsonSerializer.Serialize(part, OpenCodeJson.Options)) return false;
        var keys = OpenCodeStepRunPresentation.AsyncKeys(current).ToHashSet(StringComparer.Ordinal);
        foreach (var message in history)
            if (OpenCodeStepRunPresentation.TryReadAsyncCompletion(message, out var completion) && keys.Contains(completion.Key))
                return OpenCodeToolRowPresentation.Build(current, completion).CanMoveToBackground;
        return OpenCodeToolRowPresentation.Build(current).CanMoveToBackground;
    }

    private async Task BackgroundForegroundToolsAsync(MessageHistoryRead read, string messageId, OpenCodeContentPart part)
    {
        if (read.Source is not { } source || !IsCurrentBackgroundTarget(read, messageId, part)) return;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            read.SessionId + ":" + read.Epoch + ":" + messageId + ":" + JsonSerializer.Serialize(part, OpenCodeJson.Options))));
        if (_backgroundAttempt == fingerprint) return; // Repeated click on the same unrefreshed invocation.
        using var cancellation = CreateOperationToken(source);
        var ct = cancellation.Token;
        var operationId = Guid.NewGuid();
        OpenCodeInteractionGate.Lease? lease = null;
        try
        {
            lease = await OpenCodeInteractionGate.Shared.EnterAsync(source.Identity, read.SessionId, ct);
            if (!IsCurrentBackgroundTarget(read, messageId, part) || _backgroundAttempt == fingerprint) return;
            if (!lease.CanControlSession())
            {
                ShowMessage(InfoBarSeverity.Warning, "转后台未提交", "当前会话存在未结算操作；请先核对已有保护状态。");
                return;
            }
            await read.Client.BackgroundGuardedAsync(read.SessionId, start =>
            {
                var started = false;
                source.TryExecute(ct, () =>
                {
                    if (!IsCurrentBackgroundTarget(read, messageId, part) || !lease.CanControlSession()) return;
                    lease.BeginSessionControl(operationId, SessionControlKind.Background, fingerprint);
                    _backgroundAttempt = fingerprint;
                    start();
                    started = true;
                });
                return started;
            }, ct);
            // Settle request admission only; never mutate the part/background/completion UI from 204.
            source.TryExecute(CancellationToken.None, () =>
            {
                if (IsCurrentMessageHistoryRead(read)) lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Succeeded);
            });
        }
        catch (OpenCodeSessionControlException ex)
        {
            if (ex.VerifiedPreEffect && lease is not null)
                source.TryExecute(CancellationToken.None, () =>
                {
                    if (!IsCurrentMessageHistoryRead(read) || lease.GetSessionControlProtection()?.OperationId != operationId) return;
                    lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Failed);
                    _backgroundAttempt = null;
                });
            if (IsCurrentMessageHistoryRead(read))
                ShowMessage(InfoBarSeverity.Warning, "转后台请求被拒绝", "服务端返回 HTTP " + ex.StatusCode + "；未自动重试。");
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException)
        {
            if (IsCurrentMessageHistoryRead(read))
                ShowMessage(InfoBarSeverity.Warning, "转后台结果未知", "未收到符合契约的确认；保留会话保护，不会自动重试。");
        }
        finally
        {
            lease?.Dispose();
            if (IsCurrentMessageHistoryRead(read))
            {
                UpdateComposerButtons();
                RefreshComposerView();
            }
        }
        if (IsCurrentMessageHistoryRead(read)) await RefreshMessagesAsync();
    }
}
