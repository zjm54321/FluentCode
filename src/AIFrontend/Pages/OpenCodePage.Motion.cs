using System;
using System.Numerics;
using AIFrontend.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

// Page-level Fluent motion: session switch, scroll-to-bottom pill and InfoBar entrances. Everything here animates one
// composition Visual at a time (never the transcript's children) and short-circuits under reduce-motion.
public sealed partial class OpenCodePage
{
    private int _transcriptRevealGeneration;
    private bool _transcriptHidden;

    private void InitializePageMotion()
    {
        AttachInfoBarEntrance(PermissionBar);
        AttachInfoBarEntrance(MessageBar);
        AttachOverlayEntrance(PermissionOverlayHost);
        AttachOverlayEntrance(QuestionOverlayHost);
        SessionLayoutStates.CurrentStateChanged += (_, args) => PlayLayoutEntrance(args.NewState?.Name);
        // Normal ↔ shell composer: the card fill cross-fades (167ms) instead of snapping to the terminal colour.
        FluentMotion.Bind(ComposerCard, enabled => ComposerCard.BackgroundTransition = enabled ? new BrushTransition { Duration = FluentMotion.Duration(FluentMotion.Fast) } : null);
    }

    /// <summary>
    /// Home / session / blank new-session layouts: the arriving surface rises 8px with a 250ms fade. The blank page
    /// staggers wordmark → preferences → composer by 50ms (333ms each), three elements only.
    /// </summary>
    private void PlayLayoutEntrance(string? state)
    {
        if (!FluentMotion.Enabled) return;
        switch (state)
        {
            case "HomeLayout":
                FluentMotion.SlideFadeIn(HomePageHost, new Vector3(0, 8, 0));
                break;
            case "SessionLayout":
                FluentMotion.SlideFadeIn(SessionLayoutRoot, new Vector3(0, 8, 0));
                break;
            case "NewSessionLayout":
                var step = TimeSpan.FromMilliseconds(50);
                if (NewSessionHeader.Children.Count > 0) FluentMotion.SlideFadeIn(NewSessionHeader.Children[0], new Vector3(0, 12, 0), FluentMotion.Slow);
                FluentMotion.SlideFadeIn(BlankPreferencesHost, new Vector3(0, 12, 0), FluentMotion.Slow, step);
                FluentMotion.SlideFadeIn(ComposerCell, new Vector3(0, 12, 0), FluentMotion.Slow, step * 2);
                break;
        }
    }

    // InfoBar collapses itself when IsOpen turns false (and NoticeDismissal relies on the CloseButton close reason),
    // so only the entrance is animated: drop in from −8px with a 250ms fade.
    private static void AttachInfoBarEntrance(InfoBar bar)
        => bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
        {
            if (bar.IsOpen) FluentMotion.SlideFadeIn(bar, new Vector3(0, -8, 0));
            else FluentMotion.Settle(bar);
        });

    // Permission / question cards rise 24px with a 250ms fade over the composer. Leaving stays instant: the host's
    // Visibility gates the composer's IsEnabled and the terminal airspace, which must not lag behind the decision.
    private static void AttachOverlayEntrance(UIElement host)
        => host.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (host.Visibility == Visibility.Visible) FluentMotion.SlideFadeIn(host, new Vector3(0, 24, 0));
            else FluentMotion.Settle(host);
        });

    /// <summary>
    /// Session switch, part 1: hide the transcript host instantly (one Visual; old rows are cleared synchronously by the
    /// rebind anyway) so the clear/re-bind churn is never seen. Part 2 (<see cref="RevealTranscript"/>) fades it back in.
    /// </summary>
    private void BeginTranscriptSwitchMotion()
    {
        var generation = ++_transcriptRevealGeneration;
        if (!FluentMotion.Enabled) { _transcriptHidden = false; FluentMotion.Settle(MessageHost); return; }
        _transcriptHidden = true;
        FluentMotion.Snap(MessageHost, 0f);
        // Never leave the transcript invisible: a failed/slow first load is revealed after 1.2s regardless.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1200);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { timer.Stop(); if (generation == _transcriptRevealGeneration) RevealTranscript(); };
        timer.Start();
    }

    /// <summary>
    /// Session switch, part 2: once the new window has been applied, wait for its first layout and then rise 8px with a
    /// 250ms fade. Idempotent; a no-op unless a switch hid the transcript.
    /// </summary>
    private void RevealTranscript()
    {
        if (!_transcriptHidden) return;
        _transcriptHidden = false;
        var generation = _transcriptRevealGeneration;
        var started = false;
        void Start()
        {
            if (started) return;
            started = true;
            MessageHost.LayoutUpdated -= OnLayout;
            if (generation != _transcriptRevealGeneration) return;
            FluentMotion.SlideFadeIn(MessageHost, new Vector3(0, 8, 0));
        }
        void OnLayout(object? sender, object args) => Start();
        MessageHost.LayoutUpdated += OnLayout;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Start);
    }

    /// <summary>Scroll-to-bottom pill: appears rising 12px with scale 0.9→1 (167ms), leaves sinking 8px (83ms). Only on real changes.</summary>
    private void SetScrollToBottomVisible(bool visible)
    {
        if (visible) FluentMotion.Show(ScrollToBottomButton, new Vector3(0, 12, 0), FluentMotion.Fast, fromScale: 0.9f);
        else FluentMotion.Hide(ScrollToBottomButton, new Vector3(0, 8, 0));
    }

    /// <summary>
    /// Jump-to-bottom click: animate with the ScrollView's own curve when within three viewports; farther jumps stay instant
    /// (an animated scroll across a long transcript realizes every row on the way).
    /// </summary>
    private bool ShouldAnimateJumpToBottom()
        => FluentMotion.Enabled && MessageScroll.ScrollableHeight - MessageScroll.VerticalOffset <= Math.Max(1, MessageScroll.ViewportHeight) * 3;
}
