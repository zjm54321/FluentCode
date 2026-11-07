using System;
using System.Linq;
using FluentCode.Pages.OpenCode;

internal static partial class Program
{
    private static void DocumentModel()
    {
        var count = checks;
        var a = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, "https://example.test/a", "same label");
        var b = a with { Id = Guid.NewGuid(), Value = "https://example.test/b" };
        var both = OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, a).InsertToken(2, 0, b);
        OpenCodeComposerDocument Observe(string next, int anchor, int head, int caret)
        {
            Check(OpenCodeComposerEditReceipt.TryCreate(both.Story, next, new(anchor, head), new(caret, caret), out var receipt), "receipt is derived from the actual edit selection/caret");
            Check(receipt.TryApply(both, next, out var result), "receipt validates exact outside text and object order");
            return result;
        }
        var one = "x\ufffcy";
        Check(Observe(one, 3, 3, 2).Objects.Single().Token.Id == a.Id, "Backspace after B deletes B, not duplicate-label A");
        Check(Observe(one, 2, 2, 2).Objects.Single().Token.Id == a.Id, "Delete before B deletes B");
        Check(Observe(one, 1, 1, 1).Objects.Single().Token.Id == b.Id, "Delete before A preserves B even though ordinal 0 changed owner");
        Check(Observe(one, 1, 2, 1).Objects.Single().Token.Id == b.Id, "selection over A preserves B");
        Check(Observe("xleft\ufffc\ufffcy", 1, 1, 5).Objects.Select(x => x.Token.Id).SequenceEqual(new[] { a.Id, b.Id }), "leading-boundary insertion shifts positions without expanding identity anchors");
        Check(Observe("xZy", 1, 3, 2).Objects.Count == 0, "cross-token replacement removes exactly both tokens");
        Check(!OpenCodeComposerEditReceipt.TryCreate(both.Story, "WRONG\ufffcy", new(2, 2), new(2, 2), out _), "invalid outside text is held rather than guessed");
        var untrusted = Observe("x\ufffc\ufffc\ufffcy", 1, 1, 2);
        Check(untrusted.Objects.Count == 2 && untrusted.Story == both.Story, "foreign inserted object is stripped rather than assigned a token ordinal");
        Check(OpenCodeComposerEditReceipt.TryCreate(both.Story, "x\ufffc\ufffc\ufffcy", new(1, 1), new(2, 2), out var foreign)
            && foreign.SanitizedAfter == new OpenCodeComposerSelection(1, 1), "foreign-object stripping also preserves the caret before the first known chip");
        Check(OpenCodeComposerEditReceipt.TryCreate(both.Story, both.Story, new(1, 2), new(2, 2), out var replacement)
            && replacement.TryApply(both, both.Story, out var replaced) && replaced.Objects.Single().Token.Id == b.Id
            && replacement.SanitizedAfter == new OpenCodeComposerSelection(1, 1), "same-story replacement by a foreign object cannot inherit the removed token identity");
        var mention = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Mention, "review", "review", true);
        var image = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Image, "", "图片 1", AttachmentId: Guid.NewGuid(), Number: 1);
        var rich = both.InsertToken(3, 0, mention).InsertToken(4, 0, image);
        var wire = rich.Serialize();
        Check(wire.Text == "x" + a.Value + b.Value + "@review[Image 1]y" && wire.Contexts.Single().Start == 1 + a.Value.Length + b.Value.Length
            && wire.Contexts.Single().Matches(wire.Text), "single-pass serialization derives mention offsets after raw URLs");
        Check(OpenCodeComposerDocument.Text(wire.Text).Serialize().Text == wire.Text && OpenCodeComposerDocument.Text(wire.Text).Objects.Count == 0,
            "plain-copy round trip preserves raw values without restoring token authority");
        Check(rich.Replace(4, 1, "").ImageHighWater == 1 && rich.Replace(4, 1, "").WithHighWater(3).ImageHighWater == 3, "image high-water survives deletion");
        var imageB = image with { Id = Guid.NewGuid(), AttachmentId = Guid.NewGuid(), Number = 19, Label = "图片 19" };
        var images = OpenCodeComposerDocument.Empty.InsertToken(0, 0, image).InsertToken(1, 0, imageB);
        var surviving = images.Replace(0, 1, "");
        Check(surviving.RawText == "[Image 1]" && surviving.Objects.Single().Token is { Number: 1, Label: "图片 1" } survivor
            && survivor.Id == imageB.Id && survivor.AttachmentId == imageB.AttachmentId, "deletion renumbers the surviving image without changing identity/attachment");
        var pdf = image with { Id = Guid.NewGuid(), Kind = OpenCodeComposerInlineKind.Pdf, Number = 8 };
        var ordered = surviving.InsertToken(0, 0, image).InsertToken(1, 0, pdf);
        Check(ordered.RawText == "[Image 1][PDF 1][Image 2]" && ordered.Objects.Select(x => x.Token.Number).SequenceEqual(new[] { 1, 1, 2 }),
            "image/PDF ordinals are contiguous per kind in document order, including insertion before an existing image");
        var unicode = OpenCodeComposerDocument.Text("a\r\n😀z \n");
        Check(unicode.Story == "a\n😀z \n" && unicode.Length == 7, "UTF-16 positions, CRLF, user trailing space/newline preserved");
        Check(!OpenCodeComposerEditReceipt.TryCreate(unicode.Story, "a\n\ude00z \n", new(3, 3), new(2, 2), out _), "receipt cannot split a surrogate pair");
        var random = new Random(81273);
        var current = rich;
        for (var round = 0; round < 500; round++)
        {
            var start = random.Next(current.Length + 1);
            var removed = random.Next(current.Length - start + 1);
            var inserted = new string((char)('a' + round % 26), random.Next(4));
            var next = current.Story.Remove(start, removed).Insert(start, inserted);
            Check(OpenCodeComposerEditReceipt.TryCreate(current.Story, next, new(start, start + removed), new(start + inserted.Length, start + inserted.Length), out var receipt)
                && receipt.TryApply(current, next, out _), "random explicit replacement validates");
            var expected = current.Objects.Where(x => x.Position < start || x.Position >= start + removed).Select(x => x.Token.Id).ToArray();
            current = current.Replace(start, removed, inserted);
            Check(current.Objects.Select(x => x.Token.Id).SequenceEqual(expected) && current.Story == next, "random replacement preserves exactly surviving IDs and text");
        }
        Console.WriteLine($"PASS immutable document / editor receipts: {checks - count} assertions; duplicate ordinals, boundary insertion, replacement, Unicode, foreign-object stripping, send/copy offsets and deterministic properties.");
    }

    private static void DocumentProtocol()
    {
        var count = checks;
        using var f = new Fixture();
        var a = new OpenCodeComposerInlineToken(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, "https://example.test/a", "same label");
        var b = a with { Id = Guid.NewGuid(), Value = "https://example.test/b" };
        var doc = OpenCodeComposerDocument.Text("xy").InsertToken(1, 0, a).InsertToken(2, 0, b);
        Check(f.Core.TryApplyDocumentObservation(f.Core.Snapshot.Token, new(f.Core.Snapshot.Document, doc, new(3, 3))) is not null,
            "explicit model transaction installs duplicate-label opaque token identities");
        var local = doc;
        OpenCodeComposerDocumentObservation? observation = null;
        var writes = 0;
        var reenter = true;
        OpenCodeComposerEditorSync? sync = null;
        sync = new(() => f.Core.Snapshot, () => local.Serialize().Text, text => { writes++; local = OpenCodeComposerDocument.Text(text); },
            () => false, (token, text, carry) =>
            {
                var ack = f.Core.TryApplyDocumentObservation(token, sync!.SubmittedObservation!, carry);
                if (ack is { Document: { } accepted } && reenter)
                {
                    reenter = false;
                    local = accepted.Replace(accepted.Length, 0, "b");
                    observation = new(accepted, local, new(local.Length, local.Length));
                    sync.OnTextChanging();
                }
                return ack;
            }, () => observation, snapshot => { writes++; local = snapshot.Document; });
        local = doc.Replace(doc.Length, 0, "a");
        observation = new(doc, local, new(local.Length, local.Length));
        sync.OnTextChanging();
        Check(sync.Flush() == OpenCodeComposerInputState.Clean && f.Core.Snapshot.Text == "x" + a.Value + b.Value + "yab"
            && sync.LastAcknowledgment?.Revision == 2 && sync.BaselineToken == f.Core.Snapshot.Token && writes == 0,
            "reentrant document observation rebases only on its predecessor ACK, without a native echo/write");
        Check(f.Core.Snapshot.Document.Objects.Select(x => x.Token.Id).SequenceEqual(new[] { a.Id, b.Id }), "reentrant receipt chain preserves both duplicate IDs");

        // A stale native receipt must hold; the NEXT genuine edit recovers plain text only, never token capabilities.
        var stale = local;
        Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, 0, 0, "new "), "controller edit with deliberately delayed presentation");
        local = stale.Replace(stale.Length, 0, "x");
        observation = new(stale, local, new(local.Length, local.Length));
        sync.OnTextChanging();
        Check(sync.Flush() == OpenCodeComposerInputState.HeldStale && !sync.Prepare(), "stale document remains held across send/render");
        var held = local;
        local = local.Replace(local.Length, 0, "y");
        observation = new(held, local, new(local.Length, local.Length));
        sync.OnTextChanging();
        Check(sync.Flush() == OpenCodeComposerInputState.Clean && f.Core.Snapshot.Document.Objects.Count == 0
            && f.Core.Snapshot.Text == local.Serialize().Text, "new genuine edit recovers text without borrowing old link identities");

        var random = new Random(9182);
        for (var round = 0; round < 150; round++)
        {
            if (round % 15 == 0)
            {
                var snapshot = f.Core.Snapshot;
                Check(f.Core.TryApplyDocumentObservation(snapshot.Token, new(snapshot.Document, doc, new(1, 1))) is not null, "restore known model fixture transaction");
            }
            var before = f.Core.Snapshot.Document;
            var start = random.Next(before.Length + 1);
            var removed = random.Next(before.Length - start + 1);
            var inserted = new string((char)('a' + round % 26), 1 + random.Next(3));
            var selection = new OpenCodeComposerSelection(start + removed, start);
            f.Core.SetDocumentSelection(f.Core.Snapshot.Token, selection);
            Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, start, removed, inserted), "random model edit commits");
            var after = f.Core.Snapshot.Document;
            Check(f.Core.TryUndoDocument(f.Core.Snapshot.Token) && f.Core.Snapshot.Document.SameContent(before)
                && f.Core.Snapshot.Selection == selection, "random undo restores exact IDs, runs and anchor/head");
            Check(f.Core.TryUndoDocument(f.Core.Snapshot.Token, redo: true) && f.Core.Snapshot.Document.SameContent(after),
                "random redo restores the exact resulting document");
            var serialized = after.Serialize();
            Check(serialized.Contexts.All(x => x.Matches(serialized.Text)) && after.Objects.Select(x => x.Token.Id).Distinct().Count() == after.Objects.Count,
                "random history/copy serialization has valid mention offsets and unique opaque IDs");
        }
        Console.WriteLine($"PASS document-aware protocol/history: {checks - count} assertions; reentrant ACK, stale/held plain recovery and 150 edit/undo/redo properties.");
    }
}
