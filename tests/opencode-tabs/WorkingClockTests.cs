// B13: pure tests for the REAL shared working clock (src/AIFrontend/Pages/OpenCode/OpenCodeWorkingClock.cs).
// Time is an injected monotonic counter; no UI, no network.
using System;
using System.Collections.Generic;
using AIFrontend.Pages.OpenCode;

internal static class WorkingClockTests
{
    private static int checks;
    private static void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("B13 clock: " + name); }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private sealed class Probe : IOpenCodeWorkingClockSubscriber
    {
        internal int Notified;
        internal readonly List<double> SeenPhases = new();
        public void OnWorkingClockStateChanged(OpenCodeWorkingClock clock) { Notified++; SeenPhases.Add(clock.Phase); }
    }

    internal static void Run()
    {
        long now = 0;
        const long frequency = 1000; // 1 tick = 1 ms
        var clock = new OpenCodeWorkingClock(() => now, frequency, TimeSpan.FromMilliseconds(1000));
        var transitions = new List<bool>();
        clock.RunningChanged += transitions.Add;

        Check(!clock.IsRunning && clock.SubscriberCount == 0 && clock.StartCount == 0, "idle clock is stopped before any working tab");

        // Phase is absolute (monotonic time mod period): a first joiner mid-period starts there, not at 0.
        now = 12_250;
        var a = new Probe();
        Check(clock.Join(a) && clock.IsRunning && clock.StartCount == 1 && transitions.Count == 1 && transitions[0], "first working tab starts the clock once");
        Check(a.Notified == 1 && Near(a.SeenPhases[0], 0.25), "first joiner lands on the global phase (0.25), not phase 0");

        // Join later: joiner sees the current global phase; existing subscriber untouched; no restart.
        now = 12_900;
        var b = new Probe();
        clock.Join(b);
        Check(Near(b.SeenPhases[0], 0.9) && Near(clock.Phase, 0.9), "late joiner joins current phase 0.9 instead of restarting");
        Check(a.Notified == 1 && clock.StartCount == 1 && clock.StopCount == 0, "adding a working tab never notifies/resets other tabs or restarts the clock");
        Check(!clock.Join(b) && b.Notified == 1, "duplicate join is idempotent");

        // Same instant → same phase for everyone (single source).
        Check(Near(clock.PhaseAt(now), clock.Phase), "all subscribers read one phase source");

        // Leaving does not disturb remaining subscribers.
        now = 13_100;
        Check(clock.Leave(b) && clock.IsRunning && a.Notified == 1 && clock.StopCount == 0, "removing a working tab never resets the others");
        Check(Near(clock.Phase, 0.1), "phase keeps flowing on the global timeline after a removal");

        // Last leave (tab done or tab strip unloaded) stops the clock completely.
        Check(clock.Leave(a) && !clock.IsRunning && clock.SubscriberCount == 0 && clock.StopCount == 1 && transitions.Count == 2 && !transitions[1], "zero working/loaded tabs stops the clock");
        Check(!clock.Leave(a) && clock.StopCount == 1, "duplicate leave is idempotent and does not double-stop");

        // Restart after a full stop rejoins the same absolute timeline (not a fresh 0).
        now = 20_600;
        var c = new Probe();
        clock.Join(c);
        Check(clock.IsRunning && clock.StartCount == 2 && Near(c.SeenPhases[0], 0.6), "restart resumes on the absolute global phase");

        // Reduced motion: static soft indicator; clock stops even with subscribers; all are told to go static.
        var d = new Probe();
        clock.Join(d);
        var cBefore = c.Notified;
        clock.SetMotionEnabled(false);
        Check(!clock.MotionEnabled && !clock.IsRunning && clock.StopCount == 2 && c.Notified == cBefore + 1 && d.Notified == 2, "reduced motion stops the clock and switches every indicator to static");
        var e = new Probe();
        clock.Join(e);
        Check(!clock.IsRunning && clock.StartCount == 2 && e.Notified == 1, "joining under reduced motion stays static and does not start frames");
        clock.SetMotionEnabled(false);
        Check(clock.StopCount == 2 && e.Notified == 1, "repeated reduced-motion setting is a no-op");
        clock.SetMotionEnabled(true);
        Check(clock.IsRunning && clock.StartCount == 3 && e.Notified == 2, "re-enabling animations resumes on the shared clock");
        clock.Leave(c); clock.Leave(d); clock.Leave(e);
        Check(!clock.IsRunning && clock.StopCount == 3, "all leave again → stopped");

        // Width-normalized relative phase: different tab widths stay in step.
        const double s = 0.38;
        foreach (var phase in new[] { 0.0, 0.17, 0.5, 0.83, 0.999 })
        {
            var narrow = OpenCodeWorkingClock.HighlightOffset(phase, 80, s) / 80;
            var wide = OpenCodeWorkingClock.HighlightOffset(phase, 260, s) / 260;
            Check(Near(narrow, wide), "normalized offset independent of width at phase " + phase);
        }
        Check(Near(OpenCodeWorkingClock.HighlightOffset(0, 100, s), -s * 100) && Near(OpenCodeWorkingClock.HighlightOffset(1, 100, s), 100), "sweep enters fully from the left and exits fully to the right (one direction)");
        Check(OpenCodeWorkingClock.HighlightOffset(0.3, 100, s) < OpenCodeWorkingClock.HighlightOffset(0.6, 100, s), "monotonic left→right direction");

        // Negative / wrapped timestamps stay in [0,1).
        Check(Near(clock.PhaseAt(-250), 0.75) && Near(clock.PhaseAt(1000), 0), "phase wraps into [0,1)");
        Check(OpenCodeWorkingClock.Shared.Period == OpenCodeWorkingClock.DefaultPeriod, "shared clock uses the single default period");

        Console.WriteLine($"PASS B13 shared working clock: {checks} assertions (absolute phase join, no reset on add/remove, stop at zero, reduced motion static, width-normalized)");
    }
}
