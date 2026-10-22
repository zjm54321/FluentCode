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
            && controller.Snapshot.Todo.Note == "当前后端未启用 to do（experimental.todo）", "config disabled → honest text");
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
        Check(controller.Snapshot.Todo.Value?.State == OpenCodeTodoState.Unknown, "partial pagination → Unknown");
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
    }
}
