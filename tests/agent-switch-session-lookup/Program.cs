using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

// Headless verification for AgentSwitchCoordinator's session lookup: the exact single-session read
// (GET api/session/{sessionID}) must replace any bounded list scan, so a session outside the first
// listed pages can still be switched, while 404 / protocol mismatch / cancellation never become
// success and never start a mutation. Fake HTTP only: no live server, no list scan, no UI.
internal static class Program
{
    private static int assertions;
    private const string SessionId = "ses_target";
    private const string TargetAgent = "build";
    private const string OtherAgent = "plan";
    private const string Directory = "/work";

    private static void Main() => Run().GetAwaiter().GetResult();

    private static async Task Run()
    {
        await ExactReadSwitchesSessionOutsideAnyList();
        await MissingSessionIsBlockedWithoutMutation();
        await MismatchedSessionIsBlockedWithoutMutation();
        await CancellationDuringReadNeverBecomesSuccess();
        await OpaqueRecoveryCursorRemainsUsable();
        await ExplicitReconciliationDoesNotReplayMutation();
        Console.WriteLine($"PASS: 6 agent-switch session-lookup scenarios, {assertions} assertions; fake HTTP only; no live server.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Recorder
    {
        public List<string> Requests { get; } = new();

        public void Add(HttpRequestMessage request) =>
            Requests.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);

        public int Count(string methodAndPath) => Requests.FindAll(r => r == methodAndPath).Count;
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static object Session(string id, string? parentId, string agent) => new
    {
        id,
        parentID = parentId,
        projectID = "prj_1",
        agent,
        cost = 0,
        tokens = new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } },
        time = new { created = 1, updated = 2 },
        location = new { directory = Directory },
    };

    private static object Agent(string id) => new
    {
        id,
        name = id,
        mode = "primary",
        request = new { },
        permissions = new { },
        hidden = false,
    };

    private static object Agents() => new
    {
        location = new { directory = Directory },
        data = new[] { Agent(OtherAgent), Agent(TargetAgent) },
    };

    private static object EmptyPage() => new
    {
        data = Array.Empty<object>(),
        cursor = new { previous = (string?)null, next = (string?)null },
    };

    private static object EmptyList() => new { data = Array.Empty<object>() };

    private static object EmptyActive() => new { data = new Dictionary<string, object>() };

    // Deterministic fake backend: the exact read answers for SessionId (unless overridden); every other
    // endpoint the coordinator consults for an idle switchable session answers with an empty snapshot.
    private static HttpResponseMessage Route(Recorder recorder, HttpRequestMessage request,
        Func<HttpResponseMessage?>? overrideSession)
    {
        recorder.Add(request);
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/api/session/" + SessionId + "/agent")
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        if (request.Method == HttpMethod.Get && path == "/api/session/" + SessionId)
            return overrideSession?.Invoke() ?? Json(new { data = Session(SessionId, null, OtherAgent) });
        if (request.Method == HttpMethod.Get && path == "/api/agent") return Json(Agents());
        if (request.Method == HttpMethod.Get && path == "/api/session/" + SessionId + "/message") return Json(EmptyPage());
        if (request.Method == HttpMethod.Get && path == "/api/session/" + SessionId + "/permission") return Json(EmptyList());
        if (request.Method == HttpMethod.Get && path == "/api/session/" + SessionId + "/form") return Json(EmptyList());
        if (request.Method == HttpMethod.Get && path == "/api/session/" + SessionId + "/inbox") return Json(EmptyList());
        if (request.Method == HttpMethod.Get && path == "/api/session/active") return Json(EmptyActive());
        // A session LIST request (GET /api/session) must never occur; fail loudly if the coordinator issues one.
        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(send(request));
    }

    private static AgentSwitchResult Switch(Recorder recorder, Func<HttpResponseMessage?>? overrideSession = null,
        Action? onSessionRead = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/session/" + SessionId)
                onSessionRead?.Invoke();
            return Route(recorder, request, overrideSession);
        }));
        using var client = new OpenCodeClient(http,
            new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "tok" }, true);
        var source = new OpenCodePageSource("ep_1", 7L, () => client);
        var origin = new AgentSwitchOrigin(source, 7L, SessionId, 1L);
        var coordinator = new AgentSwitchCoordinator(new OpenCodeInteractionGate());
        return coordinator.SwitchAsync(origin, Guid.NewGuid(), TargetAgent, _ => true, cancellationToken)
            .GetAwaiter().GetResult();
    }

    private static Task ExactReadSwitchesSessionOutsideAnyList()
    {
        var recorder = new Recorder();
        var result = Switch(recorder);
        Check(result.Status == AgentSwitchStatus.Applied, "switch applies through the exact single-session read");
        Check(result.MutationStarted, "mutation is recorded as started");
        Check(result.ObservedAgentId == TargetAgent, "observed agent is the applied target");
        Check(recorder.Count("GET /api/session/" + SessionId) == 1, "exactly one exact session read is issued");
        Check(recorder.Count("GET /api/session") == 0, "no session LIST request is issued (the old 4x100 scan is gone)");
        Check(recorder.Count("POST /api/session/" + SessionId + "/agent") == 1, "exactly one agent switch POST is issued");
        return Task.CompletedTask;
    }

    private static Task MissingSessionIsBlockedWithoutMutation()
    {
        var recorder = new Recorder();
        var result = Switch(recorder, () => new HttpResponseMessage(HttpStatusCode.NotFound));
        Check(result.Status == AgentSwitchStatus.Blocked, "a 404 exact read is reported as Blocked");
        Check(!result.MutationStarted, "a 404 exact read never starts a mutation");
        Check(recorder.Count("POST /api/session/" + SessionId + "/agent") == 0, "no agent switch POST after a 404 read");
        Check(recorder.Count("GET /api/session") == 0, "no fallback session LIST request after a 404 read");
        return Task.CompletedTask;
    }

    private static Task MismatchedSessionIsBlockedWithoutMutation()
    {
        var recorder = new Recorder();
        var result = Switch(recorder,
            () => Json(new { data = Session("ses_other", null, OtherAgent) }));
        Check(result.Status == AgentSwitchStatus.Blocked, "a mismatched exact read id is reported as Blocked");
        Check(!result.MutationStarted, "a mismatched exact read never starts a mutation");
        Check(recorder.Count("POST /api/session/" + SessionId + "/agent") == 0, "no agent switch POST after a mismatched read");
        return Task.CompletedTask;
    }

    private static Task CancellationDuringReadNeverBecomesSuccess()
    {
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        // Cancel the caller token exactly when the exact session read happens, then answer successfully:
        // the read must not be mistaken for success, and no mutation may start.
        var result = Switch(recorder, onSessionRead: cts.Cancel, cancellationToken: cts.Token);
        Check(result.Status == AgentSwitchStatus.OriginLost, "cancellation during the exact read is OriginLost, not success");
        Check(!result.MutationStarted, "cancellation during the exact read never starts a mutation");
        Check(recorder.Count("POST /api/session/" + SessionId + "/agent") == 0, "no agent switch POST after cancellation");
        return Task.CompletedTask;
    }

    private static async Task ExplicitReconciliationDoesNotReplayMutation()
    {
        var recorder = new Recorder();
        using var http = new HttpClient(new Handler(request => Route(recorder, request, null)));
        using var client = new OpenCodeClient(http, new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "tok" }, true);
        var source = new OpenCodePageSource("ep_reconcile", 7L, () => client);
        var gate = new OpenCodeInteractionGate();
        var operation = Guid.NewGuid();
        using (var lease = await gate.EnterAsync(source.Identity, SessionId))
        { lease.BeginAgentSwitch(operation, TargetAgent); lease.CompleteAgentSwitch(NotificationInteractionStatus.OutcomeUnknown); }
        var result = await new AgentSwitchCoordinator(gate).ReconcileAsync(new(source, 7L, SessionId, 1L), operation, _ => true);
        Check(result.Status == AgentSwitchStatus.Reconciled && !result.MutationStarted, "explicit reconciliation only settles the captured unknown operation");
        Check(recorder.Count("POST /api/session/" + SessionId + "/agent") == 0, "reconciliation never retries the mutation");
        using var reconciled = await gate.EnterAsync(source.Identity, SessionId);
        Check(reconciled.CanSwitchAgent() && reconciled.GetAgentSwitchProtection() is null, "later explicit switches may start after reconciliation");
    }

    private static async Task OpaqueRecoveryCursorRemainsUsable()
    {
        string cursor = new string('x', 180) + "+/=&? 游标";
        int pages = 0;
        var recorder = new Recorder();
        using var http = new HttpClient(new Handler(request => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/message"))
            {
                pages++;
                if (pages == 2)
                    Check(request.RequestUri.Query.Contains("cursor=" + Uri.EscapeDataString(cursor), StringComparison.Ordinal),
                        "agent-switch opaque cursor is URI escaped once, not constrained as an ID");
                return Json(new { data = pages == 1 ? new[] { new { id = "msg_old", sessionID = SessionId,
                    type = "idle", outcome = "succeeded", time = new { created = 1 } } } : Array.Empty<object>(),
                    cursor = new { next = pages == 1 ? cursor : null } });
            }
            return Route(recorder, request, null);
        }));
        using var client = new OpenCodeClient(http, new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "tok" }, true);
        var source = new OpenCodePageSource("ep_cursor", 7L, () => client);
        var gate = new OpenCodeInteractionGate(); var operation = Guid.NewGuid();
        using (var lease = await gate.EnterAsync(source.Identity, SessionId))
        { lease.BeginAgentSwitch(operation, TargetAgent); lease.CompleteAgentSwitch(NotificationInteractionStatus.OutcomeUnknown); }
        var result = await new AgentSwitchCoordinator(gate).ReconcileAsync(new(source, 7L, SessionId, 1L), operation, _ => true);
        Check(result.Status == AgentSwitchStatus.Reconciled && pages == 2, "explicit recovery still reads full history using opaque cursor");
        Check(recorder.Count("POST /api/session/" + SessionId + "/agent") == 0, "recovery cursor continuation never replays a mutation");
    }
}
