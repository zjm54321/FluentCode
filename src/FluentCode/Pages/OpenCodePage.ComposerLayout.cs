using System;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    internal static bool HideChildComposer(string? selectedSessionId, string? infoSessionId, string? parentId)
        => selectedSessionId is not null && selectedSessionId == infoSessionId && parentId is not null;

    private bool IsChildComposerSession => !_newSessionDraftActive && !_homeActive
        && ReferenceEquals(_agentSelectorSource, _source) && _agentSelectorRevision == _sourceRevision
        && _agentSelectorSessionId == _selectedSessionId
        && _agentSelector.CurrentSessionInfo is { } info && HideChildComposer(_selectedSessionId, info.Id, info.ParentId);

    private void RefreshChildComposerVisibility()
    {
        var child = IsChildComposerSession;
        ComposerCell.Visibility = child && !ComposerCoveredByOverlay ? Visibility.Collapsed : Visibility.Visible;
        ComposerCard.Visibility = child ? Visibility.Collapsed : Visibility.Visible;
        ComposerQueueHost.Visibility = child ? Visibility.Collapsed : Visibility.Visible;
        RefreshChildStopButton();
        RefreshChildReturnButton();
        if (child) ExitShellComposer(focus: false);
        if (FullScreenComposerActive && (child || _fullScreenBinding is not { } binding || !OwnsComposerBinding(binding)))
            CloseFullScreenComposer(focus: false);
    }
    private bool _composerLayoutPending;
    private bool _composerOverFiveLines;
    private bool _composerOverTwoLines;

    internal static bool UseSingleRowComposer(bool newSession, bool wide, bool overTwoLines) => !newSession && wide && !overTwoLines;

    internal static bool ShowComposerToolbarChoices(bool newSession, bool wide) => newSession || !wide;

    internal static bool ComposerOverTwoLines(double height, double line, bool wasOverTwoLines)
        => height > 2 * line + (wasOverTwoLines ? 0 : 0.5);

    internal static double ComposerSingleRowTextWidth(double layoutWidth, double leftWidth, double actionsWidth, double spacing, double chrome)
        => Math.Max(1, layoutWidth - leftWidth - actionsWidth - 2 * spacing - chrome);

    private void ArrangeComposerRows(bool singleRow)
    {
        // Change attached layout properties only; never detach the editor or its host.
        Grid.SetColumn(ComposerEditorHost, singleRow ? 1 : 0);
        Grid.SetColumnSpan(ComposerEditorHost, singleRow ? 1 : 3);
        Grid.SetRow(ComposerToolbarLeft, singleRow ? 0 : 1);
        Grid.SetRow(ComposerActions, singleRow ? 0 : 1);
        // Compact one/two-line drafts keep their actions beside the editor; longer drafts put them below.
        var lastLine = singleRow ? new Thickness(0, 0, 0, 4) : new Thickness(0);
        ComposerToolbarLeft.Margin = lastLine;
        ComposerActions.Margin = lastLine;
        ComposerActions.VerticalAlignment = singleRow ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        ComposerInputLayout.RowSpacing = singleRow ? 0 : 8;
        // Resident wide-session panels own these controls even when a long draft moves actions below.
        var choices = ShowComposerToolbarChoices(_newSessionDraftActive, _sidebarLayout.IsWide);
        BlankToolbarChoices.Visibility = choices ? Visibility.Visible : Visibility.Collapsed;
        ComposerToolbarMiddle.Visibility = choices && !_newSessionDraftActive ? Visibility.Visible : Visibility.Collapsed;
        ComposerChoiceSpacer.Width = _newSessionDraftActive ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ComposerStatusColumn.Width = _newSessionDraftActive ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }

    private void InitializeComposerLayout()
    {
        Composer.TextChanged += (_, _) => QueueComposerLayout();
        ComposerInputLayout.SizeChanged += (_, _) => QueueComposerLayout();
        Composer.SizeChanged += (_, _) => QueueComposerLayout();
        Composer.Loaded += (_, _) => QueueComposerLayout();
        Composer.NativeLayoutReady += QueueComposerLayout;
        Composer.TextCompositionEnded += (_, _) => QueueComposerLayout();
        Composer.RegisterPropertyChangedCallback(Control.FontSizeProperty, (_, _) => QueueComposerLayout());
        Composer.RegisterPropertyChangedCallback(Control.FontFamilyProperty, (_, _) => QueueComposerLayout());
        ComposerLeadingHost.SizeChanged += (_, _) => QueueComposerLayout();
        ComposerActions.SizeChanged += (_, _) => QueueComposerLayout();
        QueueComposerLayout();
    }

    private void QueueComposerLayout()
    {
        if (_composerLayoutPending) return;
        _composerLayoutPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _composerLayoutPending = false;
            if (_isComposing || FullScreenComposerActive || ComposerInputLayout.ActualWidth <= 0) return;
            try { UpdateComposerLayout(); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                System.Diagnostics.Debug.WriteLine($"Composer layout: {error.GetType().Name} HRESULT=0x{error.HResult:X8}");
                // A rejected native metric must not escape the dispatcher callback. Keep the
                // current viewport; TextChanged/NativeLayoutReady will remeasure a stable surface.
            }
        });
    }

    private void UpdateComposerLayout()
    {
        UpdateConversationColumns();
        var line = Composer.LineMetrics.Height;
        // The compact toolbar-row projection always uses the base inset, independent of its output.
        var padding = ComposerTextPadding(expandVisible: false);
        var stroke = Composer.BorderThickness;
        var chrome = padding.Top + padding.Bottom + stroke.Top + stroke.Bottom;
        var horizontalChrome = padding.Left + padding.Right + stroke.Left + stroke.Right;
        // Always test the hypothetical single-row width, even while the editor spans all columns.
        // Testing the expanded editor would otherwise bounce between two and three wrapped lines.
        var singleRowWidth = ComposerSingleRowTextWidth(ComposerInputLayout.ActualWidth,
            ComposerToolbarLeft.ActualWidth, ComposerActions.ActualWidth, ComposerInputLayout.ColumnSpacing, horizontalChrome);
        var compactHeight = Composer.MeasureCompactContentHeight(singleRowWidth);
        _composerOverTwoLines = !_newSessionDraftActive && _sidebarLayout.IsWide
            && ComposerOverTwoLines(compactHeight, line, _composerOverTwoLines);
        var singleRow = UseSingleRowComposer(_newSessionDraftActive, _sidebarLayout.IsWide, _composerOverTwoLines);
        ArrangeComposerRows(singleRow);
        UpdateComposerToolbarLayout();
        // Grow/expand/scrollbar decisions use only the native surface's actually arranged width.
        var contentHeight = Math.Max(line, Composer.MeasureContentHeight() - chrome);
        var maximum = OpenCodeComposerHeightPolicy.ViewportHeight(line, chrome);
        Composer.MinHeight = OpenCodeComposerHeightPolicy.MinimumHeight(contentHeight, line, chrome);
        if (Math.Abs(Composer.MaxHeight - maximum) > 0.5) Composer.MaxHeight = maximum;
        _composerOverFiveLines = OpenCodeComposerHeightPolicy.CanExpand(contentHeight, line);
        ComposerExpandButton.Visibility = _composerOverFiveLines ? Visibility.Visible : Visibility.Collapsed;
        var textPadding = ComposerTextPadding(_composerOverFiveLines);
        if (Composer.Padding != textPadding) Composer.Padding = textPadding; // Shell editor binds to this padding.
        // Hidden scrollbars reserve no width. Only a capped draft needs a vertical scrollbar;
        // unlike Disabled, Hidden still permits native caret scrolling.
        ScrollViewer.SetVerticalScrollBarVisibility(Composer, contentHeight + chrome > maximum
            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
    }

    // Symmetric 12 so text fills the editor; the right inset grows to 44 (button 32 + margin 4 + gap 8) only while the
    // expand button is shown over the text. A fixed 44 used to leave an empty strip at the right edge of every draft.
    internal static Thickness ComposerTextPadding(bool expandVisible) => new(12, 9, expandVisible ? 44 : 12, 9);

    internal static (double Width, double RightGutter) ConversationColumn(double available, double maximum, bool blank)
    {
        available = Math.Max(0, available);
        // SB1: keep the reading column/composer centered in the full-width scroll viewport.
        // Reserve the bar (56) + gap (8) symmetrically, instead of shifting text left by a right gutter.
        var reserved = blank ? 0 : 2 * (56 + 8);
        return (Math.Min(Math.Max(0, available - reserved), maximum), 0);
    }

    internal static double ConversationScrollBarLeft(double gridWidth, double hostWidth)
    {
        return Math.Clamp((gridWidth + hostWidth) / 2 + 8, 0, Math.Max(0, gridWidth - 56));
    }

    private void TimelineColumn_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateConversationColumns();

    private void UpdateConversationColumns()
    {
        var padding = SessionLayoutRoot.Padding;
        var available = SessionLayoutRoot.ActualWidth - padding.Left - padding.Right;
        if (!_blankComposerPlacement && TimelineGrid.ActualWidth > 0) available = TimelineGrid.ActualWidth;
        if (available <= 0) return;
        // A single finite reading column, relative to the live pane content/viewport, not the window.
        // Normal mode explicitly uses the reading limit, never an inherited blank MaxWidth.
        var maximum = _blankComposerPlacement ? ComposerCell.MaxWidth : MessageHost.MaxWidth;
        var column = ConversationColumn(available, maximum, _blankComposerPlacement);
        ComposerCell.Width = column.Width;
        ComposerCell.Margin = new Thickness(0, 8, column.RightGutter, 0);
        if (!_blankComposerPlacement)
        {
            MessageHost.Width = column.Width;
            PlaceConversationGutters(available, column.Width);
        }
    }

    private void UpdateComposerSecondaryActions(bool working, bool canSend, OpenCodeComposerSnapshot snapshot)
    {
        var queue = _composerDeliverySupported && working && snapshot.HasDraft && snapshot.SelectedCommand is null;
        ComposerQueueButton.Visibility = queue ? Visibility.Visible : Visibility.Collapsed;
        ComposerQueueButton.IsEnabled = queue && canSend;
        ComposerExpandButton.IsEnabled = snapshot.IsActive && !snapshot.IsBusy && !_isSending
            && !HasPendingNativeAction() && !ComposerCoveredByOverlay;
        QueueComposerLayout();
    }

    private void ComposerQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ComposerQueueButton.IsEnabled || !_composerDeliverySupported || _selectedSessionId is not { } selected
            || !IsComposerSessionWorking(selected)) return;
        RequestComposerSubmission(OpenCodeDeliveryModes.Queue);
    }

    private void ComposerExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_composerOverFiveLines || !ComposerExpandButton.IsEnabled || !_pageActive || _composerView is null
            || _composerController is null || !_composerView.PrepareSubmission() || HasPendingNativeAction()) return;
        OpenFullScreenComposer();
    }
}
