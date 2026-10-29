using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;

namespace AIFrontend.Services.OpenCode;

/// <summary>One-shot plugin writes. UI owns confirmation, current-binding checks and subsequent read refresh.</summary>
public sealed class OpenCodePluginMutationCoordinator(OpenCodeInteractionGate? gate = null)
{
    private readonly OpenCodeInteractionGate _gate = gate ?? OpenCodeInteractionGate.Shared;
    private sealed record Unknown(PluginMutationOrigin Origin, SessionControlKind Kind, string Fingerprint, PluginMutationResult Result);
    private readonly ConcurrentDictionary<Guid, Unknown> _unknown = new();

    public Task<PluginMutationResult> ApplyPresetAsync(PluginMutationOrigin origin, string exactPresetName,
        Func<PluginMutationOrigin, bool> canStart, CancellationToken cancellationToken = default)
        => MutateAsync(origin, exactPresetName, SessionControlKind.ApplySessionPreset, canStart, cancellationToken);

    public Task<PluginMutationResult> ClearEncryptedAsync(PluginMutationOrigin origin,
        Func<PluginMutationOrigin, bool> canStart, CancellationToken cancellationToken = default)
        => MutateAsync(origin, null, SessionControlKind.ClearEncryptedContents, canStart, cancellationToken);

    /// <summary>Read-only local protection lookup, including after returning with a new current view generation.
    /// Keep this coordinator across view bindings. No RPC, acknowledgement, refresh or state change.</summary>
    public async Task<PluginMutationProtection?> GetProtectionAsync(PluginMutationOrigin origin,
        Func<PluginMutationOrigin, bool> canInspect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canInspect);
        if (origin?.Source is null || origin.OwnerGeneration < 0) return null;
        try
        {
            using var lease = await _gate.EnterAsync(origin.Source.Identity, origin.SessionId, cancellationToken).ConfigureAwait(false);
            PluginMutationProtection? metadata = null;
            origin.Source.TryExecute(cancellationToken, () =>
            {
                if (origin.Revision != origin.Source.Revision || !canInspect(origin)
                    || lease.GetSessionControlProtection() is not { } protection
                    || !_unknown.TryGetValue(protection.OperationId, out var recorded)
                    || !MatchesProtectedScope(origin, recorded.Origin)
                    || recorded.Kind != protection.Kind || recorded.Fingerprint != protection.TargetFingerprint) return;
                metadata = new(protection.OperationId, protection.Kind, recorded.Result);
            });
            return metadata;
        }
        catch (OperationCanceledException) { return null; }
    }

    private static bool MatchesProtectedScope(PluginMutationOrigin current, PluginMutationOrigin original)
        // Shared gate tombstones are endpoint-scoped, including across credential/revision changes.
        // Current source/revision/owner authority is checked separately under Source.TryExecute.
        => current.Source.Identity == original.Source.Identity
            && current.SessionId == original.SessionId && current.Directory == original.Directory;

    /// <summary>Explicit acknowledgement only: current owner at the same protected scope and exact operation,
    /// including a replacement view generation. Zero RPC, no retry or success/reconciliation inference.
    /// A non-null return retains OutcomeUnknown and changes only ProtectionRetained to false.</summary>
    public async Task<PluginMutationResult?> AcknowledgeUnknownAsync(PluginMutationOrigin origin, Guid operationId,
        Func<PluginMutationOrigin, bool> canStart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canStart);
        if (origin?.Source is null || origin.OwnerGeneration < 0 || !_unknown.TryGetValue(operationId, out var recorded)
            || !MatchesProtectedScope(origin, recorded.Origin)) return null;
        try
        {
            using var lease = await _gate.EnterAsync(origin.Source.Identity, origin.SessionId, cancellationToken).ConfigureAwait(false);
            var released = false;
            origin.Source.TryExecute(cancellationToken, () =>
            {
                if (origin.Revision != origin.Source.Revision || !canStart(origin)) return;
                released = lease.TryAcknowledgePluginMutation(operationId, recorded.Kind, recorded.Fingerprint);
            });
            if (!released) return null;
            _unknown.TryRemove(operationId, out _);
            return recorded.Result with { ProtectionRetained = false };
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task<PluginMutationResult> MutateAsync(PluginMutationOrigin origin, string? exactPresetName,
        SessionControlKind kind, Func<PluginMutationOrigin, bool> canStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(canStart);
        var operationId = Guid.NewGuid();
        PluginMutationResult NotStarted(string code) => new(PluginMutationStatus.NotStarted, false, false,
            null, null, code, "The operation was not started.", operationId);
        if (origin?.Source is null || origin.Revision != origin.Source.Revision || origin.OwnerGeneration < 0
            || cancellationToken.IsCancellationRequested || origin.Source.IsRevoked) return NotStarted("stale-origin");
        try
        {
            OpenCodeClient.ValidatePluginMutationInput(origin.SessionId, origin.Directory);
            if (kind == SessionControlKind.ApplySessionPreset && string.IsNullOrEmpty(exactPresetName)) return NotStarted("invalid-input");
        }
        catch (ArgumentException) { return NotStarted("invalid-input"); }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { origin.Revision, origin.SessionId, origin.Directory, origin.OwnerGeneration, kind, exactPresetName }))));
        var started = false;
        OpenCodeInteractionGate.Lease? lease = null;
        PluginMutationResult result;
        try
        {
            using var beforeDispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, origin.Source.RevocationToken);
            lease = await _gate.EnterAsync(origin.Source.Identity, origin.SessionId, beforeDispatch.Token).ConfigureAwait(false);
            if (!lease.CanControlSession()) return NotStarted("session-protected");
            using var client = origin.Source.CreateClient();
            // Only source revocation and the client's bounded timeout own the dispatched operation.
            // View lifetime cancellation is checked in the start guard, never linked to the HTTP request.
            bool Start(Action send)
            {
                var dispatched = false;
                origin.Source.TryExecute(beforeDispatch.Token, () =>
                {
                    if (origin.Revision != origin.Source.Revision || !canStart(origin)
                        || beforeDispatch.IsCancellationRequested) return;
                    lease.BeginSessionControl(operationId, kind, fingerprint);
                    try { send(); }
                    catch
                    {
                        // A synchronous failure in the initiation action occurred before it returned a
                        // transport task. No async workflow was dispatched; release this local begin.
                        lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Failed);
                        throw;
                    }
                    started = true;
                    dispatched = true;
                });
                return dispatched;
            }
            if (kind == SessionControlKind.ApplySessionPreset)
            {
                var preset = await client.ApplySessionPresetGuardedAsync(origin.SessionId, origin.Directory,
                    exactPresetName!, Start, origin.Source.RevocationToken).ConfigureAwait(false);
                result = new(PluginMutationStatus.Accepted, true, false, preset, null, null, null, operationId);
            }
            else
            {
                var clear = await client.ClearEncryptedContentsGuardedAsync(origin.SessionId, origin.Directory,
                    Start, origin.Source.RevocationToken).ConfigureAwait(false);
                result = new(PluginMutationStatus.Accepted, true, false, null, clear, null, null, operationId);
            }
        }
        catch (OpenCodePluginMutationRpcException error) when (started)
        {
            result = new(error.IsDefinitiveRejection ? PluginMutationStatus.Rejected : PluginMutationStatus.OutcomeUnknown,
                true, !error.IsDefinitiveRejection, null, null, error.FailureCode, error.Message, operationId);
        }
        catch (Exception)
        {
            result = started ? new(PluginMutationStatus.OutcomeUnknown, true, true, null, null,
                "unconfirmed", "The operation outcome is unknown; do not retry without acknowledgement.", operationId)
                : NotStarted("not-started");
        }
        finally
        {
            // Settlement belongs to this original lease, even after the view/source has moved on.
            // The result carries no callback that could publish into a new view or foreign read cache.
            if (!started) lease?.Dispose();
        }
        if (started)
        {
            try
            {
                lease!.CompleteSessionControl(operationId, result.Status switch
                {
                    PluginMutationStatus.Accepted => NotificationInteractionStatus.Succeeded,
                    PluginMutationStatus.Rejected => NotificationInteractionStatus.Failed,
                    _ => NotificationInteractionStatus.OutcomeUnknown,
                });
                if (result.ProtectionRetained) _unknown[operationId] = new(origin, kind, fingerprint, result);
            }
            finally { lease!.Dispose(); }
        }
        return result;
    }
}
