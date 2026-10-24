using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using AIFrontend.Pages.OpenCode;

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
        _annotatedSources.InsertScalar("MaxOffset", 0);
        _annotatedSources.InsertScalar("Offset", 0);
        // WinUI writes Multiplier itself from its measured rail and our SetValues range.
        // Never replace it with a fixed ratio or a second thumb-position expression.
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
        var position = OpenCodeScrollBarProjection.Position(MessageScroll.ScrollableHeight, MessageScroll.VerticalOffset);
        controller.SetIsScrollable(position.Maximum > 0);
        controller.SetValues(0, position.Maximum, position.Offset, position.ViewportLength);
        // SetValues updates native state, but only the panning expression moves the blue thumb.
        // Feed both paths the same live offset after every view/layout/window-height change.
        _annotatedSources?.InsertScalar("MaxOffset", (float)position.Maximum);
        _annotatedSources?.InsertScalar("Offset", (float)position.Offset);
        var annotations = _conversationView.ScrollAnnotations()
            .Select(item => (item.Text, item.User, Offset: OpenCodeScrollBarProjection.ScrollTarget(item.Offset, position.Maximum)))
            .ToArray();
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
        // Native DetailLabelTemplate binds Content as text, not as a child UIElement.
        args.Content = OpenCodeScrollBarProjection.DetailText(item.User, item.Text);
    }
}
