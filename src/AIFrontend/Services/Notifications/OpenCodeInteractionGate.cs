using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Notifications;

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
        internal (string RequestId, NotificationNavigationKind Kind)? SessionUnknown;
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
        }
        await state.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new(state);
    }

    public sealed class Lease : IDisposable
    {
        private SessionState? _state;
        private (string, NotificationNavigationKind)? _mutation;
        private bool _affectsSession;
        internal Lease(SessionState state) => _state = state;
        private SessionState State => _state ?? throw new ObjectDisposedException(nameof(Lease));

        public NotificationInteractionStatus? GetProtectionStatus(string requestId, NotificationNavigationKind kind)
        {
            if (State.SessionUnknown is not null) return NotificationInteractionStatus.OutcomeUnknown;
            if (State.Results.TryGetValue((requestId, kind), out var status))
                return status == NotificationInteractionStatus.Succeeded ? NotificationInteractionStatus.Duplicate : status;
            return State.Results.Count >= 256 ? NotificationInteractionStatus.NeedsApp : null;
        }

        public bool CanSubmit(string requestId, NotificationNavigationKind kind) => GetProtectionStatus(requestId, kind) is null;

        public (string RequestId, NotificationNavigationKind Kind)? GetSessionProtection() => State.SessionUnknown;

        /// <summary>Call immediately before the one POST/DELETE, after trusted fresh-state validation.</summary>
        public void BeginMutation(string requestId, NotificationNavigationKind kind, bool affectsSession = false)
        {
            if (affectsSession && kind != NotificationNavigationKind.Permission)
                throw new InvalidOperationException("Only a Permission reject can protect the whole session.");
            if (_mutation is not null || !CanSubmit(requestId, kind)) throw new InvalidOperationException("Request protected; explicit reconciliation required.");
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
            if (_mutation is not null) throw new InvalidOperationException("Mutation in progress.");
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
            if (_mutation is not null) throw new InvalidOperationException("Mutation in progress.");
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
            state?.Mutex.Release(); // Started but uncompleted mutation remains unknown, including session scope.
        }
    }
}
