using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private IReadOnlyList<(string Text, bool User, double Offset)> _scrollAnnotations = Array.Empty<(string, bool, double)>();
    private CompositionPropertySet? _annotatedSources;

    // Native AnnotatedScrollBar controller, bridged to the existing single ScrollViewer owner.
    // Keeping that owner preserves pagination ACKs, idle visibility proof and virtual-window anchors.
    private void ConversationScrollBar_Loaded(object sender, RoutedEventArgs e)
    {
        _annotatedSources = ElementCompositionPreview.GetElementVisual(ConversationScrollBar).Compositor.CreatePropertySet();
        _annotatedSources.InsertScalar("MinOffset", 0);
        _annotatedSources.InsertScalar("MaxOffset", (float)MessageScroll.ScrollableHeight);
        _annotatedSources.InsertScalar("Offset", (float)MessageScroll.VerticalOffset);
        _annotatedSources.InsertScalar("Multiplier", -1);
        // The native panning implementation owns its thumb expression and movement ratio.
        ConversationScrollBar.ScrollController.PanningInfo?.SetPanningElementExpressionAnimationSources(
            _annotatedSources, "MinOffset", "MaxOffset", "Offset", "Multiplier");
        MessageScroll.ViewChanged += AnnotatedViewportChanged;
        MessageScroll.LayoutUpdated += AnnotatedLayoutUpdated;
        UpdateAnnotatedScrollBar();
    }

    private void ConversationScrollBar_Unloaded(object sender, RoutedEventArgs e)
    {
        MessageScroll.ViewChanged -= AnnotatedViewportChanged;
        MessageScroll.LayoutUpdated -= AnnotatedLayoutUpdated;
        ConversationScrollBar.ScrollController.PanningInfo?.SetPanningElementExpressionAnimationSources(null!, "", "", "", "");
        _annotatedSources?.Dispose();
        _annotatedSources = null;
        _scrollAnnotations = Array.Empty<(string, bool, double)>();
    }

    private void AnnotatedViewportChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateAnnotatedScrollBar();
    private void AnnotatedLayoutUpdated(object? sender, object e) => UpdateAnnotatedScrollBar();

    private void UpdateAnnotatedScrollBar()
    {
        var controller = ConversationScrollBar.ScrollController;
        controller.SetIsScrollable(MessageScroll.ScrollableHeight > 0);
        controller.SetValues(0, Math.Max(0, MessageScroll.ScrollableHeight), MessageScroll.VerticalOffset, MessageScroll.ViewportHeight);
        _annotatedSources?.InsertScalar("MaxOffset", (float)Math.Max(0, MessageScroll.ScrollableHeight));
        _annotatedSources?.InsertScalar("Offset", (float)MessageScroll.VerticalOffset);
        var annotations = _conversationView.ScrollAnnotations();
        if (annotations.SequenceEqual(_scrollAnnotations)) return;
        _scrollAnnotations = annotations;
        ConversationScrollBar.Labels.Clear();
        foreach (var item in annotations)
            // The template draws only a dash; retain role + full text for its accessible name.
            ConversationScrollBar.Labels.Add(new AnnotatedScrollBarLabel($"{(item.User ? "你" : "AI 回复")}：{item.Text}", item.Offset));
    }

    private void ConversationScrollBar_Scrolling(AnnotatedScrollBar sender, AnnotatedScrollBarScrollingEventArgs args)
    {
        // Native track/buttons/drag compute the requested offset; no custom rail or hit-testing.
        args.Cancel = true;
        MessageScroll.ChangeView(null, Math.Clamp(args.ScrollOffset, 0, MessageScroll.ScrollableHeight), null, disableAnimation: true);
    }

    private void ConversationScrollBar_DetailLabelRequested(AnnotatedScrollBar sender, AnnotatedScrollBarDetailLabelRequestedEventArgs args)
    {
        if (_scrollAnnotations.Count == 0) return;
        var item = _scrollAnnotations.OrderBy(a => Math.Abs(a.Offset - args.ScrollOffset)).First();
        var body = new StackPanel { Spacing = 4, MaxWidth = 260 };
        body.Children.Add(new TextBlock { Text = item.User ? "你" : "AI 回复", FontSize = 12 });
        body.Children.Add(new TextBlock { Text = item.Text, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2 });
        args.Content = body;
    }
}
