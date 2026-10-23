using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Notifications;

public sealed record AgentSwitchProtection(Guid OperationId, string TargetAgentId);
public enum ComposerSubmissionKind { Prompt, Command }
public sealed record ComposerSubmissionProtection(Guid OperationId, ComposerSubmissionKind Kind);
public enum SessionControlKind { SwitchModel, StageRevert, ClearRevert, RevertContinuation, Interrupt, Background, DeleteRoot, RenameRoot, ArchiveRoot }
public sealed record SessionControlProtection(Guid OperationId, SessionControlKind Kind, string TargetFingerprint);

/// <summary>Process-local shared page/toast arbitration. Hold a lease across recheck, mutation AND result.
/// No automatic expiry/eviction of unknown outcomes. All writers must participate.</summary>
public sealed class OpenCodeInteractionGate
{
    public static OpenCodeInteractionGate Shared { get; } = new();
    private readonly object _sync = new();
    private readonly Dictionary<(string, string), SessionState> _sessions = new();
    internal sealed class SessionState
    {
        internal readonly SemaphoreSlim Mutex = new(1, 1);
        internal readonly Dictionary<(string, NotificationNavigationKind), NotificationInteractionStatus> Results = new();
        internal int Waiters;
        internal int Holders;
        internal (string RequestId, NotificationNavigationKind Kind)? SessionUnknown;
        internal AgentSwitchProtection? AgentSwitchUnknown;
        internal ComposerSubmissionProtection? ComposerUnknown;
        internal SessionControlProtection? ControlUnknown;
    }

    public async Task<Lease> EnterAsync(string endpointIdentity, string sessionId, CancellationToken cancellationToken = default)
    {
        if (!NotificationArgumentCodec.IsValidRequestId(endpointIdentity) || !NotificationArgumentCodec.IsValidRequestId(sessionId))
            throw new ArgumentException("Invalid opaque endpoint/session identity.");
        SessionState state;
        lock (_sync)
        {
            if (!_sessions.TryGetValue((endpointIdentity, sessionId), out state!))
            {
                if (_sessions.Count >= 256) throw new InvalidOperationException("Interaction session capacity reached; fail closed.");
                _sessions.Add((endpointIdentity, sessionId), state = new());
            }
            state.Waiters++;
        }
        try
        {
            await state.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync) { state.Waiters--; state.Holders++; }
            return new(state, endpointIdentity, sessionId, this);
        }
        catch
        {
            lock (_sync)
            {
                state.Waiters--;
                RetireIfInactive((endpointIdentity, sessionId), state);
            }
            throw;
        }
    }

    /// <summary>
    /// Display-only, non-blocking, read-only probe: reports whether an EXISTING session state currently carries
    /// a whole-session protection. Returns false when the identities are illegal OR the session mutex is busy;
    /// callers MUST NOT read false as "no protection". This neither creates session state nor consumes the 256
    /// capacity, and it never mutates Results/unknown/gate state, so it is unusable for authorization or
    /// reconciliation. Read under the mutex so a concurrent lease holder cannot produce a torn value.
    /// </summary>
    public bool TryPeekSessionProtection(string endpointIdentity, string sessionId,
        out (string RequestId, NotificationNavigationKind Kind)? protection)
    {
        protection = null;
        if (!NotificationArgumentCodec.IsValidRequestId(endpointIdentity)
            || !NotificationArgumentCodec.IsValidRequestId(sessionId))
        {
            return false; // Illegal identity: unreadable, NOT unprotected.
        }

        lock (_sync)
        {
            if (!_sessions.TryGetValue((endpointIdentity, sessionId), out var state)) return true;
            if (!state.Mutex.Wait(0)) return false;
            try
            {
                protection = state.SessionUnknown;
                return true;
            }
            finally { state.Mutex.Release(); }
        }
    }

    // Display-only; busy/illegal identities are unreadable and must be rendered fail closed.
    public bool TryPeekComposerSubmissionProtection(string endpointIdentity, string sessionId,
        out ComposerSubmissionProtection? protection, out bool canSubmitComposer)
    {
        protection = null;
        canSubmitComposer = false;
        if (!NotificationArgumentCodec.IsValidRequestId(endpointIdentity) || !NotificationArgumentCodec.IsValidRequestId(sessionId)) return false;
        lock (_sync)
        {
            if (!_sessions.TryGetValue((endpointIdentity, sessionId), out var state)) { canSubmitComposer = true; return true; }
            if (!state.Mutex.Wait(0)) return false;
            try
            {
                protection = state.ComposerUnknown;
                canSubmitComposer = state.ComposerUnknown is null && state.SessionUnknown is null
                    && state.AgentSwitchUnknown is null && state.ControlUnknown is null;
                return true;
            }
            finally { state.Mutex.Release(); }
        }
    }

    public sealed class Lease : IDisposable
    {
        private SessionState? _state;
        private (string, NotificationNavigationKind)? _mutation;
        private bool _affectsSession;
        private Guid? _agentSwitch;
        private Guid? _composerSubmission;
        private Guid? _sessionControl;
        private bool _pairedContinuation;
        private readonly string _endpointIdentity;
        private readonly string _sessionId;
        private readonly OpenCodeInteractionGate _owner;
        internal Lease(SessionState state, string endpointIdentity, string sessionId, OpenCodeInteractionGate owner)
        { _state = state; _endpointIdentity = endpointIdentity; _sessionId = sessionId; _owner = owner; }
        private SessionState State => _state ?? throw new ObjectDisposedException(nameof(Lease));

        /// <summary>Already-held-lease adapters must verify the exact endpoint/session, never reacquire.</summary>
        public bool MatchesScope(string endpointIdentity, string sessionId)
            => _state is not null && _endpointIdentity == endpointIdentity && _sessionId == sessionId;

        public NotificationInteractionStatus? GetProtectionStatus(string requestId, NotificationNavigationKind kind)
        {
            if (State.SessionUnknown is not null || State.AgentSwitchUnknown is not null) return NotificationInteractionStatus.OutcomeUnknown;
            if (State.Results.TryGetValue((requestId, kind), out var status))
                return status == NotificationInteractionStatus.Succeeded ? NotificationInteractionStatus.Duplicate : status;
            return State.Results.Count >= 256 ? NotificationInteractionStatus.NeedsApp : null;
        }

        public bool CanSubmit(string requestId, NotificationNavigationKind kind) => GetProtectionStatus(requestId, kind) is null;

        /// <summary>Only a fresh complete owner-scoped pending list may retire settled request results.
        /// Missing unknown outcomes are retained until explicit user reconciliation.</summary>
        public void RetireSettledRequests(NotificationNavigationKind kind, IReadOnlySet<string> pending)
        {
            var state = State;
            var retired = new List<(string, NotificationNavigationKind)>();
            foreach (var pair in state.Results)
                if (pair.Key.Item2 == kind && !pending.Contains(pair.Key.Item1)
                    && pair.Value != NotificationInteractionStatus.OutcomeUnknown)
                    retired.Add(pair.Key);
            foreach (var key in retired) state.Results.Remove(key);
        }

        public (string RequestId, NotificationNavigationKind Kind)? GetSessionProtection() => State.SessionUnknown;

        public AgentSwitchProtection? GetAgentSwitchProtection() => State.AgentSwitchUnknown;
        public ComposerSubmissionProtection? GetComposerSubmissionProtection() => State.ComposerUnknown;
        public SessionControlProtection? GetSessionControlProtection() => State.ControlUnknown;

        public bool CanSubmitComposer() => _mutation is null && _agentSwitch is null && _composerSubmission is null
            && _sessionControl is null && State.SessionUnknown is null && State.AgentSwitchUnknown is null
            && State.ComposerUnknown is null && State.ControlUnknown is null;

        public void BeginComposerSubmission(Guid operationId, ComposerSubmissionKind kind)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("Operation ID must not be empty.", nameof(operationId));
            if (kind is not (ComposerSubmissionKind.Prompt or ComposerSubmissionKind.Command)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!CanSubmitComposer()) throw new InvalidOperationException("Session protected; explicit reconciliation required.");
            State.ComposerUnknown = new(operationId, kind);
            _composerSubmission = operationId;
        }

        public void CompleteComposerSubmission(Guid operationId, NotificationInteractionStatus status)
        {
            if (_pairedContinuation) throw new InvalidOperationException("Use exact paired continuation settlement.");
            if (operationId == Guid.Empty || _composerSubmission != operationId || State.ComposerUnknown?.OperationId != operationId)
                throw new InvalidOperationException("Composer operation mismatch.");
            if (status is not (NotificationInteractionStatus.Succeeded or NotificationInteractionStatus.Expired
                or NotificationInteractionStatus.Failed or NotificationInteractionStatus.OutcomeUnknown))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (status != NotificationInteractionStatus.OutcomeUnknown) State.ComposerUnknown = null;
            _composerSubmission = null;
        }

        public bool CanSwitchAgent()
        {
            var state = State;
            if (_mutation is not null || _agentSwitch is not null || _sessionControl is not null
                || state.SessionUnknown is not null || state.AgentSwitchUnknown is not null
                || state.ComposerUnknown is not null || state.ControlUnknown is not null)
                return false;
            foreach (var status in state.Results.Values)
                if (status == NotificationInteractionStatus.OutcomeUnknown) return false;
            return true;
        }

        public bool CanControlSession() => CanSwitchAgent() && CanSubmitComposer();

        public void BeginSessionControl(Guid operationId, SessionControlKind kind, string targetFingerprint)
        {
            ValidateControl(operationId, kind, targetFingerprint);
            if (!CanControlSession()) throw new InvalidOperationException("Session control protected.");
            State.ControlUnknown = new(operationId, kind, targetFingerprint);
            _sessionControl = operationId;
        }

        public void CompleteSessionControl(Guid operationId, NotificationInteractionStatus status)
        {
            if (_pairedContinuation) throw new InvalidOperationException("Use exact paired continuation settlement.");
            RequireControl(operationId);
            ValidateSettlement(status);
            if (status != NotificationInteractionStatus.OutcomeUnknown) State.ControlUnknown = null;
            _sessionControl = null;
        }

        /// <summary>Atomic same-operation protection for enhanced prompt/command plus implicit revert effects.
        /// Caller already holds this lease and enters captured Source.TryExecute. No self-bypass flag.</summary>
        public void BeginComposerContinuation(Guid operationId, ComposerSubmissionKind kind, string stageFingerprint)
        {
            ValidateControl(operationId, SessionControlKind.RevertContinuation, stageFingerprint);
            if (kind is not (ComposerSubmissionKind.Prompt or ComposerSubmissionKind.Command)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!CanControlSession()) throw new InvalidOperationException("Session control protected.");
            State.ControlUnknown = new(operationId, SessionControlKind.RevertContinuation, stageFingerprint);
            State.ComposerUnknown = new(operationId, kind);
            _sessionControl = _composerSubmission = operationId;
            _pairedContinuation = true;
        }

        /// <summary>Command 204 alone can settle composer acceptance, not prove rollback commitment.
        /// Keep continuationStatus OutcomeUnknown unless exact effects are established by the caller's contract.</summary>
        public void CompleteComposerContinuation(Guid operationId, NotificationInteractionStatus composerStatus,
            NotificationInteractionStatus continuationStatus)
        {
            RequireControl(operationId);
            if (!_pairedContinuation || _composerSubmission != operationId || State.ComposerUnknown?.OperationId != operationId)
                throw new InvalidOperationException("Paired continuation operation mismatch.");
            ValidateSettlement(composerStatus);
            ValidateSettlement(continuationStatus);
            if (composerStatus != NotificationInteractionStatus.OutcomeUnknown) State.ComposerUnknown = null;
            if (continuationStatus != NotificationInteractionStatus.OutcomeUnknown) State.ControlUnknown = null;
            _sessionControl = _composerSubmission = null;
            _pairedContinuation = false;
        }

        private static void ValidateControl(Guid operationId, SessionControlKind kind, string fingerprint)
        {
            ArgumentNullException.ThrowIfNull(fingerprint);
            if (operationId == Guid.Empty || !Enum.IsDefined(kind)) throw new ArgumentException("Invalid session control operation.");
            if (fingerprint.Length != 64) throw new ArgumentException("Expected SHA-256 target/stage fingerprint.", nameof(fingerprint));
            foreach (var ch in fingerprint) if (!Uri.IsHexDigit(ch)) throw new ArgumentException("Invalid fingerprint.", nameof(fingerprint));
        }

        private void RequireControl(Guid operationId)
        {
            if (operationId == Guid.Empty || _sessionControl != operationId || State.ControlUnknown?.OperationId != operationId)
                throw new InvalidOperationException("Session control operation mismatch.");
        }

        private static void ValidateSettlement(NotificationInteractionStatus status)
        {
            if (status is not (NotificationInteractionStatus.Succeeded or NotificationInteractionStatus.Expired
                or NotificationInteractionStatus.Failed or NotificationInteractionStatus.OutcomeUnknown))
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        /// <summary>Caller holds this lease and uses captured Source.TryExecute with complete origin/context
        /// validation to begin AND start the operation synchronously. Gate performs no backend/source validation.</summary>
        public void BeginAgentSwitch(Guid operationId, string targetAgentId)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("Operation ID must not be empty.", nameof(operationId));
            if (!NotificationArgumentCodec.IsValidRequestId(targetAgentId)) throw new ArgumentException("Invalid agent ID.", nameof(targetAgentId));
            if (!CanSwitchAgent()) throw new InvalidOperationException("Session protected; explicit reconciliation required.");
            State.AgentSwitchUnknown = new(operationId, targetAgentId);
            _agentSwitch = operationId;
        }

        public void CompleteAgentSwitch(NotificationInteractionStatus status)
            => CompleteAgentSwitch(_agentSwitch ?? throw new InvalidOperationException("No agent switch started."), status);

        /// <summary>Settle only the captured operation under Source.TryExecute after rechecking full origin.
        /// If revoked, leave unknown. Transport/cancellation/uncertain results MUST use OutcomeUnknown.</summary>
        public void CompleteAgentSwitch(Guid operationId, NotificationInteractionStatus status)
        {
            if (operationId == Guid.Empty || _agentSwitch != operationId || State.AgentSwitchUnknown?.OperationId != operationId)
                throw new InvalidOperationException("Agent switch operation mismatch.");
            if (status is not (NotificationInteractionStatus.Succeeded or NotificationInteractionStatus.Expired
                or NotificationInteractionStatus.Failed or NotificationInteractionStatus.OutcomeUnknown))
                throw new ArgumentOutOfRangeException(nameof(status));
            if (status != NotificationInteractionStatus.OutcomeUnknown) State.AgentSwitchUnknown = null;
            _agentSwitch = null;
        }

        /// <summary>Explicit user-driven complete authoritative reconciliation under captured Source.TryExecute
        /// and full origin validation only; never polling. Closes this exact unknown operation;
        /// any later switch requires a new explicit Begin.</summary>
        public void ResolveAgentSwitchUnknown(Guid operationId)
        {
            if (_mutation is not null || _agentSwitch is not null) throw new InvalidOperationException("Mutation in progress.");
            if (operationId == Guid.Empty || State.AgentSwitchUnknown?.OperationId != operationId)
                throw new InvalidOperationException("Agent switch protection source mismatch.");
            State.AgentSwitchUnknown = null;
        }

        /// <summary>Call immediately before the one POST/DELETE, after trusted fresh-state validation.</summary>
        public void BeginMutation(string requestId, NotificationNavigationKind kind, bool affectsSession = false)
        {
            if (affectsSession && kind != NotificationNavigationKind.Permission)
                throw new InvalidOperationException("Only a Permission reject can protect the whole session.");
            if (_mutation is not null || _agentSwitch is not null || !CanSubmit(requestId, kind)) throw new InvalidOperationException("Request protected; explicit reconciliation required.");
            State.Results[(requestId, kind)] = NotificationInteractionStatus.OutcomeUnknown;
            _mutation = (requestId, kind);
            _affectsSession = affectsSession;
            if (affectsSession) State.SessionUnknown = (requestId, kind);
        }

        public void CompleteMutation(NotificationInteractionStatus status)
        {
            if (_mutation is not { } key) throw new InvalidOperationException("No mutation started.");
            if (status == NotificationInteractionStatus.Succeeded || status == NotificationInteractionStatus.Expired)
                State.Results[key] = status;
            else if (status == NotificationInteractionStatus.Failed)
                State.Results.Remove(key); // Only a definitive rejection, never timeout/transport/5xx.
            else status = NotificationInteractionStatus.OutcomeUnknown;
            if (_affectsSession && status != NotificationInteractionStatus.OutcomeUnknown && State.SessionUnknown == key)
                State.SessionUnknown = null;
            _mutation = null;
        }

        /// <summary>Explicit user-driven reconciliation ONLY, under this lease after a fresh authoritative read.
        /// Never call from polling. allowRetry requires an explicit user decision about possible duplicate effects.
        /// affectsSession requires reconciliation of the entire session after an uncertain reject.</summary>
        public void ResolveUnknown(string requestId, NotificationNavigationKind kind, bool allowRetry, bool affectsSession = false)
        {
            if (_mutation is not null || _agentSwitch is not null) throw new InvalidOperationException("Mutation in progress.");
            if (State.SessionUnknown is not null)
            {
                if (!affectsSession) throw new InvalidOperationException("Whole-session reconciliation required.");
                ResolveSessionUnknown(requestId, kind, allowRetry); // The caller's bool cannot bypass source-key validation.
                return;
            }
            if (State.Results.TryGetValue((requestId, kind), out var result) && result == NotificationInteractionStatus.OutcomeUnknown)
            {
                if (allowRetry) State.Results.Remove((requestId, kind));
                else State.Results[(requestId, kind)] = NotificationInteractionStatus.Expired;
            }
        }

        /// <summary>
        /// Explicit user reconciliation after a complete fresh permission list AND confirmation of possible
        /// whole-session effects. The original reject may no longer be pending; pass GetSessionProtection's
        /// exact source key. Never poll to unlock. Changes only that source result, not other unknown requests.
        /// </summary>
        public void ResolveSessionUnknown(string requestId, NotificationNavigationKind kind, bool allowRetry)
        {
            if (_mutation is not null || _agentSwitch is not null) throw new InvalidOperationException("Mutation in progress.");
            var state = State;
            var key = (requestId, kind);
            if (kind != NotificationNavigationKind.Permission || state.SessionUnknown != key
                || !state.Results.TryGetValue(key, out var result) || result != NotificationInteractionStatus.OutcomeUnknown)
                throw new InvalidOperationException("Session protection source mismatch or no unknown reject to reconcile.");
            if (allowRetry) state.Results.Remove(key);
            else state.Results[key] = NotificationInteractionStatus.Expired;
            state.SessionUnknown = null;
        }

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is null) return;
            state.Mutex.Release(); // Started but uncompleted mutation remains unknown, including session scope.
            lock (_owner._sync)
            {
                state.Holders--;
                _owner.RetireIfInactive((_endpointIdentity, _sessionId), state);
            }
        }
    }

    private void RetireIfInactive((string, string) key, SessionState state)
    {
        if (state.Waiters != 0 || state.Holders != 0 || state.SessionUnknown is not null
            || state.AgentSwitchUnknown is not null || state.ComposerUnknown is not null || state.ControlUnknown is not null
            || state.Results.Count != 0)
            return;
        if (_sessions.TryGetValue(key, out var current) && ReferenceEquals(current, state))
            _sessions.Remove(key);
    }
}
