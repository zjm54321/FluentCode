using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static OpenCodeEvent Event(string type, string data) => OpenCodeEvent.Parse("{\"type\":\"" + type + "\",\"data\":" + data + "}");
    private static OpenCodeMessage Message(string id, string text) => new() { Id = id, Type = "assistant", Content = new() { new() { Id = "p1", Type = "text", Text = text } }, Time = new() { Created = 10 } };
    private static string Text(OpenCodeEventReducer reducer) => reducer.Messages.Single().Content!.Single().Text!;
    private static async Task Main()
    {
        var reducer = new OpenCodeEventReducer("ses_test");
        reducer.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_a","id":"p1","type":"text","text":"hello"}}"""));
        Check(Text(reducer) == "hello", "part.updated inserts without message");
        reducer.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_a","id":"p1","type":"text","text":"replace"}}"""));
        Check(Text(reducer) == "replace", "part.updated replaces by stable part ID");
        reducer.Apply(Event("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_a","partID":"p1","field":"text","delta":" delta"}"""));
        Check(Text(reducer) == "replace delta", "delta appends");
        Check(!reducer.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_other","messageID":"msg_a","id":"p1","type":"text","text":"wrong"}}""")), "other session ignored");

        var early = new OpenCodeEventReducer("ses_test");
        early.Apply(Event("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_a","partID":"p1","field":"text","delta":" after"}"""));
        early.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_a","id":"p1","type":"text","text":"before"}}"""));
        Check(Text(early) == "before after", "out-of-order delta retained until part arrives");

        var fetch = reducer.Version;
        reducer.Apply(Event("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_a","partID":"p1","field":"text","delta":" live"}"""));
        reducer.MergeSnapshot(new[] { Message("msg_a", "stale") }, fetch);
        Check(Text(reducer) == "replace delta live", "events during fetch win over stale snapshot");
        reducer.MergeSnapshot(new[] { Message("msg_a", "reconnected") }, reducer.Version);
        Check(Text(reducer) == "reconnected", "reconnect snapshot repairs missed deltas");

        var native = new OpenCodeEventReducer("ses_test");
        native.Apply(Event("session.reasoning.started", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0}"""));
        Check(native.Messages.Single().Content!.Single().Type == "reasoning" && Text(native) == "", "native reasoning begins immediately with empty text");
        Check(native.Messages.Single().Content!.Single().Time?.Completed is null, "reasoning remains in progress");
        native.Apply(Event("session.reasoning.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"delta":"thinking"}"""));
        Check(Text(native) == "thinking", "native reasoning delta appends");
        native.Apply(Event("session.reasoning.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"text":"thought"}"""));
        Check(Text(native) == "thought" && native.Messages.Single().Content!.Single().Time?.Completed is not null, "native reasoning end replaces final text and completes");

        await Transport();
        await HomeDataContracts();
        await DefaultSelectionContracts();
        Console.WriteLine($"PASS event-sync: {checks} assertions (insert/replace, delta, out-of-order, fetch race, native reasoning, stream EOF/reconnect/cancel/auth).");
    }

    private static async Task HomeDataContracts()
    {
        const string directory = "/remote/Project A+&中";
        const string project = """{"id":"prj_same","canonical":"/remote/main","name":"Actual name","sandboxes":["/remote/copy"],"time":{"created":1,"updated":2,"active":3}}""";
        var handler = new HomeHandler();
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new()
        {
            BaseUrl = "http://localhost:4096/prefix/api", AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = "test", Secret = "secret",
        }, true);
        handler.Response = "[" + project + "," + project + "]";
        var projects = await client.GetProjectsAsync();
        Check(projects.Count == 2 && projects[0].Id == projects[1].Id, "raw project catalog preserves actual entries rather than Git-ID dedup");
        Check(projects[0].Directory is null && projects[0].Canonical == "/remote/main"
            && projects[0].Name == "Actual name" && projects[0].Sandboxes!.Single() == "/remote/copy"
            && projects[0].Time?.Active == 3, "catalog metadata retained without forging directory");
        Check(handler.Path == "/prefix/api/project" && handler.Query == "", "project raw-array route preserves base prefix and default query omission");
        await client.GetProjectsAsync(true);
        Check(handler.Query == "?archived=true", "archived true uses verified string query");
        await client.GetProjectsAsync(false);
        Check(handler.Query == "?archived=false", "archived false is not omitted");
        foreach (var invalid in new[] { "{\"data\":[" + project + "]}", "[{}]", "[null]" })
        {
            handler.Response = invalid;
            await HomeProtocolFailure(() => client.GetProjectsAsync(), "project malformed/wrapped response rejected");
        }

        handler.Response = """{"location":{"directory":"/resolved/location"},"data":[{"path":"../sibling/actual.txt","type":"file"},{"path":"../sibling/child","type":"directory"}]}""";
        var fs = await client.GetFileSystemAsync(directory, "../sibling +&中");
        Check(handler.Path == "/prefix/api/fs/list"
            && handler.Query.Contains("location%5Bdirectory%5D=" + Uri.EscapeDataString(directory), StringComparison.Ordinal)
            && handler.Query.Contains("path=" + Uri.EscapeDataString("../sibling +&中"), StringComparison.Ordinal), "fs uses verified route/deepObject and escaped exact remote paths");
        Check(fs.Location?.Directory == "/resolved/location" && fs.Data![0].Path == "../sibling/actual.txt"
            && fs.Data[1].Type == "directory", "fs relative entries remain relative to returned location, not browsed path");
        await client.GetFileSystemAsync(null);
        Check(handler.Query == "", "default fs location/path omitted");
        foreach (var invalid in new[] { "{}", "{\"location\":{\"directory\":\"/r\"},\"data\":[{\"path\":\"x\",\"type\":\"link\"}]}" })
        {
            handler.Response = invalid;
            await HomeProtocolFailure(() => client.GetFileSystemAsync(directory), "fs malformed response rejected");
        }

        handler.Response = """{"directory":"/resolved/location","project":{"id":"prj_same","directory":"/resolved/location","canonical":"/remote/main"}}""";
        var location = await client.GetLocationAsync(directory, default);
        Check(location.Directory == "/resolved/location" && location.Project?.Id == "prj_same"
            && handler.Query.Contains(Uri.EscapeDataString(directory), StringComparison.Ordinal), "existing location resolution remains direct-object and directory-scoped");

        handler.Response = """{"location":{"directory":"/resolved/location"},"data":[{"id":"agent_real","name":"Real","mode":"primary","hidden":false,"request":{},"permissions":[],"model":{"providerID":"provider_real","id":"model_real","variant":"high"}}]}""";
        var agents = await client.ListAgentsAsync(directory);
        Check(handler.Path == "/prefix/api/agent" && handler.Query.Contains(Uri.EscapeDataString(directory), StringComparison.Ordinal)
            && agents.Data!.Single().Model?.GetProperty("variant").GetString() == "high", "existing agent catalog keeps actual directory and optional model default");
        handler.Response = """{"location":{"directory":"/resolved/location"},"data":[{"id":"model_real","providerID":"provider_real","name":"Real","variants":[{"id":"high","settings":{"temperature":1}},{"id":"low"}],"enabled":true,"status":"active"}]}""";
        var models = await client.ListModelsAsync(directory);
        var catalogModel = models.Data!.Single();
        Check(handler.Path == "/prefix/api/model" && handler.Query.Contains(Uri.EscapeDataString(directory), StringComparison.Ordinal)
            && catalogModel.Variants!.Select(v => v.Id).SequenceEqual(new[] { "high", "low" })
            && catalogModel.Enabled == true && catalogModel.Status == "active", "existing model catalog projects actual selectable variants/enabled/status without duplicating service");

        handler.Response = """{"data":{"id":"ses_created","location":{"directory":"/resolved/location"},"agent":"agent_real","model":{"providerID":"provider_real","id":"model_real","variant":"high"}}}""";
        var before = handler.Calls;
        var created = await client.CreateSessionConfiguredAsync(directory, "agent_real", new()
            { ProviderId = "provider_real", Id = "model_real", Variant = "high" });
        using (var body = JsonDocument.Parse(handler.Body!))
        {
            var root = body.RootElement;
            Check(handler.Calls == before + 1 && handler.Method == HttpMethod.Post && handler.Path == "/prefix/api/session"
                && handler.Query == "" && root.EnumerateObject().Count() == 3, "configured creation is one POST, not switches/config writes");
            Check(root.GetProperty("location").GetProperty("directory").GetString() == directory
                && root.GetProperty("agent").GetString() == "agent_real"
                && root.GetProperty("model").GetProperty("providerID").GetString() == "provider_real"
                && root.GetProperty("model").GetProperty("id").GetString() == "model_real"
                && root.GetProperty("model").GetProperty("variant").GetString() == "high", "atomic create captures exact directory/agent/model/variant wire names");
        }
        Check(created.Id == "ses_created" && created.Model?.Variant == "high", "configured creation reads existing data-wrapped Session.Info");
        await client.CreateSessionConfiguredAsync(null, null, null);
        Check(handler.Body == "{}", "null creation preferences omit all fields for server defaults");
        await client.CreateSessionConfiguredAsync(directory, null, new() { ProviderId = "provider_real", Id = "model_real" });
        using (var body = JsonDocument.Parse(handler.Body!))
            Check(!body.RootElement.TryGetProperty("agent", out _) && !body.RootElement.GetProperty("model").TryGetProperty("variant", out _), "unspecified agent/variant omitted, not explicit defaults");
        await client.CreateSessionAsync(null, default);
        Check(handler.Body == "{}", "existing creation signature/body compatible");
        await client.CreateSessionAsync(directory, default);
        using (var body = JsonDocument.Parse(handler.Body!))
            Check(body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("location").GetProperty("directory").GetString() == directory, "existing directory-only creation unchanged");
        handler.Response = "{\"data\":{}}";
        await HomeProtocolFailure(() => client.CreateSessionConfiguredAsync(null, null, null), "configured creation requires returned session identity");
        before = handler.Calls;
        try { await client.CreateSessionConfiguredAsync(null, null, new() { Id = "model_real" }); throw new Exception("missing model validation"); }
        catch (ArgumentException) { Check(handler.Calls == before, "invalid model reference fails before transport"); }
        try { await client.CreateSessionConfiguredAsync(null, " ", null); throw new Exception("missing agent validation"); }
        catch (ArgumentException) { Check(handler.Calls == before, "blank explicit agent fails before transport"); }
        handler.Status = HttpStatusCode.ServiceUnavailable;
        try { await client.CreateSessionConfiguredAsync(null, null, null); throw new Exception("missing HTTP failure"); }
        catch (OpenCodeApiException) { Check(handler.Calls == before + 1, "configured create surfaces error and never retries"); }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.GetProjectsAsync(cancellationToken: cancelled.Token); throw new Exception("missing cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private static async Task DefaultSelectionContracts()
    {
        const string directory = "/remote/Selection A+&中";
        const string unrelatedSecret = "FAKE-UNRELATED-SECRET-MUST-NOT-SURVIVE";
        var handler = new HomeHandler();
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new()
        {
            BaseUrl = "http://localhost:4096/prefix/api", AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = "test", Secret = "secret",
        }, true);
        handler.Response = """[{"type":"document","path":"/private/low.json","info":{"default_agent":"low","model":"provider_low/family/model#real-variant","provider":{"secret":"FAKE-UNRELATED-SECRET-MUST-NOT-SURVIVE"},"system":"FAKE-UNRELATED-SECRET-MUST-NOT-SURVIVE"}},{"type":"directory","path":"/private/source"},{"type":"document","info":{"default_agent":"actual-primary"}},{"type":"document","info":{"unrelated":true}}]""";
        var selection = await client.GetConfiguredSelectionAsync(directory);
        Check(handler.Method == HttpMethod.Get && handler.Path == "/prefix/api/config"
            && handler.Query == "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directory) && handler.Body is null,
            "config uses RAW-array GET with exact escaped deepObject directory, auth and prefix");
        Check(selection.DefaultAgentId == "actual-primary" && selection.Model?.ProviderId == "provider_low"
            && selection.Model.Id == "family/model" && selection.Model.Variant == "real-variant",
            "latest defined agent wins while omitted model preserves lower selection and first-slash model ID");
        var safeProjection = JsonSerializer.Serialize(selection);
        Check(typeof(OpenCodeConfiguredSelection).GetProperties().Length == 2
            && !safeProjection.Contains(unrelatedSecret, StringComparison.Ordinal)
            && !safeProjection.Contains("/private/", StringComparison.Ordinal)
            && !selection.ToString().Contains("actual-primary", StringComparison.Ordinal),
            "returned config projection retains only independent selection fields and redacts diagnostics");
        handler.Response = """[{"type":"document","info":{"default_agent":"low","model":"lower/model#lower"}},{"type":"document","info":{"model":{"providerID":"actual-provider","model":"family/actual-model","variant":"actual-variant"}}}]""";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.DefaultAgentId == "low" && selection.Model?.ProviderId == "actual-provider"
            && selection.Model.Id == "family/actual-model" && selection.Model.Variant == "actual-variant",
            "high-priority config object model uses model key, not API id, without clearing absent agent");
        handler.Response = """[{"type":"document","info":{"model":"provider/model#old"}},{"type":"document","info":{"model":{"providerID":"provider","model":"model"}}}]""";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.Model?.Id == "model" && selection.Model.Variant is null,
            "replacement model without variant never inherits old variant or synthesizes high");
        handler.Response = """[{"type":"document","info":{"default_agent":"low","model":"provider/model"}},{"type":"document","info":{"default_agent":"","model":""}}]""";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.DefaultAgentId is null && selection.Model is null, "latest empty strings represent absence rather than generic defaults");
        handler.Response = "[]";
        selection = await client.GetConfiguredSelectionAsync(directory);
        Check(selection.DefaultAgentId is null && selection.Model is null, "empty raw config catalog keeps missing selection absent");
        foreach (var invalid in new[]
        {
            "{\"data\":[]}", "[{\"type\":\"document\",\"info\":null}]",
            "[{\"type\":\"document\",\"info\":{\"default_agent\":7}}]",
            "[{\"type\":\"document\",\"info\":{\"model\":{\"providerID\":\"p\",\"id\":\"wrong-key\"}}}]",
            "[{\"type\":\"document\",\"info\":{\"model\":\"p/m#v#extra\"}}]",
            "[{\"type\":\"document\",\"info\":{\"model\":null}}]"
        })
        {
            handler.Response = invalid;
            try { await client.GetConfiguredSelectionAsync(directory); throw new Exception("missing selection protocol error"); }
            catch (OpenCodeProtocolException ex)
            {
                Check(!ex.ToString().Contains(unrelatedSecret, StringComparison.Ordinal)
                    && ex.Message.Contains("GET api/config", StringComparison.Ordinal), "malformed relevant config schema fails with generic operation-only error");
            }
        }
        var location = JsonSerializer.Serialize(new { directory });
        handler.Response = "{\"location\":" + location + ",\"data\":{\"providerID\":\"actual-provider\",\"id\":\"actual-model\",\"name\":\"Actual Model\",\"variants\":[{\"id\":\"not-a-default\",\"settings\":{\"secret\":\"" + unrelatedSecret + "\"}}],\"headers\":{\"secret\":\"" + unrelatedSecret + "\"}}}";
        var actualDefault = await client.GetDefaultModelInfoAsync(directory);
        Check(handler.Method == HttpMethod.Get && handler.Path == "/prefix/api/model/default"
            && handler.Query == "?location%5Bdirectory%5D=" + Uri.EscapeDataString(directory)
            && actualDefault.Location?.Directory == directory && actualDefault.Data?.Id == "actual-model"
            && actualDefault.Data.ProviderId == "actual-provider", "actual default model GET uses verified wrapper and exact directory association");
        Check(!JsonSerializer.Serialize(actualDefault).Contains(unrelatedSecret, StringComparison.Ordinal),
            "default model projection drops headers/settings rather than retaining provider secrets");
        handler.Response = "{\"location\":" + location + ",\"data\":null}";
        Check((await client.GetDefaultModelInfoAsync(directory)).Data is null, "null real default model is legitimate absence");
        foreach (var invalid in new[]
        {
            "{\"location\":" + location + "}", "{\"location\":{\"directory\":\"/other\"},\"data\":null}",
            "{\"location\":" + location + ",\"data\":{\"id\":\"missing-provider\"}}"
        })
        {
            handler.Response = invalid;
            await HomeProtocolFailure(() => client.GetDefaultModelInfoAsync(directory), "default model rejects missing data, wrong directory and malformed model identity");
        }
        handler.Response = "{\"location\":" + location + ",\"data\":[{\"id\":\"agent_actual\",\"name\":\"Actual\",\"mode\":\"primary\",\"hidden\":false,\"color\":\"#12AbEF\",\"request\":{},\"permissions\":[]},{\"id\":\"agent_plain\",\"name\":\"Plain\",\"mode\":\"primary\",\"hidden\":false,\"request\":{},\"permissions\":[]}]}";
        var agents = await client.ListAgentsAsync(directory);
        Check(agents.Data![0].Color == "#12AbEF" && agents.Data[1].Color is null,
            "resolved agent color is preserved exactly and absent color is not fabricated");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = handler.Calls;
        foreach (var action in new Func<Task>[]
        {
            () => client.GetConfiguredSelectionAsync(directory, cancelled.Token),
            () => client.GetDefaultModelInfoAsync(directory, cancelled.Token),
        })
        {
            try { await action(); throw new Exception("missing selection cancellation"); }
            catch (OperationCanceledException) { Check(handler.Calls == before, "selection GET cancellation is honored without transport or retries"); }
        }
        handler.Response = "{\"secret\":\"" + unrelatedSecret + "\"}";
        handler.Status = HttpStatusCode.ServiceUnavailable;
        try { await client.GetConfiguredSelectionAsync(directory); throw new Exception("missing config HTTP error"); }
        catch (OpenCodeApiException ex)
        {
            Check(handler.Calls == before + 1 && !ex.ToString().Contains(unrelatedSecret, StringComparison.Ordinal),
                "selection HTTP failure does not retry or expose response body");
        }
    }

    private static async Task HomeProtocolFailure(Func<Task> action, string name)
    {
        try { await action(); throw new Exception(name); }
        catch (OpenCodeProtocolException) { Check(true, name); }
    }

    private sealed class HomeHandler : HttpMessageHandler
    {
        public string Response = "{}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Query = "";
        public string? Path, Body;
        public HttpMethod? Method;
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Check(request.Headers.Authorization?.Scheme == "Basic", "Home API reuses per-request authentication");
            Method = request.Method; Path = request.RequestUri!.AbsolutePath; Query = request.RequestUri.Query;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task Transport()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenCodeClient(http, new() { BaseUrl = "http://localhost:4096", AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode", Secret = "secret" }, true);
        var reconnects = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await foreach (var item in client.SubscribeEventsAsync())
                {
                    Check(item.Type == "session.text.delta", "SSE event type from data envelope");
                    var received = item.ReceivedAt;
                    await Task.Delay(50);
                    var model = new OpenCodeEventReducer("ses_test");
                    model.Apply(item);
                    var applied = DateTimeOffset.UtcNow;
                    Check(model.Messages.Single().Content!.Single().Text == "live", "stream event applied by production reducer");
                    Console.WriteLine($"HEADLESS event→reducer apply received_utc={received:O} applied_utc={applied:O} latency_ms={(applied-received).TotalMilliseconds:F1}; 50ms coalescing, not WinUI render");
                }
            }
            catch (OpenCodeTransportException) { reconnects++; }
        }
        Check(reconnects == 2 && handler.Calls == 2, "EOF observable; caller resubscribes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await foreach (var _ in client.SubscribeEventsAsync(cancelled.Token)) { } throw new Exception("missing cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Check(request.RequestUri!.AbsolutePath == "/api/event", "verified V2 SSE path");
            Check(request.Headers.Authorization?.Scheme == "Basic", "SSE per-request Basic auth");
            var json = """{"type":"session.text.delta","data":{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"delta":"live"}}""";
            if (Calls == 2) json = JsonSerializer.Serialize(json); // Effect JSON-string envelope also supported.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: session.text.delta\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream") });
        }
    }
}
