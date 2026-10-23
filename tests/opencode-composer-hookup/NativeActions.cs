// Page native-action integration. Real Page partials/core/coordinator/client/gate and immutable UI models;
// dialog gestures, XAML controls and HttpMessageHandler are explicit doubles. ContinuationActions.cs covers Phase B.
#pragma warning disable CS0067, CS0169, CS0414, CS0649
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class NativeTests
{
    private static int checks;
    private static void Check(bool value, string note) { checks++; if (!value) throw new Exception(note); }
    internal static async Task Run()
    {
        // This process exercises the real asynchronous load queue, never the user's persisted history.
        typeof(OpenCodeRecentModels).GetField("_path", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(OpenCodeRecentModels.Shared, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "isolated-normal-picker-recents.json"));
        typeof(OpenCodeRecentModels).GetField("_loaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(OpenCodeRecentModels.Shared, true);
        await TypedAndModels(); var a = checks;
        await SidebarModelChoices();
        await RestorationAndOwnership(); var b = checks - a;
        await GuardsAndEpoch();
        Console.WriteLine($"PASS: 3 Page-native groups, {checks} assertions ({a}/{b}/{checks-a-b}); real production Page/core/backend; HTTP/WinUI/dialog doubles; separate continuation groups follow.");
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly Program.Handler Http = new();
        internal readonly OpenCodePageSource Source;
        internal readonly OpenCodeClient Client;
        internal readonly OpenCodePage Page;
        internal string? Model, Stage;
        internal string? LastMutationBody;
        internal string? LastMutationPath;
        internal readonly List<string> MutationPaths = new();
        internal string Catalog = ModelInfo;
        internal string Default = ModelInfo;
        internal string History = "{\"data\":[{\"id\":\"msg_idle\",\"type\":\"idle\",\"time\":{\"created\":3},\"outcome\":\"succeeded\"}," + User("newer", 2) + "," + User("older", 1) + "],\"cursor\":{\"next\":null}}";
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>>? Mutation;
        internal Fixture()
        {
            OpenCodeClient Create() => new(new HttpClient(Http, false), new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
            Http.Respond = Respond;
            Client = Create(); Source = new("native-" + Guid.NewGuid(), 7, Create); Page = new(Source, Client);
            OpenCodeModelPickerDialog.Next = p => Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, p));
            OpenCodeVariantPickerDialog.Next = p => Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, p));
            OpenCodeRollbackConfirmationDialog.Next = p => Task.FromResult(new OpenCodeRollbackConfirmationResult(OpenCodeRollbackOutcome.Confirmed, p, false, true));
            OpenCodeDraftEditorDialog.Next = (_, _) => Task.FromResult(new OpenCodeDraftEditorResult(OpenCodeDraftEditorOutcome.Cancelled, null));
            OpenCodeComposerContinuationDialog.Next = p => Task.FromResult(new OpenCodeContinuationResult(OpenCodeContinuationOutcome.Cancelled, p));
        }
        internal const string ModelInfo = "{\"id\":\"catalog-id\",\"modelID\":\"provider-alias\",\"providerID\":\"provider\",\"name\":\"Choice\",\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]},\"variants\":[{\"id\":\"fast\"}],\"time\":{\"released\":1},\"cost\":[],\"status\":\"active\",\"enabled\":true,\"limit\":{\"context\":1000,\"output\":100}}";
        private static string User(string id, int time) => $"{{\"id\":\"msg_{id}\",\"type\":\"user\",\"time\":{{\"created\":{time}}},\"text\":\"{id} text\"}}";
        private Task<HttpResponseMessage> Respond(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method != HttpMethod.Get)
            {
                LastMutationBody = Http.Body;
                LastMutationPath = path; MutationPaths.Add(path);
                if (Mutation is not null) return Mutation(r);
                if (path.EndsWith("/revert/stage")) { using var body = JsonDocument.Parse(Http.Body!); Stage = "{\"messageID\":" + body.RootElement.GetProperty("messageID").GetRawText() + "}"; return Task.FromResult(Program.Json("{\"data\":" + Stage + "}")); }
                if (path.EndsWith("/revert")) Stage = null;
                if (path.EndsWith("/model")) { using var body = JsonDocument.Parse(Http.Body!); Model = body.RootElement.GetProperty("model").GetRawText(); }
                if (path == "/api/session") return Task.FromResult(Program.Json("{\"data\":{\"id\":\"ses_created\"}}"));
                if (path.EndsWith("/interrupt")) return Task.FromResult(Program.Json("{\"interrupted\":true}"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            var json = path switch {
                "/api/command" => "{\"data\":[{\"name\":\"models\",\"description\":\"collision\"}]}",
                "/api/model" => "{\"location\":{\"directory\":\"/synthetic\"},\"data\":[" + Catalog + "]}",
                "/api/model/default" => "{\"location\":{\"directory\":\"/synthetic\"},\"data\":" + Default + "}",
                "/api/session/active" => "{\"data\":{}}",
                "/api/session/ses_test" => "{\"data\":{\"id\":\"ses_test\",\"projectID\":\"p\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/synthetic\"}" + (Model is null ? "" : ",\"model\":" + Model) + (Stage is null ? "" : ",\"revert\":" + Stage) + "}}",
                _ when path.EndsWith("/message") => History,
                _ when path.EndsWith("/inbox") || path.EndsWith("/permission") || path.EndsWith("/form") => "{\"data\":[]}",
                _ => throw new Exception("Unexpected offline route " + path),
            };
            return Task.FromResult(Program.Json(json));
        }
        public void Dispose() { Page.UnloadFixture(); Client.Dispose(); Source.Revoke(); }
    }
    private static async Task TypedAndModels()
    {
        using (var f = new Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseSession, "sessions");
            Check(f.Page.InvokeNative(OpenCodeComposerClientActionKind.NewSession), "synchronous Accepted from typed event");
            Check(f.Page.HomeNavigations == 0 && f.Page.NewTabRequests == 0 && f.Http.Posts == 0,
                "ticket dispatch ignores forged display Kind and defers home navigation, never a blank tab");
            f.Page.DrainDispatcher();
            Check(f.Page.HomeNavigations == 1 && !f.Page.NavigationWhileNativeAction, "sessions returns home after native ownership settles");
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseAgent, "agents"); await f.Page.RunNative();
            Check(f.Page.AgentOpens == 1 && f.Http.Posts == 0, "agents opens the selector's picker dialog without switch/HTTP");
            await f.Page.StageNative(OpenCodeComposerClientActionKind.NewSession, "new"); await f.Page.RunNative();
            Check(f.Page.NewTabRequests == 1 && f.Page.NewTabNavigations == 0 && f.Http.Posts == 0 && f.Page.Created is null,
                "new requests a deferred blank tab without creating an unnamed backend session");
            f.Page.DrainDispatcher();
            Check(f.Page.NewTabNavigations == 1 && !f.Page.NavigationWhileNativeAction && f.Http.Posts == 0,
                "new navigates only after native ownership settles, still without a backend POST");
        }
        foreach (var name in new[] { "session", "sessions" })
        {
            using var f = new Fixture();
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseSession, name);
            await f.Page.RunNative();
            Check(f.Page.HomeNavigations == 0 && f.Page.NewTabRequests == 0 && f.Http.Posts == 0,
                name + " stages the native home action and defers navigation without HTTP");
            f.Page.DrainDispatcher();
            Check(f.Page.HomeNavigations == 1 && f.Page.NewTabNavigations == 0 && !f.Page.NavigationWhileNativeAction,
                name + " activates only home, not the blank conversation page");
        }
        foreach (var scenario in new[] { "explicit", "inherited", "missing", "changed", "later-edit" })
        {
            using var f = new Fixture();
            if (scenario == "explicit") f.Model = "{\"id\":\"unavailable\",\"providerID\":\"provider\",\"variant\":\"old\"}";
            if (scenario == "missing") f.Default = "null";
            OpenCodeModelPickerDialog.Next = p => {
                Check(OpenCodeModelPickerDialog.LastSourceKey == f.Source.Identity, "normal picker receives captured stable source identity");
                Check(p.Rows.Single().ModelId == "catalog-id" && p.Rows.Single().Variants.Single() == "fast", "canonical catalog key and exact variant presented");
                Check(scenario == "explicit" ? p.Current is OpenCodeModelCurrent.Explicit { Available: false, Key.Variant: "old" }
                    : p.Current is OpenCodeModelCurrent.Inherited i && (i.Default is null) == (scenario == "missing"), "explicit unavailable/inherited/missing not substituted");
                if (scenario == "changed") f.Catalog = f.Catalog.Replace("\"fast\"", "\"slow\"");
                if (scenario == "later-edit") { f.Page.DrainView(); f.Page.Text("newer draft"); }
                return Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Confirmed, new("provider", "catalog-id", "fast"), p));
            };
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseModel, "models"); await f.Page.RunNative();
            Check(f.Http.Posts == (scenario is "changed" or "later-edit" ? 0 : 1), $"changed displayed basis/draft cannot POST ({scenario}, posts={f.Http.Posts}, path={f.Http.Path}, notices={string.Join(";", f.Page.Messages)})");
            if (f.Http.Posts == 1) Check(f.LastMutationBody == "{\"model\":{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"fast\"}}" && f.Page.Working == 0, "exact selection wire, acceptance not Working");
        }
        await ModelLoadOwnership();
        await NormalVariantChoices();
    }

    private static async Task NormalVariantChoices()
    {
        // Independent strength dialog returns the existing model-choice contract. Exercise that
        // contract through the REAL normal Page preview/fingerprint/confirm/transport path;
        // native dialog gestures/rendering remain explicit doubles, not claimed as UI evidence.
        foreach (var scenario in new[] { "opaque", "default", "withdrawn", "owner-token", "owner-source", "cancel" })
        {
            using var f = new Fixture();
            f.Model = "{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"high\"}";
            f.Catalog = Fixture.ModelInfo.Replace("[{\"id\":\"fast\"}]", "[{\"id\":\"high\"},{\"id\":\"extra_high\"}]");
            var wantedVariant = scenario == "default" ? null : "extra_high";
            OpenCodeModelPickerDialog.Next = p =>
            {
                Check(OpenCodeModelPickerDialog.LastSourceKey == f.Source.Identity, "normal strength preserves captured MRU source key: " + scenario);
                Check(p.Current is OpenCodeModelCurrent.Explicit { Key.ProviderId: "provider", Key.ModelId: "catalog-id", Key.Variant: "high" }, "normal strength starts from actual current model/provider/variant");
                var row = OpenCodeModelPresentation.CurrentRow(p)!;
                var strength = new OpenCodeModelPickerState(p);
                Check(strength.SelectModel(row.ProviderId, row.ModelId) && strength.SelectVariant(wantedVariant), "strength choice uses catalog-only original opaque ID or null");
                if (scenario == "withdrawn") f.Catalog = f.Catalog.Replace("extra_high", "xhigh");
                if (scenario == "owner-token") f.Page.LoseNormalPickerOwnership("token");
                if (scenario == "owner-source") f.Page.LoseNormalPickerOwnership("source");
                return Task.FromResult(scenario == "cancel"
                    ? new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, null)
                    : strength.TryFreezeConfirmation()!);
            };
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseModel, "models");
            await f.Page.RunNative();
            var accepted = scenario is "opaque" or "default";
            Check(f.Http.Posts == (accepted ? 1 : 0), "normal strength fresh-catalog/owner/cancel fence prevents mutation: " + scenario);
            if (accepted)
            {
                var expected = scenario == "default"
                    ? "{\"model\":{\"id\":\"catalog-id\",\"providerID\":\"provider\"}}"
                    : "{\"model\":{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"extra_high\"}}";
                Check(f.LastMutationPath == "/api/session/ses_test/model" && f.LastMutationBody == expected,
                    "normal strength wire omits default/null and otherwise preserves exact variant spelling");
                Check(f.Page.Working == 0, "strength selection is not a send/Working transition");
            }
        }
    }

    private static async Task SidebarModelChoices()
    {
        foreach (var scenario in new[] { "model", "variant", "default", "withdrawn", "session-aba", "later-edit", "cancel" })
        {
            using var f = new Fixture();
            f.Model = "{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"fast\"}";
            f.Page.Text("保留当前草稿");
            await f.Page.File();
            var before = f.Page.Snapshot;
            var modelOpens = 0;
            var variantOpens = 0;
            Task<OpenCodeModelPickerResult> Choose(OpenCodeModelPickerPresentation p)
            {
                Check(p.Current is OpenCodeModelCurrent.Explicit { Key.ModelId: "catalog-id", Key.Variant: "fast" }
                    && p.Rows.Single().Variants.SequenceEqual(new[] { "fast" }), "sidebar uses real session/catalog, not cached card selection");
                if (scenario == "withdrawn") f.Catalog = f.Catalog.Replace("fast", "slow");
                if (scenario == "session-aba") { f.Page.Switch("ses_other"); f.Page.Switch("ses_test"); }
                if (scenario == "later-edit") { f.Page.DrainView(); f.Page.Text("更新的草稿"); }
                return Task.FromResult(scenario == "cancel"
                    ? new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, null)
                    : new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Confirmed, new("provider", "catalog-id", null), p));
            }
            OpenCodeModelPickerDialog.Next = p => { modelOpens++; return Choose(p); };
            OpenCodeVariantPickerDialog.Next = p => { variantOpens++; return Choose(p); };
            await f.Page.RunSidebarModel(scenario != "model");
            var accepted = scenario is "model" or "variant" or "default";
            Check(modelOpens == (scenario == "model" ? 1 : 0) && variantOpens == (scenario == "model" ? 0 : 1),
                "sidebar model/variant routes to its existing independent native picker: " + scenario);
            Check(f.Http.Posts == (accepted ? 1 : 0), "sidebar shares fresh catalog, draft and exact binding fences: " + scenario);
            if (accepted)
                Check(f.LastMutationPath == "/api/session/ses_test/model" && f.LastMutationBody == "{\"model\":{\"id\":\"catalog-id\",\"providerID\":\"provider\"}}",
                    "sidebar confirmation modifies only current session with null/default omitted");
            if (scenario != "session-aba")
                Check(f.Page.Snapshot.Text == (scenario == "later-edit" ? "更新的草稿" : before.Text)
                    && f.Page.Snapshot.Attachments.Count == before.Attachments.Count, "sidebar never injects slash text or discards files");
            Check(f.Page.Working == 0, "sidebar selection is not a send");
        }
    }

    private static async Task ModelLoadOwnership()
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var history = OpenCodeRecentModels.Shared;
        var queue = typeof(OpenCodeRecentModels).GetField("_queue", flags)!.GetValue(history)!;
        var pending = typeof(OpenCodeRecentModels).GetField("_pending", flags)!;
        foreach (var loss in new[] { "source", "client", "session", "token", "revoked" })
        {
            using var f = new Fixture();
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseModel, "models");
            var release = Program.Signal<bool>();
            lock (queue) pending.SetValue(history, release.Task);
            var opens = 0;
            OpenCodeModelPickerDialog.Next = p => {
                opens++;
                return Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Confirmed, new("provider", "catalog-id", "fast"), p));
            };
            var action = f.Page.RunNative();
            try
            {
                lock (queue) Check(!ReferenceEquals(pending.GetValue(history), release.Task), "production LoadAsync entered real queue before opening: " + loss);
                Check(!action.IsCompleted && opens == 0 && f.Http.Posts == 0, "picker waits for history load: " + loss);
                if (loss == "revoked") f.Source.Revoke();
                else f.Page.LoseNormalPickerOwnership(loss);
            }
            finally { release.TrySetResult(true); }
            await action;
            Check(opens == 0 && f.Http.Posts == 0, "load-await ownership loss cannot open or submit normal picker: " + loss);
        }
    }
    private static async Task RestorationAndOwnership()
    {
        foreach (var lateEdit in new[] { false, true })
        {
            using var f = new Fixture();
            if (lateEdit) f.Mutation = _ => { f.Stage = "{\"messageID\":\"msg_newer\"}"; f.Page.DrainView(); f.Page.Text("newer typing"); return Task.FromResult(Program.Json("{\"data\":" + f.Stage + "}")); };
            var epoch = f.Page.Epoch;
            await f.Page.StageNative(OpenCodeComposerClientActionKind.Undo, "undo"); await f.Page.RunNative();
            Check(f.Http.Posts == 1 && f.LastMutationBody == "{\"messageID\":\"msg_newer\",\"files\":false}", "fresh nearest user and explicit default files false");
            Check(f.Page.Epoch == epoch + 1 && f.Page.Boundary == "msg_newer" && f.Page.AckInvalidations == 1, "accepted undo changes same-session history epoch/ACK baseline");
            Check(f.Page.Snapshot.Text == (lateEdit ? "newer typing" : "newer text"), "original-ticket restoration or newer draft preservation");
            if (lateEdit) Check(f.Page.Messages.Any(m => m.Contains("newer text")), "later edit displays text candidate");
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Check(lease.GetSessionControlProtection() is null, "accepted settlement survives later draft edit");
        }
        using (var f = new Fixture())
        {
            f.Stage = "{\"messageID\":\"msg_newer\"}";
            await f.Page.StageNative(OpenCodeComposerClientActionKind.Redo, "redo", "keep"); await f.Page.RunNative();
            Check(f.Http.Posts == 1 && f.Stage is null && f.Page.Snapshot.Text == "keep", "redo clears entire stage without draft write");
        }
        foreach (var stale in new[] { false, true })
        {
            using var f = new Fixture();
            OpenCodeDraftEditorDialog.Next = (dialog, apply) => {
                if (stale) { f.Page.DrainView(); f.Page.Text("later"); }
                var outcome = apply("detached"); f.Page.DrainDispatcher();
                Check(outcome == (stale ? OpenCodeDraftEditorApplyOutcome.Stale : OpenCodeDraftEditorApplyOutcome.Applied), "atomic editor exact ticket");
                Check(stale || !dialog.Stale, "own successful application does not self-invalidate");
                return Task.FromResult(new OpenCodeDraftEditorResult(stale ? OpenCodeDraftEditorOutcome.ClosedStale : OpenCodeDraftEditorOutcome.Applied, stale ? "detached" : null));
            };
            await f.Page.StageNative(OpenCodeComposerClientActionKind.EditDraft, "editor", "initial"); await f.Page.RunNative();
            Check(f.Page.Snapshot.Text == (stale ? "later" : "detached") && f.Http.Posts == 0, "editor not external process or send");
        }
    }
    private static async Task GuardsAndEpoch()
    {
        using (var f = new Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.EditDraft, "editor", "draft");
            await f.Page.SendLegacyDirect(); f.Page.Click();
            Check(f.Http.Posts == 0, "pending intent blocks actual send/button");
            f.Page.EndCatalogLifetime(); await f.Page.RunNative();
            Check(f.Http.Posts == 0, "expired view lifetime yields no action ticket");
        }
        using (var f = new Fixture())
        {
            f.Page.SetWorking();
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
            { lease.BeginSessionControl(Guid.NewGuid(), SessionControlKind.SwitchModel, new string('A', 64)); }
            Check(!f.Page.ShouldStop(), "control slot blocks Stop UI probe");
            await f.Page.StopDirect(); f.Page.Text("prompt"); await f.Page.SendLegacyDirect();
            Check(f.Http.Posts == 0, "actual Stop and legacy sender enforce control slot");
        }
        using (var f = new Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.NewSession, "new");
            f.Source.Revoke(); await f.Page.RunNative();
            Check(f.Http.Posts == 0, "revoked source produces zero create POST");
        }
        using (var f = new Fixture())
        {
            var response = Program.Signal<HttpResponseMessage>(); f.Mutation = _ => response.Task;
            await f.Page.StageNative(OpenCodeComposerClientActionKind.NewSession, "new"); var action = f.Page.RunNative();
            f.Page.Switch("other"); response.SetResult(Program.Json("{\"data\":{\"id\":\"stale\"}}")); await action;
            Check(f.Page.Created is null && f.Page.Tracked == 0, "old create response cannot publish/navigate newer selection");
        }
        using (var f = new Fixture())
        {
            var read = f.Page.CaptureReadCheck(); var epoch = f.Page.Epoch;
            f.Page.AcceptHistory("newer");
            Check(!read() && f.Page.Epoch == epoch + 1, "same-session accepted history invalidates captured latest/older read");
            f.Page.ApplyHistory(new() { new() { Id = "older" }, new() { Id = "newer" }, new() { Id = "idle" } });
            Check(f.Page.VisibleIds.SequenceEqual(new[] { "older" }), "rollback boundary filters server-retained tail");
            f.Page.AcceptHistory(null); f.Page.ApplyHistory(new() { new() { Id = "older" }, new() { Id = "newer" } });
            Check(f.Page.VisibleIds.SequenceEqual(new[] { "older", "newer" }), "redo epoch permits restored whole history");
        }
        // Wave 5 G1: answering a question or replying to a permission (or settling via notification/gate)
        // never shows transient informational ack notices like "表单回答已提交"/"权限决定已提交"/"服务器已接受该决定";
        // genuine uncertain-outcome warnings and errors still show.
        using (var f = new Fixture())
        {
            f.Page.TestSettleInteraction(true, "frm_1", "表单回答已提交", "服务端已明确接受（HTTP 204）。");
            f.Page.TestSettleInteraction(false, "prm_1", "权限决定已提交", "服务端已明确接受（HTTP 204）：允许一次。");
            f.Page.TestSettleInteraction(true, "frm_2", "表单已取消", "服务端已明确接受取消（HTTP 204）。");
            Check(f.Page.Messages.Count == 0 && f.Page.RemovedInteractions == 3,
                "G1: SettleInteraction removes settled form/permission without showing transient informational ack notice");

            f.Page.TestHandleProtectedAtSubmit(isForm: true, "frm_dup", NotificationInteractionStatus.Duplicate, sessionProtected: false);
            f.Page.TestHandleProtectedAtSubmit(isForm: true, "frm_exp", NotificationInteractionStatus.Expired, sessionProtected: false);
            f.Page.TestHandleProtectedAtSubmit(isForm: false, "prm_dup", NotificationInteractionStatus.Duplicate, sessionProtected: false);
            f.Page.TestHandleProtectedAtSubmit(isForm: false, "prm_exp", NotificationInteractionStatus.Expired, sessionProtected: false);
            Check(f.Page.Messages.Count == 0 && f.Page.SuppressedInteractions == 4,
                "G1: AlreadySettled Duplicate/Expired suppresses interaction silently without transient info notice");

            f.Page.TestHandleProtectedAtSubmit(isForm: true, "frm_unk", NotificationInteractionStatus.OutcomeUnknown, sessionProtected: false);
            Check(f.Page.Messages.Count == 1 && f.Page.Messages[0].Contains("该表单受共享保护", StringComparison.Ordinal),
                "G1: genuine protection/uncertainty warning is still shown");
            f.Page.Messages.Clear();

            f.Page.TestApplyNavigationResult(
                new NotificationNavigationTarget(NotificationSource.OpenCode, "ses_test", "frm_nav_ok", "msg_1", NotificationNavigationKind.Form),
                new NotificationInteractionResult(NotificationInteractionStatus.Succeeded, "ok"));
            f.Page.TestApplyNavigationResult(
                new NotificationNavigationTarget(NotificationSource.OpenCode, "ses_test", "prm_nav_dup", "msg_1", NotificationNavigationKind.Permission),
                new NotificationInteractionResult(NotificationInteractionStatus.Duplicate, "dup"));
            f.Page.TestApplyNavigationResult(
                new NotificationNavigationTarget(NotificationSource.OpenCode, "ses_test", "frm_nav_exp", "msg_1", NotificationNavigationKind.Form),
                new NotificationInteractionResult(NotificationInteractionStatus.Expired, "exp"));
            Check(f.Page.Messages.Count == 0 && f.Page.SuppressedInteractions == 7,
                "G1: notification Succeeded/Duplicate/Expired suppresses interaction silently without '服务器已接受该决定' notice");

            f.Page.TestApplyNavigationResult(
                new NotificationNavigationTarget(NotificationSource.OpenCode, "ses_test", "frm_nav_unk", "msg_1", NotificationNavigationKind.Form),
                new NotificationInteractionResult(NotificationInteractionStatus.OutcomeUnknown, "unk"));
            Check(f.Page.Messages.Count == 1 && f.Page.Messages[0].Contains("提交结果未知", StringComparison.Ordinal),
                "G1: notification OutcomeUnknown warning is still shown");
        }
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly ScrollViewer MessageScroll = new();
        private readonly XamlRoot XamlRoot = new();
        private readonly Dictionary<string, string> _lastIdleBySession = new();
        private const int MaxScrollSettlePasses = 3;
        // Unrelated message-page presentation/metadata surface; the real binding/pagination partials compile.
        private readonly Dictionary<string, OpenCodeSessionInfo> _sessionItems = new(StringComparer.Ordinal);
        private bool _isRefreshing, _refreshQueued, _refreshQueuedInitial, _refreshQueuedForceFollow, _refreshQueuedExplicit;
        internal object MessagePageDiagnostics => (_isRefreshing, _refreshQueued, _refreshQueuedInitial,
            _refreshQueuedForceFollow, _refreshQueuedExplicit, _firstMessagesLoading, _messagesResponseEmpty, _messagesLoadError);
        internal int AckInvalidations;
        internal string? Created;
        internal int NewTabRequests;
        internal int NewTabNavigations;
        internal int HomeNavigations;
        internal bool NavigationWhileNativeAction;
        partial void RequestNewSessionTab()
        {
            NewTabRequests++;
            DispatcherQueue.TryEnqueue(() => { NewTabNavigations++; NavigationWhileNativeAction |= _nativeActionOwner is not null; });
        }
        internal int AgentOpens => _agentSelector.Opens;
        internal long Epoch => _messageHistoryEpoch;
        internal string? Boundary => _messageRevertBoundary;
        internal IEnumerable<string?> VisibleIds => _conversationView.Last.Select(m => m.Id);
        internal async Task StageNative(OpenCodeComposerClientActionKind kind, string name, string args = "")
        {
            Text("/" + name + (args.Length == 0 ? "" : " " + args));
            await Controller.LoadCatalogAsync(Snapshot.Token, _composerView!.Lifetime);
            if (!_composerView.Choose(Snapshot.Token, Snapshot.CatalogHandle!, new OpenCodeComposerCommandIdentity.Client(kind))) throw new Exception("Could not stage " + name);
            DrainView();
        }
        internal Task RunNative() { var s = Snapshot; var t = s.ClientIntent is { } i ? Controller.TryTakeClientAction(s.Token, i.Id) : null; return t is null ? Task.CompletedTask : RunClientActionAsync(t); }
        internal Task RunSidebarModel(bool variantOnly) { var t = Controller.TryTakeModelPickerAction(Snapshot.Token); return t is null ? Task.CompletedTask : RunClientActionAsync(t, variantOnly); }
        internal void LoseNormalPickerOwnership(string loss)
        {
            if (loss == "source") _source = null;
            else if (loss == "client") _client = null;
            else if (loss == "session") Switch("ses_other");
            else if (loss == "token") { DrainView(); Text("changed while loading recents"); }
            else throw new ArgumentException(loss);
        }
        internal bool InvokeNative(OpenCodeComposerClientActionKind forgedDisplayKind) => _composerView!.Invoke(forgedDisplayKind);
        internal void EndCatalogLifetime() => _composerView!.EndLifetime();
        internal Task StopDirect() => InterruptSelectedSessionAsync();
        internal void DrainDispatcher() { while (DispatcherQueue.Queue.TryDequeue(out var callback)) callback(); }
        internal Func<bool> CaptureReadCheck() { var r = CaptureMessageHistoryRead(_client!, _selectedSessionId!); return () => IsCurrentMessageHistoryRead(r); }
        internal void AcceptHistory(string? boundary) => ResetAcceptedHistoryProjection(boundary);
        internal void ApplyHistory(List<OpenCodeMessage> rows) => ApplyRefreshedMessages(rows, null, true);
        private void InvalidateViewAcknowledgement() => AckInvalidations++;
        private void CancelSessionOperations() => _sessionCts?.Cancel();
        private void UpdateEmptyState() { }
        // Viewport ACK is outside this composer harness; real cache Apply now requests that surface.
        private void NotifyViewAcknowledgementRendered(IReadOnlyList<OpenCodeMessage> rows, bool visible) { }
        private const bool _hostWindowVisible = false;
        private void InsertSessionItemFirst(OpenCodeSessionInfo session) => Created = session.Id;
        private void RefreshSessionStateUi() { }
        internal Task? NewSessionRefresh;
        internal Action? NewSessionBound;
        private Task SwitchToSessionAsync(string id, Action? onBound = null, bool forceDraftTransition=false)
        {
            Switch(id); onBound?.Invoke(); NewSessionBound?.Invoke();
            return NewSessionRefresh ?? Task.CompletedTask;
        }
        private OpenCodeComposerToken? _newSessionHandoffToken;
        private string? _newSessionCreatedId;
        private OpenCodeComposerBinding? _newSessionCreatedBinding;
        private OpenCodeComposerToken? _newSessionCreatedToken;
        private long _newSessionCreateEpoch;
        private TabViewItem? _newSessionTab;
        private static readonly object NewSessionTabTag = new();
        private bool _suppressTabSelection;
        private readonly TabsStub SessionTabs = new();
        private void EnterNewSessionLayout(TabViewItem? selected=null) {if(selected is not null)_newSessionTab=selected;EnterBlank();} // Native placement boundary; real handlers live in opencode-tabs.
        internal void OpenBlankTab() => OpenNewSessionTab();
        private bool _homeActive, _blankPickerPending;
        private string? _activeTabSessionId;
        private long _blankPickerEpoch;
        private string? _blankMetadataRuntime,_blankMetadataDirectory;
        private Guid _blankMetadataDraft,_blankMetadataSlotId;
        private string? _homeDefaultDirectory="/synthetic";
        private sealed class HomeStub { internal OpenCodeHomeProjectKey? SelectedProject; }
        private readonly HomeStub _homeView=new();
        private void RefreshBlankPreferenceLabels(){}
        private void ShowSourceWaiting(){} // Native connection/recovery visibility is outside this controller/page-transport harness.
        private void ShowHomePage(){HomeNavigations++;NavigationWhileNativeAction |= _nativeActionOwner is not null;} // Native Home selection tested by opencode-tabs.
        private void SetBlankComposerPlacement(bool blank){} // Native layout only.
        private void UpdateSessionTabsEmptyState(){} // Actual native actor/selection routing is source-linked in opencode-tabs.
        private Task LoadBlankMetadataAsync(OpenCodeBlankDraftPreferences basis,OpenCodeComposerToken token)=>Task.CompletedTask; // I/O boundary; real defaults loader tested by opencode-draft-slots.
        // Native placement/indicator boundary; actual retired actor handlers are linked in opencode-draft-slots.
        private sealed class SessionTabEntry(TabViewItem item){internal readonly TabViewItem Item=item;internal OpenCodeDraftSlotKey? DraftSlotKey;internal readonly ProgressRing Working=new();internal readonly Button Attention=new();}
        private readonly Dictionary<string,SessionTabEntry> _sessionTabs=new();
        private readonly Dictionary<string,string> _sessionTitles=new();
        private void SelectSessionTabSilently(string? id)=>SessionTabs.SelectedItem=_homeActive?null:id is not null&&_sessionTabs.TryGetValue(id,out var s)?s.Item:_newSessionTab;
        private void CreateSessionTab(string id,int insertAt=-1)
        {var t=new TabViewItem{Tag=id,Header=id,IsClosable=true};_sessionTabs[id]=new(t);SessionTabs.TabItems.Insert(insertAt<0?SessionTabs.TabItems.Count:Math.Clamp(insertAt,0,SessionTabs.TabItems.Count),t);}
        private void RemoveSessionTab(SessionTabEntry entry)
        {
            if(entry.DraftSlotKey is{} key&&_draftSlots.TryGetValue(key,out var data))ReleaseDraftSlot(data);
            var pair=_sessionTabs.FirstOrDefault(p=>ReferenceEquals(p.Value,entry));if(pair.Key is not null)_sessionTabs.Remove(pair.Key);
            _retiredSessionTabs.Remove(entry.Item);SessionTabs.TabItems.Remove(entry.Item);
        }
        private string _homeRuntimeSource = "synthetic-runtime";
        private readonly Dictionary<string, OpenCodeHomeProject> _homeProjects = new(StringComparer.Ordinal)
            { ["/synthetic"] = new(new("synthetic-runtime", "/synthetic"), "synthetic") };
        private OpenCodeBlankDraftPreferences? _blankPreferences;
        private IReadOnlyList<OpenCodeAgentInfo> _blankAgents = Array.Empty<OpenCodeAgentInfo>();
        private IReadOnlyList<OpenCodeModelInfo> _blankModels = Array.Empty<OpenCodeModelInfo>();
        private bool _blankModelChosen;
        private string? _blankPreferenceError;
        internal OpenCodeBlankDraftPreferences BlankPreferences => _blankPreferences!;
        internal void UseBlankSelection(OpenCodeBlankDraftPreferences value) => _blankPreferences = value.BlankSlotId==Guid.Empty?value with{BlankSlotId=_activeBlankSlotId??Guid.Empty}:value;
        internal void ResolveBlankDefaults(IReadOnlyList<OpenCodeAgentInfo> agents, IReadOnlyList<OpenCodeModelInfo> models,
            string? configuredAgent, OpenCodeModelRef? configuredModel, OpenCodeModelInfo? directoryModel,
            bool chosenModel = false, OpenCodeBlankDraftPreferences? basis = null)
        {
            _blankAgents = agents; _blankModels = models; _blankModelChosen = chosenModel;
            ApplyBlankDefaults(basis ?? _blankPreferences!, configuredAgent, configuredModel, directoryModel);
        }
        internal static string StrengthCaption(OpenCodeModelInfo model, OpenCodeModelRef selected) => BlankStrengthText(model, selected);
        private readonly Dictionary<(string SourceIdentity, Guid BlankSlotId), string?> _blankCreationResults = new();
        internal void SetupBlankPreferences(string directory = "/synthetic", string? agent = null, OpenCodeModelRef? model = null)
        {
            _homeProjects[directory] = new(new(_homeRuntimeSource, directory), directory);
            if(_activeDraftSlot is{} current&&current.Key.Directory!=directory)ChangeBlankSlotDirectory(directory);
            _blankPreferences = new(_homeRuntimeSource, Snapshot.Token.Draft, directory, agent, model){BlankSlotId=_activeBlankSlotId??Guid.Empty};
            if(_activeDraftSlot is{} slot){slot.DefaultsInitialized=true;slot.AgentPresentationDirectory=directory;} // Explicit default-metadata boundary; actual acceptance/labels are exercised in opencode-draft-slots.
        }
        internal void StaleBlankSource() => _homeRuntimeSource = Guid.NewGuid().ToString("N");
        internal void BlankSourceAba()
        {
            var source = _source;
            InvalidateDraftSourceEpoch();_source = null; _sourceGeneration++; BindComposer();
            _source = source; _sourceGeneration++; BindComposer();
            _homeRuntimeSource = Guid.NewGuid().ToString("N");
        }
        private string? _convertNewSessionTabTo { get; set; }
        private void SelectSessionItem(string id) { }
        partial void OnSessionContextSelected(string id){_homeActive=false;_newSessionDraftActive=false;}
        internal Task CreateBlankSession() => CreateSessionAndSubmitAsync(Snapshot.Token);
        private readonly OpenCodePermissionOverlay _permissionOverlay = new();
        private readonly InteractionsViewStub _interactionsView = new();
        private readonly Panel PermissionOverlayHost = new();
        private bool _sessionProtectionHint;
        internal int RemovedInteractions => _interactionsView.Removed;
        internal int SuppressedInteractions => _interactionsView.Suppressed;
        internal void TestSettleInteraction(bool isForm, string id, string title, string message) => SettleInteraction(isForm, id, title, message);
        internal void TestHandleProtectedAtSubmit(bool isForm, string id, AIFrontend.Services.Notifications.NotificationInteractionStatus protection, bool sessionProtected)
        {
            if (isForm) HandleProtectedFormAtSubmit(id, protection, sessionProtected);
            else HandleProtectedPermissionAtSubmit(id, protection, sessionProtected);
        }
        internal void TestApplyNavigationResult(AIFrontend.Services.Notifications.NotificationNavigationTarget target, AIFrontend.Services.Notifications.NotificationInteractionResult result) => ApplyNavigationResult(result, target);
        internal int ComposerFocuses => Composer.Focuses;
        internal void SetPermissionDismissal(bool focused)
        {
            _composerCoveredByPermission = true; _permissionOverlay.Focused = focused;
            Composer.FocusState = FocusState.Unfocused; UpdateComposerButtons();
        }
        internal int DismissPermission() => SyncPermissionOverlay();
        private bool IsScrollContextCurrent(int page, int session, string? id) => _pageActive && page == _pageGeneration && session == _sessionGeneration && id == _selectedSessionId;
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public sealed class ListView { public object? SelectedItem; public int Focuses; public bool Focus(FocusState state) { Focuses++; return true; } }
    public sealed class TextBlock { public string Text = ""; }
    public sealed class ScrollViewer { public double VerticalOffset, ScrollableHeight; public void UpdateLayout() { } public void ChangeView(double? x, double? y, double? zoom, bool disableAnimation) { VerticalOffset = y ?? VerticalOffset; } }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed class OpenCodePermissionCard { }
    internal sealed class InteractionsViewStub
    {
        public readonly List<object> Children = new();
        internal int Removed;
        internal int Suppressed;
        internal void RemoveSettled(bool isForm, string id) => Removed++;
        internal void Suppress(bool isForm, string id) => Suppressed++;
        internal void MarkPermissionBlocked(string id, string detail) { }
        internal void MarkFormBlocked(string id, string detail) { }
        internal void FocusPermission(string id) { }
        internal void FocusForm(string id) { }
    }
    internal sealed class OpenCodePermissionOverlay
    {
        internal bool Focused;
        internal bool ContainsFocus() => Focused;
        internal int Sync(IEnumerable<OpenCodePermissionCard> cards)
        {
            int count = cards.Count();
            if (count == 0) Focused = false; // Detaching final presenter destroys its focus ancestry.
            return count;
        }
        internal void FocusCurrent() => Focused = true;
    }
    internal static class OpenCodeModelPickerDialog
    {
        internal static Func<OpenCodeModelPickerPresentation, Task<OpenCodeModelPickerResult>> Next = null!;
        internal static string? LastSourceKey;
        internal static Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot root, OpenCodeModelPickerPresentation p, Func<Task<OpenCodeModelPickerPresentation?>> refresh, string? sourceKey = null)
        { LastSourceKey = sourceKey; return Next(p); }
    }
    internal static class OpenCodeVariantPickerDialog
    {
        internal static Func<OpenCodeModelPickerPresentation, Task<OpenCodeModelPickerResult>> Next = null!;
        internal static Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot root, OpenCodeModelPickerPresentation p) => Next(p);
    }
    internal static class OpenCodeRollbackConfirmationDialog
    {
        internal static Func<OpenCodeRollbackPreviewPresentation, Task<OpenCodeRollbackConfirmationResult>> Next = null!;
        internal static Task<OpenCodeRollbackConfirmationResult> ShowAsync(XamlRoot root, OpenCodeRollbackPreviewPresentation p) => Next(p);
    }
    internal sealed class OpenCodeDraftEditorDialog
    {
        internal static Func<OpenCodeDraftEditorDialog, Func<string, OpenCodeDraftEditorApplyOutcome>, Task<OpenCodeDraftEditorResult>> Next = null!;
        internal bool Stale;
        internal void MarkStale() => Stale = true;
        internal int SelectionStart { get; private set; }
        internal int SelectionLength { get; private set; }
        internal Task<OpenCodeDraftEditorResult> ShowAsync(XamlRoot root, string initial, Func<string, OpenCodeDraftEditorApplyOutcome> apply, int selectionStart = 0, int selectionLength = 0)
        { SelectionStart = selectionStart; SelectionLength = selectionLength; return Next(this, apply); }
    }
}
