using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentCode.Motion;

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

    private static int Main(string[] args)
    {
        Tokens();
        SlowMotion();
        Policy();
        ShortCircuit();
        Generations();
        Bindings();
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

        // ---- the two library traps from PLAN-v2 §3 ----
        Check(contract.Contains("label.TitleTemplate = TryResource(TitleTemplateKey)")
            || contract.Contains("TitleTemplate = TryResource(TitleTemplateKey)"),
            "STATIC adapter passes the title template (the library template has two title presenters)");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(contract, @"\b\w+\.Foreground\s*=|Control\.ForegroundProperty"),
            "STATIC adapter never sets Foreground, which would bypass the library severity colours");

        // ---- the vendor type stays in the adapter and App.xaml only ----
        var vendorOwners = sources.Where(path => File.ReadAllText(path).Contains("DevWinUI") && !path.Contains("CommunityTextBadge")).Select(Path.GetFileName).ToArray();
        Check(vendorOwners.Length == 0, "STATIC only the adapter names DevWinUI in C# (found: " + string.Join(", ", vendorOwners) + ")");
        var xamlVendor = Directory.GetFiles(Path.Combine(repo, "src/FluentCode"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(path => File.ReadAllText(path).Contains("using:DevWinUI")).Select(Path.GetFileName).ToArray();
        Check(xamlVendor.SequenceEqual(new[] { "App.xaml" }), "STATIC only App.xaml declares the vendor XAML namespace (found: " + string.Join(", ", xamlVendor) + ")");

        // ---- compatibility keys keep their target type (CONTRACT-F.md §4) ----
        foreach (var (key, target) in new[] { ("OpenCodeCautionBadgeStyle", "Border"), ("OpenCodeCriticalBadgeStyle", "Border"), ("OpenCodeNeutralBadgeStyle", "Border"), ("OpenCodeTransparentToolButtonStyle", "Button"), ("DockCard", "Border") })
            Check(app.Contains($"x:Key=\"{key}\" TargetType=\"{target}\""), $"STATIC compatibility key keeps its TargetType: {key} → {target}");
        // B22: dead in the foundation pass — nothing in src/ or tests/ referenced it.
        Check(!app.Contains("OpenCodeTransparentSurfaceStyle") && !sources.Any(path => File.ReadAllText(path).Contains("OpenCodeTransparentSurfaceStyle")),
            "STATIC the dead OpenCodeTransparentSurfaceStyle is deleted and unreferenced");
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

        // ---- the package addition is the only package change (PLAN-v2 §3(1), §3 risk boundary) ----
        var csproj = Read("FluentCode.csproj");
        Check(csproj.Contains("CommunityToolkit.WinUI.Animations\" Version=\"8.2.251219\""), "STATIC Toolkit Animations is pinned at the agreed 8.2.251219");
        Check(csproj.Contains("Microsoft.WindowsAppSDK\" Version=\"2.6.5\"") && csproj.Contains("DevWinUI\" Version=\"10.9.1\""), "STATIC SDK and DevWinUI pins are unchanged");
        var lock_ = Read("packages.lock.json");
        Check(lock_.Contains("\"CommunityToolkit.WinUI.Animations\"") && lock_.Contains("\"resolved\": \"8.2.251219\""),
            "STATIC the lock file records the added package");
    }
}
