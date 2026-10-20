using System;
using System.Threading;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Immutable, atomically published snapshot of the composer's EXISTING input ledger (OpenCodeComposerEditorSync) plus
/// view lifetime and IME provenance. It contains no text, files or secrets and grants no permission: source/draft
/// ownership stays with Core/Page. Safe to read from any thread (a single reference read).
/// </summary>
/// <param name="Sequence">Observation sequence of this view instance. Bumps on every REAL input observation (user
/// TextChanging), every composition start/end and every lifetime change (attach/reattach/unload/resume). Flush/reconcile
/// publications and guarded programmatic writes do not bump it. Strict start capture compares it exactly.</param>
/// <param name="Available">A live view with an attached editor ledger.</param>
/// <param name="LifetimeGeneration">The view lifetime generation (diagnostic; also changes on attach/unload/resume).</param>
/// <param name="InputRevision">The ledger's existing InputRevision (restarts at reattach; never compare it alone).</param>
/// <param name="State">The ledger state at publication: Clean / Pending / HeldUnbound / HeldStale.</param>
/// <param name="Composing">An IME composition is active in the attached editor.</param>
/// <param name="CompositionEpoch">Bumps on every composition start AND end (a start→end without input still counts).</param>
internal sealed record OpenCodeComposerInputEvidence(long Sequence, bool Available, int LifetimeGeneration,
    long InputRevision, OpenCodeComposerInputState State, bool Composing, long CompositionEpoch)
{
    internal static readonly OpenCodeComposerInputEvidence Unavailable =
        new(0, false, 0, 0, OpenCodeComposerInputState.Pending, false, 0);

    /// <summary>Clean, idle and live: the only state a strict start may be captured in (after a clean Prepare*).</summary>
    internal bool IsClean => Available && State == OpenCodeComposerInputState.Clean && !Composing;

    /// <summary>
    /// Strict start check: <paramref name="current"/> is still exactly the clean observation captured earlier. Any real
    /// edit, IME transition or lifetime change since the capture fails it permanently — returning to the same text or
    /// composition state cannot re-authorise an old capture (the sequence is monotonic per view).
    /// </summary>
    internal static bool StillCurrent(OpenCodeComposerInputEvidence captured, OpenCodeComposerInputEvidence current)
        => captured is not null && current is not null && captured.IsClean && current.IsClean
            && captured.Sequence == current.Sequence && captured.LifetimeGeneration == current.LifetimeGeneration
            && captured.InputRevision == current.InputRevision && captured.CompositionEpoch == current.CompositionEpoch;
}

/// <summary>
/// WinUI-free publisher used by the composer view's handlers (UI thread). It never reads XAML, App, controller or
/// clipboard and never reconciles; it only wraps the existing ledger's observation entry and republishes C# data.
/// Real input is detected solely by the ledger's own guard (<see cref="OpenCodeComposerEditorSync.IsWriting"/> /
/// InputRevision change), so guarded programmatic writes and their alias read-backs are never counted.
/// </summary>
internal sealed class OpenCodeComposerInputEvidenceSource
{
    private readonly Func<OpenCodeComposerEditorSync?> _sync;
    private readonly Func<bool> _live;
    private readonly Func<int> _generation;
    private readonly Func<bool> _composing;
    private long _sequence;
    private long _compositionEpoch;
    private OpenCodeComposerInputEvidence _current = OpenCodeComposerInputEvidence.Unavailable;

    internal OpenCodeComposerInputEvidenceSource(Func<OpenCodeComposerEditorSync?> sync, Func<bool> live,
        Func<int> generation, Func<bool> composing)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _generation = generation ?? throw new ArgumentNullException(nameof(generation));
        _composing = composing ?? throw new ArgumentNullException(nameof(composing));
    }

    /// <summary>Pure atomic read of the last published evidence (any thread).</summary>
    internal OpenCodeComposerInputEvidence Current => Volatile.Read(ref _current);

    /// <summary>
    /// Editor TextChanging (UI thread, synchronous): the ONLY observation entry. For a real (unguarded) change the
    /// evidence is invalidated BEFORE the ledger records it, then republished; a guarded write records nothing and
    /// publishes nothing new. C# only — no visual tree work.
    /// </summary>
    internal void RecordTextChanging()
    {
        var sync = _sync();
        if (sync is null) return;
        if (sync.IsWriting) { sync.OnTextChanging(); return; } // Guarded programmatic write: ignored by the ledger too.
        Interlocked.Increment(ref _sequence);
        Publish(conservativePending: true); // A concurrent reader never sees the old clean capture as current.
        sync.OnTextChanging();               // The existing ledger records (InputRevision++, Pending).
        Publish(conservativePending: false);
    }

    /// <summary>Composition start or end (also LostFocus ending a composition). Always a new provenance step.</summary>
    internal void CompositionChanged()
    {
        Interlocked.Increment(ref _compositionEpoch);
        Interlocked.Increment(ref _sequence);
        Publish(conservativePending: false);
    }

    /// <summary>Attach/reattach/unload/resume: old captures can never match again; availability follows the lifetime.</summary>
    internal void LifetimeChanged()
    {
        Interlocked.Increment(ref _sequence);
        Publish(conservativePending: false);
    }

    /// <summary>After flush/reconcile/discard: republish the ledger state without a new observation.</summary>
    internal void Refresh() => Publish(conservativePending: false);

    private void Publish(bool conservativePending)
    {
        var sync = _sync();
        var live = _live();
        var evidence = new OpenCodeComposerInputEvidence(Interlocked.Read(ref _sequence), live && sync is not null,
            _generation(), sync?.InputRevision ?? 0,
            conservativePending || sync is null ? OpenCodeComposerInputState.Pending : sync.State,
            _composing(), Interlocked.Read(ref _compositionEpoch));
        Volatile.Write(ref _current, evidence);
    }
}
