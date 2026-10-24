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
        Check(OpenCodeComposerPlusMenu.TerminalTooltip.Contains("Shell")
            && OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Terminal, true, false, false, true)
            && !OpenCodeComposerPlusMenu.IsEnabled(OpenCodeComposerPlusMenuKind.Terminal, false, false, false, true), "terminal enabled only for editable composer");
        var shell = new OpenCodeComposerShellState();
        shell.Enter();
        Check(shell.Active && !shell.CanSubmit, "shell empty Enter is inert");
        shell.Text = "  \r\n";
        Check(!shell.CanSubmit, "whitespace shell is inert");
        shell.Text = "printf '你好'";
        Check(shell.CanSubmit, "nonempty shell command eligible");
        shell.Exit();
        Check(!shell.Active && shell.Text == "printf '你好'" && !shell.CanSubmit, "exit keeps shell text separately");
        shell.Enter();
        Check(shell.CanSubmit, "reenter restores shell command");
        shell.Reset();
        Check(!shell.Active && shell.Text == "", "slot reset clears shell without touching a normal draft");
        Check(OpenCodeComposerShellState.IsEntry("", "!", 0, 0, false, false)
            && OpenCodeComposerShellState.IsEntry("normal\r\n你好", "!normal\r\n你好", 0, 0, false, false), "bang at offset zero consumes only bang, preserves normal draft");
        Check(OpenCodeComposerShellState.IsEntry("", "!", 1, 0, false, false)
            && OpenCodeComposerShellState.IsEntry("normal", "!normal", 1, 0, false, false), "native post-insertion caret still enters shell for exact leading bang delta");
        Check(!OpenCodeComposerShellState.IsEntry("x", "x!", 1, 0, false, false)
            && !OpenCodeComposerShellState.IsEntry("", "!", 0, 0, true, false)
            && !OpenCodeComposerShellState.IsEntry("", "!", 0, 0, false, true)
            && !OpenCodeComposerShellState.IsEntry("x", "!", 0, 1, false, false), "no shell for midtext, IME, autocomplete or selection replacement");
        Check(OpenCodeComposerShellState.IsExit(true, false, false, 3, "pwd")
            && OpenCodeComposerShellState.IsExit(false, true, false, 0, "pwd")
            && OpenCodeComposerShellState.IsExit(false, false, true, 0, ""), "official Escape, backspace at zero, empty Ctrl+C exits");
        Check(!OpenCodeComposerShellState.IsExit(false, true, false, 1, "pwd")
            && !OpenCodeComposerShellState.IsExit(false, false, true, 0, "pwd"), "backspace elsewhere and nonempty Ctrl+C do not exit");
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
        Check(OpenCodeComposerCharTriggers.MentionInsertion(new("explorer", false)) == "@explorer "
            && OpenCodeComposerCharTriggers.MentionInsertion(new("review", true)) == "@review ", "official agent and skill insertion uses registered ID");

        using var f = new Fixture();
        f.Handler.Send = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.NoContent));
        await f.Client.ExecuteSessionShellAsync("ses_test", "printf '你好'", CancellationToken.None);
        Check(f.Handler.LastBody is not null && System.Text.Json.JsonDocument.Parse(f.Handler.LastBody).RootElement.EnumerateObject().Count() == 1
            && System.Text.Json.JsonDocument.Parse(f.Handler.LastBody).RootElement.GetProperty("command").GetString() == "printf '你好'", "shell sends only official command body");
        string? shellUri = null;
        f.Handler.Send = (r, _) => { shellUri = r.RequestUri!.AbsolutePath; return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.NoContent)); };
        await f.Client.ExecuteSessionShellAsync("ses/escaped", "pwd");
        Check(shellUri == "/api/session/ses%2Fescaped/shell", "shell uses escaped official session endpoint, never prompt or global shell");
        f.Handler.Send = (_, _) => Task.FromResult(Json("{}"));
        try { await f.Client.ExecuteSessionShellAsync("ses_test", "pwd"); Check(false, "shell rejects non-204"); }
        catch (AIFrontend.Services.OpenCode.OpenCodeProtocolException) { Check(true, "shell requires actual 204"); }
        f.Handler.Send = (_, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.NotFound));
        try { await f.Client.ExecuteSessionShellAsync("ses_test", "pwd"); Check(false, "shell rejects 404"); }
        catch (AIFrontend.Services.OpenCode.OpenCodeApiException e) { Check(e.StatusCode == 404, "shell reports real HTTP error"); }
        var uris = new System.Collections.Generic.List<string>();
        f.Handler.Send = (r, _) =>
        {
            uris.Add(Uri.UnescapeDataString(r.RequestUri!.AbsoluteUri));
            return Task.FromResult(Json(r.RequestUri.AbsolutePath.EndsWith("/agent")
                ? """{"location":{"directory":"/repo"},"data":[{"id":"explorer","name":"Explore","request":{},"mode":"subagent","hidden":false,"permissions":[]},{"id":"hidden","name":"Hidden","request":{},"mode":"subagent","hidden":true,"permissions":[]},{"id":"primary","name":"Primary","request":{},"mode":"primary","hidden":false,"permissions":[]},{"id":"all","name":"All","request":{},"mode":"all","hidden":false,"permissions":[]}]}"""
                : """{"location":{"directory":"/repo"},"data":[{"id":"review","name":"Review","path":"/repo/SKILL.md","content":"private skill content","description":"Review changes"}]}"""));
        };
        var found = await OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", "", CancellationToken.None);
        Check(uris.Count == 2 && uris.Any(u => u.Contains("/api/agent?")) && uris.Any(u => u.Contains("/api/skill?"))
            && uris.All(u => u.Contains("location[directory]=/repo") && !u.Contains("/fs/")), "official agent and skill endpoints with exact deepObject directory; no filesystem");
        Check(found.Count == 3 && found[0].Id == "explorer" && found[1].Id == "all" && found[2].IsSkill, "visible subagent/all agents plus skills, never hidden or primary");
        var filtered = await OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", "REVIEW", CancellationToken.None);
        Check(filtered.Count == 1 && filtered[0].Id == "review" && filtered[0].IsSkill, "case-insensitive context filtering");

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
        Check(error is { Rows.Count: 0, Error: not null } && error.Error.Contains("不可用"), "honest backend error, no fabricated rows or exception payload disclosure");
        f.Handler.Send = (_, _) => Task.FromResult(Json("{\"data\":[]}"));
        var malformed = await queries.QueryAsync((q, ct) => OpenCodeComposerMentions.SearchAsync(f.Client, "/repo", q, ct), "malformed");
        Check(malformed is { Rows.Count: 0, Error: not null }, "malformed endpoint shape becomes error");
        var repo = SplashRepo();
        var view = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs"));
        var page = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.Composer.cs"));
        var shellPage = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.ShellComposer.cs"));
        var blankPage = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.NewSessionTab.cs"));
        Check(page.IndexOf("TrySubmitShell(delivery, ref shellHandled)", StringComparison.Ordinal) < page.IndexOf("if (TryHandleSideQuestionSubmission(snapshot))", StringComparison.Ordinal)
            && shellPage.Contains("delivery == OpenCodeDeliveryModes.Queue") && !shellPage.Contains("SendPromptAsync("), "shell intercepted before all normal submission and queue routes");
        Check(shellPage.Contains("e.Cancel = true;") && !shellPage.Contains("\n        Composer.Text =")
            && shellPage.Contains("ComposerNormalSurface.Opacity = 0;") && shellPage.Contains("ComposerNormalSurface.IsEnabled = false;")
            && !shellPage.Contains("ComposerInputLayout.Visibility ="), "bang canceled before ledger; terminal hides and disables normal controls while retaining their measured layout and draft");
        Check(shellPage.Contains("CreateSessionAndSubmitAsync(_composerController!.Snapshot.Token, _shellComposer.Text)")
            && blankPage.Contains("if (shellCommand is not null) OnCreatedShellSubmit(shellCommand);"), "blank shell uses existing configured creation and exact handoff, never normal prompt");
        Check(view.Contains("foreach (var entry in OpenCodeComposerPlusMenu.Entries)") && view.Contains("new MenuFlyoutSeparator()"), "view uses tested menu definition and native divider");
        Check(!view.Contains("_discardItem") && !view.Contains("_discardSeparator") && view.Contains("_statusDiscardButton.Flyout = _discardFlyout;"), "discard confirmation belongs to status recovery, not the + menu");
        Check(view.Contains("offerDiscard |= (enhanced || s.IsBoundDraft) && !inactiveBound && !s.IsBusy;")
            && view.Contains("opening != fresh.Token || fresh.IsBusy") && view.Contains("DiscardRequested?.Invoke(this, new(fresh.Token))"), "active enhanced drafts retain guarded discard via the existing recovery entry");
        Check(page.Contains("_composerView.MentionSearchProvider = ComposerMentionSearch;") && page.Contains("OpenCodeComposerMentions.SearchAsync(binding.Client, slot.Directory, query, ct)"), "real provider wired to active binding directory");
        Check(view.Contains("_attachItem.Click += (_, _) => RequestPick();") && page.Contains("_composerView.PickRequested += (_, e) => _ = PickComposerFilesAsync(e.Token);"), "existing attachment picker handoff intact");
        Check(view.Contains("没有匹配的 agent 或 skill") && view.Contains("result.Error") && view.Contains("_mentionQueries.IsCurrent(result)"), "view exposes empty/error and rejects queued stale results");
        Check(view.Contains("trigger != _mentionTrigger") && view.Contains("_mentionArmed = false;"), "acceptance requires exact query and application writes disarm triggers");
        f.Handler.Send = (_, _) => Task.FromResult(Json(Receipt));
        var referenceText = "Inspect @explorer @review ";
        f.Text(referenceText);
        Check(f.Core.TryAddContext(f.Core.Snapshot.Token, "explorer", false, 8)
            && f.Core.TryAddContext(f.Core.Snapshot.Token, "review", true, 18), "chosen contexts retained under exact draft token");
        var basis = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
        Check(basis.Contexts.Count == 2 && f.Core.Snapshot.IsBoundDraft, "mention draft binds to source and captures both typed identities");
        var submitted = await f.Client.SendPromptGuardedAsync("ses_test", basis.Text, null, send => { send(); return true; }, CancellationToken.None, contexts: basis.Contexts);
        using var body = System.Text.Json.JsonDocument.Parse(f.Handler.LastBody!);
        Check(submitted.Id == "msg_real"
            && body.RootElement.GetProperty("agents")[0].GetProperty("name").GetString() == "explorer"
            && body.RootElement.GetProperty("skills")[0].GetProperty("id").GetString() == "review"
            && body.RootElement.GetProperty("agents")[0].GetProperty("mention").GetProperty("start").GetInt32() == 8
            && !body.RootElement.TryGetProperty("files", out _), "prompt sends official agent name + skill id mention attachments, no file upload or eager skill activation");
        f.Text("Prefix " + referenceText);
        Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Contexts[0].Start == 15, "edit before context moves mention span");
        f.Text("Prefix Inspect @changed @review ");
        Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Contexts is { Count: 1 } remaining && remaining[0].IsSkill, "editing a mention removes its typed part, preserves other contexts");
        var key = Key(f, Guid.NewGuid(), "/repo");
        var retained = Capture(f);
        Activate(f, key, f.Core.CreateEmptySlotData());
        Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Contexts.Count == 0, "other slot never inherits context parts");
        Activate(f, key, retained);
        Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Contexts is { Count: 1 } restored && restored[0].Id == "review", "slot restoration preserves context identity and span");
        f.Core.DiscardDraft(f.Core.Snapshot.Token);
        Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Contexts.Count == 0, "discard clears contexts");
        Console.WriteLine($"PASS: B7 {checks - before} menu/trigger/context/cancellation/empty/error/wiring assertions; synthetic HTTP, no native UI execution.");
    }
}
