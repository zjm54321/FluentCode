using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

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
        => !_pendingDeliveryInFlight && CanWithdrawQueued(item) && _source is { } source
            && _selectedSessionId is { } session && item.Id is { } id
            && !_uncertainDelivery.ContainsKey((source, _sourceRevision, session, id));

    private void ReconcilePendingDeliveryUpdates(IReadOnlyList<OpenCodeInboxItem>? inbox)
    {
        if (inbox is null || _source is not { } source || _selectedSessionId is not { } session) return;
        foreach (var entry in _uncertainDelivery.Where(pair => ReferenceEquals(pair.Key.Source, source)
            && pair.Key.Revision == _sourceRevision && pair.Key.Session == session).ToArray())
        {
            var item = inbox.FirstOrDefault(row => row.Id == entry.Key.Id);
            if (item is null || item.Delivery == entry.Value) _uncertainDelivery.Remove(entry.Key);
        }
    }

    private async void OnPendingDeliveryChangeRequested(object? sender, OpenCodePendingDeliveryRequest request)
    {
        if (_pendingDeliveryInFlight || _queuedWithdrawInFlight || !_pageActive || !SourceReady
            || _source is not { IsRevoked: false } source || _client is not { } client
            || _selectedSessionId is not { } session || _queuedInboxSessionId != session
            || request.Delivery is not (OpenCodeDeliveryModes.Steer or OpenCodeDeliveryModes.Queue)) return;
        var item = _queuedInbox?.FirstOrDefault(row => row.Id == request.InboxId);
        if (item is null || !CanUpdatePendingDelivery(item) || item.Delivery == request.Delivery) return;
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
        var started = false;
        _pendingDeliveryInFlight = true;
        _queuedPromptsView.IsWithdrawBusy = true;
        _uncertainDelivery[key] = request.Delivery;
        try
        {
            Task? pending = null;
            source.TryExecute(token, () =>
            {
                if (!Current()) return;
                started = true;
                pending = client.UpdateInboxDeliveryAsync(session, request.InboxId, request.Delivery, token);
            });
            if (pending is null) return;
            await pending; // 204 is not admission/delivery/completion proof. Re-read actual inbox; no optimistic mode edit.
            if (!Current()) return;
            Task<IReadOnlyList<OpenCodeInboxItem>>? fresh = null;
            source.TryExecute(token, () => { if (Current()) fresh = client.GetInboxAsync(session, token); });
            if (fresh is null) return;
            var inbox = await fresh;
            if (!Current()) return;
            _queuedInboxSessionId = session;
            _queuedInbox = inbox;
        }
        catch (OpenCodeApiException error) when (error.StatusCode is 400 or 401 or 403 or 404 or 409)
        {
            _uncertainDelivery.Remove(key);
            if (Current()) ShowMessage(InfoBarSeverity.Warning, "未更改投递方式", "服务端未接受这次更改；消息仍以实际列表显示为准。");
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException)
        {
            if (Current()) ShowMessage(InfoBarSeverity.Warning, "投递方式尚未确认", "不会自动重试；等候服务端列表确认后才能再次更改这条消息。");
        }
        catch (Exception)
        {
            if (Current()) ShowMessage(InfoBarSeverity.Warning, "投递方式尚未确认", "未自动重试这次更改，请以服务端列表为准。");
        }
        finally
        {
            if (!started) _uncertainDelivery.Remove(key);
            _pendingDeliveryInFlight = false;
            _queuedPromptsView.IsWithdrawBusy = _queuedWithdrawInFlight;
            if (Current()) UpdateQueuedPrompts();
        }
    }
}
