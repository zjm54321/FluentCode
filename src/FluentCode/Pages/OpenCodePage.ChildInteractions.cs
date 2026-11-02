using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private sealed class ChildInteractionSession
    {
        internal bool Verified, Reading, RefreshPending;
        internal long Version;
    }

    private sealed record ChildInteractionBinding(OpenCodePageSource Source, long Revision, int SourceGeneration,
        OpenCodeClient Client, int SessionGeneration, string SelectedSessionId, CancellationTokenSource Cancellation)
    {
        internal readonly Dictionary<string, ChildInteractionSession> Sessions = new(StringComparer.Ordinal);
        internal bool Discovering, DiscoverAgain;
    }

    private ChildInteractionBinding? _childInteractionBinding;
    private string? _sessionRecoveryTargetId;

    private void ResetChildInteractions()
    {
        _childInteractionBinding?.Cancellation.Cancel();
        _childInteractionBinding?.Cancellation.Dispose();
        _childInteractionBinding = null;
        _sessionRecoveryTargetId = null;
    }

    private bool ChildInteractionsCurrent(ChildInteractionBinding binding)
        => ReferenceEquals(binding, _childInteractionBinding) && !binding.Cancellation.IsCancellationRequested
            && binding.SelectedSessionId == _selectedSessionId
            && IsCurrentBinding(binding.Source, binding.Revision, binding.SourceGeneration, binding.Client, binding.SessionGeneration);

    private ChildInteractionBinding? EnsureChildInteractionBinding()
    {
        if (!_pageActive || !SourceReady || _sessionMissing || _selectedSessionId is not { } selected) return null;
        if (_childInteractionBinding is { } current && ChildInteractionsCurrent(current)) return current;
        ResetChildInteractions();
        return _childInteractionBinding = new(_source!, _sourceRevision, _sourceGeneration, _client!, _sessionGeneration,
            selected, CreateOperationToken(_source!));
    }

    private bool IsInteractionSession(string? sessionId)
        => sessionId is not null && (sessionId == _selectedSessionId
            || _childInteractionBinding is { } binding && ChildInteractionsCurrent(binding)
                && binding.Sessions.TryGetValue(sessionId, out var child) && child.Verified);

    private string? GetInteractionSession(bool isForm, string id)
    {
        var sessionId = _interactionsView.GetSessionId(isForm, id);
        return IsInteractionSession(sessionId) ? sessionId : null;
    }

    private void QueueChildInteractionDiscovery()
    {
        if (EnsureChildInteractionBinding() is not { } binding) return;
        if (binding.Discovering) { binding.DiscoverAgain = true; return; }
        _ = DiscoverChildInteractionsAsync(binding);
    }

    private async Task DiscoverChildInteractionsAsync(ChildInteractionBinding binding)
    {
        binding.Discovering = true;
        var ct = binding.Cancellation.Token;
        try
        {
            Task<OpenCodeSessionsResponse> Read(OpenCodeSessionListQuery query, CancellationToken token)
            {
                if (!TryStartRead(binding.Source, binding.Revision, binding.SourceGeneration, binding.Client, binding.SessionGeneration, token,
                        () => binding.Client.ListSessionsAsync(query, token), out var task)) throw new OperationCanceledException(token);
                return task!;
            }
            await foreach (var info in OpenCodeSessionAncestry.EnumerateDescendantsAsync(Read, binding.SelectedSessionId, ct))
            {
                if (!ChildInteractionsCurrent(binding)) return;
                if (!binding.Sessions.TryGetValue(info.Id!, out var child)) binding.Sessions[info.Id!] = child = new();
                child.Verified = true; // Each page is filtered and checked against an already verified parent.
                QueueChildInteractionRead(binding, info.Id!, child);
            }
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { /* Unknown descendants are not admitted; live asks can verify their exact ancestry. */ }
        finally
        {
            binding.Discovering = false;
            if (ChildInteractionsCurrent(binding) && binding.DiscoverAgain)
            {
                binding.DiscoverAgain = false;
                QueueChildInteractionDiscovery();
            }
        }
    }

    // Runs before the selected-session message/workflow filter: a parent send can be waiting on this child.
    private void OnChildInteractionEvent(OpenCodeEvent item)
    {
        if (item.SessionId is not { } sessionId || sessionId == _selectedSessionId) return;
        if (item.Type == "session.deleted")
        {
            if (_childInteractionBinding is { } existing && ChildInteractionsCurrent(existing) && existing.Sessions.Remove(sessionId))
            {
                _interactionsView.RemoveSession(sessionId);
                UpdateInteractionsSection();
            }
            return;
        }
        if (item.Type is not ("permission.asked" or "permission.replied" or "form.created" or "form.replied" or "form.cancelled")) return;
        if (EnsureChildInteractionBinding() is not { } binding) return;
        if (!binding.Sessions.TryGetValue(sessionId, out var child)) binding.Sessions[sessionId] = child = new();
        child.Version++; // An in-flight REST snapshot must never revive a request settled by this event.
        var form = item.Type.StartsWith("form.", StringComparison.Ordinal);
        var key = item.Type == "permission.replied" ? "requestID" : "id";
        if (item.Type is "permission.replied" or "form.replied" or "form.cancelled"
            && item.Data.TryGetProperty(key, out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() is { } requestId && _interactionsView.GetSessionId(form, requestId) == sessionId)
        {
            _interactionsView.RemoveSettled(form, requestId);
            UpdateInteractionsSection();
        }
        QueueChildInteractionRead(binding, sessionId, child);
    }

    private void QueueChildInteractionRead(ChildInteractionBinding binding, string sessionId, ChildInteractionSession child)
    {
        child.RefreshPending = true;
        if (!child.Reading) _ = RefreshChildInteractionsAsync(binding, sessionId, child);
    }

    private async Task RefreshChildInteractionsAsync(ChildInteractionBinding binding, string sessionId, ChildInteractionSession child)
    {
        child.Reading = true;
        var ct = binding.Cancellation.Token;
        bool Current() => ChildInteractionsCurrent(binding) && binding.Sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(child, current);
        try
        {
            if (!child.Verified)
            {
                if (!TryStartRead(binding.Source, binding.Revision, binding.SourceGeneration, binding.Client, binding.SessionGeneration, ct,
                        () => OpenCodeSessionAncestry.IsAncestorAsync(binding.Client, sessionId, binding.SelectedSessionId, ct), out var ancestry)) return;
                var verified = await ancestry!;
                if (!Current()) return;
                if (!verified) { binding.Sessions.Remove(sessionId); return; }
                child.Verified = true;
            }
            while (Current() && child.RefreshPending)
            {
                child.RefreshPending = false;
                var version = child.Version;
                if (!TryStartRead(binding.Source, binding.Revision, binding.SourceGeneration, binding.Client, binding.SessionGeneration, ct,
                        () => ReadChildInteractionsAsync(binding.Client, sessionId, ct), out var pending)) return;
                var snapshot = await pending!;
                if (!Current()) return;
                if (version != child.Version) { child.RefreshPending = true; continue; }
                _interactionsView.ApplyForSession(sessionId, snapshot.Permissions, snapshot.Forms);
                if (_interactionBusy) _interactionsView.SetSubmitting(false, string.Empty, true, string.Empty);
                UpdateInteractionsSection();
            }
        }
        catch (OperationCanceledException) { }
        catch (OpenCodeException) { /* Preserve cards on a failed read; every mutation re-reads the exact request. */ }
        finally { child.Reading = false; }
    }

    private static async Task<(IReadOnlyList<OpenCodePermissionRequest>? Permissions, IReadOnlyList<OpenCodeFormInfo>? Forms)> ReadChildInteractionsAsync(
        OpenCodeClient client, string sessionId, CancellationToken ct)
    {
        static async Task<T?> Optional<T>(Func<Task<T>> read) where T : class
        {
            try { return await read(); }
            catch (OpenCodeException) { return null; }
        }
        var permissions = Optional(() => client.GetPendingPermissionsAsync(sessionId, ct));
        var forms = Optional(() => client.GetPendingFormsAsync(sessionId, ct));
        await Task.WhenAll(permissions, forms);
        return (await permissions, await forms);
    }
}
