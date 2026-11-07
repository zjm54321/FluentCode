using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>Private staged bytes plus one exact document transaction. Publication happens only in its ACK.</summary>
internal sealed class OpenCodeComposerImagePaste(OpenCodeComposerToken token, int start, int length,
    string beforeText, string insertionText, string text, OpenCodeComposerDocument document,
    Func<OpenCodeComposerToken, string, OpenCodeComposerEditorAck?> apply)
{
    internal OpenCodeComposerToken Token { get; } = token;
    internal int SelectionStart { get; } = start;
    internal int SelectionLength { get; } = length;
    internal string BeforeText { get; } = beforeText;
    internal string InsertionText { get; } = insertionText;
    internal string Text { get; } = text;
    internal OpenCodeComposerDocument Document { get; } = document;
    internal OpenCodeComposerEditorAck? TryApply(OpenCodeComposerToken basis, string editedText) => apply(basis, editedText);
}

internal sealed partial class OpenCodeComposerController
{
    private sealed record DocumentFrame(OpenCodeComposerDocument Document, OpenCodeComposerSelection Selection, StoredFile[] Files);
    private readonly Stack<DocumentFrame> _undo = new();
    private readonly Stack<DocumentFrame> _redo = new();
    private IReadOnlyList<OpenCodeComposerLinkSpan> _links => _document.Objects
        .Where(x => x.Token.Kind == OpenCodeComposerInlineKind.Link)
        .Select(x => new OpenCodeComposerLinkSpan(_document.TextOffset(x.Position), x.Token.Value, x.Token.Value)).ToArray();
    private IReadOnlyList<OpenCodeComposerImageSpan> _images => _document.Objects
        .Where(x => x.Token.Kind == OpenCodeComposerInlineKind.Image)
        .Select(x => new OpenCodeComposerImageSpan(_document.TextOffset(x.Position), x.Token.Number, x.Token.AttachmentId!.Value)).ToArray();
    private string SendText(string _) => _document.Serialize().Text;
    private OpenCodePromptContext[] SendContexts(string _) => _document.Serialize().Contexts;
    internal int NextImageNumber { get { lock (_sync) return _document.ImageHighWater + 1; } }
    private void RememberDocument()
    {
        _undo.Push(new(_document, _selection, _files.ToArray()));
        _redo.Clear();
    }
    private void CommitDocument(OpenCodeComposerDocument next, OpenCodeComposerSelection selection, bool remember = true)
    {
        if (remember && !_document.SameContent(next)) RememberDocument();
        var removed = _document.Objects.Where(x => x.Token.Kind == OpenCodeComposerInlineKind.Image
            && next.Objects.All(y => y.Token.Id != x.Token.Id)).Select(x => x.Token.AttachmentId).ToHashSet();
        _files.RemoveAll(file => removed.Contains(file.Id));
        _document = next;
        _selection = selection.Clamp(next.Length);
    }
    internal void SetDocumentSelection(OpenCodeComposerToken token, OpenCodeComposerSelection selection)
    {
        lock (_sync) { if (Current(token)) _selection = selection.Clamp(_document.Length); }
    }
    internal bool TryUndoDocument(OpenCodeComposerToken token, bool redo = false)
    {
        lock (_sync)
        {
            if (!Current(token) || Busy || HasClientAction) return false;
            var from = redo ? _redo : _undo;
            var to = redo ? _undo : _redo;
            if (!from.TryPop(out var frame)) return false;
            to.Push(new(_document, _selection, _files.ToArray()));
            var highWater = _document.ImageHighWater;
            _document = frame.Document.WithHighWater(highWater);
            _selection = frame.Selection.Clamp(_document.Length);
            _files.Clear();
            _files.AddRange(frame.Files);
            Edited(preserveOrigin: true);
        }
        Publish();
        return true;
    }
    internal OpenCodeComposerEditorAck? TryApplyDocumentObservation(OpenCodeComposerToken token,
        OpenCodeComposerDocumentObservation observation, bool allowUnboundCarry = false)
    {
        OpenCodeComposerEditorAck acknowledgment;
        lock (_sync)
        {
            if (!observation.IsValid || !_document.SameContent(observation.Before)
                || !Current(token) && !(allowUnboundCarry && _binding?.Slot is null && Current(Token)
                    && _origin is null && _command is null && _files.Count == 0 && observation.Document.Objects.Count == 0
                    && token.Draft == _draft && token.Edit == _edit)) return null;
            CommitDocument(observation.Document, observation.Selection);
            Edited();
            acknowledgment = new(Token, _text) { Document = _document };
        }
        Publish();
        return acknowledgment;
    }
    internal bool TryReplaceDocument(OpenCodeComposerToken token, int start, int length, string text)
    {
        lock (_sync)
        {
            if (!Current(token) || Busy || HasClientAction || start < 0 || length < 0 || start > _document.Length - length) return false;
            var next = _document.Replace(start, length, text);
            var caret = start + next.Length - (_document.Length - length);
            CommitDocument(next, new(caret, caret));
            Edited();
        }
        Publish();
        return true;
    }
    internal bool TryAddLink(OpenCodeComposerToken token, int start, string url)
    {
        lock (_sync)
        {
            var link = OpenCodeComposerLinkSpan.Create(start, url);
            if (!Current(token) || Busy || HasClientAction || link is null || !link.Matches(_text)) return false;
            var from = _document.NativeOffset(start);
            var end = _document.NativeOffset(link.End, true);
            CommitDocument(_document.InsertToken(from, end - from,
                new(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, url, OpenCodeLinkChipParser.Parse(url)!.Label)), new(from + 1, from + 1));
            Edited(preserveOrigin: true);
        }
        Publish();
        return true;
    }
    internal async Task<OpenCodeComposerImagePaste?> StageImagePasteAsync(OpenCodeComposerPickTicket ticket,
        IReadOnlyList<OpenCodeComposerFileSource> sources, int start, int length, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(sources);
        OpenCodeComposerBinding binding;
        CancellationToken scopeCancellation;
        StoredFile[] existing;
        OpenCodeComposerDocument before;
        bool invalid;
        lock (_sync)
        {
            invalid = !OwnsPick() || start < 0 || length < 0 || start > _text.Length - length;
            binding = _binding!;
            scopeCancellation = _scopeCancellation.Token;
            existing = _files.ToArray();
            before = _document;
        }
        if (invalid) { CancelPick(ticket); return null; }
        var retained = false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Source.RevocationToken, scopeCancellation);
        try
        {
            var files = await ReadAttachmentFilesAsync(sources, existing,
                () => { lock (_sync) return OwnsPick(); }, linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (!OwnsPick() || linked.IsCancellationRequested || files.Length == 0) return null;
                if (files.Any(f => !f.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Image paste requires image attachments.");
                var raw = before.Serialize().Text;
                var runs = new List<OpenCodeComposerRun>();
                if (start > 0 && !char.IsWhiteSpace(raw[start - 1])) runs.Add(new OpenCodeComposerTextRun(" "));
                var number = before.ImageHighWater + 1;
                foreach (var file in files)
                {
                    runs.Add(new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Image,
                        "", "图片 " + number, AttachmentId: file.Id, Number: number++));
                    runs.Add(new OpenCodeComposerTextRun(" "));
                }
                if (start + length < raw.Length && char.IsWhiteSpace(raw[start + length])) runs.RemoveAt(runs.Count - 1);
                var from = before.NativeOffset(start);
                var end = before.NativeOffset(start + length, true);
                var document = before.Replace(from, end - from, runs);
                var text = document.Serialize().Text;
                var insertion = text.Substring(start, text.Length - (raw.Length - length));
                retained = true;
                return new(ticket.Token, start, length, raw, insertion, text, document,
                    (basis, editedText) => ApplyImagePaste(basis, editedText, ticket, before, document, files,
                        from + document.Length - (before.Length - (end - from))));
            }
        }
        catch (Exception)
        {
            lock (_sync) { if (OwnsPick()) _note = "附件未添加；原附件保持不变。"; }
            return null;
        }
        finally { if (!retained) CancelPick(ticket); }
        bool OwnsPick() => Current(ticket.Token) && _pickOwner == ticket.Owner;
    }
    private OpenCodeComposerEditorAck? ApplyImagePaste(OpenCodeComposerToken basis, string editedText,
        OpenCodeComposerPickTicket ticket, OpenCodeComposerDocument before, OpenCodeComposerDocument document, StoredFile[] files, int caret)
    {
        OpenCodeComposerEditorAck acknowledgment;
        lock (_sync)
        {
            if (basis != ticket.Token || !Current(basis) || _pickOwner != ticket.Owner || HasClientAction
                || !_document.SameContent(before) || editedText != document.Serialize().Text) return null;
            CommitDocument(document, new(caret, caret));
            _files.AddRange(files);
            _origin ??= _binding;
            _pickOwner = null;
            Edited(preserveOrigin: true);
            acknowledgment = new(Token, _text) { Document = _document };
        }
        Publish();
        return acknowledgment;
    }
    internal bool TryAddImage(OpenCodeComposerToken token, int start, int number, Guid attachmentId)
    {
        lock (_sync)
        {
            var image = new OpenCodeComposerImageSpan(start, number, attachmentId);
            if (!Current(token) || Busy || HasClientAction || number < 1 || !image.Matches(_text) || _files.All(f => f.Id != attachmentId)) return false;
            var from = _document.NativeOffset(start);
            var end = _document.NativeOffset(image.End, true);
            CommitDocument(_document.InsertToken(from, end - from,
                new(Guid.NewGuid(), OpenCodeComposerInlineKind.Image, "", "图片 " + number, AttachmentId: attachmentId, Number: number)), new(from + 1, from + 1));
            Edited(preserveOrigin: true);
        }
        Publish();
        return true;
    }
    private void RemoveImageTextFor(Guid attachmentId)
    {
        var image = _document.Objects.FirstOrDefault(x => x.Token.AttachmentId == attachmentId);
        if (image.Token is null) return;
        var length = image.Position + 1 < _document.Length && _document.Story[image.Position + 1] == ' ' ? 2 : 1;
        CommitDocument(_document.Replace(image.Position, length, ""), new(image.Position, image.Position), remember: false);
    }
}
