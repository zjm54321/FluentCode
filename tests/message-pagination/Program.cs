using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private const string Session = "ses_test";

    private static int _assertions;

    private static async Task HomeRootActions()
    {
        foreach (var action in new[] { OpenCodeHomeSessionAction.Delete, OpenCodeHomeSessionAction.Rename })
        {
            var backend = new HomeBackend(); using var client = backend.CreateClient(); var page = new OpenCodePage();
            page.TestActivate("ses_unrelated", client, backend.CreateClient);
            page.TestAcceptHomeRoot("ses_root", "/remote/worktree", "actual root title");
            if (action == OpenCodeHomeSessionAction.Delete) page.TestHomeSelected("ses_root");
            backend.BeforeMutation = () => Check(page.TestHomeRootTitle("ses_root") == "actual root title" && page.MetadataApplications == 0,
                "no optimistic row/title update before real management acceptance");
            page.HomeConfirmation = () =>
            {
                Check(page.HomeDialogId == "ses_root" && page.HomeDialogTitle == "actual root title" && backend.Requests.Count == 0,
                    "confirmation uses accepted actual row metadata, not the stale event DTO or hidden/current selection; opening does no network");
                page.TestHomeCosmeticRefresh();
                return Task.FromResult<string?>(action == OpenCodeHomeSessionAction.Delete ? "ses_root" : "confirmed new title");
            };
            await page.TestRunHomeAction(7, "/remote/worktree", "ses_root", action);
            Check(backend.Mutations == 1 && backend.ListReads == 1 && page.MetadataApplications == 1 && page.HomeSwitchRequests.Count == 0,
                "actual production consumer reaches one real target mutation and authoritative metadata refresh, never navigation");
            Check(action == OpenCodeHomeSessionAction.Delete
                ? page.TestHomeRootTitle("ses_root") is null && page.DeletedBindings == 1 && page.Bindings == 1 && page.RemovedTabs == 1
                : page.TestHomeRootTitle("ses_root") == "confirmed new title" && page.DeletedBindings == 0,
                "only accepted delete invalidates its normal binding; only refreshed server metadata publishes rename");
            Check(page.UnrelatedDraft == "keep this draft" && !page.TestHomeBusy && !await page.TestHomeComposerProtected("ses_root"),
                "unrelated draft retained and known exact management slot released");
        }
        foreach (var denial in new[] { "cancel", "stale revision", "foreign directory", "changed title", "changed source", "archive" })
        {
            var backend = new HomeBackend(); using var client = backend.CreateClient(); var page = new OpenCodePage();
            page.TestActivate("ses_other", client, backend.CreateClient); page.TestAcceptHomeRoot("ses_root", "/remote/worktree", "actual root title");
            page.HomeConfirmation = () =>
            {
                if (denial == "changed title") page.TestAcceptHomeRoot("ses_root", "/remote/worktree", "foreign rename");
                if (denial == "changed source") page.TestHomeResetActionScope();
                return Task.FromResult<string?>(denial == "cancel" ? null : "new title");
            };
            await page.TestRunHomeAction(denial == "stale revision" ? 6 : 7, denial == "foreign directory" ? "/wrong" : "/remote/worktree", "ses_root",
                denial == "archive" ? OpenCodeHomeSessionAction.Archive : OpenCodeHomeSessionAction.Rename);
            Check(backend.Requests.Count == 0 && page.HomeSwitchRequests.Count == 0 && page.MetadataApplications == 0,
                "actual Home action denies without HTTP/navigation/optimism: " + denial);
        }
        {
            var backend = new HomeBackend { Unknown = true }; using var client = backend.CreateClient(); var page = new OpenCodePage();
            page.TestActivate("ses_other", client, backend.CreateClient); page.TestAcceptHomeRoot("ses_root", "/remote/worktree", "actual root title");
            page.HomeConfirmation = () => Task.FromResult<string?>("new title");
            await page.TestRunHomeAction(7, "/remote/worktree", "ses_root", OpenCodeHomeSessionAction.Rename);
            Check(backend.Mutations == 1 && backend.ListReads == 1 && await page.TestHomeComposerProtected("ses_root")
                && page.TestHomeStatus?.Contains("结果未知") == true && page.TestHomeRootTitle("ses_root") == "actual root title",
                "ambiguous actual consumer retains protection/explanation and does metadata-only refresh, no optimistic rename/replay");
        }
        {
            var backend = new HomeBackend(); using var client = backend.CreateClient(); var page = new OpenCodePage();
            page.TestActivate("ses_other", client, backend.CreateClient); page.TestAcceptHomeRoot("ses_root", "/remote/worktree", "actual root title");
            var first = new TaskCompletionSource<string?>(); var second = new TaskCompletionSource<string?>();
            page.HomeConfirmation = () => first.Task;
            var old = page.TestRunHomeAction(7, "/remote/worktree", "ses_root", OpenCodeHomeSessionAction.Delete);
            page.TestHomeResetActionScope(); page.TestAcceptHomeRoot("ses_root", "/remote/worktree", "actual root title");
            page.HomeConfirmation = () => second.Task;
            var fresh = page.TestRunHomeAction(8, "/remote/worktree", "ses_root", OpenCodeHomeSessionAction.Rename);
            first.SetResult("ses_root"); await old;
            Check(page.TestHomeBusy && backend.Requests.Count == 0, "foreign finally cannot clear a newer runtime action owner");
            second.SetResult(null); await fresh;
        }
    }

    private sealed class HomeBackend
    {
        internal readonly List<string> Requests = new();
        internal int Mutations, ListReads;
        internal bool Unknown;
        internal Action? BeforeMutation;
        private bool _deleted;
        private string _title = "actual root title";
        private string Info => "{\"id\":\"ses_root\",\"projectID\":\"p\",\"title\":" + JsonSerializer.Serialize(_title)
            + ",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/remote/worktree\"}}";
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        internal OpenCodeClient CreateClient() => new(new HttpClient(new Handler(async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(request.Method + " " + path);
            if (request.Method != HttpMethod.Get)
            {
                Mutations++; BeforeMutation?.Invoke();
                Check(path == "/api/session/ses_root", "consumer mutates actual context root, not selected/hidden session");
                if (Unknown) throw new HttpRequestException("offline");
                if (request.Method == HttpMethod.Delete) _deleted = true;
                else { using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); _title = body.RootElement.GetProperty("title").GetString()!; }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (path == "/api/session") { ListReads++; return Json("{\"data\":[" + (_deleted ? "" : Info) + "],\"cursor\":{}}"); }
            if (path == "/api/session/ses_root") return Json("{\"data\":" + Info + "}");
            if (path.EndsWith("/message")) return Json("{\"data\":[{\"id\":\"msg_idle\",\"type\":\"idle\",\"time\":{\"created\":3},\"outcome\":\"succeeded\"}],\"cursor\":{}}");
            if (path == "/api/session/active") return Json("{\"data\":{}}");
            if (path.EndsWith("/permission") || path.EndsWith("/form") || path.EndsWith("/inbox")) return Json("{\"data\":[]}");
            throw new InvalidOperationException("Unexpected Home control route " + path);
        })), new() { BaseUrl = "https://home-actions.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
    }

    private static async Task HomeUnchangedSelectionClick()
    {
        var backend = new Backend(); using var client = backend.CreateClient(); var page = new OpenCodePage();
        page.TestActivate(Session, client);
        var hiddenAlreadyTarget = page.TestPrepareHomeClick("ses_target", "/remote/worktree");
        var rendered = new OpenCodeHomeSessionRow(new(new("runtime-a", "/remote/worktree"),
            new() { Id = "ses_target", Location = new() { Directory = "/remote/worktree" } }), "SYNTHETIC", "SYNTHETIC",
            OpenCodeHomeTone.Unknown, OpenCodeHomeDateGroup.Today, null);
        var visualContent = new object(); // Native own-container ItemClick's Content, not a ListViewItem/data row.
        Check(ReferenceEquals(OpenCodeHomeClick.Resolve(visualContent, null, rendered), rendered), "actual Home click producer resolves its content's bound row when native container cast/mapping are absent");
        Check(OpenCodeHomeClick.Resolve(visualContent, null, null) is null, "producer never invents session metadata from untagged visual content");
        Check(page.TestHiddenSelectionIs(hiddenAlreadyTarget) && page.HomeSwitchRequests.Count == 0, "regression setup: hidden native item already selected while page has no selected session");
        var copies = Windows.ApplicationModel.DataTransfer.Clipboard.Writes;
        page.TestHomeAction(7, "/remote/worktree", "ses_target", OpenCodeHomeSessionAction.CopyId);
        Check(Windows.ApplicationModel.DataTransfer.Clipboard.Text == "ses_target" && Windows.ApplicationModel.DataTransfer.Clipboard.Writes == copies + 1
            && page.HomeSwitchRequests.Count == 0, "actual Home copy route copies the real target ID without navigation or native selection changes; clipboard boundary doubled");
        page.TestHomeAction(6, "/remote/worktree", "ses_target", OpenCodeHomeSessionAction.CopyId);
        page.TestHomeAction(7, "/remote/worktree", "ses_target", OpenCodeHomeSessionAction.Delete);
        Check(Windows.ApplicationModel.DataTransfer.Clipboard.Writes == copies + 1 && page.HomeSwitchRequests.Count == 0, "stale/unsupported context action cannot copy, navigate, or pretend a server mutation");
        await page.TestOpenHome(7, "/remote/worktree", "ses_target");
        Check(page.HomeSwitchRequests.SequenceEqual(new[] { "ses_target" }) && page.HomeMirrors == 1, "actual production Home router invokes existing switch exactly once and mirrors only the accepted binding");
        Check(page.TestHiddenSelectionIs(hiddenAlreadyTarget), "navigation does not depend on changing the hidden native item");
        await page.TestOpenHome(6, "/remote/worktree", "ses_target");
        Check(page.HomeSwitchRequests.Count == 1, "stale Home event cannot invoke switch");
        page.TestHomeUnavailable();
        await page.TestOpenHome(7, "/remote/worktree", "ses_target");
        Check(page.HomeSwitchRequests.Count == 1 && page.TestHomeUnavailableExplained, "genuinely unavailable source explains the blocked click instead of pretending to navigate");
    }

    private static Task BlankToSessionColumn()
    {
        var blank = OpenCodePage.ConversationColumn(1200, 1184, 1040, true);
        var session = OpenCodePage.ConversationColumn(1200, 1184, 920, false);
        Check(blank.Width == 1040 && blank.RightGutter == 0, "blank uses its own centered main-content width");
        Check(session.Width == 920 && session.RightGutter == 16, "NEW to SESSION explicitly restores reading max width and actual viewport gutter");
        Check((1200 - session.RightGutter) / 2 == 1184d / 2, "composer column center matches reading viewport, not screen/sidebar center");
        return Task.CompletedTask;
    }

    private static Task HomeMetadataProjection()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var a = new OpenCodeHomeProjectKey("runtime-a", "/work/a");
        var b = new OpenCodeHomeProjectKey("runtime-a", "/work/b");
        OpenCodeSessionInfo Info(string id, string directory, string? outcome, DateTimeOffset time) => new()
            { Id = id, Title = "Actual " + id, ProjectId = "same-git-id", Outcome = outcome,
              Location = new() { Directory = directory }, Time = new() { Created = time.ToUnixTimeMilliseconds(), Updated = time.ToUnixTimeMilliseconds() } };
        var success = new OpenCodeHomeSession(a, Info("ses_a", a.Directory, "succeeded", now));
        var failed = new OpenCodeHomeSession(b, Info("ses_b", b.Directory, "failed", now.AddDays(-1)));
        var unknown = new OpenCodeHomeSession(a, Info("ses_c", a.Directory, null, now.AddDays(-3)), new(Working: false));
        var snapshot = new OpenCodeHomeSnapshot(7, new[] { new OpenCodeHomeProject(a, "A", "same-git-id"), new(b, "B", "same-git-id") }, new[] { unknown, failed, success });
        var rows = OpenCodeHomeProjection.Rows(snapshot, null, null, now, TimeZoneInfo.Utc);
        Check(rows.Count == 3 && rows.Select(row => row.Session.Project.Directory).Distinct().Count() == 2, "equal Git ID never merges exact worktree directories");
        Check(rows[0].Tone == OpenCodeHomeTone.Succeeded && rows[1].Tone == OpenCodeHomeTone.Failed && rows[2].Tone == OpenCodeHomeTone.Unknown, "actual outcomes survive; inactive does not imply success");
        Check(rows[0].DateGroup == OpenCodeHomeDateGroup.Today && rows[1].DateGroup == OpenCodeHomeDateGroup.Yesterday && rows[2].DateGroup == OpenCodeHomeDateGroup.Earlier, "today/yesterday/earlier use actual timestamps in supplied local zone");
        Check(OpenCodeHomeProjection.Rows(snapshot, b, "actual SES_B", now, TimeZoneInfo.Utc).Single().Session.Info.Id == "ses_b", "search/project filter uses actual loaded titles and exact directory");
        Check(OpenCodeHomeProjection.DateGroup(null, now, TimeZoneInfo.Utc) == OpenCodeHomeDateGroup.Unknown
            && OpenCodeHomeProjection.DateGroup(now.AddDays(1), now, TimeZoneInfo.Utc) == OpenCodeHomeDateGroup.Future, "missing/future dates stay honest");
        Check(OpenCodePage.RemoteChildDirectory("/remote/base", "../sibling") == "/remote/base/../sibling", "remote child path remains relative to response location, not local Windows or input browse path");
        var backend = new Backend(); using var client = backend.CreateClient(); var page = new OpenCodePage(); page.TestActivate(Session, client); page.TestHomeDirectory(a.Directory);
        Check(page.TestHomeEvent(7, a) && !page.TestHomeEvent(6, a), "actual adapter event check rejects stale projection revision");
        page.TestReplaceHomeSource();
        Check(!page.TestHomeEvent(8, a) && !page.TestHomeEvent(7, a), "actual adapter rejects old runtime source even with fresh numeric revision");
        return Task.CompletedTask;
    }

    private static async Task<int> Main()
    {
        try
        {
            foreach (var (name, run) in new (string, Func<Task>)[]
            {
                ("real Switch/Navigate immediate authoritative loading and exact empty/error state", BindingImmediateAndEmpty),
                ("REST/SSE/cache/reactivate share actual viewport proof gate; question adapter survives Working override", ViewportIntegration),
                ("binding refresh owners isolate late reads, auxiliary waits and finally queues", BindingLateOwners),
                ("accepted two-buffer windows reused with bounded page-owned slots", BindingWindowReuse),
                ("failure/cancellation, rapid same-ID return and transferred continuation buffers", BindingFailureAndRapidReturn),
                ("source/revision/directory identity and unknown-directory resolution", BindingExactIdentity),
                ("source, revision and page-generation independently isolate retained windows", BindingIndependentGenerations),
                ("rollback/workflow/revocation/unload evict visual windows and refresh owners", BindingInvalidation),
                ("rolled-off window preserved and updated in place", RolledOffWindowPreservedAndUpdated),
                ("refresh/continuation completion orders consistent", RefreshOlderOrdersConsistent),
                ("exhausted/stopped chains not revived by polling", StopStatesNotRevivedByPolling),
                ("external tail revert marks real context estimate stale; renderer unchanged", TailRevertContextEstimate),
                ("ordered forward windows and older pagination remain complete", ForwardContinuityControls),
                ("backward/deleted/reordered/disjoint windows remain uncertain", UncertainWindowControls),
                ("invalid/ambiguous window ids cannot certify completeness", InvalidWindowIds),
                ("nonempty-to-empty uncertainty persists until hard reset", EmptyTransitionAndReset),
                ("same-session rollback epoch rejects latest/older and late finally", AcceptedHistoryRaces),
                ("whole send deferral and authoritative effects refresh", SendHistoryWorkflow),
                ("scroll-to-top auto-load is upward-only, single-flight and gated", ScrollTopAutoLoad),
                ("held SSE readiness does not block REST paint; bounded first-ready/reconnect repair", FirstPaintAndRepair),
                ("inflight deltas beat snapshot; stale readiness ignored; workflow reconciliation deferred", SnapshotAndReadinessFences),
                ("authoritative SSE/active REST publish composer activity without stale overwrite", ActivityPublication),
                ("Home metadata-only source/directory/date/outcome projection", HomeMetadataProjection),
                ("Home click does not depend on hidden native selection changing", HomeUnchangedSelectionClick),
                ("Home actual root action consumer, confirmation/current/source guards and metadata-only refresh", HomeRootActions),
                ("blank to session restores one finite centered content column", BlankToSessionColumn),
            })
            {
                var before = _assertions;
                await run().WaitAsync(TimeSpan.FromSeconds(30));
                Console.WriteLine($"PASS {name}: {_assertions - before} assertions");
            }

            Console.WriteLine($"PASS total {_assertions}; fake HTTP/WinUI only; production pagination + extracted latest refresh/accepted reset/ACK invalidation + real ContextEstimate/transport.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static OpenCodeMessage Msg(int number, string? text = null) =>
        new() { Id = "msg_" + number, Type = "user", Text = text ?? ("t" + number) };

    private static readonly OpenCodeModelRef Model = new() { Id = "model", ProviderId = "provider" };
    private static readonly OpenCodeModelInfo ModelInfo = new()
    {
        Id = "model", ProviderId = "provider", Limit = new() { Context = 100_000 },
    };

    private static OpenCodeMessage Assistant(string id, double created) => new()
    {
        Id = id, Type = OpenCodeMessageTypes.Assistant, Time = new() { Created = created }, Model = Model,
        Tokens = new() { Input = 100, Output = 10, Reasoning = 2, Cache = new() { Read = 3, Write = 4 } },
    };

    private static void CheckIncompleteEstimate(OpenCodePage page, string scenario)
    {
        var estimate = page.TestContextEstimate(Model, ModelInfo);
        Check(!page.TestHistoryComplete && estimate.Stale
            && estimate.StaleReasons.Contains(OpenCodeContextStaleReason.HistoryIncomplete), scenario);
    }

    private static Task TailRevertContextEstimate()
    {
        var backend = new Backend();
        using var client = backend.CreateClient();
        var page = new OpenCodePage();
        page.TestActivate(Session, client);
        var a = Assistant("A", 100);
        var b = Assistant("B", 200);
        page.TestApplyRefresh(new() { a, b }, null, explicitRefresh: false);
        var before = page.TestContextEstimate(Model, ModelInfo);
        Check(page.TestHistoryComplete && !before.Stale && before.LatestAssistantUsage?.MessageId == "B",
            "complete A/B begins with real, non-stale B usage reference");

        page.TestApplyRefresh(new() { a }, null, explicitRefresh: false);
        CheckIncompleteEstimate(page, "external revert B => refresh A marks actual merged estimate HistoryIncomplete");
        var after = page.TestContextEstimate(Model, ModelInfo);
        Check(after.LatestAssistantUsage?.MessageId == "B" && after.ReferenceTokens == 119,
            "frozen merged history still contains newer B usage but it is explicitly stale");
        Check(page.TestLastApplied().Count == 2 && ReferenceEquals(page.TestLastApplied()[0], b)
            && ReferenceEquals(page.TestLastApplied()[1], a), "Gate1 renderer still receives preserved B then refreshed A");

        page.TestApplyRefresh(new() { a }, null, explicitRefresh: true);
        CheckIncompleteEstimate(page, "explicit cursor restart cannot re-certify retained deleted B");
        page.TestApplyRefresh(new() { a, Assistant("C", 300) }, null, explicitRefresh: false);
        CheckIncompleteEstimate(page, "later overlapping append cannot clear revert uncertainty");
        page.TestHardReset();
        page.TestApplyRefresh(new() { a }, null, explicitRefresh: false);
        var reset = page.TestContextEstimate(Model, ModelInfo);
        Check(page.TestHistoryComplete && !reset.Stale && reset.LatestAssistantUsage?.MessageId == "A"
            && page.TestLastApplied().Count == 1, "hard source/session reset clears uncertainty and retained B together");
        return Task.CompletedTask;
    }

    private static async Task ForwardContinuityControls()
    {
        var backend = new Backend();
        backend.OnRequest(_ => Task.FromResult(Page(1, 2, null)));
        using var client = backend.CreateClient();
        var page = new OpenCodePage();
        page.TestActivate(Session, client);
        // Non-lexical ids and decreasing timestamps: observer must retain service order, not sort.
        var a = Assistant("z", 300);
        var b = Assistant("a", 200);
        var c = Assistant("m", 100);
        var d = Assistant("d", 400);
        page.TestApplyRefresh(new() { a, b }, null, explicitRefresh: false);
        page.TestApplyRefresh(new() { a with { Text = "updated" }, b }, null, explicitRefresh: false);
        Check(page.TestHistoryComplete && !page.TestContextEstimate(Model, ModelInfo).Stale,
            "unchanged ids/content update preserves completeness without timestamp/id sorting");
        page.TestApplyRefresh(new() { a, b, c }, null, explicitRefresh: false);
        Check(page.TestHistoryComplete, "normal append preserves completeness");
        page.TestApplyRefresh(new() { b, c, d }, null, explicitRefresh: false);
        Check(page.TestHistoryComplete && !page.TestContextEstimate(Model, ModelInfo).Stale,
            "contiguous old suffix/new prefix forward slide preserves completeness");
        Check(page.TestLastApplied().Count == 4 && page.TestLastApplied()[0].Id == "z",
            "forward slide still preserves old prefix in renderer");
        page.TestApplyRefresh(new() { c, d }, null, explicitRefresh: false);
        Check(page.TestHistoryComplete, "older-prefix-only rolloff with represented tail remains complete");

        page.TestHardReset();
        page.TestApplyRefresh(Asc(3, 4), "c2", explicitRefresh: false);
        Check(!page.TestHistoryComplete, "initial next cursor means history incomplete");
        await page.TestLoadOlderAsync();
        Check(page.TestHistoryComplete && SameIds(Ids(page), 1, 4), "older pagination to endpoint certifies complete history");
        page.TestApplyRefresh(Asc(4, 5), null, explicitRefresh: false);
        Check(page.TestHistoryComplete && SameIds(Ids(page), 1, 5), "forward slide after older pagination stays complete");
    }

    private static async Task UncertainWindowControls()
    {
        var backend = new Backend();
        using var client = backend.CreateClient();
        foreach (var (name, previous, incoming) in new (string, List<OpenCodeMessage>, List<OpenCodeMessage>)[]
        {
            ("backward window", Asc(2, 4), Asc(1, 3)),
            ("older prefix expansion", Asc(2, 4), Asc(1, 4)),
            ("interior deletion", Asc(1, 4), new() { Msg(1), Msg(3), Msg(4) }),
            ("reordering", Asc(1, 4), new() { Msg(1), Msg(3), Msg(2), Msg(4) }),
            ("old prefix moved behind suffix", Asc(1, 3), new() { Msg(2), Msg(3), Msg(1) }),
            ("insertion inside overlap", Asc(1, 3), new() { Msg(1), Msg(9), Msg(2), Msg(3) }),
            ("disjoint window", Asc(1, 3), Asc(5, 7)),
        })
        {
            var page = new OpenCodePage();
            page.TestActivate(Session, client);
            page.TestApplyRefresh(previous, null, explicitRefresh: false);
            Check(page.TestHistoryComplete, name + " starts complete");
            page.TestApplyRefresh(incoming, null, explicitRefresh: false);
            CheckIncompleteEstimate(page, name + " cannot certify completeness");
            page.TestApplyRefresh(incoming, "restart", explicitRefresh: true);
            await page.TestLoadOlderAsync();
            CheckIncompleteEstimate(page, name + " remains uncertain after explicit restart and endpoint");
        }

        var retained = new OpenCodePage();
        retained.TestActivate(Session, client);
        retained.TestApplyRefresh(Asc(1, 3), null, explicitRefresh: false);
        retained.TestApplyRefresh(Asc(2, 4), null, explicitRefresh: false);
        retained.TestApplyRefresh(new() { Msg(3), Msg(4), Msg(1) }, null, explicitRefresh: false);
        CheckIncompleteEstimate(retained, "older retained id cannot reappear as an apparently new suffix");
    }

    private static Task InvalidWindowIds()
    {
        var backend = new Backend();
        using var client = backend.CreateClient();
        foreach (var invalid in new OpenCodeMessage[]
        {
            Msg(2) with { Id = null }, Msg(2) with { Id = "" }, Msg(2) with { Id = " " },
            Msg(2) with { Id = "msg\t2" }, Msg(2) with { Id = "msg\02" }, null!, Msg(1),
        })
        {
            var page = new OpenCodePage();
            page.TestActivate(Session, client);
            page.TestApplyRefresh(new() { Msg(1), invalid }, null, explicitRefresh: false);
            CheckIncompleteEstimate(page, "invalid/null/duplicate id in initial window cannot certify history");
            page.TestApplyRefresh(Asc(1, 2), null, explicitRefresh: true);
            CheckIncompleteEstimate(page, "valid replacement and cursor restart do not erase invalid-id uncertainty");
            page.TestHardReset();
            page.TestApplyRefresh(Asc(1, 2), null, explicitRefresh: false);
            page.TestApplyRefresh(new() { Msg(1), Msg(2), invalid }, null, explicitRefresh: false);
            CheckIncompleteEstimate(page, "invalid/duplicate id in incoming append cannot certify continuity");
        }

        return Task.CompletedTask;
    }

    private static Task EmptyTransitionAndReset()
    {
        var backend = new Backend();
        using var client = backend.CreateClient();
        var page = new OpenCodePage();
        page.TestActivate(Session, client);
        page.TestApplyRefresh(new(), null, explicitRefresh: false);
        Check(!page.TestHistoryComplete, "initial empty window does not certify history");
        page.TestApplyRefresh(Asc(1, 2), null, explicitRefresh: false);
        Check(page.TestHistoryComplete, "initial empty to first valid window is not a gap");
        page.TestApplyRefresh(Asc(2, 3), null, explicitRefresh: false);
        page.TestApplyRefresh(new(), null, explicitRefresh: false);
        CheckIncompleteEstimate(page, "nonempty-to-empty transition marks history uncertain");
        Check(SameIds(Ids(page), 1, 1), "empty refresh retains only preexisting older block, unchanged renderer semantics");
        page.TestApplyRefresh(Asc(2, 3), null, explicitRefresh: true);
        CheckIncompleteEstimate(page, "empty-to-nonempty with retained old history remains uncertain");
        page.TestHardReset();
        Check(!page.TestHistoryComplete && page.TestNextCursor is null, "hard reset clears chain as before");
        page.TestApplyRefresh(Asc(8, 9), null, explicitRefresh: false);
        Check(page.TestHistoryComplete && SameIds(Ids(page), 8, 9), "hard reset clears gap and old merged contents");

        page.TestApplyRefresh(new(), null, explicitRefresh: false);
        page.TestApplyRefresh(Asc(8, 9), null, explicitRefresh: false);
        CheckIncompleteEstimate(page, "nonempty-to-empty remains sticky even with no older block");
        return Task.CompletedTask;
    }

    private static List<OpenCodeMessage> Asc(int from, int to)
    {
        var list = new List<OpenCodeMessage>();
        for (var i = from; i <= to; i++)
        {
            list.Add(Msg(i));
        }

        return list;
    }

    private static List<OpenCodeMessage> Desc(int from, int to)
    {
        var list = new List<OpenCodeMessage>();
        for (var i = to; i >= from; i--)
        {
            list.Add(Msg(i));
        }

        return list;
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Page(int from, int to, string? next) =>
        Json(new { data = Desc(from, to), cursor = new { previous = (string?)null, next } });

    private static HttpResponseMessage Error(HttpStatusCode status) => new(status);

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index > 0 && string.Equals(pair[..index], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(index + 1)..]);
            }
        }

        return null;
    }

    private static List<string> Ids(OpenCodePage page)
    {
        var ids = new List<string>();
        foreach (var message in page.TestLastApplied())
        {
            ids.Add(message.Id ?? "?");
        }

        return ids;
    }

    private static bool SameIds(List<string> actual, int from, int to)
    {
        if (actual.Count != to - from + 1)
        {
            return false;
        }

        for (var i = 0; i < actual.Count; i++)
        {
            if (!string.Equals(actual[i], "msg_" + (from + i), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllDistinct(List<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task AcceptedHistoryRaces()
    {
        // A non-cancelled latest response must be fenced by the independent epoch, not just CTS.
        var lateLatest = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend(); backend.OnRequest(_ => lateLatest.Task);
        using var client = backend.CreateClient(); var page = new OpenCodePage(); page.TestActivate(Session, client);
        var latest = page.TestLatestAsync(); var epoch = page.TestEpoch;
        page.TestHardReset(); // production reset increments epoch WITHOUT cancelling this HTTP request
        page.TestApplyRefresh(Asc(1, 2), null, true);
        lateLatest.SetResult(Page(3, 5, null)); await latest;
        Check(page.TestEpoch == epoch + 1 && SameIds(Ids(page), 1, 2), "old latest cannot merge/render into new same-session epoch");
        Check(page.ExecutionPublications == 0 && page.AckPublications == 0, "old latest cannot publish execution evidence/view ACK");

        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnRequest(cursor => cursor == "old" ? oldResponse.Task : newResponse.Task);
        page.TestApplyRefresh(Asc(101, 200), "old", true);
        var old = page.TestLoadOlderAsync();
        page.TestAcceptedHistory("msg_150");
        Check(page.AckInvalidated && page.TestAppliedCount == 0, "accepted rollback clears buffers/ACK baseline before fresh history");
        page.TestApplyRefresh(Asc(101, 200), "fresh", true);
        Check(SameIds(Ids(page), 101, 149), "fresh rollback projection hides exactly target and later rows");
        var newer = page.TestLoadOlderAsync();
        oldResponse.SetResult(Page(1, 100, null)); await old;
        Check(page.TestOlderBusy && page.TestNextCursor == "fresh" && SameIds(Ids(page), 101, 149), "late old finally cannot clear newer loading/cursor or resurrect rolled-back rows");
        newResponse.SetResult(Page(1, 100, null)); await newer;
        Check(!page.TestOlderBusy && SameIds(Ids(page), 1, 149), "current older page still prepends before staged boundary");
        page.TestAcceptedHistory(null);
        backend.OnRequest(_ => Task.FromResult(Page(1, 200, null))); await page.TestLatestAsync();
        Check(SameIds(Ids(page), 1, 200) && page.ExecutionPublications == 0 && page.AckPublications == 0, "redo clear permits fresh history but user-only rows without viewport proof do not ACK");

        // Source replacement with unchanged session also fences non-cancelled older GETs.
        var sourceResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnRequest(_ => sourceResponse.Task);
        page.TestApplyRefresh(Asc(101, 200), "source-old", true); var sourceOld = page.TestLoadOlderAsync();
        page.TestActivate(Session, client); page.TestHardReset(); page.TestApplyRefresh(Asc(8, 9), null, true);
        sourceResponse.SetResult(Page(1, 100, null)); await sourceOld;
        Check(SameIds(Ids(page), 8, 9), "replacement source revision/generation retains newer history");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private static async Task SendHistoryWorkflow()
    {
        var backend = new Backend(); using var client = backend.CreateClient(); var page = new OpenCodePage(); page.TestActivate(Session, client);
        page.TestApplyRefresh(Asc(1, 4), "older", true); page.SeedUnknownAck(); var epoch = page.TestEpoch;
        page.TestBeginSend(11);
        await page.TestLatestAsync(); await page.TestLoadOlderAsync();
        Check(backend.Queries.Count == 0 && page.ExecutionPublications == 0 && page.AckPublications == 0, "during confirmation/transport/handoff both reads/evidence/ACK are deferred");
        Check(page.TestEpoch == epoch + 1 && page.AttemptKeys == 1 && page.AckInvalidated, "begin invalidates epoch/live ACK without erasing unknown attempt keys");
        page.TestEndSend(11, false);
        Check(SameIds(Ids(page), 1, 4) && !page.TestNeedsState, "NoStage terminal preserves ordinary retained pagination");
        page.TestBeginSend(12); page.TestEndSend(12, true);
        Check(page.TestAppliedCount == 0 && page.TestNeedsState && page.AttemptKeys == 1, "staged accepted OR unresolved resets history, keeps keys and demands fresh boundary");
        backend.State = "{\"data\":{\"id\":\"ses_test\",\"projectID\":\"p\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/synthetic\"},\"revert\":{\"messageID\":\"msg_3\"}}}";
        backend.OnRequest(_ => Task.FromResult(Page(1, 5, null))); await page.TestLatestAsync();
        Check(backend.StateReads == 1 && SameIds(Ids(page), 1, 2) && !page.TestNeedsState, "fresh authoritative stage remains staged even after accepted send effects");
        Check(page.ExecutionPublications == 0 && page.AckPublications == 0 && page.AttemptKeys == 1, "fresh user-only history generates no execution terminal or viewport ACK; old keys retained");
        var oldAuxiliary = page.LastAuxiliaryFence;
        page.TestBeginSend(13); page.TestEndSend(12, true);
        Check(oldAuxiliary is not null && !oldAuxiliary(), "latest refresh passes its captured epoch into late auxiliary publication fence");
        var count = backend.Queries.Count; await page.TestLatestAsync();
        Check(backend.Queries.Count == count, "old workflow owner cannot end newer deferral");
        page.TestEndSend(13, true); backend.State = "malformed"; await page.TestLatestAsync();
        Check(page.TestAppliedCount == 0 && page.TestNeedsState && page.ExecutionPublications == 0, "failed authoritative state read cannot mean empty stage or publish stale history");
        await ReviewHistoryRegressions();
    }

    private static string StateResponse(string? boundary = null) => "{\"data\":{\"id\":\"ses_test\",\"projectID\":\"p\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/synthetic\"}"
        + (boundary is null ? "" : ",\"revert\":{\"messageID\":\"" + boundary + "\"}") + "}}";

    private static async Task ReviewHistoryRegressions()
    {
        var backend = new Backend(); using var client = backend.CreateClient(); var page = new OpenCodePage(); page.TestActivate(Session, client);
        page.TestUnreadSelection(); backend.State = StateResponse("msg_3"); backend.OnRequest(_ => Task.FromResult(Page(1, 5, null)));
        await page.TestLatestAsync();
        Check(backend.StateReads == 1 && SameIds(Ids(page), 1, 2), "initial unread staged session reads state BEFORE rendering and hides target/tail");
        page.TestBeginSend(20); page.TestEndSend(20, false); var epoch = page.TestEpoch;
        backend.State = StateResponse(); backend.OnRequest(_ => Task.FromResult(Page(1, 2, null))); await page.TestLatestAsync();
        Check(page.TestEpoch > epoch && SameIds(Ids(page), 1, 2) && page.TestRetainedRows.Count == 2,
            "authoritative known-stage to absent transition discards committed cached tail instead of unmasking it");
        page.TestAcceptedHistory("msg_4"); page.TestApplyRefresh(Asc(1, 5), null, true);
        page.TestBeginSend(21); page.TestEndSend(21, false); epoch = page.TestEpoch;
        backend.State = StateResponse("msg_4"); backend.OnRequest(_ => Task.FromResult(Page(3, 5, null))); await page.TestLatestAsync();
        Check(page.TestEpoch == epoch && SameIds(Ids(page), 1, 3) && page.TestRetainedRows.Count == 5,
            "unchanged authoritative rollback context retains ordinary rolled-off history");
        page.TestUnreadSelection(); backend.State = "malformed"; var messagesBefore = backend.Queries.Count;
        var appliesBefore = page.TestAppliedCount;
        await page.TestLatestAsync();
        Check(page.TestNeedsState && backend.Queries.Count == messagesBefore && page.TestAppliedCount == appliesBefore,
            "failed initial state read cannot fetch/publish unfiltered replacement history");

        var oldIdle = new OpenCodeMessage { Id = "msg_old_idle", Type = OpenCodeMessageTypes.Idle, Outcome = OpenCodeSessionOutcomes.Succeeded };
        var newUser = new OpenCodeMessage { Id = "msg_accepted", Type = OpenCodeMessageTypes.User, Text = "synthetic" };
        var newIdle = new OpenCodeMessage { Id = "msg_new_idle", Type = OpenCodeMessageTypes.Idle, Outcome = OpenCodeSessionOutcomes.Succeeded };
        page.TestAcceptedHistory(null); page.TestAcceptedWorking(newUser.Id!); var events = page.ExecutionEvents.Count;
        page.TestObserve(new() { oldIdle });
        Check(page.TestWorking && page.ExecutionEvents.Count == events, "historical surviving idle seeds baseline without consuming new Working or raising completion");
        page.TestObserve(new() { oldIdle, newUser, newIdle });
        Check(!page.TestWorking && page.ExecutionEvents.Count == events + 1, "actual execution observer reports genuinely later ordered terminal after accepted user witness");
        page.TestAcceptedHistory(null); page.TestAcceptedWorking(newUser.Id!); events = page.ExecutionEvents.Count;
        page.TestObserve(new() { oldIdle, newUser, newIdle });
        Check(!page.TestWorking && page.ExecutionEvents.Count == events + 1, "first fresh window already containing completed new round is not discarded as baseline");
        page.TestAcceptedHistory(null); page.TestAcceptedWorking(newUser.Id!); events = page.ExecutionEvents.Count;
        page.TestObserve(new() { oldIdle, newUser });
        Check(page.TestWorking && page.ExecutionEvents.Count == events, "new user after historical idle establishes order baseline without false completion");
        page.TestObserve(new() { newIdle });
        Check(!page.TestWorking && page.ExecutionEvents.Count == events + 1, "ordinary later-idle behavior resumes after the user order witness, even if user rolled out");
    }

    /// <summary>假 HTTP 后端：按续页游标返回页面；记录每个请求的原始 query 供断言。</summary>
    /// <summary>
    /// F3：无“加载更早消息”按钮——时间线向上滚入顶部阈值（200）时经同一受保护入口自动续页；
    /// 向下滚动、阈值外、无游标、历史工作流进行中都不触发；在途期间重复滚动不产生第二个请求（单飞）。
    /// </summary>
    private static async Task ScrollTopAutoLoad()
    {
        Check(OpenCodePage.ShouldAutoLoadOlderMessages(150, double.NaN, 1000), "first observation inside threshold loads");
        Check(OpenCodePage.ShouldAutoLoadOlderMessages(150, 400, 1000), "upward scroll into threshold loads");
        Check(!OpenCodePage.ShouldAutoLoadOlderMessages(250, 400, 1000), "outside threshold does not load");
        Check(!OpenCodePage.ShouldAutoLoadOlderMessages(150, 100, 1000), "downward scroll near top does not load");
        Check(!OpenCodePage.ShouldAutoLoadOlderMessages(0, 10, 0), "non-scrollable content does not load from ViewChanged");
        Check(OpenCodePage.OlderMessagesAutoLoadThreshold == 200, "threshold is ~200 DIP");

        var backend = new Backend();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnRequest(_ => response.Task);
        using var client = backend.CreateClient();
        var page = new OpenCodePage();
        page.TestActivate(Session, client);

        page.TestScroll(100, 5000);
        Check(backend.Queries.Count == 0 && !page.TestMoreAvailable, "no cursor: scrolling to top never requests");

        page.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        Check(page.TestMoreAvailable && page.TestCanAutoLoad && !page.TestLoadingShown, "chain armed, nothing loading");
        page.TestScroll(4000, 5000);
        page.TestScroll(1000, 5000);
        Check(backend.Queries.Count == 0, "scrolling outside the threshold does not request");

        page.TestBeginSend(7);
        page.TestScroll(150, 5000);
        Check(backend.Queries.Count == 0 && !page.TestOlderBusy, "history workflow in progress blocks auto-load");
        page.TestEndSend(7, reset: false);
        page.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: true);

        page.TestScroll(1000, 5000);
        page.TestScroll(150, 5000);
        Check(page.TestOlderBusy && page.TestLoadingShown && !page.TestCanAutoLoad && backend.Queries.Count == 1,
            "upward scroll into threshold starts exactly one continuation and shows the top progress ring");
        page.TestScroll(100, 5000);
        page.TestScroll(20, 5000);
        page.TestScroll(0, 5000);
        Check(backend.Queries.Count == 1, "single-flight: repeated scrolling while loading issues no second request");
        Check(QueryValue(backend.Queries[0], "cursor") == "c100" && QueryValue(backend.Queries[0], "order") is null,
            "auto-load uses the same cursor+limit continuation");

        response.SetResult(Page(1, 100, "c0"));
        for (var i = 0; i < 200 && page.TestOlderBusy; i++)
        {
            await Task.Delay(10);
        }

        Check(!page.TestOlderBusy && !page.TestLoadingShown && page.TestNextCursor == "c0" && SameIds(Ids(page), 1, 200),
            "completed auto-load prepends older page, advances cursor and hides the ring");
        Check(page.TestScrollOffset == 0, "prepend keeps the reading offset (no jump; stub has no layout growth)");

        backend.OnRequest(_ => Task.FromResult(Page(1, 0, null)));
        page.TestScroll(50, 5000);
        Check(backend.Queries.Count == 1, "downward scroll after load does not request");
        page.TestScroll(10, 5000);
        for (var i = 0; i < 200 && page.TestOlderBusy; i++)
        {
            await Task.Delay(10);
        }

        Check(backend.Queries.Count == 2 && page.TestNextCursor is null && !page.TestMoreAvailable,
            "next upward scroll continues; empty page ends the chain");
        page.TestScroll(0, 5000);
        page.TestScroll(0, 5000);
        Check(backend.Queries.Count == 2, "exhausted chain is never requested by scrolling");
    }

    private sealed class Backend
    {
        private Func<string, Task<HttpResponseMessage>> _handler = _ => Task.FromResult(Page(1, 0, null));

        public List<string> Queries { get; } = new();
        public string State = StateResponse();
        public int StateReads;

        public void OnRequest(Func<string, Task<HttpResponseMessage>> handler) => _handler = handler;

        public OpenCodeClient CreateClient() => new(
            new HttpClient(new Handler(async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/session/ses_test")
                { StateReads++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(State, Encoding.UTF8, "application/json") }; }
                var query = request.RequestUri!.Query;
                Queries.Add(query);
                return await _handler(QueryValue(query, "cursor") ?? string.Empty);
            })),
            new OpenCodeClientOptions { BaseUrl = "https://offline.invalid" },
            true);
    }

    /// <summary>
    /// 反例回归：已加载 1..200（历史 1..100 + 窗口 101..200）且链已推进后，刷新窗口滑到 111..210——
    /// 101..110 必须保留（旧实现整体替换窗口导致永久丢失），同 id 条目用新副本就地更新，
    /// 已推进的链不被轮询重建。
    /// </summary>
    private static async Task RolledOffWindowPreservedAndUpdated()
    {
        var backend = new Backend();
        backend.OnRequest(cursor => Task.FromResult(cursor == "c100" ? Page(1, 100, "c0") : Page(1, 100, null)));
        using var client = backend.CreateClient();
        var page = new OpenCodePage();
        page.TestActivate(Session, client);

        page.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        Check(page.TestNextCursor == "c100" && page.TestMoreAvailable, "first refresh establishes an unadvanced chain");

        await page.TestLoadOlderAsync();
        Check(page.TestNextCursor == "c0", "consumed page advances the chain");
        Check(SameIds(Ids(page), 1, 200) && AllDistinct(Ids(page)), "initial history merges to 1..200 without duplicates");

        // 刷新窗口滑到 111..210：101..110 滚出旧窗口但必须保留；msg_150 内容更新为新副本。
        var updated150 = Msg(150, "updated-150");
        var window = Asc(111, 210);
        window[150 - 111] = updated150;
        page.TestApplyRefresh(window, "c110", explicitRefresh: false);

        var applied = page.TestLastApplied();
        Check(applied.Count == 210 && SameIds(Ids(page), 1, 210) && AllDistinct(Ids(page)),
            "rolled-off 101..110 preserved in server order (1..210, no duplicates)");
        Check(ReferenceEquals(applied[150 - 1], updated150) && applied[150 - 1].Text == "updated-150",
            "same-id entry updated in place from the refreshed window");
        Check(page.TestNextCursor == "c0", "advanced chain is not rebuilt by polling refresh");

        // 续页请求形状：只带 cursor + limit，绝不带 order。
        var lastQuery = backend.Queries[^1];
        Check(QueryValue(lastQuery, "cursor") == "c100" && QueryValue(lastQuery, "limit") == "100"
            && QueryValue(lastQuery, "order") is null, "continuation carries cursor+limit only");
    }

    /// <summary>
    /// 并发回归：同一事件集（窗口滑到 111..210 + 旧链续页返回 1..100）在两种完成顺序下，
    /// 可见历史一致（1..210、无重复、无丢失）；旧响应绝不覆盖刷新重建后的链游标。
    /// </summary>
    private static async Task RefreshOlderOrdersConsistent()
    {
        // A：续页在途 → 刷新先完成（重建链）→ 旧响应最后到达。
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backendA = new Backend();
        backendA.OnRequest(async cursor =>
        {
            if (cursor == "c100")
            {
                entered.TrySetResult();
                await release.Task;
            }

            return Page(1, 100, "c0");
        });
        using var clientA = backendA.CreateClient();
        var pageA = new OpenCodePage();
        pageA.TestActivate(Session, clientA);
        pageA.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);

        var load = pageA.TestLoadOlderAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pageA.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: false);
        Check(pageA.TestNextCursor == "c110", "refresh rebuilds the unadvanced chain while the continuation is in flight");
        release.SetResult();
        await load;

        Check(SameIds(Ids(pageA), 1, 210) && AllDistinct(Ids(pageA)),
            "stale continuation still merges content without loss or duplicates");
        Check(pageA.TestNextCursor == "c110", "stale response never overwrites the rebuilt chain cursor");

        // B：刷新先完成 → 用新链游标续页（无竞争顺序）：结果与 A 一致。
        var backendB = new Backend();
        backendB.OnRequest(cursor => Task.FromResult(cursor == "c110" ? Page(1, 110, null) : Page(1, 100, "c0")));
        using var clientB = backendB.CreateClient();
        var pageB = new OpenCodePage();
        pageB.TestActivate(Session, clientB);
        pageB.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        pageB.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: false);
        await pageB.TestLoadOlderAsync();

        Check(SameIds(Ids(pageB), 1, 210) && AllDistinct(Ids(pageB)),
            "fresh continuation produces the identical visible history 1..210");
        Check(pageB.TestNextCursor is null, "chain exhausted after loading the full history");
    }

    /// <summary>
    /// 结束/停止链不被普通轮询复活（空页、非法游标、循环游标、HTTP 400），且提示中的
    /// “刷新消息”恢复路径真实有效（显式刷新可重建链并继续续页）。
    /// </summary>
    private static async Task StopStatesNotRevivedByPolling()
    {
        // 空页（正常结束）：轮询不复活；显式刷新真实恢复。
        var backendA = new Backend();
        backendA.OnRequest(_ => Task.FromResult(Page(1, 0, "c0")));
        using var clientA = backendA.CreateClient();
        var pageA = new OpenCodePage();
        pageA.TestActivate(Session, clientA);
        pageA.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        await pageA.TestLoadOlderAsync();
        Check(pageA.TestNextCursor is null && !pageA.TestMoreAvailable, "empty page ends the chain normally");

        pageA.TestApplyRefresh(Asc(102, 201), "c101", explicitRefresh: false);
        Check(pageA.TestNextCursor is null && !pageA.TestMoreAvailable, "polling never revives an exhausted chain");

        pageA.TestApplyRefresh(Asc(102, 201), "c101", explicitRefresh: true);
        Check(pageA.TestNextCursor == "c101" && pageA.TestMoreAvailable, "explicit refresh really restarts an exhausted chain");

        // 非法游标（异常停止）：只提示一次；轮询不复活也不重复提示；显式刷新可恢复并继续续页到结束。
        var backendB = new Backend();
        backendB.OnRequest(cursor => Task.FromResult(cursor switch
        {
            "c100" => Page(1, 100, ""),
            "c110" => Page(1, 110, null),
            _ => Page(1, 100, null),
        }));
        using var clientB = backendB.CreateClient();
        var pageB = new OpenCodePage();
        pageB.TestActivate(Session, clientB);
        pageB.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        await pageB.TestLoadOlderAsync();
        Check(pageB.TestNextCursor is null && !pageB.TestMoreAvailable && pageB.TestWarningNoticeCount() == 1,
            "invalid cursor stops the chain with a single notice");

        pageB.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: false);
        Check(pageB.TestNextCursor is null && !pageB.TestMoreAvailable && pageB.TestWarningNoticeCount() == 1,
            "polling neither revives nor re-notifies a stopped chain");

        pageB.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: true);
        Check(pageB.TestNextCursor == "c110" && pageB.TestMoreAvailable, "explicit refresh really restarts a stopped chain");
        await pageB.TestLoadOlderAsync();
        Check(pageB.TestNextCursor is null, "restarted chain can consume the next page to the end");

        // 循环游标（异常停止）：轮询不复活。
        var backendC = new Backend();
        backendC.OnRequest(_ => Task.FromResult(Page(1, 100, "c100")));
        using var clientC = backendC.CreateClient();
        var pageC = new OpenCodePage();
        pageC.TestActivate(Session, clientC);
        pageC.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        await pageC.TestLoadOlderAsync();
        pageC.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: false);
        Check(pageC.TestNextCursor is null && !pageC.TestMoreAvailable, "loop cursor stays stopped across polling");

        // 空刷新窗口不永久禁用链：消息随后出现时由普通刷新重建。
        var backendD = new Backend();
        using var clientD = backendD.CreateClient();
        var pageD = new OpenCodePage();
        pageD.TestActivate(Session, clientD);
        pageD.TestApplyRefresh(new List<OpenCodeMessage>(), null, explicitRefresh: false);
        Check(pageD.TestNextCursor is null && !pageD.TestMoreAvailable, "empty refresh window has no chain");
        pageD.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        Check(pageD.TestNextCursor == "c100" && pageD.TestMoreAvailable, "non-empty refresh builds the chain once messages appear");

        // HTTP 400（异常停止）：轮询不复活；显式刷新真实恢复。
        var backendE = new Backend();
        backendE.OnRequest(cursor => Task.FromResult(cursor == "c100" ? Error(HttpStatusCode.BadRequest) : Page(1, 110, null)));
        using var clientE = backendE.CreateClient();
        var pageE = new OpenCodePage();
        pageE.TestActivate(Session, clientE);
        pageE.TestApplyRefresh(Asc(101, 200), "c100", explicitRefresh: false);
        await pageE.TestLoadOlderAsync();
        Check(pageE.TestNextCursor is null && !pageE.TestMoreAvailable, "HTTP 400 stops the chain");
        pageE.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: false);
        Check(pageE.TestNextCursor is null && !pageE.TestMoreAvailable, "polling never revives a chain stopped by HTTP 400");
        pageE.TestApplyRefresh(Asc(111, 210), "c110", explicitRefresh: true);
        Check(pageE.TestNextCursor == "c110" && pageE.TestMoreAvailable, "explicit refresh restarts after HTTP 400");
    }
}
