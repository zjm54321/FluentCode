using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Link and pasted-image chips (spec: artifacts/design-rich-chips-20261106T151052Z/spec.md §4–§5). The editor text holds
/// a short label for a link chip; this ledger maps it back to the exact URL for send and copy. "[Image N]" is literal
/// TUI text bound to one attachment: removing either removes the other, and Undo of the text deletion restores both.
/// </summary>
internal sealed partial class OpenCodeComposerController
{
    private readonly List<OpenCodeComposerLinkSpan> _links = new();
    private readonly List<OpenCodeComposerImageSpan> _images = new();
    private int _imageHighWater;
    // The last edit that dropped chips: an edit returning to exactly Before (native Undo) re-attaches them.
    private (string Before, OpenCodeComposerLinkSpan[] Links, (OpenCodeComposerImageSpan Span, StoredFile? File)[] Images)? _chipUndo;

    private void ResetChips(IEnumerable<OpenCodeComposerLinkSpan>? links, IEnumerable<OpenCodeComposerImageSpan>? images, int highWater)
    {
        _links.Clear();
        _images.Clear();
        if (links is not null) _links.AddRange(links);
        if (images is not null) _images.AddRange(images);
        _imageHighWater = highWater;
        _chipUndo = null;
    }

    /// <summary>The text the model receives: link chips restored to their full URLs.</summary>
    private string SendText(string text) => OpenCodeComposerChips.Expand(text, _links);

    /// <summary>Mention spans re-based onto <see cref="SendText"/> (links before a mention lengthen the text).</summary>
    private OpenCodePromptContext[] SendContexts(string text)
        => _contexts.Select(c => c with { Start = OpenCodeComposerChips.ExpandOffset(text, c.Start, _links) }).ToArray();

    /// <summary>Called under the lock from <see cref="UpdateContextText"/> before <c>_text</c> changes.</summary>
    private void UpdateChipText(string before, string after)
    {
        if (_chipUndo is { } undo && string.Equals(after, undo.Before, StringComparison.Ordinal) && !string.Equals(before, after, StringComparison.Ordinal))
        {
            OpenCodeComposerChips.Shift(_links, before, after, l => l.Start, l => l.End, (l, s) => l with { Start = s }, (l, t) => l.Matches(t));
            OpenCodeComposerChips.Shift(_images, before, after, i => i.Start, i => i.End, (i, s) => i with { Start = s }, (i, t) => i.Matches(t));
            foreach (var l in undo.Links) if (l.Matches(after) && _links.All(x => x.Start != l.Start)) _links.Add(l);
            foreach (var (span, file) in undo.Images)
            {
                if (!span.Matches(after) || _images.Any(x => x.Start == span.Start)) continue;
                if (file is not null && _files.All(f => f.Id != file.Id))
                {
                    if (_files.Count >= OpenCodeAttachment.MaxFiles) continue;
                    _files.Add(file);
                }
                _images.Add(span);
            }
            _chipUndo = null;
            return;
        }
        var oldLinks = _links.ToArray();
        var oldImages = _images.ToArray();
        OpenCodeComposerChips.Shift(_links, before, after, l => l.Start, l => l.End, (l, s) => l with { Start = s }, (l, t) => l.Matches(t));
        OpenCodeComposerChips.Shift(_images, before, after, i => i.Start, i => i.End, (i, s) => i with { Start = s }, (i, t) => i.Matches(t));
        var lostLinks = oldLinks.Where(l => _links.All(x => x.Url != l.Url || x.Display != l.Display || !Moved(l, x, before, after))).ToArray();
        var lostImages = oldImages.Where(i => _images.All(x => x.AttachmentId != i.AttachmentId)).ToArray();
        if (lostLinks.Length == 0 && lostImages.Length == 0)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal)) _chipUndo = null;
            return;
        }
        // Deleting "[Image N]" removes its attachment (kept privately for Undo; numbers are never reused).
        var removed = new List<(OpenCodeComposerImageSpan, StoredFile?)>();
        foreach (var image in lostImages)
        {
            var file = _files.FirstOrDefault(f => f.Id == image.AttachmentId);
            if (file is not null) _files.Remove(file);
            removed.Add((image, file));
        }
        _chipUndo = (before, lostLinks, removed.ToArray());
    }

    private static bool Moved(OpenCodeComposerLinkSpan old, OpenCodeComposerLinkSpan now, string before, string after)
    {
        var (_, oldEnd, delta) = OpenCodeComposerChips.Diff(before, after);
        return now.Start == old.Start || (old.Start >= oldEnd && now.Start == old.Start + delta);
    }

    /// <summary>
    /// Binds a chip label the editor just inserted at <paramref name="start"/> (native, undoable edit) to its URL.
    /// Rejected unless the label is exactly there in the current draft.
    /// </summary>
    internal bool TryAddLink(OpenCodeComposerToken token, int start, string url)
    {
        lock (_sync)
        {
            if (OpenCodeComposerLinkSpan.Create(start, url) is not { } link) return false;
            if (!Current(token) || Busy || HasClientAction || !link.Matches(_text)
                || _links.Any(l => l.Start < link.End && link.Start < l.End)) return false;
            _links.Add(link);
            _chipUndo = null;
            Edited(preserveOrigin: true);
        }
        Publish();
        return true;
    }

    /// <summary>Next "[Image N]" number for this draft (max used + 1; deletions never renumber).</summary>
    internal int NextImageNumber
    {
        get { lock (_sync) return OpenCodeComposerChips.NextImageNumber(_images, _imageHighWater); }
    }

    /// <summary>Binds literal "[Image N]" at <paramref name="start"/> to a staged attachment.</summary>
    internal bool TryAddImage(OpenCodeComposerToken token, int start, int number, Guid attachmentId)
    {
        lock (_sync)
        {
            var image = new OpenCodeComposerImageSpan(start, number, attachmentId);
            if (!Current(token) || Busy || HasClientAction || !image.Matches(_text) || _files.All(f => f.Id != attachmentId)
                || _images.Any(i => i.AttachmentId == attachmentId || i.Start < image.End && image.Start < i.End)) return false;
            _images.Add(image);
            _imageHighWater = Math.Max(_imageHighWater, number);
            Edited(preserveOrigin: true);
        }
        Publish();
        return true;
    }

    /// <summary>Attachment-bar × also removes its "[Image N]" (and one separating space after it) from the text.</summary>
    private void RemoveImageTextFor(Guid attachmentId)
    {
        var image = _images.FirstOrDefault(i => i.AttachmentId == attachmentId);
        if (image is null) return;
        _images.Remove(image);
        if (!image.Matches(_text)) return;
        var end = image.End < _text.Length && _text[image.End] == ' ' ? image.End + 1 : image.End;
        var next = _text.Remove(image.Start, end - image.Start);
        // The exact removed range is known: shift explicitly (a text diff could be ambiguous between "[Image 1]" and "[Image 2]").
        var removed = end - image.Start;
        for (var i = _links.Count - 1; i >= 0; i--)
        {
            var l = _links[i];
            if (l.Start >= end) _links[i] = l = l with { Start = l.Start - removed };
            if (!l.Matches(next)) _links.RemoveAt(i);
        }
        for (var i = _images.Count - 1; i >= 0; i--)
        {
            var m = _images[i];
            if (m.Start >= end) _images[i] = m = m with { Start = m.Start - removed };
            if (!m.Matches(next)) _images.RemoveAt(i);
        }
        for (var i = _contexts.Count - 1; i >= 0; i--)
        {
            var c = _contexts[i];
            if (c.Start >= end) _contexts[i] = c = c with { Start = c.Start - removed };
            if (!c.Matches(next)) _contexts.RemoveAt(i);
        }
        _text = next;
        _chipUndo = null;
    }
}
