using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

// Headless verification for the exact single-session read (GET api/session/{sessionID}).
// Fake HTTP only: no live server, no list scan, no navigation/ancestor traversal.
internal static class Program
{
    private static int assertions;

    private static void Main() => Run().GetAwaiter().GetResult();

    private static async Task Run()
    {
        await ExactPathAndParentPreserved();
        await InvalidIdRejectedWithoutRequest();
        await ErrorAndCancellationDecodedWithoutLeak();
        await AncestryDirectAndNonRelative();
        await AncestryCycleBudgetAndErrors();
        Console.WriteLine($"PASS: 5 session-info scenarios, {assertions} assertions; fake HTTP only; no live server.");
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

    private static OpenCodeClient Client(HttpClient http) =>
        new(http, new() { BaseUrl = "https://offline.invalid", Secret = "tok" }, true);

    private static object Session(string id, string? parentId) => new
    {
        id,
        parentID = parentId,
        projectID = "prj_1",
        cost = 1.5,
        tokens = new { input = 1, output = 2, reasoning = 0, cache = new { read = 0, write = 0 } },
        time = new { created = 1, updated = 2 },
        location = new { directory = "/work" },
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static async Task<TException> Expect<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception other)
        {
            throw new InvalidOperationException($"{message}: got {other.GetType().Name}");
        }

        throw new InvalidOperationException($"{message}: no exception thrown");
    }

    private static async Task ExactPathAndParentPreserved()
    {
        string? method = null, path = null, query = null, auth = null;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            method = request.Method.Method;
            path = request.RequestUri!.AbsolutePath;
            query = request.RequestUri.Query;
            auth = request.Headers.Authorization?.ToString();
            return Task.FromResult(Json(new { data = Session("ses_child", "ses_parent") }));
        }));
        using var client = Client(http);
        var info = await client.GetSessionInfoAsync("ses_child");
        Check(method == "GET", "single-session read is a GET");
        Check(path == "/api/session/ses_child", "exact id path, not a list scan");
        Check(string.IsNullOrEmpty(query), "no query parameters on the exact read");
        Check(auth == "Bearer tok", "existing per-request Authorization is reused");
        Check(info.Id == "ses_child" && info.ParentId == "ses_parent", "id and parentID preserved verbatim");

        // Root session: an absent parentID must stay null, never an invented ancestor.
        using var rootHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { data = Session("ses_root", null) }))));
        using var rootClient = Client(rootHttp);
        var root = await rootClient.GetSessionInfoAsync("ses_root");
        Check(root.Id == "ses_root" && root.ParentId is null, "root session keeps null parentID");
    }

    private static async Task InvalidIdRejectedWithoutRequest()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(new { data = Session("ses_child", null) }));
        }));
        using var client = Client(http);
        foreach (var bad in new[] { "", "   ", "not-a-session", "ses", "ses/x" })
        {
            var rejected = await Expect<ArgumentException>(() => client.GetSessionInfoAsync(bad), $"invalid id '{bad}' rejected");
            Check(rejected is not null, $"invalid id '{bad}' raises ArgumentException");
        }

        Check(requests == 0, "invalid ids are rejected before any HTTP request");
    }

    private static async Task ErrorAndCancellationDecodedWithoutLeak()
    {
        const string echoed = "ses_child?token=SECRETVALUE";
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(echoed, Encoding.UTF8, "application/json"),
        })));
        using var client = Client(http);
        var api = await Expect<OpenCodeApiException>(() => client.GetSessionInfoAsync("ses_child"), "404 decoded as OpenCodeApiException");
        Check(api.StatusCode == 404, "HTTP status is decoded");
        Check(!api.Message.Contains("ses_child") && !api.Message.Contains("SECRETVALUE") && !api.Message.Contains("token"),
            "error message carries no echoed id/credential/body");
        Check(api.ErrorTag is null && api.ServerMessage is null, "error body is not read or retained");

        using var mismatchHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { data = Session("ses_other", "ses_parent") }))));
        using var mismatchClient = Client(mismatchHttp);
        await Expect<OpenCodeProtocolException>(() => mismatchClient.GetSessionInfoAsync("ses_child"), "mismatched response id is a protocol error");

        using var badParentHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { data = Session("ses_child", "root") }))));
        using var badParentClient = Client(badParentHttp);
        await Expect<OpenCodeProtocolException>(() => badParentClient.GetSessionInfoAsync("ses_child"), "non-ses parentID is a protocol error");

        using var cancelHttp = new HttpClient(new Handler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Json(new { data = Session("ses_child", null) }));
        }));
        using var cancelClient = Client(cancelHttp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await Expect<OperationCanceledException>(() => cancelClient.GetSessionInfoAsync("ses_child", cts.Token), "caller cancellation preserved");
        Check(cancelled.CancellationToken == cts.Token, "cancellation carries the caller token");
    }

    // Fake HTTP session graph: GET /api/session/{id} answers with the mapped parentID (or 404 when unknown),
    // so the ancestry walk is exercised only over the exact single-session read (no list scan).
    private static HttpClient Graph(IReadOnlyDictionary<string, string?> parents, int[] requests) =>
        new(new Handler((request, _) =>
        {
            requests[0]++;
            var id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            return parents.TryGetValue(id, out var parent)
                ? Task.FromResult(Json(new { data = Session(id, parent) }))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));

    private static async Task AncestryDirectAndNonRelative()
    {
        var parents = new Dictionary<string, string?>
        {
            ["ses_child"] = "ses_parent",
            ["ses_parent"] = "ses_root",
            ["ses_root"] = null,
        };

        var requests = new int[1];
        using var http = Graph(parents, requests);
        using var client = Client(http);

        Check(await OpenCodeSessionAncestry.IsAncestorAsync(client, "ses_child", "ses_parent"),
            "direct child is authorized by the real parent chain");
        Check(requests[0] == 1, "direct child costs exactly one exact-session read (no list scan)");

        requests[0] = 0;
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(client, "ses_child", "ses_uncle"),
            "a non-relative expected parent is rejected");
        Check(requests[0] is >= 1 and <= OpenCodeSessionAncestry.MaxDepth, "non-relative walk stays within the depth budget");

        var deep = new Dictionary<string, string?>
        {
            ["ses_c"] = "ses_b",
            ["ses_b"] = "ses_a",
            ["ses_a"] = "ses_root",
            ["ses_root"] = null,
        };
        var deepRequests = new int[1];
        using var deepHttp = Graph(deep, deepRequests);
        using var deepClient = Client(deepHttp);
        Check(await OpenCodeSessionAncestry.IsAncestorAsync(deepClient, "ses_c", "ses_a"),
            "an intermediate real ancestor is authorized");
        Check(deepRequests[0] == 2, "an intermediate ancestor costs two exact reads");

        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(deepClient, "ses_root", "ses_a"),
            "a root candidate has no ancestors and is rejected");

        var cheapRequests = new int[1];
        using var cheapHttp = Graph(parents, cheapRequests);
        using var cheapClient = Client(cheapHttp);
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(cheapClient, "ses_child", "ses_child"),
            "candidate equal to expected is rejected");
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(cheapClient, "not-a-session", "ses_parent"),
            "a malformed candidate id can never be authorized");
        Check(cheapRequests[0] == 0, "self/malformed candidates issue no HTTP request");
    }

    private static async Task AncestryCycleBudgetAndErrors()
    {
        // Cycle ses_a -> ses_b -> ses_a with the expected ancestor absent: bounded, rejected.
        var cycle = new Dictionary<string, string?> { ["ses_a"] = "ses_b", ["ses_b"] = "ses_a" };
        var cycleRequests = new int[1];
        using var cycleHttp = Graph(cycle, cycleRequests);
        using var cycleClient = Client(cycleHttp);
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(cycleClient, "ses_a", "ses_absent"),
            "a cyclic chain never authorizes a missing ancestor");
        Check(cycleRequests[0] < OpenCodeSessionAncestry.MaxDepth, "cycle detection stops before exhausting the budget");

        // 40-link linear chain without the expected ancestor: must stop exactly at MaxDepth.
        var longChain = new Dictionary<string, string?>();
        for (var i = 0; i < 40; i++) longChain["ses_" + i] = i == 39 ? null : "ses_" + (i + 1);
        var budgetRequests = new int[1];
        using var budgetHttp = Graph(longChain, budgetRequests);
        using var budgetClient = Client(budgetHttp);
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(budgetClient, "ses_0", "ses_missing"),
            "an over-deep chain is rejected at the depth budget");
        Check(budgetRequests[0] == OpenCodeSessionAncestry.MaxDepth, "the walk stops exactly at MaxDepth, never unbounded");

        // Failure mid-chain: the first hop resolves, the next lookup 404s -> no positive verdict.
        var broken = new Dictionary<string, string?> { ["ses_child"] = "ses_gone" };
        var errorRequests = new int[1];
        using var errorHttp = Graph(broken, errorRequests);
        using var errorClient = Client(errorHttp);
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(errorClient, "ses_child", "ses_never"),
            "a failed read is never treated as ancestry");
        Check(errorRequests[0] == 2, "the walk stops at the first failed read");

        // A malformed parentID surfaces as a protocol error, i.e. not ancestry.
        using var badParentHttp = new HttpClient(new Handler((_, _) =>
            Task.FromResult(Json(new { data = Session("ses_child", "root") }))));
        using var badParentClient = Client(badParentHttp);
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(badParentClient, "ses_child", "ses_root"),
            "a non-ses parentID is rejected as not ancestry");

        // No caching: a failed attempt must be re-queried, not remembered as a positive conclusion.
        var flaky = new int[1];
        using var flakyHttp = new HttpClient(new Handler((request, _) =>
        {
            flaky[0]++;
            if (flaky[0] == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var id = request.RequestUri!.AbsolutePath.Split('/')[^1];
            return Task.FromResult(Json(new { data = Session(id, id == "ses_child" ? "ses_parent" : null) }));
        }));
        using var flakyClient = Client(flakyHttp);
        Check(!await OpenCodeSessionAncestry.IsAncestorAsync(flakyClient, "ses_child", "ses_parent"),
            "the first failed attempt is not authorized");
        Check(await OpenCodeSessionAncestry.IsAncestorAsync(flakyClient, "ses_child", "ses_parent"),
            "a later successful attempt is re-queried, not served from a cache");

        // Caller cancellation propagates as OperationCanceledException, never as a false verdict.
        using var cancelHttp = new HttpClient(new Handler((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Json(new { data = Session("ses_child", "ses_parent") }));
        }));
        using var cancelClient = Client(cancelHttp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await Expect<OperationCanceledException>(
            () => OpenCodeSessionAncestry.IsAncestorAsync(cancelClient, "ses_child", "ses_parent", cts.Token),
            "ancestry walk preserves caller cancellation");
        Check(cancelled.CancellationToken == cts.Token, "ancestry cancellation carries the caller token");
    }
}
