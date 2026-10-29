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
        await ResponseBounds.RunAsync();
        await TransportAndConnectionStates();
        await QolBuiltinOmoProjection();
        TodoReplay();
        PluginGatesAndCoreChildren();
        await ServerInfo();
        await McpControl();
        Console.WriteLine($"PASS: 6 capability groups, {assertions} assertions; fake HTTP only; Billion/LSP/private storage/RPC requests: 0.");
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
        Check(snapshot.Todo.Availability == OpenCodeCapabilityAvailability.Unknown && snapshot.Omo.Availability == OpenCodeCapabilityAvailability.Unavailable, "plugin catalog cannot enable native to do");
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

    private static async Task QolBuiltinOmoProjection()
    {
        // QOL builtin.ts:16-21; OMO src/index.ts:3673-3684; schema/src/plugin.ts:10-43.
        // Literal /api/plugin rows: the static builtin has an id, no package target and no rpc feature.
        var cases = new[]
        {
            ("active builtin", """[{"id":"oh-my-opencode-slim","source":{"type":"builtin"},"features":{"server":true},"state":{"status":"active"}}]""", OpenCodeCapabilityAvailability.Available),
            ("failed builtin", """[{"id":"oh-my-opencode-slim","source":{"type":"builtin"},"features":{"server":true},"state":{"status":"failed","error":"synthetic load failure"}}]""", OpenCodeCapabilityAvailability.Unavailable),
            ("unknown runtime", """[{"id":"oh-my-opencode-slim","source":{"type":"builtin"},"features":{"server":true},"state":{"status":"future"}}]""", OpenCodeCapabilityAvailability.Unknown),
            ("rpc without server", """[{"id":"oh-my-opencode-slim","source":{"type":"builtin"},"features":{"rpc":true},"state":{"status":"active"}}]""", OpenCodeCapabilityAvailability.Unavailable),
            ("duplicate exact identity", """[{"id":"oh-my-opencode-slim","source":{"type":"builtin"},"features":{"server":true},"state":{"status":"active"}},{"source":{"type":"package","target":"oh-my-opencode-slim@3.0.6"},"features":{"server":true},"state":{"status":"failed","error":"synthetic duplicate"}}]""", OpenCodeCapabilityAvailability.Unknown),
            ("similar builtin id", """[{"id":"not-oh-my-opencode-slim","source":{"type":"builtin"},"features":{"server":true},"state":{"status":"active"}}]""", OpenCodeCapabilityAvailability.Unavailable),
            ("anonymous builtin", """[{"source":{"type":"builtin"},"features":{"server":true},"state":{"status":"active"}}]""", OpenCodeCapabilityAvailability.Unavailable),
            ("unidentified local path", """[{"source":{"type":"local","path":"/not-read/oh-my-opencode-slim"},"features":{"server":true},"state":{"status":"active"}}]""", OpenCodeCapabilityAvailability.Unknown),
        };
        var requests = 0;
        foreach (var (label, rows, expected) in cases)
        {
            using var http = new HttpClient(new Handler((request, _) =>
            {
                requests++;
                Check(request.Method == HttpMethod.Get && request.Content is null
                    && request.RequestUri!.AbsolutePath == "/prefix/api/plugin"
                    && Uri.UnescapeDataString(request.RequestUri.Query) == "?location[directory]=/work",
                    "QOL OMO catalog only; no RPC/config/TUI-state probe: " + label);
                return Task.FromResult(Json("{\"location\":{\"directory\":\"/work\"},\"data\":" + rows + "}"));
            }));
            using var client = Client(http);
            var plugins = await client.GetPluginsAsync("/work");
            var snapshot = Capabilities(plugins.ToArray());
            Check(snapshot.Complete && snapshot.Omo.Availability == expected, "QOL OMO conservative gate: " + label);
            if (label != "active builtin") continue;

            var plugin = plugins.Single();
            Check(plugin.Id == "oh-my-opencode-slim" && OpenCodeCapabilityStatus.IsBuiltIn(plugin)
                && plugin.Source?.Target is null && plugin.Features is { Server: true, Rpc: null },
                "QOL embedded OMO identity survives transport without invented package/RPC metadata");
            Check(snapshot.PluginEntries.Single().Running == true && snapshot.Omo.Reason.Contains("仅使用 core 公开数据", StringComparison.Ordinal),
                "QOL active server evidence authorizes core projection, not preset RPC readiness");
            var children = OpenCodeSubagentStatus.ProjectChildren("ses_parent",
                [new() { Id = "ses_qol_child", ParentId = "ses_parent", Agent = "fixer", Title = "QOL child" }],
                true, ["ses_qol_child"], true, snapshot);
            Check(children.Complete && children.Children.Single() is { SessionId: "ses_qol_child", Agent: "fixer", Active: true },
                "QOL builtin enables existing native session/foreground child projection");
        }
        Check(requests == cases.Length, "QOL OMO one catalog read per fixture, zero RPC/private-state requests");
        Console.WriteLine("PASS QOL embedded OMO: active builtin/no target/no rpc, failed/unknown/duplicate/identity/server guards; core children only.");
    }

    private static OpenCodeContentPart Tool(string status, object input, string name = "todowrite") => new()
    {
        Type = "tool", Name = name, State = JsonSerializer.SerializeToElement(new { status, input }),
    };

    private static object Todos(string content) => new { todos = new[] { new { content, status = "in_progress", priority = "high" } } };
    private static OpenCodeMessage Message(string id, params OpenCodeContentPart[] parts)
        => new() { Id = id, Type = "assistant", Content = [.. parts] };

    private static OpenCodeTodoSnapshot Derive(IEnumerable<OpenCodeMessage?> messages, bool complete = true,
        OpenCodeTodoConfig config = OpenCodeTodoConfig.Enabled, OpenCodeTodoHistory? history = null, OpenCodeCapabilityScope? scope = null)
        => OpenCodeTodoReplay.Derive("ses_selected", messages, complete, history ?? OpenCodeTodoHistory.NoRevert, config, scope ?? Scope);

    // A10: official v2.1.1 native derivation (sidebar P$e/US): latest completed todowrite before revert, normalized.
    private static void TodoReplay()
    {
        var old = Message("msg_old", Tool("completed", Todos("older")));
        var current = Message("msg_current", Tool("completed", Todos("first in message")), Tool("completed", Todos("latest")));
        var ignored = Message("msg_not_done", Tool("running", Todos("running")), Tool("streaming", "partial JSON"),
            Tool("error", Todos("failed")), Tool("pending", Todos("pending-call")), Tool("future", Todos("future")),
            Tool("completed", Todos("read"), "todoread"), Tool("completed", Todos("wrong-case"), "TodoWrite"));
        var replay = Derive(new[] { old, current, ignored });
        Check(replay.State == OpenCodeTodoState.Listed && replay.Complete && replay.SourceMessageId == "msg_current"
            && replay.Items.Single().Content == "latest", "latest completed write wins including in-message order; non-completed/other tools ignored (official findLast)");
        Check(replay.Items[0] == new OpenCodeTodoItem("latest", "in_progress") && replay.SessionId == "ses_selected" && replay.Scope == Scope
            && replay.Remaining == 1, "official item shape {content,status}; priority ignored; selected scope retained");
        // Cleared → verified Empty (not unknown, not failure).
        var clear = Message("msg_clear", Tool("completed", new { todos = Array.Empty<object>() }));
        var cleared = Derive(new[] { old, clear, ignored });
        Check(cleared.State == OpenCodeTodoState.Empty && cleared.Complete && cleared.Items.IsEmpty && cleared.SourceMessageId == "msg_clear"
            && cleared.Reason.Contains("清空"), "Todos cleared → Empty");

        // Normalization: drop non-object / non-string / empty content; unknown or missing status → pending; extras ignored.
        var messy = Message("msg_messy", Tool("completed", new
        {
            todos = new object?[]
            {
                "plain string", 7, null, new { content = "" }, new { content = 5, status = "pending" },
                new { content = "keeps order 1", status = "completed", priority = "urgent" },
                new { content = "unknown status", status = "blocked" },
                new { content = "missing status" },
                new { content = "numeric status", status = 3 },
                new { content = "cancelled", status = "cancelled" },
            },
        }));
        var normalized = Derive(new[] { old, messy });
        Check(normalized.State == OpenCodeTodoState.Listed && normalized.Items.Select(i => i.Content).SequenceEqual(new[]
            { "keeps order 1", "unknown status", "missing status", "numeric status", "cancelled" }), "official normalization drops invalid items, keeps server order");
        Check(normalized.Items.Select(i => i.Status).SequenceEqual(new[] { "completed", "pending", "pending", "pending", "cancelled" })
            && normalized.Remaining == 3, "unknown/missing status → pending; remaining = pending + in_progress");
        var onlyInvalid = Derive(new[] { old, Message("msg_invalid_items", Tool("completed", new { todos = new object[] { 1, "x", new { content = "" } } })) });
        Check(onlyInvalid.State == OpenCodeTodoState.Empty && onlyInvalid.SourceMessageId == "msg_invalid_items",
            "all-invalid items normalize to empty (official), never back to older list");

        // Revert cutoff: only messages BEFORE revert messageID count.
        var afterRevert = Message("msg_after", Tool("completed", Todos("after revert")));
        var reverted = Derive(new[] { old, afterRevert, current }, history: new(false, true, "msg_after"));
        Check(reverted.State == OpenCodeTodoState.Listed && reverted.SourceMessageId == "msg_old" && reverted.Items.Single().Content == "older",
            "revert cutoff hides todowrite at/after revert message");
        var revertFirst = Derive(new[] { afterRevert, current }, history: new(false, true, "msg_after"));
        Check(revertFirst.State == OpenCodeTodoState.Empty && revertFirst.SourceMessageId is null,
            "everything reverted + experimental.todo on → verified Empty");
        var revertMissingComplete = Derive(new[] { old, current }, history: new(false, true, "msg_gone"));
        Check(revertMissingComplete.SourceMessageId == "msg_current", "complete history + revert id absent → whole list (official findIndex -1)");
        var revertMissingPartial = Derive(new[] { old, current }, complete: false, history: new(false, true, "msg_gone"));
        Check(revertMissingPartial.State == OpenCodeTodoState.Unknown && revertMissingPartial.Items.IsEmpty,
            "partial window without revert boundary → Unknown, never a guessed list");
        Check(Derive(new[] { old }, history: new(false, false, null)).State == OpenCodeTodoState.Unknown, "unconfirmed revert boundary → Unknown");

        // Unknown vs failure vs empty.
        var none = Derive(new[] { ignored });
        Check(none.State == OpenCodeTodoState.Empty && none.Complete && none.SourceMessageId is null && none.Capability.Availability == OpenCodeCapabilityAvailability.Available,
            "complete history, no write, experimental.todo=true → Empty");
        var incomplete = Derive(new[] { old }, complete: false);
        Check(incomplete.State == OpenCodeTodoState.Listed && incomplete.Complete && incomplete.SourceMessageId == "msg_old",
            "latest completed write in a contiguous suffix is authoritative without older pages");
        var missing = Derive(Array.Empty<OpenCodeMessage>(), complete: false);
        Check(missing.State == OpenCodeTodoState.Unknown && !missing.Complete && missing.Reason.Contains("未取全"), "unfetched history is Unknown, not Empty");
        var readFailed = Derive(new[] { old }, complete: false, history: new(true, true, null));
        Check(readFailed.State == OpenCodeTodoState.ReadFailure && readFailed.Items.IsEmpty && readFailed.Reason.Contains("读取失败"),
            "messages load failure → ReadFailure (not Empty, not Unknown)");
        Check(Derive(new[] { clear }, complete: false) is { Complete: true, State: OpenCodeTodoState.Empty },
            "latest completed clear is authoritative without older pages");
        foreach (var input in new object[] { new { }, new { todos = "not-array" }, "{\"todos\":[]}" })
        {
            var bad = Message("msg_bad", Tool("completed", input));
            var result = Derive(new[] { old, bad });
            Check(result.State == OpenCodeTodoState.Empty && result.SourceMessageId == "msg_bad" && result.Items.IsEmpty,
                "official US: non-array/missing todos normalizes to empty, never falls back to old tasks");
            Check(Derive(new[] { bad, clear }).State == OpenCodeTodoState.Empty, "later valid replacement repairs older malformed write");
        }
        Check(Derive(new[] { old, current with { Content = null } }).State == OpenCodeTodoState.ReadFailure,
            "missing assistant content after latest write → ReadFailure, not current list");
        Check(Derive(new[] { old, Message("msg_null_part", new OpenCodeContentPart[] { null! }) }).State == OpenCodeTodoState.ReadFailure,
            "null content part → ReadFailure");
        var fakeUser = Message("msg_user", Tool("completed", Todos("in user"))) with { Type = "user" };
        Check(Derive(new[] { old, fakeUser }).SourceMessageId == "msg_old", "non-assistant content not interpreted as tool execution");
        Check(Derive(new[] { old }, scope: Scope with { Stale = true }).State == OpenCodeTodoState.Unknown, "stale scope → Unknown");

        // Native capability gating (no plugin): experimental.todo + history evidence.
        var off = Derive(new[] { Message("msg_no_tools") }, config: OpenCodeTodoConfig.Disabled);
        Check(off.State == OpenCodeTodoState.Disabled && off.Reason == "当前后端未启用 To Do（experimental.todo）"
            && off.Capability.Availability == OpenCodeCapabilityAvailability.Unavailable, "experimental.todo off + no writes → honest capability gap");
        var offWithHistory = Derive(new[] { old }, config: OpenCodeTodoConfig.Disabled);
        Check(offWithHistory.State == OpenCodeTodoState.Listed && offWithHistory.Capability.Availability == OpenCodeCapabilityAvailability.Available,
            "native todowrite in history is itself capability evidence");
        Check(Derive(new[] { ignored }, config: OpenCodeTodoConfig.Disabled).Capability.Availability == OpenCodeCapabilityAvailability.Available,
            "native todowrite presence proves capability even before completion, without inventing its unfinished list");
        var configUnknown = Derive(new[] { Message("msg_no_tools") }, config: OpenCodeTodoConfig.Unknown);
        Check(configUnknown.State == OpenCodeTodoState.Unknown && configUnknown.Capability.Availability == OpenCodeCapabilityAvailability.Unknown
            && configUnknown.Reason.Contains("experimental.todo"), "config read failure + no writes → Unknown, not Disabled/Empty");
        Check(Derive(new[] { old }, config: OpenCodeTodoConfig.Unknown).State == OpenCodeTodoState.Listed, "history evidence independent of config read");
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
        Check(absent.Plugins.Availability == OpenCodeCapabilityAvailability.Available && absent.Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "successful empty plugin directory says nothing about native to do");
        var unknown = OpenCodeCapabilityStatus.Create(Scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.NotObserved(), OpenCodeCapabilityRead<OpenCodePluginInfo>.NotObserved());
        Check(unknown.Todo.Availability == OpenCodeCapabilityAvailability.Unknown && unknown.Omo.Availability == OpenCodeCapabilityAvailability.Unknown, "unobserved plugins cannot be enabled");
        var local = Capabilities(new OpenCodePluginInfo { Source = new() { Type = "local", Path = "/fake/opencode-todolist" }, Features = new() { Server = true }, State = new() { Status = "active" } });
        Check(local.Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "local path substring is not installation evidence");
        Check(Capabilities(Plugin("not-opencode-todolist")).Todo.Availability != OpenCodeCapabilityAvailability.Available, "substring package match rejected");
        Check(Capabilities(Plugin("opencode-todolist", server: null)).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "TUI-only declaration says nothing about native to do");
        Check(Capabilities(Plugin("opencode-todolist", "failed")).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "failed plugin says nothing about native to do");
        Check(Capabilities(Plugin("opencode-todolist", "future")).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "unknown plugin runtime never inferred active");
        Check(Capabilities(TodoPlugin, TodoPlugin).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "conflicting duplicate identities are uncertain");
        Check(Capabilities(Plugin("billion-context")).Billion.Availability == OpenCodeCapabilityAvailability.Unavailable, "listed active Billion plugin still does not authorize management requests");
        var available = Capabilities(TodoPlugin, OmoPlugin);
        var stale = OpenCodeCapabilityStatus.Create(Scope with { Stale = true }, OpenCodeCapabilityRead<OpenCodeMcpServer>.Success([]), OpenCodeCapabilityRead<OpenCodePluginInfo>.Success([TodoPlugin, OmoPlugin]));
        Check(stale.Todo.Availability == OpenCodeCapabilityAvailability.Unknown && stale.PluginEntries.All(row => row.Running is null), "stale evidence cannot enable feature or show current running");
        foreach (var gated in new[] { absent, unknown, local, stale })
        {
            // A10: plugin presence no longer gates native to do (no silent plugin source either way).
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
        Check(withBuiltin.Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "built-in and third-party plugins do not prove native to do capability");
        Check(Capabilities(builtin).Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "built-ins alone cannot prove native to do capability");
        Check(Capabilities(builtin, new OpenCodePluginInfo { Source = new() { Type = "sdk" }, Features = new() { Server = true }, State = new() { Status = "active" } })
            .Todo.Availability == OpenCodeCapabilityAvailability.Unknown, "anonymous sdk plugin still keeps target uncertain");
        Check(withBuiltin.PluginEntries.Length == 2, "built-ins remain in the snapshot; only the connection view hides them");

        var mutable = new List<OpenCodePluginInfo> { TodoPlugin };
        var copied = OpenCodeCapabilityRead<OpenCodePluginInfo>.Success(mutable);
        mutable.Clear();
        Check(copied.Items.Length == 1 && copied.Complete, "read result is detached from caller's mutable list");
    }
}
