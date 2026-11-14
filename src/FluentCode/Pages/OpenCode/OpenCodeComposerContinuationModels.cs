using System;
using System.Collections.Generic;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

// WinUI-free model for the one confirmation a send can still need while an undo is pending. UI-owned and immutable:
// the page maps its fresh stage/draft read into the presentation and passes an opaque Basis; every result returns that
// SAME presentation instance (never a re-read). Confirming does not send, commit, restore or change any draft — the
// page re-acquires its lease and re-validates stage + draft before its single POST.
//
// Whether a send asks at all is not decided here: OpenCodeSessionControlCoordinator.PrepareContinuationAsync does
// (SessionControlContinuationRule). An ordinary message after an undo that restored no file goes straight through.

/// <summary>
/// Immutable confirmation input. <paramref name="Basis"/> is an opaque page object identifying exactly what was read
/// (e.g. the stage/draft consent record). <paramref name="IsServerCommand"/>: the send is a selected server command.
/// <paramref name="RestoredFileCount"/>: the files the pending undo restored (the server's <c>revert.files</c>);
/// sending leaves them as they are now.
/// </summary>
internal sealed record OpenCodeContinuationPresentation(bool IsServerCommand, int RestoredFileCount, object Basis)
{
    public object Basis { get; } = Basis ?? throw new ArgumentNullException(nameof(Basis));
    /// <summary>False only when the undo that was asked about is gone by the time of the next read.</summary>
    internal bool HasStagedRevert { get; init; } = true;
    internal OpenCodeContinuationStageView StageView { get; init; }

    /// <summary>Maps the coordinator's question to what the dialog says. <paramref name="stage"/> is the pending
    /// undo the coordinator just read (null: it is gone); <paramref name="origin"/> carries what the page was
    /// showing when Send was pressed. Uses the coordinator's own predicates, so the two cannot disagree.</summary>
    internal static OpenCodeContinuationPresentation Describe(SessionControlOrigin origin, SessionControlRevert? stage,
        bool isServerCommand, object basis)
        => new(isServerCommand, SessionControlContinuationRule.RestoredFileCount(stage), basis)
        {
            HasStagedRevert = stage is not null,
            StageView = stage is null || SessionControlContinuationRule.ShownToUser(origin, stage) ? OpenCodeContinuationStageView.AsShown
                : origin.ObservedStage is null ? OpenCodeContinuationStageView.Unknown
                : origin.ObservedStage.MessageId is null ? OpenCodeContinuationStageView.NotShown
                : OpenCodeContinuationStageView.Moved,
        };
}

/// <summary>
/// How the pending undo relates to what the page was showing when Send was pressed. <see cref="Moved"/>: the page
/// showed an undo at another message. <see cref="NotShown"/>: the page showed no undo. <see cref="Unknown"/>: the
/// page had not read the session's undo state yet.
/// </summary>
internal enum OpenCodeContinuationStageView { AsShown, Moved, NotShown, Unknown }

internal enum OpenCodeContinuationOutcome { Confirmed, Cancelled, OwnerUnavailable, Stale }

/// <summary>Single result. <see cref="Presentation"/> is always the exact instance the dialog was shown with.</summary>
internal sealed record OpenCodeContinuationResult(OpenCodeContinuationOutcome Outcome, OpenCodeContinuationPresentation Presentation)
{
    internal object Basis => Presentation.Basis;
}

/// <summary>
/// One dialog's lifetime: exactly one completion (first wins). Confirmation exists only through an explicit
/// <see cref="TryConfirm"/> while open; caller cancellation completes it as Stale; after completion everything is inert.
/// Also holds everything the dialog says, so the wording and the default button can be tested without WinUI.
/// </summary>
internal sealed class OpenCodeContinuationConfirmationState
{
    private readonly object _gate = new();
    private OpenCodeContinuationResult? _result;

    internal OpenCodeContinuationConfirmationState(OpenCodeContinuationPresentation presentation)
    {
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
    }

    internal OpenCodeContinuationPresentation Presentation { get; }
    internal bool IsCompleted { get { lock (_gate) return _result is not null; } }
    internal OpenCodeContinuationResult? Result { get { lock (_gate) return _result; } }

    /// <summary>The explicit “发送” gesture. False when already completed (cancelled/stale/closed): no late confirm.</summary>
    internal bool TryConfirm() => TryComplete(OpenCodeContinuationOutcome.Confirmed);

    /// <summary>Caller cancellation (stage/session/draft changed while open). No effect once completed.</summary>
    internal bool MarkStale() => TryComplete(OpenCodeContinuationOutcome.Stale);

    /// <summary>
    /// Dialog closed by any path. Returns the single completion: an earlier confirm/stale wins; otherwise Cancelled
    /// (default/close button, Esc, programmatic hide). Never upgrades to Confirmed.
    /// </summary>
    internal OpenCodeContinuationResult Finish()
    {
        TryComplete(OpenCodeContinuationOutcome.Cancelled);
        lock (_gate) return _result!;
    }

    /// <summary>Owner missing / not on the UI thread / another dialog already open.</summary>
    internal OpenCodeContinuationResult FailOwner()
    {
        TryComplete(OpenCodeContinuationOutcome.OwnerUnavailable);
        lock (_gate) return _result!;
    }

    private bool TryComplete(OpenCodeContinuationOutcome outcome)
    {
        lock (_gate)
        {
            if (_result is not null) return false;
            _result = new(outcome, Presentation);
            return true;
        }
    }

    private string Subject => Presentation.IsServerCommand ? "这条命令" : "这条消息";
    internal string Title => (Presentation.HasStagedRevert ? "发送" : "仍然发送") + Subject + "？";
    internal const string PrimaryText = "发送";
    internal const string CloseText = "取消";

    /// <summary>取消 is always the default button (focus, Enter): what this prompt confirms cannot be taken back, and
    /// an Enter still held from the input box must not confirm it.</summary>
    internal bool ConfirmIsDefault => false;

    private string Files => Presentation.RestoredFileCount > 0
        ? $"，撤回时恢复的 {Presentation.RestoredFileCount} 个文件保持现在的样子" : string.Empty;

    /// <summary>The one sentence that says what happens.</summary>
    internal string Summary => !Presentation.HasStagedRevert
        ? $"这次撤回已经不存在了，可能已在其他窗口或设备上被恢复，或已成为最终结果。{Subject}会照常发送。"
        : Presentation.IsServerCommand
            ? $"发出这条命令后，已撤回的消息可能被永久删除{Files}，不能再用“恢复撤回”还原。"
            : $"发送后，已撤回的消息会被永久删除{Files}，不能再用“恢复撤回”还原。";

    /// <summary>The pending undo is not known to be the one on screen. Null when it does not apply.</summary>
    internal string? StageNote => !Presentation.HasStagedRevert ? null : Presentation.StageView switch
    {
        OpenCodeContinuationStageView.Moved => "这次撤回的位置和这里显示的不一样，可能刚在其他窗口或设备上改过。将被删除的消息可能和你看到的不同。",
        OpenCodeContinuationStageView.NotShown => "这个会话有一次撤回还没有显示在这里，可能是在其他窗口或设备上做的。",
        OpenCodeContinuationStageView.Unknown => "会话内容还没有加载完，无法确认这次撤回和你看到的一致。",
        _ => null,
    };

    /// <summary>
    /// Why a server command after an undo still asks. The server's reply to a command does not say whether the
    /// command went on to send a message (which makes the undo final), so the app keeps this session protected
    /// afterwards, and nothing but a restart releases that protection. Null when it does not apply.
    /// </summary>
    internal string? CommandNote => Presentation.HasStagedRevert && Presentation.IsServerCommand
        ? "命令发出后，FluentCode 无法确认这次撤回是否已成为最终结果，会停用这个会话的发送、撤回和模型切换，重启 FluentCode 后恢复。"
        : null;

    /// <summary>Caution lines, in display order. Empty for an ordinary message whose undo is the one on screen.</summary>
    internal IReadOnlyList<string> Cautions()
    {
        var list = new List<string>(2);
        if (StageNote is { } stage) list.Add(stage);
        if (CommandNote is { } command) list.Add(command);
        return list;
    }
}
