using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentCode.Pages.OpenCode;

// WinUI-free, immutable presentation/result records and local state for the native builtin dialogs (/models, /undo,
// /redo, /editor). They are UI-owned on purpose: the page maps backend/coordinator types into them and maps results
// back, re-checking all authority after the dialog closes. Views never hold clients, leases, gates or binding targets,
// never perform IO, and never retarget a confirmation to a different preview.

// ---------------------------------------------------------------------------------------------------------------------
// Model picker
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Exact model choice. <paramref name="ModelId"/> is the CATALOG id (not the provider-facing model id);
/// <paramref name="Variant"/> null means the base model (omitted on the wire), never an invented name.</summary>
internal sealed record OpenCodeModelChoiceKey(string ProviderId, string ModelId, string? Variant)
{
    internal bool SameModel(OpenCodeModelChoiceKey other)
        => string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal) && string.Equals(ModelId, other.ModelId, StringComparison.Ordinal);
}

/// <summary>One catalog row. <paramref name="Variants"/> are exactly the variants offered for THIS model.</summary>
internal sealed record OpenCodeModelPickerRow(string ProviderId, string ModelId, string Name, string? ProviderName,
    bool Available, IReadOnlyList<string> Variants, string? Detail);

/// <summary>Current session selection as read by the page. Absence (inherited) is distinct from an explicit choice.</summary>
internal abstract record OpenCodeModelCurrent
{
    private OpenCodeModelCurrent() { }
    /// <summary>The session names a model. <paramref name="Available"/> false: kept and shown as unavailable, never replaced.</summary>
    internal sealed record Explicit(OpenCodeModelChoiceKey Key, bool Available) : OpenCodeModelCurrent;
    /// <summary>No session model: the server default applies. <paramref name="Default"/> null means there is no default.</summary>
    internal sealed record Inherited(OpenCodeModelChoiceKey? Default) : OpenCodeModelCurrent;
    /// <summary>State could not be read; the dialog shows that and still lets the user choose explicitly.</summary>
    internal sealed record Unknown : OpenCodeModelCurrent;
}

/// <summary>Immutable picker input. <paramref name="Basis"/> is an opaque page object identifying what was read.</summary>
internal sealed record OpenCodeModelPickerPresentation(string SessionLabel, IReadOnlyList<OpenCodeModelPickerRow> Rows,
    OpenCodeModelCurrent Current, string? Note, object? Basis);

internal enum OpenCodeModelPickerOutcome { Confirmed, Cancelled, OwnerUnavailable }

/// <summary>Dialog result. Confirmed carries the exact presentation it was chosen from; the page must re-validate it.</summary>
internal sealed record OpenCodeModelPickerResult(OpenCodeModelPickerOutcome Outcome, OpenCodeModelChoiceKey? Choice,
    OpenCodeModelPickerPresentation? Presentation);

/// <summary>Local picker state: row selection is local; only an explicit confirmation produces a result.</summary>
internal sealed class OpenCodeModelPickerState
{
    internal OpenCodeModelPickerState(OpenCodeModelPickerPresentation presentation)
    {
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
    }

    internal OpenCodeModelPickerPresentation Presentation { get; private set; }
    /// <summary>The user's PENDING local choice (never the session's current/explicit state, which lives in Presentation.Current).</summary>
    internal OpenCodeModelPickerRow? SelectedRow { get; private set; }
    internal string? SelectedVariant { get; private set; }
    /// <summary>Set when a refresh withdrew the pending choice; the user must choose again explicitly.</summary>
    internal string? ChoiceWithdrawnNotice { get; private set; }
    /// <summary>A read-only refresh is outstanding: confirmation is refused until it completes.</summary>
    internal bool IsRefreshing { get; private set; }
    /// <summary>The dialog closed (any way). Nothing may change afterwards; late refresh completions are ignored.</summary>
    internal bool IsClosed { get; private set; }
    private long _refreshTicket;

    /// <summary>Selects an AVAILABLE row; the variant resets to the base model.</summary>
    internal bool SelectModel(string providerId, string modelId)
    {
        if (IsClosed) return false;
        var row = Presentation.Rows.FirstOrDefault(r => r.Available
            && string.Equals(r.ProviderId, providerId, StringComparison.Ordinal) && string.Equals(r.ModelId, modelId, StringComparison.Ordinal));
        if (row is null) return false;
        SelectedRow = row;
        SelectedVariant = null;
        ChoiceWithdrawnNotice = null;
        return true;
    }

    /// <summary>Null = base model. A non-null variant must be one the selected model actually offers.</summary>
    internal bool SelectVariant(string? variant)
    {
        if (IsClosed || SelectedRow is null) return false;
        if (variant is not null && !SelectedRow.Variants.Contains(variant, StringComparer.Ordinal)) return false;
        SelectedVariant = variant;
        return true;
    }

    internal OpenCodeModelChoiceKey? Choice
        => SelectedRow is null ? null : new(SelectedRow.ProviderId, SelectedRow.ModelId, SelectedVariant);

    /// <summary>Choosing exactly the current explicit model+variant changes nothing, so it is not confirmable. Never while a
    /// refresh is outstanding or after close.</summary>
    internal bool CanConfirm => !IsRefreshing && !IsClosed && Choice is { } choice
        && !(Presentation.Current is OpenCodeModelCurrent.Explicit e && e.Key == choice);

    /// <summary>
    /// Read-only refresh result. The pending choice survives only if the EXACT model (still available) and the EXACT
    /// variant (or base) are still offered; otherwise the choice is withdrawn — never retargeted to base/another variant.
    /// </summary>
    internal void Replace(OpenCodeModelPickerPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (IsClosed) return;
        Presentation = presentation;
        if (SelectedRow is not { } selected) return;
        var keep = presentation.Rows.FirstOrDefault(r => r.Available
            && string.Equals(r.ProviderId, selected.ProviderId, StringComparison.Ordinal)
            && string.Equals(r.ModelId, selected.ModelId, StringComparison.Ordinal));
        var variant = SelectedVariant;
        if (keep is not null && (variant is null || keep.Variants.Contains(variant, StringComparer.Ordinal)))
        {
            SelectedRow = keep; // Same model+variant key; refreshed display details only.
            return;
        }
        SelectedRow = null;
        SelectedVariant = null;
        ChoiceWithdrawnNotice = "刚才选择的模型或变体在重新读取后已不再提供，请重新选择。";
    }

    /// <summary>Starts a single-flight read-only refresh; null when one is outstanding or the dialog closed.</summary>
    internal long? BeginRefresh()
    {
        if (IsClosed || IsRefreshing) return null;
        IsRefreshing = true;
        return ++_refreshTicket;
    }

    /// <summary>
    /// Applies a refresh completion only for the current ticket of a still-open dialog. Returns false for late/stale
    /// completions (closed, superseded): the caller must then not touch any UI. <paramref name="updated"/> null = keep.
    /// </summary>
    internal bool CompleteRefresh(long ticket, OpenCodeModelPickerPresentation? updated)
    {
        if (IsClosed || !IsRefreshing || ticket != _refreshTicket) return false;
        IsRefreshing = false;
        if (updated is not null) Replace(updated);
        return true;
    }

    /// <summary>
    /// The explicit primary gesture: freezes the exact presentation + choice shown NOW, or null when not confirmable
    /// (refresh outstanding, closed, nothing chosen, no change). Later state never alters a frozen result.
    /// </summary>
    internal OpenCodeModelPickerResult? TryFreezeConfirmation()
        => CanConfirm ? new(OpenCodeModelPickerOutcome.Confirmed, Choice, Presentation) : null;

    /// <summary>Dialog closed: fence every later mutation (selection, refresh completion).</summary>
    internal void Close()
    {
        IsClosed = true;
        IsRefreshing = false;
    }

    internal OpenCodeModelPickerResult Confirm()
        => TryFreezeConfirmation() ?? new(OpenCodeModelPickerOutcome.Cancelled, null, null);

    internal static string Describe(OpenCodeModelCurrent current, IReadOnlyList<OpenCodeModelPickerRow> rows) => current switch
    {
        OpenCodeModelCurrent.Explicit { Available: true } e => "当前：" + Label(e.Key, rows),
        OpenCodeModelCurrent.Explicit e => "当前：" + Label(e.Key, rows) + "（此模型现在不可用；保持不变，除非你另选一个并确认）",
        OpenCodeModelCurrent.Inherited { Default: { } d } => "当前：未单独指定，使用默认模型 " + Label(d, rows),
        OpenCodeModelCurrent.Inherited => "当前：未单独指定，服务器也没有默认模型",
        _ => "当前模型未能读取",
    };

    internal static string Label(OpenCodeModelChoiceKey key, IReadOnlyList<OpenCodeModelPickerRow> rows)
    {
        var row = rows.FirstOrDefault(r => r.ProviderId == key.ProviderId && r.ModelId == key.ModelId);
        var name = row?.Name is { Length: > 0 } n ? n : key.ModelId;
        return $"{name}（{row?.ProviderName ?? key.ProviderId}）" + (key.Variant is { } v ? " · " + v : string.Empty);
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Undo / redo confirmation
// ---------------------------------------------------------------------------------------------------------------------

internal enum OpenCodeRollbackKind { Undo, Redo }

/// <summary>
/// Exact immutable preview prepared by the page from a fresh read. Undo: <paramref name="TargetText"/> is the text that
/// would be restored into the empty draft. <paramref name="PriorStageFileCount"/> is the number of files the staged
/// undo changed (the server's <c>revert.files</c>): exactly those are rewritten when the stage is cleared or moved, so
/// zero means this rollback touches no file unless restoration is ticked. <paramref name="Identity"/> is an opaque
/// page object (e.g. the coordinator preview); the confirmation result carries this same instance back and the page
/// must re-read before acting.
/// </summary>
internal sealed record OpenCodeRollbackPreviewPresentation(OpenCodeRollbackKind Kind, string? TargetText,
    int PriorStageFileCount, bool TargetHasNonTextReferences, bool ReferencesIncomplete, bool DraftEmpty, object Identity)
{
    /// <summary>Undo only: the preview prepared for the SAME target with file restoration, read before the dialog
    /// opens so that one dialog covers both choices. Null: file restoration is not offered this time.</summary>
    internal object? FilesIdentity { get; init; }
}

internal enum OpenCodeRollbackOutcome { Confirmed, Cancelled, OwnerUnavailable }

internal sealed record OpenCodeRollbackConfirmationResult(OpenCodeRollbackOutcome Outcome,
    OpenCodeRollbackPreviewPresentation? Preview, bool RestoreFiles)
{
    /// <summary>The one identity the user confirmed: the with-files preview only when that box was ticked.
    /// Null unless confirmed. The page executes exactly this object and never swaps it afterwards.</summary>
    internal object? ConfirmedIdentity => Outcome != OpenCodeRollbackOutcome.Confirmed || Preview is null ? null
        : RestoreFiles ? Preview.FilesIdentity : Preview.Identity;
}

internal sealed class OpenCodeRollbackConfirmationState
{
    internal OpenCodeRollbackConfirmationState(OpenCodeRollbackPreviewPresentation preview)
    {
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
    }

    internal OpenCodeRollbackPreviewPresentation Preview { get; }
    /// <summary>Undo only. Explicit opt-in; default false.</summary>
    internal bool RestoreFiles { get; private set; }

    /// <summary>
    /// Whether this rollback asks at all. Undo always asks once: the prompt names the message and is the one place
    /// where file restoration can be chosen. Redo only brings messages back and can be undone again, so it asks
    /// only when the staged undo changed files (redo would change them back).
    /// </summary>
    internal static bool RequiresPrompt(OpenCodeRollbackPreviewPresentation preview)
        => preview.Kind == OpenCodeRollbackKind.Undo || preview.PriorStageFileCount > 0;

    /// <summary>For a rollback that needs no prompt: the gesture itself confirmed exactly this preview.</summary>
    internal static OpenCodeRollbackConfirmationResult ConfirmWithoutPrompt(OpenCodeRollbackPreviewPresentation preview)
        => RequiresPrompt(preview) ? new(OpenCodeRollbackOutcome.Cancelled, null, false)
            : new(OpenCodeRollbackOutcome.Confirmed, preview, false);

    internal bool OffersFileRestoration => Preview.Kind == OpenCodeRollbackKind.Undo && Preview.FilesIdentity is not null;
    /// <summary>Undo of a message that referenced files/agents/skills (or whose references are not fully known):
    /// only its text returns to the input box. Told as one caution line; it is not a separate tick any more.</summary>
    internal bool RestoresTextOnly => Preview.Kind == OpenCodeRollbackKind.Undo
        && (Preview.TargetHasNonTextReferences || Preview.ReferencesIncomplete);

    internal void SetRestoreFiles(bool value) => RestoreFiles = OffersFileRestoration && value;

    internal string? BlockingReason => Preview.Kind == OpenCodeRollbackKind.Undo && !Preview.DraftEmpty
        ? "输入框里还有内容。撤回的文字要放回输入框，请先发送或清空。"
        : Preview.Kind == OpenCodeRollbackKind.Undo && string.IsNullOrEmpty(Preview.TargetText) && !Preview.TargetHasNonTextReferences
            ? "没有找到可以撤回的消息。"
            : null;

    internal bool CanConfirm => BlockingReason is null;

    /// <summary>
    /// 撤回 is the default button only in the plain case: nothing to warn about, and 恢复撤回 can take it back.
    /// With a caution on screen 取消 is the default. Decided when the prompt opens and fixed afterwards: the
    /// accent does not jump between the buttons when the file box is ticked, and the box always starts unticked,
    /// so restoring files takes a step of its own.
    /// </summary>
    internal bool ConfirmIsDefault => CanConfirm && Cautions().Count == 0;

    internal OpenCodeRollbackConfirmationResult Confirm()
        => CanConfirm ? new(OpenCodeRollbackOutcome.Confirmed, Preview, RestoreFiles)
            : new(OpenCodeRollbackOutcome.Cancelled, null, false);

    internal string Title => Preview.Kind == OpenCodeRollbackKind.Undo ? "撤回这条消息？" : "恢复已撤回的消息？";
    internal string PrimaryText => Preview.Kind == OpenCodeRollbackKind.Undo ? "撤回" : "恢复";

    /// <summary>The one sentence that says what will happen.</summary>
    internal string Summary => Preview.Kind == OpenCodeRollbackKind.Undo
        ? "这条消息和它之后的回复会被撤回，文字放回输入框。之后可以用“恢复撤回”还原。"
        : "已撤回的消息会全部回到会话里，输入框的内容不变。";

    internal const string RestoreFilesLabel = "同时恢复文件";
    /// <summary>Always shown under the box when it is offered: the risk is read before ticking.</summary>
    internal const string RestoreFilesNote = "文件会回到这条消息之前的样子，可能覆盖你之后的修改；已经执行的命令等外部操作不会撤销。";

    /// <summary>Only text comes back: the caution line that replaced the separate tick. Null when it does not apply.</summary>
    internal string? TextOnlyNote => !RestoresTextOnly ? null : Preview.ReferencesIncomplete
        ? "无法确认这条消息是否带有文件、智能体或技能引用。放回输入框的只有文字。"
        : "这条消息带有文件、智能体或技能引用。放回输入框的只有文字，这些引用需要重新添加。";

    /// <summary>The staged undo changed files, and this rollback rewrites them. Null when it does not apply.</summary>
    internal string? FileEffectNote => Preview.PriorStageFileCount <= 0 ? null
        : Preview.Kind == OpenCodeRollbackKind.Undo
            ? $"上一次撤回同时恢复了 {Preview.PriorStageFileCount} 个文件。这次如果不勾选“{RestoreFilesLabel}”，它们会变回那次撤回之前的样子，你在这之后对它们的修改会丢失。"
            : $"撤回时同时恢复了 {Preview.PriorStageFileCount} 个文件。恢复后它们会变回撤回之前的样子，你在这之后对它们的修改会丢失。";

    /// <summary>Caution lines, in display order. Empty in the plain case: nothing risky, nothing to read.</summary>
    internal IReadOnlyList<string> Cautions()
        => new[] { FileEffectNote, TextOnlyNote }.Where(line => line is not null).Select(line => line!).ToArray();
}

// ---------------------------------------------------------------------------------------------------------------------
// Expanded draft editor (FluentCode's own buffer, not a remote VISUAL/EDITOR)
// ---------------------------------------------------------------------------------------------------------------------

internal enum OpenCodeDraftEditorApplyOutcome { Applied, Stale }
internal enum OpenCodeDraftEditorOutcome { Applied, Cancelled, ClosedStale, OwnerUnavailable }

/// <summary>Dialog result. <paramref name="RetainedText"/> is the detached buffer when it could not be applied.</summary>
internal sealed record OpenCodeDraftEditorResult(OpenCodeDraftEditorOutcome Outcome, string? RetainedText);

/// <summary>
/// Detached buffer state: ordinary dirty tracking only (no second acknowledgment ledger). Initialization and any
/// programmatic write are not user edits. Apply is allowed only when not stale, not composing and not already applying.
/// </summary>
internal sealed class OpenCodeDraftEditorSession
{
    private int _writeDepth;

    internal OpenCodeDraftEditorSession(string initialText)
    {
        Initial = initialText ?? throw new ArgumentNullException(nameof(initialText));
        Buffer = initialText;
    }

    internal string Initial { get; }
    internal string Buffer { get; private set; }
    internal bool Dirty => !string.Equals(Buffer, Initial, StringComparison.Ordinal);
    internal bool Composing { get; private set; }
    internal bool Applying { get; private set; }
    internal bool Stale { get; private set; }
    internal string? StaleReason { get; private set; }
    internal DateTimeOffset LastCompositionEnded { get; private set; } = DateTimeOffset.MinValue;

    internal bool CanApply => !Stale && !Composing && !Applying;

    /// <summary>Guarded programmatic write (initialization): the echo is ignored.</summary>
    internal void Write(Action assign)
    {
        _writeDepth++;
        try { assign(); }
        finally { _writeDepth--; }
    }

    /// <summary>Editor TextChanging with the new text. Records user edits only.</summary>
    internal void OnTextChanging(string text)
    {
        if (_writeDepth > 0) return;
        Buffer = text ?? string.Empty;
    }

    internal void OnCompositionStarted() => Composing = true;
    internal void OnCompositionEnded(DateTimeOffset now) { Composing = false; LastCompositionEnded = now; }

    /// <summary>A keyboard Apply (Ctrl+Enter) is ignored while composing or right after composition ends (IME confirm).</summary>
    internal bool KeyboardApplyAllowed(DateTimeOffset now)
        => CanApply && now - LastCompositionEnded >= OpenCodeComposerInputPolicy.ImeEnterGuard;

    /// <summary>Source/session/draft changed: Apply is disabled for good; the buffer stays visible and copyable.</summary>
    internal void MarkStale(string reason)
    {
        Stale = true;
        StaleReason = reason;
    }

    /// <summary>Single-flight apply through the page's exact-ticket seam.</summary>
    internal OpenCodeDraftEditorApplyOutcome? Apply(Func<string, OpenCodeDraftEditorApplyOutcome> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (!CanApply) return null;
        Applying = true;
        try
        {
            var outcome = apply(Buffer);
            if (outcome == OpenCodeDraftEditorApplyOutcome.Stale)
                MarkStale("草稿已在别处变化（或会话、连接已切换），不能再应用这段文字。文字仍保留在这里，可以复制后关闭。");
            return outcome;
        }
        catch (Exception)
        {
            MarkStale("应用失败，文字仍保留在这里，可以复制后关闭。");
            return OpenCodeDraftEditorApplyOutcome.Stale;
        }
        finally { Applying = false; }
    }
}
