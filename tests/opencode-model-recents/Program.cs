using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); _checks++; }
    private static OpenCodeModelInfo Model(string provider, string id, string name) => new() { ProviderId = provider, Id = id, Name = name };
    private static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "model-recents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await History(root);
            await Sends(root);
            Console.WriteLine($"PASS {_checks} assertions: MRU/persistence and production enhanced + extracted verbatim legacy send, fake HTTP only.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task History(string root)
    {
        var path = Path.Combine(root, "history.json"); var store = new OpenCodeRecentModels(path);
        var a = Model("a", "same", "Zulu"); var b = Model("b", "same", "Alpha");
        Check(store.Order("s", new[] { a, b }).SequenceEqual(new[] { b, a }), "unused name order before load");
        await store.RecordSuccessfulUseAsync("s", "a", "same");
        await store.RecordSuccessfulUseAsync("other", "b", "same");
        Check(store.Order("s", new[] { b, a }).First() == a, "source/provider collision isolation");
        await store.RecordSuccessfulUseAsync("s", "b", "same");
        await store.RecordSuccessfulUseAsync("s", "a", "same");
        var entries = JsonSerializer.Deserialize<OpenCodeRecentModels.Entry[]>(await File.ReadAllTextAsync(path))!;
        Check(entries.Count(e => e.Source == "s" && e.ProviderId == "a") == 1, "same model dedupes independent of variant (no variant API/field)");
        var reloaded = new OpenCodeRecentModels(path); await reloaded.LoadAsync();
        Check(reloaded.Order("s", new[] { a, b }).First() == a, "restart MRU reload");
        Check(reloaded.Order("s", new[] { b }).Count == 1 && reloaded.Order("s", new[] { a, b }).First() == a, "stale catalog intersection does not prune history");
        var ties = new[] { Model("z", "a", "Equal"), Model("a", "z", "Equal"), Model("a", "a", "Equal") };
        Check(store.Order("unused", ties).Select(m => m.ProviderId + m.Id).SequenceEqual(new[] { "aa", "az", "za" }), "name tie provider/id deterministic");
        await store.RecordSuccessfulUseAsync("a:b", "c", "d");
        await store.RecordSuccessfulUseAsync("a", "b:c", "d");
        entries = JsonSerializer.Deserialize<OpenCodeRecentModels.Entry[]>(await File.ReadAllTextAsync(path))!;
        Check(entries.Any(e => e.Source == "a:b" && e.ProviderId == "c") && entries.Any(e => e.Source == "a" && e.ProviderId == "b:c"), "tuple delimiter collisions stay distinct");
        var queued = Enumerable.Range(0, 90).Select(i => store.RecordSuccessfulUseAsync("q", "p", "m" + i)).ToArray();
        await Task.WhenAll(queued);
        entries = JsonSerializer.Deserialize<OpenCodeRecentModels.Entry[]>(await File.ReadAllTextAsync(path))!;
        Check(entries.Length == OpenCodeRecentModels.Capacity && entries[0].ModelId == "m89" && entries[^1].ModelId == "m26", "concurrent queue bounded and ordered by acknowledgement invocation");
        await store.RecordSuccessfulUseAsync(new string('x', 1025), "p", "bad");
        Check(!File.ReadAllText(path).Contains("bad"), "oversized key ignored");
        var corrupt = Path.Combine(root, "corrupt.json"); await File.WriteAllTextAsync(corrupt, "[null,{\"Source\":\"s\"}]");
        var broken = new OpenCodeRecentModels(corrupt); await broken.LoadAsync();
        Check(broken.Order("s", new[] { a, b }).First() == b, "invalid load entries ignored");
        await File.WriteAllTextAsync(corrupt, "{{invalid"); broken = new(corrupt); await broken.LoadAsync();
        await broken.RecordSuccessfulUseAsync("s", "a", "same");
        Check(broken.Order("s", new[] { a, b }).First() == a, "corrupt JSON fails soft and later successful use persists");
        await File.WriteAllBytesAsync(corrupt, new byte[OpenCodeRecentModels.MaximumFileBytes + 1]);
        broken = new(corrupt); await broken.LoadAsync();
        Check(broken.Order("s", new[] { a, b }).First() == b, "oversized file rejected");
        // Exclusive reader permits loading old valid JSON but denies replacement on Windows.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var before = await File.ReadAllTextAsync(path);
            await store.RecordSuccessfulUseAsync("failure", "a", "same");
            Check(await File.ReadAllTextAsync(path) == before, "atomic replacement failure preserves old bytes");
            Check(store.Order("failure", new[] { b, a }).First() == a, "persistence failure retains memory usage");
            Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "failed atomic replacement cleans temporary file");
        }
        var blocker = Path.Combine(root, "not-directory"); await File.WriteAllTextAsync(blocker, "block");
        var unwritable = new OpenCodeRecentModels(Path.Combine(blocker, "recent.json"));
        await unwritable.LoadAsync(); await unwritable.RecordSuccessfulUseAsync("s", "a", "same");
        Check(unwritable.Order("s", new[] { b, a }).First() == a, "directory I/O failure never throws");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string? ModelId = "captured";
        internal string Provider = "provider";
        internal int Posts;
        internal new Func<CancellationToken, Task<HttpResponseMessage>> Send = _ => Task.FromResult(Json("{\"data\":{\"id\":\"msg_real\",\"sessionID\":\"ses_test\",\"type\":\"user\",\"payload\":{\"text\":\"accepted\"}}}"));
        internal static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) { Posts++; return Send(ct); }
            return Task.FromResult(Json(JsonSerializer.Serialize(new { data = new {
                id = "ses_test", projectID = "project", cost = 0, tokens = new { },
                time = new { created = 1, updated = 1 }, location = new { directory = "/workspace" },
                model = ModelId is null ? null : new { id = ModelId, providerID = Provider, variant = "high" }
            }}, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })));
        }
    }

    private static async Task Sends(string root)
    {
        foreach (var legacy in new[] { false, true })
        foreach (var scenario in new[] { "success", "stale-success", "reject", "unknown", "cancel", "unknown-model", "persistence-failure", "cancel-before-start" })
        {
            Console.WriteLine($"Send case: legacy={legacy}, {scenario}");
            using var handler = new Handler(); using var http = new HttpClient(handler);
            using var client = new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid" }, true);
            var source = new OpenCodePageSource("stable-source", 1, () => new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid" }, true));
            var binding = new OpenCodeComposerBinding(source, 1, 1, client, 1, 1, "ses_test");
            var owned = true; var gate = legacy ? OpenCodeInteractionGate.Shared : new OpenCodeInteractionGate();
            var core = new OpenCodeComposerController(_ => owned, gate); core.Bind(binding); core.SetText(core.Snapshot.Token, "draft");
            if (!legacy)
            {
                var ticket = core.BeginPick(core.Snapshot.Token)!;
                await core.AddFilesAsync(ticket, new[] { new OpenCodeComposerFileSource("a.txt", null, _ => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1 }))) });
            }
            var basis = core.CaptureDraft(core.Snapshot.Token)!;
            var origin = new SessionControlOrigin(source, 1, "ses_test", 1);
            var context = new OpenCodeComposerSendContext(origin, basis, null, _ => owned, _ => owned);
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.Send = _ => { admitted.SetResult(); return reply.Task; };
            if (scenario == "unknown-model") handler.ModelId = null;
            var path = Path.Combine(root, (legacy ? "legacy-" : "enhanced-") + scenario + ".json");
            if (scenario == "persistence-failure") Directory.CreateDirectory(path);
            var history = new OpenCodeRecentModels(path); Task recorded = Task.CompletedTask;
            using var cancellation = new CancellationTokenSource();
            if (scenario == "cancel-before-start") cancellation.Cancel();
            var pending = legacy
                ? new OpenCodePage(core, gate, history).Send(context, cancellation.Token)
                : core.SubmitAsync(context, cancellation.Token, promptAccepted: model => recorded = history.RecordSuccessfulUseAsync(source.Identity, model?.ProviderId, model?.Id));
            if (scenario == "cancel-before-start")
            {
                await pending; await history.LoadAsync();
                Check(handler.Posts == 0 && !File.Exists(path) && core.Snapshot.HasDraft, "cancel-before-start neither sends nor records nor clears");
                continue;
            }
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!File.Exists(path), "not recorded before ack: " + scenario);
            handler.ModelId = "changed-selection"; handler.Provider = "changed-provider";
            if (scenario == "stale-success") owned = false;
            if (scenario == "reject") reply.SetResult(new(HttpStatusCode.BadRequest));
            else if (scenario == "unknown") reply.SetResult(Handler.Json("{}"));
            else if (scenario == "cancel") { cancellation.Cancel(); reply.SetCanceled(); }
            else reply.SetResult(Handler.Json("{\"data\":{\"id\":\"msg_real\",\"sessionID\":\"ses_test\",\"type\":\"user\",\"payload\":{\"text\":\"accepted\"}}}"));
            var result = await pending;
            await recorded; await history.LoadAsync(); // Queue barrier includes the legacy fire-and-forget record.
            var captured = Model("provider", "captured", "Zulu"); var fallback = Model("changed-provider", "changed-selection", "Alpha");
            var success = scenario is "success" or "stale-success" or "persistence-failure";
            Check(history.Order(source.Identity, new[] { fallback, captured }).First() == (success ? captured : fallback), "ack-only captured identity: " + legacy + "/" + scenario);
            Check(handler.Posts == 1, "one mutation, never resend: " + scenario);
            if (scenario == "persistence-failure") Check(result is OpenCodeComposerSubmitOutcome.PromptAccepted, "persistence failure cannot rewrite accepted send");
            if (scenario == "stale-success") Check(result is OpenCodeComposerSubmitOutcome.OriginLost, "stale receipt leaves original semantics");
            if (scenario is "reject" or "unknown" or "cancel") Check(core.Snapshot.HasDraft, "failed/unknown/cancelled preserves draft");
            if (legacy)
            {
                using var lease = await gate.EnterAsync(source.Identity, "ses_test");
                // Legacy unstaged requests deliberately install no shared unknown-result tombstone.
                Check(lease.GetComposerSubmissionProtection() is null, "legacy protection policy unchanged");
            }
        }
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly OpenCodeComposerController _composerController;
        private readonly OpenCodeSessionControlCoordinator _sessionControls;
        private readonly OpenCodeRecentModels _testHistory;
        internal OpenCodePage(OpenCodeComposerController controller, OpenCodeInteractionGate gate, OpenCodeRecentModels history)
        { _composerController = controller; _sessionControls = new(gate); _testHistory = history; }
        internal Task<OpenCodeComposerSubmitOutcome> Send(OpenCodeComposerSendContext context, CancellationToken ct) => SendLegacyAttemptAsync(context, ct,
            model => _ = _testHistory.RecordSuccessfulUseAsync(context.Basis.Binding.Source.Identity, model?.ProviderId, model?.Id));
    }
}
