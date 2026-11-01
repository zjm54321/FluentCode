using System;

namespace FluentCode.Pages.OpenCode;

/// <summary>UI-thread scheduling seam (production: the view's DispatcherQueue adapter).</summary>
internal interface IOpenCodeComposerUiScheduler
{
    /// <summary>Queue <paramref name="callback"/> on the UI thread; false when the dispatcher is shutting down.</summary>
    bool TryEnqueue(Action callback);
}

/// <summary>
/// WinUI-free lifetime fence for the composer view: coalesced render requests and generation invalidation on
/// actual unload / editor reattachment. There is deliberately NO timer or polling of any kind.
/// </summary>
/// <remarks>
/// <para>A newly constructed view is live although never loaded, so constructor-first hookup (AttachEditor before
/// the host is in the tree) still renders. Only an Unloaded that balances every Loaded makes it unloaded
/// (WinUI may raise a re-parent's Loaded before the old Unloaded); a later Loaded resumes it.</para>
/// <para>Unload, resume and reattachment bump the generation. Queued renders capture the generation when queued
/// and are dropped on mismatch. <see cref="Invalidated"/> lets owners cancel their own captured work directly
/// (it is raised on the UI thread for unload/reattach; never from a worker thread).</para>
/// </remarks>
internal sealed class OpenCodeComposerViewLifetime
{
    private readonly object _sync = new();
    private readonly IOpenCodeComposerUiScheduler _scheduler;
    private readonly Action _render;
    private int _generation;
    private int _loads;
    private bool _unloaded;
    private bool _renderQueued;

    internal OpenCodeComposerViewLifetime(IOpenCodeComposerUiScheduler scheduler, Action render)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    /// <summary>Raised (UI thread) after the generation was bumped by unload or reattachment.</summary>
    internal event Action? Invalidated;

    internal bool IsLive { get { lock (_sync) return !_unloaded; } }
    internal int Generation { get { lock (_sync) return _generation; } }

    /// <summary>True when <paramref name="generation"/> is still current and the view is live.</summary>
    internal bool IsCurrent(int generation) { lock (_sync) return !_unloaded && generation == _generation; }

    /// <summary>Raw UI-thread queueing for owners that capture their own immutable generation/IDs.</summary>
    internal bool TryEnqueue(Action callback) => _scheduler.TryEnqueue(callback);

    /// <summary>Thread-safe, coalesced per generation. Dropped while unloaded (resume renders anyway).</summary>
    internal void RequestRender()
    {
        int generation;
        lock (_sync)
        {
            if (_unloaded || _renderQueued) return;
            _renderQueued = true;
            generation = _generation;
        }
        if (!_scheduler.TryEnqueue(() => RunRender(generation)))
        {
            lock (_sync) if (_generation == generation) _renderQueued = false; // Dispatcher shutting down.
        }
    }

    private void RunRender(int generation)
    {
        lock (_sync)
        {
            if (generation != _generation || _unloaded) return; // Stale: the bump already cleared the flag.
            _renderQueued = false;
        }
        _render();
    }

    /// <summary>FrameworkElement.Loaded. Resumes (new generation + one render) only after an actual unload.</summary>
    internal void OnLoaded()
    {
        lock (_sync)
        {
            _loads++;
            if (!_unloaded) return;
            _unloaded = false;
            BumpLocked();
        }
        RequestRender();
    }

    /// <summary>FrameworkElement.Unloaded. Invalidates every queued callback and cancels owners' captured work.</summary>
    internal void OnUnloaded()
    {
        lock (_sync)
        {
            _loads = Math.Max(0, _loads - 1);
            if (_loads > 0 || _unloaded) return; // Out-of-order re-parent: a newer Loaded is still standing.
            _unloaded = true;
            BumpLocked();
        }
        Invalidated?.Invoke();
    }

    /// <summary>AttachEditor. Fences callbacks captured for the previous editor, then renders the new one.</summary>
    internal void OnEditorAttached()
    {
        lock (_sync) BumpLocked();
        Invalidated?.Invoke();
        RequestRender();
    }

    private void BumpLocked()
    {
        _generation++;
        _renderQueued = false;
    }
}
