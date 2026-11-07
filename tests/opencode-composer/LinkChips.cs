using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static string Label(string url) => OpenCodeLinkChipParser.Parse(url)?.Label ?? "<null>";

    private static async Task LinkChips()
    {
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
            var mentionAt = text.IndexOf('@');
            Check(f.Core.TryAddContext(f.Core.Snapshot.Token, "explorer", false, mentionAt), "mention after chip");
            Check(f.Core.Snapshot.SendText == "fix " + url + " @explorer ", "send text restores the raw URL");
            var basis = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
            Check(basis.SendText.Contains(url) && basis.SendContexts[0].Start == ("fix " + url + " ").Length && basis.SendContexts[0].Matches(basis.SendText), "mention span re-based onto sent text");
            await f.Client.SendPromptGuardedAsync("ses_test", basis.SendText, null, send => { send(); return true; }, CancellationToken.None, contexts: basis.SendContexts);
            Check(f.Handler.LastBody!.Contains(url) && !f.Handler.LastBody.Contains("\\u2003"), "wire carries the full URL, never the label");
            Check(OpenCodeComposerChips.ExpandRange(text, 0, 6, f.Core.Snapshot.Links) == "fix " + url, "copy of a partial chip selection yields the whole URL");
            Check(OpenCodeComposerChips.ExpandRange(text, mentionAt, 9, f.Core.Snapshot.Links) == "@explorer", "copy without chips unchanged");
            var deleted = text.Remove(span.Start, span.Display.Length);
            f.Text(deleted);
            Check(f.Core.Snapshot.Links.Count == 0 && f.Core.Snapshot.SendText == deleted, "whole-chip delete drops the link");
            f.Text(text);
            Check(f.Core.Snapshot.Links.Count == 1 && f.Core.Snapshot.SendText.Contains(url), "Ctrl+Z restores the chip");
            f.Text(text.Insert(span.Start + 5, "x"));
            Check(f.Core.Snapshot.Links.Count == 0 && !f.Core.Snapshot.SendText.Contains(url), "edit inside chip un-chips it");
            f.Text(text);
            Check(f.Core.Snapshot.Links.Count == 1, "undo again re-chips");
            f.Text("fix " + url + " @explorer ");
            Check(f.Core.Snapshot.Links.Count == 0 && f.Core.Snapshot.Text.Contains(url), "convert back to text = raw URL in editor");
            f.Text(text);
            f.Text("Please " + text);
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

        // ---- [Image N]: numbering, linkage, no renumbering
        using (var f = new Fixture())
        {
            async Task<Guid> Paste(string after)
            {
                var before = f.Core.Snapshot.Attachments.Select(a => a.Id).ToHashSet();
                await f.File("p.png", 4);
                var id = f.Core.Snapshot.Attachments.Select(a => a.Id).First(x => !before.Contains(x));
                var n = f.Core.NextImageNumber;
                var start = f.Core.Snapshot.Text.Length;
                f.Text(f.Core.Snapshot.Text + OpenCodeComposerChips.ImageToken(n) + after);
                Check(f.Core.TryAddImage(f.Core.Snapshot.Token, start, n, id), "image chip bound " + n);
                return id;
            }
            f.Text("look ");
            var one = await Paste(" ");
            await Paste(" ");
            Check(f.Core.Snapshot.Text == "look [Image 1] [Image 2] " && f.Core.Snapshot.Images.Count == 2, "[Image N] counts from 1 in TUI text form");
            var full = f.Core.Snapshot.Text;
            f.Text(full.Replace("[Image 1]", "", StringComparison.Ordinal));
            Check(f.Core.Snapshot.Images.Count == 1, "other image survives: " + string.Join(",", f.Core.Snapshot.Images.Select(i => i.Start + ":" + i.Number)) + " text=" + f.Core.Snapshot.Text);
            Check(f.Core.Snapshot.Attachments.All(a => a.Id != one) && f.Core.Snapshot.Images.Single().Number == 2, "deleting the chip removes the attachment; no renumbering");
            f.Text(full);
            Check(f.Core.Snapshot.Attachments.Any(a => a.Id == one) && f.Core.Snapshot.Images.Count == 2, "Undo restores chip + attachment");
            f.Core.RemoveAttachment(f.Core.Snapshot.Token, one);
            Check(f.Core.Snapshot.Images.Count == 1, "x keeps other: " + f.Core.Snapshot.Text);
            Check(f.Core.Snapshot.Text == "look [Image 2] " && f.Core.Snapshot.Images.Single().Number == 2, "× on the attachment removes [Image 1] text");
            var three = await Paste("");
            Check(f.Core.Snapshot.Text.EndsWith("[Image 3]", StringComparison.Ordinal) && f.Core.Snapshot.Images.Any(i => i.Number == 3 && i.AttachmentId == three), "next number is max+1, never reused");
            Check(f.Core.Snapshot.SendText == f.Core.Snapshot.Text, "[Image N] is sent verbatim (TUI parity)");
            f.Core.DiscardDraft(f.Core.Snapshot.Token);
            f.Text("again ");
            Check(f.Core.NextImageNumber == 1, "a fresh message restarts at 1");
        }

        // ---- production wiring (source checks; WinUI surfaces are not executed here)
        var repo = Environment.GetEnvironmentVariable("FC_SPLASH_REPO")!;
        string Src(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var view = Src("OpenCodeComposerView.cs") + Src("OpenCodeComposerView.LinkChips.cs");
        Check(view.Contains("CopyingToClipboard") && view.Contains("CuttingToClipboard") && view.Contains("OpenCodeComposerChips.ExpandRange"), "copy/cut restore raw URLs");
        Check(view.Contains("TryDeleteChipToken") && view.Contains("转回文字") && view.Contains("VirtualKey.Control"), "atomic delete, convert-back menu, Ctrl+click open");
        var presenter = Src("OpenCodeRichTextPresenter.cs");
        Check(presenter.Contains("OpenCodeLinkChipParser.Find(") && presenter.Contains("inCode") && presenter.Contains("RewriteMarkdownLinks"), "transcript chips skip code and see real markdown targets");
        var chipView = Src("OpenCodeLinkChipView.cs");
        Check(chipView.Contains("Uri.UriSchemeHttps") && chipView.Contains("Launcher.LaunchUriAsync") && chipView.Contains("AutomationProperties.SetName")
            && chipView.Contains("HighContrast"), "click opens http/https only; Narrator name; HC");
        var fav = Src("OpenCodeFaviconCache.cs");
        Check(fav.Contains("ConnectCallback") && fav.Contains("IsPrivateAddress"), "favicon fetch refuses private addresses after DNS");
    }
}
