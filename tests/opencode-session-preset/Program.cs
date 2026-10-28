using System;
using System.Collections.Generic;
using System.IO;
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
    private const string Secret = "SESSION_PRESET_SECRET";
    private const string Session = "ses_requested";
    private const string Directory = "/workspace/项目 & next";

    private static async Task Main()
    {
        await ExactRpcEnvelopeAndLocation();
        await BoundUnboundAndNullableName();
        await RequestOwnershipAndExplicitRefresh();
        await UnsupportedAndUnavailableAreNotUnbound();
        await WrongSessionAndMalformedOutput();
        await CancellationAndTimeout();
        await TransportAndClientLifecycle();
        Console.WriteLine($"PASS: 7 session-preset scenarios, {assertions} assertions; production transport; fake HTTP only.");
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    {
        assertions++;
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Redacted(Exception error)
    {
        Check(!error.ToString().Contains(Secret, StringComparison.Ordinal)
            && !error.ToString().Contains(Directory, StringComparison.Ordinal)
            && !error.ToString().Contains(Session, StringComparison.Ordinal), "exception retains no request/body/credential data");
        Check(error.InnerException is null, "no raw transport or parse inner exception");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static OpenCodeClient Client(HttpClient http, TimeSpan? timeout = null) => new(http, new()
    {
        BaseUrl = "https://offline.invalid/prefix/api/",
        Secret = Secret,
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    }, transportSafetyConfirmed: true);

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static object State(string status = "bound", string? name = "backend-preset", string sessionId = Session)
        => new { sessionID = sessionId, status, presetName = name, affects = "future-children-only" };

    private static async Task ExactRpcEnvelopeAndLocation()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            Check(request.Method == HttpMethod.Post, "RPC read uses POST");
            Check(request.RequestUri!.AbsolutePath == "/prefix/api/rpc/oh-my-opencode-slim.session-preset.v1/read",
                "real OMO RPC ID and client base prefix, not desktop bridge");
            Check(request.RequestUri.Query == "?location%5Bdirectory%5D=" + Uri.EscapeDataString(Directory), "exact deepObject location");
            Check(request.Headers.Authorization?.ToString() == "Bearer " + Secret, "source authentication reused per request");
            Check(request.Content!.Headers.ContentType?.MediaType == "application/json", "JSON payload");
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var root = json.RootElement;
            Check(root.EnumerateObject().Count() == 1 && root.TryGetProperty("input", out _), "actual input envelope only");
            var input = root.GetProperty("input");
            Check(input.EnumerateObject().Count() == 1 && input.GetProperty("sessionID").GetString() == Session,
                "strict read input contains only sessionID");
            return Json(new { output = State() });
        }));
        using var client = Client(http);
        var state = await client.GetSessionPresetAsync(Session, Directory);
        Check(calls == 1, "no handshake, probe, retry or global preset read");
        Check(http.DefaultRequestHeaders.Authorization is null, "authentication not written to shared defaults");
        Check(state.SessionId == Session && state.Directory == Directory && state.PresetName == "backend-preset", "backend fact and request scope preserved");
        Check(state.Status == OpenCodeSessionPresetStatus.Bound && state.Affects == "future-children-only", "bound and affects preserved");
    }

    private static async Task BoundUnboundAndNullableName()
    {
        foreach (var (status, name) in new (string, string?)[] { ("bound", "named"), ("unbound", null), ("bound", null), ("bound", "") })
        {
            using var http = new HttpClient(new Handler((request, _) =>
            {
                Check(request.RequestUri!.Query == "", "null directory uses source default without a query");
                return Task.FromResult(Json(new { output = State(status, name) }));
            }));
            using var client = Client(http);
            var state = await client.GetSessionPresetAsync(Session);
            Check(state.Status == (status == "bound" ? OpenCodeSessionPresetStatus.Bound : OpenCodeSessionPresetStatus.Unbound), "status never inferred from name");
            Check(state.PresetName == name, "null/empty name is not replaced with a global/default preset");
        }
    }

    private static async Task RequestOwnershipAndExplicitRefresh()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new
        {
            output = ++calls == 1 ? State("unbound", null) : State("bound", "fresh"),
        }))));
        using var client = Client(http);
        using var replacement = Client(http); // Same URL/auth still belongs to a different source client lifetime.
        var first = await client.GetSessionPresetAsync(Session, Directory);
        var second = await client.GetSessionPresetAsync(Session, Directory);
        Check(first.Status == OpenCodeSessionPresetStatus.Unbound && second.PresetName == "fresh" && calls == 2,
            "each explicit read refreshes; unbound is not cached");
        Check(first.IsFor(client, Session, Directory), "snapshot bound to requesting client/session/location");
        Check(!first.IsFor(replacement, Session, Directory), "same server URL is not source ownership");
        Check(!first.IsFor(client, "ses_other", Directory), "session ownership exact");
        Check(!first.IsFor(client, Session, Directory + "/") && !first.IsFor(client, Session), "location ownership exact, default is distinct");
        Check(!first.IsFor(null, Session, Directory), "missing client cannot own a snapshot");
    }

    private static async Task UnsupportedAndUnavailableAreNotUnbound()
    {
        foreach (var (status, code, unsupported, unavailable) in new[]
        {
            (404, (string?)null, true, true),
            (400, "rpc.method_not_found", true, true),
            (400, "rpc.unavailable", false, true),
            (400, "rpc.invalid_input", false, false),
            (500, "rpc.invalid_output", false, false),
            (500, "rpc.internal", false, false),
            (401, (string?)null, false, false),
            (403, (string?)null, false, false),
            (400, Secret, false, false),
        })
        {
            int calls = 0;
            using var http = new HttpClient(new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(Json(new { _tag = status == 500 ? "RpcInternalError" : "RpcError", type = code,
                    message = Secret + Directory + Session, data = new { secret = Secret } }, (HttpStatusCode)status));
            }));
            using var client = Client(http);
            var error = await Expect<OpenCodeSessionPresetRpcException>(() => client.GetSessionPresetAsync(Session, Directory));
            Check(error.StatusCode == status && error.Code == (code == Secret ? null : code), "only status and recognized code retained");
            Check(error.IsUnsupported == unsupported && error.IsUnavailable == unavailable, "unsupported/unregistered remain distinct errors, never unbound");
            Check(calls == 1, "failure is not retried");
            Redacted(error);
        }

        int refresh = 0;
        using var retryHttp = new HttpClient(new Handler((_, _) => Task.FromResult(++refresh == 1
            ? Json(new { _tag = "RpcError", type = "rpc.unavailable", message = "not registered" }, HttpStatusCode.BadRequest)
            : Json(new { output = State() }))));
        using var retryClient = Client(retryHttp);
        await Expect<OpenCodeSessionPresetRpcException>(() => retryClient.GetSessionPresetAsync(Session));
        Check((await retryClient.GetSessionPresetAsync(Session)).Status == OpenCodeSessionPresetStatus.Bound && refresh == 2,
            "manual refresh may discover a newly registered RPC; unsupported is not cached");
    }

    private static async Task WrongSessionAndMalformedOutput()
    {
        var valid = new Dictionary<string, object?>
        {
            ["sessionID"] = Session, ["status"] = "bound", ["presetName"] = "name", ["affects"] = "future-children-only",
        };
        var malformed = new List<object> { new { data = State() }, State(), new { output = (object?)null }, new { output = State(sessionId: "ses_other") } };
        foreach (var key in valid.Keys)
        {
            var missing = new Dictionary<string, object?>(valid);
            missing.Remove(key);
            malformed.Add(new { output = missing });
        }
        foreach (var (key, value) in new (string, object?)[]
        {
            ("sessionID", "SES_requested"), ("status", "unknown"), ("status", null), ("presetName", 42), ("affects", "current-session"),
        })
        {
            var changed = new Dictionary<string, object?>(valid) { [key] = value };
            malformed.Add(new { output = changed });
        }
        foreach (var body in malformed)
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
            using var client = Client(http);
            Redacted(await Expect<OpenCodeProtocolException>(() => client.GetSessionPresetAsync(Session, Directory)));
        }
        using var invalidJsonHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Secret),
        })));
        using var invalidJsonClient = Client(invalidJsonHttp);
        Redacted(await Expect<OpenCodeProtocolException>(() => invalidJsonClient.GetSessionPresetAsync(Session)));
    }

    private static async Task CancellationAndTimeout()
    {
        int calls = 0;
        using var cancelled = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            calls++;
            cancelled.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }));
        using var client = Client(http);
        var during = await Expect<OperationCanceledException>(() => client.GetSessionPresetAsync(Session, Directory, cancelled.Token));
        Check(during.CancellationToken == cancelled.Token && calls == 1, "in-flight cancellation preserves caller token");
        Redacted(during);
        var before = await Expect<OperationCanceledException>(() => client.GetSessionPresetAsync(Session, Directory, cancelled.Token));
        Check(before.CancellationToken == cancelled.Token && calls == 1, "pre-cancelled read sends no request");

        using var timeoutHttp = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        }));
        using var timeoutClient = Client(timeoutHttp, TimeSpan.FromMilliseconds(25));
        Redacted(await Expect<OpenCodeTimeoutException>(() => timeoutClient.GetSessionPresetAsync(Session, Directory)));
    }

    private static async Task TransportAndClientLifecycle()
    {
        foreach (var failure in new Exception[] { new HttpRequestException(Secret + Directory + Session), new IOException(Secret) })
        {
            using var failedHttp = new HttpClient(new Handler((_, _) => Task.FromException<HttpResponseMessage>(failure)));
            using var failedClient = Client(failedHttp);
            Redacted(await Expect<OpenCodeTransportException>(() => failedClient.GetSessionPresetAsync(Session, Directory)));
        }
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json(new { output = State() }));
        }));
        using var client = Client(http);
        await Expect<ArgumentException>(() => client.GetSessionPresetAsync(" "));
        await Expect<ArgumentException>(() => client.GetSessionPresetAsync(Session, " "));
        Check(calls == 0, "invalid session/location rejected locally");
        client.Dispose();
        await Expect<ObjectDisposedException>(() => client.GetSessionPresetAsync(Session));
        using var independent = Client(http);
        Check((await independent.GetSessionPresetAsync(Session)).PresetName == "backend-preset" && calls == 1,
            "disposing source client does not dispose injected HTTP transport");
    }
}
