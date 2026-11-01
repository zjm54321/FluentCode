using System;
using System.Collections.Generic;

namespace FluentCode.Pages.OpenCode;

// WinUI-free model for the "send while a staged undo exists" confirmation. UI-owned and immutable: the page maps its
// fresh stage/draft read into the presentation and passes an opaque Basis; every result returns that SAME presentation
// instance (never a re-read). Confirming does not send, commit, restore or change any draft — the page re-acquires its
// lease and re-validates stage + draft before its single POST.

/// <summary>
/// Immutable confirmation input. <paramref name="Basis"/> is an opaque page object identifying exactly what was read
/// (e.g. the stage/draft consent record). <paramref name="IsServerCommand"/>: the send is a selected server command.
/// <paramref name="StageMayHaveFileEffects"/>: the staged undo may already have restored files.
/// </summary>
internal sealed record OpenCodeContinuationPresentation(string SessionLabel, bool IsServerCommand,
    bool StageMayHaveFileEffects, object Basis)
{
    public object Basis { get; } = Basis ?? throw new ArgumentNullException(nameof(Basis));
    internal bool HasStagedRevert { get; init; } = true;
}

internal enum OpenCodeContinuationOutcome { Confirmed, Cancelled, OwnerUnavailable, Stale }

/// <summary>Single result. <see cref="Presentation"/> is always the exact instance the dialog was shown with.</summary>
internal sealed record OpenCodeContinuationResult(OpenCodeContinuationOutcome Outcome, OpenCodeContinuationPresentation Presentation)
{
    internal object Basis => Presentation.Basis;
}

/// <summary>
/// One dialog's lifetime: exactly one completion (first wins). Confirmation exists only through an explicit
/// <see cref="TryConfirm"/> while open; caller cancellation completes it as Stale; after completion everything is inert.
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

    /// <summary>The explicit "继续发送" gesture. False when already completed (cancelled/stale/closed): no late confirm.</summary>
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

    /// <summary>Risk lines, in display order.</summary>
    internal IReadOnlyList<string> Warnings()
    {
        var list = new List<string>
        {
            Presentation.HasStagedRevert
                ? "这个会话正停在一次撤回处，撤回还没有最终确定。从这里继续发送，可能会把撤回确定下来，被撤回的后续消息将被永久丢弃。"
                : "撤回状态已变化，目前未检测到暂存撤回。继续发送前会再次检查会话和草稿。",
        };
        list.Add(Presentation.StageMayHaveFileEffects
            ? "撤回时可能已经恢复过文件，这些文件改动可能会保留；已产生的外部效果（例如执行过的命令、发出的请求）无法自动撤销。"
            : "已产生的外部效果（例如执行过的命令、发出的请求）无法自动撤销。");
        if (Presentation.IsServerCommand)
            list.Add(Presentation.HasStagedRevert
                ? "这次发送的是服务器命令：服务器回复“已收到”只表示命令已被接收，不代表已经执行，也不代表撤回已经确定。"
                : "这次发送的是服务器命令：服务器回复“已收到”只表示命令已被接收，不代表已经执行。");
        list.Add("这是尽力而为的操作：其他窗口或客户端同时操作时，结果可能发生变化。");
        list.Add("确认后，发送前还会重新检查会话和草稿；如果状态有变化，不会发送，需要重新确认。确认本身不会修改草稿。");
        return list;
    }
}
