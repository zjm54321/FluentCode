using System;

// Production UpdateTimer/Dispose bodies are extracted by run.ps1. These event sources
// deliberately keep strong delegates, like DispatcherQueueTimer and the shared clock.
namespace TimelineLifetime;

internal sealed class DispatcherQueueTimer
{
    internal event Action<DispatcherQueueTimer, object>? Tick;
    internal int HandlerCount => Tick?.GetInvocationList().Length ?? 0;
    internal bool IsRunning { get; private set; }
    internal bool IsRepeating { get; set; }
    internal TimeSpan Interval { get; set; }
    internal void Start() => IsRunning = true;
    internal void Stop() => IsRunning = false;
    internal void Fire() => Tick?.Invoke(this, new object());
}

internal sealed class DispatcherQueue
{
    internal static DispatcherQueue GetForCurrentThread() => new();
    internal DispatcherQueueTimer CreateTimer() => new();
}

internal sealed class FakeClock
{
    internal bool HostVisible { get; private set; } = true;
    internal event Action<bool>? HostVisibilityChanged;
    internal int HandlerCount => HostVisibilityChanged?.GetInvocationList().Length ?? 0;
    internal void SetVisible(bool visible) { HostVisible = visible; HostVisibilityChanged?.Invoke(visible); }
}

internal static class OpenCodeWorkingClock
{
    internal static FakeClock Shared { get; } = new();
}

internal sealed class FakeWorking
{
    internal bool IsActive { get; set; } = true;
    internal bool IsVisibleWhileLoaded { get; set; } = true;
    internal bool Disposed { get; private set; }
    internal event Action? VisibilityChanged;
    internal int HandlerCount => VisibilityChanged?.GetInvocationList().Length ?? 0;
    internal void Notify() => VisibilityChanged?.Invoke();
    internal void Dispose() { Disposed = true; IsActive = false; Notify(); }
}

internal sealed class FakePhrases
{
    internal bool Stopped { get; private set; }
    internal void Tick(double now) { }
    internal void Stop() => Stopped = true;
}

internal sealed partial class GroupLifecycleProbe
{
    private DispatcherQueueTimer? _timer;
    private bool _disposed, _loaded = true;
    private readonly FakeWorking _working = new();
    private readonly FakePhrases _phrases = new();
    private int _refreshes;
    private static double NowMs() => 0;
    private void RefreshWorkingText() => _refreshes++;

    internal GroupLifecycleProbe()
    {
        // Source guards separately pin these exact production constructor subscriptions.
        _working.VisibilityChanged += UpdateTimer;
        OpenCodeWorkingClock.Shared.HostVisibilityChanged += HostVisibilityChanged;
    }

    internal static void Exercise(Action<bool, string> check)
    {
        var unused = new GroupLifecycleProbe();
        unused.Dispose();
        check(unused._timer is null && unused._working.HandlerCount == 0 && OpenCodeWorkingClock.Shared.HandlerCount == 0,
            "executed disposal before first tick removes visibility subscriptions without creating a timer");

        var probe = new GroupLifecycleProbe();
        probe.UpdateTimer();
        var timer = probe._timer!;
        for (var i = 0; i < 100; i++) probe.UpdateTimer();
        check(ReferenceEquals(timer, probe._timer) && timer.HandlerCount == 1 && timer.IsRunning
            && timer.IsRepeating && timer.Interval == TimeSpan.FromSeconds(1),
            "executed repeated updates reuse the timer and its single named handler");
        OpenCodeWorkingClock.Shared.SetVisible(false);
        check(!timer.IsRunning, "executed hidden-host policy stops the timer");
        OpenCodeWorkingClock.Shared.SetVisible(true);
        check(timer.IsRunning, "executed restored-host policy restarts the same timer");
        probe.Dispose();
        probe.Dispose();
        check(!timer.IsRunning && timer.HandlerCount == 0 && probe._timer is null,
            "executed disposal stops, detaches and releases the timer, including repeated disposal");
        check(probe._working.HandlerCount == 0 && OpenCodeWorkingClock.Shared.HandlerCount == 0
            && probe._working.Disposed && probe._phrases.Stopped,
            "executed disposal removes both visibility subscriptions and disposes working state");
        var refreshes = probe._refreshes;
        timer.Fire();
        probe._working.Notify();
        OpenCodeWorkingClock.Shared.SetVisible(false);
        probe.UpdateTimer();
        OpenCodeWorkingClock.Shared.SetVisible(true);
        check(probe._timer is null && probe._refreshes == refreshes,
            "disposed group cannot receive stale timer/visibility callbacks or recreate its timer");
    }
}

internal static class CompositionTarget
{
    internal static event Action<object?, object>? Rendering;
    internal static int HandlerCount => Rendering?.GetInvocationList().Length ?? 0;
    internal static void Render() => Rendering?.Invoke(null, new object());
}

internal sealed partial class WindowFrameProbe
{
    private bool _windowQueued, _windowFrameSubscribed, _updatingWindow;
    private int _windowGeneration, _queuedWindowGeneration, _updates;
    private readonly object _viewport = new();
    private bool IsLoaded { get; set; } = true;
    private void UpdateWindow() => _updates++;

    internal static void Exercise(Action<bool, string> check)
    {
        var probe = new WindowFrameProbe();
        for (var i = 0; i < 100; i++) probe.QueueWindowUpdate();
        check(CompositionTarget.HandlerCount == 1, "executed frame queue installs one bounded named static-event handler");
        CompositionTarget.Render();
        check(CompositionTarget.HandlerCount == 0 && probe._updates == 1 && !probe._windowQueued,
            "executed frame callback removes its static-event root before updating the window");
        probe.QueueWindowUpdate();
        probe._windowGeneration++;
        CompositionTarget.Render();
        check(CompositionTarget.HandlerCount == 0 && probe._updates == 1,
            "executed stale frame releases its subscription without rebuilding");
        probe.QueueWindowUpdate();
        probe.CancelWindowFrame();
        probe.CancelWindowFrame();
        check(CompositionTarget.HandlerCount == 0 && !probe._windowFrameSubscribed && !probe._windowQueued,
            "executed cancellation releases the frame root idempotently");
        probe.IsLoaded = false;
        probe.QueueWindowUpdate();
        probe.IsLoaded = true;
        probe._updatingWindow = true;
        probe.QueueWindowUpdate();
        check(CompositionTarget.HandlerCount == 0, "executed unloaded/updating gates cannot install frame handlers");
    }
}
