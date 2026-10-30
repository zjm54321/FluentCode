using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private sealed class PresetHttp : HttpMessageHandler
    {
        internal int Reads;
        internal readonly List<string> Directories = new();
        internal Func<string, CancellationToken, Task<HttpResponseMessage>> Respond =
            (id, _) => Task.FromResult(PresetReply(id, "bound", "真实预设"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/rpc/oh-my-opencode-slim.session-preset.v1/read", StringComparison.Ordinal))
                return new(HttpStatusCode.NotFound);
            Reads++;
            Check(request.Method == HttpMethod.Post, "preset metadata uses its dedicated read RPC, not global configuration");
            Directories.Add(request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var id = body.RootElement.GetProperty("input").GetProperty("sessionID").GetString()!;
            return await Respond(id, token);
        }
    }

    private static HttpResponseMessage PresetReply(string id, string status, string? name)
        => Json(JsonSerializer.Serialize(new { output = new { sessionID = id, status, presetName = name, affects = "future-children-only" } }));

    private static (PageStub Page, PresetHttp Http, OpenCodeSessionInsightsController Controller) PresetFixture()
    {
        var handler = new PresetHttp();
        var client = new OpenCodeClient(new HttpClient(handler), new() { BaseUrl = "https://offline.invalid/prefix",
            AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var page = new PageStub { Client = client, SessionId = "ses_preset", Revision = 1 };
        page.Source = new OpenCodePageSource("preset-test", 1, () => client);
        return (page, handler, new OpenCodeSessionInsightsController(page.IsCurrent));
    }

    private static async Task SessionPresetMetadata()
    {
        var (page, http, controller) = PresetFixture();
        var binding = page.Capture();
        const string directory = "/work/用户 project";
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None);
        var section = controller.SessionPresetFor(binding, directory);
        Check(section.Value?.IsFor(page.Client, binding.SessionId, directory) == true
            && section.Value.Status == OpenCodeSessionPresetStatus.Bound, "named preset retains exact client/session/directory scope");
        Check(section.Value?.PresetName == "真实预设" && section.Note.Contains("之后新建的子会话"),
            "preset name and future-children-only scope are retained");
        Check(Uri.UnescapeDataString(http.Directories[0]).Contains(directory), "RPC carries the exact verified session directory");
        for (var i = 0; i < 8; i++) await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None);
        controller.UpdateContext(binding, Array.Empty<OpenCodeMessage>(), true);
        await controller.LoadAsync(binding, Array.Empty<OpenCodeMessage>(), true, CancellationToken.None);
        Check(http.Reads == 1 && controller.Snapshot.Preset.Value?.PresetName == "真实预设",
            "re-render, context events and general insights loads do not poll the preset RPC");

        http.Respond = (id, _) => Task.FromResult(PresetReply(id, "unbound", "not-the-global-default"));
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None, refresh: true);
        Check(http.Reads == 2 && controller.Snapshot.Preset.Value?.Status == OpenCodeSessionPresetStatus.Unbound,
            "explicit refresh reports unbound even when a global-looking name is supplied");
        http.Respond = (id, _) => Task.FromResult(PresetReply(id, "bound", null));
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None, refresh: true);
        Check(controller.Snapshot.Preset.Value?.Status == OpenCodeSessionPresetStatus.Bound && controller.Snapshot.Preset.Value.PresetName is null,
            "bound without name is neither unbound nor a guessed preset");

        http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None, refresh: true);
        Check(controller.Snapshot.Preset.State == OpenCodeInsightState.Unavailable && controller.Snapshot.Preset.Value is null
            && controller.Snapshot.Preset.Note.Contains("不支持"), "unsupported RPC is unavailable, not unbound");
        http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("{\"type\":\"rpc.unavailable\",\"secret\":\"DO_NOT_DISPLAY\"}") });
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None, refresh: true);
        Check(controller.Snapshot.Preset.State == OpenCodeInsightState.Unavailable && controller.Snapshot.Preset.Note.Contains("暂时")
            && !controller.Snapshot.Preset.Note.Contains("DO_NOT_DISPLAY"), "temporary RPC unavailability stays distinct in safe tooltip copy");
        http.Respond = (_, _) => Task.FromResult(Json("{\"output\":{\"sessionID\":\"wrong\"}}"));
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None, refresh: true);
        Check(controller.Snapshot.Preset.State == OpenCodeInsightState.Failed && controller.Snapshot.Preset.Value is null,
            "malformed or foreign session response never supplies a displayed name");
        var attempts = http.Reads;
        await controller.LoadSessionPresetAsync(binding, directory, CancellationToken.None);
        Check(http.Reads == attempts, "failed and unsupported reads do not retry themselves");
        Check(controller.SessionPresetFor(binding, "/other").State == OpenCodeInsightState.NotLoaded,
            "same session at a different directory cannot display old preset");
        await controller.LoadSessionPresetAsync(binding, " ", CancellationToken.None);
        Check(http.Reads == attempts, "unknown/empty directory never falls back to a global location");
        controller.Reset();
        Check(controller.Snapshot.Preset.Value is null && controller.Snapshot.Preset.State == OpenCodeInsightState.NotLoaded,
            "binding reset clears named and unsupported preset state");
    }

    private static async Task SessionPresetOwnership()
    {
        var (page, http, controller) = PresetFixture();
        var binding = page.Capture();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Respond = (_, _) => { entered.TrySetResult(true); return held.Task; };
        var old = controller.LoadSessionPresetAsync(binding, "/old", CancellationToken.None);
        await entered.Task;
        for (var i = 0; i < 5; i++) _ = controller.LoadSessionPresetAsync(binding, "/old", CancellationToken.None);
        Check(http.Reads == 1, "in-flight binding load is single-flight across repeated layout notifications");
        http.Respond = (id, _) => Task.FromResult(PresetReply(id, "bound", "new directory"));
        await controller.LoadSessionPresetAsync(binding, "/new", CancellationToken.None);
        held.SetResult(PresetReply(binding.SessionId, "bound", "old directory")); await old;
        Check(controller.Snapshot.Preset.Value?.PresetName == "new directory"
            && controller.SessionPresetFor(binding, "/old").Value is null, "late old-directory result and finally cannot replace new metadata");

        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        http.Respond = (_, _) => { entered.TrySetResult(true); return held.Task; };
        var previousSession = controller.LoadSessionPresetAsync(binding, "/new", CancellationToken.None, refresh: true);
        await entered.Task;
        page.SessionGeneration++; page.SessionId = "ses_other"; controller.Reset();
        var next = page.Capture();
        http.Respond = (id, _) => Task.FromResult(PresetReply(id, "unbound", null));
        await controller.LoadSessionPresetAsync(next, "/new", CancellationToken.None);
        held.SetResult(PresetReply(binding.SessionId, "bound", "foreign name")); await previousSession;
        Check(controller.Snapshot.Preset.Value?.SessionId == "ses_other" && controller.Snapshot.Preset.Value.Status == OpenCodeSessionPresetStatus.Unbound,
            "session switch rejects old name even after its HTTP reply completes");
        var accepted = controller.Snapshot.Preset.Value!;
        var replacement = PresetFixture();
        Check(!accepted.IsFor(replacement.Page.Client, "ses_other", "/new"), "identical server URL with a different client never owns a preset snapshot");
        page.PageGeneration++;
        Check(controller.SessionPresetFor(next, "/new").Value is null, "old page generation cannot expose preset data");
        next = page.Capture();
        await controller.LoadSessionPresetAsync(next, "/new", CancellationToken.None);
        Check(controller.Snapshot.Preset.Value?.IsFor(page.Client, "ses_other", "/new") == true,
            "new page binding reads once instead of inheriting an earlier generation");
        var reads = http.Reads;
        page.Active = false;
        await controller.LoadSessionPresetAsync(next, "/new", CancellationToken.None, refresh: true);
        Check(http.Reads == reads && controller.SessionPresetFor(next, "/new").Value is null, "hidden/unloaded owner neither starts RPC nor exposes retained value");
        page.Active = true;
        page.Source!.Revoke();
        await controller.LoadSessionPresetAsync(next, "/new", CancellationToken.None, refresh: true);
        Check(http.Reads == reads, "revoked source cannot initiate preset read");
    }
}
