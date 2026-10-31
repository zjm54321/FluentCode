using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    // Transcript message menu 撤回 / 恢复 reuse the composer's /undo and /redo capability end to end:
    // the server preview, the rollback confirmation dialog, restoring the prompt into the composer and
    // the 恢复 path all stay in RunNativeRollbackAsync. The menu only supplies the gesture.
    private void InitializeTranscriptMenu()
    {
        _conversationView.RollbackContext = TranscriptRollbackContext;
        _conversationView.RollbackRequested += (_, undo) => RequestTranscriptRollback(undo);
    }

    private OpenCodeRollbackContext? TranscriptRollbackContext()
    {
        if (_selectedSessionId is not { } id || _composerController is null || _composerView is null
            || IsChildComposerSession) return null;
        var snapshot = _composerController.Snapshot;
        var empty = snapshot.Text.Length == 0 && snapshot.SelectedCommand is null && snapshot.Attachments.Count == 0;
        return new(IsComposerSessionWorking(id) || _isSending, empty, HasPendingNativeAction(),
            _messageRevertBoundary is not null);
    }

    private void RequestTranscriptRollback(bool undo)
    {
        if (TranscriptRollbackContext() is null || _composerView?.PrepareSubmission() != true || _composerController is null) return;
        var ticket = _composerController.TryTakeRollbackAction(GetComposerSnapshot().Token, undo);
        if (ticket is not null) _ = RunClientActionAsync(ticket);
        else ShowMessage(InfoBarSeverity.Warning, undo ? "暂时无法撤回" : "暂时无法恢复",
            undo ? "请先清空输入框，并等待当前操作结束。" : "请等待当前操作结束后再试。");
    }
}
