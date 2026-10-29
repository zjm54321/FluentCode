using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Metadata for unknown operations must survive navigation, page replacement and source reconnect.
    private static readonly OpenCodePluginMutationCoordinator PluginMutations = new();
    private readonly OpenCodePluginActionsView _pluginActionsView = new(showPresetSummary: false);
    private readonly Guid _pluginActionsViewId = Guid.NewGuid();
    private long _pluginActionsGeneration;
    private sealed record PluginActionOwner(OpenCodeInsightsBinding Binding, PluginMutationOrigin Origin, OpenCodePluginActionContext Context);
    private PluginActionOwner? _pluginActionOwner, _pluginPendingOwner, _pluginProtectionOwner, _pluginProtectionCheckedOwner;
    private PluginMutationProtection? _pluginProtection;
    private PluginMutationResult? _pluginActionResult;
    private string? _pluginActionNotice;
    private bool _pluginActionRefreshFailed;

    private void InitializePluginActions()
    {
        RightPanelHeaderSlot.Children.Add(_pluginActionsView);
        _pluginActionsView.PresetApplyRequested += request => _ = RunPluginMutationAsync(request.Context, request.ExactName);
        _pluginActionsView.ClearEncryptedRequested += context => _ = RunPluginMutationAsync(context, null);
        _pluginActionsView.ProtectionAcknowledgementRequested += request => _ = AcknowledgePluginProtectionAsync(request);
    }

    private void RefreshPluginActions(OpenCodeInsightsBinding? binding, string? directory,
        OpenCodeInsightSection<OpenCodeSessionPresetState>? preset)
    {
        if (binding is null || string.IsNullOrWhiteSpace(directory) || !IsCurrentInsightsBinding(binding)
            || !IsSameMessageBinding(binding.SessionId, directory))
        {
            Volatile.Write(ref _pluginActionOwner, null); _pluginActionsView.Render(null); return;
        }
        var owner = _pluginActionOwner;
        if (owner?.Binding != binding || owner.Origin.Directory != directory)
        {
            var generation = ++_pluginActionsGeneration;
            owner = new(binding, new(binding.Source, binding.Revision, binding.SessionId, directory, generation),
                new(_pluginActionsViewId, generation, binding.SessionId, directory));
            Volatile.Write(ref _pluginActionOwner, owner);
            _pluginProtectionOwner = _pluginProtectionCheckedOwner = null; _pluginProtection = null; _pluginActionResult = null;
            _pluginActionNotice = null; _pluginActionRefreshFailed = false;
        }
        var pending = ReferenceEquals(_pluginPendingOwner, owner);
        var inspected = ReferenceEquals(_pluginProtectionCheckedOwner, owner);
        var unknown = _pluginProtection is { } protection ? new OpenCodePluginUnknownProtection(protection.OperationId,
            protection.Kind == SessionControlKind.ApplySessionPreset ? OpenCodePluginActionKind.ApplyPreset : OpenCodePluginActionKind.ClearEncrypted) : null;
        var ready = inspected && CanStartPluginMutation(owner.Origin) && !pending;
        var counts = _pluginActionResult is { Status: PluginMutationStatus.Accepted, Clear: { } clear }
            ? new OpenCodeClearEncryptedCounts(clear.ChangedMessages, clear.ChangedEvents, clear.RemovedFields) : null;
        _pluginActionsView.Render(new(owner.Context, preset is null ? "会话预设尚未读取" : OpenCodeSessionInsightsController.SessionPresetLabel(preset),
            ready && preset?.State == OpenCodeInsightState.Loaded, ready, pending, _pluginActionNotice, counts, unknown));
        if (!inspected && !ReferenceEquals(_pluginProtectionOwner, owner)) _ = InspectPluginProtectionAsync(owner);
    }

    // Called by the coordinator under Source.TryExecute, possibly off the UI thread.
    // Only logical fields and immutable binding records: no XAML, Dispatcher, App or configuration access.
    private bool IsPluginOriginCurrent(PluginMutationOrigin origin)
    {
        var owner = Volatile.Read(ref _pluginActionOwner);
        var messages = _messageBinding;
        return owner is not null && owner.Origin == origin && !origin.Source.IsRevoked && IsCurrentInsightsBinding(owner.Binding)
            && !_homeActive && !_newSessionDraftActive && !_sessionMissing
            && messages is not null && messages.Directory == origin.Directory && messages.SessionId == origin.SessionId
            && ReferenceEquals(messages.Source, origin.Source) && messages.Revision == origin.Revision;
    }
    private bool CanStartPluginMutation(PluginMutationOrigin origin) => IsPluginOriginCurrent(origin) && !_isSending && !_isComposing
        && _nativeActionOwner is null && !_nativeDialogPending && !_newSessionCreatePending && !_draftRecoveryPending && !_draftSlotChanging;

    private async Task InspectPluginProtectionAsync(PluginActionOwner owner)
    {
        _pluginProtectionOwner = owner; // Deduplicate even synchronous render callbacks while waiting for the shared gate.
        var protection = await PluginMutations.GetProtectionAsync(owner.Origin, IsPluginOriginCurrent);
        await PublishPluginUiAsync(() =>
        {
            if (!IsPluginOriginCurrent(owner.Origin)) return;
            _pluginProtectionCheckedOwner = owner;
            _pluginProtection = protection;
            RefreshRightPanelMetadata();
        });
    }

    private async Task RunPluginMutationAsync(OpenCodePluginActionContext context, string? exactName)
    {
        var owner = _pluginActionOwner;
        if (owner is null || owner.Context != context || !IsPluginOriginCurrent(owner.Origin)
            || ReferenceEquals(_pluginPendingOwner, owner) || _pluginProtection is not null) return;
        _pluginPendingOwner = owner; _pluginActionResult = null; _pluginActionNotice = null; _pluginActionRefreshFailed = false;
        RefreshRightPanelMetadata(); UpdateComposerButtons();
        try
        {
            // Navigation is checked before dispatch but does NOT cancel a dispatched operation's settlement.
            var result = exactName is null
                ? await PluginMutations.ClearEncryptedAsync(owner.Origin, CanStartPluginMutation)
                : await PluginMutations.ApplyPresetAsync(owner.Origin, exactName, CanStartPluginMutation);
            var refresh = false;
            PluginActionOwner? projectionOwner = null;
            await PublishPluginUiAsync(() =>
            {
                if (result.Status == PluginMutationStatus.Accepted && result.Clear is not null)
                {
                    EvictPluginClearedWindows(owner.Origin); // Data invalidation survives view retirement/ABA.
                    if (_pluginActionOwner is { } current && IsPluginOriginCurrent(current.Origin)
                        && current.Origin.Source.Identity == owner.Origin.Source.Identity
                        && current.Origin.SessionId == owner.Origin.SessionId && current.Origin.Directory == owner.Origin.Directory)
                    { projectionOwner = current; refresh = true; }
                }
                if (!IsPluginOriginCurrent(owner.Origin)) return;
                _pluginActionResult = result;
                _pluginActionNotice = OpenCodePluginMutationPresentation.Describe(result, exactName is not null);
                _pluginProtection = result.ProtectionRetained ? new(result.OperationId,
                    exactName is null ? SessionControlKind.ClearEncryptedContents : SessionControlKind.ApplySessionPreset, result) : null;
                if (result.Status == PluginMutationStatus.NotStarted && result.FailureCode == "session-protected")
                    _pluginProtectionOwner = _pluginProtectionCheckedOwner = null;
                refresh = result.Status == PluginMutationStatus.Accepted;
            });
            if (refresh)
            {
                var readOwner = projectionOwner ?? owner;
                try
                {
                    OpenCodeConversationView.ReadingPosition? reading = null;
                    await PublishPluginUiAsync(() =>
                    {
                        if (result.Clear is not null && IsPluginOriginCurrent(readOwner.Origin))
                        {
                            reading = _conversationView.CaptureReadingPosition();
                            InvalidatePluginClearedProjection(readOwner.Origin);
                        }
                    });
                    await InvokePluginUiAsync(async () =>
                    {
                        if (!IsPluginOriginCurrent(readOwner.Origin)) return;
                        if (result.Clear is not null)
                        {
                            await RefreshMessagesAsync(explicitRefresh: true);
                            if (IsPluginOriginCurrent(readOwner.Origin))
                            {
                                if (reading is not null && _messageWindowAccepted)
                                    _conversationView.RestoreReadingPosition(reading, () => IsPluginOriginCurrent(readOwner.Origin));
                                if (ReferenceEquals(readOwner, owner)) _pluginActionRefreshFailed |= _messagesLoadError is not null || !_messageWindowAccepted;
                                await RefreshListedSessionStatusesAsync();
                            }
                        }
                        else
                        {
                            await ReadRightPanelPresetAsync(owner.Binding, owner.Origin.Directory, refresh: true);
                            if (IsPluginOriginCurrent(owner.Origin)) _pluginActionRefreshFailed = _insights?.SessionPresetFor(owner.Binding, owner.Origin.Directory).State != OpenCodeInsightState.Loaded;
                        }
                    });
                }
                catch (Exception) { await PublishPluginUiAsync(() => { if (ReferenceEquals(readOwner, owner) && IsPluginOriginCurrent(owner.Origin)) _pluginActionRefreshFailed = true; }); }
            }
        }
        finally
        {
            await PublishPluginUiAsync(() =>
            {
                if (ReferenceEquals(_pluginPendingOwner, owner)) _pluginPendingOwner = null;
                if (IsPluginOriginCurrent(owner.Origin))
                {
                    if (_pluginActionRefreshFailed) _pluginActionNotice += " 操作已被接纳，但显示状态刷新未完成。";
                    RefreshRightPanelMetadata(); UpdateComposerButtons(); QueueSessionControlCardRefresh();
                }
            });
        }
    }

    private async Task AcknowledgePluginProtectionAsync(OpenCodePluginProtectionAcknowledgement request)
    {
        var owner = _pluginActionOwner;
        if (owner is null || owner.Context != request.Context || !IsPluginOriginCurrent(owner.Origin)
            || _pluginProtection?.OperationId != request.OperationId || ReferenceEquals(_pluginPendingOwner, owner)) return;
        _pluginPendingOwner = owner; RefreshRightPanelMetadata();
        try
        {
            var acknowledged = await PluginMutations.AcknowledgeUnknownAsync(owner.Origin, request.OperationId, IsPluginOriginCurrent);
            await PublishPluginUiAsync(() =>
            {
                if (!IsPluginOriginCurrent(owner.Origin) || _pluginProtection?.OperationId != request.OperationId) return;
                if (acknowledged is { Status: PluginMutationStatus.OutcomeUnknown, ProtectionRetained: false })
                {
                    _pluginProtection = null; _pluginActionResult = acknowledged;
                    _pluginActionNotice = "已解除这次操作的本地保护；先前服务器结果仍未知。没有重试或撤销。";
                }
                else
                {
                    _pluginActionNotice = "本地保护未解除：状态或操作已变化，请重新检查。";
                    _pluginProtectionOwner = _pluginProtectionCheckedOwner = null;
                }
            });
        }
        finally
        {
            await PublishPluginUiAsync(() =>
            {
                if (ReferenceEquals(_pluginPendingOwner, owner)) _pluginPendingOwner = null;
                if (IsPluginOriginCurrent(owner.Origin)) { RefreshRightPanelMetadata(); UpdateComposerButtons(); QueueSessionControlCardRefresh(); }
            });
        }
    }

    private void EvictPluginClearedWindows(PluginMutationOrigin origin)
        => _retainedMessageWindows.RemoveAll(window => window.Binding.Source.Identity == origin.Source.Identity
            && window.Binding.SessionId == origin.SessionId && window.Binding.Directory == origin.Directory);

    private void InvalidatePluginClearedProjection(PluginMutationOrigin origin)
    {
        if (!IsPluginOriginCurrent(origin)) return;
        EvictPluginClearedWindows(origin);
        // Retire pre-clear buffered message/status events, retaining browser and other sessions' events.
        var retained = new List<(CancellationTokenSource Owner, OpenCodeEvent Event)>();
        while (_eventQueue.TryDequeue(out var item))
            if (item.Event.SessionId != origin.SessionId || !(item.Event.Type.StartsWith("message.", StringComparison.Ordinal)
                || item.Event.Type.StartsWith("session.", StringComparison.Ordinal))) retained.Add(item);
        foreach (var item in retained) _eventQueue.Enqueue(item);
        ResetAcceptedHistoryProjection(_messageRevertBoundary, boundaryKnown: false);
        _statusRefreshEpoch++;
        _liveSessionStatuses.Remove(origin.SessionId); _liveStatusVersion++;
        _workingSessions.Remove(origin.SessionId);
        if (_statusRefreshRunning) _statusRefreshAgain = true;
        RefreshSessionStateUi(); RefreshComposerSessionActivity(); RefreshConversationSessionActivity();
    }

    private Task PublishPluginUiAsync(Action publish)
    {
        if (DispatcherQueue.HasThreadAccess) { publish(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try { publish(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }

    private Task InvokePluginUiAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetCanceled();
        return completion.Task;
    }
}
