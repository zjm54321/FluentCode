using System;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private readonly TextBlock _composerMeasure = new() { TextWrapping = TextWrapping.Wrap };
    private bool _composerLayoutPending;
    private bool _composerOverFiveLines;

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
        // Two fixed rows: only the input's own fullscreen corner consumes editor width.
        var editorWidth = Math.Max(1, ComposerInputLayout.ActualWidth - horizontalChrome);
        measure.Text = Composer.Text + "\u200b"; // Preserve a trailing empty line in native text measure.
        measure.Measure(new Size(editorWidth, double.PositiveInfinity));
        var maximum = _blankComposerPlacement ? Math.Max(180, line * 8 + chrome) : OpenCodeComposerHeightPolicy.ViewportHeight(line, chrome);
        // Reserve room for the naturally measured official brand and external preferences on short windows.
        // Normal-session five-line sizing is unchanged; this is only the empty centered host's initial height.
        var blankMinimum = Math.Clamp(SessionLayoutRoot.ActualHeight * 0.24, 104, 156);
        Composer.MinHeight = _blankComposerPlacement ? Math.Min(blankMinimum, maximum) : 64;
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
