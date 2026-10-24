using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

public sealed record SessionControlOrigin(OpenCodePageSource Source, long Revision, string SessionId, long OwnerGeneration)
{
    /// <summary>Carry forward known unresolved children even if rollback removed their launch rows.
    /// The Page owner must supply its retained immutable execution evidence at invocation.</summary>
    public ImmutableArray<string> PreviouslyKnownUnresolvedChildSessionIds { get; init; } = ImmutableArray<string>.Empty;
    public bool LocalSubmissionInProgress { get; init; }
    public bool ApplyingSendSelection { get; init; }
}
/// <summary>Caller-owned exact authoritative draft token/fingerprint, including command/files/IME state.
/// Empty must mean empty text, command AND files. It is not permission to borrow a newer editor token.</summary>
public sealed record SessionControlDraft(string Fingerprint, bool Empty);
public sealed record SessionControlRequest(SessionControlKind Kind, SessionControlModelRef? Model = null, bool Files = false,
    string? Directory = null, string? ExpectedTitle = null, string? NewTitle = null);
public enum SessionControlStatus { Ready, Accepted, Blocked, OriginLost, ConfirmationRequired, InsufficientHistory,
    NothingToUndo, NoStage, OutcomeUnknown, DefinitivelyRejected }

/// <summary>One-use preview; display all warnings before calling ConfirmAsync. No lease survives its creation.</summary>
public sealed class SessionControlPreview
{
    internal int Consumed;
    internal SessionControlPreview(SessionControlOrigin origin, SessionControlRequest request, SessionControlDraft draft,
        SessionControlState state, SessionControlModels? catalog, SessionControlDefaultModel? inherited,
        SessionControlHistoryMessage? target, string fingerprint)
    { Origin = origin; Request = request; Draft = draft; State = state; Catalog = catalog; Inherited = inherited; Target = target; Fingerprint = fingerprint; }
    public SessionControlOrigin Origin { get; }
    public SessionControlRequest Request { get; }
    public SessionControlDraft Draft { get; }
    public SessionControlState State { get; }
    public SessionControlModels? Catalog { get; }
    public SessionControlDefaultModel? Inherited { get; }
    public SessionControlHistoryMessage? Target { get; }
    public string Fingerprint { get; }
    public bool ExplicitModelUnavailable => State.Model is { } model && Catalog is { } catalog && !catalog.Models.Any(m => m.Offers(model));
    public bool RequiresTextOnlyConsent => Target?.HasNonTextReferences == true;
    public bool PriorStageMayHaveFileEffects => State.Revert?.MayHaveFileEffects == true;
    public string Warning => Request.Kind == SessionControlKind.SwitchModel
        ? "Applies to subsequent turns. Busy restriction is native client policy, not a documented model-switch 409 guarantee."
        : "Best-effort rollback; no cross-client CAS. Snapshots may overwrite current edits, exclude external effects, or be unavailable. "
          + "Moving a stage can restore its prior files even with files:false. Redo clears the entire stage and may wake queued execution. "
          + "Restoration is text-only; file/agent/skill references are not restored or fetched.";
}
public sealed record SessionControlPreviewResult(SessionControlStatus Status, SessionControlPreview? Preview);
public sealed record SessionControlRestoration(SessionControlOrigin Origin, string MessageId, string Text,
    bool HadNonTextReferences, string ExpectedDraftFingerprint, string AcceptedStageFingerprint);
public sealed record SessionControlResult(Guid OperationId, SessionControlStatus Status, bool MutationStarted,
    bool AcceptanceKnown, bool ProtectionRetained, SessionControlState? Observed, SessionControlRestoration? Restoration,
    SessionControlErrorKind? Error = null);

public sealed record SessionControlContinuationConsent(SessionControlOrigin Origin, string StageFingerprint, string DraftFingerprint);
public sealed record SessionControlContinuationPreparation(SessionControlStatus Status, SessionControlState? State,
    SessionControlContinuationConsent? RequiredConsent, SessionControlContinuationPermit? Permit);
public sealed class SessionControlContinuationPermit
{
    internal SessionControlContinuationPermit(SessionControlOrigin origin, OpenCodeInteractionGate.Lease lease,
        SessionControlDraft draft, SessionControlRevert stage)
    { Origin = origin; Lease = lease; Draft = draft; Stage = stage; }
    public SessionControlOrigin Origin { get; }
    public SessionControlDraft Draft { get; }
    public SessionControlRevert Stage { get; }
    internal OpenCodeInteractionGate.Lease Lease { get; }
    internal int Consumed;
    internal Guid OperationId;
    internal bool Paired;
}

/// <summary>Single-shot headless session controls. All writers must use the shared lease.
/// Pure owner predicates read immutable captured fields, never XAML/App/current mutable page state.
/// Start authority includes the captured draft ticket for undo/continuation, even during awaited preflight.
/// Operation ownership includes the full source/selection/local-operation lifetime but survives draft edits;
/// known backend settlement must not depend on whether the original restoration ticket is still usable.
/// Fresh GET + local lease is not a server CAS or cross-client atomic transaction.</summary>
public sealed class OpenCodeSessionControlCoordinator
{
    public const int HistoryPageSize = 100, HistoryPageBudget = 4, PendingBudget = 256;
    private readonly OpenCodeInteractionGate _gate;
    public OpenCodeSessionControlCoordinator(OpenCodeInteractionGate gate) => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    public async Task<SessionControlPreviewResult> PreviewAsync(SessionControlOrigin origin, SessionControlRequest request,
        SessionControlDraft draft, Func<SessionControlOrigin, bool> isCurrent, CancellationToken ct = default)
    {
        Validate(origin, request, draft, isCurrent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, origin.Source.RevocationToken);
        try
        {
            using var lease = await _gate.EnterAsync(origin.Source.Identity, origin.SessionId, linked.Token).ConfigureAwait(false);
            if (!lease.CanControlSession()) return new(SessionControlStatus.Blocked, null);
            using var client = CreateClient(origin, isCurrent, linked.Token);
            return await FreshAsync(origin, request, draft, isCurrent, client, linked.Token).ConfigureAwait(false);
        }
        catch (Lost) { return new(SessionControlStatus.OriginLost, null); }
        catch (Exception e) when (ReadFailure(e)) { return new(SessionControlStatus.Blocked, null); }
    }

    /// <summary>canStart includes exact draft/action authority; ownsOperation is the stable captured operation
    /// owner. Both gate initiation; only ownsOperation gates settlement/refresh. Restoration still requires
    /// the caller's original exact draft ticket, never a newer token or a post-start bypass flag.</summary>
    public Task<SessionControlResult> ConfirmAsync(SessionControlPreview preview, Guid operationId,
        SessionControlDraft draft, Func<SessionControlOrigin, bool> canStart,
        Func<SessionControlOrigin, bool> ownsOperation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (operationId == Guid.Empty) throw new ArgumentException("Empty operation ID.", nameof(operationId));
        Validate(preview.Origin, preview.Request, draft, canStart);
        ArgumentNullException.ThrowIfNull(ownsOperation);
        if (Interlocked.Exchange(ref preview.Consumed, 1) != 0 || preview.Draft != draft)
            return Task.FromResult(new SessionControlResult(operationId, SessionControlStatus.ConfirmationRequired, false, false, false, null, null));
        return ApplyAsync(preview.Origin, preview.Request, preview, operationId, draft, canStart, ownsOperation, ct);
    }

    /// <summary>A pending next-send choice has no intervening preview dialog. Validate it once
    /// under the same lease as its mutation; never borrow stale picker/catalog evidence.</summary>
    public Task<SessionControlResult> ApplySendModelAsync(SessionControlOrigin origin, Guid operationId,
        SessionControlModelRef model, SessionControlDraft draft, Func<SessionControlOrigin, bool> canStart,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.ApplyingSendSelection) throw new ArgumentException("Not a pending send selection.", nameof(origin));
        ArgumentNullException.ThrowIfNull(model);
        return ApplyAsync(origin, new(SessionControlKind.SwitchModel, model), null, operationId, draft, canStart, canStart, ct);
    }

    private async Task<SessionControlResult> ApplyAsync(SessionControlOrigin origin, SessionControlRequest request,
        SessionControlPreview? preview, Guid operationId, SessionControlDraft draft,
        Func<SessionControlOrigin, bool> canStart, Func<SessionControlOrigin, bool> ownsOperation, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Empty operation ID.", nameof(operationId));
        Validate(origin, request, draft, canStart);
        ArgumentNullException.ThrowIfNull(ownsOperation);
        bool StartAuthority(SessionControlOrigin candidate) => ownsOperation(candidate) && canStart(candidate);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, origin.Source.RevocationToken);
        OpenCodeInteractionGate.Lease? lease = null;
        bool started = false, accepted = false;
        SessionControlResult Result(SessionControlStatus status, SessionControlState? observed = null,
            SessionControlRestoration? restoration = null, SessionControlErrorKind? error = null)
            => new(operationId, status, started, accepted, lease?.GetSessionControlProtection() is not null, observed, restoration, error);
        try
        {
            lease = await _gate.EnterAsync(origin.Source.Identity, origin.SessionId, linked.Token).ConfigureAwait(false);
            if (!lease.CanControlSession()) return Result(SessionControlStatus.Blocked);
            using var client = CreateClient(origin, StartAuthority, linked.Token);
            var fresh = await FreshAsync(origin, request, draft, StartAuthority, client, linked.Token).ConfigureAwait(false);
            if (fresh.Status != SessionControlStatus.Ready || fresh.Preview is null
                || preview is not null && fresh.Preview.Fingerprint != preview.Fingerprint)
                return Result(SessionControlStatus.ConfirmationRequired);

            bool Start(Action send) => Fence(origin, StartAuthority, linked.Token, () =>
            {
                if (!lease.CanControlSession()) throw new InvalidOperationException("Session control protected.");
                lease.BeginSessionControl(operationId, request.Kind, fresh.Preview.Fingerprint);
                started = true;
                send();
            });
            SessionControlRevert? acceptedStage = null;
            switch (request.Kind)
            {
                case SessionControlKind.SwitchModel:
                    await client.SwitchModelGuardedAsync(origin.SessionId, request.Model!, Start, linked.Token).ConfigureAwait(false); break;
                case SessionControlKind.StageRevert:
                    acceptedStage = await client.StageRevertGuardedAsync(origin.SessionId, fresh.Preview.Target!.Id,
                        request.Files, Start, linked.Token).ConfigureAwait(false); break;
                case SessionControlKind.ClearRevert:
                    await client.ClearRevertGuardedAsync(origin.SessionId, Start, linked.Token).ConfigureAwait(false); break;
                case SessionControlKind.DeleteRoot:
                    await client.DeleteSessionGuardedAsync(origin.SessionId, Start, linked.Token).ConfigureAwait(false); break;
                case SessionControlKind.RenameRoot:
                    await client.RenameSessionGuardedAsync(origin.SessionId, request.NewTitle!, request.Directory!, Start, linked.Token).ConfigureAwait(false); break;
                case SessionControlKind.ArchiveRoot:
                    await client.ArchiveSessionGuardedAsync(origin.SessionId, request.Directory!, Start, linked.Token).ConfigureAwait(false); break;
                default: throw new InvalidOperationException("Continuation uses the already-held-lease seam.");
            }
            accepted = true;
            // Settlement is exact and source-owned. Caller cancellation after a known acceptance is not rejection.
            if (!Fence(origin, ownsOperation, CancellationToken.None,
                () => lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Succeeded)))
                return Result(SessionControlStatus.OutcomeUnknown);
            SessionControlState? observed = null;
            try { if (request.Kind != SessionControlKind.DeleteRoot) observed = await Read(origin, ownsOperation, linked.Token, () => client.GetSessionControlStateAsync(origin.SessionId, linked.Token)).ConfigureAwait(false); }
            catch (Exception e) when (ReadFailure(e) || e is Lost) { /* Accepted remains accepted; display refresh is separate. */ }
            SessionControlRestoration? restoration = null;
            if (acceptedStage is not null && observed?.Revert?.Fingerprint == acceptedStage.Fingerprint && fresh.Preview.Target is { } target)
                restoration = new(origin, target.Id, target.Text!, target.HasNonTextReferences, draft.Fingerprint, acceptedStage.Fingerprint);
            return Result(SessionControlStatus.Accepted, observed, restoration);
        }
        catch (OpenCodeSessionControlException error) when (started)
        {
            if (error.VerifiedPreEffect && Fence(origin, ownsOperation, CancellationToken.None,
                () => lease!.CompleteSessionControl(operationId, NotificationInteractionStatus.Failed)))
                return Result(SessionControlStatus.DefinitivelyRejected, error: error.Kind);
            return Result(SessionControlStatus.OutcomeUnknown, error: error.Kind);
        }
        catch (Exception) when (started) { return Result(SessionControlStatus.OutcomeUnknown); }
        catch (Lost) { return Result(SessionControlStatus.OriginLost); }
        catch (Exception e) when (ReadFailure(e)) { return Result(SessionControlStatus.Blocked); }
        finally { lease?.Dispose(); }
    }

    /// <summary>No lease reacquisition and no prompt/commit POST. canStart checks full origin and exact draft
    /// authority. Unknown stage never means unstaged. NoStage is information, NOT send authorization:
    /// the caller must still apply its existing ordinary/enhanced sender policy.</summary>
    public async Task<SessionControlContinuationPreparation> PrepareContinuationAsync(SessionControlOrigin origin,
        OpenCodeInteractionGate.Lease heldLease, SessionControlDraft draft, SessionControlContinuationConsent? consent,
        Func<SessionControlOrigin, bool> canStart, CancellationToken ct = default)
    {
        Validate(origin, new(SessionControlKind.RevertContinuation), draft, canStart);
        if (!heldLease.MatchesScope(origin.Source.Identity, origin.SessionId) || heldLease.GetSessionControlProtection() is not null)
            return new(SessionControlStatus.Blocked, null, null, null);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, origin.Source.RevocationToken);
        try
        {
            using var client = CreateClient(origin, canStart, linked.Token);
            var state = await Read(origin, canStart, linked.Token, () => client.GetSessionControlStateAsync(origin.SessionId, linked.Token)).ConfigureAwait(false);
            if (state.Revert is not { } stage)
                return new(consent is null ? SessionControlStatus.NoStage : SessionControlStatus.ConfirmationRequired, state, null, null);
            if (!heldLease.CanControlSession()) return new(SessionControlStatus.Blocked, state, null, null);
            var required = new SessionControlContinuationConsent(origin, stage.Fingerprint, draft.Fingerprint);
            if (required != consent) return new(SessionControlStatus.ConfirmationRequired, state, required, null);
            return new(SessionControlStatus.Ready, state, null, new(origin, heldLease, draft, stage));
        }
        catch (Exception e) when (ReadFailure(e) || e is Lost)
        { return new(SessionControlStatus.ConfirmationRequired, null, null, null); }
    }

    /// <summary>Use as the validated prompt/command client's synchronous starter. Exactly one send; no commit call.
    /// null composerKind is the ordinary sender; enhanced sends install BOTH protections atomically.
    /// canStart must still check exact draft/action authority and full operation origin.</summary>
    public bool TryStartContinuation(SessionControlContinuationPermit permit, OpenCodeInteractionGate.Lease heldLease,
        Guid operationId, SessionControlDraft draft, ComposerSubmissionKind? composerKind,
        Func<SessionControlOrigin, bool> canStart, Action start, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(permit); ArgumentNullException.ThrowIfNull(start);
        if (!ReferenceEquals(permit.Lease, heldLease) || !heldLease.MatchesScope(permit.Origin.Source.Identity, permit.Origin.SessionId)
            || permit.Draft != draft || Interlocked.Exchange(ref permit.Consumed, 1) != 0) return false;
        return Fence(permit.Origin, canStart, ct, () =>
        {
            if (composerKind is { } kind) heldLease.BeginComposerContinuation(operationId, kind, permit.Stage.Fingerprint);
            else heldLease.BeginSessionControl(operationId, SessionControlKind.RevertContinuation, permit.Stage.Fingerprint);
            permit.OperationId = operationId;
            permit.Paired = composerKind is not null;
            start();
        });
    }

    /// <summary>ownsOperation checks the original stable source/binding/local operation owner, independent of
    /// later draft edits. It does not authorize draft restoration or prove commit from a command 204.</summary>
    public bool CompleteContinuation(SessionControlContinuationPermit permit, OpenCodeInteractionGate.Lease heldLease,
        Guid operationId, NotificationInteractionStatus submissionStatus, NotificationInteractionStatus continuationStatus,
        Func<SessionControlOrigin, bool> ownsOperation)
    {
        if (!ReferenceEquals(permit.Lease, heldLease) || operationId == Guid.Empty || permit.OperationId != operationId) return false;
        return Fence(permit.Origin, ownsOperation, CancellationToken.None, () =>
        {
            if (permit.Paired) heldLease.CompleteComposerContinuation(operationId, submissionStatus, continuationStatus);
            else heldLease.CompleteSessionControl(operationId, continuationStatus);
        });
    }

    private async Task<SessionControlPreviewResult> FreshAsync(SessionControlOrigin origin, SessionControlRequest request,
        SessionControlDraft draft, Func<SessionControlOrigin, bool> current, OpenCodeClient client, CancellationToken ct)
    {
        Task<T> Get<T>(Func<Task<T>> start) => Read(origin, current, ct, start);
        var state = await Get(() => client.GetSessionControlStateAsync(origin.SessionId, ct)).ConfigureAwait(false);
        bool RootMatches(SessionControlState candidate) => candidate.ParentId is null && candidate.Directory == request.Directory
            && candidate.Title == request.ExpectedTitle;
        var rootAction = request.Kind is SessionControlKind.DeleteRoot or SessionControlKind.RenameRoot or SessionControlKind.ArchiveRoot;
        if (rootAction && !RootMatches(state)) return new(SessionControlStatus.ConfirmationRequired, null);
        SessionControlModels? catalog = null; SessionControlDefaultModel? inherited = null;
        if (request.Kind == SessionControlKind.SwitchModel)
        {
            catalog = await Get(() => client.GetSelectableModelsAsync(state.Directory, ct)).ConfigureAwait(false);
            if (catalog.Directory != state.Directory || request.Model is null || !catalog.Models.Any(m => m.Offers(request.Model)))
                return new(SessionControlStatus.Blocked, null);
            if (state.Model is null && !origin.ApplyingSendSelection)
            {
                inherited = await Get(() => client.GetDefaultModelAsync(state.Directory, ct)).ConfigureAwait(false);
                if (inherited.Directory != state.Directory) return new(SessionControlStatus.Blocked, null);
            }
            if (origin.ApplyingSendSelection)
            {
                if (state.ParentId is not null) return new(SessionControlStatus.Blocked, null);
                // A subsequent-turn choice is valid during execution. Do not fingerprint streaming
                // history: it would invalidate every send while a provider is producing output.
                var selectionFingerprint = SessionControlJson.Hash(new { state.SessionId, state.Directory,
                    state.Model, request, draft, selected = catalog.Models.Single(m => m.Offers(request.Model)).Fingerprint });
                return new(SessionControlStatus.Ready, new(origin, request, draft, state, catalog, inherited, null, selectionFingerprint));
            }
        }
        else if (request.Kind == SessionControlKind.StageRevert && !draft.Empty) return new(SessionControlStatus.Blocked, null);
        else if (request.Kind == SessionControlKind.ClearRevert && state.Revert is null) return new(SessionControlStatus.NoStage, null);
        else if (request.Kind == SessionControlKind.RevertContinuation) return new(SessionControlStatus.Blocked, null);

        async Task<(List<SessionControlHistoryMessage> Messages, bool Complete)> HistoryAsync()
        {
            var messages = new List<SessionControlHistoryMessage>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            // A continuation cursor gates the next history page; those reads remain serial.
            for (int pageIndex = 0; pageIndex < HistoryPageBudget; pageIndex++)
            {
                var page = await Get(() => client.GetSessionControlHistoryPageAsync(origin.SessionId, cursor, HistoryPageSize, ct)).ConfigureAwait(false);
                messages.AddRange(page.Messages);
                if (page.Next is null) return (messages, true);
                if (page.Messages.IsEmpty || !cursors.Add(page.Next)) break;
                cursor = page.Next;
            }
            return (messages, false);
        }
        // All reads share the captured origin/lease; Read fences initiation AND settlement.
        // State/directory and selectable-model validation above gate this read-only batch.
        var historyRead = HistoryAsync();
        var permissionsRead = Get(() => client.GetPendingPermissionsAsync(origin.SessionId, ct));
        var formsRead = Get(() => client.GetPendingFormsAsync(origin.SessionId, ct));
        var inboxRead = Get(() => client.GetInboxAsync(origin.SessionId, ct));
        var activeRead = Get(() => client.GetActiveSessionsAsync(ct));
        await Task.WhenAll(historyRead, permissionsRead, formsRead, inboxRead, activeRead).ConfigureAwait(false);
        var (descending, complete) = await historyRead.ConfigureAwait(false);
        var permissions = await permissionsRead.ConfigureAwait(false);
        var forms = await formsRead.ConfigureAwait(false);
        var inbox = await inboxRead.ConfigureAwait(false);
        var active = await activeRead.ConfigureAwait(false);
        if (!Fence(origin, current, ct, () => { })) throw new Lost();
        if (!complete || descending.Any(m => !m.ReferencesComplete)
            || descending.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != descending.Count)
            return new(SessionControlStatus.InsufficientHistory, null);
        descending.Reverse(); // The ONLY order conversion: server desc -> authoritative chronological order.
        var history = descending.ToImmutableArray();
        int boundary = history.Length;
        if (state.Revert is { } staged)
        {
            boundary = descending.FindIndex(m => m.Id == staged.MessageId);
            if (boundary < 0) return new(SessionControlStatus.InsufficientHistory, null);
        }
        SessionControlHistoryMessage? target = null;
        if (request.Kind == SessionControlKind.StageRevert)
        {
            for (var i = boundary - 1; i >= 0; i--) if (history[i].Type == "user") { target = history[i]; break; }
            if (target is null) return new(SessionControlStatus.NothingToUndo, null);
        }

        if (permissions.Count > PendingBudget || forms.Count > PendingBudget || inbox.Count > PendingBudget
            || inbox.Any(i => i.SessionId != origin.SessionId) || active.Data is null) return new(SessionControlStatus.Blocked, null);
        active.Data.TryGetValue(origin.SessionId, out var drain);
        var execution = OpenCodeExecutionProjection.Evaluate(new(origin.SessionId,
            new(origin.SessionId, true, true, true, history.Select(m => m.Execution).ToImmutableArray()),
            drain is null || drain.Type == "running", drain?.Type == "running", true, permissions.Count != 0 || forms.Count != 0,
            true, inbox.Count != 0, origin.LocalSubmissionInProgress, false, origin.PreviouslyKnownUnresolvedChildSessionIds));
        if (!execution.CanSwitchAgent) return new(SessionControlStatus.Blocked, null);
        // Reads aren't atomic. Detect changes noticed during this preflight, but never claim cross-client CAS.
        var last = await Get(() => client.GetSessionControlStateAsync(origin.SessionId, ct)).ConfigureAwait(false);
        if (last.Fingerprint != state.Fingerprint || rootAction && !RootMatches(last)) return new(SessionControlStatus.ConfirmationRequired, null);
        var fingerprint = SessionControlJson.Hash(new { state.Fingerprint, request, draft,
            target = target?.Fingerprint, history = history.Select(m => m.Fingerprint).ToArray(),
            selected = catalog?.Models.Single(m => m.Offers(request.Model!)).Fingerprint,
            inherited = inherited?.Model?.Fingerprint });
        return new(SessionControlStatus.Ready, new(origin, request, draft, state, catalog, inherited, target, fingerprint));
    }

    private static void Validate(SessionControlOrigin origin, SessionControlRequest request, SessionControlDraft draft,
        Func<SessionControlOrigin, bool> current)
    {
        ArgumentNullException.ThrowIfNull(origin); ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(draft); ArgumentNullException.ThrowIfNull(current);
        // Running-session controls are not model/history preview operations.
        if (!Enum.IsDefined(request.Kind) || request.Kind is SessionControlKind.Interrupt or SessionControlKind.Background || string.IsNullOrWhiteSpace(draft.Fingerprint)
            || !origin.SessionId.StartsWith("ses", StringComparison.Ordinal) || !NotificationArgumentCodec.IsValidRequestId(origin.SessionId))
            throw new ArgumentException("Invalid session control context.");
        if (request.Kind is SessionControlKind.DeleteRoot or SessionControlKind.RenameRoot or SessionControlKind.ArchiveRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Directory);
            if (request.Kind == SessionControlKind.RenameRoot) ArgumentException.ThrowIfNullOrWhiteSpace(request.NewTitle);
        }
    }
    private static bool Fence(SessionControlOrigin origin, Func<SessionControlOrigin, bool> current, CancellationToken ct, Action action)
    {
        bool executed = false;
        return origin.Source.TryExecute(ct, () =>
        {
            if (origin.Source.Revision != origin.Revision || !current(origin)) return;
            action(); executed = true;
        }) && executed;
    }
    private static OpenCodeClient CreateClient(SessionControlOrigin origin, Func<SessionControlOrigin, bool> current, CancellationToken ct)
    {
        OpenCodeClient? client = null;
        if (!Fence(origin, current, ct, () => client = origin.Source.CreateClient())) throw new Lost();
        return client!;
    }
    private static async Task<T> Read<T>(SessionControlOrigin origin, Func<SessionControlOrigin, bool> current,
        CancellationToken ct, Func<Task<T>> start)
    {
        Task<T>? pending = null;
        if (!Fence(origin, current, ct, () => pending = start())) throw new Lost();
        var result = await pending!.ConfigureAwait(false);
        if (!Fence(origin, current, ct, () => { })) throw new Lost();
        return result;
    }
    private static bool ReadFailure(Exception error) => error is OperationCanceledException or OpenCodeException
        or System.Net.Http.HttpRequestException or InvalidOperationException;
    private sealed class Lost : Exception { }
}
