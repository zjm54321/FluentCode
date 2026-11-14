// Contract guards for the Native UI effects adapter (C13 celebration, C20 skeleton bars, C12 flip) and for
// the renderer call sites that consume it. The adapter itself is WinUI code and cannot be instantiated headlessly, so
// its lifetime, cancellation and motion-policy rules are pinned as structural guards over the production sources, the
// same way the other WinUI-touching suites in this repository do it. Nothing here opens a window.
// The one part that is executed is the flashcard's turn order (FlipStateTests): that state is WinUI-free.
//
// The cancellation tests below are deliberately order-sensitive: they pin that a burst removed by the duration limit,
// by unload, by disposal or by a superseding burst cannot leave its animated canvas in the tree.
using System;
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
            && adapter.Contains("internal static FlipEffect Flip(FrameworkElement host, Action<bool> renderFace) => new(host, renderFace);", StringComparison.Ordinal)
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

        require(Regex.Matches(adapter, @"FluentMotion\.Bind\(").Count == 2,
            "Motion is bound once per host: one binding for the celebration, one for the skeleton");
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
        var flipEffect = Slice(adapter, "internal sealed class FlipEffect", "private sealed class SkeletonHost");
        require(flipEffect.Contains("private readonly NativeUiFlipState _state = new();", StringComparison.Ordinal)
            && flipEffect.Contains("_render?.Invoke(_state.Shown);", StringComparison.Ordinal)
            && Regex.Matches(Code(flipEffect), @"_render\?\.Invoke\(").Count == 1,
            "The flashcard paints the face its state shows, from one place, on every report");
        require(flipEffect.Contains("var step = _state.Request(flipped, animate: false);", StringComparison.Ordinal),
            "The face changes in the frame of the report: the adapter starts no turn");
        require(flipEffect.Contains("if (_disposed) return;", StringComparison.Ordinal)
            && flipEffect.Contains("_disposed = true; _render = null;", StringComparison.Ordinal),
            "Disposal drops the render callback, so a report arriving after it paints nothing");

        // ---------------------------------------------------------------- C12/C13 call sites

        var flash = Slice(content, "case \"Flashcard\":", "case \"Celebration\":");
        require(flash.Contains("var flash = new Button", StringComparison.Ordinal) && flash.Contains("flash.Click += (_, _) =>", StringComparison.Ordinal),
            "The flashcard is still the native Button with its click invocation");
        require(flash.Contains("var flipKey = \"$ui:\" + key + \":flipped\";", StringComparison.Ordinal),
            "The persisted flip key is unchanged");
        require(flash.Contains("NativeUiCommunityEffects.Flip(flash, RenderFace)", StringComparison.Ordinal)
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
