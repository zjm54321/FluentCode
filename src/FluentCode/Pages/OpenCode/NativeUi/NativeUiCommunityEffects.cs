// SPDX-License-Identifier: GPL-3.0-or-later
// The one place in Native UI that touches DevWinUI effect controls and library animation calls.
// Callers pass app-level inputs and receive ordinary WinUI elements; no DevWinUI or Toolkit type appears in this
// surface or in renderer state. Each effect states its own lifetime rules below.
// APIs checked against DevWinUI 10.9.1 (source at commit 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e),
// CommunityToolkit.WinUI.Animations 8.2.251219 (CommunityToolkit/Windows, branch rel/8.2.251219, commit
// a6b4dc451c0e54dd29f58743894a956100e7f713) and WinUI 2.6.5.
using System;
using System.Collections.Generic;
using System.Linq;
using DevWinUI;
using FluentCode.Motion;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
// An alias, not a using: the Toolkit's extension methods stay out of a file that also imports DevWinUI's.
using Toolkit = CommunityToolkit.WinUI.Animations;

namespace FluentCode.Pages.OpenCode;

internal static class NativeUiCommunityEffects
{
    /// <summary>
    /// C13: the celebration band. Keeps the existing 640×100 footprint, fires on a false→true edge only, and holds a
    /// short-lived confetti instance that is dropped on completion, supersession, unload or its time limit.
    /// </summary>
    internal static CelebrationEffect Celebration() => new();
    /// <summary>
    /// C12: the flashcard's face and the turn between two faces. <paramref name="renderFace"/> paints one face and
    /// is only called inside a render pass; <paramref name="repaint"/> runs such a pass, for the change of face at
    /// the middle of a turn. With motion off the face changes in the frame of the report.
    /// </summary>
    internal static FlipEffect Flip(FrameworkElement host, Action<bool> renderFace, Action repaint) => new(host, renderFace, repaint);
    /// <summary>
    /// C20: the three streaming-skeleton bars of <c>NativeUiTheme.Placeholder</c> as one element. The bars shimmer
    /// only while the motion policy allows it and are static system-colour bars with reduced motion or in high
    /// contrast.
    /// </summary>
    internal static FrameworkElement SkeletonBars() => new SkeletonHost().Bars;

    /// <summary>
    /// C13. Owns one band host for the lifetime of a card node. Callers only report the model's <c>when</c> value: the
    /// false→true edge, the burst, its bounds and its teardown all live here.
    /// </summary>
    internal sealed class CelebrationEffect : IDisposable
    {
        // The footprint the hand-rolled celebration had, clip included, so a particle cannot paint outside it.
        private const double BandWidth = 640;
        private const double BandHeight = 100;
        // Strength is set against the hand-rolled burst this replaced: 24 opaque rectangles of 5–8 x 8 DIP (52 DIP²
        // on average, 1,248 DIP² in all) for 1.2–1.65s. A library piece is a parallelogram whose corners are offset
        // by 10 x Scalar DIP along a turning wobble angle w and by 2–3 DIP (r) along a turning tilt angle t. Its area
        // is |(10 x Scalar)² sin 2w − r² sin 2t| / 2, which averages 100 x Scalar² / π once 10 x Scalar is well over
        // 3. Its alpha is 1 − tick / Ticks, so it fades from the first frame and averages one half
        // (ghost1372/DevWinUI, commit 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e,
        // dev/DevWinUI/Controls/Win2DAndComposition/ConfettiCannon/ConfettiEngine.cs, Update and Draw).
        //   Scalar 1  -> a piece averages 32 DIP² inside a box of about 8 x 8 DIP, the old piece's footprint.
        //   48 pieces -> 1,528 DIP² at the first frame (1.22 x the old burst), 764 DIP² averaged over the fade (0.61 x).
        // Matching the average would take 78 pieces and start at twice the old burst, so the count brackets it instead.
        // At Scalar .3 with 24 pieces a piece came to about 3 DIP² and the burst to about 3% of the old one.
        private const int Particles = 48;
        private const double PieceScale = 1;
        // The rectangles were spread across the whole band at (i*29)%600 and fell straight down; four origins cover
        // the same width, and twelve pieces at each take every one of the six tones twice.
        private const int Columns = 4;
        // ≈1.5s at 60fps, inside the 1.2–1.65s the previous burst used.
        private const int BurstFrames = 90;
        private static readonly TimeSpan BurstLimit = TimeSpan.FromSeconds(2.5);
        private readonly Canvas _band = new() { Height = 0, IsHitTestVisible = false, Clip = new RectangleGeometry { Rect = new(0, 0, BandWidth, BandHeight) } };
        // Captured while the card is built on the UI thread: the band may be fired before it enters the tree, where its
        // own DispatcherQueue is not yet available, and the lifetime timer must never depend on that.
        private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();
        private ConfettiCannon? _cannon;
        private DispatcherQueueTimer? _limit;
        private bool? _seen; // Tri-state: the first report only records the state, so only a real false→true fires.
        private bool _disposed;

        internal CelebrationEffect()
        {
            _band.Unloaded += OnUnloaded;
            // Bound once per host, never per render: turning motion off while the card is alive drops the burst.
            FluentMotion.Bind(_band, enabled => { if (!enabled) End(); });
        }

        internal FrameworkElement Host => _band;

        internal void SetActive(bool active)
        {
            if (_disposed) return;
            var was = _seen; _seen = active;
            if (was is false && active) Fire();
            else if (was is true && !active) End(); // The model cancelled the celebration: drop the burst with it.
        }

        private void Fire()
        {
            if (!FluentMotion.Enabled) return; // Reduced motion: no confetti.
            End(); // A second burst never stacks on the previous one.
            try
            {
                var cannon = new ConfettiCannon { Width = BandWidth, Height = BandHeight, HorizontalAlignment = HorizontalAlignment.Left };
                cannon.AnimationCompleted += OnCompleted;
                _cannon = cannon; _band.Children.Add(cannon); _band.Height = BandHeight;
                // Fire(options), never a long-running preset: FireFireworks/FireSnow/FireSchoolPride run their own timers.
                // The engine advances one step per frame and adds gravity as a constant per-frame offset, so Ticks is the
                // burst length in frames and these values reproduce the previous gentle fall (about 1.2px/frame, inside
                // the band for its whole life, fading out as it goes) across the same width.
                for (var column = 0; column < Columns; column++)
                    cannon.Fire(new ConfettiCannonOptions
                    {
                        ParticleCount = Particles / Columns, Angle = 270, Spread = 180, StartVelocity = .65, Decay = 1, Gravity = .1,
                        Drift = 0, Ticks = BurstFrames, Origin = new((column + .5) / Columns, 0), Scalar = PieceScale,
                        Shapes = new() { "square" }, Colors = Palette(),
                    });
                _limit = _queue?.CreateTimer();
                if (_limit is not null) { _limit.Interval = BurstLimit; _limit.Tick += OnLimit; _limit.Start(); }
            }
            catch (Exception) { End(); } // No Win2D activation in this context: the card renders without confetti.
        }

        /// <summary>
        /// The six named tones the hand-rolled burst cycled through, in its order and as the card's theme paints them,
        /// so the burst matches the status and category colours around it. A tone whose brush cannot be read is left
        /// out; with none left the library falls back to its own colours.
        /// </summary>
        private List<Windows.UI.Color> Palette()
            => new[] { "info", "success", "warning", "purple", "pink", "danger" }
                .Select(tone => NativeUiTheme.Brush(tone, theme: _band.ActualTheme)).OfType<SolidColorBrush>().Select(brush => brush.Color).ToList();

        private void OnCompleted(object? sender, EventArgs args) => End();
        private void OnLimit(DispatcherQueueTimer sender, object args) => End();
        private void OnUnloaded(object sender, RoutedEventArgs args) => End();

        /// <summary>Drops the instance and its animated canvas. A fired instance is never reused; the next edge builds a new one.</summary>
        private void End()
        {
            if (_limit is not null) { _limit.Tick -= OnLimit; _limit.Stop(); _limit = null; }
            if (_cannon is { } cannon) { cannon.AnimationCompleted -= OnCompleted; _cannon = null; }
            // DevWinUI 10.9.1 Unloaded detaches Draw/Update and removes the canvas from the visual tree:
            // https://github.com/ghost1372/DevWinUI, commit 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e,
            // dev/DevWinUI/Controls/Win2DAndComposition/ConfettiCannon/ConfettiCannon.cs:49-70.
            _band.Children.Clear();
            _band.Height = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _band.Unloaded -= OnUnloaded;
            End();
        }
    }

    /// <summary>
    /// C12. The flashcard's face and its turn. <see cref="NativeUiFlipDriver"/>, which the native-ui tests execute,
    /// decides the order; this class is the surface it runs on, and the one place that calls the animation library.
    /// <para>
    /// A turn is two legs on the host's composition visual, about the vertical line through its centre: away to
    /// edge-on, where the face changes, and back. The card is never turned past edge-on, so no face is ever seen
    /// mirrored. There is no perspective: that would need a transform on the host's parent, which the renderer
    /// re-parents.
    /// </para>
    /// <para>
    /// Lifetime: no Storyboard is created (the library only builds one for XAML-layer animations, and none is used
    /// here). The library's batch keeps its completion handler, so that handler is given a
    /// <see cref="NativeUiFlipLeg"/>, which holds nothing once the leg ended or was dropped. The Unloaded handler
    /// and the timer's Tick handler are named methods, removed on disposal; the timer lives for one leg.
    /// </para>
    /// <para>
    /// The first turn takes the host's composition visual. From then on WinUI refuses the strict facade properties
    /// on that element (<c>Scale</c>, <c>Rotation</c>, <c>CenterPoint</c> and their transitions): it locks an
    /// element to one of the two models (microsoft/microsoft-ui-xaml, tag winui3/release/2.6.5,
    /// src/dxaml/xcp/core/core/elements/depends.cpp, StrictOnlyApiCheck). So this class never calls
    /// <c>FluentMotion.Settle</c> on the host, which sets <c>Scale</c>, and rests the visual itself.
    /// </para>
    /// </summary>
    internal sealed class FlipEffect : IDisposable, INativeUiFlipSurface
    {
        private const float EdgeOnDegrees = 90;
        // How long past its own length a leg may run before the turn is ended for it. Edge-on the card has no
        // width, so a leg whose end never arrives must not be allowed to leave it there.
        private const double OverrunMs = 500;
        private readonly FrameworkElement _host;
        private readonly NativeUiFlipDriver _driver;
        // Captured while the card is built on the UI thread, as the celebration does.
        private readonly DispatcherQueue? _queue = DispatcherQueue.GetForCurrentThread();
        private Action<bool>? _render;
        private Action? _repaint;
        private DispatcherQueueTimer? _overrun;
        private bool _turned; // The host's composition visual was taken. Never true for a card that was not turned.
        private bool _disposed;

        internal FlipEffect(FrameworkElement host, Action<bool> renderFace, Action repaint)
        {
            _host = host; _render = renderFace; _repaint = repaint; _driver = new(this);
            _host.Unloaded += OnUnloaded;
            // Bound once per host, never per render: turning motion off while a card is mid-turn ends the turn.
            FluentMotion.Bind(_host, enabled => { if (!enabled) _driver.Halt(); });
        }

        /// <summary>
        /// Reports the face the model wants, inside a render pass, and paints the face that shows. Every report
        /// paints, so a reactive refresh reaches the face on screen, which mid-turn is still the old one.
        /// </summary>
        internal void Request(bool flipped) => _driver.Request(flipped);

        /// <summary>Windows "animation effects" and the app's reduce-motion setting both end up in the motion policy.</summary>
        bool INativeUiFlipSurface.CanTurn => FluentMotion.Enabled && _host.IsLoaded && _host.ActualWidth > 0;

        void INativeUiFlipSurface.Paint(bool back) => _render?.Invoke(back);

        void INativeUiFlipSurface.Repaint() => _repaint?.Invoke();

        bool INativeUiFlipSurface.Turn(bool away, Action landed)
        {
            StopOverrun();
            if (_queue is null) return false; // Nothing could end a leg whose end never arrives: do not start one.
            try
            {
                var time = FluentMotion.Duration(away ? FluentMotion.Fast : FluentMotion.Normal);
                var visual = ElementCompositionPreview.GetElementVisual(_host);
                _turned = true;
                visual.RotationAxis = System.Numerics.Vector3.UnitY;
                // The axis is vertical, so only the centre's X matters: a new face of another height needs nothing.
                visual.CenterPoint = new((float)(_host.ActualWidth / 2), 0, 0);
                // Composition layer with a completion callback: one scoped batch and no Storyboard
                // (CommunityToolkit/Windows, branch rel/8.2.251219,
                // components/Animations/src/Builders/AnimationBuilder.cs, Start(UIElement, Action)). No starting
                // value is given, so a leg starts from the angle the card is at and a reversal mid-leg does not
                // jump (same branch, components/Animations/src/Extensions/CompositorExtensions.cs,
                // CreateScalarKeyFrameAnimation: key frame 0 is only inserted for an explicit "from").
                // Away turns at a steady rate, 90 degrees in 167ms = 0.54 degrees a millisecond. The width seen is
                // the cosine of the angle, so a steady turn already starts gently and is fastest edge-on. Back is
                // the library's sine ease-out, cubic-bezier(0.39, 0.575, 0.565, 1), over 250ms: it starts at
                // 0.575 / 0.39 x 90 / 250 = 0.53 degrees a millisecond, so the card keeps its speed through
                // edge-on and slows as the new face comes round.
                Toolkit.AnimationBuilder.Create()
                    .RotationInDegrees(
                        to: away ? EdgeOnDegrees : 0, duration: time,
                        easingType: away ? Toolkit.EasingType.Linear : Toolkit.EasingType.Sine,
                        easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                        layer: Toolkit.FrameworkLayer.Composition)
                    .Start(_host, landed);
                StartOverrun(time);
                return true;
            }
            catch (Exception) { return false; } // No compositor for this element: the driver changes the face at once.
        }

        void INativeUiFlipSurface.Rest()
        {
            StopOverrun();
            if (!_turned) return; // Never turned: the element's visual was never taken and is left alone.
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(_host);
                visual.StopAnimation(nameof(Microsoft.UI.Composition.Visual.RotationAngleInDegrees));
                visual.RotationAngleInDegrees = 0;
            }
            catch (Exception) { }
        }

        private void StartOverrun(TimeSpan leg)
        {
            if (_queue?.CreateTimer() is not { } timer) return;
            timer.IsRepeating = false; timer.Interval = leg + TimeSpan.FromMilliseconds(OverrunMs);
            timer.Tick += OnOverrun; timer.Start(); _overrun = timer;
        }

        private void StopOverrun()
        {
            if (_overrun is not { } timer) return;
            timer.Tick -= OnOverrun; timer.Stop(); _overrun = null;
        }

        private void OnOverrun(DispatcherQueueTimer sender, object args) { StopOverrun(); _driver.Halt(); }
        private void OnUnloaded(object sender, RoutedEventArgs args) => _driver.Halt();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _render = null; _repaint = null; // Whatever arrives after this paints nothing.
            _host.Unloaded -= OnUnloaded;
            StopOverrun();
            _driver.Dispose(); // Drops the leg in flight, so its end reaches nothing, and rests the card.
        }
    }

    /// <summary>
    /// C20. Builds the skeleton bars once and swaps each row's bar between the library shimmer and a static bar.
    /// <para>
    /// Lifetime: nothing outside the bars holds this object. The motion binding lives on the bars themselves, and
    /// the contrast subscription exists only between Loaded and Unloaded, so bars that are built and dropped
    /// without entering the tree leave nothing behind.
    /// </para>
    /// </summary>
    private sealed class SkeletonHost
    {
        /// <summary>The shared bar-box style App.xaml declares for the streaming skeleton.</summary>
        private const string BarStyle = "AppStreamingShimmerStyle";
        // The proportions of the bars this replaces.
        private static readonly double[] Fractions = { 0.4, 1, 0.72 };
        private readonly Grid[] _rows;
        private AccessibilitySettings? _contrast;
        // Tri-state: null until the bars are built for the first time, so the first refresh always populates them and
        // every later one is a no-op unless the motion policy or the contrast setting actually changed.
        private bool? _animated;

        internal SkeletonHost()
        {
            var bars = new StackPanel { Spacing = 10 };
            AutomationProperties.SetAccessibilityView(bars, AccessibilityView.Raw);
            _rows = new Grid[Fractions.Length];
            for (var index = 0; index < Fractions.Length; index++)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new() { Width = new GridLength(Fractions[index], GridUnitType.Star) });
                if (Fractions[index] < 1) row.ColumnDefinitions.Add(new() { Width = new GridLength(1 - Fractions[index], GridUnitType.Star) });
                _rows[index] = row; bars.Children.Add(row);
            }
            Bars = bars;
            bars.Loaded += OnLoaded; bars.Unloaded += OnUnloaded;
            // Bound once per host, never per render. The binding applies the current policy immediately and then on every
            // change, so the bars follow a motion change while they are alive and are populated on construction.
            FluentMotion.Bind(bars, _ => Refresh());
        }

        internal FrameworkElement Bars { get; }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (_contrast is null)
            {
                try { _contrast = new AccessibilitySettings(); _contrast.HighContrastChanged += OnContrastChanged; } catch (Exception) { _contrast = null; }
            }
            Refresh(); // Re-reads contrast; a no-op unless it changed.
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            // The library releases its composition objects when a shimmer unloads, so the bars are rebuilt on the next
            // load instead of being kept across the gap.
            _animated = null;
            if (_contrast is null) return;
            _contrast.HighContrastChanged -= OnContrastChanged; // Named handler, removed on unload; a reload re-subscribes.
            _contrast = null;
        }

        /// <summary>The bars animate only while motion is allowed and only outside high contrast.</summary>
        private void Refresh()
        {
            var animated = FluentMotion.Enabled && !HighContrast();
            if (_animated == animated) return;
            _animated = animated;
            for (var index = 0; index < _rows.Length; index++)
            {
                _rows[index].Children.Clear();
                _rows[index].Children.Add(animated ? Shimmer() : Static());
            }
        }

        /// <summary>
        /// The shimmer bar. It wears the app's shared skeleton style; the values a bar cannot do without are also set
        /// here, so a missing key costs nothing: the library's default style has a 40 DIP minimum width, wider than
        /// the short bar's share of a narrow card, and a Control is a tab stop unless told otherwise.
        /// OnApplyTemplate and Loaded try composition initialization even for an inactive Shimmer, so static rows
        /// use Borders instead. In high contrast the library's background resource names a colour, not a brush,
        /// which is the other reason those rows are Borders. Source: https://github.com/ghost1372/DevWinUI,
        /// commit 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e, dev/DevWinUI/Controls/Native/Others/Shimmer.cs:65-83 and
        /// dev/DevWinUI/Themes/Styles/Controls/Shimmer.xaml:6-24.
        /// </summary>
        private static Shimmer Shimmer()
        {
            var shimmer = new Shimmer
            {
                Height = 12, CornerRadius = new CornerRadius(4), IsActive = true, HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0, IsTabStop = false, IsHitTestVisible = false,
            };
            if (Application.Current.Resources.TryGetValue(BarStyle, out var style) && style is Style shared) shimmer.Style = shared;
            return shimmer;
        }

        /// <summary>
        /// The static bar, in place of the shimmer under reduced motion or in high contrast. It is the bar the
        /// streaming placeholder drew before the shimmer, so those two settings look as they did; the shared style
        /// targets the shimmer control, so it is deliberately not applied here.
        /// </summary>
        private static Border Static() => new()
        {
            Height = 12, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SubtleFillColorSecondaryBrush")),
        };

        /// <summary>
        /// Read from the subscription while the bars are loaded, and from a throwaway object before that. When the
        /// setting cannot be read the bars stay static: that is right in every contrast mode.
        /// </summary>
        private bool HighContrast()
        {
            try { return (_contrast ?? new AccessibilitySettings()).HighContrast; }
            catch (Exception) { return true; }
        }

        private void OnContrastChanged(AccessibilitySettings sender, object args)
            => Bars.DispatcherQueue?.TryEnqueue(() => { if (_contrast is not null) Refresh(); });
    }
}
