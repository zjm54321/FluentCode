using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Confirmation before sending from a session that is viewing a staged undo. Native Fluent ContentDialog; the default
/// (and Enter/Esc) path is 取消, confirmation needs the explicit “继续发送” button. It never sends, commits, restores or
/// edits drafts, holds no controller/client/lease, and returns the exact presentation it was shown with. The caller must
/// invoke it on the owner's UI thread and re-validate stage + draft under its lease after the await.
/// </summary>
internal static class OpenCodeComposerContinuationDialog
{
    /// <param name="owner">Owning XamlRoot (no Window.Current fallback). Null → OwnerUnavailable.</param>
    /// <param name="cancellation">Optional: the page cancels when the stage/session/draft it read changes → Stale.</param>
    internal static async Task<OpenCodeContinuationResult> ShowAsync(XamlRoot? owner,
        OpenCodeContinuationPresentation presentation, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        var state = new OpenCodeContinuationConfirmationState(presentation);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (owner is null || dispatcher is null) return state.FailOwner();
        if (cancellation.IsCancellationRequested) { state.MarkStale(); return state.Finish(); }

        var body = new StackPanel { Spacing = 10, MinWidth = 340, MaxWidth = 520 };
        if (presentation.SessionLabel.Length > 0) body.Children.Add(OpenCodeInteractionUi.Caption(presentation.SessionLabel, 0.75));
        var warnings = new StackPanel { Spacing = 6 };
        foreach (var w in state.Warnings())
        {
            var line = new Grid { ColumnSpacing = 6 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon
            {
                Glyph = "\uE7BA",
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Style = OpenCodeInteractionUi.TryStyle("OpenCodeCautionIconStyle"),
            };
            var text = OpenCodeInteractionUi.Caption(w, 0.85);
            text.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(text, 1);
            line.Children.Add(icon);
            line.Children.Add(text);
            warnings.Children.Add(line);
        }
        AutomationProperties.SetName(warnings, "继续发送前的风险说明");
        body.Children.Add(warnings);

        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = new TextBlock { Text = presentation.HasStagedRevert ? "从撤回处继续发送？" : "撤回状态已变化，继续发送？", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "继续发送",
            CloseButtonText = "取消",
            // Focus/Enter land on 取消: a held or IME-confirm Enter carried over from the composer can only cancel.
            DefaultButton = ContentDialogButton.Close,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        AutomationProperties.SetAutomationId(dialog, "OpenCodeContinuationDialog");
        bool closingOrFinished = false;
        void FinishClosing()
        {
            closingOrFinished = true;
            state.Finish(); // Synchronous terminal seam: a late primary/cancellation cannot replace this decision.
        }
        dialog.CloseButtonClick += (_, _) => FinishClosing();
        dialog.Closing += (_, _) => FinishClosing();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // The explicit gesture; refused once anything else completed this dialog (stale/closing).
            if (!state.TryConfirm()) args.Cancel = true;
        };

        // Caller cancellation may fire on any thread; it completes Stale (first wins) and closes on the UI thread.
        // After the dialog finished, both the completion and the hide are inert.
        CancellationTokenRegistration registration = default;
        if (cancellation.CanBeCanceled)
            registration = cancellation.Register(() =>
            {
                if (!state.MarkStale()) return;
                dispatcher.TryEnqueue(() =>
                {
                    if (closingOrFinished) return;
                    try { dialog.Hide(); } catch (Exception) { /* Already closed. */ }
                });
            });
        try
        {
            await dialog.ShowAsync();
            return state.Finish(); // Single completion: earlier Confirm/Stale wins; otherwise Cancelled.
        }
        catch (Exception)
        {
            return state.FailOwner(); // e.g. another ContentDialog already open; an earlier completion still wins.
        }
        finally
        {
            closingOrFinished = true;
            registration.Dispose();
        }
    }
}
