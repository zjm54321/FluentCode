using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Billion;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

// Offline evidence for the Phase 2 session-insights controller (the same file the page partial uses).
// Fake HTTP only: no live OpenCode/Billion, no WinUI, no config, no model calls.
internal static partial class Program
{
    private static int _assertions;

    private static async Task Main()
    {
        await LateResultsCannotReappear();
        await HonestProjections();
        await BillionIsExplicitOnly();
        await TraversalInitiationFences();
        await BillionInitiationFences();
        Console.WriteLine($"PASS: 5 session-insights groups, {_assertions} assertions; production controller/service/source linked; fake HTTP only; no live OpenCode/Billion/WinUI.");
        var beforeOrigin = _assertions;
        await OriginSelection();
        await OriginRaces();
        await OriginSourceAndSession();
        Console.WriteLine($"PASS: 3 Billion-origin groups, {_assertions - beforeOrigin} assertions; total {_assertions}; fake HTTP only, not native/Page evidence.");
        var beforeDiscovery = _assertions;
        await DiscoveryWireAndLoad();
        await DiscoveryFailures();
        await DiscoveryOwnership();
        Console.WriteLine($"PASS: 3 Billion-discovery groups, {_assertions - beforeDiscovery} assertions; total {_assertions}; source-linked fake HTTP only; no plugin/native/Page runtime proof.");
        var beforeContext = _assertions;
        await LiveContextRefresh();
        await LiveContextRaces();
        Console.WriteLine($"PASS: 2 live-context groups, {_assertions - beforeContext} assertions; total {_assertions}; real controller, fake HTTP, bounded catalog/no full automatic reads.");
    }

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAILED: " + label);
    }

    // ------------------------------------------------------------------
    // Thin page stub: the same binding fields the real page compares.
    // ------------------------------------------------------------------

    private sealed class PageStub
    {
        public bool Active = true;
        public OpenCodePageSource? Source;
        public long Revision;
        public int SourceGeneration = 1;
        public OpenCodeClient? Client;
        public int PageGeneration = 1;
        public int SessionGeneration = 1;
        public string? SessionId;
        public Action? AfterCurrentCheck;

        public bool IsCurrent(OpenCodeInsightsBinding b)
        {
            var current = Active && ReferenceEquals(b.Source, Source) && b.Revision == Revision && b.SourceGeneration == SourceGeneration
                && ReferenceEquals(b.Client, Client) && b.PageGeneration == PageGeneration && b.SessionGeneration == SessionGeneration
                && b.SessionId == SessionId;
            // Test-only boundary: invalidate AFTER a precheck captured true, BEFORE the caller dispatches.
            var after = AfterCurrentCheck;
            AfterCurrentCheck = null;
            after?.Invoke();
            return current;
        }

        public OpenCodeInsightsBinding Capture()
            => new(Source!, Revision, SourceGeneration, Client!, PageGeneration, SessionGeneration, SessionId!);
    }

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public readonly Dictionary<string, TaskCompletionSource<bool>> Gates = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.NotFound);
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get, "only GET requests");
            var key = request.RequestUri!.AbsolutePath;
            Paths.Add(request.RequestUri.PathAndQuery);
            if (BeforeResponse is { } before) await before(request, token);
            if (Gates.TryGetValue(key, out var gate))
            {
                await gate.Task.WaitAsync(token);
            }

            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Session(string id, string? parent, double cost = 1.5, string directory = "C:/work")
        => $$$"""
        {"id":"{{{id}}}","projectID":"prj","parentID":{{{(parent is null ? "null" : "\"" + parent + "\"")}}},"cost":{{{cost}}},
         "tokens":{"input":100,"output":20,"reasoning":5,"cache":{"read":300,"write":40}},
         "time":{"created":1,"updated":2},"location":{"directory":"{{{directory}}}"},
         "model":{"id":"model_a","providerID":"prov"}}
        """;

    private const string Models = """
        {"location":{"directory":"C:/work"},"data":[{"id":"model_a","providerID":"prov","name":"A",
          "limit":{"context":200000},
          "cost":[{"input":3,"output":15,"cache":{"read":0.3,"write":3.75}},
                  {"tier":{"type":"context","size":200000},"input":6,"output":22.5,"cache":{"read":0.6,"write":7.5}}]}]}
        """;

    private static string Plugins(bool todo, bool omo) => "{\"location\":{\"directory\":\"C:/work\"},\"data\":["
        + string.Join(",", new[]
        {
            todo ? """{"id":"opencode-todolist","source":{"type":"package","target":"opencode-todolist"},"features":{"server":true},"state":{"status":"active"}}""" : null,
            omo ? """{"id":"oh-my-opencode-slim","source":{"type":"package","target":"oh-my-opencode-slim"},"features":{"server":true},"state":{"status":"active"}}""" : null,
        }.Where(item => item is not null)) + "]}";

    private const string Mcp = """{"location":{"directory":"C:/work"},"data":[{"name":"docs","status":{"status":"connected"}}]}""";

    private static (PageStub Page, Fake Fake, OpenCodeSessionInsightsController Controller) Fixture(
        string sessionId, Func<string, BillionContextClient>? billion = null)
    {
        var fake = new Fake();
        var http = new HttpClient(fake);
        var client = new OpenCodeClient(http, new OpenCodeClientOptions
        {
            BaseUrl = "https://offline.invalid/prefix",
            AuthenticationMode = OpenCodeAuthenticationMode.None,
        }, true);
        var page = new PageStub { Client = client, SessionId = sessionId };
        page.Source = new OpenCodePageSource("ep-test", 1, () => client);
        page.Revision = 1;
        var controller = new OpenCodeSessionInsightsController(page.IsCurrent,
            billion ?? (_ => throw new InvalidOperationException("Billion must not be created")));
        return (page, fake, controller);
    }

    // Explicit fake-origin setup for pre-existing tests; never a production fallback.
    private static void Confirm(OpenCodeSessionInsightsController controller, OpenCodeInsightsBinding binding, bool value)
    {
        var context = controller.BindBillionContext(binding)!;
        if (value && controller.Snapshot.BillionOrigin is null)
            controller.SetBillionOrigin(binding, context, "http://127.0.0.1:18787");
        controller.SetBillionConfirmed(binding, value, controller.Snapshot.BillionContext);
    }

    private static Task ReadBillion(OpenCodeSessionInsightsController controller, OpenCodeInsightsBinding binding)
        => controller.ReadBillionAsync(binding, CancellationToken.None, controller.BindBillionContext(binding));

    private static OpenCodeMessage Message(string json) => JsonSerializer.Deserialize<OpenCodeMessage>(json)!;

    private static OpenCodeMessage Assistant(string id, double created, double input)
        => Message($$$"""{"id":"{{{id}}}","type":"assistant","time":{"created":{{{created}}}},"model":{"id":"model_a","providerID":"prov"},"tokens":{"input":{{{input}}},"output":10,"reasoning":0,"cache":{"read":0,"write":0}},"content":[]}""");

    private static OpenCodeMessage TodoWrite(string id, double created, string todosJson)
        => Message("{\"id\":\"" + id + "\",\"type\":\"assistant\",\"time\":{\"created\":" + created
            + "},\"content\":[{\"type\":\"tool\",\"name\":\"todowrite\",\"state\":{\"status\":\"completed\",\"input\":{\"todos\":"
            + todosJson + "}}}]}");

    // ------------------------------------------------------------------
    // Group 1: source/session switch + cancel; latest refresh owns completion.
    // ------------------------------------------------------------------

    private static async Task LateResultsCannotReappear()
    {
        var (page, fake, controller) = Fixture("ses_a");
        fake.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/prefix/api/session/ses_a" => Json("{\"data\":" + Session("ses_a", null) + "}"),
            "/prefix/api/session/ses_b" => Json("{\"data\":" + Session("ses_b", null) + "}"),
            "/prefix/api/session" => Json("{\"data\":[],\"cursor\":{\"previous\":null,\"next\":null}}"),
            "/prefix/api/model" => Json(Models),
            "/prefix/api/mcp" => Json(Mcp),
            "/prefix/api/plugin" => Json(Plugins(false, false)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        // A: session switch while Session.Info is in flight.
        var gate = new TaskCompletionSource<bool>();
        fake.Gates["/prefix/api/session/ses_a"] = gate;
        using (var outer = new CancellationTokenSource())
        {
            var pending = controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, outer.Token);
            Check(controller.Snapshot.Loading && controller.Snapshot.SessionId == "ses_a", "switch.loading-owned-by-a");
            var startedBefore = controller.OpenCodeReadsStarted;

            page.SessionGeneration++;
            page.SessionId = "ses_b";
            controller.Reset();          // page hook on switch
            outer.Cancel();               // page session token cancelled
            gate.SetResult(true);
            await pending;
            Check(controller.Snapshot.SessionId is null && !controller.Snapshot.Loading, "switch.cleared-not-loading");
            Check(controller.Snapshot.Cost.State == OpenCodeInsightState.NotLoaded && controller.Snapshot.Cost.Value is null,
                "switch.no-late-cost");
            Check(controller.OpenCodeReadsStarted == startedBefore, "switch.no-reads-after-reset");
        }

        // B: stale binding captured before switch is rejected without any request.
        var stale = page.Capture() with { SessionGeneration = page.SessionGeneration - 1, SessionId = "ses_a" };
        var pathsBefore = fake.Paths.Count;
        await controller.LoadAsync(stale, Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        Check(fake.Paths.Count == pathsBefore && controller.Snapshot.SessionId is null, "stale-binding.no-request");

        // C: switch WITHOUT cancellation (race where the page token is not yet cancelled): binding recheck alone discards.
        fake.Gates.Clear();
        var modelGate = new TaskCompletionSource<bool>();
        fake.Gates["/prefix/api/model"] = modelGate;
        var load = controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        await Task.Delay(20);
        page.SessionGeneration++;
        page.SessionId = "ses_c";
        modelGate.SetResult(true);
        await load;
        Check(controller.Snapshot.Cost.Value is null && controller.Snapshot.SampledAt is null,
            "binding-recheck.late-result-dropped-even-without-cancel");
        page.SessionId = "ses_b";
        page.SessionGeneration++;
        controller.Reset();

        // D: two refreshes — old finally can't clear newer loading; newest wins.
        fake.Gates.Clear();
        var firstGate = new TaskCompletionSource<bool>();
        fake.Gates["/prefix/api/session/ses_b"] = firstGate;
        var binding = page.Capture();
        var first = controller.LoadAsync(binding, Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        await Task.Delay(20);
        var secondGate = new TaskCompletionSource<bool>();
        fake.Gates["/prefix/api/session/ses_b"] = secondGate;
        var second = controller.LoadAsync(binding, Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        await first; // superseded (its token was cancelled by the newer load)
        Check(controller.Snapshot.Loading, "refresh.old-finally-keeps-newer-loading");
        firstGate.TrySetResult(true);
        secondGate.SetResult(true);
        await second;
        Check(!controller.Snapshot.Loading && controller.Snapshot.Cost.State == OpenCodeInsightState.Loaded,
            "refresh.newest-completes");
        Check(controller.Snapshot.Cost.Value!.Scope.SelectedIsRoot && controller.Snapshot.Cost.Value.Summary.TotalCost == 1.5,
            "refresh.root-tree-cost");

        // E: source revoked → no new request starts.
        var revokedPaths = fake.Paths.Count;
        page.Source!.Revoke();
        await controller.LoadAsync(binding, Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        Check(fake.Paths.Count == revokedPaths, "revoked.no-request");

        // F: no timer/background: nothing happens without explicit calls.
        var idle = fake.Paths.Count;
        await Task.Delay(50);
        Check(fake.Paths.Count == idle, "no-background-reads");
    }

    // ------------------------------------------------------------------
    // Group 2: incomplete/unknown projections remain honest (incl. Todo empty clear).
    // ------------------------------------------------------------------

    private static async Task HonestProjections()
    {
        // Child session whose parent read fails: no numbers, never a subtree labelled as the conversation.
        {
            var (page, fake, controller) = Fixture("ses_child");
            fake.Respond = request => request.RequestUri!.AbsolutePath switch
            {
                "/prefix/api/session/ses_child" => Json("{\"data\":" + Session("ses_child", "ses_root") + "}"),
                "/prefix/api/session/ses_root" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                "/prefix/api/model" => Json(Models),
                "/prefix/api/mcp" => Json(Mcp),
                "/prefix/api/plugin" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
            await controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
            var s = controller.Snapshot;
            Check(s.Cost.State == OpenCodeInsightState.Failed && s.Cost.Value is null, "child.root-unverified-no-cost");
            Check(s.Cost.Note.Contains("子树"), "child.explains-no-subtree-substitution");
            Check(!fake.Paths.Any(path => path.Contains("parentID=ses_child")), "child.no-subtree-tree-read");
            Check(s.Capabilities.State == OpenCodeInsightState.Partial
                && s.Capabilities.Value!.Plugins.Availability == OpenCodeCapabilityAvailability.Unknown,
                "plugins-failed.unknown-not-empty");
            Check(s.Todo.State == OpenCodeInsightState.Failed && s.Todo.Value!.Items.IsEmpty && !s.Todo.Value.Complete,
                "plugins-failed.todo-unknown");
            Check(s.Children.State == OpenCodeInsightState.Failed, "plugins-failed.children-unknown");
            Check(!fake.Paths.Any(path => path.StartsWith("/prefix/api/session/active")), "plugins-failed.no-active-read");
            Check(s.Pricing.State == OpenCodeInsightState.Loaded && s.Pricing.Value!.Tiers.Length == 2
                && s.Pricing.Value.Tiers[1].Tier?.Size == 200000 && Math.Abs((s.Pricing.Value.Tiers[0].Multipliers?.Read ?? 0) - 0.1) < 1e-9,
                "pricing.tiers-explicit-and-same-tier-ratio");
            Check(s.Context.State == OpenCodeInsightState.Partial && s.Context.Value!.IsExact == false,
                "context.no-usage-is-partial-not-exact");
        }

        // Child session with verified root: whole-tree scope, child own row separate; tree read failure branch → Partial.
        {
            var (page, fake, controller) = Fixture("ses_child");
            fake.Respond = request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var query = request.RequestUri.Query;
                if (path == "/prefix/api/session/ses_child") return Json("{\"data\":" + Session("ses_child", "ses_root", 2) + "}");
                if (path == "/prefix/api/session/ses_root") return Json("{\"data\":" + Session("ses_root", null, 5) + "}");
                if (path == "/prefix/api/session" && query.Contains("parentID=ses_root"))
                    return Json("{\"data\":[" + Session("ses_child", "ses_root", 2) + "],\"cursor\":{\"previous\":null,\"next\":null}}");
                if (path == "/prefix/api/session" && query.Contains("parentID=ses_child"))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (path == "/prefix/api/model") return Json(Models);
                if (path == "/prefix/api/mcp") return Json(Mcp);
                if (path == "/prefix/api/plugin") return Json(Plugins(true, true));
                if (path == "/prefix/api/session/active") return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };
            var messages = new[]
            {
                TodoWrite("msg_1", 1, """[{"content":"a","status":"pending","priority":"high"}]"""),
                Assistant("msg_2", 2, 1000),
                TodoWrite("msg_3", 3, "[]"),
            };

            await controller.LoadAsync(page.Capture(), messages, historyComplete: true, CancellationToken.None);
            var s = controller.Snapshot;
            var cost = s.Cost.Value!;
            Check(!cost.Scope.SelectedIsRoot && cost.Scope.RootSessionId == "ses_root" && cost.Scope.AncestorReads == 1,
                "child.verified-root-scope");
            Check(cost.SelectedOwn?.CoreCost == 2 && cost.Summary.RootCost == 5, "child.own-row-separate");
            Check(s.Cost.State == OpenCodeInsightState.Partial && cost.Summary.Incomplete && cost.Summary.TotalCost == 7,
                "tree.branch-failure-known-partial");
            Check(s.Todo.State == OpenCodeInsightState.Loaded && s.Todo.Value!.Items.IsEmpty && s.Todo.Value.Complete
                && s.Todo.Value.Reason.Contains("清空"), "todo.empty-clear-complete");
            Check(s.Children.State == OpenCodeInsightState.Partial && s.Children.Value!.Children.IsEmpty
                && !s.Children.Value.Complete, "children.list-failure-partial-not-none");
            Check(s.Context.Value!.ReferenceTokens == 1010 && s.Context.Value.ContextLimit == 200000
                && s.Context.State == OpenCodeInsightState.Loaded, "context.reference-and-limit-no-percentage");

            // Same messages, history NOT complete → Todo/Context remain honest.
            await controller.LoadAsync(page.Capture(), messages, historyComplete: false, CancellationToken.None);
            s = controller.Snapshot;
            Check(s.Todo.State == OpenCodeInsightState.Partial && !s.Todo.Value!.Complete && s.Todo.Value.Reason.StartsWith("Incomplete"),
                "todo.incomplete-history-not-empty-claim");
            Check(s.Context.State == OpenCodeInsightState.Partial
                && s.Context.Value!.StaleReasons.Contains(OpenCodeContextStaleReason.HistoryIncomplete), "context.history-incomplete-stale");
        }

        // OMO verified: direct children + same-source active; absent active → false (not "done").
        {
            var (page, fake, controller) = Fixture("ses_root");
            fake.Respond = request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/prefix/api/session/ses_root") return Json("{\"data\":" + Session("ses_root", null) + "}");
                if (path == "/prefix/api/session")
                    return Json("{\"data\":[" + Session("ses_k1", "ses_root") + "," + Session("ses_k2", "ses_root")
                        + "],\"cursor\":{\"previous\":null,\"next\":null}}");
                if (path == "/prefix/api/session/active") return Json("""{"data":{"ses_k1":{"type":"running"}}}""");
                if (path == "/prefix/api/model") return Json(Models);
                if (path == "/prefix/api/mcp") return Json(Mcp);
                if (path == "/prefix/api/plugin") return Json(Plugins(false, true));
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };
            await controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
            var children = controller.Snapshot.Children;
            Check(children.State == OpenCodeInsightState.Loaded && children.Value!.Complete, "omo.children-complete");
            Check(children.Value!.Children.Single(c => c.SessionId == "ses_k1").Active == true
                && children.Value.Children.Single(c => c.SessionId == "ses_k2").Active == false, "omo.active-tristate");
            Check(controller.Snapshot.Todo.State == OpenCodeInsightState.Unavailable, "todo.plugin-not-listed-unavailable");
        }
    }

    // ------------------------------------------------------------------
    // Group 3: Billion — explicit click only; confirmation scoped to source; separate from Core; no content fields.
    // ------------------------------------------------------------------

    private sealed class BillionFake : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public TaskCompletionSource<bool>? DetailGate;
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeResponse;
        public string Detail = """{"id":"ses_root","contextTokens":1234,"contextTokensSource":"usage","contextWindow":200000,"requests":7,"handoffHtml":"SECRET_HANDOFF","title":"SECRET_TITLE","blockDetails":[{"summary":"SECRET_SUMMARY"}]}""";
        public HttpStatusCode DetailStatus = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "billion.get-no-auth");
            Check(request.RequestUri!.Host == "127.0.0.1", "billion.loopback-only");
            Paths.Add(request.RequestUri.AbsolutePath);
            if (BeforeResponse is { } before) await before(request, token);
            if (request.RequestUri.AbsolutePath == "/__bili/health") return Json("""{"ok":true,"version":"0.1.189"}""");
            if (DetailGate is not null) await DetailGate.Task.WaitAsync(token);
            return DetailStatus == HttpStatusCode.OK ? Json(Detail) : new HttpResponseMessage(DetailStatus);
        }
    }

    private static async Task BillionIsExplicitOnly()
    {
        var billion = new BillionFake();
        var created = 0;
        var (page, fake, controller) = Fixture("ses_root", origin =>
        {
            created++;
            return new BillionContextClient(origin, handler: new NonDisposingHandler(billion));
        });
        fake.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/prefix/api/session/ses_root" => Json("{\"data\":" + Session("ses_root", null) + "}"),
            "/prefix/api/session/ses_other" => Json("{\"data\":" + Session("ses_other", null) + "}"),
            "/prefix/api/session" => Json("{\"data\":[],\"cursor\":{\"previous\":null,\"next\":null}}"),
            "/prefix/api/model" => Json(Models),
            "/prefix/api/mcp" => Json(Mcp),
            "/prefix/api/plugin" => Json(Plugins(false, false)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        await controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        await controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
        Check(created == 0 && billion.Paths.Count == 0 && controller.BillionReadsStarted == 0, "billion.no-read-on-overview-refresh");
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Idle, "billion.idle-by-default");

        await ReadBillion(controller, page.Capture());
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.MissingOrigin && created == 0
            && billion.Paths.Count == 0, "billion.unconfirmed-no-request");

        Confirm(controller, page.Capture(), true);
        Check(controller.Snapshot.BillionConfirmed && controller.Snapshot.Cost.Value is not null, "billion.confirm-keeps-overview");
        await ReadBillion(controller, page.Capture());
        var view = controller.Snapshot.Billion;
        Check(billion.Paths.SequenceEqual(new[] { "/__bili/health", "/__bili/sessions/ses_root/detail" }), "billion.health-then-single-detail");
        Check(view.Phase == OpenCodeBillionPhase.Loaded && view.Stats!.ContextTokens == 1234 && view.Stats.Requests == 7
            && view.SessionId == "ses_root", "billion.numeric-stats " + view.Phase + " " + view.Note + " " + view.Stats?.ContextTokens + " " + view.Stats?.Requests);
        var rendered = controller.Snapshot.ToString() + view.Stats + view.Health;
        Check(!rendered.Contains("SECRET"), "billion.no-content-fields-in-snapshot");
        Check(controller.Snapshot.Cost.Value!.Summary.TotalCost == 1.5, "billion.not-summed-into-core-cost");

        // Session switch while detail is pending: late record never appears for the new session.
        billion.DetailGate = new TaskCompletionSource<bool>();
        var pending = ReadBillion(controller, page.Capture());
        page.SessionGeneration++;
        page.SessionId = "ses_other";
        controller.Reset();
        billion.DetailGate.SetResult(true);
        await pending;
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Idle && controller.Snapshot.Billion.Stats is null,
            "billion.switch-drops-late-record");
        billion.DetailGate = null;

        // Confirmation survives a session switch within the same source revision, but not a new revision.
        Check(controller.IsBillionConfirmed(page.Capture()), "billion.confirmation-same-source");
        var oldSource = page.Source!;
        page.Source = new OpenCodePageSource("ep-test", 2, () => page.Client!);
        page.Revision = 2;
        page.SourceGeneration++;
        controller.Reset();
        Check(!controller.IsBillionConfirmed(page.Capture()), "billion.new-revision-requires-new-confirmation");
        var before = billion.Paths.Count;
        await ReadBillion(controller, page.Capture());
        Check(billion.Paths.Count == before && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.MissingOrigin,
            "billion.new-revision-no-request");
        Check(!ReferenceEquals(oldSource, page.Source), "fixture.source-replaced");

        // A record whose identity differs from the captured session is never shown (backend identity check).
        Confirm(controller, page.Capture(), true);
        page.SessionGeneration++;
        page.SessionId = "ses_root";
        controller.Reset();
        var savedDetail = billion.Detail;
        billion.Detail = savedDetail.Replace("\"id\":\"ses_root\"", "\"id\":\"ses_elsewhere\"");
        await ReadBillion(controller, page.Capture());
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Failed && controller.Snapshot.Billion.Stats is null,
            "billion.identity-mismatch-not-shown");
        billion.Detail = savedDetail;

        // NoRecord is distinct from failure and from zero.
        Confirm(controller, page.Capture(), true);
        billion.DetailStatus = HttpStatusCode.NotFound;
        await ReadBillion(controller, page.Capture());
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.NoRecord && controller.Snapshot.Billion.Stats is null,
            "billion.no-record-explicit");

        // Unchecking cancels and clears.
        Confirm(controller, page.Capture(), false);
        Check(!controller.Snapshot.BillionConfirmed && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Idle,
            "billion.uncheck-clears");
        Check(controller.BillionReadsStarted == 8, "billion.request-count-exact");
    }

    // ------------------------------------------------------------------
    // Group 4: each traversal HTTP start is fenced, independently of cancellation propagation.
    // ------------------------------------------------------------------

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static object SourceLock(OpenCodePageSource source)
        => typeof(OpenCodePageSource).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;

    private static void TreeResponses(Fake fake)
    {
        fake.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/prefix/api/session/ses_root" => Json("{\"data\":" + Session("ses_root", null) + "}"),
            "/prefix/api/session" when request.RequestUri.Query.Contains("parentID=ses_root")
                && !request.RequestUri.Query.Contains("cursor=") => Json("{\"data\":[" + Session("ses_child", "ses_root")
                    + "],\"cursor\":{\"previous\":null,\"next\":\"page_2\"}}"),
            "/prefix/api/session" => Json("{\"data\":[],\"cursor\":{\"previous\":null,\"next\":null}}"),
            "/prefix/api/model" => Json(Models),
            "/prefix/api/mcp" => Json(Mcp),
            "/prefix/api/plugin" => Json(Plugins(false, false)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static async Task TraversalInitiationFences()
    {
        // Hold either the usage root (second exact GET) or the first child page. Source revocation
        // is real; a last-registered callback deliberately delays propagation to the page/load token.
        foreach (var holdChild in new[] { false, true })
        {
            var (page, fake, controller) = Fixture("ses_root");
            TreeResponses(fake);
            using var outer = CancellationTokenSource.CreateLinkedTokenSource(page.Source!.RevocationToken);
            using var releaseCancellation = new ManualResetEventSlim();
            var cancellationEntered = Signal();
            using var registration = page.Source.RevocationToken.Register(() =>
            {
                cancellationEntered.TrySetResult(true);
                releaseCancellation.Wait();
            });
            var entered = Signal();
            var release = Signal();
            var rootReads = 0;
            CancellationToken suspendedToken = default;
            fake.BeforeResponse = (request, token) =>
            {
                Check(Monitor.IsEntered(SourceLock(page.Source)), "tree.each-http-start-inside-source-lock");
                var path = request.RequestUri!.AbsolutePath;
                var hold = holdChild ? path == "/prefix/api/session"
                    : path == "/prefix/api/session/ses_root" && ++rootReads == 2;
                if (!hold) return Task.CompletedTask;
                suspendedToken = token;
                entered.TrySetResult(true);
                return release.Task; // Intentionally no WaitAsync(token): the response can arrive after revocation.
            };
            var load = controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, outer.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                page.Source.Revoke();
                await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(page.Source.IsRevoked && !outer.IsCancellationRequested && !suspendedToken.IsCancellationRequested,
                    "tree.revoked-before-request-token-cancellation");
                var pathsBefore = fake.Paths.Count;
                release.TrySetResult(true);
                await load.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!suspendedToken.IsCancellationRequested && fake.Paths.Count == pathsBefore,
                    holdChild ? "tree.revoked-child-no-continuation-or-descendant" : "tree.revoked-root-zero-child-http");
                Check(fake.Paths.Count(path => path.StartsWith("/prefix/api/session?")) == (holdChild ? 1 : 0),
                    "tree.exact-child-http-count-after-revoke");
                Check(controller.Snapshot.Cost.Value is null && controller.Snapshot.SampledAt is null,
                    "tree.revocation-never-publishes-partial-old-scope");
            }
            finally
            {
                release.TrySetResult(true);
                releaseCancellation.Set();
                await load.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        // Binding invalidation also fences the continuation, without any source/token cancellation.
        {
            var (page, fake, controller) = Fixture("ses_root");
            TreeResponses(fake);
            var entered = Signal();
            var release = Signal();
            var rootReads = 0;
            CancellationToken suspendedToken = default;
            fake.BeforeResponse = (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath != "/prefix/api/session/ses_root" || ++rootReads != 2)
                    return Task.CompletedTask;
                suspendedToken = token;
                entered.TrySetResult(true);
                return release.Task;
            };
            var load = controller.LoadAsync(page.Capture(), Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.SessionGeneration++;
            var before = fake.Paths.Count;
            release.SetResult(true);
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!suspendedToken.IsCancellationRequested && fake.Paths.Count == before,
                "tree.binding-invalidated-with-live-token-zero-child-http");
            Check(controller.Snapshot.Cost.Value is null, "tree.invalid-binding-no-partial-cost");
        }

        // Same-binding refresh: isolate the ID fence by detaching ONLY the old cancellation handle
        // in this test. LoadAsync still performs the real ownership replacement; old ct remains live.
        {
            var (page, fake, controller) = Fixture("ses_root");
            TreeResponses(fake);
            var entered = Signal();
            var release = Signal();
            var rootReads = 0;
            CancellationToken oldToken = default;
            fake.BeforeResponse = (request, token) =>
            {
                if (request.RequestUri!.AbsolutePath != "/prefix/api/session/ses_root" || ++rootReads != 2)
                    return Task.CompletedTask;
                oldToken = token;
                entered.TrySetResult(true);
                return release.Task;
            };
            var binding = page.Capture();
            var old = controller.LoadAsync(binding, Array.Empty<OpenCodeMessage>(), false, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            typeof(OpenCodeSessionInsightsController).GetField("_loadCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, null);
            await controller.LoadAsync(binding, Array.Empty<OpenCodeMessage>(), true, CancellationToken.None);
            var newest = controller.Snapshot;
            var before = fake.Paths.Count;
            release.SetResult(true);
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!oldToken.IsCancellationRequested && fake.Paths.Count == before,
                "tree.same-binding-newer-load-id-blocks-old-live-token-continuation");
            Check(ReferenceEquals(newest, controller.Snapshot) && newest.Cost.State == OpenCodeInsightState.Loaded,
                "tree.old-load-cannot-publish-or-settle-new-owner");
            Check(fake.Paths.Count(path => path.Contains("parentID=ses_root")) == 2
                && fake.Paths.Count(path => path.Contains("parentID=ses_child")) == 1,
                "tree.valid-new-scope-reads-both-pages-and-descendant");
            Check(controller.OpenCodeReadsStarted == 10, "tree.logical-read-counter-unchanged-by-inner-http-fences");
        }

        // The standalone overload remains usable; the guarded overload rejects before its root GET.
        {
            var (page, fake, _) = Fixture("ses_root");
            TreeResponses(fake);
            var rejected = false;
            try
            {
                await OpenCodeUsageService.ReadTreeAsync(page.Client!, "ses_root", null, CancellationToken.None, _ => false);
            }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && fake.Paths.Count == 0, "tree.rejected-starter-aborts-before-root-with-live-token");
            var snapshot = await OpenCodeUsageService.ReadTreeAsync(page.Client!, "ses_root");
            Check(!snapshot.Summary.Incomplete && snapshot.Summary.TotalCost == 3 && fake.Paths.Count == 4,
                "tree.legacy-overload-retains-unfenced-standalone-behavior");
        }
        Console.WriteLine("PASS: traversal initiation fences: revoked root=0 child GET; revoked child=0 continuation/descendant; delayed tokens live; same-binding owner ID fenced; valid tree=4 HTTP.");
    }

    // ------------------------------------------------------------------
    // Group 5: Billion health AND detail initiation use the source/owner/confirmation boundary.
    // ------------------------------------------------------------------

    private static async Task BillionInitiationFences()
    {
        // The injected factory is a deterministic boundary after entry/confirmation prechecks.
        foreach (var invalidate in new[] { "source", "binding", "confirmation" })
        {
            var billion = new BillionFake();
            Action? atFactory = null;
            var (page, _, controller) = Fixture("ses_root", origin =>
            {
                atFactory!();
                return new BillionContextClient(origin, handler: new NonDisposingHandler(billion));
            });
            var binding = page.Capture();
            Confirm(controller, binding, true);
            atFactory = () =>
            {
                if (invalidate == "source") page.Source!.Revoke();
                else if (invalidate == "binding") page.SessionGeneration++;
                else Confirm(controller, binding, false);
            };
            await ReadBillion(controller, binding);
            Check(billion.Paths.Count == 0 && controller.BillionReadsStarted == 0,
                "billion.health-precheck-dispatch-" + invalidate + "-no-http");
            Check(controller.Snapshot.Billion.Stats is null, "billion.invalidated-health-no-stats");
        }

        // Return a cached true from the post-health field precheck, then invalidate the page binding.
        // This simulates a switch in the check-to-send gap without adding a production test hook.
        {
            var billion = new BillionFake();
            var (page, _, controller) = Fixture("ses_root", origin => new BillionContextClient(origin, handler: billion));
            Confirm(controller, page.Capture(), true);
            CancellationToken requestToken = default;
            billion.BeforeResponse = (_, token) =>
            {
                Check(Monitor.IsEntered(SourceLock(page.Source!)), "billion.health-http-start-inside-source-lock");
                requestToken = token;
                page.AfterCurrentCheck = () => page.SessionGeneration++;
                return Task.CompletedTask;
            };
            await ReadBillion(controller, page.Capture());
            Check(!requestToken.IsCancellationRequested && billion.Paths.SequenceEqual(new[] { "/__bili/health" })
                && controller.BillionReadsStarted == 1, "billion.detail-precheck-dispatch-binding-invalidated-no-http");
            Check(controller.Snapshot.Billion.Stats is null, "billion.invalidated-detail-no-stats");
        }

        // A newer click on the SAME binding owns dispatch even if old cancellation is delayed.
        // Replace at the factory (health gap) or cached-true owner check (detail gap).
        foreach (var replaceBeforeDetail in new[] { false, true })
        {
            var billion = new BillionFake();
            Action? atFactory = null;
            var (page, _, controller) = Fixture("ses_root", origin =>
            {
                var replace = atFactory;
                atFactory = null;
                replace?.Invoke();
                return new BillionContextClient(origin, handler: new NonDisposingHandler(billion));
            });
            var binding = page.Capture();
            Confirm(controller, binding, true);
            Task? replacement = null;
            CancellationToken oldToken = default;
            void Replace()
            {
                var field = typeof(OpenCodeSessionInsightsController).GetField("_billionCts", BindingFlags.Instance | BindingFlags.NonPublic)!;
                oldToken = ((CancellationTokenSource)field.GetValue(controller)!).Token;
                field.SetValue(controller, null); // Test-only delayed-cancellation seam, not a copied algorithm.
                replacement = ReadBillion(controller, binding);
            }
            if (replaceBeforeDetail)
            {
                var first = true;
                billion.BeforeResponse = (_, _) =>
                {
                    if (first)
                    {
                        first = false;
                        page.AfterCurrentCheck = Replace;
                    }
                    return Task.CompletedTask;
                };
            }
            else atFactory = Replace;
            await ReadBillion(controller, binding);
            Check(replacement is not null, "billion.same-binding-new-click-really-started");
            await replacement!;
            Check(!oldToken.IsCancellationRequested && billion.Paths.Count == (replaceBeforeDetail ? 3 : 2)
                && controller.BillionReadsStarted == billion.Paths.Count,
                replaceBeforeDetail ? "billion.newer-id-fences-old-detail-dispatch" : "billion.newer-id-fences-old-health-dispatch");
            Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Loaded
                && billion.Paths.Count(path => path.EndsWith("/detail")) == 1,
                "billion.only-new-owner-publishes-single-detail");
        }

        // Suspend health and revoke the source without linking cancellation: detail must not start.
        {
            var billion = new BillionFake();
            var (page, _, controller) = Fixture("ses_root", origin => new BillionContextClient(origin, handler: billion));
            Confirm(controller, page.Capture(), true);
            var entered = Signal();
            var release = Signal();
            CancellationToken healthToken = default;
            billion.BeforeResponse = (_, token) =>
            {
                healthToken = token;
                entered.TrySetResult(true);
                return release.Task;
            };
            var read = ReadBillion(controller, page.Capture());
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.Source!.Revoke();
            release.SetResult(true);
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!healthToken.IsCancellationRequested && billion.Paths.Count == 1 && controller.BillionReadsStarted == 1,
                "billion.revoked-after-health-live-token-no-detail");
        }

        // Valid unchanged scope: both actual sends (not merely helper entry) happen under source lock.
        {
            var billion = new BillionFake();
            var (page, _, controller) = Fixture("ses_root", origin => new BillionContextClient(origin, handler: billion));
            Confirm(controller, page.Capture(), true);
            billion.BeforeResponse = (_, _) =>
            {
                Check(Monitor.IsEntered(SourceLock(page.Source!)), "billion.every-http-start-inside-source-lock");
                return Task.CompletedTask;
            };
            await ReadBillion(controller, page.Capture());
            Check(billion.Paths.Count == 2 && controller.BillionReadsStarted == 2
                && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Loaded,
                "billion.valid-scope-health-and-detail-preserved");
        }
        Console.WriteLine("PASS: Billion initiation fences: health precheck invalidation=0 HTTP; detail check-to-send invalidation=0 detail HTTP; revoked health with live token=0 detail; valid scope=2 locked HTTP.");
    }

    /// <summary>The connector owns/disposes its handler; keep the shared fake alive across clicks.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }
}
