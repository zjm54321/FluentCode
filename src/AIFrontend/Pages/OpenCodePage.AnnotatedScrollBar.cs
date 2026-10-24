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
    private FrameworkElement? _annotatedHitArea;
    private (double X, double Y, ulong Timestamp)? _annotatedPointerPosition;
    private bool _annotatedPointerInside;

    // Native AnnotatedScrollBar controller, bridged to the existing single ScrollViewer owner.
    // Keeping that owner preserves pagination ACKs, idle visibility proof and virtual-window anchors.
    private void ConversationScrollBar_Loaded(object sender, RoutedEventArgs e)
    {
        ConversationScrollBar.ApplyTemplate();
        _annotatedHitArea = FindAnnotatedPart(ConversationScrollBar, "PART_VerticalGrid");
        if (FindAnnotatedPart(ConversationScrollBar, "PART_ToolTipRail") is { } rail)
            _annotatedDetailToolTip = ToolTipService.GetToolTip(rail) as ToolTip;
        if (_annotatedDetailToolTip is { } toolTip)
        {
            // Public UIElement property on the existing native ToolTip; the pill must not own hover input.
            toolTip.IsHitTestVisible = false;
            toolTip.Opened += AnnotatedDetailToolTipOpened;
        }
        // Observe handled child events too: native pointer capture/tooltip ownership can swallow ordinary handlers.
        ConversationScrollBar.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(AnnotatedPointerMoved), true);
        ConversationScrollBar.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(AnnotatedPointerMoved), true);
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
        ConversationScrollBar.RemoveHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(AnnotatedPointerMoved));
        ConversationScrollBar.RemoveHandler(UIElement.PointerMovedEvent, new PointerEventHandler(AnnotatedPointerMoved));
        ConversationScrollBar.RemoveHandler(UIElement.PointerExitedEvent, new PointerEventHandler(AnnotatedPointerExited));
        ConversationScrollBar.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(AnnotatedPointerCaptureLost));
        if (_annotatedDetailToolTip is { } toolTip) toolTip.Opened -= AnnotatedDetailToolTipOpened;
        _annotatedDetailToolTip = null;
        _annotatedHitArea = null;
        _annotatedPointerPosition = null;
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
        if (!_annotatedPointerInside || !AnnotatedPointerIsInHitArea() || _scrollAnnotations.Count == 0) return;
        var item = _scrollAnnotations.OrderBy(a => Math.Abs(a.Offset - args.ScrollOffset)).First();
        // Native DetailLabelTemplate binds Content as text, not as a child UIElement.
        args.Content = OpenCodeScrollBarProjection.DetailText(item.User, item.Text);
    }

    private void AnnotatedPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (UpdateAnnotatedPointerPosition(args) && !_annotatedPointerInside) CloseAnnotatedDetail();
    }

    private void AnnotatedPointerExited(object sender, PointerRoutedEventArgs args)
    {
        // Test the native interaction grid, not the control's padding/button rows or the popup.
        // Genuine child-to-child exits inside that grid retain hover; left/right exits both invalidate it.
        if (UpdateAnnotatedPointerPosition(args) && !_annotatedPointerInside) CloseAnnotatedDetail();
    }

    private void AnnotatedPointerCaptureLost(object sender, PointerRoutedEventArgs args) => CloseAnnotatedDetail();

    private bool UpdateAnnotatedPointerPosition(PointerRoutedEventArgs args)
    {
        if (_annotatedHitArea is not { } hitArea) return false;
        var point = args.GetCurrentPoint(hitArea);
        // Input timestamps are monotonic microseconds. A late enter/move must not undo a newer exit.
        if (_annotatedPointerPosition is { } previous && point.Timestamp < previous.Timestamp) return false;
        _annotatedPointerPosition = (point.Position.X, point.Position.Y, point.Timestamp);
        _annotatedPointerInside = AnnotatedPointerIsInHitArea();
        return true;
    }

    private bool AnnotatedPointerIsInHitArea()
    {
        // Match WinUI's IsOutOfVerticalGridBounds 1px rounding tolerance, including the left edge.
        return _annotatedHitArea is { } hitArea && _annotatedPointerPosition is { } point
            && point.X >= 1 && point.X <= hitArea.ActualWidth - 1
            && point.Y >= 1 && point.Y <= hitArea.ActualHeight - 1;
    }

    private void AnnotatedDetailToolTipOpened(object sender, RoutedEventArgs args)
    {
        // ToolTipService can complete a delayed open after the native exit close; do not leave a stale pill.
        if (!_annotatedPointerInside || !AnnotatedPointerIsInHitArea()) CloseAnnotatedDetail();
    }

    private void CloseAnnotatedDetail()
    {
        _annotatedPointerInside = false;
        // Keep the native template and close its native ToolTip via the documented IsOpen API.
        // No SetValues, label mutation, replacement popup, or template re-application on pointer exit.
        if (_annotatedDetailToolTip is { } toolTip) toolTip.IsOpen = false;
    }

    private static FrameworkElement? FindAnnotatedPart(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindAnnotatedPart(VisualTreeHelper.GetChild(root, i), name) is { } child) return child;
        return null;
    }
}
