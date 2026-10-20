using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    private static readonly OpenCodeCapabilityScope Scope = new("https://offline.invalid/prefix/", 42, "/work", DateTimeOffset.UnixEpoch);
    private static readonly OpenCodePluginInfo TodoPlugin = Plugin("opencode-todolist");
    private static readonly OpenCodePluginInfo OmoPlugin = Plugin("oh-my-opencode-slim");

    private static async Task Main()
    {
        await TransportAndConnectionStates();
        TodoReplay();
        PluginGatesAndCoreChildren();
        await ServerInfo();
        await McpControl();
        Console.WriteLine($"PASS: 5 capability groups, {assertions} assertions; fake HTTP only; Billion/LSP/private storage/RPC requests: 0.");
    }

    private static void Check(bool condition, string label)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static OpenCodeClient Client(HttpClient http, TimeSpan? timeout = null)
        => new(http, new() { BaseUrl = "https://offline.invalid/prefix", Secret = "synthetic-token", Timeout = timeout ?? TimeSpan.FromSeconds(5) }, true);

    private static async Task<T> Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { assertions++; return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static OpenCodePluginInfo Plugin(string name, string status = "active", bool? server = true)
        => new()
        {
            Source = new() { Type = "package", Target = name + "@0.4.1" },
            Features = new() { Server = server }, State = new() { Status = status },
        };

    private static OpenCodeCapabilitySnapshot Capabilities(params OpenCodePluginInfo[] plugins)
        => OpenCodeCapabilityStatus.Create(Scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.Success([]),
            OpenCodeCapabilityRead<OpenCodePluginInfo>.Success(plugins));

    private static async Task TransportAndConnectionStates()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Check(request.Method == HttpMethod.Get && request.Content is null, "read-only GET without body");
            Check(request.Headers.Authorization?.ToString() == "Bearer synthetic-token", "existing per-request authorization");
            Check(Uri.UnescapeDataString(request.RequestUri.Query) == "?location[directory]=/work & 汉字", "deepObject directory encoded once");
            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/prefix/api/mcp" => Json("""
                    {"location":{"directory":"/work & 汉字"},"data":[
                      {"name":"one","status":{"status":"connected"}},
                      {"name":"two","status":{"status":"pending"}},
                      {"name":"three","status":{"status":"disabled"}},
                      {"name":"four","status":{"status":"failed","error":"synthetic mcp failure"}},
                      {"name":"five","status":{"status":"needs_auth","error":"synthetic auth hint"}},
                      {"name":"six","status":{"status":"future"}}]}
                    """),
                "/prefix/api/plugin" => Json("""
                    {"location":{"directory":"/work & 汉字"},"data":[
                      {"id":"todo-id","source":{"type":"package","target":"opencode-todolist@0.4.1","version":"0.4.1","outdated":true,"updating":true},"features":{"server":true,"tui":true},"state":{"status":"active"}},
                      {"source":{"type":"package","target":"oh-my-opencode-slim"},"features":{"server":true},"state":{"status":"failed","error":"synthetic plugin error"}},
                      {"source":{"type":"local","path":"/not-read/plugin"},"features":{"rpc":true},"state":{"status":"future"}}]}
                    """),
                _ => throw new InvalidOperationException("Unexpected endpoint, including Billion/LSP/config/RPC"),
            });
        }));
        using var client = Client(http);
        var mcp = await client.GetMcpServersAsync("/work & 汉字");
        var plugins = await client.GetPluginsAsync("/work & 汉字");
        Check(mcp.Count == 6 && mcp[3].Status?.Error == "synthetic mcp failure" && mcp[4].Status?.Error == "synthetic auth hint", "nested MCP statuses and errors parsed");
        Check(plugins.Count == 3 && plugins[0].Source?.Version == "0.4.1" && plugins[0].Features?.Tui == true, "plugin source and declared features parsed");
        Check(plugins[0].Source?.Outdated == true && plugins[0].Source?.Updating == true && plugins[1].Source?.Outdated is null, "package outdated/updating flags parsed, absent stays null");
        Check(plugins[1].State?.Error == "synthetic plugin error" && plugins[2].Features?.Rpc == true, "plugin state error and RPC declaration retained without using RPC");
        var snapshot = OpenCodeCapabilityStatus.Create(Scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.Success(mcp), OpenCodeCapabilityRead<OpenCodePluginInfo>.Success(plugins));
        Check(snapshot.Mcp.Availability == OpenCodeCapabilityAvailability.Available && snapshot.Plugins.Availability == OpenCodeCapabilityAvailability.Available, "directory query success is distinct from row availability");
        var expected = new[] { OpenCodeCapabilityAvailability.Available, OpenCodeCapabilityAvailability.Unknown,
            OpenCodeCapabilityAvailability.Unavailable, OpenCodeCapabilityAvailability.Unavailable,
            OpenCodeCapabilityAvailability.Unavailable, OpenCodeCapabilityAvailability.Unknown };
        Check(snapshot.McpServers.Select(row => row.Connection.Availability).SequenceEqual(expected), "all MCP states mapped conservatively");
        Check(snapshot.PluginEntries.All(row => row.Listed) && snapshot.PluginEntries[0].Running == true
            && snapshot.PluginEntries[1].Running == false && snapshot.PluginEntries[2].Running is null, "listed config evidence is separate from running");
        Check(snapshot.Todo.Availability == OpenCodeCapabilityAvailability.Available && snapshot.Omo.Availability == OpenCodeCapabilityAvailability.Unavailable, "only active server plugin enables feature");
        Check(snapshot.Lsp.Availability == OpenCodeCapabilityAvailability.Unavailable && snapshot.Lsp.Reason == "OpenCode V2 未公开LSP状态接口", "LSP never inferred from connection");
        Check(snapshot.Billion.Availability == OpenCodeCapabilityAvailability.Unavailable && requests.Count == 2, "Billion remains disabled without a request");
        Check(snapshot.Scope == Scope && snapshot.Complete, "scope/revision/sample time retained");

        foreach (var endpoint in new[] { "mcp", "plugin" })
        {
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
            {
                var count = 0;
                using var failedHttp = new HttpClient(new Handler((request, _) =>
                {
                    count++;
                    Check(request.RequestUri!.Query == "", "default location omits query");
                    return Task.FromResult(Json("synthetic-secret-error-body", status));
                }));
                using var failedClient = Client(failedHttp);
                var error = await Expect<OpenCodeApiException>(() => ReadEndpoint(failedClient, endpoint));
                Check(count == 1 && error.StatusCode == (int)status && !error.ToString().Contains("synthetic-secret"), "HTTP error sanitized and not retried");
                var failed = OpenCodeCapabilityStatus.Create(Scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.Failed(error), OpenCodeCapabilityRead<OpenCodePluginInfo>.Failed(error));
                Check(failed.Mcp.Availability == OpenCodeCapabilityAvailability.Unknown && failed.Plugins.Availability == OpenCodeCapabilityAvailability.Unknown
                    && failed.Todo.Availability == OpenCodeCapabilityAvailability.Unknown && failed.Omo.Availability == OpenCodeCapabilityAvailability.Unknown && !failed.Complete, "HTTP failures including 404 remain Unknown, not Unavailable");
            }

            foreach (var payload in new[] { "not-json", "{}", "{\"data\":null}", "{\"data\":[null]}",
                endpoint == "mcp" ? "{\"data\":[{\"name\":\"x\",\"status\":\"connected\"}]}" : "{\"data\":[{\"id\":\"x\",\"state\":{\"status\":\"active\"}}]}" })
            {
                using var malformedHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(payload))));
                using var malformed = Client(malformedHttp);
                var error = await Expect<OpenCodeProtocolException>(() => ReadEndpoint(malformed, endpoint));
                Check(!OpenCodeCapabilityRead<OpenCodePluginInfo>.Failed(error).Complete, "malformed response never becomes successful empty directory");
            }

            using var transportHttp = new HttpClient(new Handler((_, _) => throw new HttpRequestException("synthetic transport secret")));
            using var transportClient = Client(transportHttp);
            var transport = await Expect<OpenCodeTransportException>(() => ReadEndpoint(transportClient, endpoint));
            Check(OpenCodeCapabilityRead<OpenCodeMcpServer>.Failed(transport).UnknownReason == "状态查询失败，无法确认", "transport failure has safe unknown reason");

            using var timeoutHttp = new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json("{}"); }));
            using var timeoutClient = Client(timeoutHttp, TimeSpan.FromMilliseconds(20));
            var timeout = await Expect<OpenCodeTimeoutException>(() => ReadEndpoint(timeoutClient, endpoint));
            Check(!OpenCodeCapabilityRead<OpenCodePluginInfo>.Failed(timeout).Complete, "timeout remains incomplete");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var canceled = await Expect<OperationCanceledException>(() => ReadEndpoint(timeoutClient, endpoint, cts.Token));
            Check(canceled.CancellationToken == cts.Token, "caller cancellation preserved, not converted to unavailable");
        }
    }

    private static async Task ReadEndpoint(OpenCodeClient client, string endpoint, CancellationToken token = default)
    {
        if (endpoint == "mcp") await client.GetMcpServersAsync(cancellationToken: token);
        else await client.GetPluginsAsync(cancellationToken: token);
    }

    private static OpenCodeContentPart Tool(string status, object input, string name = "todowrite") => new()
    {
        Type = "tool", Name = name, State = JsonSerializer.SerializeToElement(new { status, input }),
    };

    private static object Todos(string content) => new { todos = new[] { new { content, status = "in_progress", priority = "high" } } };
    private static OpenCodeMessage Message(string id, params OpenCodeContentPart[] parts)
        => new() { Id = id, Type = "assistant", Content = [.. parts] };

    private static void TodoReplay()
    {
        var capabilities = Capabilities(TodoPlugin);
        var old = Message("msg_old", Tool("completed", Todos("older")));
        var current = Message("msg_current", Tool("completed", Todos("first in message")), Tool("completed", Todos("latest")));
        var ignored = Message("msg_not_done", Tool("running", Todos("running")), Tool("streaming", "partial JSON"),
            Tool("error", Todos("failed")), Tool("completed", Todos("read"), "todoread"), Tool("completed", Todos("wrong-case"), "TodoWrite"));
        var replay = OpenCodeTodoReplay.Replay("ses_selected", new[] { old, current, ignored }, true, capabilities);
        Check(replay.Complete && replay.SourceMessageId == "msg_current" && replay.Items.Single().Content == "latest", "latest completed write wins including in-message order");
        Check(replay.Items[0].Status == "in_progress" && replay.Items[0].Priority == "high" && replay.SessionId == "ses_selected" && replay.Scope == Scope, "todo fields and selected scope retained");
        var clear = Message("msg_clear", Tool("completed", new { todos = Array.Empty<object>() }));
        var cleared = OpenCodeTodoReplay.Replay("ses_selected", new[] { old, clear, ignored }, true, capabilities);
        Check(cleared.Complete && cleared.Items.IsEmpty && cleared.SourceMessageId == "msg_clear" && cleared.Reason.Contains("清空"), "empty array is an explicit clear");
        var none = OpenCodeTodoReplay.Replay("ses_selected", new[] { ignored }, true, capabilities);
        Check(none.Complete && none.Items.IsEmpty && none.SourceMessageId is null, "unfinished calls do not invent todos");
        var incomplete = OpenCodeTodoReplay.Replay("ses_selected", new[] { old }, false, capabilities);
        Check(!incomplete.Complete && incomplete.Items.Single().Content == "older" && incomplete.Reason.Contains("Incomplete"), "partial history retains reference but cannot claim current completeness");
        var missing = OpenCodeTodoReplay.Replay("ses_selected", Array.Empty<OpenCodeMessage>(), false, capabilities);
        Check(!missing.Complete && missing.SourceMessageId is null && missing.Reason.Contains("不能据此断言无任务"), "failed/unfetched history is not no tasks");
        Check(!OpenCodeTodoReplay.Replay("ses_selected", new[] { clear }, false, capabilities).Complete, "even valid clear never hides pagination incompleteness");
        foreach (var input in new object[] { new { }, new { todos = "not-array" },
            new { todos = new[] { new { content = "x", status = "unknown", priority = "high" } } },
            new { todos = new[] { new { content = "x", status = "pending", priority = 123 } } },
            "{\"todos\":[]}" })
        {
            var bad = Message("msg_bad", Tool("completed", input));
            var result = OpenCodeTodoReplay.Replay("ses_selected", new[] { old, bad }, true, capabilities);
            Check(!result.Complete && result.SourceMessageId == "msg_bad" && result.Items.IsEmpty, "malformed latest completed write must not fall back to old tasks");
            Check(OpenCodeTodoReplay.Replay("ses_selected", new[] { bad, clear }, true, capabilities).Complete, "later valid replacement repairs older malformed write");
        }
        // Wave3 D2: priority is optional in a valid todowrite; omitted/null priority keeps the task (empty priority), in server order.
        var noPriority = Message("msg_no_priority", Tool("completed", new
        {
            todos = new object[]
            {
                new { content = "second-first", status = "pending" },
                new { content = "with-null", status = "completed", priority = (string?)null },
                new { content = "with-low", status = "in_progress", priority = "low" },
            },
        }));
        var optional = OpenCodeTodoReplay.Replay("ses_selected", new[] { old, noPriority }, true, capabilities);
        Check(optional.Complete && optional.SourceMessageId == "msg_no_priority" && optional.Items.Length == 3,
            "todos without priority are still listed (D2)");
        Check(optional.Items.Select(item => item.Content).SequenceEqual(new[] { "second-first", "with-null", "with-low" }),
            "server order kept (no regrouping by status)");
        Check(optional.Items[0].Priority == "" && optional.Items[1].Priority == "" && optional.Items[2].Priority == "low",
            "omitted/null priority projects to empty; explicit priority retained");
        foreach (var badPriority in new object[] { "urgent", 3, true })
        {
            var invalid = Message("msg_bad_priority", Tool("completed", new { todos = new[] { new { content = "x", status = "pending", priority = badPriority } } }));
            var rejected = OpenCodeTodoReplay.Replay("ses_selected", new[] { old, invalid }, true, capabilities);
            Check(!rejected.Complete && rejected.Items.IsEmpty && rejected.SourceMessageId == "msg_bad_priority",
                "a present but invalid priority still rejects the whole latest list: " + badPriority);
        }
        var future = Message("msg_future", Tool("future", Todos("unknown")));
        Check(!OpenCodeTodoReplay.Replay("ses_selected", new[] { old, future }, true, capabilities).Complete, "unknown tool state makes replay uncertain");
        var fakeUser = Message("msg_user", Tool("completed", Todos("in user"))) with { Type = "user" };
        Check(OpenCodeTodoReplay.Replay("ses_selected", new[] { old, fakeUser }, true, capabilities).SourceMessageId == "msg_old", "non-assistant content not interpreted as tool execution");
        Check(!OpenCodeTodoReplay.Replay("ses_selected", new[] { old, current with { Content = null } }, true, capabilities).Complete,
            "missing assistant content is incomplete, not no tasks");
        Check(!OpenCodeTodoReplay.Replay("ses_selected", new[] { old, Message("msg_null_part", new OpenCodeContentPart[] { null! }) }, true, capabilities).Complete,
            "null content part is incomplete, not no tasks");
    }

    private static IEnumerable<OpenCodeMessage> MustNotReadMessages()
    {
        throw new InvalidOperationException("Unavailable plugin must short-circuit replay");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async Task ServerInfo()
    {
        var bodies = new Queue<string>(new[]
        {
            """{"version":"1.2.3","pid":42,"urls":["http://127.0.0.1:4096"],"paths":{"tmp":"/tmp/x"}}""",
            """{"pid":42,"urls":[],"paths":{"tmp":"/tmp/x"}}""",
        });
        var requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Check(request.Method == HttpMethod.Get && request.Content is null && request.RequestUri!.AbsolutePath == "/prefix/api/info"
                && request.RequestUri.Query == "", "GET api/info without body or query");
            Check(request.Headers.Authorization?.ToString() == "Bearer synthetic-token", "server info uses existing authorization");
            return Task.FromResult(Json(bodies.Dequeue()));
        }));
        var client = Client(http);
        var info = await client.GetServerInfoAsync();
        Check(info.Version == "1.2.3", "server version parsed (pid/urls/paths ignored)");
        await Expect<OpenCodeProtocolException>(() => client.GetServerInfoAsync());
        Check(requests == 2, "no retry");
        Check(client.DisplayBaseUrl == "https://offline.invalid/prefix" && !client.DisplayBaseUrl.Contains("synthetic-token"), "display URL is the configured base without credentials");
    }

    private static async Task McpControl()
    {
        var seen = new List<(HttpMethod Method, string Path, string Query)>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Check(request.Content is null, "MCP control POST has no body");
            Check(request.Headers.Authorization?.ToString() == "Bearer synthetic-token", "MCP control uses existing authorization");
            seen.Add((request.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }));
        using var client = Client(http);
        await client.ControlMcpServerAsync("my server/1", OpenCodeMcpControlAction.Connect, "/work & 汉字");
        await client.ControlMcpServerAsync("files", OpenCodeMcpControlAction.Disconnect);
        Check(seen.Count == 2
            && seen[0] == (HttpMethod.Post, "/prefix/api/experimental/mcp/my%20server%2F1/connect", "?location[directory]=/work & 汉字")
            && seen[1] == (HttpMethod.Post, "/prefix/api/experimental/mcp/files/disconnect", ""),
            "H6: MCP connect/disconnect POST routes, name escaping and optional location[directory]");
        foreach (var bad in new[] { "", " ", ".", "..", "a\nb", new string('x', 257) })
        {
            await Expect<ArgumentException>(() => client.ControlMcpServerAsync(bad, OpenCodeMcpControlAction.Connect));
        }

        Check(seen.Count == 2, "invalid MCP names send nothing");
    }

    private static void PluginGatesAndCoreChildren()
    {
        var absent = Capabilities();
        Check(absent.Plugins.Availability == OpenCodeCapabilityAvailability.Available && absent.Todo.Availability == OpenCodeCapabilityAvailability.Unavailable, "successful empty directory distinct from query failure");
        var unknown = OpenCodeCapabilityStatus.Create(Scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.NotObserved(), OpenCodeCapabilityRead<OpenCodePluginInfo>.NotObserved());
        Check(unknown.Todo.Availability == OpenCodeCapabilityAvailability.Unknown && unknown.Omo.Availability == OpenCodeCapabilityAvailability.Unknown, "unobserved plugins cannot be enabled");
        var local = Capabilities(new OpenCodePluginInfo { Source = new() { Type = "local", Path = "/fake/opencode-todolist" }, Features = new() { Server = true }, State = new() { Status = "active" } });
        Check(local.Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "local path substring is not installation evidence");
        Check(Capabilities(Plugin("not-opencode-todolist")).Todo.Availability != OpenCodeCapabilityAvailability.Available, "substring package match rejected");
        Check(Capabilities(Plugin("opencode-todolist", server: null)).Todo.Availability == OpenCodeCapabilityAvailability.Unavailable, "active TUI-only declaration does not enable server feature");
        Check(Capabilities(Plugin("opencode-todolist", "failed")).Todo.Availability == OpenCodeCapabilityAvailability.Unavailable, "listed failed plugin does not enable feature");
        Check(Capabilities(Plugin("opencode-todolist", "future")).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "unknown plugin runtime never inferred active");
        Check(Capabilities(TodoPlugin, TodoPlugin).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "conflicting duplicate identities are uncertain");
        Check(Capabilities(Plugin("billion-context")).Billion.Availability == OpenCodeCapabilityAvailability.Unavailable, "listed active Billion plugin still does not authorize management requests");
        var available = Capabilities(TodoPlugin, OmoPlugin);
        var stale = OpenCodeCapabilityStatus.Create(Scope with { Stale = true }, OpenCodeCapabilityRead<OpenCodeMcpServer>.Success([]), OpenCodeCapabilityRead<OpenCodePluginInfo>.Success([TodoPlugin, OmoPlugin]));
        Check(stale.Todo.Availability == OpenCodeCapabilityAvailability.Unknown && stale.PluginEntries.All(row => row.Running is null), "stale evidence cannot enable feature or show current running");
        foreach (var gated in new[] { absent, unknown, local, stale })
        {
            var todo = OpenCodeTodoReplay.Replay("ses_parent", MustNotReadMessages(), true, gated);
            Check(todo.Capability.Availability != OpenCodeCapabilityAvailability.Available && !todo.Complete && todo.Items.IsEmpty, "missing/unknown/stale plugin stops replay before reading messages");
            var children = OpenCodeSubagentStatus.ProjectChildren("ses_parent", [], true, [], true, gated);
            Check(children.Capability.Availability != OpenCodeCapabilityAvailability.Available && !children.Complete && children.Children.IsEmpty, "OMO availability follows plugin evidence");
            Check(gated.Lsp.Availability == OpenCodeCapabilityAvailability.Unavailable && gated.Billion.Availability == OpenCodeCapabilityAvailability.Unavailable, "LSP/Billion unavailable regardless of other capabilities");
        }

        OpenCodeSessionInfo[] sessions = [
            new() { Id = "ses_a", ParentId = "ses_parent", Agent = "explore", Outcome = "succeeded", Title = "Explore the repo (@explore subagent)" },
            new() { Id = "ses_b", ParentId = "ses_parent", Agent = "fixer", Title = "  " },
            new() { Id = "ses_foreign", ParentId = "ses_elsewhere" },
            new() { Id = "ses_grandchild", ParentId = "ses_a" },
            new() { Id = "ses_root" }];
        var projected = OpenCodeSubagentStatus.ProjectChildren("ses_parent", sessions, true, ["ses_a"], true, available);
        Check(projected.Complete && projected.Children.Length == 2 && projected.Children[0].Active == true && projected.Children[1].Active == false, "core parent filter and active projection only for direct children");
        Check(projected.Children[0].Agent == "explore" && projected.Scope == Scope && projected.Reason.Contains("foreground"), "no derived alias/completion/reusable from Outcome");
        Check(projected.Children[0].Title == "Explore the repo (@explore subagent)" && projected.Children[1].Title is null,
            "H7: child title projected from Session.Info.title; blank title stays unknown (never the id)");
        var partial = OpenCodeSubagentStatus.ProjectChildren("ses_parent", sessions, false, ["ses_a"], false, available);
        Check(!partial.Complete && partial.Children[0].Active == true && partial.Children[1].Active is null, "partial active evidence preserves positive membership, not false inactive");
        var failedActive = OpenCodeSubagentStatus.ProjectChildren("ses_parent", sessions, true, null, false, available);
        Check(!failedActive.Complete && failedActive.Children.All(row => row.Active is null), "active read failure never infers idle");
        var duplicates = OpenCodeSubagentStatus.ProjectChildren("ses_parent", [sessions[0], sessions[0], new() { Id = "ses_parent", ParentId = "ses_parent" }], true, [], true, available);
        Check(!duplicates.Complete && duplicates.Children.Length == 1, "duplicate/self child evidence marked incomplete");
        // Built-in detection: OpenAPI Plugin.Source is a type-discriminated union (builtin/package/local/sdk).
        var builtin = new OpenCodePluginInfo { Source = new() { Type = "builtin" }, Features = new() { Server = true }, State = new() { Status = "active" } };
        Check(OpenCodeCapabilityStatus.IsBuiltIn(builtin) && !OpenCodeCapabilityStatus.IsBuiltIn(TodoPlugin)
            && !OpenCodeCapabilityStatus.IsBuiltIn(new OpenCodePluginInfo { Source = new() { Type = "local", Path = "/opt/opencode/builtin/x" } })
            && !OpenCodeCapabilityStatus.IsBuiltIn(new OpenCodePluginInfo { Id = "builtin", Source = new() { Type = "package", Target = "builtin" } })
            && !OpenCodeCapabilityStatus.IsBuiltIn(new OpenCodePluginInfo()), "only source.type == builtin is built-in (no name/path guessing)");
        var withBuiltin = Capabilities(TodoPlugin, builtin);
        Check(withBuiltin.Todo.Availability == OpenCodeCapabilityAvailability.Available, "anonymous built-in plugin cannot be the target package");
        Check(Capabilities(builtin).Todo.Availability == OpenCodeCapabilityAvailability.Unavailable, "built-ins alone: target not listed (not unknown)");
        Check(Capabilities(builtin, new OpenCodePluginInfo { Source = new() { Type = "sdk" }, Features = new() { Server = true }, State = new() { Status = "active" } })
            .Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "anonymous sdk plugin still keeps target uncertain");
        Check(withBuiltin.PluginEntries.Length == 2, "built-ins remain in the snapshot; only the connection view hides them");

        var mutable = new List<OpenCodePluginInfo> { TodoPlugin };
        var copied = OpenCodeCapabilityRead<OpenCodePluginInfo>.Success(mutable);
        mutable.Clear();
        Check(copied.Items.Length == 1 && copied.Complete, "read result is detached from caller's mutable list");
    }
}
