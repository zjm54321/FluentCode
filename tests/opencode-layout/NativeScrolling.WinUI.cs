// Compile-only API boundary against the cached real WinUI assemblies; never run.
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage : Page
{
    private readonly ScrollView MessageScroll = new();
    private readonly AnnotatedScrollBar ConversationScrollBar = new();
    private readonly ConversationProbe _conversationView = new();

    // Match the production conversation's native offset/state event and request APIs.
    private static void VerifyScrollingApi(ScrollView viewport)
    {
        viewport.ViewChanged += Changed;
        viewport.StateChanged += Changed;
        _ = viewport.State is ScrollingInteractionState.Interaction or ScrollingInteractionState.Inertia;
        _ = viewport.ScrollTo(viewport.HorizontalOffset, viewport.VerticalOffset,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        viewport.ViewChanged -= Changed;
        viewport.StateChanged -= Changed;
    }
    private static void Changed(ScrollView sender, object args) { }
    private sealed class ConversationProbe
    {
        internal IReadOnlyList<(string Text, bool User, double Offset)> ScrollAnnotations() => Array.Empty<(string, bool, double)>();
    }
}
