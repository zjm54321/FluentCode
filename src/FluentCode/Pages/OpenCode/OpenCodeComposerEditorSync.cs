using System;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Pages.OpenCode;

/// <summary>Local input ledger state (phase3-sync-protocol §3).</summary>
internal enum OpenCodeComposerInputState
{
    /// <summary>No unresolved local input; the editor shows (an alias of) the baseline controller text.</summary>
    Clean,
    /// <summary>Recorded user input not yet acknowledged by the controller.</summary>
    Pending,
    /// <summary>Unbound-baseline input the controller could not take yet (no active binding); retried with carry.</summary>
    HeldUnbound,
    /// <summary>Input the controller rejected for its basis. Kept visible/read-only; never retried; explicit discard only.</summary>
    HeldStale,
}

/// <summary>Acknowledged user observation: <paramref name="Revision"/> is the submitted input revision (not a later one).</summary>
internal readonly record struct OpenCodeComposerInputAcknowledgment(long Revision, OpenCodeComposerToken Token, string Text);

// Read-only recovery data, never a pending input, token or edit authority. Selection belongs to the view.
internal readonly record struct OpenCodeComposerEditorRecovery(string Text, OpenCodeComposerInputState State, bool IsComposing);

/// <summary>
/// WinUI-free editor ↔ controller input ledger (phase3-sync-protocol §3). Input is recorded from the editor's
/// synchronous TextChanging only; nothing is inferred from "editor text differs from controller text".
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each genuine change bumps <see cref="InputRevision"/> and is recorded against the DISPLAYED baseline token.
/// Further changes coalesce text/revision but never replace the pending input's ownership (basis/carry).</item>
/// <item>Only the controller acknowledgment returned for that input proves acceptance. A newer reentrant input is
/// rebased solely onto the acknowledgment of its own preceding input, never onto a fresh snapshot token.</item>
/// <item>Carry is requested only for input recorded against an unbound baseline (no origin). A rejected carry
/// input is held while there is no active snapshot and becomes <see cref="OpenCodeComposerInputState.HeldStale"/>
/// once an active controller has refused it; bound input that is rejected is HeldStale immediately.</item>
/// <item>Application writes use <see cref="WriteSnapshot"/> or readonly <see cref="TryPresentRecovery"/>: depth guard, assign, actual read-back.
/// Changes raised during the write are ignored; the read-back is an alias of the controller text, not an edit.</item>
/// <item>Rendering adopts a new token (even with identical text) only while <see cref="OpenCodeComposerInputState.Clean"/>.</item>
/// </list>
/// </remarks>
internal sealed class OpenCodeComposerEditorSync
{
    private sealed record PendingInput(OpenCodeComposerToken Basis, long Revision, string Text, bool CarryAllowed);

    private readonly Func<OpenCodeComposerSnapshot> _source;
    private readonly Func<string?> _readEditor;
    private readonly Action<string> _assignEditor;
    private readonly Func<bool> _isComposing;
    private readonly Func<OpenCodeComposerToken, string, bool, OpenCodeComposerEditorAck?> _apply;

    private OpenCodeComposerToken _baselineToken;
    private bool _baselineBound;
    private string _controllerText = string.Empty;
    private string _editorText = string.Empty;
    private PendingInput? _pending;
    private int _writeDepth;
    private bool _suspendedForRecovery;
    private long _recoveryEpoch;

    /// <param name="apply">Raises the view's TextEdited (single synchronous page handler) and returns its acknowledgment.</param>
    internal OpenCodeComposerEditorSync(Func<OpenCodeComposerSnapshot> source, Func<string?> readEditor,
        Action<string> assignEditor, Func<bool> isComposing,
        Func<OpenCodeComposerToken, string, bool, OpenCodeComposerEditorAck?> apply)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _readEditor = readEditor ?? throw new ArgumentNullException(nameof(readEditor));
        _assignEditor = assignEditor ?? throw new ArgumentNullException(nameof(assignEditor));
        _isComposing = isComposing ?? throw new ArgumentNullException(nameof(isComposing));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        var snapshot = _source();
        _baselineToken = snapshot.Token;
        _baselineBound = snapshot.IsBoundDraft;
        _controllerText = snapshot.Text;
        _editorText = _readEditor() ?? string.Empty;
        // Text already in the editor at attach time is an observation of pre-existing user content (the legacy
        // editor may hold it); it is recorded as input, never silently overwritten and never diff-inferred later.
        if (_editorText != _controllerText) Record(_editorText);
    }

    internal OpenCodeComposerInputState State { get; private set; } = OpenCodeComposerInputState.Clean;
    internal long InputRevision { get; private set; }
    internal bool IsWriting => _writeDepth > 0;
    internal OpenCodeComposerToken BaselineToken => _baselineToken;
    /// <summary>Last actual editor read-back / recorded text (normalised native representation).</summary>
    internal string EditorText => _editorText;
    internal bool HasUnresolvedInput => State != OpenCodeComposerInputState.Clean;
    internal bool IsSuspendedForRecovery => _suspendedForRecovery;

    internal OpenCodeComposerEditorRecovery CaptureRecovery()
        => new(_readEditor() ?? string.Empty, State, _isComposing());

    internal OpenCodeComposerEditorRecovery SuspendForRecovery()
    {
        _recoveryEpoch++;
        var recovery = CaptureRecovery();
        _suspendedForRecovery = true;
        _pending = null;
        _baselineToken = default;
        _baselineBound = true;
        _controllerText = string.Empty;
        _editorText = recovery.Text;
        LastAcknowledgment = null;
        State = OpenCodeComposerInputState.HeldStale;
        return recovery;
    }

    // Presentation only: no snapshot/baseline/input authority is adopted, even from old-epoch saved text.
    internal bool TryPresentRecovery(string text)
    {
        if (!_suspendedForRecovery) return false;
        var epoch = _recoveryEpoch;
        if (_isComposing() || !_suspendedForRecovery || epoch != _recoveryEpoch) return false;
        epoch = ++_recoveryEpoch; // A nested presenter or a new forced hold wins over this presentation.
        _writeDepth++;
        try
        {
            _assignEditor(text);
            if (!_suspendedForRecovery || epoch != _recoveryEpoch) return false;
            var actual = _readEditor() ?? string.Empty;
            if (_isComposing() || !_suspendedForRecovery || epoch != _recoveryEpoch) return false;
            _editorText = actual;
            return true;
        }
        finally { _writeDepth--; }
    }

    // Only after the user has copied recovery or explicitly discarded it. Does not write/adopt any snapshot.
    internal void ResolveRecovery()
    {
        if (!_suspendedForRecovery) return;
        _recoveryEpoch++;
        _suspendedForRecovery = false;
        _pending = null;
        LastAcknowledgment = null;
        State = OpenCodeComposerInputState.Clean;
    }

    internal bool TryActivateClean(OpenCodeComposerToken activated)
    {
        if (_suspendedForRecovery || State != OpenCodeComposerInputState.Clean || _isComposing()) return false;
        var snapshot = _source();
        if (snapshot.Token != activated || !snapshot.IsActive) return false;
        LastAcknowledgment = null;
        WriteSnapshot(snapshot);
        return !_suspendedForRecovery && State == OpenCodeComposerInputState.Clean
            && _baselineToken == activated && _source().Token == activated;
    }
    /// <summary>
    /// Latest controller acknowledgment of RECORDED user input: the submitted input revision plus the accepted token/text.
    /// Guarded application writes never set it, so programmatic echoes are not user observations. Read-only presentation seam.
    /// </summary>
    internal OpenCodeComposerInputAcknowledgment? LastAcknowledgment { get; private set; }

    /// <summary>Editor TextChanging (synchronous; Text already holds the new value). Records only.</summary>
    internal void OnTextChanging()
    {
        if (_writeDepth > 0 || _suspendedForRecovery) return; // No recovery text gets a new basis.
        Record(_readEditor() ?? string.Empty);
    }

    private void Record(string text)
    {
        InputRevision++;
        _editorText = text;
        if (_pending is null)
        {
            _pending = new(_baselineToken, InputRevision, text, CarryAllowed: !_baselineBound);
            State = OpenCodeComposerInputState.Pending;
        }
        else
        {
            _pending = _pending with { Revision = InputRevision, Text = text }; // Ownership unchanged.
        }
    }

    /// <summary>Flush recorded input only (never inferred). Clears only the revision it submitted.</summary>
    internal OpenCodeComposerInputState Flush()
    {
        if (_suspendedForRecovery) return State;
        for (var round = 0; round < 8; round++)
        {
            if (State is OpenCodeComposerInputState.Clean or OpenCodeComposerInputState.HeldStale) return State;
            var submitted = _pending!;
            var ack = _apply(submitted.Basis, submitted.Text, submitted.CarryAllowed);
            if (_suspendedForRecovery) return State; // Forced invalidation may run reentrantly during publication.
            if (ack is not { } accepted)
            {
                var pending = _pending!; // May have coalesced reentrantly; ownership is unchanged either way.
                State = pending.CarryAllowed && !_source().IsActive
                    ? OpenCodeComposerInputState.HeldUnbound
                    : OpenCodeComposerInputState.HeldStale;
                return State;
            }
            _baselineToken = accepted.Token;
            _controllerText = accepted.Text;
            // Observation seam only: the acknowledgment belongs to the SUBMITTED revision, never to a later global one.
            LastAcknowledgment = new(submitted.Revision, accepted.Token, accepted.Text);
            if (_pending!.Revision == submitted.Revision)
            {
                _pending = null;
                State = OpenCodeComposerInputState.Clean;
                return State;
            }
            // Newer reentrant observation: rebase ONLY onto its own predecessor's acknowledgment.
            _pending = _pending with { Basis = accepted.Token };
            State = OpenCodeComposerInputState.Pending;
        }
        return State;
    }

    /// <summary>
    /// Render-time reconciliation: flush, then (only when Clean) adopt the current token or write the current
    /// controller text through the guard. Returns true when a needed write is deferred by an active composition
    /// (the caller re-renders on composition end; no timer).
    /// </summary>
    internal bool Reconcile()
    {
        if (_isComposing()) return true;
        if (Flush() != OpenCodeComposerInputState.Clean) return false;
        var snapshot = _source();
        if (snapshot.Text == _controllerText)
        {
            _baselineToken = snapshot.Token;
            _baselineBound = snapshot.IsBoundDraft;
            return false;
        }
        if (_isComposing()) return true;
        WriteSnapshot(snapshot);
        return false;
    }

    /// <summary>The single guarded application write: depth guard, assign, actual read-back, token association.</summary>
    internal void WriteSnapshot(OpenCodeComposerSnapshot snapshot)
    {
        // Enforce this at the writer, not only at Reconcile/Prepare: activation and accepted-draft
        // handoffs use this entry too. Even an identical Text assignment resets native IME state.
        if (_suspendedForRecovery || _isComposing()) return;
        _writeDepth++;
        try { _assignEditor(snapshot.Text); }
        finally { _writeDepth--; }
        if (_suspendedForRecovery) return;
        _editorText = _readEditor() ?? string.Empty;
        _controllerText = snapshot.Text;
        _baselineToken = snapshot.Token;
        _baselineBound = snapshot.IsBoundDraft;
    }

    /// <summary>UI-thread submission gate: no composition, all recorded input acknowledged, editor reconciled.</summary>
    internal bool Prepare()
    {
        if (_suspendedForRecovery || _isComposing()) return false;
        var deferred = Reconcile();
        return !deferred && State == OpenCodeComposerInputState.Clean && _source().Token == _baselineToken;
    }

    /// <summary>
    /// One synchronous UI turn of the accepted-draft handoff (§4): flush → held check → composition wait →
    /// finalize → guarded write. No await or callback between the flush, finalize and the editor baseline.
    /// </summary>
    /// <returns>The finalization result, or null when finalize was not invoked (held input or waiting).</returns>
    internal OpenCodeComposerDraftFinalization? FinalizeAccepted(OpenCodeComposerToken submittedToken,
        Func<OpenCodeComposerDraftFinalization> finalize, out bool waitForComposition)
    {
        waitForComposition = false;
        if (Flush() != OpenCodeComposerInputState.Clean) return null; // Unresolved input: never clear.
        if (_isComposing() && _source().Token == submittedToken)
        {
            waitForComposition = true;
            return null;
        }
        var result = finalize();
        if (result == OpenCodeComposerDraftFinalization.Cleared) WriteSnapshot(_source());
        return result;
    }
}

/// <summary>
/// UI accepted-draft handoff coordinator implementing <see cref="OpenCodeComposerAcceptedDraftHandoff"/> semantics:
/// immutable handoff ID + lifetime generation captured when queued; composition end only wakes a recheck;
/// unload/reattachment/lifetime cancellation abandon local clearing and complete the task. Never faults.
/// </summary>
internal sealed class OpenCodeComposerAcceptedDraftHandoffs
{
    private sealed class Handoff(long id, int generation, OpenCodeComposerToken submitted,
        Func<OpenCodeComposerDraftFinalization> finalize, CancellationToken lifetime)
    {
        internal readonly long Id = id;
        internal readonly int Generation = generation;
        internal readonly OpenCodeComposerToken Submitted = submitted;
        internal readonly Func<OpenCodeComposerDraftFinalization> Finalize = finalize;
        internal readonly CancellationToken Lifetime = lifetime;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal int Closed;
        internal bool FinalizeUsed;
        internal bool Waiting;
        internal OpenCodeComposerDraftFinalization? Result;
    }

    private readonly OpenCodeComposerViewLifetime _lifetime;
    private readonly Func<OpenCodeComposerEditorSync?> _sync;
    private readonly object _gate = new();
    private long _ids;
    private Handoff? _current; // Latest begun handoff (queued or waiting); guarded by _gate.

    internal OpenCodeComposerAcceptedDraftHandoffs(OpenCodeComposerViewLifetime lifetime, Func<OpenCodeComposerEditorSync?> sync)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _lifetime.Invalidated += CancelPending;
    }

    /// <summary>Diagnostics/tests: last finalize result of the most recently completed handoff.</summary>
    internal OpenCodeComposerDraftFinalization? LastResult { get; private set; }
    internal bool IsWaitingForComposition { get { lock (_gate) return _current is { Waiting: true }; } }

    /// <summary>Any thread. Dispatches to the UI thread with the generation captured now.</summary>
    internal Task Begin(OpenCodeComposerToken submittedToken, Func<OpenCodeComposerDraftFinalization> finalize,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(finalize);
        var handoff = new Handoff(Interlocked.Increment(ref _ids), _lifetime.Generation, submittedToken, finalize, lifetimeToken);
        if (lifetimeToken.IsCancellationRequested || !_lifetime.IsCurrent(handoff.Generation))
        {
            Close(handoff);
            return handoff.Completion.Task;
        }
        Handoff? superseded;
        lock (_gate)
        {
            superseded = _current;
            _current = handoff; // Unload/reattach can now cancel it even before its step runs.
        }
        if (superseded is not null) Close(superseded);
        handoff.Registration = lifetimeToken.Register(static state => Close((Handoff)state!), handoff);
        if (!_lifetime.TryEnqueue(() => Step(handoff))) Close(handoff); // Failed dispatch abandons clearing.
        return handoff.Completion.Task;
    }

    /// <summary>UI thread. Composition ended / focus lost: a wake-up that re-runs every check, never authorization.</summary>
    internal void Wake()
    {
        Handoff? handoff;
        lock (_gate)
        {
            handoff = _current;
            if (handoff is not { Waiting: true }) return;
            handoff.Waiting = false;
        }
        if (!_lifetime.TryEnqueue(() => Step(handoff))) Close(handoff);
    }

    /// <summary>UI thread. Unload/reattachment cancels the pending handoff directly.</summary>
    internal void CancelPending()
    {
        Handoff? handoff;
        lock (_gate) { handoff = _current; _current = null; }
        if (handoff is not null) Close(handoff);
    }

    private void Step(Handoff handoff)
    {
        if (Volatile.Read(ref handoff.Closed) != 0) return;
        if (handoff.Lifetime.IsCancellationRequested || !_lifetime.IsCurrent(handoff.Generation) || _sync() is not { } sync)
        {
            Close(handoff);
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_current, handoff)) { Close(handoff); return; } // A newer handoff owns the editor.
        }
        OpenCodeComposerDraftFinalization? result = null;
        bool wait;
        try
        {
            result = sync.FinalizeAccepted(handoff.Submitted, () =>
            {
                if (handoff.FinalizeUsed) return OpenCodeComposerDraftFinalization.AlreadyFinalized;
                handoff.FinalizeUsed = true; // Single use from this side as well.
                return handoff.Finalize();
            }, out wait);
        }
        catch (Exception)
        {
            wait = false; // Never fault the controller's await; settlement is already final.
        }
        if (wait)
        {
            lock (_gate) if (ReferenceEquals(_current, handoff) && Volatile.Read(ref handoff.Closed) == 0) { handoff.Waiting = true; return; }
            Close(handoff); // Cancelled during the step.
            return;
        }
        handoff.Result = result;
        LastResult = result;
        Close(handoff);
        lock (_gate) if (ReferenceEquals(_current, handoff)) _current = null;
    }

    private static void Close(Handoff handoff)
    {
        if (Interlocked.Exchange(ref handoff.Closed, 1) != 0) return;
        handoff.Waiting = false;
        handoff.Registration.Unregister();
        handoff.Completion.TrySetResult();
    }
}
