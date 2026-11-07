using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class OpenCodeComposerView
{
    private OpenCodeComposerImagePaste? _imagePaste;
    private Guid? _hoverToken;
    private void OnChipPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_editor is not { } editor || _composing || !IsControlDown()) return;
        if (editor.HitToken(args.GetCurrentPoint(editor).Position) is { Kind: OpenCodeComposerInlineKind.Link } token)
        {
            args.Handled = true;
            _ = OpenCodeLinkChipView.OpenAsync(token.Value);
        }
    }
    private void OnChipPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_editor is not { } editor || _composing) return;
        var token = editor.HitToken(args.GetCurrentPoint(editor).Position);
        if (_hoverToken == token?.Id) return;
        _hoverToken = token?.Id;
        object? content = token?.Label;
        if (token is { Kind: OpenCodeComposerInlineKind.Link } link && OpenCodeLinkChipParser.Parse(link.Value) is { } chip)
            content = OpenCodeLinkChipView.CreateCard(chip);
        ToolTipService.SetToolTip(editor, content is null ? null : new ToolTip { Content = content });
    }
    private void OnChipPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _hoverToken = null;
        if (_editor is { } editor) ToolTipService.SetToolTip(editor, null);
    }

    internal OpenCodeComposerLinkSpan? LinkAtCaret()
    {
        if (_editor is not { } editor || _composing) return null;
        var selection = editor.NativeSelection;
        var document = editor.ModelDocument;
        var item = document.Objects.FirstOrDefault(x => x.Token.Kind == OpenCodeComposerInlineKind.Link
            && (x.Position == selection.Start || selection.Start == selection.End && x.Position + 1 == selection.Start));
        return item.Token is null ? null : new(document.TextOffset(item.Position), item.Token.Value, item.Token.Value);
    }
    internal void ConvertLinkToText(OpenCodeComposerLinkSpan link) => ReplaceLink(link, link.Url);
    internal void DeleteLink(OpenCodeComposerLinkSpan link) => ReplaceLink(link, "");
    private void ReplaceLink(OpenCodeComposerLinkSpan link, string text)
    {
        if (_editor is not { } editor || editor.IsReadOnly || _composing || !FlushForIntent()) return;
        var document = editor.ModelDocument;
        var item = document.Objects.FirstOrDefault(x => x.Token.Kind == OpenCodeComposerInlineKind.Link
            && document.TextOffset(x.Position) == link.Start && x.Token.Value == link.Url);
        if (item.Token is null) return;
        var next = document.Replace(item.Position, 1, text);
        editor.StageDocument(next, new(item.Position + text.Length, item.Position + text.Length));
        FlushForIntent();
        RequestRender();
    }

    /// <summary>One model observation owns staged bytes, inline IDs and text under the existing paste acknowledgment.</summary>
    internal bool InsertImagePaste(OpenCodeComposerImagePaste paste)
    {
        if (_editor is not { } editor || _composing || !FlushForIntent() || _source().Token != paste.Token
            || editor.Text != paste.BeforeText || editor.SelectionStart != paste.SelectionStart
            || editor.SelectionLength != paste.SelectionLength) return false;
        _imagePaste = paste;
        try
        {
            var caret = paste.Document.NativeOffset(paste.SelectionStart + paste.InsertionText.Length, end: true);
            editor.StageDocument(paste.Document, new(caret, caret));
            var accepted = FlushForIntent() && _sync?.BaselineToken == _source().Token;
            if (accepted) _sync!.Reconcile();
            return accepted;
        }
        finally { _imagePaste = null; }
    }
    internal bool InsertPlainPaste(OpenCodeComposerToken token, string text)
    {
        if (_editor is not { } editor || _composing || !FlushForIntent() || _source().Token != token || _source().IsBusy) return false;
        var before = editor.ModelDocument;
        var selection = editor.NativeSelection;
        var document = before.Replace(selection.Start, selection.End - selection.Start, text);
        var caret = selection.Start + document.Length - (before.Length - (selection.End - selection.Start));
        editor.StageDocument(document, new(caret, caret));
        return FlushForIntent();
    }
}
