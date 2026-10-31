using System;
using System.IO;
using System.Linq;
using AIFrontend.Motion;

// Offline tests for the Fluent motion tokens, the reduce-motion policy and the generation gate
// (src/AIFrontend/Motion/FluentMotionPolicy.cs), plus static guards over the WinUI execution layer.
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static void Check(bool condition, string name)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.Error.WriteLine("FAIL " + name);
    }

    private static int Main(string[] args)
    {
        Tokens();
        SlowMotion();
        Policy();
        ShortCircuit();
        Generations();
        if (args.Length > 0) Static(args[0]);
        if (_failures > 0)
        {
            Console.Error.WriteLine($"{_failures} of {_checks} fluent-motion checks failed");
            return 1;
        }
        Console.WriteLine($"PASS {_checks} fluent-motion token, reduce-motion short-circuit, generation and static checks");
        return 0;
    }

    private static void Tokens()
    {
        Check(FluentMotionTokens.FasterMs == 83, "Faster = 83ms");
        Check(FluentMotionTokens.FastMs == 167, "Fast = 167ms");
        Check(FluentMotionTokens.NormalMs == 250, "Normal = 250ms");
        Check(FluentMotionTokens.SlowMs == 333, "Slow = 333ms");
        Check(FluentMotionTokens.SlowerMs == 500, "Slower = 500ms");
        Check(FluentMotionTokens.Decelerate == new MotionCurve(0f, 0f, 0f, 1f), "enter = cubic-bezier(0,0,0,1)");
        Check(FluentMotionTokens.Accelerate == new MotionCurve(1f, 0f, 1f, 1f), "exit = cubic-bezier(1,0,1,1)");
        Check(FluentMotionTokens.PointToPoint == new MotionCurve(0.55f, 0.55f, 0f, 1f), "point-to-point = cubic-bezier(0.55,0.55,0,1)");
        var all = new[] { FluentMotionTokens.Decelerate, FluentMotionTokens.Accelerate, FluentMotionTokens.PointToPoint };
        Check(all.All(c => c.X1 is >= 0 and <= 1 && c.X2 is >= 0 and <= 1), "bezier x control points stay in [0,1] (valid timing function)");
    }

    private static void SlowMotion()
    {
        Check(FluentMotionTokens.ParseSlowMotion(null) == 1, "slow-mo unset = 1");
        Check(FluentMotionTokens.ParseSlowMotion(" ") == 1, "slow-mo blank = 1");
        Check(FluentMotionTokens.ParseSlowMotion("5") == 5, "slow-mo 5");
        Check(FluentMotionTokens.ParseSlowMotion("2.5") == 2.5, "slow-mo invariant decimal");
        Check(FluentMotionTokens.ParseSlowMotion("0") == 1 && FluentMotionTokens.ParseSlowMotion("-3") == 1, "slow-mo non-positive ignored");
        Check(FluentMotionTokens.ParseSlowMotion("abc") == 1 && FluentMotionTokens.ParseSlowMotion("NaN") == 1 && FluentMotionTokens.ParseSlowMotion("Infinity") == 1, "slow-mo invalid ignored");
        Check(FluentMotionTokens.ParseSlowMotion("100") == 20, "slow-mo capped at 20");
        Check(FluentMotionTokens.Scale(167, 1) == TimeSpan.FromMilliseconds(167), "scale identity");
        Check(FluentMotionTokens.Scale(167, 5) == TimeSpan.FromMilliseconds(835), "scale 5x");
        Check(FluentMotionTokens.Scale(0, 1) == TimeSpan.FromMilliseconds(1), "scale floor 1ms");
        Check(FluentMotionTokens.Scale(250, 0) == TimeSpan.FromMilliseconds(250), "scale invalid factor treated as 1");
    }

    private static void Policy()
    {
        Check(FluentMotionPolicy.Resolve(systemAnimationsEnabled: true, userReduceMotion: false), "system on + follow = animate");
        Check(!FluentMotionPolicy.Resolve(true, true), "system on + in-app reduce = no motion");
        Check(!FluentMotionPolicy.Resolve(false, false), "system off = no motion");
        Check(!FluentMotionPolicy.Resolve(false, true), "in-app setting can never force motion on when system is off");
    }

    private static void ShortCircuit()
    {
        int instant = 0, animate = 0;
        var played = FluentMotionPolicy.Run(false, () => instant++, () => animate++);
        Check(!played && instant == 1 && animate == 0, "reduce-motion: final state applied synchronously, animation never started");

        instant = animate = 0;
        played = FluentMotionPolicy.Run(true, () => instant++, () => animate++);
        Check(played && instant == 0 && animate == 1, "motion on: animation started, no instant fallback");

        instant = 0;
        played = FluentMotionPolicy.Run(true, () => instant++, () => throw new InvalidOperationException("composition unavailable"));
        Check(!played && instant == 1, "animation failure falls back to the final state");

        // Completion callbacks run synchronously in the short-circuit (e.g. Collapse after a fade-out).
        var collapsed = false;
        FluentMotionPolicy.Run(false, () => collapsed = true, () => { });
        Check(collapsed, "completion (collapse) is synchronous when motion is off");
    }

    private static void Generations()
    {
        var gate = new MotionGeneration();
        var show = gate.Next();
        var hide = gate.Next();
        Check(!gate.IsCurrent(show) && gate.IsCurrent(hide), "newer animation supersedes the older one");
        var showAgain = gate.Next();
        var hideCompletedLate = gate.IsCurrent(hide);
        Check(!hideCompletedLate && gate.IsCurrent(showAgain), "late fade-out completion cannot collapse a re-shown element");
        Check(gate.Current == showAgain, "current generation tracks the last call");

        // Rapid toggle burst: only the final call's completion is honoured.
        var burst = new MotionGeneration();
        var tokens = Enumerable.Range(0, 10).Select(_ => burst.Next()).ToArray();
        Check(tokens.Count(burst.IsCurrent) == 1 && burst.IsCurrent(tokens[^1]), "burst of 10: last call wins");
    }

    // Static guards over the WinUI layer: one UISettings owner, no layout (dependent) animations, terminal HWND gating.
    private static void Static(string repo)
    {
        string Read(string relative) => File.ReadAllText(Path.Combine(repo, "src/AIFrontend", relative));
        var motion = Read("Motion/FluentMotion.cs");
        Check(motion.Contains("FluentMotionPolicy.Run(Enabled, Instant,"), "STATIC Animate routes through the reduce-motion short-circuit");
        Check(motion.Contains("FluentMotionPolicy.Resolve(_systemEnabled, _userReduce)"), "STATIC Enabled = system AND NOT in-app reduce");
        Check(!motion.Contains("ElementCompositionPreview.") && motion.Contains("element.StartAnimation(animation)"),
            "STATIC shared motion uses XAML facade animations, never hands out a Visual before Scale/Opacity APIs");
        Check(!Read("MainWindow.xaml.cs").Contains("ElementCompositionPreview.GetElementVisual(SettingsGearIcon)"),
            "STATIC settings gear uses the same facade ownership as shared motion");
        var sources = Directory.GetFiles(Path.Combine(repo, "src/AIFrontend"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        var settingsOwners = sources.Where(path => File.ReadAllText(path).Contains("new UISettings()") || File.ReadAllText(path).Contains("new Windows.UI.ViewManagement.UISettings()"))
            .Select(Path.GetFileName).ToArray();
        Check(settingsOwners.SequenceEqual(new[] { "FluentMotion.cs" }), "STATIC only FluentMotion creates UISettings (found: " + string.Join(", ", settingsOwners) + ")");
        Check(!sources.Any(path => File.ReadAllText(path).Contains("EnableDependentAnimation = true")), "STATIC no dependent (layout) animations in src");
        var strip = Read("Pages/OpenCode/OpenCodeBrowserStrip.cs");
        Check(!strip.Contains("SetTargetProperty(size, \"Height\")") && strip.Contains("SlideFadeIn(this"), "STATIC browser strip no longer animates Height");
        var airspace = Read("Pages/OpenCode/OpenCodePersistentPtyAirspace.cs");
        Check(!airspace.Contains("TickCount64 + 600"), "STATIC terminal HWND has no fixed 600ms entrance delay");
        var overlay = Read("Pages/OpenCodePage.PersistentPtyOverlay.cs");
        Check(overlay.Contains("_persistentPtyEntranceLease = view.Renderer.SuppressAirspace()") && overlay.Contains("lease?.Dispose();"),
            "STATIC terminal HWND stays hidden until the card entrance completes");
        Check(overlay.Contains("view.Renderer.CaptureFrame()") && overlay.IndexOf("CaptureFrame()", StringComparison.Ordinal) < overlay.IndexOf("SetPresentationVisible(false)", StringComparison.Ordinal),
            "STATIC terminal HWND is captured then hidden before the card exit");
        var view = Read("Pages/OpenCode/OpenCodePersistentPtyView.cs");
        Check(view.Contains("_listMotionLease = Renderer.SuppressAirspace()"), "STATIC terminal HWND hidden during the ☰ list slide");
        var conversation = Read("Pages/OpenCode/OpenCodeConversationView.cs");
        Check(!conversation.Contains("ChildrenTransitions") && conversation.Contains("MaxLiveEntrances = 6"), "STATIC transcript has no children transitions; live entrances capped");
    }
}
