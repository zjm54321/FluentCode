using System;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private bool IsChildComposerSession => !_newSessionDraftActive && !_homeActive
        && ReferenceEquals(_agentSelectorSource, _source) && _agentSelectorRevision == _sourceRevision
        && _agentSelectorSessionId == _selectedSessionId
        && _agentSelector.CurrentSessionInfo is { ParentId: not null } child && child.Id == _selectedSessionId;

    private void RefreshChildComposerVisibility()
    {
        var child = IsChildComposerSession;
        ComposerCell.Visibility = child ? Visibility.Collapsed : Visibility.Visible;
        ComposerQueueHost.Visibility = child ? Visibility.Collapsed : Visibility.Visible;
        if (child) ExitShellComposer(focus: false);
    }
    private readonly TextBlock _composerMeasure = new() { TextWrapping = TextWrapping.Wrap };
    private bool _composerLayoutPending;
    private bool _composerOverFiveLines;

    internal static bool UseSingleRowComposer(bool newSession, bool wide) => !newSession && wide;

    private void ArrangeComposerRows(bool singleRow)
    {
        // Change attached layout properties only; never detach the editor or its host.
        Grid.SetColumn(ComposerEditorHost, singleRow ? 1 : 0);
        Grid.SetColumnSpan(ComposerEditorHost, singleRow ? 1 : 3);
        Grid.SetRow(ComposerToolbarLeft, singleRow ? 0 : 1);
        Grid.SetRow(ComposerActions, singleRow ? 0 : 1);
        ComposerInputLayout.RowSpacing = singleRow ? 0 : 8;
        BlankToolbarChoices.Visibility = singleRow ? Visibility.Collapsed : Visibility.Visible;
        ComposerToolbarMiddle.Visibility = !singleRow && !_newSessionDraftActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InitializeComposerLayout()
    {
        Composer.TextChanged += (_, _) => QueueComposerLayout();
        ComposerInputLayout.SizeChanged += (_, _) => QueueComposerLayout();
        Composer.SizeChanged += (_, _) => QueueComposerLayout();
        Composer.Loaded += (_, _) => QueueComposerLayout();
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
            if (_isComposing || ComposerInputLayout.ActualWidth <= 0) return;
            UpdateComposerLayout();
        });
    }

    private void UpdateComposerLayout()
    {
        UpdateConversationColumns();
        var singleRow = UseSingleRowComposer(_newSessionDraftActive, _sidebarLayout.IsWide);
        ArrangeComposerRows(singleRow);
        UpdateComposerToolbarLayout();
        var measure = _composerMeasure;
        measure.FontFamily = Composer.FontFamily;
        measure.FontSize = Composer.FontSize;
        measure.FontWeight = Composer.FontWeight;
        measure.FontStyle = Composer.FontStyle;
        measure.CharacterSpacing = Composer.CharacterSpacing;
        measure.IsTextScaleFactorEnabled = Composer.IsTextScaleFactorEnabled;
        measure.FlowDirection = Composer.FlowDirection;
        measure.Language = Composer.Language;
        measure.Text = "Mg";
        measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var line = Math.Max(1, measure.DesiredSize.Height);
        var padding = Composer.Padding;
        var stroke = Composer.BorderThickness;
        var chrome = padding.Top + padding.Bottom + stroke.Top + stroke.Bottom;
        var horizontalChrome = padding.Left + padding.Right + stroke.Left + stroke.Right;
        // Measure the editor's own arranged width: wide sessions share its row with +/send.
        var editorWidth = Math.Max(1, ComposerEditorHost.ActualWidth - horizontalChrome);
        measure.Text = Composer.Text + "\u200b"; // Preserve a trailing empty line in native text measure.
        measure.Measure(new Size(editorWidth, double.PositiveInfinity));
        var maximum = _blankComposerPlacement ? Math.Max(180, line * 8 + chrome) : OpenCodeComposerHeightPolicy.ViewportHeight(line, chrome);
        // Reserve room for the naturally measured official brand and external preferences on short windows.
        // Normal-session five-line sizing is unchanged; this is only the empty centered host's initial height.
        var blankMinimum = Math.Clamp(SessionLayoutRoot.ActualHeight * 0.24, 104, 156);
        Composer.MinHeight = _blankComposerPlacement ? Math.Min(blankMinimum, maximum) : singleRow ? line + chrome : 64;
        if (Math.Abs(Composer.MaxHeight - maximum) > 0.5) Composer.MaxHeight = maximum;
        _composerOverFiveLines = OpenCodeComposerHeightPolicy.CanExpand(measure.DesiredSize.Height, line);
        ComposerExpandButton.Visibility = _composerOverFiveLines ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static (double Width, double RightGutter) ConversationColumn(double available, double viewport, double maximum, bool blank)
    {
        available = Math.Max(0, available);
        var gutter = !blank && viewport > 0 ? Math.Max(0, available - viewport) : 0;
        return (Math.Min(Math.Max(0, available - gutter), maximum), gutter);
    }

    private void UpdateConversationColumns()
    {
        var padding = SessionLayoutRoot.Padding;
        var available = SessionLayoutRoot.ActualWidth - padding.Left - padding.Right;
        if (available <= 0) return;
        // A single finite reading column, relative to the live pane content/viewport, not the window.
        // Normal mode explicitly uses the reading limit, never an inherited blank MaxWidth.
        var maximum = _blankComposerPlacement ? ComposerCell.MaxWidth : MessageHost.MaxWidth;
        var column = ConversationColumn(available, MessageScroll.ViewportWidth, maximum, _blankComposerPlacement);
        ComposerCell.Width = column.Width;
        ComposerCell.Margin = new Thickness(0, 8, column.RightGutter, 0);
        if (!_blankComposerPlacement) MessageHost.Width = column.Width;
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
        var ticket = _composerController.TryTakeDraftEditorAction(GetComposerSnapshot().Token);
        if (ticket is not null) DispatchClientAction(ticket);
    }
}
