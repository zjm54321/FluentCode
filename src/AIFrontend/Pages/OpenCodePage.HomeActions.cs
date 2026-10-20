using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private sealed record HomeActionOwner(SessionControlOrigin Origin, OpenCodeClient Client, string Runtime, int SourceGeneration, int PageGeneration, OpenCodeHomeProjectKey Project, string? Title, Guid OperationId)
    {
        public string SessionId => Origin.SessionId;
    }
    private volatile HomeActionOwner? _homeActionOwner;
    private sealed record HomeActionRoot(string Directory, string? Title);
    private volatile ImmutableDictionary<string, HomeActionRoot> _homeActionRoots = ImmutableDictionary<string, HomeActionRoot>.Empty;

    private void CaptureHomeActionRoots()
    {
        var roots = ImmutableDictionary.CreateBuilder<string, HomeActionRoot>(StringComparer.Ordinal);
        foreach (var pair in _sessionItems)
            if (HomeSessionMetadata.TryGetValue(pair.Value, out var info) && info.Id == pair.Key && info.ParentId is null
                && info.Location?.Directory is { } directory) roots[pair.Key] = new(directory, info.Title);
        _homeActionRoots = roots.ToImmutable();
    }

    private bool CanRequestHomeSessionAction(long revision, OpenCodeHomeSession session, OpenCodeHomeSessionAction action)
        => HomeSessionActionTargetCurrent(revision, session.Project, session.Info.Id)
        && (action == OpenCodeHomeSessionAction.CopyId || action is OpenCodeHomeSessionAction.Delete or OpenCodeHomeSessionAction.Rename
            && _homeActionOwner is null && _homeDialog is null && !HasPendingNativeAction());

    // Used under Source.TryExecute/on coordinator workers: immutable identity and page fields only.
    // Does not depend on native selection, a composer ticket, or cosmetic Home projection revisions.
    private bool OwnsHomeAction(HomeActionOwner owner, SessionControlOrigin origin)
        => ReferenceEquals(_homeActionOwner, owner) && ReferenceEquals(owner.Origin, origin)
        && _pageActive && ReferenceEquals(_source, origin.Source) && ReferenceEquals(_client, owner.Client)
        && _sourceRevision == origin.Revision && _sourceGeneration == owner.SourceGeneration
        && _pageGeneration == owner.PageGeneration && _homeRuntimeSource == owner.Runtime;

    // UI-thread check against the latest accepted actual root metadata after the dialog await.
    private bool HomeActionRootCurrent(HomeActionOwner owner)
        => OwnsHomeAction(owner, owner.Origin) && HomeSessionActionTargetCurrent(_homeRevision, owner.Project, owner.SessionId)
        && _sessionItems.TryGetValue(owner.SessionId, out var item) && HomeSessionMetadata.TryGetValue(item, out var info)
        && info.Title == owner.Title;

    private bool CanStartHomeAction(HomeActionOwner owner, SessionControlOrigin origin)
        => OwnsHomeAction(owner, origin) && _homeActive && !(_selectedSessionId == owner.SessionId && _isSending)
        && _homeActionRoots.TryGetValue(owner.SessionId, out var root) && root.Directory == owner.Project.Directory && root.Title == owner.Title;

    private async Task RunHomeSessionActionAsync(long revision, OpenCodeHomeSession session, OpenCodeHomeSessionAction action)
    {
        if (action == OpenCodeHomeSessionAction.CopyId)
        {
            CopyHomeSessionId(revision, session.Project, session.Info.Id, action);
            return;
        }
        if (!CanRequestHomeSessionAction(revision, session, action) || session.Info.Id is not { } id
            || _source is not { } source || _client is not { } client || _homeCancellation is null) return;
        // Capture accepted metadata, not an old flyout DTO or hidden SessionList.SelectedItem.
        if (!_sessionItems.TryGetValue(id, out var item) || !HomeSessionMetadata.TryGetValue(item, out var root)) return;
        var origin = new SessionControlOrigin(source, _sourceRevision, id, 0)
        {
            LocalSubmissionInProgress = _selectedSessionId == id && _isSending,
            PreviouslyKnownUnresolvedChildSessionIds = _selectedSessionId == id ? _historyUnresolvedChildren : ImmutableArray<string>.Empty,
        };
        var owner = new HomeActionOwner(origin, client, _homeRuntimeSource, _sourceGeneration, _pageGeneration,
            session.Project, root.Title, Guid.NewGuid());
        _homeActionOwner = owner;
        var ct = _homeCancellation.Token;
        try
        {
            var title = await ConfirmHomeSessionActionAsync(owner, action);
            if (title is null || !HomeActionRootCurrent(owner)) return;
            var kind = action == OpenCodeHomeSessionAction.Delete ? SessionControlKind.DeleteRoot : SessionControlKind.RenameRoot;
            var request = new SessionControlRequest(kind, Directory: owner.Project.Directory, ExpectedTitle: owner.Title,
                NewTitle: kind == SessionControlKind.RenameRoot ? title : null);
            // Root management neither reads nor restores the composer draft. This is the exact action
            // capability fingerprint, not a fabricated selected-session/composer ticket.
            var draft = new SessionControlDraft(SessionControlJson.Hash(new { owner.OperationId, id, request }), true);
            bool CanStart(SessionControlOrigin o) => CanStartHomeAction(owner, o);
            var preview = await _sessionControls.PreviewAsync(origin, request, draft, CanStart, ct);
            if (!HomeActionRootCurrent(owner)) return;
            if (preview.Preview is not { } ready)
            {
                SetHomeActionStatus(owner, preview.Status);
                return;
            }
            var result = await _sessionControls.ConfirmAsync(ready, owner.OperationId, draft, CanStart,
                o => OwnsHomeAction(owner, o), ct);
            if (!OwnsHomeAction(owner, origin) || source.IsRevoked) return;
            if (result.AcceptanceKnown && kind == SessionControlKind.DeleteRoot) InvalidateDeletedHomeSession(owner);
            SetHomeActionStatus(owner, result.Status);
            if (result.AcceptanceKnown || result.MutationStarted && result.Status == SessionControlStatus.OutcomeUnknown)
                await RefreshHomeActionMetadataAsync(owner, ct); // Read-only; never replay or release unknown protection.
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (OwnsHomeAction(owner, origin) && !source.IsRevoked)
            {
                _homeMessage = "操作未完成；未自动重试，草稿与已有保护保持不变。请刷新会话列表。";
                QueueHomeProjection();
            }
        }
        finally
        {
            if (ReferenceEquals(_homeActionOwner, owner))
            {
                _homeActionOwner = null;
                QueueHomeProjection();
                UpdateComposerButtons();
                RefreshComposerView();
            }
        }
    }

    private async Task<string?> ConfirmHomeSessionActionAsync(HomeActionOwner owner, OpenCodeHomeSessionAction action)
    {
        if (XamlRoot is null || _homeDialog is not null || !HomeActionRootCurrent(owner)) return null;
        var rename = action == OpenCodeHomeSessionAction.Rename;
        var input = new TextBox { Text = owner.Title ?? "", AcceptsReturn = false };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = (owner.Title ?? "（未命名会话）") + Environment.NewLine + owner.SessionId,
            TextWrapping = TextWrapping.Wrap });
        if (rename) body.Children.Add(input);
        else body.Children.Add(new TextBlock { Text = "这将永久删除此会话及其所有子会话，无法撤销。", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = rename ? "修改会话名称" : "删除此会话及子会话？",
            Content = body, PrimaryButtonText = rename ? "保存名称" : "永久删除", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None, Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
            IsPrimaryButtonEnabled = !rename || !string.IsNullOrWhiteSpace(input.Text) };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        _homeDialog = dialog;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary && HomeActionRootCurrent(owner)
                ? rename ? input.Text : owner.SessionId : null;
        }
        finally { if (ReferenceEquals(_homeDialog, dialog)) _homeDialog = null; }
    }

    private void SetHomeActionStatus(HomeActionOwner owner, SessionControlStatus status)
    {
        if (!OwnsHomeAction(owner, owner.Origin) || owner.Origin.Source.IsRevoked) return;
        _homeMessage = status switch
        {
            SessionControlStatus.Accepted => "服务器已接纳操作；正在重新读取会话列表。",
            SessionControlStatus.OutcomeUnknown => "操作结果未知，保护已保留；不会自动重试。仅刷新会话元数据，请先核对服务端状态。",
            SessionControlStatus.DefinitivelyRejected => "服务器明确拒绝了操作；未更改草稿。",
            SessionControlStatus.ConfirmationRequired => "会话目标或状态已变化；未执行，请重新打开菜单并确认。",
            _ => "当前执行、排队、权限或保护状态不允许操作；未执行。",
        };
        QueueHomeProjection();
    }

    private void InvalidateDeletedHomeSession(HomeActionOwner owner)
    {
        if (!OwnsHomeAction(owner, owner.Origin)) return;
        // Reuse normal tab removal and binding/history/ACK invalidation. Never select another row,
        // discard the sole composer draft, or open user history as a context-action side effect.
        if (_sessionTabs.TryGetValue(owner.SessionId, out var tab)) RemoveSessionTab(tab);
        _sessionTitles.Remove(owner.SessionId);
        _lastIdleBySession.Remove(owner.SessionId);
        _workingSessions.Remove(owner.SessionId);
        if (_activeTabSessionId == owner.SessionId) _activeTabSessionId = null;
        if (_selectedSessionId == owner.SessionId || _sessionReturnContext is { } context
            && context.TargetSessionId == owner.SessionId && IsSessionReturnContextCurrent(context))
        {
            DeselectSessionForNewTab();
            BindComposer(); // Normal null binding retains the existing controller-owned draft.
        }
    }

    private async Task RefreshHomeActionMetadataAsync(HomeActionOwner owner, CancellationToken ct)
    {
        var epoch = BeginSessionRefresh();
        Task<OpenCodeSessionsResponse>? pending = null;
        try
        {
            owner.Origin.Source.TryExecute(ct, () =>
            {
                if (OwnsHomeAction(owner, owner.Origin)) pending = owner.Client.ListSessionsAsync(
                    new OpenCodeSessionListQuery { Order = OpenCodeSessionOrders.Desc, Limit = 50, ParentFilter = OpenCodeSessionParentFilter.Roots }, ct);
            });
            if (pending is null) return;
            var response = await pending;
            if (!OwnsHomeAction(owner, owner.Origin) || owner.Origin.Source.IsRevoked || epoch != _sessionListEpoch) return;
            _ = RebuildSessionList(response.Data ?? new List<OpenCodeSessionInfo>()); // Metadata only; deliberately never SwitchToSessionAsync.
            RestartSessionPagination((response.Data?.Count ?? 0) > 0 ? response.Cursor?.Next : null);
            _homeSessionsMessage = null;
            RefreshSessionStateUi();
            QueueHomeProjection();
        }
        catch (Exception)
        {
            if (OwnsHomeAction(owner, owner.Origin) && !owner.Origin.Source.IsRevoked && epoch == _sessionListEpoch)
            {
                _homeSessionsMessage = "操作后的会话列表未能刷新；请手动刷新。不会自动重试写操作，已有保护保持不变。";
                QueueHomeProjection();
            }
        }
    }
}
