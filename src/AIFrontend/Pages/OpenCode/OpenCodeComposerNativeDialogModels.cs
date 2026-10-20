using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Pages.OpenCode;

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
/// would be restored into the empty draft. <paramref name="Identity"/> is an opaque page object (e.g. the coordinator
/// preview); the confirmation result carries this same instance back and the page must re-read before acting.
/// </summary>
internal sealed record OpenCodeRollbackPreviewPresentation(OpenCodeRollbackKind Kind, string SessionLabel,
    string? TargetText, string? TargetLabel, bool HasExistingStage, bool PriorStageMayHaveFileEffects,
    bool TargetHasNonTextReferences, bool ReferencesIncomplete, bool DraftEmpty, object Identity);

internal enum OpenCodeRollbackOutcome { Confirmed, Cancelled, OwnerUnavailable }

internal sealed record OpenCodeRollbackConfirmationResult(OpenCodeRollbackOutcome Outcome,
    OpenCodeRollbackPreviewPresentation? Preview, bool RestoreFiles, bool TextOnlyAcknowledged);

internal sealed class OpenCodeRollbackConfirmationState
{
    internal OpenCodeRollbackConfirmationState(OpenCodeRollbackPreviewPresentation preview)
    {
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
    }

    internal OpenCodeRollbackPreviewPresentation Preview { get; }
    /// <summary>Undo only. Explicit opt-in; default false.</summary>
    internal bool RestoreFiles { get; private set; }
    internal bool TextOnlyAcknowledged { get; private set; }

    internal bool OffersFileRestoration => Preview.Kind == OpenCodeRollbackKind.Undo;
    /// <summary>Undo restoring a message that referenced files/agents/skills (or whose references are not fully known).</summary>
    internal bool RequiresTextOnlyAcknowledgment => Preview.Kind == OpenCodeRollbackKind.Undo
        && (Preview.TargetHasNonTextReferences || Preview.ReferencesIncomplete);

    internal void SetRestoreFiles(bool value) => RestoreFiles = OffersFileRestoration && value;
    internal void SetTextOnlyAcknowledged(bool value) => TextOnlyAcknowledged = RequiresTextOnlyAcknowledgment && value;

    internal string? BlockingReason => Preview.Kind == OpenCodeRollbackKind.Undo && !Preview.DraftEmpty
        ? "撤回需要输入框、已选命令和附件都为空（撤回的文字会放回输入框）。请先发送或清空当前草稿。"
        : Preview.Kind == OpenCodeRollbackKind.Undo && string.IsNullOrEmpty(Preview.TargetText) && !Preview.TargetHasNonTextReferences
            ? "没有找到可以撤回的消息。"
            : null;

    internal bool CanConfirm => BlockingReason is null && (!RequiresTextOnlyAcknowledgment || TextOnlyAcknowledged);

    internal OpenCodeRollbackConfirmationResult Confirm()
        => CanConfirm ? new(OpenCodeRollbackOutcome.Confirmed, Preview, RestoreFiles, TextOnlyAcknowledged)
            : new(OpenCodeRollbackOutcome.Cancelled, null, false, false);

    /// <summary>Risk lines shown before any confirmation, in this order.</summary>
    internal IReadOnlyList<string> Warnings()
    {
        var list = new List<string>();
        if (Preview.Kind == OpenCodeRollbackKind.Undo)
        {
            list.Add("这条消息及其后的回复会从会话中撤回；撤回的文字会放回输入框，不会自动重新发送。");
            if (Preview.HasExistingStage)
                list.Add("这个会话已经有一次未完成的撤回，这次会在它的基础上继续。");
            if (Preview.PriorStageMayHaveFileEffects)
                list.Add("之前的撤回可能已经改动过文件；即使这次不恢复文件，移动撤回点也可能恢复那次的文件状态。");
        }
        else
        {
            list.Add("将清除这个会话的整个撤回状态，恢复所有已撤回的消息。输入框中的内容保持不变。");
            list.Add("撤回之后如果在别处（其他窗口或客户端）又做过修改，恢复后可能与那些修改冲突。");
            list.Add("恢复后，排队中的工作可能会继续运行。");
        }
        list.Add("这是尽力而为的操作：服务器不提供跨客户端的一致性检查，其他客户端同时修改时结果可能不同。确认前会重新读取一次，内容变化时需要重新确认。");
        list.Add("文件快照可能覆盖你当前的编辑，不包括外部效果，也可能不可用。");
        if (RequiresTextOnlyAcknowledgment)
            list.Add(Preview.ReferencesIncomplete
                ? "无法确认这条消息是否引用了文件、智能体或技能。只会恢复文字，引用不会恢复、也不会重新下载。"
                : "这条消息引用了文件、智能体或技能。只会恢复文字，这些引用不会恢复、也不会重新下载。");
        return list;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Expanded draft editor (AIFrontend's own buffer, not a remote VISUAL/EDITOR)
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
