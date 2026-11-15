using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// The one confirmation a send can still need while an undo is pending, in the shape of the undo prompt: a question
/// as title, one sentence that says what happens, and a caution line only when a fact calls for one. The default
/// (and Enter/Esc) path is 取消; confirming needs the explicit “发送” button. It never sends, commits, restores or
/// edits drafts, holds no controller/client/lease, and returns the exact presentation it was shown with. The caller
/// must invoke it on the owner's UI thread and re-validate stage + draft under its lease after the await.
/// Wording and rules live in <see cref="OpenCodeContinuationConfirmationState"/>.
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

        // Same measures as the undo prompt, so the two steps of one journey look like one family.
        var body = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 480 };
        var summary = OpenCodeInteractionUi.StyledText(state.Summary, OpenCodeInteractionUi.BodyStyleKey);
        AutomationProperties.SetAutomationId(summary, "OpenCodeContinuationSummary");
        body.Children.Add(summary);
        foreach (var caution in state.Cautions())
            body.Children.Add(OpenCodeRollbackConfirmationDialog.CautionLine(caution,
                caution == state.CommandNote ? "OpenCodeContinuationCommandNote" : "OpenCodeContinuationStageNote"));

        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = state.Title,
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = OpenCodeContinuationConfirmationState.PrimaryText,
            CloseButtonText = OpenCodeContinuationConfirmationState.CloseText,
            // Focus/Enter land on 取消: a held or IME-confirm Enter carried over from the composer can only cancel.
            DefaultButton = state.ConfirmIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close,
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
