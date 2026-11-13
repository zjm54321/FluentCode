using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentCode.Motion;
using FluentCode.Pages;

// Offline tests for Fluent motion tokens, reduce-motion policy, generation gates and binding lifetimes,
// plus static guards over the WinUI execution layer.
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

    /// <summary>The source with comments removed, so a guard can never be satisfied by prose alone.</summary>
    private static string StripComments(string source)
    {
        var withoutBlocks = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(withoutBlocks, @"//[^\n]*", "");
    }

    private static int Main(string[] args)
    {
        Tokens();
        SlowMotion();
        Policy();
        ShortCircuit();
        Generations();
        Bindings();
        NoticePolicy();
        if (args.Length > 0) Static(args[0]);
        if (_failures > 0)
        {
            Console.Error.WriteLine($"{_failures} of {_checks} fluent-motion checks failed");
            return 1;
        }
        Console.WriteLine($"PASS {_checks} fluent-motion token, reduce-motion short-circuit, generation, binding lifetime and static checks");
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

    private sealed class BindingOwner(FluentMotionBindings.Token token)
    {
        internal FluentMotionBindings.Token Token { get; } = token;
        internal int Calls;
    }

    private sealed class BindingCapture(BindingOwner owner)
    {
        internal void Apply(bool enabled) => owner.Calls++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddCollectibleOwner(FluentMotionBindings bindings)
    {
        var owner = new BindingOwner(bindings.CreateToken());
        var capture = new BindingCapture(owner);
        owner.Token.Add(enabled => capture.Apply(enabled));
        return new WeakReference(owner);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Bindings()
    {
        var bindings = new FluentMotionBindings();
        var dropped = AddCollectibleOwner(bindings);
        Collect();
        Check(!dropped.IsAlive, "binding callback -> capture -> owner cycle is collected while registry lives");
        bindings.Apply(false);
        Check(bindings.EntryCount == 0, "Apply prunes collected binding tokens");

        var first = new BindingOwner(bindings.CreateToken());
        var second = new BindingOwner(bindings.CreateToken());
        int firstCalls = 0, secondCalls = 0, otherOwnerCalls = 0, throws = 0;
        first.Token.Add(enabled => firstCalls += enabled ? 1 : 10);
        first.Token.Add(_ => { throws++; throw new InvalidOperationException("binding failure"); });
        first.Token.Add(enabled => secondCalls += enabled ? 1 : 10);
        second.Token.Add(enabled => otherOwnerCalls += enabled ? 1 : 10);
        bindings.Apply(true);
        bindings.Apply(false);
        Check(firstCalls == 11 && secondCalls == 11 && otherOwnerCalls == 11 && throws == 2,
            "Apply reaches every live binding and both values, including after a throwing callback on the same owner");
        GC.KeepAlive(first);
        GC.KeepAlive(second);

        var churn = new FluentMotionBindings();
        var bounded = true;
        for (var batch = 0; batch < 32; batch++)
        {
            for (var i = 0; i < 32; i++) AddCollectibleOwner(churn);
            Collect();
            bounded &= churn.EntryCount <= 64;
        }
        Check(bounded, "adding and dropping 1024 owners keeps weak entry count bounded without Apply");
        GC.KeepAlive(churn);
        GC.KeepAlive(bindings);
    }

    // Static guards over the WinUI layer: one UISettings owner, no layout (dependent) animations, terminal HWND gating.
    // Only a notice that needs the user's action may interrupt a terminal; a passive one is held back while it would
    // overlap. Every combination of kind, severity, action button and overlap.
    private static void NoticePolicy()
    {
        foreach (var kind in Enum.GetValues<OpenCodeNoticeKind>())
            foreach (var errorOrWarning in new[] { false, true })
                foreach (var hasAction in new[] { false, true })
                    foreach (var overlaps in new[] { false, true })
                    {
                        bool needs = kind is OpenCodeNoticeKind.SshAlert or OpenCodeNoticeKind.Permission
                            || (kind == OpenCodeNoticeKind.Message && (errorOrWarning || hasAction));
                        string label = $"{kind} errorOrWarning={errorOrWarning} action={hasAction} overlaps={overlaps}";
                        bool held = OpenCodeNoticePolicy.IsHeldBack(kind, errorOrWarning, hasAction, overlaps);
                        bool takes = OpenCodeNoticePolicy.TakesTerminal(kind, errorOrWarning, hasAction, overlaps);
                        Check(OpenCodeNoticePolicy.NeedsAction(kind, errorOrWarning, hasAction) == needs, "notice needs action: " + label);
                        Check(held == (overlaps && !needs), "notice held back: " + label);
                        Check(takes == (overlaps && needs), "notice takes terminal: " + label);
                        Check(!(held && takes) && (overlaps || (!held && !takes)), "notice never both held and leasing, and neither without overlap: " + label);
                    }
        Check(OpenCodeNoticePolicy.IsHeldBack(OpenCodeNoticeKind.Message, false, false, true)
            && OpenCodeNoticePolicy.IsHeldBack(OpenCodeNoticeKind.StateHint, true, true, true),
            "plain informational message and the state pill are passive whatever else is set");
        Check(!OpenCodeNoticePolicy.TakesTerminal(OpenCodeNoticeKind.Message, false, false, true)
            && !OpenCodeNoticePolicy.TakesTerminal(OpenCodeNoticeKind.StateHint, false, false, true),
            "a passive notice never takes the terminal");
        Check(OpenCodeNoticePolicy.Overlaps(0, 0, 10, 10, 5, 5, 20, 20) && OpenCodeNoticePolicy.Overlaps(5, 5, 20, 20, 0, 0, 10, 10),
            "rectangles sharing area overlap, in either order");
        Check(!OpenCodeNoticePolicy.Overlaps(0, 0, 10, 10, 0, 10, 10, 20) && !OpenCodeNoticePolicy.Overlaps(0, 0, 10, 10, 10, 0, 20, 10),
            "rectangles that only touch do not overlap (a notice ending at the terminal's top edge is not held)");
        Check(!OpenCodeNoticePolicy.Overlaps(0, 0, 10, 10, 20, 0, 30, 10) && !OpenCodeNoticePolicy.Overlaps(0, 0, 10, 10, 0, 20, 10, 30),
            "separated rectangles do not overlap");
        Check(OpenCodeNoticePolicy.Overlaps(0, 0, 100, 100, 40, 40, 60, 60), "a contained rectangle overlaps");
    }

    private static void Static(string repo)
    {
        string Read(string relative) => File.ReadAllText(Path.Combine(repo, "src/FluentCode", relative));
        var motion = Read("Motion/FluentMotion.cs");
        Check(!motion.Contains("List<(WeakReference<DependencyObject> Owner, Action<bool> Apply)>"),
            "STATIC motion registry no longer strongly owns callbacks");
        Check(motion.Contains("DependencyProperty.RegisterAttached(") && motion.Contains("owner.SetValue(BindingTokenProperty, token)"),
            "STATIC binding token is owned by an attached property on the native element");
        Check(Read("Pages/OpenCode/OpenCodeModelLogo.cs").Contains("RasterizePixelWidth"), "STATIC model SVGs have explicit raster sizes");
        var toolGroup = Read("Pages/OpenCode/OpenCodeToolGroupView.cs").Split("internal sealed class OpenCodeToolGroupVisual", StringSplitOptions.None)[1];
        var toolGroupDispose = toolGroup.Substring(toolGroup.IndexOf("internal void Dispose()", StringComparison.Ordinal));
        Check(toolGroupDispose.Contains("_timer.Tick -= Timer_Tick;") && toolGroupDispose.Contains("_working.VisibilityChanged -= UpdateTimer;"),
            "STATIC tool group Dispose detaches timer and visibility handlers");
        Check(motion.Contains("FluentMotionPolicy.Run(Enabled, Instant,"), "STATIC Animate routes through the reduce-motion short-circuit");
        Check(motion.Contains("FluentMotionPolicy.Resolve(_systemEnabled, _userReduce)"), "STATIC Enabled = system AND NOT in-app reduce");
        Check(!motion.Contains("ElementCompositionPreview.") && motion.Contains("element.StartAnimation(animation)"),
            "STATIC shared motion uses XAML facade animations, never hands out a Visual before Scale/Opacity APIs");
        Check(!Read("MainWindow.xaml.cs").Contains("ElementCompositionPreview.GetElementVisual(SettingsGearIcon)"),
            "STATIC settings gear uses the same facade ownership as shared motion");
        var sources = Directory.GetFiles(Path.Combine(repo, "src/FluentCode"), "*.cs", SearchOption.AllDirectories)
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
        var transcriptMotion = Read("Pages/OpenCodePage.Motion.cs");
        Check(transcriptMotion.Contains("FluentMotion.Settle(MessageScroll)")
            && transcriptMotion.Contains("FluentMotion.Snap(MessageScroll, 0f)")
            && transcriptMotion.Contains("FluentMotion.SlideFadeIn(MessageScroll,")
            && transcriptMotion.Contains("MessageHost.LayoutUpdated += OnLayout"),
            "STATIC session motion targets outer ScrollView and retains content layout reveal gate");
        Check(!sources.Any(path => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path),
            @"FluentMotion\.\w+\(MessageHost\b|MessageHost\.(Scale|Translation)\s*=|GetElementVisual\(MessageHost\b")),
            "STATIC direct scroll content is never reset/animated by app motion or visual handout");
        var feedback = Read("Motion/ActionFeedback.cs");
        Check(feedback.Contains("SuccessGlyph = \"\\uE73E\"") && feedback.Contains("FluentMotion.ScaleFadeIn(overlay, 0.6f, Center, FluentMotion.Fast)")
            && feedback.Contains("RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted"),
            "STATIC ActionFeedback: ✓ E73E, 167ms FluentMotion (reduce-motion aware), screen-reader notification");
        Check(feedback.Contains("slot.Host.Children[0].Opacity = 0") && !feedback.Contains("Visibility.Collapsed"),
            "STATIC ActionFeedback overlays the result on the transparent original content (no layout shift)");
        var settings = string.Concat(Directory.GetFiles(Path.Combine(repo, "src/FluentCode/Pages/Settings"), "*.cs").Select(File.ReadAllText));
        Check(Read("Pages/Settings/SettingsCards.cs").Contains("IsEditable = true") && settings.Contains("ActionFeedback.Success(_browserSave")
            && !settings.Contains("InfoBarSeverity.Success") && !settings.Contains("已保存\""),
            "STATIC settings: editable font boxes, in-place save feedback, no success text/InfoBars");
        Foundation(repo, Read, sources);
    }

    // Guards over lane F's published contract (App.xaml keys + Controls/CommunityTextBadge.cs). Text scans, because a
    // headless runner cannot reference DevWinUI: the point is that nothing in the app may either.
    private static void Foundation(string repo, Func<string, string> Read, string[] sources)
    {
        var app = Read("App.xaml");
        var badge = Read("Controls/CommunityTextBadge.cs");

        // ---- the four P1 keys exist, and are the badge/shimmer kinds ----
        foreach (var key in new[] { "AppCompactTextBadgeStyle", "AppPillTextBadgeStyle", "AppBadgeTitleTemplate", "AppStreamingShimmerStyle" })
            Check(app.Contains($"x:Key=\"{key}\""), $"STATIC P1 key is published: {key}");
        Check(app.Contains("x:Key=\"AppCompactTextBadgeStyle\" TargetType=\"dev:IssueLabel\"")
            && app.Contains("x:Key=\"AppPillTextBadgeStyle\" TargetType=\"dev:IssueLabel\""),
            "STATIC badge styles target the library label type, not Border");
        Check(app.Contains("x:Key=\"AppStreamingShimmerStyle\" TargetType=\"dev:Shimmer\""), "STATIC shimmer style targets the library shimmer type");
        Check(app.Contains("xmlns:dev=\"using:DevWinUI\""), "STATIC App.xaml declares the vendor namespace once");

        // ---- geometry contract (CONTRACT-F.md §1): each style's own block, not a neighbour's ----
        string StyleBlock(string key)
        {
            var start = app.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
            return start < 0 ? "" : app.Substring(start, app.IndexOf("</Style>", start, StringComparison.Ordinal) - start);
        }
        var compact = StyleBlock("AppCompactTextBadgeStyle");
        var pill = StyleBlock("AppPillTextBadgeStyle");
        Check(pill.Contains("Property=\"MinHeight\" Value=\"20\"") && pill.Contains("Property=\"CornerRadius\" Value=\"10\"") && pill.Contains("Property=\"Padding\" Value=\"8,2,8,2\""),
            "STATIC pill badge keeps the C06 geometry: 20 DIP high, radius 10, padding 8,2");
        Check(compact.Contains("Property=\"MinHeight\" Value=\"0\"") && compact.Contains("Property=\"CornerRadius\" Value=\"4\"") && compact.Contains("Property=\"Padding\" Value=\"6,1,6,2\""),
            "STATIC compact badge keeps the inline-tag geometry: radius 4, padding 6,1,6,2, no forced height");
        foreach (var (name, block) in new[] { ("compact", compact), ("pill", pill) })
        {
            Check(block.Contains("BasedOn=\"{StaticResource DefaultIssueLabelStyle}\""), $"STATIC {name} badge derives from the library style instead of copying its template");
            Check(!block.Contains("Property=\"Template\""), $"STATIC {name} badge does not carry a copied vendor template");
            Check(!block.Contains("Property=\"Foreground\""), $"STATIC {name} badge style never sets Foreground (it would bypass the tone colours)");
            Check(block.Contains("Property=\"FontFamily\" Value=\"{ThemeResource ContentControlThemeFontFamily}\""), $"STATIC {name} badge follows the user's font setting");
        }
        Check(!StyleBlock("AppStreamingShimmerStyle").Contains("Property=\"Template\""), "STATIC shimmer style does not carry a copied vendor template");
        // Badge text stays selectable: the hand-drawn badges enabled selection (NativeUiTheme.Text), and a
        // text-only badge must not lose it while an icon-carrying badge keeps it.
        var titleTemplate = app.Substring(app.IndexOf("x:Key=\"AppBadgeTitleTemplate\"", StringComparison.Ordinal));
        titleTemplate = titleTemplate.Substring(0, titleTemplate.IndexOf("</DataTemplate>", StringComparison.Ordinal));
        Check(titleTemplate.Contains("IsTextSelectionEnabled=\"True\""), "STATIC the badge title template keeps the text selectable (as before the migration)");
        Check(!titleTemplate.Contains("IsTextSelectionEnabled=\"False\""), "STATIC the badge title template does not disable text selection");

        // ---- merge order (PLAN-v2 §3(2)); the same scan the MSIX runner does, repeated here so a bad edit fails fast ----
        var devIndex = app.IndexOf("ms-appx:///DevWinUI/Themes/Generic.xaml", StringComparison.Ordinal);
        var toolkitIndex = app.IndexOf("ms-appx:///CommunityToolkit.WinUI.Controls.SettingsControls/Themes/Generic.xaml", StringComparison.Ordinal);
        var appIndex = app.IndexOf("ms-appx:///Styles/AppTokens.xaml", StringComparison.Ordinal);
        Check(devIndex >= 0 && toolkitIndex > devIndex && appIndex > toolkitIndex,
            "STATIC App.xaml merge order stays WinUI → DevWinUI → Toolkit SettingsControls → app tokens");

        // ---- no duplicate global key (the single-alias rule) ----
        var keys = System.Text.RegularExpressions.Regex.Matches(app, "x:Key=\"([A-Za-z0-9_]+)\"").Select(m => m.Groups[1].Value).ToArray();
        var dupes = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)
            .Except(new[] { "TabViewItemHeaderBackgroundSelected", "ShellTerminalBackground", "ShellTerminalForeground", "ShellTerminalPrompt" })
            .ToArray();
        Check(dupes.Length == 0, "STATIC no duplicate global resource key outside the theme dictionaries (found: " + string.Join(", ", dupes) + ")");

        // ---- the adapter's public surface never names a vendor type ----
        Check(badge.Contains("internal static class CommunityTextBadge"), "STATIC the badge adapter exists");
        Check(badge.Contains("internal static FrameworkElement Create("), "STATIC adapter construction returns an ordinary WinUI element");
        Check(badge.Contains("internal static void Update(FrameworkElement badge"),
            "STATIC adapter update accepts the ordinary element it handed out");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(badge, @"(internal|public)\s+static\s+\S*\s*\bIssueLabel"),
            "STATIC no adapter member exposes a vendor type; the tone mapping stays private");
        var contract = System.Text.RegularExpressions.Regex.Match(badge, @"internal static class CommunityTextBadge[\s\S]*$").Value;
        Check(System.Text.RegularExpressions.Regex.Matches(contract, @"\.Title\s*=\s*([^;]+);").All(m => m.Groups[1].Value.Trim() == "text ?? string.Empty"),
            "STATIC adapter assigns Title as string data only, never an element");
        // C06: the Native UI badge has seven tone inputs (badgeTone: muted/info/success/warning/danger/purple/pink),
        // so the adapter must carry a tone for each of them.
        var toneEnum = System.Text.RegularExpressions.Regex.Match(badge, @"internal enum CommunityTextBadgeTone\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        var toneNames = System.Text.RegularExpressions.Regex.Matches(toneEnum, @"^\s*([A-Za-z]+),?\s*$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Check(toneNames.SequenceEqual(new[] { "Neutral", "Caution", "Critical", "Success", "Attention", "Purple", "Pink" }),
            "STATIC the tone enum is exactly the seven status/categorical tones (found: " + string.Join(", ", toneNames) + ")");
        foreach (var pair in new[] { ("Neutral", "Gray"), ("Caution", "Warning"), ("Critical", "Error"), ("Success", "Success"), ("Attention", "Blue"), ("Purple", "Purple"), ("Pink", "Pink") })
            Check(System.Text.RegularExpressions.Regex.IsMatch(contract, $@"CommunityTextBadgeTone\.{pair.Item1}\s*=>\s*IssueLabelSeverity\.{pair.Item2}"),
                $"STATIC tone mapping {pair.Item1} → {pair.Item2} (CONTRACT-F.md §2)");

        // ---- the two library traps from PLAN-v2 §3 ----
        Check(contract.Contains("label.TitleTemplate = TryResource(TitleTemplateKey)")
            || contract.Contains("TitleTemplate = TryResource(TitleTemplateKey)"),
            "STATIC adapter passes the title template (the library template has two title presenters)");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(contract, @"\b\w+\.Foreground\s*=|Control\.ForegroundProperty"),
            "STATIC adapter never sets Foreground, which would bypass the library severity colours");

        // ---- the vendor type stays in the two adapters, and in App.xaml ----
        // Two vendor boundaries: the text-badge adapter (this lane) and the effects adapter (N1, per its contract the
        // only file that may name a DevWinUI effect control). Both hand ordinary WinUI elements to their consumers.
        // The scan reads each file with comments removed (same rule as the Native UI effect guards): C06 documents
        // the library's tone-brush provenance in comments right at the call sites, and prose is not a vendor type.
        var vendorAdapters = new[] { "CommunityTextBadge.cs", "NativeUiCommunityEffects.cs" };
        var vendorOwners = sources.Where(path => StripComments(File.ReadAllText(path)).Contains("DevWinUI") && !vendorAdapters.Contains(Path.GetFileName(path))).Select(Path.GetFileName).ToArray();
        Check(vendorOwners.Length == 0, "STATIC only the badge and effects adapters name DevWinUI in C# (found: " + string.Join(", ", vendorOwners) + ")");
        var xamlVendor = Directory.GetFiles(Path.Combine(repo, "src/FluentCode"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => File.ReadAllText(path).Contains("using:DevWinUI")).Select(Path.GetFileName).ToArray();
        Check(xamlVendor.SequenceEqual(new[] { "App.xaml" }), "STATIC only App.xaml declares the vendor XAML namespace (found: " + string.Join(", ", xamlVendor) + ")");

        // ---- compatibility keys keep their target type (CONTRACT-F.md §4) ----
        foreach (var (key, target) in new[] { ("OpenCodeCautionBadgeStyle", "Border"), ("OpenCodeCriticalBadgeStyle", "Border"), ("OpenCodeNeutralBadgeStyle", "Border"), ("OpenCodeTransparentToolButtonStyle", "Button"), ("OpenCodeTransparentSurfaceStyle", "Border"), ("DockCard", "Border") })
            Check(app.Contains($"x:Key=\"{key}\" TargetType=\"{target}\""), $"STATIC compatibility key keeps its TargetType: {key} → {target}");
        // B22: OpenCodeTransparentSurfaceStyle stays until P4. Lanes T (OpenCodeToolGroupView) and V
        // (OpenCodePersistentPtyInventoryView) consume it through TryStyle, which returns null silently on a miss.
        Check(System.Text.RegularExpressions.Regex.IsMatch(app, @"x:Key=""OpenCodeTransparentSurfaceStyle""[\s\S]{0,200}?SubtleFillColorTransparentBrush"),
            "STATIC OpenCodeTransparentSurfaceStyle is still defined with its transparent fill (retirement deferred to P4)");
        var tokens = Read("Styles/AppTokens.xaml");
        Check(tokens.Contains("x:Key=\"OpenCodeAttentionDotStyle\" TargetType=\"Ellipse\"") && tokens.Contains("x:Key=\"OpenCodeCautionDotStyle\" TargetType=\"Ellipse\""),
            "STATIC the dot styles stay Ellipse-targeted in AppTokens until TabStatus migrates");
        Check(System.Text.RegularExpressions.Regex.IsMatch(app, @"x:Key=""DockCard""[\s\S]{0,300}?SolidBackgroundFillColorQuarternaryBrush"),
            "STATIC DockCard stays opaque (SolidBackgroundFillColorQuarternary)");

        // ---- the lanes' published keys exist under their final names ----
        foreach (var key in new[] {
            "OpenCodeQuickAccessToggleButtonStyle", "OpenCodeOverlayViewportScrollViewerStyle", "OpenCodeOverlayFlyoutPresenterStyle", "OpenCodeOverlaySmokeStyle",
            "OpenCodePickerFlyoutPresenterStyle", "OpenCodeContextMenuFlyoutPresenterStyle", "OpenCodeComposerPopupChromeStyle", "OpenCodeComposerCommandChipChromeStyle",
            "OpenCodeComposerIntentChipChromeStyle", "OpenCodeComposerNativeOriginChromeStyle", "OpenCodeComposerSecondaryChipChromeStyle",
            "OpenCodeAccentTextStyle", "OpenCodeSecondaryTextStyle", "OpenCodeDisabledTextStyle", "OpenCodeTextOnAccentStyle",
            "OpenCodeAccentFontIconStyle", "OpenCodeSecondaryFontIconStyle", "OpenCodeCriticalFontIconStyle", "OpenCodeSuccessFontIconStyle",
            "OpenCodeContextMenuDividerRectangleStyle", "OpenCodeAgentMenuItemStyle",
            "OpenCodeAgentTreeAccentIconStyle", "OpenCodeAgentTreeCaptionTextStyle",
            "OpenCodeBrowserFloatingPillChromeStyle", "OpenCodeBrowserToolToggleStyle", "OpenCodeBrowserPreviewTableHeaderTemplate" })
            Check(app.Contains($"x:Key=\"{key}\""), $"STATIC published contract key exists: {key}");

        FloatingNotices(repo, app);

        // ---- the package addition is the only package change (PLAN-v2 §3(1), §3 risk boundary) ----
        var csproj = Read("FluentCode.csproj");
        Check(csproj.Contains("CommunityToolkit.WinUI.Animations\" Version=\"8.2.251219\""), "STATIC Toolkit Animations is pinned at the agreed 8.2.251219");
        Check(csproj.Contains("Microsoft.WindowsAppSDK\" Version=\"2.6.5\"") && csproj.Contains("DevWinUI\" Version=\"10.9.1\""), "STATIC SDK and DevWinUI pins are unchanged");
        var lock_ = Read("packages.lock.json");
        Check(lock_.Contains("\"CommunityToolkit.WinUI.Animations\"") && lock_.Contains("\"resolved\": \"8.2.251219\""),
            "STATIC the lock file records the added package");
    }

    // Session-page notices float over the content. They must never go back into a layout row above the timeline,
    // where opening or closing one pushes the conversation, the composer or Home down.
    private static void FloatingNotices(string repo, string app)
    {
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.xaml"));
        System.Xml.Linq.XElement? Named(string name) => page.Descendants()
            .SingleOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        string? Attr(System.Xml.Linq.XElement? element, string name) => (string?)element?.Attribute(name);

        var layer = Named("NoticeLayer");
        var root = Named("PageRoot");
        Check(layer is not null && layer.Parent == root && Attr(root, "RowDefinitions") == "Auto,*"
            && Attr(layer, "Grid.Row") == "1" && Attr(layer, "VerticalAlignment") == "Top",
            "STATIC notices: NoticeLayer overlays the page's star row, top-aligned");
        Check(layer is not null && layer.Attribute("Background") is null && Attr(layer, "Canvas.ZIndex") == "1"
            && layer.ElementsBeforeSelf().Any(e => e == Named("SessionTabs"))
            && layer.ElementsAfterSelf().Any(e => e == Named("SessionPane")),
            "STATIC notices: the layer has no background (input passes through), sits above the page and follows the tab strip in tab order");
        foreach (var name in new[] { "SshConnectionAlert", "PermissionBar", "MessageBar", "SessionStateHint" })
        {
            var notice = Named(name);
            var surface = Named(name + "Surface");
            Check(notice is not null && surface is not null && notice.Parent == surface
                && surface.Ancestors().Contains(layer) && Attr(surface, "Visibility") == "Collapsed"
                && (Attr(surface, "Style") ?? "").Contains("OpenCodeFloatingNotice"),
                $"STATIC notices: {name} keeps its name and floats on an opaque surface inside NoticeLayer");
            Check(notice is not null && !notice.Ancestors().Any(a => a == Named("SessionLayoutRoot") || a == Named("WorkspaceGrid")),
                $"STATIC notices: {name} is not inside the session layout rows");
        }
        Check(!(Named("SessionLayoutRoot")?.Descendants().Any(e => e.Name.LocalName == "InfoBar") ?? true),
            "STATIC notices: no InfoBar is declared in the session layout");
        Check(app.Contains("x:Key=\"OpenCodeFloatingNoticeSurfaceStyle\"") && app.Contains("x:Key=\"OpenCodeFloatingNoticePillStyle\""),
            "STATIC notices: shared surface styles are published in App.xaml");
        var notices = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Notices.cs")));
        Check(notices.Contains("RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnNoticeSourceChanged)")
            && notices.Contains("WatchNoticeBar(MessageBar)") && notices.Contains("WatchNoticeBar(PermissionBar)")
            && notices.Contains("WatchNoticeBar(SshConnectionAlert)"),
            "STATIC notices: the layer observes the bars, so direct writers and ShowMessage keep working");
        var pageCode = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.xaml.cs")));
        Check(pageCode.Contains("private void ShowMessage(InfoBarSeverity severity, string title, string message)")
            && pageCode.Contains("InitializeNotices();"),
            "STATIC notices: ShowMessage keeps its signature and the layer is wired once in the constructor");
        var bounds = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Notices.Layout.cs")));
        Check(bounds.Contains("FindName(\"ShellNoticeBar\")")
            && File.ReadAllText(Path.Combine(repo, "src/FluentCode/MainWindow.xaml")).Contains("x:Name=\"ShellNoticeBar\""),
            "STATIC notices: the shell notice bar the layer moves below still has the name it looks up");
        Check(bounds.Contains("UnregisterPropertyChangedCallback(InfoBar.IsOpenProperty, _shellNoticeOpenToken)")
            && bounds.Contains("_shellNoticeBar.SizeChanged -= Notices_BoundsChanged;"),
            "STATIC notices: callbacks on the window-owned bar are removed when the page unloads");
        Check(bounds.Contains("InteractionsSection, ComposerQueueHost, ComposerCell") && bounds.Contains("NoticeLayer.MaxHeight = height"),
            "STATIC notices: the stack is capped above the interaction, queue and composer rows");
        Check(System.Text.RegularExpressions.Regex.Matches(bounds, @"\b(\w+)\.(Margin|MaxHeight|Height|Padding) = ")
            .All(m => m.Groups[1].Value == "NoticeLayer"),
            "STATIC notices: positioning changes only the layer itself, never the page layout");
        var timing = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Notices.Timing.cs")));
        Check(timing.Contains("MessageBar.Severity != InfoBarSeverity.Informational") && timing.Contains("MessageBar.ActionButton is not null")
            && timing.Contains("_noticeMessagePointerOver || IsNoticeFocusWithin(MessageBarSurface)"),
            "STATIC notices: only plain informational messages close themselves, and not while pointed at or focused");
        Check(!timing.Contains("PermissionBar.IsOpen = false") && !timing.Contains("SshConnectionAlert.IsOpen = false")
            && !timing.Contains("RecordUserDismissal"),
            "STATIC notices: timing never closes the permission or SSH notice and never writes dismissal memory");
        Check(timing.Contains("message.Tick -= NoticeMessageTimer_Tick;") && timing.Contains("hint.Tick -= NoticeHintTimer_Tick;")
            && bounds.Contains("StopNoticeTimers();") && !timing.Contains("Tick += (") && !timing.Contains("static event"),
            "STATIC notices: timers use named Tick handlers that are removed when the page unloads");
        Check(timing.Contains("FluentMotion.SlideFadeIn(NoticeStack") && !timing.Contains("Storyboard") && !timing.Contains("FluentMotion.Bind("),
            "STATIC notices: the entrance goes through FluentMotion, so reduced motion shows the notice at once");
        var airspace = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Notices.Airspace.cs")));
        Check(airspace.Contains("terminal.SuppressAirspace(snapshot: true)") && airspace.Contains("OpenCodeNoticePolicy.TakesTerminal(kind,")
            && airspace.Contains("lease.Dispose()") && bounds.Contains("ReleaseNoticeAirspace();")
            && !airspace.Contains("static readonly Dictionary") && !airspace.Contains("static Dictionary"),
            "STATIC notices: a notice over the terminal HWND holds a snapshot lease only while it overlaps, released on close and unload");
        Check(notices.Contains("UpdateNoticeAirspace();") && notices.Contains("InitializeNoticeAirspace();"),
            "STATIC notices: every notice change re-evaluates the terminal overlap");
        var holds = StripComments(File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Notices.Holds.cs")));
        Check(holds.Contains("OpenCodeNoticePolicy.IsHeldBack(OpenCodeNoticeKind.Message, NoticeMessageErrorOrWarning,")
            && holds.Contains("OpenCodeNoticePolicy.IsHeldBack(OpenCodeNoticeKind.StateHint, false, false,")
            && !holds.Contains("SuppressAirspace") && !holds.Contains(".Focus("),
            "STATIC notices: passive notices ask the policy, and holding one back takes no lease and no focus");
        Check(notices.Contains("bool message = messageWanted && !_noticeMessageHeld;") && notices.Contains("bool hint = hintWanted && !_noticeHintHeld;")
            && timing.Contains("!IsLoaded || _noticeMessageHeld ||") && holds.Contains("if (hintHeld) _noticeHintTimer?.Stop();"),
            "STATIC notices: a held-back notice is not shown and its dismissal clock does not run");
        Check(!airspace.Contains("SessionStateHintSurface") && !timing.Contains(".Focus(") && !notices.Contains(".Focus(")
            && !airspace.Contains(".Focus("),
            "STATIC notices: the pill never leases a terminal and no notice code moves keyboard focus");
    }
}
