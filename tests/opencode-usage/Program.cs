using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int assertions;
    private static async Task Main()
    {
        await TreePagingAndCompleteness();
        await TokensCostAndModelPrices();
        ContextReferenceAndStaleness();
        Console.WriteLine($"PASS: 3 usage groups, {assertions} assertions; production-linked; fake HTTP only; no live service/model/config/ACP.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };
    private static HttpResponseMessage Raw(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };
    private static object Page(IEnumerable<OpenCodeSessionInfo> sessions, string? next = null) =>
        new { data = sessions.ToArray(), cursor = new { previous = (string?)null, next } };
    private static OpenCodeClient Client(HttpClient http) => new(http,
        new() { BaseUrl = "https://offline.invalid/prefix", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static Dictionary<string, string> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
    private static OpenCodeModelRef Model(string id = "model_a", string provider = "provider_a") => new() { Id = id, ProviderId = provider };
    private static OpenCodeTokenUsage Tokens(double input = 10, double output = 20, double reasoning = 3, double read = 4, double write = 5) =>
        new() { Input = input, Output = output, Reasoning = reasoning, Cache = new() { Read = read, Write = write } };
    private static OpenCodeSessionInfo Session(string id, string? parent = null, double cost = 1) =>
        new() { Id = id, ParentId = parent, Cost = cost, Tokens = Tokens(), Model = Model() };
    private static bool Has(UsageTreeSummary summary, OpenCodeUsageIncompleteReason reason) => summary.IncompleteReasons.Any(issue => issue.Reason == reason);
    private static bool Stale(OpenCodeContextEstimateResult estimate, OpenCodeContextStaleReason reason) => estimate.StaleReasons.Contains(reason);

    private static async Task TreePagingAndCompleteness()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null, "read-only, no credentials fixture");
            paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/prefix/api/session/ses_root")
                return Task.FromResult(Json(new { data = Session("ses_root", cost: 10) }));
            Check(request.RequestUri.AbsolutePath == "/prefix/api/session", "tree never calls messages/config/ACP/model");
            var query = Query(request);
            Check(query.Count == 3 && query["limit"] == "50", "only parentID, limit and order/cursor; no directory filter");
            Check(query.ContainsKey("cursor") != query.ContainsKey("order"), "order and cursor mutually exclusive");
            if (query.TryGetValue("order", out var order)) Check(order == "desc", "first page desc");
            var parent = query["parentID"];
            var cursor = query.GetValueOrDefault("cursor");
            object page = (parent, cursor) switch
            {
                ("ses_root", null) => Page(new[] { Session("ses_a", "ses_root", 2) }, "opaque +/&=one"),
                // A partial overlap flags incomplete but still discovers the next unique descendant.
                ("ses_root", "opaque +/&=one") => Page(new[] { Session("ses_a", "ses_root", 200), Session("ses_b", "ses_root", 3) }, "end-root"),
                ("ses_root", "end-root") => Page(Array.Empty<OpenCodeSessionInfo>(), "ignored-empty-page"),
                ("ses_a", null) => Page(new[] { Session("ses_c", "ses_a", 4) }, "end-a"),
                ("ses_a", "end-a") => Page(Array.Empty<OpenCodeSessionInfo>()),
                ("ses_b", null) => Page(Array.Empty<OpenCodeSessionInfo>()),
                ("ses_c", null) => Page(Array.Empty<OpenCodeSessionInfo>()),
                _ => throw new InvalidOperationException("unexpected query"),
            };
            return Task.FromResult(Json(page));
        }));
        using var client = Client(http);
        var snapshot = await OpenCodeUsageService.ReadTreeAsync(client, "ses_root");
        var tree = snapshot.Summary;
        Check(tree.SessionCount == 4 && tree.TotalCost == 19 && tree.RootCost == 10, "each session once, recursive grandchild counted");
        Check(tree.Tokens.Total == 168, "five disjoint token categories for four sessions");
        Check(Has(tree, OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle), "overlap is not silently treated as complete");
        Check(paths.Count == 8, "short page continues; empty page ends; recurse all children");
        Check(snapshot.SampledAt >= snapshot.StartedAt, "non-atomic snapshot interval recorded");

        async Task<UsageTreeSummary> Scenario(Func<string, string?, HttpResponseMessage> page, OpenCodeUsageBudget? budget = null)
        {
            using var fake = new HttpClient(new Handler((request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/ses_root", StringComparison.Ordinal))
                    return Task.FromResult(Json(new { data = Session("ses_root") }));
                var query = Query(request);
                return Task.FromResult(page(query["parentID"], query.GetValueOrDefault("cursor")));
            }));
            using var transport = Client(fake);
            return (await OpenCodeUsageService.ReadTreeAsync(transport, "ses_root", budget)).Summary;
        }

        var complete = await Scenario((parent, cursor) => Json(parent == "ses_root" && cursor is null
            ? Page(new[] { Session("ses_child", "ses_root") }, "end") : Page(Array.Empty<OpenCodeSessionInfo>())));
        Check(!complete.Incomplete && complete.SessionCount == 2, "clean finite tree complete");
        var cycle = await Scenario((parent, _) => Json(Page(new[]
        {
            parent == "ses_root" ? Session("ses_child", "ses_root") : Session("ses_root", "ses_child", 999),
        })));
        Check(cycle.SessionCount == 2 && cycle.TotalCost == 2 && Has(cycle, OpenCodeUsageIncompleteReason.RepeatedSessionOrCycle), "cycle stops without counting root twice");
        var repeated = await Scenario((parent, cursor) => Json(parent == "ses_root"
            ? Page(new[] { Session("ses_a", "ses_root") }, cursor is null ? "first" : "second") : Page(Array.Empty<OpenCodeSessionInfo>())));
        Check(Has(repeated, OpenCodeUsageIncompleteReason.RepeatedPage) && repeated.SessionCount == 2, "repeated full page bounded even if cursor supplied");
        var repeatedCursor = await Scenario((parent, cursor) => Json(parent == "ses_root"
            ? Page(new[] { Session(cursor is null ? "ses_a" : "ses_b", parent) }, "same") : Page(Array.Empty<OpenCodeSessionInfo>())));
        Check(Has(repeatedCursor, OpenCodeUsageIncompleteReason.RepeatedPage) && repeatedCursor.SessionCount == 3, "repeated cursor stops even with unique data");
        var fork = await Scenario((parent, _) => Json(parent == "ses_root"
            ? Page(new[] { Session("ses_fork", null, 999), Session("ses_child", parent) }) : Page(Array.Empty<OpenCodeSessionInfo>())));
        Check(fork.TotalCost == 2 && fork.SessionCount == 2 && Has(fork, OpenCodeUsageIncompleteReason.ParentMismatch), "fork and wrong-parent rows excluded");
        var failure = await Scenario((parent, _) => parent == "ses_root"
            ? Json(Page(new[] { Session("ses_a", parent), Session("ses_b", parent) }))
            : parent == "ses_a" ? new(HttpStatusCode.ServiceUnavailable) : Json(Page(Array.Empty<OpenCodeSessionInfo>())));
        Check(failure.SessionCount == 3 && Has(failure, OpenCodeUsageIncompleteReason.ReadFailed), "branch failure preserves known subtotal");
        var sessionBudget = await Scenario((parent, _) => Json(Page(new[] { Session("ses_a", parent), Session("ses_b", parent) })), new(2, 20));
        Check(sessionBudget.SessionCount == 2 && Has(sessionBudget, OpenCodeUsageIncompleteReason.SessionBudgetExhausted), "session budget includes root");
        var pageBudget = await Scenario((parent, _) => Json(Page(new[] { Session("ses_a", parent) }, "next")), new(20, 1));
        Check(pageBudget.SessionCount == 2 && Has(pageBudget, OpenCodeUsageIncompleteReason.PageBudgetExhausted), "page budget global and exact");
        foreach (var bad in new[] { "", " ", "\n", new string('x', 4097) })
        {
            var invalid = await Scenario((parent, _) => Json(Page(new[] { Session("ses_a", parent) }, bad)));
            Check(Has(invalid, OpenCodeUsageIncompleteReason.InvalidCursor), "invalid cursor not treated as end");
        }
        foreach (var malformed in new[] { "[]", "{}", "{\"data\":[]}", "{\"data\":[null],\"cursor\":{\"next\":null}}",
            "{\"data\":[{\"id\":\"ses_a\",\"parentID\":\"ses_root\"}],\"cursor\":{\"next\":null}}" })
        {
            var invalid = await Scenario((_, _) => Raw(malformed));
            Check(Has(invalid, OpenCodeUsageIncompleteReason.ReadFailed), "malformed page cannot silently default required usage to zero");
        }
        using var missingRootHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var missingRootClient = Client(missingRootHttp);
        var unavailable = (await OpenCodeUsageService.ReadTreeAsync(missingRootClient, "ses_root")).Summary;
        Check(unavailable.RootCost is null && unavailable.SessionCount == 0 && Has(unavailable, OpenCodeUsageIncompleteReason.RootUnavailable), "failed root unknown, not free");
        using var missingCostHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Raw("""
            {"data":{"id":"ses_root","tokens":{"input":1,"output":2,"reasoning":0,"cache":{"read":0,"write":0}}}}
            """))));
        using var missingCostClient = Client(missingCostHttp);
        var missingCost = (await OpenCodeUsageService.ReadTreeAsync(missingCostClient, "ses_root")).Summary;
        Check(missingCost.RootCost is null && Has(missingCost, OpenCodeUsageIncompleteReason.ReadFailed), "missing wire cost not defaulted to valid zero");
        var isolatedCalls = 0;
        using var isolatedHttp = new HttpClient(new Handler((request, _) =>
        {
            isolatedCalls++;
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/ses_root", StringComparison.Ordinal)
                ? new { data = Session("ses_root", cost: 99) } : Page(Array.Empty<OpenCodeSessionInfo>())));
        }));
        using var isolatedClient = Client(isolatedHttp);
        var otherSource = await OpenCodeUsageService.ReadTreeAsync(isolatedClient, "ses_root");
        await OpenCodeUsageService.ReadTreeAsync(isolatedClient, "ses_root");
        Check(otherSource.Summary.TotalCost == 99 && isolatedCalls == 4, "same session ID across clients and repeated reads never cached");
        using var cts = new CancellationTokenSource();
        using var cancelHttp = new HttpClient(new Handler((_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("cancellation did not propagate");
        }));
        using var cancelClient = Client(cancelHttp);
        try { await OpenCodeUsageService.ReadTreeAsync(cancelClient, "ses_root", cancellationToken: cts.Token); throw new InvalidOperationException("not cancelled"); }
        catch (OperationCanceledException exception) { Check(exception.CancellationToken == cts.Token, "caller cancellation preserved, no partial late result"); }
        Console.WriteLine("PASS group 1: tree dedup/cycle/fork, opaque pagination, malformed reads, budgets, cancellation.");
    }

    private static async Task TokensCostAndModelPrices()
    {
        var root = Session("ses_root", cost: 0);
        var child = Session("ses_child", "ses_root", 2);
        var summary = OpenCodeUsageAggregator.Aggregate("ses_root", new[] { child, Session("ses_fork", cost: 500), root, child with { Cost = 200 } }, Array.Empty<OpenCodeUsageIssue>());
        Check(summary.RootCost == 0 && summary.TotalCost == 2 && summary.SessionCount == 2, "pure aggregator counts first unique root tree samples only");
        Check(summary.Tokens == new OpenCodeUsageTokens(20, 40, 6, 8, 10) && summary.Tokens.Total == 84, "no inclusive input/output double count");
        Check(summary.Tokens.PromptTotal == 38 && Math.Abs(summary.Tokens.CacheHitPercent!.Value - 800d / 38) < 1e-9,
            "cache hit denominator is uncached input + cache read + write, never output or reasoning");
        Check(OpenCodeUsageTokens.Zero.CacheHitPercent is null, "zero prompt has no invented hit rate");
        Check(OpenCodeUsageTokens.From(new OpenCodeTokenUsage { Cache = new() { Read = 0, Write = 0 } }) is null,
            "missing required usage counters never become zero");
        Check(summary.Sessions.Single(item => item.SessionId == "ses_child").CoreCost == 2, "per-session details retained without raw metadata/body");
        Check(summary.Label == "Core 累计估算" && !summary.ZeroCostProvesFree && !summary.HistoricalPricingCoverageKnown, "missing price zero never asserted free");
        Check(!summary.CoversExternalAcpBilling && !summary.CanAddBillionBilling, "billing scopes cannot be merged");
        Check(summary.Incomplete, "pure duplicate reports incomplete");
        var missing = OpenCodeUsageAggregator.Aggregate("ses_root", new[] { root with { Tokens = null, Cost = double.NaN } }, Array.Empty<OpenCodeUsageIssue>());
        Check(missing.RootCost is null && Has(missing, OpenCodeUsageIncompleteReason.InvalidUsage), "invalid numeric usage remains unknown");
        var overflow = OpenCodeUsageAggregator.Aggregate("ses_root", new[] { root with { Cost = double.MaxValue }, child with { Cost = double.MaxValue } }, Array.Empty<OpenCodeUsageIssue>());
        Check(double.IsFinite(overflow.TotalCost) && Has(overflow, OpenCodeUsageIncompleteReason.NumericOverflow), "no non-finite totals");

        using var http = new HttpClient(new Handler((request, _) =>
        {
            Check(request.RequestUri!.AbsolutePath == "/prefix/api/model" && request.Method == HttpMethod.Get, "exact read-only model endpoint");
            Check(Query(request).Count == 1 && Query(request)["location[directory]"] == "/work +/模型", "model directory deepObject escaped, not config");
            return Task.FromResult(Raw("""
                {"location":{"directory":"/work +/模型"},"data":[
                 {"id":"model_a","providerID":"provider_a","name":"A","limit":{"context":200000,"output":8000},"cost":[
                  {"input":2,"output":10,"cache":{"read":0.2,"write":2.5}},
                  {"tier":{"type":"context","size":200000},"input":4,"output":20,"cache":{"read":0.8,"write":6}}]},
                 {"id":"missing","providerID":"provider_a","cost":[{"input":2,"cache":{"read":0.2}}]},
                 {"id":"zero","providerID":"provider_a","cost":[{"input":0,"cache":{"read":0,"write":0}}]}]}
                """));
        }));
        using var client = Client(http);
        var models = await client.ListModelsAsync("/work +/模型");
        Check(models.Data!.Count == 3 && models.Data[0].Limit?.Context == 200000, "model limits and array tiers decoded");
        var baseRatio = OpenCodeUsageAggregator.DeriveCacheMultipliers(models.Data[0], 0);
        var tierRatio = OpenCodeUsageAggregator.DeriveCacheMultipliers(models.Data[0], 1);
        Check(baseRatio is { Read: 0.1, Write: 1.25, Tier: null, Label: "派生倍率" }, "base ratios from absolute prices");
        Check(tierRatio is { Read: 0.2, Write: 1.5, Tier.Size: 200000, CostIndex: 1 }, "same-tier denominator, not base input price");
        Check(tierRatio!.ProviderId == "provider_a" && tierRatio.ModelId == "model_a", "ratios retain model and provider applicability");
        var partial = OpenCodeUsageAggregator.DeriveCacheMultipliers(models.Data[1], 0);
        Check(partial is { Read: 0.1, Write: null } && models.Data[1].Limit is null, "missing cache write price/limit remain unknown");
        Check(OpenCodeUsageAggregator.DeriveCacheMultipliers(models.Data[2], 0) is null, "zero input denominator cannot derive ratios");
        Check(OpenCodeUsageAggregator.DeriveCacheMultipliers(models.Data[0], 2) is null, "no invented tier");
        var badPrice = models.Data[0] with { Cost = new() { new() { Input = double.NaN, Cache = new() { Read = 1 } } } };
        Check(OpenCodeUsageAggregator.DeriveCacheMultipliers(badPrice, 0) is null, "non-finite price never derived");
        var attached = OpenCodeUsageAggregator.Aggregate("ses_root", new[] { root }, Array.Empty<OpenCodeUsageIssue>(), tierRatio);
        Check(attached.ReferenceCacheMultipliers == tierRatio && attached.TotalCost == 0 && !attached.ZeroCostProvesFree, "reference tier never reprices historical costs");
        Console.WriteLine("PASS group 2: five disjoint tokens, Session.Info-only cost, missing prices, same-tier derived ratios, model HTTP.");
    }

    private static void ContextReferenceAndStaleness()
    {
        var current = Model();
        var modelInfo = new OpenCodeModelInfo { Id = current.Id, ProviderId = current.ProviderId, Limit = new() { Context = 200000 } };
        OpenCodeMessage Assistant(string id, double time) => new()
        {
            Id = id, Type = OpenCodeMessageTypes.Assistant, Model = current, Time = new() { Created = time }, Tokens = Tokens(), Cost = 999,
        };
        var latest = Assistant("msg_latest", 20);
        var older = Assistant("msg_older", 10) with { Tokens = Tokens(10000) };
        var failed = Assistant("msg_failed", 30) with { Error = new() { Type = "error", Message = "synthetic" } };
        var result = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest, older, failed }, current, modelInfo, true);
        Check(!result.Stale && !result.IsExact && result.ContextLimit == 200000, "fresh reference still never exact");
        Check(result.ReferenceTokens == 42 && result.LatestAssistantUsage?.MessageId == "msg_latest", "latest successful assistant only, not cumulative tokens or error usage");
        var changedModel = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, Model("model_b"), modelInfo, true);
        Check(Stale(changedModel, OpenCodeContextStaleReason.ModelChanged) && changedModel.ContextLimit is null, "same provider model change stale, wrong-model limit not reused");
        var changedProvider = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, Model(provider: "provider_b"), modelInfo, true);
        Check(Stale(changedProvider, OpenCodeContextStaleReason.ProviderChanged), "provider change stale");
        var changedVariant = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, current with { Variant = "high" }, modelInfo, true);
        Check(Stale(changedVariant, OpenCodeContextStaleReason.ModelChanged), "variant change conservatively stale");
        var compacted = new OpenCodeMessage { Type = OpenCodeMessageTypes.Compaction, Time = new() { Created = 21 }, Status = OpenCodeCompactionStatuses.Completed, Tokens = Tokens(9000) };
        var afterCompaction = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest, compacted }, current, modelInfo, true);
        Check(Stale(afterCompaction, OpenCodeContextStaleReason.CompactionAfterUsage) && afterCompaction.ReferenceTokens == 42, "compaction marks old reference stale, compaction tokens not added");
        var afterFreshUsage = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest, compacted, Assistant("msg_fresh", 22) }, current, modelInfo, true);
        Check(!afterFreshUsage.Stale && afterFreshUsage.LatestAssistantUsage?.MessageId == "msg_fresh", "new assistant after compaction refreshes reference");
        var hiddenBoundary = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, current, modelInfo, true, 21);
        Check(Stale(hiddenBoundary, OpenCodeContextStaleReason.CompactionAfterUsage), "caller boundary invalidates even when compaction message absent");
        var switched = new OpenCodeMessage { Type = OpenCodeMessageTypes.ModelSwitched, Model = current, Time = new() { Created = 21 } };
        var switchedBack = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest, switched }, current, modelInfo, true);
        Check(Stale(switchedBack, OpenCodeContextStaleReason.ModelSwitchAfterUsage), "switch away and back does not revive old usage");
        var incomplete = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, current, modelInfo, false);
        Check(Stale(incomplete, OpenCodeContextStaleReason.HistoryIncomplete), "incomplete history cannot prove fresh boundary");
        var unavailable = OpenCodeContextEstimate.Estimate("ses_root", Array.Empty<OpenCodeMessage>(), current, modelInfo, true);
        Check(unavailable.ReferenceTokens is null && Stale(unavailable, OpenCodeContextStaleReason.NoAssistantUsage), "no usage unknown not zero");
        var unknownBoundary = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest, compacted with { Time = null } }, current, modelInfo, true);
        Check(Stale(unknownBoundary, OpenCodeContextStaleReason.UnknownBoundary), "undated compaction conservatively stale");

        foreach (var type in new[] { OpenCodeMessageTypes.Compaction, OpenCodeMessageTypes.ModelSwitched })
        {
            foreach (var timeJson in new[] { "{}", "{\"created\":0}" })
            {
                var boundaryMessage = JsonSerializer.Deserialize<OpenCodeMessage>($$"""
                    {"id":"msg_boundary","type":"{{type}}","status":"completed",
                     "model":{"id":"model_a","providerID":"provider_a"},"time":{{timeJson}}}
                    """)!;
                Check(boundaryMessage.Time is { Created: 0 }, "wire missing/epoch-zero created retains frozen DTO default");
                var ambiguous = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest, boundaryMessage }, current, modelInfo, true);
                Check(ambiguous.Stale && Stale(ambiguous, OpenCodeContextStaleReason.UnknownBoundary)
                    && ambiguous.LatestAssistantUsage?.MessageId == "msg_latest", "ambiguous boundary cannot prove it predates positive assistant usage");
            }
            var earlier = OpenCodeContextEstimate.Estimate("ses_root", new[]
            {
                latest, new OpenCodeMessage { Type = type, Time = new() { Created = 19 }, Status = OpenCodeCompactionStatuses.Completed },
            }, current, modelInfo, true);
            Check(!earlier.Stale, "positively dated earlier compaction/model switch preserves fresh reference");
        }
        var zeroBoundary = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, current, modelInfo, true, 0);
        Check(zeroBoundary.Stale && Stale(zeroBoundary, OpenCodeContextStaleReason.UnknownBoundary), "caller epoch-zero boundary is unknown, not proven earlier");
        var earlierBoundary = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, current, modelInfo, true, 19);
        Check(!earlierBoundary.Stale, "positively dated earlier caller boundary remains fresh");
        var equalBoundary = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest }, current, modelInfo, true, 20);
        Check(Stale(equalBoundary, OpenCodeContextStaleReason.CompactionAfterUsage), "equal caller boundary remains conservatively stale");
        foreach (var timeJson in new[] { "{}", "{\"created\":0}" })
        {
            var ambiguousAssistant = JsonSerializer.Deserialize<OpenCodeMessage>($$"""
                {"id":"msg_ambiguous","type":"assistant","model":{"id":"model_a","providerID":"provider_a"},
                 "time":{{timeJson}},"tokens":{"input":10,"output":20,"reasoning":3,"cache":{"read":4,"write":5} } }
                """)!;
            Check(ambiguousAssistant.Time is { Created: 0 }, "assistant wire missing/epoch-zero created is ambiguous");
            var alone = OpenCodeContextEstimate.Estimate("ses_root", new[] { ambiguousAssistant }, current, modelInfo, true);
            Check(alone.Stale && Stale(alone, OpenCodeContextStaleReason.UnknownBoundary)
                && Stale(alone, OpenCodeContextStaleReason.NoAssistantUsage) && alone.ReferenceTokens is null,
                "ambiguous assistant timestamp cannot anchor a fresh reference");
            var mixed = OpenCodeContextEstimate.Estimate("ses_root", new[] { ambiguousAssistant, latest }, current, modelInfo, true);
            Check(mixed.Stale && Stale(mixed, OpenCodeContextStaleReason.UnknownBoundary)
                && mixed.LatestAssistantUsage?.MessageId == "msg_latest", "ambiguous assistant cannot silently sort before known usage");
        }
        var zeroTokens = OpenCodeContextEstimate.Estimate("ses_root", new[] { latest with { Tokens = Tokens(0, 0, 0, 0, 0) } }, current, modelInfo, true);
        Check(!zeroTokens.Stale && zeroTokens.ReferenceTokens == 0, "valid zero token counts remain fresh with known timestamp");
        Console.WriteLine("PASS group 3: latest assistant-only reference, model/provider/variant/switch-back/compaction stale rules, incomplete history.");
    }
}
