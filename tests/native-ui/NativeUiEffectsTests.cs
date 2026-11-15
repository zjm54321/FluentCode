// Contract guards for the Native UI effects adapter (C13 celebration, C20 skeleton bars, C12 flip) and for
// the renderer call sites that consume it. The adapter itself is WinUI code and cannot be instantiated headlessly, so
// its lifetime, cancellation and motion-policy rules are pinned as structural guards over the production sources, the
// same way the other WinUI-touching suites in this repository do it. Nothing here opens a window.
// The part that is executed is the flashcard's turn (FlipStateTests, FlipDriverTests): its state and its driver are
// WinUI-free, and the driver runs here on a scripted surface in place of the adapter.
//
// The cancellation tests below are deliberately order-sensitive: they pin that a burst removed by the duration limit,
// by unload, by disposal or by a superseding burst cannot leave its animated canvas in the tree.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentCode.Pages.OpenCode;

internal static class NativeUiEffectsTests
{
    private const string Adapter = "NativeUiCommunityEffects.cs";

    internal static void Run(string repo, Action<bool, string> require)
    {
        CommentScannerTests(require);
        var folder = Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi");
        var adapter = File.ReadAllText(Path.Combine(folder, Adapter));
        var content = File.ReadAllText(Path.Combine(folder, "NativeUiRenderer.Content.cs"));
        var controls = File.ReadAllText(Path.Combine(folder, "NativeUiRenderer.Controls.cs"));

        // ---------------------------------------------------------------- the adapter is the only vendor surface

        // A type or template-key rename in the library must touch this one file, never the renderer or protocol models.
        var others = Directory.GetFiles(folder, "*.cs").Where(path => Path.GetFileName(path) != Adapter).ToArray();
        var effectUsers = others.Where(path =>
        {
            var text = File.ReadAllText(path);
            return text.Contains("ConfettiCannon", StringComparison.Ordinal) || text.Contains("Shimmer", StringComparison.Ordinal);
        }).Select(Path.GetFileName).Order().ToArray();
        require(effectUsers.Length == 0, "Only the effects adapter names the DevWinUI effect controls (found also in: " + string.Join(", ", effectUsers) + ")");
        var animationUsers = others.Where(path => File.ReadAllText(path).Contains("CommunityToolkit.WinUI.Animations", StringComparison.Ordinal))
            .Select(Path.GetFileName).Order().ToArray();
        require(animationUsers.Length == 0, "Library animation calls stay inside the effects adapter (found also in: " + string.Join(", ", animationUsers) + ")");
        require(adapter.Contains("using DevWinUI;", StringComparison.Ordinal), "The effects adapter imports the library that holds the effect controls");
        require(!Regex.IsMatch(adapter, @"internal (static )?(ConfettiCannon|Shimmer)\b"), "No library control type crosses the adapter surface");

        // Entry points take app-level inputs and answer with ordinary WinUI elements.
        require(adapter.Contains("internal static CelebrationEffect Celebration() => new();", StringComparison.Ordinal)
            && adapter.Contains("internal static FlipEffect Flip(FrameworkElement host, Action<bool> renderFace, Action repaint) => new(host, renderFace, repaint);", StringComparison.Ordinal)
            && adapter.Contains("internal static FrameworkElement SkeletonBars() => new SkeletonHost().Bars;", StringComparison.Ordinal),
            "The adapter exposes Celebration/Flip/SkeletonBars without a vendor type in its surface");

        // ---------------------------------------------------------------- C13: trigger semantics and bounds

        require(adapter.Contains("var was = _seen; _seen = active;", StringComparison.Ordinal)
            && Regex.Matches(adapter, @"_seen = active").Count == 1,
            "Only the reported `when` value writes the trigger state; teardown never rewrites it");
        require(adapter.Contains("if (was is false && active) Fire();", StringComparison.Ordinal),
            "Celebration fires only on a false→true edge, not on every render or on a true→true refresh");
        require(adapter.Contains("else if (was is true && !active) End();", StringComparison.Ordinal),
            "A celebration the model withdraws (true→false) drops its burst");
        require(Regex.Matches(adapter, @"\bFire\(\);").Count == 1 && Regex.Matches(adapter, @"void Fire\(\)").Count == 1,
            "The burst has exactly one call site, the false→true edge guard");
        require(adapter.Contains("if (!FluentMotion.Enabled) return; // Reduced motion: no confetti.", StringComparison.Ordinal),
            "Reduced motion skips the burst before any instance exists");
        require(adapter.Contains("private const int Columns = 4;", StringComparison.Ordinal)
            && adapter.Contains("ParticleCount = Particles / Columns", StringComparison.Ordinal)
            && adapter.Contains("for (var column = 0; column < Columns; column++)", StringComparison.Ordinal),
            "The burst is one fixed set of pieces, split across the band");
        // Strength, recomputed against the hand-rolled burst this replaced: 24 opaque rectangles of 5–8 x 8 DIP.
        // A library piece averages 100 x Scalar² / π DIP² and fades linearly, so it averages half its alpha
        // (ghost1372/DevWinUI, commit 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e,
        // dev/DevWinUI/Controls/Win2DAndComposition/ConfettiCannon/ConfettiEngine.cs). Arithmetic, not a rendering.
        var pieces = int.Parse(Regex.Match(adapter, @"private const int Particles = (\d+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var pieceScale = double.Parse(Regex.Match(adapter, @"private const double PieceScale = ([\d.]+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        require(adapter.Contains("Scalar = PieceScale,", StringComparison.Ordinal), "The burst's piece size is the declared scale");
        const double oldPiece = 6.5 * 8, oldInk = 24 * oldPiece;
        var pieceArea = 100 * pieceScale * pieceScale / Math.PI; var firstFrame = pieces * pieceArea; var averaged = firstFrame / 2;
        string Sum(double value) => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        require(pieceArea >= .5 * oldPiece && pieceArea <= 1.25 * oldPiece,
            "A piece is near the old rectangle's size: " + Sum(pieceArea) + " against " + Sum(oldPiece) + " DIP²");
        require(firstFrame >= oldInk && firstFrame <= 1.5 * oldInk && averaged >= .5 * oldInk,
            "The burst brackets the old one: " + Sum(firstFrame) + " DIP² at the first frame and " + Sum(averaged) + " averaged over the fade, against " + Sum(oldInk));
        require(24 * (100 * .3 * .3 / Math.PI) / 2 < .05 * oldInk, "The values this replaces (24 pieces at Scalar .3) came to under a twentieth of the old burst");
        require(pieces % (4 * 6) == 0, "Each of the four origins gives every one of the six tones the same number of pieces");
        require(adapter.Contains("=> new[] { \"info\", \"success\", \"warning\", \"purple\", \"pink\", \"danger\" }", StringComparison.Ordinal)
            && adapter.Contains(".Select(tone => NativeUiTheme.Brush(tone, theme: _band.ActualTheme)).OfType<SolidColorBrush>().Select(brush => brush.Color).ToList();", StringComparison.Ordinal)
            && !adapter.Contains("ChartColor", StringComparison.Ordinal),
            "The burst cycles the six named tones as the card's theme paints them, not the chart series, whose sixth colour is grey");
        require(adapter.Contains("private const double BandWidth = 640;", StringComparison.Ordinal)
            && adapter.Contains("private const double BandHeight = 100;", StringComparison.Ordinal)
            && adapter.Contains("Clip = new RectangleGeometry { Rect = new(0, 0, BandWidth, BandHeight) }", StringComparison.Ordinal),
            "The 640x100 celebration footprint and its clip are unchanged");
        require(adapter.Contains("cannon.Fire(new ConfettiCannonOptions", StringComparison.Ordinal),
            "The burst is fired with explicit options as a short-lived effect");
        // Preset names may appear in an explanatory comment, so the guard reads the code with comments removed.
        var adapterCode = Code(adapter);
        var presets = new[] { "FireBasic", "FireRandomDirection", "FireRealistic", "FireFireworks", "FireStars", "FireSnow", "FireSchoolPride" }
            .Where(preset => adapterCode.Contains(preset + "(", StringComparison.Ordinal)).ToArray();
        require(presets.Length == 0, "No long-running preset is called, so no preset timer can outlive the card (found: " + string.Join(", ", presets) + ")");
        require(Regex.Matches(adapterCode, @"\.Fire\(").Count == 1, "Fire has one call site, and it passes options rather than naming a preset");
        require(adapter.Contains("Ticks = BurstFrames", StringComparison.Ordinal) && adapter.Contains("BurstLimit = TimeSpan.FromSeconds(2.5)", StringComparison.Ordinal),
            "The burst length and its duration limit are both bounded");

        // ---------------------------------------------------------------- C13: removal on every exit

        require(adapter.Contains("private void OnCompleted(object? sender, EventArgs args) => End();", StringComparison.Ordinal)
            && adapter.Contains("private void OnLimit(DispatcherQueueTimer sender, object args) => End();", StringComparison.Ordinal)
            && adapter.Contains("private void OnUnloaded(object sender, RoutedEventArgs args) => End();", StringComparison.Ordinal),
            "Completion, the duration limit and unload each remove the effect");
        require(adapter.Contains("End(); // A second burst never stacks on the previous one.", StringComparison.Ordinal),
            "A superseding burst drops the previous instance before the new one is built");
        require(adapter.Contains("catch (Exception) { End(); }", StringComparison.Ordinal),
            "A failure while building the effect leaves nothing in the tree");
        require(adapter.Contains("_band.Children.Clear();", StringComparison.Ordinal) && adapter.Contains("_cannon = null;", StringComparison.Ordinal)
            && adapter.Contains("_band.Height = 0;", StringComparison.Ordinal),
            "The instance and its animated canvas are dropped, not left resident in the tree");
        require(adapter.Contains("cannon.AnimationCompleted -= OnCompleted;", StringComparison.Ordinal)
            && adapter.Contains("_limit.Tick -= OnLimit; _limit.Stop(); _limit = null;", StringComparison.Ordinal),
            "The completion and limit handlers are detached when the effect ends");
        require(Regex.Matches(adapter, @"_disposed = true").Count == 2,
            "Only the two Dispose methods end the hosts; tearing an effect down does not dispose them");
        // A completion arriving after supersession, unload or disposal finds no instance and no handler.
        require(adapter.Contains("if (_cannon is { } cannon) { cannon.AnimationCompleted -= OnCompleted; _cannon = null; }", StringComparison.Ordinal),
            "An ending effect detaches before it clears, so a late completion re-enters nothing");

        // ---------------------------------------------------------------- motion changes while the card is alive

        require(Regex.Matches(adapter, @"FluentMotion\.Bind\(").Count == 3,
            "Motion is bound once per host: one binding each for the celebration, the flashcard and the skeleton");
        require(adapter.Contains("FluentMotion.Bind(_band, enabled => { if (!enabled) End(); });", StringComparison.Ordinal),
            "Turning motion off while the card is alive drops a running burst");
        require(adapter.Contains("FluentMotion.Bind(bars, _ => Refresh());", StringComparison.Ordinal),
            "The skeleton bars follow a motion change for as long as they are alive");
        var celebrationCtor = adapter.IndexOf("internal CelebrationEffect()", StringComparison.Ordinal);
        var celebrationBind = adapter.IndexOf("FluentMotion.Bind(_band", StringComparison.Ordinal);
        var celebrationHost = adapter.IndexOf("internal FrameworkElement Host", StringComparison.Ordinal);
        require(celebrationCtor >= 0 && celebrationBind > celebrationCtor && celebrationBind < celebrationHost,
            "The celebration binds motion in its constructor, never on every render");
        var skeletonCtor = adapter.IndexOf("internal SkeletonHost()", StringComparison.Ordinal);
        var skeletonBind = adapter.IndexOf("FluentMotion.Bind(bars", StringComparison.Ordinal);
        var skeletonBars = adapter.IndexOf("internal FrameworkElement Bars", StringComparison.Ordinal);
        require(skeletonCtor >= 0 && skeletonBind > skeletonCtor && skeletonBind < skeletonBars,
            "The skeleton binds motion in its constructor, never on every render");

        // No static collection or static event may hold an effect, a host or a closure over either.
        require(!adapter.Contains("static event", StringComparison.Ordinal)
            && !adapter.Contains("static readonly List", StringComparison.Ordinal)
            && !adapter.Contains("static readonly Dictionary", StringComparison.Ordinal)
            && !adapter.Contains("ConditionalWeakTable", StringComparison.Ordinal),
            "The adapter declares no static event, collection or registry");
        var statics = Regex.Matches(adapter, @"private static readonly [^\n]*").Select(match => match.Value.Trim()).ToArray();
        require(statics.Contains("private static readonly TimeSpan BurstLimit = TimeSpan.FromSeconds(2.5);")
            && statics.Contains("private static readonly double[] Fractions = { 0.4, 1, 0.72 };")
            && statics.Length == 2,
            "Its statics are immutable value data only (found: " + string.Join(" | ", statics) + ")");

        // ---------------------------------------------------------------- C12: the flashcard's face

        FlipStateTests(require);
        FlipDriverTests(require);
        var flipEffect = Slice(adapter, "internal sealed class FlipEffect", "private sealed class SkeletonHost");
        var flipCode = Code(flipEffect);
        require(flipEffect.Contains("internal sealed class FlipEffect : IDisposable, INativeUiFlipSurface", StringComparison.Ordinal)
            && flipEffect.Contains("internal void Request(bool flipped) => _driver.Request(flipped);", StringComparison.Ordinal)
            && flipEffect.Contains("void INativeUiFlipSurface.Paint(bool back) => _render?.Invoke(back);", StringComparison.Ordinal)
            && Regex.Matches(flipCode, @"_render\?\.Invoke\(").Count == 1 && Regex.Matches(flipCode, @"_repaint\?\.Invoke\(").Count == 1,
            "The flashcard adapter is the surface of the executed driver and paints from one place");
        require(flipEffect.Contains("bool INativeUiFlipSurface.CanTurn => FluentMotion.Enabled && _host.IsLoaded && _host.ActualWidth > 0;", StringComparison.Ordinal),
            "A turn is animated only while the motion policy allows it and the card is on screen with a size");
        require(flipCode.Contains("Toolkit.AnimationBuilder.Create()", StringComparison.Ordinal)
            && flipCode.Contains("layer: Toolkit.FrameworkLayer.Composition)", StringComparison.Ordinal)
            && flipCode.Contains(".Start(_host, landed);", StringComparison.Ordinal)
            && !flipCode.Contains("FrameworkLayer.Xaml", StringComparison.Ordinal) && !flipCode.Contains("Storyboard", StringComparison.Ordinal),
            "The turn is the library's composition-layer rotation with a completion callback: no Storyboard is created");
        require(adapter.Contains("using Toolkit = CommunityToolkit.WinUI.Animations;", StringComparison.Ordinal)
            && !Regex.IsMatch(Code(adapter), @"using CommunityToolkit\.WinUI"),
            "The animation library is reached through an alias, so its extension methods do not meet DevWinUI's");
        require(flipCode.Contains("to: away ? EdgeOnDegrees : 0", StringComparison.Ordinal) && flipCode.Contains("private const float EdgeOnDegrees = 90;", StringComparison.Ordinal)
            && !Regex.IsMatch(flipCode, @"\bfrom:"),
            "A leg ends edge-on or face-on and starts from wherever the card is, so no face is seen mirrored and a reversal does not jump");
        require(flipCode.Contains("FluentMotion.Duration(away ? FluentMotion.Fast : FluentMotion.Normal)", StringComparison.Ordinal),
            "Both legs take their length from the shared motion tokens, slow motion included");
        // The hand-off visual and the strict facade properties exclude each other on one element (see the adapter).
        require(!flipCode.Contains("FluentMotion.Settle", StringComparison.Ordinal) && !flipCode.Contains("FluentMotion.Animate", StringComparison.Ordinal)
            && !Regex.IsMatch(flipCode, @"_host\.(Scale|Rotation|CenterPoint|Translation)\b"),
            "The flashcard never mixes the strict facade properties with the composition visual it turns");
        require(flipCode.Contains("if (!_turned) return;", StringComparison.Ordinal)
            && flipCode.Contains("visual.StopAnimation(nameof(Microsoft.UI.Composition.Visual.RotationAngleInDegrees));", StringComparison.Ordinal)
            && flipCode.Contains("visual.RotationAngleInDegrees = 0;", StringComparison.Ordinal),
            "Resting stops the rotation and puts the card face-on, and leaves a card that never turned alone");
        require(Regex.Matches(flipCode, @"FluentMotion\.Bind\(").Count == 1
            && flipCode.Contains("FluentMotion.Bind(_host, enabled => { if (!enabled) _driver.Halt(); });", StringComparison.Ordinal)
            && Slice(flipCode, "internal FlipEffect(", "internal void Request").Contains("FluentMotion.Bind(", StringComparison.Ordinal),
            "The motion policy is bound once, in the constructor, and turning motion off ends a turn in flight");
        require(flipCode.Contains("_host.Unloaded += OnUnloaded;", StringComparison.Ordinal) && flipCode.Contains("_host.Unloaded -= OnUnloaded;", StringComparison.Ordinal)
            && flipCode.Contains("timer.Tick += OnOverrun;", StringComparison.Ordinal) && flipCode.Contains("timer.Tick -= OnOverrun; timer.Stop(); _overrun = null;", StringComparison.Ordinal)
            && !Regex.IsMatch(flipCode, @"(Tick|Unloaded|Loaded|Completed) \+= \(") && flipCode.Contains("timer.IsRepeating = false;", StringComparison.Ordinal),
            "The unload and overrun handlers are named methods that are removed again, and the timer fires once");
        var flipDispose = flipCode[flipCode.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        require(flipDispose.Contains("_render = null; _repaint = null;", StringComparison.Ordinal) && flipDispose.Contains("_host.Unloaded -= OnUnloaded;", StringComparison.Ordinal)
            && flipDispose.Contains("StopOverrun();", StringComparison.Ordinal) && flipDispose.Contains("_driver.Dispose();", StringComparison.Ordinal),
            "Disposal drops both callbacks, the unload handler, the timer and the leg in flight");
        require(!Regex.IsMatch(flipCode, @"\bstatic\b"),
            "The flashcard adapter keeps no static state");

        // ---------------------------------------------------------------- C12/C13 call sites

        var flash = Slice(content, "case \"Flashcard\":", "case \"Celebration\":");
        require(flash.Contains("var flash = new Button", StringComparison.Ordinal) && flash.Contains("flash.Click += (_, _) =>", StringComparison.Ordinal),
            "The flashcard is still the native Button with its click invocation");
        require(flash.Contains("var flipKey = \"$ui:\" + key + \":flipped\";", StringComparison.Ordinal),
            "The persisted flip key is unchanged");
        require(flash.Contains("NativeUiCommunityEffects.Flip(flash, RenderFace, Repaint)", StringComparison.Ordinal)
            && flash.Contains("flashVisual.Apply = node => flip.Request(_runtime.Field(flipKey, false) is true);", StringComparison.Ordinal)
            && flash.Contains("flashVisual.Cleanup = flip.Dispose;", StringComparison.Ordinal),
            "The flashcard drives its face through the flip entry point and disposes it with the node");
        require(flash.Contains("Slot(flashFace, P(node, flipped ? \"back\" : \"front\")", StringComparison.Ordinal)
            && flash.Contains("AutomationProperties.SetName(flash, flipped ? \"闪卡背面，点击返回\" : \"闪卡正面，点击翻面\")", StringComparison.Ordinal),
            "One face is active at a time and the accessible label follows it");
        require(!flash.Contains("flash.Content =", StringComparison.Ordinal)
            && !flash.Contains("flashVisual.Apply = node => { var flipped", StringComparison.Ordinal),
            "The flip never swaps the Button out from under itself and the face is no longer decided beside it");

        var celebration = Slice(content, "case \"Celebration\":", "case \"Icon\":");
        require(celebration.Contains("NativeUiCommunityEffects.Celebration()", StringComparison.Ordinal)
            && celebration.Contains("new Visual(celebration.Host)", StringComparison.Ordinal)
            && celebration.Contains("celebrationVisual.Apply = node => celebration.SetActive(B(node, \"when\"));", StringComparison.Ordinal)
            && celebration.Contains("celebrationVisual.Cleanup = celebration.Dispose;", StringComparison.Ordinal),
            "The celebration node hosts the adapter, reports its `when` value and disposes it with the node");
        require(!celebration.Contains("Storyboard", StringComparison.Ordinal) && !celebration.Contains("new Rectangle", StringComparison.Ordinal),
            "The hand-rolled particles and their storyboard are gone from the renderer");
        require(!content.Contains("using Microsoft.UI.Xaml.Media.Animation;", StringComparison.Ordinal),
            "The renderer no longer needs the animation namespace");

        // ---------------------------------------------------------------- C20: skeleton bars

        require(adapter.Contains("private static readonly double[] Fractions = { 0.4, 1, 0.72 };", StringComparison.Ordinal)
            && adapter.Contains("bars.Loaded += OnLoaded; bars.Unloaded += OnUnloaded;", StringComparison.Ordinal),
            "The skeleton keeps the three bar proportions and subscribes on load and unload");
        require(adapter.Contains("var animated = FluentMotion.Enabled && _contrast?.HighContrast is not true;", StringComparison.Ordinal),
            "The bars shimmer only while motion is allowed and only outside high contrast");
        require(adapter.Contains("_rows[index].Children.Add(animated ? Shimmer() : Static());", StringComparison.Ordinal)
            && adapter.Contains("Height = 12, CornerRadius = new CornerRadius(4), IsActive = true", StringComparison.Ordinal)
            && adapter.Contains("(\"Background\", \"SubtleFillColorSecondaryBrush\")", StringComparison.Ordinal),
            "A shimmer that is not going to animate is never built; the fallback is the static system-colour bar");
        require(adapter.Contains("_contrast.HighContrastChanged -= OnContrastChanged;", StringComparison.Ordinal)
            && adapter.Contains("_contrast = null;", StringComparison.Ordinal)
            && adapter.Contains("private void OnContrastChanged(AccessibilitySettings sender, object args)", StringComparison.Ordinal)
            && adapter.Contains("private void OnLoaded(object sender, RoutedEventArgs args) => Refresh();", StringComparison.Ordinal)
            && adapter.Contains("_animated = null;", StringComparison.Ordinal),
            "The named contrast handler is detached on unload, and a reload rebuilds the bars instead of keeping them across the gap");
        require(adapter.Contains("private const string BarStyle = \"AppStreamingShimmerStyle\";", StringComparison.Ordinal)
            && adapter.Contains("if (Application.Current.Resources.TryGetValue(BarStyle, out var style) && style is Style shared) shimmer.Style = shared;", StringComparison.Ordinal),
            "The animated bars wear the app's shared streaming-skeleton style, and an absent key falls back to the library default");
        require(adapter.Contains("Style = OpenCodeThemedXaml.BrushStyle(\"Border\", (\"Background\", \"SubtleFillColorSecondaryBrush\"))", StringComparison.Ordinal)
            && !adapter.Contains("bar.Style = shared", StringComparison.Ordinal),
            "The static bar keeps the placeholder's own high-contrast-safe brush, never a style that targets the shimmer control");
        require(adapter.Contains("internal FrameworkElement Bars { get; }", StringComparison.Ordinal),
            "The skeleton is reachable through one element-returning entry point for lane N0 to wire in");

        // ---------------------------------------------------------------- C23: native radio group

        var radio = Slice(controls, "case \"RadioGroup\":", "case \"DatePicker\":");
        require(radio.Contains("var host = new RadioButtons { MaxColumns = 1, HorizontalAlignment = HorizontalAlignment.Stretch };", StringComparison.Ordinal)
            && !radio.Contains("GroupName = key", StringComparison.Ordinal),
            "A default/cards radio group is hosted by one native RadioButtons control, not a loose StackPanel of radios");
        require(radio.Contains("host.Items.Add(new RadioButton { Content = tileLabel, Tag = choice.Value });", StringComparison.Ordinal)
            && radio.Contains("var tileLabel = new StackPanel { Spacing = NativeUiTheme.Gap(1) };", StringComparison.Ordinal),
            "The items stay RadioButton with their label and description, so the cards style is unchanged");
        var radioChange = Slice(radio, "host.SelectionChanged += (_, _) =>", "radios = host;");
        require(radioChange.Contains("if (host.SelectedIndex >= 0 && host.SelectedIndex < current.Length)", StringComparison.Ordinal)
            && Regex.IsMatch(radioChange, @"if \(value != NativeUiValues.Text\(Field\(radioVisual, key, ""value"", label\)\)\)\s+SetField\(radioVisual, key, ""value"", label, value\);"),
            "A valid selection commits only a changed field value, so the host's applied-value echo does not re-render the card");
        require(radio.Contains("if (label.Length > 0) host.Header = NativeUiComponentTheme.Text(label);", StringComparison.Ordinal),
            "The group label is the host header, so the group keeps its UIA name");
        require(radio.Contains("pills = new Segmented { SelectionMode = ListViewSelectionMode.Single };", StringComparison.Ordinal),
            "The pills variant stays the maintained toolkit segmented control");
        require(radio.Contains("var width = radioPanel.ActualWidth > 0 ? radioPanel.ActualWidth : radios.ActualWidth;", StringComparison.Ordinal)
            && radio.Contains("radioPanel.SizeChanged += (_, _) => FitTiles();", StringComparison.Ordinal)
            && radio.Contains("host.Loaded += (_, _) => FitTiles();", StringComparison.Ordinal)
            && radio.Contains("radios = host; radioPanel.Children.Add(host); FitTiles();", StringComparison.Ordinal),
            "A card tile keeps filling the group's width, and it follows a narrower card instead of only growing");
        require(Regex.IsMatch(radioChange, @"SetField\(radioVisual, key, ""value"", label, value\);\s*\}\s*if \(!radioVisual.Disposed\) StyleCards\(\);")
            && !radioChange.Contains("if (value ==", StringComparison.Ordinal)
            && radio.Contains("foreach (var tile in radios.Items.OfType<RadioButton>()) NativeUiTheme.RadioCard(tile);", StringComparison.Ordinal),
            "Every selection change repaints the checked-state cards, even when options share a value or selection is cleared");
    }

    /// <summary>
    /// C12: the order of a flashcard turn, executed against the state the effects adapter is driven by. Nothing here
    /// reads source text: each line calls the production type and checks what it answers.
    /// </summary>
    private static void FlipStateTests(Action<bool, string> require)
    {
        const NativeUiFlipState.Step Paint = NativeUiFlipState.Step.Paint, TurnAway = NativeUiFlipState.Step.TurnAway,
            TurnBack = NativeUiFlipState.Step.TurnBack, Settle = NativeUiFlipState.Step.Settle;
        const NativeUiFlipState.Leg Rest = NativeUiFlipState.Leg.Rest, Away = NativeUiFlipState.Leg.Away, Back = NativeUiFlipState.Leg.Back;
        // A card that already shows its front, with motion allowed.
        static NativeUiFlipState Front() { var state = new NativeUiFlipState(); state.Request(false, animate: true); return state; }

        foreach (var animate in new[] { false, true })
            foreach (var face in new[] { false, true })
            {
                var first = new NativeUiFlipState();
                require(!first.Shown && first.Phase == Rest, "A fresh flashcard shows its front and is at rest");
                require(first.Request(face, animate) == Paint && first.Shown == face && first.Wanted == face && first.Phase == Rest && first.Generation == 0,
                    "A card's first face is painted without a turn, a restored back face included (face " + face + ", motion " + animate + ")");
            }

        var still = new NativeUiFlipState(); still.Request(false, animate: false);
        require(still.Request(true, animate: false) == Settle && still.Shown && still.Phase == Rest,
            "With motion off the new face shows in the frame of the report");
        require(still.Request(true, animate: false) == Paint && still.Shown, "An unchanged report repaints the face on screen");
        require(still.Request(false, animate: false) == Settle && !still.Shown && still.Phase == Rest, "With motion off the card flips back the same way");

        var card = Front();
        require(card.Request(true, animate: true) == TurnAway && !card.Shown && card.Wanted && card.Phase == Away,
            "A turn starts away from the viewer with the old face still showing");
        var away = card.Generation;
        require(card.Request(true, animate: true) == Paint && !card.Shown && card.Phase == Away && card.Generation == away,
            "A refresh during the away leg repaints the old face and leaves the leg alone");
        require(!card.Landed(away) && card.Phase == Away && !card.Shown, "A landing cannot end an away leg");
        require(card.Midpoint(away) && card.Shown && card.Phase == Back && card.Generation != away,
            "At edge-on the wanted face becomes the shown one and the back leg takes a new generation");
        var back = card.Generation;
        require(!card.Midpoint(away) && !card.Midpoint(back) && card.Shown && card.Phase == Back && card.Generation == back,
            "The midpoint counts once: reported again it changes nothing");
        require(card.Request(true, animate: true) == Paint && card.Shown && card.Generation == back, "A refresh during the back leg repaints the new face");
        require(card.Landed(back) && card.Phase == Rest && card.Shown, "Landing ends the turn on the wanted face");
        require(!card.Landed(back) && !card.Midpoint(back) && !card.Landed(card.Generation) && card.Phase == Rest && card.Shown,
            "A completion reported twice, by the animation and by its time limit, counts once");

        var undone = Front(); undone.Request(true, animate: true); var undoneAway = undone.Generation;
        require(undone.Request(false, animate: true) == TurnBack && !undone.Shown && undone.Phase == Back && undone.Generation != undoneAway,
            "Taking a turn back before edge-on turns the card back with the face it never stopped showing");
        require(!undone.Midpoint(undoneAway) && !undone.Shown && undone.Phase == Back, "The away leg that was taken back completes into nothing: no face is swapped");
        require(undone.Landed(undone.Generation) && !undone.Shown && undone.Phase == Rest, "The card that was taken back lands on its original face");

        var again = Front(); again.Request(true, animate: true); again.Midpoint(again.Generation); var againBack = again.Generation;
        require(again.Request(false, animate: true) == TurnAway && again.Shown && again.Phase == Away && again.Generation != againBack,
            "A new flip during the back leg turns away again, still showing the face that was coming in");
        require(!again.Landed(againBack) && again.Phase == Away, "The back leg that was superseded lands into nothing");
        require(again.Midpoint(again.Generation) && !again.Shown && again.Landed(again.Generation) && again.Phase == Rest,
            "The second turn swaps at its own midpoint and lands on the front");

        var halted = Front(); halted.Request(true, animate: true); var haltedAway = halted.Generation;
        require(halted.Halt() && halted.Shown && halted.Phase == Rest && halted.Generation != haltedAway,
            "Halting an away leg shows the wanted face at once and reports that the face changed");
        require(!halted.Midpoint(haltedAway) && !halted.Landed(haltedAway) && halted.Shown && halted.Phase == Rest, "A completion arriving after a halt changes nothing");
        var haltedGeneration = halted.Generation;
        require(!halted.Halt() && halted.Generation == haltedGeneration, "Halting a card at rest does nothing");
        var haltedBack = Front(); haltedBack.Request(true, animate: true); haltedBack.Midpoint(haltedBack.Generation); var haltedBackLeg = haltedBack.Generation;
        require(!haltedBack.Halt() && haltedBack.Shown && haltedBack.Phase == Rest && !haltedBack.Landed(haltedBackLeg),
            "Halting a back leg ends it without a repaint: the wanted face is already on screen");

        var switched = Front(); switched.Request(true, animate: true); var switchedAway = switched.Generation;
        require(switched.Request(false, animate: false) == Settle && !switched.Shown && switched.Phase == Rest && !switched.Midpoint(switchedAway),
            "A flip reported with motion off ends a turn in flight on the face it asks for");

        // Twenty thousand reports, completions (current and stale) and halts in a fixed pseudo-random order.
        var walk = Front(); var asked = false; var random = new Random(12611); string? broken = null;
        for (var index = 0; index < 20000 && broken is null; index++)
        {
            void Hold(bool holds, string rule) { if (!holds) broken ??= rule + " (step " + index + ")"; }
            var shown = walk.Shown; var phase = walk.Phase; var generation = walk.Generation;
            switch (random.Next(6))
            {
                case 0:
                    asked = !asked; var turn = walk.Request(asked, animate: true);
                    Hold(turn == (phase == Away ? TurnBack : TurnAway) && walk.Shown == shown && walk.Generation > generation, "an animated flip never changes the face in the frame of the report");
                    break;
                case 1:
                    asked = !asked;
                    Hold(walk.Request(asked, animate: false) == Settle && walk.Shown == asked && walk.Phase == Rest && walk.Generation > generation, "a flip with motion off settles on the wanted face");
                    break;
                case 2:
                    Hold(walk.Request(asked, random.Next(2) == 0) == Paint && walk.Shown == shown && walk.Phase == phase && walk.Generation == generation, "an unchanged report only repaints");
                    break;
                case 3:
                    var stale = generation - 1 - random.Next(3);
                    Hold(!walk.Midpoint(stale) && !walk.Landed(stale) && walk.Shown == shown && walk.Phase == phase && walk.Generation == generation, "a stale completion changes nothing");
                    break;
                case 4:
                    if (phase == Away) Hold(walk.Midpoint(generation) && walk.Shown == asked && walk.Phase == Back, "the current away leg swaps the face at its midpoint");
                    else if (phase == Back) Hold(walk.Landed(generation) && walk.Shown == shown && walk.Phase == Rest, "the current back leg lands without touching the face");
                    else Hold(!walk.Midpoint(generation) && !walk.Landed(generation) && walk.Generation == generation, "a card at rest has no completion to accept");
                    break;
                default:
                    Hold(walk.Halt() == (shown != asked) && walk.Phase == Rest, "a halt reports a change of face exactly when there was one");
                    break;
            }
            Hold(walk.Wanted == asked, "the wanted face is the last one reported");
            Hold(walk.Phase == Away ? walk.Shown != asked : walk.Shown == asked, "only an away leg shows the face that was not asked for");
            Hold(walk.Generation >= generation, "generations never go back");
        }
        require(broken is null, "The flip keeps its rules over 20,000 reports, completions and halts in a fixed pseudo-random order (first broken: " + broken + ")");
    }

    /// <summary>The surface the driver runs on in tests: it records what was asked of it and plays the render pass.</summary>
    private sealed class FlipScript : INativeUiFlipSurface
    {
        internal NativeUiFlipDriver Driver = null!;
        internal bool Motion = true, Model, InPass, Starts = true, LandsAtOnce, PaintedOutsidePass;
        internal bool? Painted; // The face on screen.
        internal bool Resting = true; // False from a started leg until the next Rest.
        internal int Paints, Repaints, Rests;
        internal readonly List<(bool Away, Action Landed)> Legs = new();
        internal Action? DuringRepaint;
        public bool CanTurn => Motion;
        public bool Turn(bool away, Action landed)
        {
            Legs.Add((away, landed));
            if (LandsAtOnce) landed();
            if (Starts) Resting = false;
            return Starts;
        }
        public void Rest() { Rests++; Resting = true; }
        public void Paint(bool back) { Paints++; Painted = back; if (!InPass) PaintedOutsidePass = true; }
        public void Repaint() { Repaints++; DuringRepaint?.Invoke(); Pass(); }
        /// <summary>A render pass: the model reports the face it wants.</summary>
        internal void Pass() { var outer = InPass; InPass = true; try { Driver.Request(Model); } finally { InPass = outer; } }
        internal void Flip() { Model = !Model; Pass(); }
        internal int Live => Legs.Count(leg => ((NativeUiFlipLeg)leg.Landed.Target!).Live);
        internal static FlipScript Front() { var script = new FlipScript(); script.Driver = new(script); script.Pass(); return script; }
    }

    /// <summary>
    /// C12: the turn as the adapter runs it, executed on a scripted surface: which leg starts when, where the face
    /// is painted, and what a leg that ends late, early or never can still reach.
    /// </summary>
    private static void FlipDriverTests(Action<bool, string> require)
    {
        const NativeUiFlipState.Leg Rest = NativeUiFlipState.Leg.Rest, Away = NativeUiFlipState.Leg.Away, Back = NativeUiFlipState.Leg.Back;

        var calls = new List<int>(); var one = new NativeUiFlipLeg(7, calls.Add);
        require(one.Live, "A leg is live until it ends");
        one.Land(); one.Land();
        require(calls.SequenceEqual(new[] { 7 }) && !one.Live, "A leg reports its end once, with its generation, and then holds nothing");
        var dropped = new NativeUiFlipLeg(8, calls.Add); dropped.Drop(); dropped.Land();
        require(calls.Count == 1 && !dropped.Live, "A dropped leg reaches nothing when its end arrives");

        var card = FlipScript.Front();
        require(card.Painted == false && card.Paints == 1 && card.Legs.Count == 0 && card.Rests == 0 && card.Repaints == 0,
            "A card's first face is painted without a turn and without touching the surface");
        var restored = new FlipScript { Model = true }; restored.Driver = new(restored); restored.Pass();
        require(restored.Painted == true && restored.Legs.Count == 0 && restored.Rests == 0, "A card restored on its back paints it without a turn");

        card.Flip();
        require(card.Legs.Count == 1 && card.Legs[0].Away && card.Painted == false && card.Driver.Phase == Away && card.Repaints == 0,
            "A flip starts the away leg and the old face stays on screen");
        card.Pass();
        require(card.Legs.Count == 1 && card.Painted == false && card.Paints == 3, "A refresh mid-turn repaints the old face and starts nothing");
        card.Legs[0].Landed();
        require(card.Repaints == 1 && card.Painted == true && card.Legs.Count == 2 && !card.Legs[1].Away && card.Driver.Phase == Back && card.Live == 1,
            "Edge-on, the face changes in a render pass of its own and the back leg starts");
        card.Legs[0].Landed();
        require(card.Repaints == 1 && card.Legs.Count == 2, "The away leg's end counts once");
        var restsBefore = card.Rests; card.Legs[1].Landed();
        require(card.Driver.Phase == Rest && card.Resting && card.Rests == restsBefore + 1 && card.Repaints == 1 && card.Live == 0 && card.Painted == true,
            "Face-on, the turn ends: the surface is rested and no leg is left live");
        require(!card.PaintedOutsidePass, "Every paint of a whole turn happened inside a render pass");

        var still = FlipScript.Front(); still.Motion = false; still.Flip();
        require(still.Painted == true && still.Legs.Count == 0 && still.Repaints == 0 && still.Driver.Phase == Rest && still.Resting,
            "With motion off the face changes in the frame of the report and no leg starts");

        var undone = FlipScript.Front(); undone.Flip(); undone.Flip();
        require(undone.Legs.Count == 2 && !undone.Legs[1].Away && undone.Painted == false && undone.Driver.Phase == Back && undone.Live == 1,
            "A flip taken back before edge-on turns the card back, still on the face it never stopped showing");
        undone.Legs[0].Landed();
        require(undone.Repaints == 0 && undone.Painted == false, "The away leg that was taken back ends into nothing");
        undone.Legs[1].Landed();
        require(undone.Driver.Phase == Rest && undone.Painted == false && undone.Repaints == 0, "The card that was taken back lands on its original face without a repaint");

        var halted = FlipScript.Front(); halted.Flip(); halted.Driver.Halt();
        require(halted.Repaints == 1 && halted.Painted == true && halted.Driver.Phase == Rest && halted.Resting && halted.Live == 0 && !halted.PaintedOutsidePass,
            "Halting an away leg rests the card and shows the wanted face through one render pass");
        halted.Legs[0].Landed();
        require(halted.Repaints == 1 && halted.Legs.Count == 1, "A leg that ends after a halt reaches nothing");
        var haltedRests = halted.Rests; halted.Driver.Halt();
        require(halted.Rests == haltedRests && halted.Repaints == 1, "Halting a card at rest does not touch the surface");
        var haltedBack = FlipScript.Front(); haltedBack.Flip(); haltedBack.Legs[0].Landed(); haltedBack.Driver.Halt();
        require(haltedBack.Repaints == 1 && haltedBack.Resting && haltedBack.Driver.Phase == Rest && haltedBack.Painted == true,
            "Halting a back leg rests the card without another pass: the wanted face is already on screen");

        var refused = FlipScript.Front(); refused.Starts = false; refused.Flip();
        require(refused.Painted == true && refused.Driver.Phase == Rest && refused.Resting && refused.Repaints == 0 && refused.Live == 0 && !refused.PaintedOutsidePass,
            "A leg that cannot start changes the face in the frame of the report");
        var early = FlipScript.Front(); early.LandsAtOnce = true; early.Flip();
        require(early.Painted == true && early.Driver.Phase == Rest && early.Resting && early.Repaints == 0 && early.Legs.Count == 1 && early.Live == 0,
            "A leg that reports its end from inside its own start did not run: the face changes at once, with no pass inside a pass");
        var refusedBack = FlipScript.Front(); refusedBack.Flip(); refusedBack.Starts = false; refusedBack.Legs[0].Landed();
        require(refusedBack.Painted == true && refusedBack.Driver.Phase == Rest && refusedBack.Resting && refusedBack.Repaints == 1,
            "A back leg that cannot start leaves the card face-on with the new face, never edge-on");
        var offAtEdge = FlipScript.Front(); offAtEdge.Flip(); offAtEdge.Motion = false; offAtEdge.Legs[0].Landed();
        require(offAtEdge.Painted == true && offAtEdge.Driver.Phase == Rest && offAtEdge.Resting && offAtEdge.Legs.Count == 1,
            "Motion turned off by edge-on: the card rests face-on with the new face and no back leg starts");

        var gone = FlipScript.Front(); gone.Flip(); gone.Driver.Dispose();
        require(gone.Resting && gone.Live == 0, "Disposal rests the surface and drops the leg in flight");
        var goneRests = gone.Rests; var gonePaints = gone.Paints;
        gone.Legs[0].Landed(); gone.Driver.Halt(); gone.Flip(); gone.Driver.Dispose();
        require(gone.Rests == goneRests && gone.Paints == gonePaints && gone.Repaints == 0 && gone.Legs.Count == 1,
            "After disposal a late end, a halt, a report and a second disposal reach nothing");
        var pruned = FlipScript.Front(); pruned.Flip(); pruned.DuringRepaint = pruned.Driver.Dispose; pruned.Legs[0].Landed();
        require(pruned.Legs.Count == 1 && pruned.Resting, "A card removed by the pass that changes its face starts no back leg");
        var changed = FlipScript.Front(); changed.Flip(); changed.DuringRepaint = () => { changed.DuringRepaint = null; changed.Model = false; };
        changed.Legs[0].Landed();
        require(changed.Legs.Count == 2 && changed.Legs[1].Away && changed.Driver.Phase == Away && changed.Painted == true && changed.Live == 1,
            "A model that flips again by the time the face changes starts the next away leg, and only that leg");

        // Ten thousand flips, refreshes, ends (current and stale), halts and motion changes in a fixed order.
        var walk = FlipScript.Front(); var random = new Random(2611); string? broken = null;
        for (var index = 0; index < 10000 && broken is null; index++)
        {
            switch (random.Next(7))
            {
                case 0: walk.Flip(); break;
                case 1: walk.Pass(); break;
                case 2: if (walk.Legs.Count > 0) walk.Legs[^1].Landed(); break;
                case 3: if (walk.Legs.Count > 0) walk.Legs[random.Next(walk.Legs.Count)].Landed(); break;
                case 4: walk.Driver.Halt(); break;
                case 5: walk.Motion = !walk.Motion; if (!walk.Motion) walk.Driver.Halt(); break;
                default: walk.Starts = random.Next(8) != 0; walk.LandsAtOnce = random.Next(16) == 0; break;
            }
            void Hold(bool holds, string rule) { if (!holds) broken ??= rule + " (step " + index + ")"; }
            var phase = walk.Driver.Phase;
            Hold(walk.Painted == walk.Driver.Shown, "the face on screen is the one the driver says is shown");
            Hold(!walk.PaintedOutsidePass, "a face is only painted inside a render pass");
            Hold(walk.Live == (phase == Rest ? 0 : 1), "exactly one leg is live during a turn and none at rest");
            Hold(walk.Resting == (phase == Rest), "the card is at rest exactly when no turn is in flight");
            Hold(phase == Away ? walk.Painted != walk.Model : walk.Painted == walk.Model, "only an away leg shows the face that was not asked for");
            Hold(phase != Back || !walk.Legs[^1].Away, "a turn in its second half is on a back leg");
            if (walk.Legs.Count > 64) walk.Legs.RemoveRange(0, 32); // Only the newest leg can be live.
        }
        require(broken is null, "The turn keeps its rules over 10,000 flips, refreshes, ends, halts and motion changes (first broken: " + broken + ")");
    }

    /// <summary>The text between two case labels of one renderer file.</summary>
    private static string Slice(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) return "";
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>Removes C# comments, preserving literals and token/line boundaries for source guards.</summary>
    private static string Code(string source)
    {
        // Keep this scanner and its executed cases in sync with tests/fluent-motion/Program.cs
        // and tests/opencode-composer-hookup/PickerEscape.cs; each runner compiles its own copy.
        var code = new System.Text.StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                code.Append(' '); index += 2;
                while (index < source.Length && source[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                code.Append(' '); index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/') { index += 2; break; }
                    if (source[index] is '\r' or '\n') code.Append(source[index]);
                    index++;
                }
                continue;
            }
            if (source[index] is '"' or '\'')
            {
                var start = index; var quote = source[index];
                var verbatim = quote == '"' && ((index > 0 && source[index - 1] == '@')
                    || (index > 1 && source[index - 1] == '$' && source[index - 2] == '@'));
                var delimiterLength = 1;
                if (quote == '"' && !verbatim)
                    while (start + delimiterLength < source.Length && source[start + delimiterLength] == '"') delimiterLength++;
                if (delimiterLength >= 3)
                {
                    index += delimiterLength;
                    while (index < source.Length)
                    {
                        if (source[index] != '"') { index++; continue; }
                        var quotes = 1;
                        while (index + quotes < source.Length && source[index + quotes] == '"') quotes++;
                        index += quotes;
                        if (quotes == delimiterLength) break;
                    }
                }
                else
                {
                    index++;
                    while (index < source.Length)
                    {
                        var current = source[index++];
                        if (!verbatim && current == '\\' && index < source.Length) { index++; continue; }
                        if (current != quote) continue;
                        if (verbatim && index < source.Length && source[index] == '"') { index++; continue; }
                        break;
                    }
                }
                code.Append(source, start, index - start);
                continue;
            }
            code.Append(source[index++]);
        }
        return code.ToString();
    }

    private static void CommentScannerTests(Action<bool, string> require)
    {
        void Literal(string source, string name)
        {
            require(Code(source + " // discard DevWinUI") == source + "  ", name + ": preserves literal and following code, removes line comment");
            require(Code(source + " /* discard DevWinUI */") == source + "  ", name + ": preserves literal and following code, removes block comment");
        }
        Literal("var url = \"https://example.test\"; DevWinUI.After();", "URL before same-line vendor code");
        Literal("var text = \"/* DevWinUI */\"; DevWinUI.After();", "Block marker inside a string");
        Literal("var type = \"DevWinUI.Widget\";", "Vendor type named only inside a string remains a reference");
        Literal("var slash = '/'; var quote = '\"'; var apostrophe = '\\''; var backslash = '\\\\'; DevWinUI.After();", "Char literals with slash, quote, escaped apostrophe and backslash");
        foreach (var prefix in new[] { "", "$" })
            Literal("var text = " + prefix + "\"\\\"/* DevWinUI */ // kept\\\"\"; DevWinUI.After();", prefix + " regular string with escaped quotes");
        foreach (var prefix in new[] { "@", "$@", "@$" })
        {
            Literal("var text = " + prefix + "\"https://host/\"\"/* DevWinUI */ // kept\"\"\"; DevWinUI.After();", prefix + " verbatim string with doubled quotes");
            Literal("var text = " + prefix + "\"\"\"/* DevWinUI */ // kept\"\"\"; DevWinUI.After();", prefix + " verbatim string starting with a doubled quote, not a raw delimiter");
        }
        foreach (var count in new[] { 3, 4, 5 })
            foreach (var prefix in new[] { "", "$$" })
            {
                var delimiter = new string('"', count);
                Literal("var text = " + prefix + delimiter + "\nhttps://host/ " + new string('"', count - 1)
                    + " /* DevWinUI */ // kept\n" + delimiter + "; DevWinUI.After();", prefix + " raw string with " + count + "-quote delimiters");
            }
        require(Code("var first = 1; // DevWinUI\r\n/* DevWinUI\ncomment */ var second = 2;")
            == "var first = 1;  \r\n \n var second = 2;", "Real vendor comments disappear while line boundaries and following code survive");
        require(Code("one/* DevWinUI */two") == "one two", "Removing a comment never joins two code tokens");
    }
}
