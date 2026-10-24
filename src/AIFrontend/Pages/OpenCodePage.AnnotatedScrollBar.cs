using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using AIFrontend.Pages.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private IReadOnlyList<(string Text, bool User, double Offset)> _scrollAnnotations = Array.Empty<(string, bool, double)>();
    private CompositionPropertySet? _annotatedSources;
    private (double Maximum, double Offset, double ViewportLength)? _annotatedPosition;
    private ToolTip? _annotatedDetailToolTip;
    private bool _annotatedPointerInside;

    // Native AnnotatedScrollBar controller, bridged to the existing single ScrollViewer owner.
    // Keeping that owner preserves pagination ACKs, idle visibility proof and virtual-window anchors.
    private void ConversationScrollBar_Loaded(object sender, RoutedEventArgs e)
    {
        ConversationScrollBar.ApplyTemplate();
        _annotatedDetailToolTip = FindAnnotatedDetailToolTip(ConversationScrollBar);
        if (_annotatedDetailToolTip is { } toolTip) toolTip.Opened += AnnotatedDetailToolTipOpened;
        ConversationScrollBar.PointerEntered += AnnotatedPointerEntered;
        // Observe handled child events too: native pointer capture/tooltip ownership can swallow ordinary handlers.
        ConversationScrollBar.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(AnnotatedPointerExited), true);
        ConversationScrollBar.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(AnnotatedPointerCaptureLost), true);
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
        CloseAnnotatedDetail();
        ConversationScrollBar.PointerEntered -= AnnotatedPointerEntered;
        ConversationScrollBar.RemoveHandler(UIElement.PointerExitedEvent, new PointerEventHandler(AnnotatedPointerExited));
        ConversationScrollBar.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(AnnotatedPointerCaptureLost));
        if (_annotatedDetailToolTip is { } toolTip) toolTip.Opened -= AnnotatedDetailToolTipOpened;
        _annotatedDetailToolTip = null;
        MessageScroll.ViewChanged -= AnnotatedViewportChanged;
        MessageScroll.LayoutUpdated -= AnnotatedLayoutUpdated;
        ConversationScrollBar.ScrollController.PanningInfo?.SetPanningElementExpressionAnimationSources(null!, "", "", "", "");
        _annotatedSources?.Dispose();
        _annotatedSources = null;
        _annotatedPosition = null;
        _scrollAnnotations = Array.Empty<(string, bool, double)>();
    }

    private void AnnotatedViewportChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateAnnotatedScrollBar();
    private void AnnotatedLayoutUpdated(object? sender, object e) => UpdateAnnotatedScrollBar();

    private void UpdateAnnotatedScrollBar()
    {
        var controller = ConversationScrollBar.ScrollController;
        var position = OpenCodeScrollBarProjection.Position(MessageScroll.ScrollableHeight, MessageScroll.VerticalOffset);
        if (_annotatedPosition != position)
        {
            _annotatedPosition = position;
            controller.SetIsScrollable(position.Maximum > 0);
            controller.SetValues(0, position.Maximum, position.Offset, position.ViewportLength);
            _annotatedSources?.InsertScalar("MaxOffset", (float)position.Maximum);
            _annotatedSources?.InsertScalar("Offset", (float)position.Offset);
        }
        // SetValues updates native state, but only the panning expression moves the blue thumb.
        // Feed both paths the same live offset after every view/layout/window-height change.
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
        if (!_annotatedPointerInside || _scrollAnnotations.Count == 0) return;
        var item = _scrollAnnotations.OrderBy(a => Math.Abs(a.Offset - args.ScrollOffset)).First();
        // Native DetailLabelTemplate binds Content as text, not as a child UIElement.
        args.Content = OpenCodeScrollBarProjection.DetailText(item.User, item.Text);
    }

    private void AnnotatedPointerEntered(object sender, PointerRoutedEventArgs args) => _annotatedPointerInside = true;

    private void AnnotatedPointerExited(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(ConversationScrollBar).Position;
        // Routed exits between template children (or into a tooltip overlapping the rail) are not rail exits.
        if (point.X >= 0 && point.X < ConversationScrollBar.ActualWidth
            && point.Y >= 0 && point.Y < ConversationScrollBar.ActualHeight) return;
        CloseAnnotatedDetail();
    }

    private void AnnotatedPointerCaptureLost(object sender, PointerRoutedEventArgs args) => CloseAnnotatedDetail();

    private void AnnotatedDetailToolTipOpened(object sender, RoutedEventArgs args)
    {
        // ToolTipService can complete a delayed open after the native exit close; do not leave a stale pill.
        if (!_annotatedPointerInside) CloseAnnotatedDetail();
    }

    private void CloseAnnotatedDetail()
    {
        _annotatedPointerInside = false;
        // Keep the native template and close its native ToolTip via the documented IsOpen API.
        // No SetValues, label mutation, replacement popup, or template re-application on pointer exit.
        if (_annotatedDetailToolTip is { } toolTip) toolTip.IsOpen = false;
    }

    private static ToolTip? FindAnnotatedDetailToolTip(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "PART_ToolTipRail" } rail
            && ToolTipService.GetToolTip(rail) is ToolTip toolTip) return toolTip;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindAnnotatedDetailToolTip(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
