using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Composer link / image chips (spec §3–§5). The TextBox keeps a short label for each link; the controller ledger maps
/// it back to the URL. Every change is a native, undoable TextBox edit through the existing TextChanging ledger:
/// paste → (native paste) → label replaces the URL (Ctrl+Z restores the URL text); Backspace/Delete removes a whole
/// chip (Ctrl+Z restores it); "转回文字" replaces the label with the URL. Send and copy always restore the raw URL.
/// </summary>
internal sealed partial class OpenCodeComposerView
{
    /// <summary>A pasted link label was inserted at <c>start</c> standing for <c>url</c> (page → controller.TryAddLink).</summary>
    internal event Action<OpenCodeComposerToken, int, string>? LinkChosen;

    private (string Text, int Start)? _pasteArmed;
    private static bool _linkTipShown;

    internal IReadOnlyList<OpenCodeComposerLinkSpan> LinkSpans => _source().Links;
    internal IReadOnlyList<OpenCodeComposerImageSpan> ImageSpans => _source().Images;

    private void AttachChipHandlers(TextBox editor, TextBox? previous)
    {
        if (previous is not null)
        {
            previous.Paste -= OnEditorPasteForChips;
            previous.CopyingToClipboard -= OnEditorCopyingToClipboard;
            previous.CuttingToClipboard -= OnEditorCuttingToClipboard;
            previous.RemoveHandler(PointerReleasedEvent, (PointerEventHandler)OnEditorPointerReleasedForChips);
        }
        editor.Paste += OnEditorPasteForChips;
        editor.CopyingToClipboard += OnEditorCopyingToClipboard;
        editor.CuttingToClipboard += OnEditorCuttingToClipboard;
        editor.AddHandler(PointerReleasedEvent, (PointerEventHandler)OnEditorPointerReleasedForChips, true);
    }

    // Paste has no deferral and the clipboard text is async: let the native paste run, then convert on TextChanged.
    private void OnEditorPasteForChips(object sender, TextControlPasteEventArgs e)
    {
        if (e.Handled || sender is not TextBox editor || editor.IsReadOnly) { _pasteArmed = null; return; }
        _pasteArmed = (editor.Text ?? string.Empty, editor.SelectionStart);
    }

    /// <summary>After a clean flush of a native paste: a single pasted link becomes a chip (second, undoable edit).</summary>
    private void ConvertPastedLink()
    {
        if (_pasteArmed is not { } armed || _editor is not { } editor) return;
        _pasteArmed = null;
        var after = editor.Text ?? string.Empty;
        var (prefix, oldEnd, delta) = OpenCodeComposerChips.Diff(armed.Text, after);
        var insertedLength = oldEnd - prefix + delta;
        if (insertedLength <= 0 || prefix + insertedLength > after.Length) return;
        var inserted = after.Substring(prefix, insertedLength);
        if (OpenCodeComposerChips.SingleLink(inserted) is not { } url) return;
        var start = prefix + inserted.IndexOf(url, StringComparison.Ordinal);
        if (OpenCodeComposerLinkSpan.Create(start, url) is not { } link || editor.IsReadOnly || !_lifetime.IsLive) return;
        var s = _source();
        if (!s.IsActive || s.IsBusy || s.Links.Any(l => l.Start < start + url.Length && start < l.End)) return;
        editor.Select(start, url.Length);
        editor.SelectedText = link.Display;
        editor.Select(link.End, 0);
        if (FlushForIntent() && _source() is { IsActive: true, IsBusy: false } chosen)
            LinkChosen?.Invoke(chosen.Token, start, url);
        if (!_linkTipShown)
        {
            _linkTipShown = true;
            Motion.ActionFeedback.Announce(editor, "已转为链接芯片；发送时仍是完整链接，按 Ctrl+Z 恢复为文本");
        }
    }

    private bool SelectionHasChips(TextBox editor)
    {
        var text = editor.Text ?? string.Empty;
        int start = editor.SelectionStart, end = start + editor.SelectionLength;
        return editor.SelectionLength > 0 && LinkSpans.Any(l => l.Matches(text) && l.Start < end && start < l.End);
    }

    // Copy restores raw URLs ("[Image N]" is already its own text); pasting back here re-forms the chip.
    private void OnEditorCopyingToClipboard(TextBox sender, TextControlCopyingToClipboardEventArgs e)
    {
        if (!SelectionHasChips(sender)) return;
        e.Handled = true;
        OpenCodeLinkChipView.Copy(OpenCodeComposerChips.ExpandRange(sender.Text ?? string.Empty, sender.SelectionStart, sender.SelectionLength, LinkSpans));
    }

    private void OnEditorCuttingToClipboard(TextBox sender, TextControlCuttingToClipboardEventArgs e)
    {
        if (!SelectionHasChips(sender) || sender.IsReadOnly) return;
        e.Handled = true;
        OpenCodeLinkChipView.Copy(OpenCodeComposerChips.ExpandRange(sender.Text ?? string.Empty, sender.SelectionStart, sender.SelectionLength, LinkSpans));
        sender.SelectedText = string.Empty; // Native, undoable removal through the normal ledger.
    }

    // Ctrl+click opens (the click itself already placed the caret); a plain click only places the caret.
    private void OnEditorPointerReleasedForChips(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TextBox editor || editor.SelectionLength != 0) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        if (LinkAt(editor.SelectionStart) is { } link)
        {
            e.Handled = true;
            _ = OpenCodeLinkChipView.OpenAsync(link.Url);
        }
    }

    /// <summary>The link chip containing or touching <paramref name="caret"/> (context menu / Ctrl+click target).</summary>
    internal OpenCodeComposerLinkSpan? LinkAt(int caret)
    {
        var text = _editor?.Text ?? string.Empty;
        return LinkSpans.FirstOrDefault(l => l.Matches(text) && caret >= l.Start && caret <= l.End);
    }

    internal OpenCodeComposerLinkSpan? LinkAtCaret() => _editor is { } e ? LinkAt(e.SelectionStart) : null;

    /// <summary>
    /// Backspace at a chip's end / Delete at its start removes the whole link or "[Image N]" chip as one native,
    /// undoable edit (an image chip also removes its attachment; Ctrl+Z brings both back).
    /// </summary>
    private bool TryDeleteChipToken(bool backward)
    {
        if (_editor is not { } editor || editor.IsReadOnly || editor.SelectionLength != 0 || !_lifetime.IsLive) return false;
        if (!FlushForIntent()) return false;
        var text = editor.Text ?? string.Empty;
        var caret = editor.SelectionStart;
        (int Start, int End)? hit = null;
        foreach (var l in LinkSpans)
            if (l.Matches(text) && (backward ? l.End == caret : l.Start == caret)) hit = (l.Start, l.End);
        foreach (var i in ImageSpans)
            if (i.Matches(text) && (backward ? i.End == caret : i.Start == caret)) hit = (i.Start, i.End);
        if (hit is not { } h) return false;
        editor.Select(h.Start, h.End - h.Start);
        editor.SelectedText = string.Empty;
        editor.Select(h.Start, 0);
        return true;
    }

    /// <summary>"转回文字": the label becomes the full URL text (native edit; Ctrl+Z re-forms the chip).</summary>
    internal bool ConvertLinkToText(OpenCodeComposerLinkSpan link)
    {
        if (_editor is not { } editor || editor.IsReadOnly || !FlushForIntent() || !link.Matches(editor.Text ?? string.Empty)) return false;
        editor.Select(link.Start, link.Display.Length);
        editor.SelectedText = link.Url;
        editor.Select(link.Start + link.Url.Length, 0);
        if (editor.FocusState == FocusState.Unfocused) editor.Focus(FocusState.Programmatic);
        return true;
    }

    /// <summary>"删除" from the chip menu: same whole-chip removal as Backspace.</summary>
    internal bool DeleteLink(OpenCodeComposerLinkSpan link)
    {
        if (_editor is not { } editor || editor.IsReadOnly || !FlushForIntent() || !link.Matches(editor.Text ?? string.Empty)) return false;
        editor.Select(link.Start, link.Display.Length);
        editor.SelectedText = string.Empty;
        editor.Select(link.Start, 0);
        return true;
    }

    /// <summary>A pasted image's "[Image N]" was inserted at <c>start</c> (page → controller.TryAddImage).</summary>
    internal event Action<OpenCodeComposerToken, int, int, Guid>? ImageChosen;

    /// <summary>
    /// Inserts the TUI placeholder "[Image N]" at the caret (native edit, like typing) for a just-staged pasted image
    /// and binds it to that attachment. Text stays exactly what the TUI writes, so sessions interoperate.
    /// </summary>
    internal bool InsertImageChip(int number, Guid attachmentId)
    {
        if (_editor is not { } editor || editor.IsReadOnly || !_lifetime.IsLive || !FlushForIntent()) return false;
        var token = OpenCodeComposerChips.ImageToken(number);
        var text = editor.Text ?? string.Empty;
        var start = editor.SelectionStart;
        var before = start > 0 && !char.IsWhiteSpace(text[start - 1]) ? " " : string.Empty;
        var end = start + editor.SelectionLength;
        var after = end < text.Length && char.IsWhiteSpace(text[end]) ? string.Empty : " ";
        editor.SelectedText = before + token + after;
        start += before.Length;
        editor.Select(start + token.Length + after.Length, 0);
        if (!FlushForIntent() || _source() is not { IsActive: true, IsBusy: false } chosen) return false;
        ImageChosen?.Invoke(chosen.Token, start, number, attachmentId);
        return true;
    }

    /// <summary>The caret never rests inside a chip: it hops to the edge it was travelling towards.</summary>
    private int SnapChipCaret(string text, int caret, int previous)
    {
        foreach (var l in LinkSpans)
            if (l.Matches(text) && caret > l.Start && caret < l.End) return caret >= previous ? l.End : l.Start;
        foreach (var i in ImageSpans)
            if (i.Matches(text) && caret > i.Start && caret < i.End) return caret >= previous ? i.End : i.Start;
        return caret;
    }
}
