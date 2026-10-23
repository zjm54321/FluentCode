using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class TodoControllerTests
{
    private static void Check(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }

    private sealed class Http : HttpMessageHandler
    {
        internal string Config = "true";
        internal TaskCompletionSource<bool>? Gate;
        internal TaskCompletionSource<bool> Entered = new();
        internal int TodoRoutes;
        internal string[] Pages = ["{\"data\":[],\"cursor\":{\"next\":null}}"];
        internal int MessageReads;
        internal TaskCompletionSource<bool>? MessageGate;
        internal TaskCompletionSource<bool> MessageEntered = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("todo")) TodoRoutes++;
            var json = "{}";
            if (path == "/api/config")
            {
                Check(request.RequestUri.Query.Contains("location%5Bdirectory%5D=C%3A%2Fwork"), "config is directory-scoped");
                var config = Config;
                Entered.TrySetResult(true);
                if (Gate is not null) await Gate.Task; // Deliberately completes even if the page canceled its token.
                if (config == "fail") return new(HttpStatusCode.InternalServerError);
                json = "[{\"type\":\"document\",\"info\":{\"experimental\":{\"todo\":" + config + "}}}]";
            }
            else if (path.EndsWith("/message"))
            {
                var index = MessageReads++;
                Check(request.RequestUri.Query.Contains("limit=100"), "bounded message pages");
                Check(!request.RequestUri.Query.Contains("cursor=") || !request.RequestUri.Query.Contains("order="), "continuations never combine cursor and order");
                var page = Pages[Math.Min(index, Pages.Length - 1)];
                MessageEntered.TrySetResult(true);
                if (MessageGate is not null) await MessageGate.Task;
                if (page == "fail") return new(HttpStatusCode.InternalServerError);
                json = page;
            }
            else if (path.StartsWith("/api/session/"))
                json = "{\"data\":{\"id\":\"" + path.Split('/').Last() + "\",\"title\":\"test\",\"agent\":\"build\",\"location\":{\"directory\":\"C:/work\"},\"model\":{\"id\":\"a\",\"providerID\":\"p\"},\"time\":{\"created\":1,\"updated\":2}}}";
            else if (path == "/api/session") json = "{\"data\":[],\"cursor\":{\"previous\":null,\"next\":null}}";
            else if (path == "/api/model") json = "{\"location\":{\"directory\":\"C:/work\"},\"data\":[]}";
            else if (path == "/api/mcp") json = "[]";
            else if (path == "/api/plugin") json = "[]";
            else return new(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static OpenCodeMessage Write(string id, string todos)
        => new() { Id = id, Type = "assistant", Content = [new() { Type = "tool", Name = "todowrite",
            State = JsonSerializer.SerializeToElement(new { status = "completed", input = new { todos = JsonSerializer.Deserialize<JsonElement>(todos) } }) }] };

    internal static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var http = new Http();
        using var client = new OpenCodeClient(new HttpClient(http), new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var source = new OpenCodePageSource("todo-test", 1, () => client);
        var binding = new OpenCodeInsightsBinding(source, 1, 1, client, 1, 1, "ses_a");
        var current = binding;
        var controller = new OpenCodeSessionInsightsController(candidate => candidate == current);
        var noRevert = OpenCodeTodoHistory.NoRevert;
        await controller.LoadAsync(binding, [], true, CancellationToken.None);
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Empty, "config enabled + full empty history → Empty");

        http.Config = "false";
        await controller.LoadAsync(binding, [], true, CancellationToken.None);
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Disabled
            && controller.Snapshot.Todo.Note == "当前后端未启用 ToDo（experimental.todo）", "config disabled → honest text");
        foreach (var config in new[] { "fail", "\"bad\"" })
        {
            http.Config = config;
            await controller.LoadAsync(binding, [], true, CancellationToken.None);
            Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Unknown, "failed/malformed config → Unknown");
        }

        var old = Write("msg_old", "[{\"content\":\"old\",\"status\":\"pending\"}]");
        var latest = Write("msg_latest", "[{\"content\":\"latest\",\"status\":\"blocked\"},null,{\"content\":\"\"}]");
        controller.UpdateContext(binding, [old, latest], true, noRevert);
        Check(controller.Snapshot.Todo.Value?.SourceMessageId == "msg_latest"
            && controller.Snapshot.Todo.Value.Items.Single() == new OpenCodeTodoItem("latest", "pending"), "native message publication updates latest + normalization, without config");
        controller.UpdateContext(binding, [old, latest], true, new(false, true, "msg_latest"));
        Check(controller.Snapshot.Todo.Value?.SourceMessageId == "msg_old", "accepted revert cutoff updates locally");
        controller.UpdateContext(binding, [Write("msg_clear", "[]")], true, noRevert);
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Empty, "cleared → Empty");
        controller.UpdateContext(binding, [old], false, noRevert);
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Listed, "completed write in latest window needs no older history");
        controller.UpdateContext(binding, [old], false, new(true, true, null));
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.ReadFailure, "history read failure → ReadFailure");

        // Late config read must use the latest accepted messages, not the explicit read's starting list.
        http.Config = "true";
        http.Gate = new();
        http.Entered = new();
        var pending = controller.LoadAsync(binding, [old], true, CancellationToken.None);
        await http.Entered.Task;
        controller.UpdateContext(binding, [old, latest], true, noRevert);
        http.Gate.SetResult(true);
        await pending;
        Check(controller.Snapshot.Todo.Value?.SourceMessageId == "msg_latest", "late config cannot restore older same-session list");

        http.Gate = new();
        http.Entered = new();
        pending = controller.LoadAsync(binding, [old], true, CancellationToken.None);
        await http.Entered.Task;
        current = binding with { SessionId = "ses_b", SessionGeneration = 2 };
        controller.Reset();
        http.Gate.SetResult(true);
        await pending;
        Check(controller.Snapshot.Todo.Value is null, "late previous session/epoch result discarded");
        controller.UpdateContext(binding, [old], true, noRevert);
        Check(controller.Snapshot.Todo.Value is null, "stale accepted-message binding discarded");
        http.Gate = null;
        await controller.LoadAsync(current, [], true, CancellationToken.None);
        Check(controller.Snapshot.Todo.Value?.SessionId == "ses_b" && controller.Snapshot.Todo.Value.Items.IsEmpty,
            "new session contains no previous session items");
        Check(http.TodoRoutes == 0, "never reads invented todo routes");
        await HistoryScanAsync();
    }

    private static string Page(string? next, params OpenCodeMessage[] descending)
        => JsonSerializer.Serialize(new { data = descending, cursor = new { next } }, OpenCodeJson.Options);

    private static async Task HistoryScanAsync()
    {
        var http = new Http();
        using var client = new OpenCodeClient(new HttpClient(http), new() { BaseUrl = "https://offline.invalid" }, true);
        var source = new OpenCodePageSource("todo-history", 1, () => client);
        var binding = new OpenCodeInsightsBinding(source, 1, 1, client, 1, 1, "ses_history");
        var current = binding;
        var controller = new OpenCodeSessionInsightsController(candidate => candidate == current);
        var recent = new OpenCodeMessage { Id = "msg_recent", Type = "user" };
        var old = Write("msg_old", "[{\"content\":\"older page\",\"status\":\"pending\"}]");
        await controller.LoadAsync(binding, [old], false, CancellationToken.None);
        Check(http.MessageReads == 0 && controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Listed,
            "window containing completed todowrite requires zero history reads");
        http.Pages = [Page("older", recent), Page("not-needed", old)];
        await controller.LoadAsync(binding, [recent], false, CancellationToken.None);
        Check(http.MessageReads == 2 && controller.Snapshot.Todo.Value?.SourceMessageId == "msg_old",
            "auto-pages without scrolling and stops at latest completed todowrite before history end");
        await controller.LoadAsync(binding, [recent], false, CancellationToken.None);
        Check(http.MessageReads == 2, "same session replay cache avoids repeat pagination");
        current = binding with { SessionId = "ses_else", SessionGeneration = 2 };
        controller.Reset();
        await controller.LoadAsync(current, [], true, CancellationToken.None);
        current = binding with { SessionGeneration = 3 };
        controller.Reset();
        await controller.LoadAsync(current, [recent], false, CancellationToken.None);
        Check(http.MessageReads == 2 && controller.Snapshot.Todo.Value?.SourceMessageId == "msg_old", "per-session cache survives tab switching");

        controller.InvalidateTodo(current.SessionId);
        http.MessageReads = 0;
        http.Pages = [Page("older", recent), Page(null, new OpenCodeMessage { Id = "msg_first", Type = "user" })];
        await controller.LoadAsync(current, [recent], false, CancellationToken.None);
        Check(http.MessageReads == 2 && controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Empty,
            "history exhausted without todowrite and enabled config yields established empty state");

        controller.InvalidateTodo(current.SessionId);
        http.MessageReads = 0;
        var reverted = Write("msg_reverted", "[{\"content\":\"must not appear\"}]");
        var cutoff = new OpenCodeMessage { Id = "msg_cut", Type = "user" };
        http.Pages = [Page("older", reverted, cutoff), Page(null, old)];
        await controller.LoadAsync(current, [reverted], false, CancellationToken.None, new(false, true, "msg_cut"));
        Check(http.MessageReads == 2 && controller.Snapshot.Todo.Value?.SourceMessageId == "msg_old", "scan finds missing revert cutoff and excludes reverted writes");

        controller.InvalidateTodo(current.SessionId);
        http.MessageReads = 0;
        http.Pages = ["fail"];
        await controller.LoadAsync(current, [recent], false, CancellationToken.None);
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.ReadFailure, "genuine message request failure remains unavailable, never empty");
        http.Pages = [Page(null, old)];
        await controller.LoadAsync(current, [recent], false, CancellationToken.None);
        Check(controller.Snapshot.Todo.Value?.SourceMessageId == "msg_old", "explicit refresh retries failed scan");

        controller.InvalidateTodo(current.SessionId);
        http.MessageReads = 0;
        http.Pages = [Page("loop", recent)];
        await controller.LoadAsync(current, [recent], false, CancellationToken.None);
        Check(http.MessageReads == 2 && controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.ReadFailure,
            "repeated cursor stops with genuine protocol failure rather than looping or showing empty");

        controller.InvalidateTodo(current.SessionId);
        http.MessageGate = new();
        http.MessageEntered = new();
        var pending = controller.LoadAsync(current, [recent], false, CancellationToken.None);
        await http.MessageEntered.Task;
        var latest = Write("msg_live", "[{\"content\":\"live completion\"}]");
        controller.UpdateContext(current, [recent, latest], false, OpenCodeTodoHistory.NoRevert);
        http.MessageGate.SetResult(true);
        await pending;
        Check(controller.Snapshot.Todo.Value?.SourceMessageId == "msg_live", "todowrite completion beats an older in-flight scan");

        controller.InvalidateTodo(current.SessionId);
        http.MessageGate = new();
        http.MessageEntered = new();
        pending = controller.LoadAsync(current, [recent], false, CancellationToken.None);
        await http.MessageEntered.Task;
        current = current with { SessionId = "ses_new", SessionGeneration = 4 };
        controller.Reset();
        http.MessageGate.SetResult(true);
        await pending;
        Check(controller.Snapshot.Todo.Value is null, "late history response discarded after cancellation/session change");

        http.MessageGate = null;
        http.MessageReads = 0;
        await controller.LoadAsync(current, [recent], false, CancellationToken.None, new(false, false, null));
        Check(http.MessageReads == 0, "unknown revert boundary waits for authoritative session metadata");
        http.MessageGate = new();
        http.MessageEntered = new();
        http.Pages = [Page("older", recent)];
        pending = controller.LoadAsync(current, [recent], false, CancellationToken.None);
        await http.MessageEntered.Task;
        source.Revoke();
        http.MessageGate.SetResult(true);
        await pending;
        Check(http.MessageReads == 1 && controller.Snapshot.Todo.Value?.SourceMessageId is null,
            "source revocation blocks late application and all older-page starts");
    }
}
