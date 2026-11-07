using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static string Label(string url) => OpenCodeLinkChipParser.Parse(url)?.Label ?? "<null>";

    private static async Task LinkChips()
    {
        await ImagePasteEditorRegression();
        // ---- §2 label rules (spec table + catalogue)
        Check(Label("https://github.com/alvinunreal/oh-my-opencode-slim") == "oh-my-opencode-slim", "repo label");
        Check(Label("https://github.com/alvinunreal/oh-my-opencode-slim/issues/1591") == "oh-my-opencode-slim #1591", "issue label repo #N (approved form)");
        var issue = OpenCodeLinkChipParser.Parse("https://github.com/alvinunreal/oh-my-opencode-slim/issues/1591")!;
        Check(issue.Owner == "alvinunreal" && issue.Kind == OpenCodeLinkKind.Issue && issue.Site == OpenCodeLinkSite.GitHub && issue.Number == "1591", "owner kept for hover card");
        Check(issue.AccessibleName.StartsWith("链接，GitHub issue，oh-my-opencode-slim 1591", StringComparison.Ordinal), "Narrator name");
        Check(Label("https://github.com/sst/opencode/pull/4821") == "opencode #4821"
            && OpenCodeLinkChipParser.Parse("https://github.com/sst/opencode/pull/4821")!.Kind == OpenCodeLinkKind.PullRequest, "PR label");
        Check(Label("https://gitlab.com/g/p/-/merge_requests/17") == "p !17", "GitLab MR keeps !");
        Check(Label("https://gitlab.com/group/sub/proj/-/issues/9") == "proj #9"
            && OpenCodeLinkChipParser.Parse("https://gitlab.com/group/sub/proj/-/issues/9")!.Owner == "group/sub", "GitLab nested group");
        Check(Label("https://github.com/sst/opencode/commit/d38b26e9a1b2c3d4e5f6") == "opencode@d38b26e", "commit 7-char sha");
        Check(Label("https://github.com/sst/opencode/blob/main/src/index.tsx#L1224-L1271") == "index.tsx:1224-1271", "file + line range");
        Check(Label("https://github.com/sst/opencode/blob/main/src/index.tsx#L5") == "index.tsx:5", "file + line");
        Check(Label("https://gitlab.com/g/p/-/blob/main/a.cs#L10-20") == "a.cs:10-20", "GitLab line range");
        Check(Label("https://codeberg.org/o/r/src/branch/main/lib/x.rs") == "x.rs", "Gitea src/branch path");
        Check(Label("https://gitee.com/o/r/pulls/12") == "r #12", "Gitee pulls");
        Check(Label("https://github.com/sst/opencode/releases/tag/v1.4.0") == "opencode v1.4.0", "release tag");
        Check(Label("https://github.com/sst") == "sst" && OpenCodeLinkChipParser.Parse("https://github.com/sst")!.Kind == OpenCodeLinkKind.Owner, "owner");
        Check(Label("https://github.com/") == "github.com", "forge home");
        Check(Label("https://www.npmjs.com/package/@scope/pkg") == "@scope/pkg" && Label("https://crates.io/crates/serde") == "serde", "package sites");
        Check(Label("https://acme.atlassian.net/browse/OPS-42") == "OPS-42" && Label("https://linear.app/t/issue/ENG-7/title") == "ENG-7", "tickets");
        Check(Label("https://blog.example.dev/posts/2026/rich-paste") == "blog.example.dev/…/rich-paste", "generic: host + last segment");
        Check(Label("https://www.example.com/") == "example.com" && Label("https://example.com/a") == "example.com/a", "generic home / one segment, www stripped");
        var local = OpenCodeLinkChipParser.Parse("http://localhost:5173/app")!;
        Check(local.Site == OpenCodeLinkSite.Local && local.Label == "localhost:5173/app", "localhost keeps port");
        Check(OpenCodeLinkChipParser.Parse("http://192.168.1.20:8080/")!.Label == "192.168.1.20:8080", "LAN IP keeps port");
        var longRepo = Label("https://github.com/o/an-extraordinarily-long-repository-name-here/issues/123456");
        Check(longRepo.Length <= 32 && longRepo.EndsWith(" #123456", StringComparison.Ordinal) && longRepo.Contains('…'), "long issue label ≤ 32 with suffix whole: " + longRepo);
        var e = OpenCodeLinkChipParser.Ellipsize(new string('a', 20) + new string('b', 20));
        Check(e.Length == 32 && e[16] == '…' && e.StartsWith("aaaa", StringComparison.Ordinal) && e.EndsWith("bbbb", StringComparison.Ordinal), "middle ellipsis exactly 32");
        // ---- look-alike / exact host
        Check(OpenCodeLinkChipParser.Parse("https://github.com.evil.io/a/b")!.Site == OpenCodeLinkSite.Generic, "brand by exact host only");
        Check(OpenCodeLinkChipParser.Parse("https://notgithub.com/a/b")!.Site == OpenCodeLinkSite.Generic, "suffix look-alike is generic");
        var idn = OpenCodeLinkChipParser.Parse("https://g\u0456thub.com/a")!; // Cyrillic і
        Check(idn.IsConfusable && idn.Site == OpenCodeLinkSite.Generic && idn.Label.StartsWith("xn--", StringComparison.Ordinal), "IDN shows punycode + warning: " + idn.Label);
        // ---- credentials hidden
        var cred = OpenCodeLinkChipParser.Parse("https://user:hunter2@example.com/x?token=abc&page=2")!;
        Check(cred.HasCredentials && !cred.Label.Contains("hunter2") && !cred.DisplayUrl.Contains("hunter2") && !cred.DisplayUrl.Contains("abc")
            && cred.DisplayUrl.Contains("token=•••") && cred.DisplayUrl.Contains("page=2") && cred.Url.Contains("hunter2"), "credentials hidden in label/card, raw URL intact: " + cred.DisplayUrl);
        // ---- invalid / other schemes
        foreach (var bad in new[] { "file:///c:/x", "vscode://x", "mailto:a@b.c", "ftp://h/x", "https://", "https:// x", "https://" + new string('a', 2050) + ".com" })
            Check(OpenCodeLinkChipParser.Parse(bad) is null, "not a chip: " + bad[..Math.Min(bad.Length, 24)]);
        // ---- finding links: punctuation, brackets, CJK, code, streaming
        static string[] Urls(string t, bool md = false, bool streaming = false) => OpenCodeLinkChipParser.Find(t, md, streaming).Select(m => m.Url).ToArray();
        Check(Urls("see https://a.com/x.").SequenceEqual(new[] { "https://a.com/x" }), "trailing period excluded");
        Check(Urls("见 https://a.com/x。谢谢").SequenceEqual(new[] { "https://a.com/x" }), "Chinese full stop excluded");
        Check(Urls("（https://a.com/x）").SequenceEqual(new[] { "https://a.com/x" }), "full-width parens excluded");
        Check(Urls("(see https://en.wikipedia.org/wiki/Foo_(bar))").SequenceEqual(new[] { "https://en.wikipedia.org/wiki/Foo_(bar)" }), "paired parens kept, unpaired trimmed");
        Check(Urls("a https://a.com, b https://b.com!").Length == 2, "two links, comma/bang trimmed");
        Check(Urls("x`https://a.com`y https://b.com", md: true).SequenceEqual(new[] { "https://b.com" }), "inline code skipped");
        Check(Urls("```\nhttps://a.com\n```\nhttps://b.com", md: true).SequenceEqual(new[] { "https://b.com" }), "fenced code skipped");
        Check(Urls("abchttps://a.com").Length == 0, "no link glued to a word");
        Check(Urls("go https://a.com/x", streaming: true).Length == 0 && Urls("go https://a.com/x ", streaming: true).Length == 1, "streaming: chip only after a boundary");
        Check(Urls("mailto:a@b.com file:///x").Length == 0, "other schemes stay text");
        // ---- markdown [text](url)
        Check(OpenCodeLinkChipParser.RewriteMarkdownLinks("[https://good.com](https://evil.io/x)") == "https://evil.io/x", "URL-looking text shows the REAL target");
        Check(OpenCodeLinkChipParser.RewriteMarkdownLinks("read [docs](https://a.com/d).") == "read docs (https://a.com/d).", "ordinary text keeps text + real target");
        Check(OpenCodeLinkChipParser.RewriteMarkdownLinks("![alt](https://a.com/i.png)") == "![alt](https://a.com/i.png)", "images untouched");
        Check(OpenCodeLinkChipParser.RewriteMarkdownLinks("`[x](https://a.com)`") == "`[x](https://a.com)`", "code untouched");
        // ---- private hosts / favicon policy
        foreach (var h in new[] { "localhost", "127.0.0.1", "10.1.2.3", "172.20.0.1", "192.168.0.1", "169.254.1.1", "100.64.0.1", "::1", "fe80::1", "fd00::1", "intranet", "nas.local", "svc.internal", "x.corp", "app.localhost" })
            Check(OpenCodeLinkChipParser.IsPrivateHost(h), "private: " + h);
        foreach (var h in new[] { "example.com", "8.8.8.8", "172.32.0.1", "2001:4860::8888" })
            Check(!OpenCodeLinkChipParser.IsPrivateHost(h), "public: " + h);
        var generic = OpenCodeLinkChipParser.Parse("https://blog.example.dev:8443/a?token=x")!;
        Check(!OpenCodeLinkChipParser.ShouldFetchFavicon(generic, false), "online setting off → globe, no fetch");
        Check(OpenCodeLinkChipParser.ShouldFetchFavicon(generic, true) && OpenCodeLinkChipParser.FaviconOrigin(generic) == "https://blog.example.dev:8443", "on → per-origin fetch, no path/query");
        Check(!OpenCodeLinkChipParser.ShouldFetchFavicon(issue, true), "forges keep bundled icons");
        Check(!OpenCodeLinkChipParser.ShouldFetchFavicon(local, true) && !OpenCodeLinkChipParser.ShouldFetchFavicon(OpenCodeLinkChipParser.Parse("https://10.0.0.5/")!, true), "never fetch private");
        Check(!OpenCodeLinkChipParser.ShouldFetchFavicon(OpenCodeLinkChipParser.Parse("http://example.com/")!, true) && !OpenCodeLinkChipParser.ShouldFetchFavicon(idn, true), "no plain-http or confusable fetch");
        var cache = new OpenCodeFaviconCache(3);
        foreach (var o in new[] { "https://a", "https://b", "https://c" }) cache.Put(o, new byte[] { 1 });
        _ = cache.TryGet("https://a", out _);
        cache.Put("https://d", new byte[] { 2 });
        Check(cache.Count == 3 && cache.TryGet("https://a", out _) && !cache.TryGet("https://b", out _), "favicon cache LRU bounded per origin");
        cache.Fail("https://e");
        Check(cache.TryGet("https://e", out var failed) && failed is null, "failure cached negatively → globe, no retry storm");
        Check(!OpenCodeFaviconCache.IsAcceptableIcon(new byte[OpenCodeFaviconCache.MaxIconBytes + 1], "image/png")
            && !OpenCodeFaviconCache.IsAcceptableIcon(new byte[] { 1 }, "text/html") && OpenCodeFaviconCache.IsAcceptableIcon(new byte[] { 1 }, "image/x-icon"), "icon size/type bounds");

        // ---- transcript [Image N]
        var tokens = OpenCodeComposerChips.AttachmentTokens("see [Image 1] and [Image 3] and [PDF 1]", 2, 1);
        Check(tokens.Count == 2 && tokens[0].Number == 1 && tokens[1].Pdf, "only [Image N]/[PDF N] backed by a file become chips");
        Check(OpenCodeComposerChips.AttachmentTokens("[Image 1]", 0, 0).Count == 0, "typed [Image 1] without file stays text");
        Check(OpenCodeComposerChips.SingleLink("  https://a.com/x \n") == "https://a.com/x" && OpenCodeComposerChips.SingleLink("a https://a.com") is null
            && OpenCodeComposerChips.SingleLink("https://a.com https://b.com") is null, "paste converts only a single link");

        // ---- composer: chip, send/copy restore, delete + undo, convert back
        using (var f = new Fixture())
        {
            f.Handler.Send = (_, _) => Task.FromResult(Json(Receipt));
            const string url = "https://github.com/alvinunreal/oh-my-opencode-slim/issues/1591";
            var span = OpenCodeComposerLinkSpan.Create(4, url)!;
            var text = "fix " + span.Display + " @explorer ";
            f.Text(text);
            Check(f.Core.TryAddLink(f.Core.Snapshot.Token, 4, url) && f.Core.Snapshot.Links.Count == 1, "link chip bound to label span");
            var mentionAt = f.Core.Snapshot.Text.IndexOf('@');
            Check(f.Core.TryAddContext(f.Core.Snapshot.Token, "explorer", false, mentionAt), "mention after chip");
            Check(f.Core.Snapshot.SendText == "fix " + url + " @explorer ", "send text restores the raw URL");
            var basis = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
            Check(basis.SendText.Contains(url) && basis.SendContexts[0].Start == ("fix " + url + " ").Length && basis.SendContexts[0].Matches(basis.SendText), "mention span re-based onto sent text");
            await f.Client.SendPromptGuardedAsync("ses_test", basis.SendText, null, send => { send(); return true; }, CancellationToken.None, contexts: basis.SendContexts);
            Check(f.Handler.LastBody!.Contains(url) && !f.Handler.LastBody.Contains("\\u2003"), "wire carries the full URL, never the label");
            Check(f.Core.Snapshot.Document.Serialize(0, 5).Text == "fix " + url, "copy serializes one whole native link object as raw URL");
            var fullText = f.Core.Snapshot.Text;
            var deleted = fullText.Remove(span.Start, url.Length);
            Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, 4, 1, ""), "delete exact native link position");
            Check(f.Core.Snapshot.Links.Count == 0 && f.Core.Snapshot.SendText == deleted, "whole-chip delete drops the link");
            Check(f.Core.TryUndoDocument(f.Core.Snapshot.Token), "model undo link deletion");
            Check(f.Core.Snapshot.Links.Count == 1 && f.Core.Snapshot.SendText.Contains(url), "Ctrl+Z restores the chip");
            Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, 4, 1, url), "convert token to its raw text explicitly");
            Check(f.Core.Snapshot.Links.Count == 0 && f.Core.Snapshot.Text.Contains(url), "convert back to text = raw URL in editor");
            Check(f.Core.TryUndoDocument(f.Core.Snapshot.Token), "undo conversion restores opaque token ID");
            Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, 0, 0, "Please "), "insert before native chip");
            Check(f.Core.Snapshot.Links.Count == 1, "chip survives edit before it: " + f.Core.Snapshot.Links.Count);
            Check(f.Core.Snapshot.Links.Single().Start == 11 && f.Core.Snapshot.SendText.StartsWith("Please fix " + url, StringComparison.Ordinal), "edit before chip moves span");
            var key = Key(f, Guid.NewGuid(), "/repo");
            var retained = Capture(f);
            Activate(f, key, f.Core.CreateEmptySlotData());
            Check(f.Core.Snapshot.Links.Count == 0, "other slot has no chips");
            Activate(f, key, retained);
            Check(f.Core.Snapshot.Links.Count == 1, "slot restoration keeps chips");
            f.Core.DiscardDraft(f.Core.Snapshot.Token);
            Check(f.Core.Snapshot.Links.Count == 0 && f.Core.Snapshot.Images.Count == 0, "discard clears chips");
        }

        // ---- [Image N]: contiguous document ordinals and opaque attachment linkage
        using (var f = new Fixture())
        {
            async Task<Guid> Paste(string after)
            {
                var before = f.Core.Snapshot.Attachments.Select(a => a.Id).ToHashSet();
                await f.File("p.png", 4);
                var id = f.Core.Snapshot.Attachments.Select(a => a.Id).First(x => !before.Contains(x));
                var n = f.Core.NextImageNumber;
                var start = f.Core.Snapshot.Text.Length;
                Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, f.Core.Snapshot.Document.Length, 0,
                    OpenCodeComposerChips.ImageToken(n) + after), "explicit image placeholder insertion preserves existing objects");
                Check(f.Core.TryAddImage(f.Core.Snapshot.Token, start, n, id), "image chip bound " + n);
                return id;
            }
            f.Text("look ");
            var one = await Paste(" ");
            await Paste(" ");
            Check(f.Core.Snapshot.Text == "look [Image 1] [Image 2] " && f.Core.Snapshot.Images.Count == 2, "[Image N] counts from 1 in TUI text form");
            var full = f.Core.Snapshot.Text;
            Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, f.Core.Snapshot.Document.NativeOffset(full.IndexOf("[Image 1]", StringComparison.Ordinal)), 1, ""), "explicit first image deletion");
            Check(f.Core.Snapshot.Images.Count == 1, "other image survives: " + string.Join(",", f.Core.Snapshot.Images.Select(i => i.Start + ":" + i.Number)) + " text=" + f.Core.Snapshot.Text);
            Check(f.Core.Snapshot.Attachments.All(a => a.Id != one) && f.Core.Snapshot.Images.Single().Number == 1
                && f.Core.Snapshot.Document.Objects.Single().Token.Label == "图片 1", "deleting the chip removes its attachment and renumbers the survivor");
            Check(f.Core.TryUndoDocument(f.Core.Snapshot.Token), "model undo restores image and bytes atomically");
            Check(f.Core.Snapshot.Attachments.Any(a => a.Id == one) && f.Core.Snapshot.Images.Count == 2, "Undo restores chip + attachment");
            f.Core.RemoveAttachment(f.Core.Snapshot.Token, one);
            Check(f.Core.Snapshot.Images.Count == 1, "x keeps other: " + f.Core.Snapshot.Text);
            Check(f.Core.Snapshot.Text == "look [Image 1] " && f.Core.Snapshot.Images.Single().Number == 1, "× on the attachment also renumbers the remaining image");
            var three = await Paste("");
            Check(f.Core.Snapshot.Text.EndsWith("[Image 2]", StringComparison.Ordinal) && f.Core.Snapshot.Images.Any(i => i.Number == 2 && i.AttachmentId == three), "new images continue the current contiguous ordinals");
            Check(f.Core.Snapshot.SendText == f.Core.Snapshot.Text, "[Image N] is sent verbatim (TUI parity)");
            f.Core.DiscardDraft(f.Core.Snapshot.Token);
            f.Text("again ");
            Check(f.Core.NextImageNumber == 1, "a fresh message restarts at 1");
        }
        using (var f = new Fixture())
        {
            var editor = new ImagePasteEditor(f);
            await editor.Paste(FileSource("A.png", 1), 0);
            await editor.Paste(FileSource("B.png", 2), 0); // File insertion order A,B; document order B,A.
            Check(f.Core.Snapshot.SendText == "[Image 1] [Image 2] ", "paste-before renumbers both labels in document order");
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.PromptAccepted, "ordered image draft sends");
            using var body = JsonDocument.Parse(f.Handler.LastBody!);
            Check(body.RootElement.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("name").GetString())
                .SequenceEqual(new[] { "B.png", "A.png" }), "wire file order follows the document, not attachment creation order");
            editor = new ImagePasteEditor(f); // The accepted send cleared the previous draft.
            await editor.Paste(FileSource("A.png", 1), 0);
            await editor.Paste(FileSource("B.png", 2), 0);
            var first = f.Core.Snapshot.Document.Objects.First().Position;
            Check(f.Core.TryReplaceDocument(f.Core.Snapshot.Token, first, 1, ""), "delete first pasted image");
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.PromptAccepted, "delete-A/send regression stays sendable");
            using var after = JsonDocument.Parse(f.Handler.LastBody!);
            Check(after.RootElement.GetProperty("text").GetString()!.Contains("[Image 1]", StringComparison.Ordinal)
                && !after.RootElement.GetProperty("text").GetString()!.Contains("[Image 2]", StringComparison.Ordinal)
                && after.RootElement.GetProperty("files").GetArrayLength() == 1
                && after.RootElement.GetProperty("files")[0].GetProperty("name").GetString() == "A.png", "one surviving file is sent as renderable [Image 1]");
        }

        // ---- production wiring (source checks; WinUI surfaces are not executed here)
        var repo = Environment.GetEnvironmentVariable("FC_SPLASH_REPO")!;
        string Src(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var view = Src("OpenCodeComposerView.cs") + Src("OpenCodeComposerView.LinkChips.cs");
        var adapter = Src("OpenCodeComposerEditor.cs");
        Check(adapter.Contains("data.SetText(SelectedText)") && adapter.Contains("_model.Serialize(_selection.Start")
            && adapter.Contains("args.Handled = true; RequestPaste();"), "copy/cut serialize raw values; native rich paste is synchronously consumed");
        Check(!view.Contains("TryDeleteChipToken") && view.Contains("ReplaceLink") && view.Contains("OpenCodeLinkChipView.OpenAsync(token.Value)"),
            "native atomic delete, model convert-back menu and Ctrl+click open");
        var presenter = Src("OpenCodeRichTextPresenter.cs");
        Check(presenter.Contains("OpenCodeLinkChipParser.Find(") && presenter.Contains("inCode") && presenter.Contains("RewriteMarkdownLinks"), "transcript chips skip code and see real markdown targets");
        var chipView = Src("OpenCodeLinkChipView.cs");
        Check(chipView.Contains("Uri.UriSchemeHttps") && chipView.Contains("Launcher.LaunchUriAsync") && chipView.Contains("AutomationProperties.SetName")
            && chipView.Contains("HighContrast"), "click opens http/https only; Narrator name; HC");
        var fav = Src("OpenCodeFaviconCache.cs");
        Check(fav.Contains("ConnectCallback") && fav.Contains("IsPrivateAddress"), "favicon fetch refuses private addresses after DNS");
    }

    private static async Task ImagePasteEditorRegression()
    {
        var before = checks;
        // The former production sequence was AddFiles -> native text ACK -> bool TryAddImage (+unacknowledged edit).
        // This adapter runs the new production staged transaction through the REAL input ledger, without a render.
        using (var f = new Fixture())
        {
            Activate(f, Key(f, Guid.NewGuid(), "/workspace"), f.Core.CreateEmptySlotData());
            f.Text("look ");
            var editor = new ImagePasteEditor(f);
            await editor.Paste(FileSource("clipboard.png", 4), editor.Text.Length);
            Check(editor.Text == "look [Image 1] " && f.Core.Snapshot.Images.Single().AttachmentId == f.Core.Snapshot.Attachments.Single().Id,
                "one acknowledged paste owns text, image span and attachment");
            Check(editor.Edit(editor.Text + "x") == OpenCodeComposerInputState.Clean,
                "image paste followed by typing before queued render must stay editable");
            Check(editor.Edit(editor.Text[..^1]) == OpenCodeComposerInputState.Clean, "Backspace after pasted image stays editable");
            var full = editor.Text;
            var span = f.Core.Snapshot.Images.Single();
            Check(editor.Edit(full.Remove(span.Start, span.End - span.Start)) == OpenCodeComposerInputState.Clean
                && f.Core.Snapshot.Images.Count == 0 && f.Core.Snapshot.Attachments.Count == 0, "whole-chip delete removes its attachment in the same native ACK");
            Check(editor.Undo() && f.Core.Snapshot.Attachments.Count == 1,
                "model Undo of chip deletion restores ID and attachment");
            Check(editor.Edit(full.Remove(span.Start, span.End - span.Start)) == OpenCodeComposerInputState.Clean && editor.Sync.Prepare(),
                "delete again leaves a sendable text draft");
            var submitted = f.Core.Snapshot.Token;
            Check(await LegacyAttempt(f, f.Context()) is OpenCodeComposerSubmitOutcome.PromptAccepted && f.Handler.Posts == 1,
                "send after paste/type/Backspace/chip delete takes the real ordinary guarded sender once");
            await f.Core.FinalizeAcceptedLegacyDraftAsync(submitted, f.Binding, () => true, default, (_, finalize, _) =>
            {
                Check(editor.Sync.FinalizeAccepted(submitted, finalize, out var waiting) == OpenCodeComposerDraftFinalization.Cleared && !waiting,
                    "accepted plain send clears through the real editor handoff");
                return Task.CompletedTask;
            });
            Check(editor.Text.Length == 0 && editor.Sync.Prepare(), "composer remains editable after accepted send");
        }
        using (var f = new Fixture())
        {
            Activate(f, Key(f, Guid.NewGuid(), "/workspace"), f.Core.CreateEmptySlotData());
            var editor = new ImagePasteEditor(f);
            await editor.Paste(FileSource("clipboard.png", 4), 0);
            Check(editor.Text == "[Image 1] " && editor.Sync.Prepare(), "image paste into empty composer is acknowledged and sendable");
            Check(editor.Edit(editor.Text + "explain") == OpenCodeComposerInputState.Clean, "empty-composer paste accepts immediate typing");
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.PromptAccepted
                && f.Handler.Posts == 1 && f.Handler.LastBody!.Contains("[Image 1] explain", StringComparison.Ordinal)
                && f.Handler.LastBody.Contains("image/png", StringComparison.Ordinal), "empty-composer image sends literal TUI token and its actual file together");
        }
        await ImagePasteSwitchRegression();
        await ImagePasteHeldRecovery();
        LinkPasteAcknowledgmentRegression();
        PendingImageProjectionRegression();
        Console.WriteLine($"PASS stuck composer: {checks - before} assertions; real controller/EditorSync/slots, staged image transaction, edit/delete/undo/send, switch cancellation, held recovery and pending attachment projection.");
    }

    private static void LinkPasteAcknowledgmentRegression()
    {
        using var f = new Fixture();
        const string url = "https://github.com/example/project/issues/1";
        f.Text("see " + url + " now");
        var editor = f.Core.Snapshot.Text;
        var before = f.Core.Snapshot.Document;
        var document = before.InsertToken(4, url.Length, new(Guid.NewGuid(), OpenCodeComposerInlineKind.Link, url, OpenCodeLinkChipParser.Parse(url)!.Label));
        OpenCodeComposerDocumentObservation observation = new(before, document, new(5, 5));
        OpenCodeComposerEditorSync? sync = null;
        sync = new(() => f.Core.Snapshot, () => editor, text => editor = text, () => false,
            (basis, text, carry) => f.Core.TryApplyDocumentObservation(basis, sync!.SubmittedObservation!, carry), () => observation);
        var token = f.Core.Snapshot.Token;
        sync.OnTextChanging();
        Check(sync.Flush() == OpenCodeComposerInputState.Clean && f.Core.Snapshot.Token.Edit == token.Edit + 1
            && f.Core.Snapshot.Links.Count == 1 && sync.BaselineToken == f.Core.Snapshot.Token,
            "one model link transaction acknowledges token identity under one revision even when raw text is unchanged");
        editor = f.Core.Snapshot.Text + "x";
        document = f.Core.Snapshot.Document;
        var next = document.Replace(document.Length, 0, "x");
        observation = new(document, next, new(next.Length, next.Length));
        sync.OnTextChanging();
        Check(sync.Flush() == OpenCodeComposerInputState.Clean
            && f.Core.Snapshot.SendText == "see " + url + " nowx",
            "typing immediately after URL chip conversion keeps the accepted baseline and raw URL send text");
    }

    private sealed class ImagePasteEditor
    {
        private readonly Fixture _fixture;
        private OpenCodeComposerImagePaste? _paste;
        private OpenCodeComposerDocument _document;
        private OpenCodeComposerDocumentObservation? _observation;
        internal string Text;
        internal bool Composing;
        internal readonly OpenCodeComposerEditorSync Sync;

        internal ImagePasteEditor(Fixture fixture)
        {
            _fixture = fixture;
            Text = fixture.Core.Snapshot.Text;
            _document = fixture.Core.Snapshot.Document;
            OpenCodeComposerEditorSync? sync = null;
            Sync = sync = new(() => fixture.Core.Snapshot, () => Text, text => { Text = text; _document = fixture.Core.Snapshot.Document; sync!.OnTextChanging(); },
                () => Composing, (basis, text, carry) =>
                {
                    var paste = _paste;
                    _paste = null;
                    return paste is not null ? paste.TryApply(basis, text)
                        : sync!.SubmittedObservation is { } observation ? fixture.Core.TryApplyDocumentObservation(basis, observation, carry)
                        : fixture.Core.TryApplyEditorInput(basis, text, carry);
                }, () => _observation);
        }

        internal OpenCodeComposerInputState Edit(string text)
        {
            var before = _document;
            OpenCodeComposerDocument next;
            if (_paste is { } paste) next = paste.Document;
            else if (Sync.State is OpenCodeComposerInputState.HeldStale or OpenCodeComposerInputState.HeldUnbound)
            {
                before = _fixture.Core.Snapshot.Document;
                next = OpenCodeComposerDocument.Text(text); // Recovery is plain text, never old attachment authority.
            }
            else
            {
                // Test-driver receipt: callers supply the exact requested replacement string; this is not product inference.
                var prefix = 0;
                while (prefix < Text.Length && prefix < text.Length && Text[prefix] == text[prefix]) prefix++;
                var suffix = 0;
                while (suffix < Text.Length - prefix && suffix < text.Length - prefix && Text[Text.Length - 1 - suffix] == text[text.Length - 1 - suffix]) suffix++;
                var from = before.NativeOffset(prefix);
                var end = before.NativeOffset(Text.Length - suffix, true);
                next = before.Replace(from, end - from, text.Substring(prefix, text.Length - prefix - suffix));
            }
            _document = next;
            _observation = new(before, next, new(next.Length, next.Length));
            Text = text;
            Sync.OnTextChanging();
            return Sync.Flush();
        }
        internal bool Undo()
        {
            if (!_fixture.Core.TryUndoDocument(_fixture.Core.Snapshot.Token)) return false;
            Sync.Reconcile();
            _document = _fixture.Core.Snapshot.Document;
            return Sync.Prepare();
        }

        internal async Task Paste(OpenCodeComposerFileSource file, int start, int length = 0)
        {
            var basis = _fixture.Core.Snapshot;
            var ticket = _fixture.Core.BeginPick(basis.Token)!;
            try
            {
                var paste = await _fixture.Core.StageImagePasteAsync(ticket, new[] { file }, start, length);
                Check(paste is not null && _fixture.Core.Snapshot.Token == basis.Token
                    && _fixture.Core.Snapshot.Attachments.Count == basis.Attachments.Count, "staging publishes no placeholder, chip or attachment edit");
                var publications = 0;
                void Published(object? _, OpenCodeComposerSnapshotChangedEventArgs e)
                {
                    publications++;
                    Check(e.Snapshot.Text == paste!.Text && e.Snapshot.Images.Count == e.Snapshot.Attachments.Count
                        && e.Snapshot.Token.Edit == basis.Token.Edit + 1, "paste has one complete text/span/file publication under one edit revision");
                }
                _fixture.Core.SnapshotChanged += Published;
                try { Check(Apply(paste!), "native image insertion is acknowledged as one transaction"); }
                finally { _fixture.Core.SnapshotChanged -= Published; }
                Check(publications == 1 && Sync.LastAcknowledgment?.Revision == Sync.InputRevision
                    && Sync.BaselineToken == _fixture.Core.Snapshot.Token, "paste ACK advances displayed baseline before another native edit");
            }
            finally { _fixture.Core.CancelPick(ticket); }
        }

        internal bool Apply(OpenCodeComposerImagePaste paste)
        {
            if (!Sync.Prepare() || _fixture.Core.Snapshot.Token != paste.Token || Text != paste.BeforeText) return false;
            _paste = paste;
            return Edit(paste.Text) == OpenCodeComposerInputState.Clean && Sync.BaselineToken == _fixture.Core.Snapshot.Token;
        }
    }

    private static async Task ImagePasteSwitchRegression()
    {
        using var f = new Fixture();
        var epoch = Guid.NewGuid();
        var a = Key(f, epoch, "/workspace", "ses_a");
        var b = Key(f, epoch, "/workspace", "ses_b");
        Activate(f, a, f.Core.CreateEmptySlotData());
        f.Text("draft A");
        var savedA = Capture(f);
        var editor = new ImagePasteEditor(f);
        var opened = Signal<bool>();
        var release = Signal<bool>();
        var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
        var delayed = new OpenCodeComposerFileSource("clipboard.png", "image/png", async ct =>
        {
            opened.TrySetResult(true);
            await release.Task.WaitAsync(ct);
            return new MemoryStream(new byte[4]);
        });
        var pending = f.Core.StageImagePasteAsync(ticket, new[] { delayed }, editor.Text.Length, 0);
        await opened.Task;
        var activated = Activate(f, b, f.Core.CreateEmptySlotData());
        Check(editor.Sync.TryActivateClean(activated), "active session switch installs the new editor baseline");
        release.TrySetResult(true);
        Check(await pending is null && f.Core.Snapshot.Text.Length == 0 && f.Core.Snapshot.Attachments.Count == 0
            && !f.Core.Snapshot.IsBusy && editor.Sync.Prepare(), "in-flight paste cannot attach, insert or hold the newly activated session");
        Check(editor.Edit("draft B") == OpenCodeComposerInputState.Clean, "new session remains editable after canceled image paste");
        var savedB = Capture(f);
        activated = Activate(f, a, savedA);
        Check(editor.Sync.TryActivateClean(activated), "return to origin draft after canceled paste");
        ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
        var staged = await f.Core.StageImagePasteAsync(ticket, new[] { FileSource("clipboard.png", 4) }, editor.Text.Length, 0);
        Check(staged is not null, "image bytes can finish staging before a session switch");
        activated = Activate(f, b, savedB);
        Check(editor.Sync.TryActivateClean(activated) && !editor.Apply(staged!), "completed old paste is rejected before native insertion after switch");
        Check(staged!.TryApply(staged.Token, staged.Text) is null, "late transaction cannot mutate another slot even if its native guard is bypassed");
        f.Core.CancelPick(ticket);
        Check(editor.Text == "draft B" && f.Core.Snapshot.Attachments.Count == 0 && !f.Core.Snapshot.IsBusy,
            "staged old paste leaves target text, attachments and picker ownership intact");
        await editor.Paste(FileSource("new.png", 4), editor.Text.Length);
        Check(f.Core.Snapshot.Images.Single().Number == 1, "target draft has its own image numbering and acknowledged paste");
    }

    private static async Task ImagePasteHeldRecovery()
    {
        using var f = new Fixture();
        Activate(f, Key(f, Guid.NewGuid(), "/workspace"), f.Core.CreateEmptySlotData());
        f.Text("held ");
        var editor = new ImagePasteEditor(f);
        await editor.Paste(FileSource("clipboard.png", 4), editor.Text.Length);
        f.Text("changed " + editor.Text); // Controller-side edit with its render deliberately delayed.
        Check(editor.Edit(editor.Text + "x") == OpenCodeComposerInputState.HeldStale && !editor.Sync.Prepare(),
            "a genuinely stale observation is held and cannot send or auto-rebase on render");
        Check(editor.Edit(editor.Text + "y") == OpenCodeComposerInputState.Clean && editor.Sync.Prepare()
            && f.Core.Snapshot.Text == editor.Text, "a NEW genuine edit explicitly rebinds ordinary held input to the active draft");
        f.Text("changed again " + editor.Text);
        Check(editor.Edit(editor.Text + "z") == OpenCodeComposerInputState.HeldStale, "second real desync enters held recovery");
        editor.Composing = true;
        Check(!editor.Sync.TryClearHeldInput(f.Core.TryDiscardDraftForRecovery), "clear recovery does not interrupt native composition");
        editor.Composing = false;
        Check(editor.Sync.TryClearHeldInput(f.Core.TryDiscardDraftForRecovery) && editor.Text.Length == 0
            && f.Core.Snapshot.Attachments.Count == 0 && f.Core.Snapshot.Images.Count == 0 && editor.Sync.Prepare(),
            "clear-and-recover is acknowledged, removes text/chips/files, and releases ordinary held state");
        Check(editor.Edit("recovered") == OpenCodeComposerInputState.Clean, "typing works after explicit clear recovery");
        f.Text("delayed core change");
        Check(editor.Edit(string.Empty) == OpenCodeComposerInputState.HeldStale, "first stale native clear stays held until explicit new intent");
        Check(editor.Edit("restart") == OpenCodeComposerInputState.Clean, "typing after a held native clear recovers without a read-only trap");
        editor.Sync.SuspendForRecovery();
        Check(editor.Edit("old source readonly text") == OpenCodeComposerInputState.HeldStale
            && !editor.Sync.TryClearHeldInput(f.Core.TryDiscardDraftForRecovery), "forced recovery never adopts old-source text through ordinary typing/clear");
        f.Core.DiscardDraft(f.Core.Snapshot.Token);
        editor.Sync.ResolveRecovery(); // The page's existing explicit forced-recovery discard route.
        Check(editor.Sync.TryActivateClean(f.Core.Snapshot.Token) && editor.Text.Length == 0
            && editor.Edit("new source draft") == OpenCodeComposerInputState.Clean, "explicit forced-recovery discard releases the hold and permits a clean active draft");
    }

    private static void PendingImageProjectionRegression()
    {
        using var json = JsonDocument.Parse("""{"text":"[Image 1] [Image 2] [PDF 1]","files":[{"name":"clipboard","mime":"image/png","data":"AA=="},{"filename":"note.pdf","mime":"application/pdf","url":"file:///note.pdf"}]}""");
        var payload = json.RootElement.Clone();
        foreach (var delivery in new[] { OpenCodeDeliveryModes.Queue, OpenCodeDeliveryModes.Steer })
        {
            var item = new OpenCodeInboxItem { Id = "msg_pending", Type = OpenCodeInboxItemTypes.User, Delivery = delivery, Payload = payload };
            var pending = OpenCodeUserAttachments.FromInbox(item);
            var sent = OpenCodeUserAttachments.FromMessage(new() { Id = item.Id, Files = payload.GetProperty("files") });
            Check(pending.SequenceEqual(sent) && OpenCodeUserAttachments.Counts(pending) == (1, 1), "pending and delivered messages share image/PDF projection: " + delivery);
            Check(pending[0].Image?.Reference == "data:image/png;base64,AA==" && pending[0].Name == "图片",
                "clipboard source name becomes a proper image label and the actual shared thumbnail source");
            var counts = OpenCodeUserAttachments.Counts(pending);
            var chips = OpenCodeComposerChips.AttachmentTokens(payload.GetProperty("text").GetString(), counts.Images, counts.Pdfs);
            Check(chips.Count == 2 && chips[0].Number == 1 && chips[1].Pdf, "pending bubble chips require that message's own N-th attachment");
            Check(OpenCodeUserAttachments.FromInbox(item with { Payload = JsonSerializer.SerializeToElement(new { text = "[Image 1]" }) }).Count == 0,
                "typed image placeholder in another pending message does not inherit attachments");
            Check(OpenCodeUserAttachments.FromInbox(item with { Type = "synthetic" }).Count == 0, "non-user work does not acquire pending user image chips");
        }
        var repo = Environment.GetEnvironmentVariable("FC_SPLASH_REPO")!;
        string Src(string relative) => File.ReadAllText(Path.Combine(repo, "src/FluentCode", relative));
        var view = Src("Pages/OpenCode/OpenCodeComposerView.cs");
        var page = Src("Pages/OpenCodePage.Composer.cs");
        var pastePage = Src("Pages/OpenCodePage.ComposerPaste.cs");
        Check(pastePage.Contains("StageImagePasteAsync") && pastePage.Contains("InsertImagePaste") && !pastePage.Contains("AddFilesAsync"),
            "production clipboard path stages then makes one acknowledged native transaction, not file/text/metadata edits");
        Check(view.Contains("OpenCodeComposerRecoveryClear") && view.Contains("清空并恢复") && page.Contains("RecoveryClearRequested")
            && page.Contains("DiscardDraftSlotRecovery") && !view.Contains("|| _sync.State == OpenCodeComposerInputState.HeldStale"),
            "production recovery is editable or has an explicit wired clear/discard action, never ordinary held read-only");
        foreach (var relative in new[] { "Pages/OpenCode/OpenCodeConversationView.cs", "Pages/OpenCode/OpenCodeQueuedPromptsView.cs" })
        {
            var source = Src(relative);
            Check(source.Contains("OpenCodeUserAttachments.FromInbox(item)") && source.Contains("AttachmentCounts = OpenCodeUserAttachments.Counts(files)")
                && source.Contains("OpenCodeUserAttachmentsView"),
                "pending surface uses same owned-chip eligibility and shared thumbnails: " + relative);
        }
        var presenter = Src("Pages/OpenCode/OpenCodeRichTextPresenter.cs");
        Check(presenter.Contains("AttachmentCounts != _lastAttachmentCounts") && presenter.Contains("(source, (style, AttachmentCounts),"),
            "same-text pending attachment changes invalidate the real rich-text render cadence and duplicate-render key");
    }
}
