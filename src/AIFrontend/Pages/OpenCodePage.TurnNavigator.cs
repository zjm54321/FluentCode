using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using AIFrontend.Pages.OpenCode;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeTurnNavigator? _turnNavigator;
    private FrameworkElement? _messageScrollBarHost;
    private readonly TranslateTransform _messageScrollBarShift = new();
    private bool _turnNavigatorQueued;
    private bool _turnOutlineDirty = true;

    // Right gutter: the ScrollView's own native scrollbar (ScrollBarController, auto-hiding Fluent
    // indicator, composition-driven thumb), shifted from the viewport edge to beside the reading column.
    // Left gutter: the turn navigator. Both use the symmetric 56 + 8 reserve of ConversationColumn.
    private void MessageScroll_Loaded(object sender, RoutedEventArgs e)
    {
        if (_turnNavigator is null)
        {
            _turnNavigator = new OpenCodeTurnNavigator { PreviewProvider = _conversationView.TurnPreview };
            Grid.SetRow(_turnNavigator, 1);
            TimelineGrid.Children.Insert(TimelineGrid.Children.IndexOf(MessageScroll) + 1, _turnNavigator);
            _turnNavigator.TurnInvoked += NavigateToTurn;
            _conversationView.TurnOutlineChanged += (_, _) => { _turnOutlineDirty = true; QueueTurnNavigatorUpdate(); };
            MessageScroll.ViewChanged += (_, _) => QueueTurnNavigatorUpdate();
        }
        if (_messageScrollBarHost is null && FindTemplatePart(MessageScroll, "PART_VerticalScrollBar") is ScrollBar bar
            && VisualTreeHelper.GetParent(bar) is FrameworkElement host)
        {
            _messageScrollBarHost = host;
            host.RenderTransform = _messageScrollBarShift;
            host.SizeChanged += (_, _) => UpdateConversationColumns();
        }
        UpdateConversationColumns();
        _turnOutlineDirty = true;
        QueueTurnNavigatorUpdate();
    }

    private void PlaceConversationGutters(double available, double columnWidth)
    {
        if (_turnNavigator is { } navigator)
            navigator.Margin = new Thickness(OpenCodeTurnOutline.NavigatorLeft(available, columnWidth, navigator.Width), 0, 0, 0);
        if (_messageScrollBarHost is { ActualWidth: > 0 } host)
            _messageScrollBarShift.X = OpenCodeTurnOutline.ScrollBarShift(MessageScroll.ActualWidth, host.ActualWidth,
                ConversationScrollBarLeft(available, columnWidth));
    }

    private void QueueTurnNavigatorUpdate()
    {
        if (_turnNavigatorQueued || _turnNavigator is null) return;
        _turnNavigatorQueued = true;
        // One coalesced pass per dispatcher turn: streaming batches and scroll frames never stack work.
        DispatcherQueue.TryEnqueue(() =>
        {
            _turnNavigatorQueued = false;
            if (_turnNavigator is not { } navigator) return;
            if (_turnOutlineDirty)
            {
                _turnOutlineDirty = false;
                var turns = _conversationView.TurnOutline;
                navigator.SetTurns(turns, turns.Count >= 2 ? _conversationView.CurrentTurnIndex() : -1);
            }
            else if (navigator.Visibility == Visibility.Visible) navigator.SetCurrent(_conversationView.CurrentTurnIndex());
        });
    }

    private void NavigateToTurn(int turn)
    {
        var pageGeneration = _pageGeneration;
        var sessionGeneration = _sessionGeneration;
        var sessionId = _selectedSessionId;
        var historyEpoch = _messageHistoryEpoch;
        _conversationView.JumpToTurn(MessageScroll, turn,
            () => IsScrollContextCurrent(pageGeneration, sessionGeneration, sessionId) && historyEpoch == _messageHistoryEpoch);
    }

    private static FrameworkElement? FindTemplatePart(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindTemplatePart(VisualTreeHelper.GetChild(root, i), name) is { } child) return child;
        return null;
    }
}
