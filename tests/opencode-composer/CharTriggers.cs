using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;

internal static partial class Program
{
    private static async Task CharTriggers()
    {
        var before = checks;
        var entries = OpenCodeComposerPlusMenu.Entries;
        Check(entries.Select(e => e.Kind).SequenceEqual(new[] { OpenCodeComposerPlusMenuKind.Attach,
            OpenCodeComposerPlusMenuKind.Separator, OpenCodeComposerPlusMenuKind.Command,
            OpenCodeComposerPlusMenuKind.Context, OpenCodeComposerPlusMenuKind.Terminal }), "plus native menu order");
        Check(entries.Where(e => e.Kind != OpenCodeComposerPlusMenuKind.Separator).Select(e => e.Text)
            .SequenceEqual(new[] { "添加附件", "命令（/）", "上下文（@）", "终端（!）" }), "plus menu labels");
        Check(OpenCodeComposerPlusMenu.TerminalUnavailableTooltip == "终端模式尚未接入"
            && !OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Terminal, true, false, false, true), "terminal disabled with honest tooltip");
        Check(OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Attach, true, false, false, false)
            && !OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Attach, true, true, false, true), "attachments retain independent availability and cap");
        foreach (var text in new[] { "@", "@src", "hello @src/a", "hello\n@readme", "\t@readme" })
            Check(OpenCodeComposerCharTriggers.FindMention(text, text.Length).Present, "legal mention: " + text);
        foreach (var text in new[] { "", " ", "@x ", "body@src", "me@example.com", "https://host/@src", "C:/src/@x", "/tmp/@x", "(@src", "@a@b", "@https://host" })
            Check(!OpenCodeComposerCharTriggers.FindMention(text, text.Length).Present, "no mention: " + text);
        Check(!OpenCodeComposerCharTriggers.FindMention("@readme", 3).Present, "no mention mid-token caret");
        foreach (var text in new[] { "/", " /review", "\t/re" })
            Check(OpenCodeComposerCharTriggers.Classify(text, text.Length) == OpenCodeComposerCharTriggerKind.Command, "existing legal command entry " + text);
        foreach (var text in new[] { "body/re", "https://host/re", "/usr/bin", "//host", "C:/path" })
            Check(OpenCodeComposerCharTriggers.Classify(text, text.Length) == OpenCodeComposerCharTriggerKind.None, "no slash trigger in word/url/path " + text);
        var insert = OpenCodeComposerCharTriggers.ContextInsertion("wordmore", 4);
        Check(insert == (" @ ", 2), "explicit context inserts legal token without destroying neighbouring text");
        Check(OpenCodeComposerCharTriggers.MentionInsertion(new("src/a.cs", false)) == "@src/a.cs "
            && OpenCodeComposerCharTriggers.MentionInsertion(new("my folder/a.cs", false)) == "@\"my folder/a.cs\" ", "path text insertion and whitespace quoting");

        using var f = new Fixture();
        string? uri = null;
        f.Handler.Send = (r, _) =>
        {
            uri = Uri.UnescapeDataString(r.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("{\"location\":{\"directory\":\"/repo\"},\"data\":[{\"path\":\"src/a.cs\",\"type\":\"file\"},{\"path\":\"src\",\"type\":\"directory\"}]}"));
        };
        var found = await OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", "a&b", CancellationToken.None);
        Check(uri!.Contains("/api/fs/find?") && uri.Contains("location[directory]=/repo") && uri.Contains("query=a&b")
            && uri.Contains("limit=30"), "official fs.find endpoint, deepObject location, query and string limit");
        Check(found.Count == 2 && found[0].Path == "src/a.cs" && found[1].IsDirectory, "real indexed rows preserve server rank");
        await OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", "", CancellationToken.None);
        Check(uri!.Contains("/api/fs/list?"), "bare @ uses real root listing");

        var queries = new OpenCodeComposerMentionQueries();
        var oldStarted = new TaskCompletionSource<CancellationToken>();
        var releaseOld = new TaskCompletionSource();
        var old = queries.QueryAsync(async (_, ct) => { oldStarted.SetResult(ct); await releaseOld.Task; return found; }, "old");
        var oldToken = await oldStarted.Task;
        var latest = await queries.QueryAsync((_, _) => Task.FromResult(found), "new");
        Check(oldToken.IsCancellationRequested && latest is not null && queries.IsCurrent(latest), "superseded backend request canceled");
        releaseOld.SetResult();
        Check(await old is null, "stale result rejected even if backend ignores cancellation");
        var debouncedCalls = 0;
        var debounced = queries.QueryAsync((_, _) => { debouncedCalls++; return Task.FromResult(found); }, "typing");
        queries.Cancel();
        Check(await debounced is null && debouncedCalls == 0, "debounce canceled before network request");
        Check(latest is not null && !queries.IsCurrent(latest), "close invalidates result queued for dispatcher");
        f.Handler.Send = (_, _) => Task.FromResult(Json("{\"location\":{\"directory\":\"/repo\"},\"data\":[]}"));
        var empty = await queries.QueryAsync((q, ct) => OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", q, ct), "nothing");
        Check(empty is { Rows.Count: 0, Error: null }, "honest empty backend result");
        f.Handler.Send = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.InternalServerError));
        var error = await queries.QueryAsync((q, ct) => OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", q, ct), "error");
        Check(error is { Rows.Count: 0, Error: not null } && error.Error.StartsWith("文件索引读取失败："), "honest backend error, no fabricated rows");
        f.Handler.Send = (_, _) => Task.FromResult(Json("{\"data\":[]}"));
        var malformed = await queries.QueryAsync((q, ct) => OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", q, ct), "malformed");
        Check(malformed is { Rows.Count: 0, Error: not null }, "malformed endpoint shape becomes error");
        var repo = SplashRepo();
        var view = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs"));
        var page = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.Composer.cs"));
        Check(view.Contains("foreach (var entry in OpenCodeComposerPlusMenu.Entries)") && view.Contains("new MenuFlyoutSeparator()"), "view uses tested menu definition and native divider");
        Check(page.Contains("_composerView.MentionSearchProvider = ComposerMentionSearch;") && page.Contains("OpenCodeComposerMentions.SearchAsync(binding.Client, slot.Directory, query, ct)"), "real provider wired to active binding directory");
        Check(view.Contains("_attachItem.Click += (_, _) => RequestPick();") && page.Contains("_composerView.PickRequested += (_, e) => _ = PickComposerFilesAsync(e.Token);"), "existing attachment picker handoff intact");
        Check(view.Contains("没有匹配的文件或文件夹") && view.Contains("result.Error") && view.Contains("_mentionQueries.IsCurrent(result)"), "view exposes empty/error and rejects queued stale results");
        Check(view.Contains("trigger != _mentionTrigger") && view.Contains("_mentionArmed = false;"), "acceptance requires exact query and application writes disarm triggers");
        f.Handler.Send = (_, _) => Task.FromResult(Json(Receipt));
        var referenceText = "Inspect " + OpenCodeComposerCharTriggers.MentionInsertion(new("src/a.cs", false));
        f.Text(referenceText);
        Check(OpenCodeComposerInputPolicy.Route(false, 0, f.Core.Snapshot.Text) == OpenCodeComposerSubmitRoute.LegacyText, "path text stays on existing ordinary prompt route");
        var submitted = await f.Client.SendPromptAsync("ses_test", f.Core.Snapshot.Text, CancellationToken.None);
        Check(submitted.Id == "msg_real"
            && f.Handler.LastBody!.Contains("@src/a.cs") && !f.Handler.LastBody.Contains("\"files\""), "fallback reference sends path text, never fabricates a file upload");
        Console.WriteLine($"PASS: B7 {checks - before} menu/trigger/index/cancellation/empty/error/wiring assertions; synthetic HTTP, no native UI execution.");
    }
}
