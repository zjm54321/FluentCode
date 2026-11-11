// SPDX-License-Identifier: GPL-3.0-or-later
// LANE N1: the one place in Native UI that touches DevWinUI effect controls and library animation calls.
// Callers pass app-level inputs and receive ordinary WinUI elements; no DevWinUI type appears in this surface
// or in renderer state. Lifetime rules and the caller contract: /tmp/opencode/selfdrawn-audit/CONTRACT-N1-effects.md.
// APIs checked against DevWinUI 10.9.1 (source at commit 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e) and WinUI 2.6.5.
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
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace FluentCode.Pages.OpenCode;

internal static class NativeUiCommunityEffects
{
    /// <summary>
    /// C13: the celebration band. Keeps the existing 640×100 footprint, fires on a false→true edge only, and holds a
    /// short-lived confetti instance that is dropped on completion, supersession, unload or its time limit.
    /// </summary>
    internal static CelebrationEffect Celebration() => new();
    /// <summary>
    /// C12: the flashcard flip entry point. Today it swaps the face in the same frame; the Toolkit AnimationBuilder
    /// sequence lands inside the flip's own animation seam in the next wave, without changing this contract.
    /// </summary>
    internal static FlipEffect Flip(FrameworkElement host, Action<bool> renderFace) => new(host, renderFace);
    /// <summary>
    /// C20: the three streaming-skeleton bars as one element, replacing the local <c>bars</c> StackPanel and its
    /// opacity Storyboard in <c>NativeUiTheme.Placeholder</c>. The bars shimmer only while the motion policy allows it
    /// and are static system-colour bars with reduced motion or in high contrast.
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
        // Bounded burst: the previous implementation drew 24 rectangles over 1.2–1.65s, so neither grows.
        private const int Particles = 24;
        // The rectangles were spread across the whole band at (i*29)%600 and fell straight down; four origins of six
        // particles keep the total at 24 while covering the same width.
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
                        Drift = 0, Ticks = BurstFrames, Origin = new((column + .5) / Columns, 0), Scalar = .3,
                        Shapes = new() { "square" }, Colors = Palette(),
                    });
                _limit = _queue?.CreateTimer();
                if (_limit is not null) { _limit.Interval = BurstLimit; _limit.Tick += OnLimit; _limit.Start(); }
            }
            catch (Exception) { End(); } // No Win2D activation in this context: the card renders without confetti.
        }

        private List<Windows.UI.Color> Palette()
            => Enumerable.Range(0, 6).Select(index => NativeUiTheme.ChartColor(index, _band.ActualTheme)).ToList();

        private void OnCompleted(object? sender, EventArgs args) => End();
        private void OnLimit(DispatcherQueueTimer sender, object args) => End();
        private void OnUnloaded(object sender, RoutedEventArgs args) => End();

        /// <summary>Drops the instance and its animated canvas. A fired instance is never reused; the next edge builds a new one.</summary>
        private void End()
        {
            if (_limit is not null) { _limit.Tick -= OnLimit; _limit.Stop(); _limit = null; }
            if (_cannon is { } cannon) { cannon.AnimationCompleted -= OnCompleted; _cannon = null; }
            _band.Children.Clear(); // Removing it runs the control's own unload cleanup: Draw/Update detached, canvas released.
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
    /// C12. The flashcard flip. It owns the displayed face and the generation that supersedes a flip still in flight,
    /// so the next wave inserts the animation sequence into <see cref="Play"/> alone.
    /// </summary>
    internal sealed class FlipEffect : IDisposable
    {
        private readonly FrameworkElement _host;
        private Action<bool>? _render;
        private bool? _target;
        private bool? _displayed;
        private int _generation;
        private bool _disposed;

        internal FlipEffect(FrameworkElement host, Action<bool> renderFace) { _host = host; _render = renderFace; }

        /// <summary>The face the adapter has painted. False is the front face, which is what a fresh card shows.</summary>
        internal bool Displayed => _displayed is true;

        /// <summary>
        /// Reports the face the model wants and takes over painting. An unchanged request repaints that face (reactive
        /// refresh); a changed one swaps it, or, once the animation lands, rotates it and swaps at the midpoint. A flip
        /// that a newer request superseded never paints its face.
        /// </summary>
        internal void Request(bool flipped)
        {
            if (_disposed) return;
            if (_target == flipped) { if (_displayed == flipped) _render?.Invoke(flipped); return; }
            _target = flipped;
            Play(++_generation, flipped); // A newer flip supersedes any flip still in flight.
        }

        /// <summary>
        /// The animation seam, and the only place the next wave changes. Today it swaps the face in this frame for both
        /// motion settings; with the animation it rotates <see cref="_host"/> 90° out on the Y axis, calls
        /// <see cref="Complete"/> with this generation at the midpoint, then rotates 90° in. Reduced motion always takes
        /// the immediate path.
        /// </summary>
        private void Play(int generation, bool flipped)
        {
            if (!FluentMotion.Enabled) FluentMotion.Settle(_host);
            Complete(generation, flipped);
        }

        /// <summary>Swaps the face unless a newer request superseded this flip.</summary>
        internal void Complete(int generation, bool flipped)
        {
            if (_disposed || generation != _generation || _displayed == flipped) return;
            _displayed = flipped; _render?.Invoke(flipped);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; ++_generation; _render = null; // A completion arriving after disposal paints nothing.
            FluentMotion.Settle(_host);
        }
    }

    /// <summary>
    /// C20. Builds the skeleton bars once and swaps each row's bar between the library shimmer and a static bar.
    /// </summary>
    private sealed class SkeletonHost
    {
        /// <summary>The shared bar-box style the app declares for the streaming skeleton (lane F's P1 contract).</summary>
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

        private void OnLoaded(object sender, RoutedEventArgs args) => Refresh(); // Re-reads contrast; a no-op unless it changed.

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
            SubscribeContrast();
            var animated = FluentMotion.Enabled && _contrast?.HighContrast is not true;
            if (_animated == animated) return;
            _animated = animated;
            for (var index = 0; index < _rows.Length; index++)
            {
                _rows[index].Children.Clear();
                _rows[index].Children.Add(animated ? Shimmer() : Static());
            }
        }

        /// <summary>
        /// The shimmer bar. It wears the app's shared skeleton style when that style is present — lane F declares it in
        /// the next wave, and this file must keep working before that, so an absent key falls back to the library's own
        /// default. Shimmer initialises its composition only once it loads, so one is built only where it will animate.
        /// </summary>
        private static Shimmer Shimmer()
        {
            var shimmer = new Shimmer
            {
                Height = 12, CornerRadius = new CornerRadius(4), IsActive = true, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            if (Application.Current.Resources.TryGetValue(BarStyle, out var style) && style is Style shared) shimmer.Style = shared;
            return shimmer;
        }

        /// <summary>
        /// The static bar, in place of the shimmer under reduced motion or in high contrast. It is the same bar the
        /// streaming placeholder draws today, so its high-contrast appearance cannot regress; the shared style targets
        /// the shimmer control, so it is deliberately not applied here.
        /// </summary>
        private static Border Static() => new()
        {
            Height = 12, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = OpenCodeThemedXaml.BrushStyle("Border", ("Background", "SubtleFillColorSecondaryBrush")),
        };

        private void SubscribeContrast()
        {
            if (_contrast is not null) return;
            try { _contrast = new AccessibilitySettings(); _contrast.HighContrastChanged += OnContrastChanged; } catch { _contrast = null; }
        }

        private void OnContrastChanged(AccessibilitySettings sender, object args)
            => Bars.DispatcherQueue?.TryEnqueue(() => { if (_contrast is not null) Refresh(); });
    }
}