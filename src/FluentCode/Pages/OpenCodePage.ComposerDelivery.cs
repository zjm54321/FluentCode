using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private bool _composerDeliverySupported;
    // Compatibility seam: default/keyboard submission is always direct, never the previous queue gesture.
    internal string? RequestedComposerDelivery => _composerDeliverySupported ? OpenCodeDeliveryModes.Steer : null;

    internal void ConfigureComposerDelivery(bool supported)
    {
        _composerDeliverySupported = supported;
    }

    private bool _pendingDeliveryInFlight;
    private readonly Dictionary<(OpenCodePageSource Source, long Revision, string Session, string Id), string> _uncertainDelivery = new();

    private void InitializePendingDeliveryActions()
        => _queuedPromptsView.DeliveryChangeRequested += OnPendingDeliveryChangeRequested;

    private bool CanUpdatePendingDelivery(OpenCodeInboxItem item)
        => _pageActive && SourceReady && item.Type == OpenCodeInboxItemTypes.User && _source is { } source
            && _selectedSessionId is { } session && item.Id is { } id
            && item.SessionId == session && OpenCodeQueuedPromptWithdrawal.IsWithdrawableId(session, id)
            && OwnsPendingDelivery(id) && !_conversationView.ContainsRenderedMessage(id)
            && !_uncertainDelivery.ContainsKey((source, _sourceRevision, session, id));

    private bool OwnsPendingDelivery(string inboxId)
        => _pendingAckIds.Contains(inboxId) || OwnsAdmittedInboxItem(inboxId);

    private void ReconcilePendingDeliveryUpdates(IReadOnlyList<OpenCodeInboxItem>? inbox)
    {
        if (inbox is null || _source is not { } source || _selectedSessionId is not { } session) return;
        foreach (var entry in _uncertainDelivery.Where(pair => ReferenceEquals(pair.Key.Source, source)
            && pair.Key.Revision == _sourceRevision && pair.Key.Session == session).ToArray())
        {
            var item = inbox.FirstOrDefault(row => row.Id == entry.Key.Id);
            if (item is null)
            {
                ForgetInboxReceipt(entry.Key.Id);
                _pendingAckIds.Remove(entry.Key.Id);
            }
            if (item is null || item.Delivery == entry.Value) _uncertainDelivery.Remove(entry.Key);
        }
    }

    private async Task<IReadOnlyList<OpenCodeInboxItem>?> ReadPendingDeliveryInboxAsync(
        OpenCodePageSource source, OpenCodeClient client, string session, Func<bool> current, CancellationToken token)
    {
        Task<IReadOnlyList<OpenCodeInboxItem>>? pending = null;
        source.TryExecute(token, () => { if (current()) pending = client.GetInboxAsync(session, token); });
        if (pending is null) return null;
        var inbox = await pending;
        if (!current()) return null;
        _queuedInboxSessionId = session;
        _queuedInbox = inbox;
        UpdateQueuedPrompts();
        return inbox;
    }

    private void ReportUnconfirmedPendingDelivery()
        => ShowMessage(InfoBarSeverity.Warning, "投递方式尚未确认",
            "服务端列表还未确认这次更改。不会自动重试；确认后才能再次更改这条消息。");

    private static string PendingDeliveryRefusalText(int status) => status switch
    {
        404 => "会话或待投递消息已不可用，请刷新会话核对。",
        409 => "服务端拒绝这次切换；消息可能已开始投递，请查看最新列表和对话记录。",
        401 or 403 => "服务端拒绝这次更改，请检查连接验证和访问权限。",
        _ => "服务端拒绝这次更改；请以最新待投递列表为准。",
    };

    private async void OnPendingDeliveryChangeRequested(object? sender, OpenCodePendingDeliveryRequest request)
    {
        if (!_pageActive) return; // A replaced page must not publish feedback into its replacement.
        if (_pendingDeliveryInFlight || _queuedWithdrawInFlight)
        {
            ShowMessage(InfoBarSeverity.Informational, "暂时无法更改", "正在处理待投递消息，请稍后再操作。");
            return;
        }
        if (!SourceReady || _source is not { IsRevoked: false } source || _client is not { } client
            || _selectedSessionId is not { } session)
        {
            ShowMessage(InfoBarSeverity.Warning, "无法更改投递方式", "连接或会话尚未就绪，请重新连接后再操作。");
            return;
        }
        if (request.Delivery is not (OpenCodeDeliveryModes.Steer or OpenCodeDeliveryModes.Queue)
            || !OpenCodeQueuedPromptWithdrawal.IsWithdrawableId(session, request.InboxId))
        {
            ShowMessage(InfoBarSeverity.Warning, "无法更改投递方式", "未识别这条待投递消息或投递方式；未做更改。");
            return;
        }
        if (!OwnsPendingDelivery(request.InboxId))
        {
            ShowMessage(InfoBarSeverity.Informational, "无法更改投递方式", "这条消息不是本客户端发送，未做更改。");
            return;
        }
        if (_conversationView.ContainsRenderedMessage(request.InboxId))
        {
            ShowMessage(InfoBarSeverity.Informational, "无法更改投递方式", "这条消息已经发送，无法再更改投递方式。");
            return;
        }
        var revision = _sourceRevision;
        var sourceGeneration = _sourceGeneration;
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        using var operation = CreateOperationToken(source);
        var token = operation.Token;
        bool Current() => !token.IsCancellationRequested && _pageActive && !source.IsRevoked
            && ReferenceEquals(source, _source) && ReferenceEquals(client, _client) && revision == _sourceRevision
            && sourceGeneration == _sourceGeneration && pageGeneration == _pageGeneration
            && sessionGeneration == _sessionGeneration && session == _selectedSessionId;
        var key = (source, revision, session, request.InboxId);
        if (_uncertainDelivery.ContainsKey(key))
        {
            ReportUnconfirmedPendingDelivery();
            return;
        }
        var started = false;
        OpenCodeApiException? refusal = null;
        _pendingDeliveryInFlight = true;
        _queuedPromptsView.IsWithdrawBusy = true;
        try
        {
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(source.Identity, session, token);
            if (!Current()) return;
            if (!lease.CanControlSession())
            {
                ShowMessage(InfoBarSeverity.Warning, "暂时无法更改", "会话中还有未确认的操作，请先核对操作结果。");
                return;
            }
            // A receipt proves local ownership, not that the item is still pending. Always preflight the server.
            var before = await ReadPendingDeliveryInboxAsync(source, client, session, Current, token);
            if (before is null || !Current()) return;
            var item = before.FirstOrDefault(row => row.Id == request.InboxId && row.SessionId == session);
            if (item is null)
            {
                ForgetInboxReceipt(request.InboxId);
                _pendingAckIds.Remove(request.InboxId);
                ShowMessage(InfoBarSeverity.Informational, "无法更改投递方式",
                    "这条消息已不在待投递列表中，可能已发送；请查看对话记录。");
                return;
            }
            if (item.Type != OpenCodeInboxItemTypes.User)
            {
                ShowMessage(InfoBarSeverity.Informational, "无法更改投递方式", "这一条不是用户输入，未做更改。");
                return;
            }
            if (item.Delivery == request.Delivery)
            {
                ShowMessage(InfoBarSeverity.Informational, "投递方式未变", "这条消息已是“" + OpenCodeQueuedPromptsView.DescribeDelivery(item.Delivery) + "”。");
                return;
            }
            Task? pending = null;
            source.TryExecute(token, () =>
            {
                if (!Current()) return;
                started = true;
                _uncertainDelivery[key] = request.Delivery;
                pending = client.UpdateInboxDeliveryAsync(session, request.InboxId, request.Delivery, token);
            });
            if (pending is null) return;
            try
            {
                await pending; // 204 is not admission/delivery/completion proof. Never optimistically edit the mode.
            }
            catch (OpenCodeApiException error) when (error.StatusCode is 400 or 401 or 403 or 404 or 409)
            {
                refusal = error;
                _uncertainDelivery.Remove(key);
            }
            catch (OpenCodeException)
            {
                // Ambiguous failure: one read may confirm the actual mode; never repeat the PATCH.
            }
            if (!Current()) return;
            var inbox = await ReadPendingDeliveryInboxAsync(source, client, session, Current, token);
            if (inbox is null || !Current()) return;
            var actual = inbox.FirstOrDefault(row => row.Id == request.InboxId && row.SessionId == session);
            if (actual is null)
            {
                ForgetInboxReceipt(request.InboxId);
                _pendingAckIds.Remove(request.InboxId);
            }
            if (refusal is not null)
                ShowMessage(InfoBarSeverity.Warning, "未更改投递方式", PendingDeliveryRefusalText(refusal.StatusCode));
            else if (actual is null)
                ShowMessage(InfoBarSeverity.Informational, "消息已不在待投递列表",
                    "消息可能已发送，已无法切换投递方式；请查看对话记录。");
            else if (actual.Delivery != request.Delivery) ReportUnconfirmedPendingDelivery();
        }
        catch (Exception error) when (error is OpenCodeException or OperationCanceledException)
        {
            ReportReadFailure();
        }
        catch (Exception)
        {
            ReportReadFailure();
        }
        finally
        {
            if (!started) _uncertainDelivery.Remove(key);
            _pendingDeliveryInFlight = false;
            _queuedPromptsView.IsWithdrawBusy = _queuedWithdrawInFlight;
            if (Current())
            {
                UpdateQueuedPrompts();
                UpdateWaitingText();
                UpdateComposerButtons();
            }
        }

        void ReportReadFailure()
        {
            if (!Current()) return;
            if (refusal is not null)
                ShowMessage(InfoBarSeverity.Warning, "未更改投递方式", PendingDeliveryRefusalText(refusal.StatusCode) + " 待投递列表未能刷新。");
            else if (started) ReportUnconfirmedPendingDelivery();
            else ShowMessage(InfoBarSeverity.Warning, "无法读取待投递列表", "请刷新后再操作；未发送更改请求。");
        }
    }
}
