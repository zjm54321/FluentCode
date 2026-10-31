using System;
using System.Collections.Generic;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Undo/redo availability for one transcript message, read when its menu opens.</summary>
/// <param name="UndoVisible">This message is the one TUI <c>/undo</c> would target (latest user message).</param>
/// <param name="UndoEnabled">Session idle, composer empty and no other native action pending.</param>
/// <param name="UndoHint">Short reason appended to the disabled item's label.</param>
/// <param name="RedoVisible">A revert is staged in this session (TUI <c>/redo</c>).</param>
internal sealed record OpenCodeRollbackMenuState(bool UndoVisible, bool UndoEnabled, string? UndoHint,
    bool RedoVisible, bool RedoEnabled);

/// <summary>Page facts the transcript needs; null means no rollback host (child/read-only windows).</summary>
internal sealed record OpenCodeRollbackContext(bool Busy, bool ComposerEmpty, bool ActionPending, bool RevertStaged);

/// <summary>Pure menu policy (no WinUI) so the session-return runner can prove it.</summary>
internal static class OpenCodeMessageRollbackPolicy
{
    /// <summary>
    /// The server stages a revert at the latest user message before the current boundary (TUI /undo). The
    /// transcript is already cut at that boundary, so only the LAST user-type row may offer 撤回. When that row
    /// is not a regular bubble (internal initiator / notice), no bubble offers it, avoiding a mismatched target.
    /// </summary>
    internal static bool IsUndoTarget(IReadOnlyList<(string Key, string? Type)> chronological, string key)
    {
        for (var i = chronological.Count - 1; i >= 0; i--)
            if (string.Equals(chronological[i].Type, "user", StringComparison.Ordinal))
                return string.Equals(chronological[i].Key, key, StringComparison.Ordinal);
        return false;
    }

    internal static OpenCodeRollbackMenuState? Evaluate(OpenCodeRollbackContext? context, bool isUndoTarget)
    {
        if (context is null) return null;
        string? hint = context.Busy ? "运行中不可用" : context.ActionPending ? "另一操作进行中"
            : !context.ComposerEmpty ? "需先清空输入框" : null;
        return new(isUndoTarget, isUndoTarget && hint is null, hint,
            context.RevertStaged, context.RevertStaged && !context.Busy && !context.ActionPending);
    }
}
