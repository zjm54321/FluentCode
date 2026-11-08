// Test doubles are deliberately limited to WinUI/native APIs and existing timeline presentation.
// Page.Composer.cs, Page.AgentIntegration.cs, extracted page members, controller/loader/gate/client
// picker adapter and editor ledger/handoff/lifetime helpers are production sources. The view and
// scheduler remain explicit doubles, not native event/dispatcher evidence. No live HTTP/private files.
#pragma warning disable CS0067, CS0169, CS0414, CS0649
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

// Action-result visuals are a native presentation boundary, not an alternate command/mutation path.
namespace FluentCode.Motion
{
    internal static class ActionFeedback
    {
        internal static object? LastSuccessControl;
        internal static string? LastSuccessAnnouncement;
        internal static void Success(object control, string announcement)
        {
            LastSuccessControl = control;
            LastSuccessAnnouncement = announcement;
        }
        internal static void Announce(object anchor, string announcement) { }
    }
}

internal static class Program
{
    private static int checks;
    internal static void Check(bool value, string note)
    {
        checks++;
        if (!value) throw new InvalidOperationException(note);
    }
    internal static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    internal static HttpResponseMessage Receipt(string session = "ses_test") => Json("{\"data\":{\"id\":\"msg_real\",\"sessionID\":\"" + session + "\",\"type\":\"user\",\"payload\":{\"text\":\"accepted\"}}}");
    internal static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed class Handler : HttpMessageHandler
    {
        internal int Posts, Gets, Interrupts;
        internal readonly List<string> Calls = new();
        internal bool NoStagePreflight;
        internal string? Path, Body;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
            (r, _) => Task.FromResult(r.Method == HttpMethod.Get
                ? r.RequestUri!.AbsolutePath == "/api/command"
                    ? Json("{\"data\":[{\"name\":\"review\",\"description\":\"Review\"}]}")
                    : throw new InvalidOperationException("Unexpected offline GET " + r.RequestUri.AbsolutePath)
                : r.RequestUri!.AbsolutePath.EndsWith("/command", StringComparison.Ordinal)
                    ? new(HttpStatusCode.NoContent) : r.RequestUri!.AbsolutePath.EndsWith("/interrupt", StringComparison.Ordinal)
                         ? Json("{\"interrupted\":true}") : Receipt(r.RequestUri!.AbsolutePath.Split('/')[3]));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) Gets++; else Posts++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/interrupt", StringComparison.Ordinal)) Interrupts++;
            Path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method.Method + " " + Path);
            Body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            // Legacy fixture callbacks below intentionally control mutation responses only.
            // Give the current continuation preflight its own authoritative no-stage GET,
            // without intercepting NativeTests' independently controlled stage/state reads.
            if (NoStagePreflight && request.Method == HttpMethod.Get && Path.StartsWith("/api/session/",StringComparison.Ordinal)
                && !Path["/api/session/".Length..].Contains('/'))
                return Task.FromResult(Json("{\"data\":{\"id\":\"" + Path["/api/session/".Length..]
                     + "\",\"projectID\":\"p\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/synthetic\"}}}"));
            // A7 slotted command send must ALSO perform a fresh real command catalog GET.
            // These callbacks control mutations only; native/explicit catalog fixtures leave this flag false.
            if (NoStagePreflight && request.Method == HttpMethod.Get && Path == "/api/command")
                return Task.FromResult(Json("{\"data\":[{\"name\":\"review\",\"description\":\"Review\"}]}"));
            return Respond(request, ct);
        }
    }
    internal sealed class Fixture : IDisposable
    {
        internal Handler Http = new();
        internal readonly OpenCodeClient Client;
        internal readonly OpenCodePageSource Source;
        internal readonly OpenCodePage Page;
        internal Fixture()
        {
            Http.NoStagePreflight = true;
            OpenCodeClient Create() => new(new HttpClient(Http, false), new() { BaseUrl = "https://offline.invalid", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(20) }, true);
            Client = Create();
            Source = new("endpoint_" + Guid.NewGuid().ToString("N"), 7, Create);
            Page = new(Source, Client);
            Page.CountStops = () => Http.Interrupts;
        }
        internal Task<OpenCodeInteractionGate.Lease> Lease(string session = "ses_test") => OpenCodeInteractionGate.Shared.EnterAsync(Source.Identity, session);
        public void Dispose() { Page.UnloadFixture(); Client.Dispose(); }
    }
    // Controllable UI continuation queue: tests can navigate after settlement but before publication.
    internal sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        private readonly SemaphoreSlim posted = new(0);
        internal readonly TaskCompletionSource<bool> Ready = Signal<bool>();
        public override void Post(SendOrPostCallback d, object? state) { queue.Enqueue((d, state)); Ready.TrySetResult(true); posted.Release(); }
        internal void Drain()
        {
            var old = Current; SetSynchronizationContext(this);
            try { while (queue.TryDequeue(out var next)) next.Callback(next.State); }
            finally { SetSynchronizationContext(old); }
        }
        internal async Task DrainUntilAsync(Task completion)
        {
            while (!completion.IsCompleted)
            {
                if (!await posted.WaitAsync(TimeSpan.FromSeconds(5))) throw new TimeoutException("Queued UI continuation did not arrive.");
                Drain();
            }
            await completion;
        }
    }
    private static async Task Main(string[] args)
    {
        // Isolate optional successful-use bookkeeping BEFORE any send/model UI group, not only NativeTests.
        // No user history/config is read or written by this offline process.
        var recentFlags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(OpenCodeRecentModels).GetField("_path",recentFlags)!.SetValue(OpenCodeRecentModels.Shared,
            Path.Combine(Path.GetTempPath(),"a7-offline-recents-"+Guid.NewGuid().ToString("N")+".json"));
        typeof(OpenCodeRecentModels).GetField("_loaded",recentFlags)!.SetValue(OpenCodeRecentModels.Shared,true);
        await CatalogDispatchTests.Run();
        if (args.Length > 2 && args[2] == "True") return;
        await ComposerActivityTests.Run();
        ComposerStatusHost.Run();
        await SideQuestionTests.Run();
        await DeferredSelectionTests.Run();
        await PluginPendingGuardTests.Run();
        if (args.Length > 1 && args[1] == "True") { await TerminalButtons(); await NativeTests.Run(); await ContinuationTests.Run(); return; }
        await RoutingAndProtection();
        await DeferredAgentSubmission();
        var first = checks;
        await ReceiptsAndOwners();
        var second = checks - first;
        await LifecycleAndHooks(args[0]);
        var third = checks - first - second;
        await AcknowledgmentAndHandoffs();
        var fourth = checks - first - second - third;
        await TerminalButtons();
        await BlankSessionDraft();
        await BlankSessionDelayedRefreshAba();
        await BlankConfiguredPreferences();
        BlankResolvedSelection();
        PermissionDismissalFocus();
        await StuckImagePasteHookup();
        Console.WriteLine($"PASS: 5 production-linked page-partial groups, {checks} assertions ({first}/{second}/{third}/{fourth}/{checks-first-second-third-fourth}); real core/editor helpers, fake HTTP, WinUI/view/scheduler doubles; no live UI/service.");
    }
    private static async Task StuckImagePasteHookup()
    {
        var before = checks;
        using var f = new Fixture();
        f.Page.Text("look ");
        var ticket = f.Page.Controller.BeginPick(f.Page.Snapshot.Token)!;
        var paste = await f.Page.Controller.StageImagePasteAsync(ticket,
            new[] { new OpenCodeComposerFileSource("clipboard.png", "image/png", _ => Task.FromResult<Stream>(new MemoryStream(new byte[4]))) }, 5, 0);
        Check(paste is not null && f.Page.ApplyStagedImage(paste), "real page TextEdited handler acknowledges text/chip/file together");
        f.Page.AppendDocumentText("x");
        Check(f.Page.InputState == OpenCodeComposerInputState.Clean && f.Page.Snapshot.Images.Count == 1,
            "real page accepts immediate typing after image paste before queued render");
        f.Page.Controller.SetText(f.Page.Snapshot.Token, "controller edit " + f.Page.Composer.Text);
        f.Page.AppendDocumentText("z");
        Check(f.Page.InputState == OpenCodeComposerInputState.HeldStale, "real page still holds genuinely stale input");
        f.Page.ClearHeldFromView();
        Check(f.Page.InputState == OpenCodeComposerInputState.Clean && f.Page.Composer.Text.Length == 0
            && f.Page.Snapshot.Attachments.Count == 0, "real page clear-and-recover action discards held text/chips/files and releases editor sync");
        f.Page.Text("works");
        Check(f.Page.InputState == OpenCodeComposerInputState.Clean, "real page accepts typing after recovery");
        Console.WriteLine($"PASS stuck image page hookup: {checks - before} assertions; production TextEdited and recovery action handlers.");
    }
    private static async Task DeferredAgentSubmission()
    {
        var before = checks;
        foreach (var outcome in new[] { "success", "rejected", "edit", "binding-aba" })
        {
            using var f = new Fixture();
            f.Page.Text("retained agent draft");
            var apply = Signal<bool>();
            var entered = Signal<bool>();
            f.Page.PendingAgent(async () =>
            {
                Check(!f.Page.Pending && f.Page.Begins == 0, "AS3 switch applies before local agent-submission lease: " + outcome);
                entered.TrySetResult(true);
                return await apply.Task;
            });
            var send = f.Page.Send();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(f.Http.Posts == 0 && f.Page.Begins == 0, "AS3 prompt waits for agent apply: " + outcome);
            await f.Page.Send();
            Check(f.Http.Posts == 0 && f.Page.AgentApplyCalls == 1, "AS3 repeated submit cannot apply or send twice: " + outcome);
            if (outcome == "edit") f.Page.Text("newer draft");
            if (outcome == "binding-aba") { f.Page.Switch("other"); f.Page.Switch("ses_test"); }
            apply.SetResult(outcome != "rejected");
            await send;
            if (outcome == "success")
                Check(f.Http.Posts == 1 && f.Page.Begins == 1 && !f.Page.Snapshot.HasDraft, "AS3 unchanged successful switch sends exactly once");
            else
                Check(f.Http.Posts == 0 && f.Page.Begins == 0 && f.Page.Snapshot.Text == (outcome == "edit" ? "newer draft" : "retained agent draft"),
                    "AS3 rejected/stale switch preserves draft and never starts submission: " + outcome);
        }
        Console.WriteLine($"PASS AS3 deferred agent apply/send fences: {checks - before} assertions; actual page route, selector/HTTP doubles.");
    }
    private static async Task TerminalButtons()
    {
        var before = checks;
        Check(OpenCodeComposerSendPresentation.Name(OpenCodeComposerSendMode.Working) == "停止"
            && OpenCodeComposerSendPresentation.Glyph(OpenCodeComposerSendMode.Working) == "\uE71A"
            && !OpenCodeComposerSendPresentation.ShowsProgress(OpenCodeComposerSendMode.Working)
            && OpenCodeComposerSendPresentation.UsesAccent(OpenCodeComposerSendMode.Working)
            && OpenCodeComposerSendPresentation.ShowsProgress(OpenCodeComposerSendMode.Submitting)
            && !OpenCodeComposerSendPresentation.UsesAccent(OpenCodeComposerSendMode.Submitting),
            "G6: Working is a native accent stop button without ProgressRing; only Submitting shows progress");
        foreach (var outcome in new[] { OpenCodeSessionOutcomes.Succeeded, OpenCodeSessionOutcomes.Interrupted })
        {
            using var f = new Fixture();
            f.Page.SetWorking();
            Check(f.Page.StopMode && f.Page.SendEnabled, "accepted running turn exposes Stop: " + outcome);
            Check(f.Page.SendMode == OpenCodeComposerSendMode.Working, "working without draft shows the Working (click = stop) state: " + outcome);
            f.Page.Text("queued follow-up");
            Check(f.Page.SendMode == OpenCodeComposerSendMode.Send && f.Page.SendEnabled && !f.Page.StopMode,
                "working with draft keeps primary direct Send through the existing send path: " + outcome);
            f.Page.Text(string.Empty);
            Check(f.Page.SendMode == OpenCodeComposerSendMode.Working && f.Page.StopMode, "clearing the draft returns to Working: " + outcome);
            var terminal = new OpenCodeMessage { Id = "msg_terminal", Type = OpenCodeMessageTypes.Idle, Outcome = outcome };
            f.Page.ObserveExecution(new() { terminal });
            Check(!f.Page.StopMode && !f.Page.SendEnabled, "polling terminal immediately restores disabled Send: " + outcome);
            Check(!f.Page.ShouldStop(), "idle handler and displayed button agree: " + outcome);
            f.Page.ObserveExecution(new() { terminal });
            Check(!f.Page.StopMode && !f.Page.SendEnabled, "duplicate idle keeps Send: " + outcome);
            f.Page.Text("next prompt");
            Check(!f.Page.StopMode && f.Page.SendEnabled, "next draft enables Send: " + outcome);
            Check(f.Page.SendMode == OpenCodeComposerSendMode.Send, "idle with draft shows Send: " + outcome);
            f.Page.CoveredByPermission = true;
            Check(!f.Page.SendEnabled && !f.Page.ComposerEnabled && f.Page.Snapshot.Text == "next prompt",
                "permission overlay disables input and Send but keeps the draft: " + outcome);
            f.Page.CoveredByPermission = false;
            Check(f.Page.SendEnabled && f.Page.ComposerEnabled, "uncovering restores input and Send: " + outcome);
        }
        using (var f = new Fixture())
        {
            f.Page.SetWorking();
            f.Page.RebaselineExecution("msg_real");
            var historical = new OpenCodeMessage { Id = "msg_old_idle", Type = OpenCodeMessageTypes.Idle, Outcome = OpenCodeSessionOutcomes.Succeeded };
            f.Page.ObserveExecution(new() { historical });
            Check(f.Page.StopMode && f.Page.SendEnabled, "historical idle without accepted prompt witness cannot clear Working");
            f.Page.ObserveExecution(new() { historical, new() { Id = "msg_real", Type = OpenCodeMessageTypes.User } });
            Check(f.Page.StopMode, "accepted prompt after old idle still requires a later terminal");
            f.Page.ObserveExecution(new() { new() { Id = "msg_fresh_idle", Type = OpenCodeMessageTypes.Idle, Outcome = OpenCodeSessionOutcomes.Interrupted } });
            Check(!f.Page.StopMode && !f.Page.SendEnabled, "fresh ordered terminal also refreshes button after rebaseline");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("uncertain draft");
            using (var pending = await f.Lease())
            {
                var operation = Guid.NewGuid();
                pending.BeginComposerSubmission(operation, ComposerSubmissionKind.Command);
                pending.CompleteComposerSubmission(operation, NotificationInteractionStatus.OutcomeUnknown);
            }
            f.Page.ObserveExecution(new() { new() { Id = "msg_unrelated_idle", Type = OpenCodeMessageTypes.Idle, Outcome = OpenCodeSessionOutcomes.Succeeded } });
            using (var lease = await f.Lease())
                Check(lease.GetComposerSubmissionProtection() is { Kind: ComposerSubmissionKind.Command }, "idle observation never settles an uncertain submission receipt");
            await f.Page.Send();
            Check(f.Http.Posts == 0 && !f.Page.StopMode && f.Page.Snapshot.HasDraft, "uncertain draft cannot retry after idle and is not cleared");
        }
        Console.WriteLine($"PASS terminal button regression: {checks - before} assertions; production idle observer and button policy, fake WinUI/gate fixtures.");
    }
    // L2 (redesign §3): the blank "新会话" tab edits the same draft UNBOUND. Enter only requests creation (never a
    // transport); an armed one-shot handoff moves command/file drafts to exactly the created session; a missed
    // handoff leaves the draft inactive (never lost, never sent elsewhere).
    private static async Task BlankSessionDraft()
    {
        var before = checks;
        using (var f = new Fixture())
        {
            f.Page.EnterBlank();
            Check(f.Page.BindingUnbound, "blank tab binds an unbound composer binding");
            f.Page.Text("first message");
            Check(f.Page.Snapshot.Text == "first message" && f.Page.Snapshot.IsActive && !f.Page.Snapshot.IsProtected && f.Page.Snapshot.CanSubmit,
                "unbound plain draft is editable and submittable (creation first)");
            Check(f.Page.SendEnabled && !f.Page.StopMode, "blank tab shows Send, never Stop");
            int unbound = 0, normal = 0;
            f.Page.Controller.UnboundSubmitRequested += (_, _) => unbound++;
            f.Page.Controller.SubmitRequested += (_, _) => normal++;
            f.Page.PublicSubmit();
            Check(unbound == 1 && normal == 0 && f.Http.Posts == 0, "Enter on blank tab requests creation only; zero POST");
            await f.Page.SendLegacyDirect(); await f.Page.Send();
            Check(f.Http.Posts == 0 && f.Page.Snapshot.Text == "first message", "legacy/enhanced senders refuse an unbound draft and keep it");
            f.Page.Text("");
            var actions = f.Page.Snapshot.ClientActions.ToDictionary(a => a.Kind, a => a.CanStage);
            Check(!actions[OpenCodeComposerClientActionKind.ChooseAgent] && !actions[OpenCodeComposerClientActionKind.ChooseModel]
                && !actions[OpenCodeComposerClientActionKind.Undo] && !actions[OpenCodeComposerClientActionKind.Redo]
                && actions[OpenCodeComposerClientActionKind.NewSession] && actions[OpenCodeComposerClientActionKind.ChooseSession]
                && actions[OpenCodeComposerClientActionKind.EditDraft], "session-dependent native actions unavailable before a session exists");
            var blank = f.Page.Snapshot;
            Check(blank.IsUnboundDraft, "blank-tab snapshot marks the unbound draft");
            foreach (var kind in new[] { OpenCodeComposerClientActionKind.ChooseAgent, OpenCodeComposerClientActionKind.ChooseModel,
                OpenCodeComposerClientActionKind.Undo, OpenCodeComposerClientActionKind.Redo })
                Check(OpenCodeComposerCommandMenuSession.UnavailableReason(kind, blank) == "开始会话后可用", "blank-tab caption for " + kind);
            Check(!OpenCodeComposerCommandMenuSession.RequiresSession(OpenCodeComposerClientActionKind.NewSession)
                && !OpenCodeComposerCommandMenuSession.RequiresSession(OpenCodeComposerClientActionKind.ChooseSession)
                && !OpenCodeComposerCommandMenuSession.RequiresSession(OpenCodeComposerClientActionKind.EditDraft), "session-free actions keep their own reasons");
            f.Page.Text("carried text");
            f.Page.LeaveBlankTo("ses_test");
            Check(f.Page.Snapshot.IsActive && f.Page.Snapshot.Text == "", "opening existing session does not carry blank text without an accepted-create association");
            f.Page.Text("new explicit existing-session input");
            Check(!f.Page.Snapshot.IsUnboundDraft
                && OpenCodeComposerCommandMenuSession.UnavailableReason(OpenCodeComposerClientActionKind.ChooseAgent, f.Page.Snapshot) != "开始会话后可用",
                "bound session never shows the blank-tab caption");
            var queuedBasis = f.Page.Controller.CaptureDraft(f.Page.Snapshot.Token, "queue")!;
            var steerBasis = f.Page.Controller.CaptureDraft(f.Page.Snapshot.Token, "steer")!;
            Check(queuedBasis.Draft != steerBasis.Draft && queuedBasis.Delivery == "queue", "immutable delivery participates in submission fingerprint");
            await f.Page.Send("queue");
            Check(f.Http.Body!.Contains("\"delivery\":\"queue\"", StringComparison.Ordinal), "plain guarded prompt sends selected queue delivery");
            Check(f.Http.Posts == 1 && f.Http.Path!.EndsWith("/prompt", StringComparison.Ordinal) && f.Http.Path.Contains("ses_test", StringComparison.Ordinal),
                "normal path sends exactly once to the created session");
        }
        foreach (var command in new[] { true, false })
        {
            using var f = new Fixture();
            f.Page.EnterBlank();
            if (command) await f.Page.Command("args"); else { f.Page.Text("with file"); await f.Page.File(); }
            Check(f.Page.Snapshot.IsBoundDraft && f.Page.Snapshot.IsActive && f.Http.Posts == 0, "unbound command/file draft staged without POST");
            var token = f.Page.Snapshot.Token;
            Check(!f.Page.Controller.ArmUnboundHandoff(token, ""), "handoff requires a concrete created session");
            var respond=f.Http.Respond;
            f.Http.Respond=(r,ct)=>r.Method==HttpMethod.Post&&r.RequestUri!.AbsolutePath=="/api/session"
                ?Task.FromResult(Json("{\"data\":{\"id\":\"ses_created\"}}")):respond(r,ct);
            await f.Page.CreateBlankSession();
            await f.Page.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Http.Posts == 2 && f.Http.Path!.EndsWith(command ? "/command" : "/prompt", StringComparison.Ordinal), "accepted-create association hands over and sends exactly once via fresh enhanced path");
            if (!command) Check(f.Http.Body!.Contains("\"delivery\":\"steer\"", StringComparison.Ordinal) && f.Http.Body.Contains("\"files\"", StringComparison.Ordinal), "enhanced guarded prompt preserves files and selected steer delivery");
            Check(!f.Page.Snapshot.HasDraft, "accepted handed-over draft cleared once");
        }
        using (var f = new Fixture())
        {
            f.Page.EnterBlank(); f.Page.Text("unarmed"); await f.Page.File();
            var token = f.Page.Snapshot.Token;
            Check(f.Page.Controller.ArmUnboundHandoff(token, "ses_created"), "armed for a different session");
            f.Page.LeaveBlankTo("ses_test");
            Check(f.Page.Snapshot.IsActive && f.Page.Snapshot.Attachments.Count == 0, "old Arm alone never retargets blank payload into independent existing slot");
            await f.Page.Send(); f.Page.PublicSubmit();
            Check(f.Http.Posts == 0, "inactive unbound-origin draft cannot be sent to a different session");
            f.Page.EnterBlank();
            Check(f.Page.Snapshot.IsActive && f.Page.Snapshot.Attachments.Count == 1, "returning to the blank tab reactivates the retained draft");
            token = f.Page.Snapshot.Token;
            Check(f.Page.Controller.ArmUnboundHandoff(token, "ses_test"), "re-armed");
            f.Page.Text("edited after arm");
            f.Page.LeaveBlankTo("ses_test");
            Check(f.Page.Snapshot.IsActive&&f.Page.Snapshot.Attachments.Count==0&&f.Page.Snapshot.Text=="", "edit after old Arm cannot inject blank data into unrelated existing slot");
        }
        Console.WriteLine($"PASS blank new-session draft: {checks - before} assertions; production controller/page partials, fake HTTP.");
    }
    private static async Task BlankSessionDelayedRefreshAba()
    {
        int before = checks;
        foreach (var kind in new[] { "text", "command", "attachment" })
        {
            using var f = new Fixture();
            f.Page.EnterBlank();
            if (kind == "command") await f.Page.Command("retained args");
            else { f.Page.Text("retained " + kind); if (kind == "attachment") await f.Page.File(); }
            var draft = f.Page.Snapshot;
            var held = Signal<bool>(); var bound = Signal<bool>();
            f.Page.NewSessionRefresh = held.Task;
            f.Page.NewSessionBound = () => bound.TrySetResult(true);
            var respond = f.Http.Respond;
            f.Http.Respond = (request, ct) => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/session"
                ? Task.FromResult(Json("{\"data\":{\"id\":\"ses_created\"}}")) : respond(request, ct);
            var create = f.Page.CreateBlankSession();
            await bound.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var firstBinding = f.Page.Snapshot.Token;
            Check(f.Page.Created == "ses_created" && f.Http.Posts == 1, kind + ": create accepted once while initial refresh held");
            f.Page.Switch("ses_other"); f.Page.Switch("ses_created");
            Check(f.Page.Snapshot.Token != firstBinding, kind + ": A→B→A installs a different exact token");
            held.SetResult(true); await create;
            var retained = f.Page.Snapshot;
            Check(f.Http.Posts == 1, kind + ": delayed original refresh cannot auto-send after binding ABA");
            Check(retained.HasDraft && retained.Text == draft.Text && retained.SelectedCommand == draft.SelectedCommand
                && retained.Attachments.Count == draft.Attachments.Count, kind + ": draft retained intact for explicit submit");
            Check(f.Page.Messages.Any(m => m.Contains("会话已创建，未发送", StringComparison.Ordinal)), kind + ": explains retained draft");
            await f.Page.Send();
            Check(f.Http.Posts == 2 && !f.Page.Snapshot.HasDraft, kind + ": fresh explicit submit remains allowed exactly once");
        }
        foreach (var ready in new[] { false, true })
        {
            using var f = new Fixture();
            f.Page.EnterBlank(); f.Page.Text("unchanged create and send");
            f.Page.NewSessionBound = () => {
                f.Page.CompleteBindingSnapshot(); // Binding HTTP/projection executes in the pagination fixture.
                f.Page.EventCoverage = ready ? Task.CompletedTask : null;
            };
            var respond = f.Http.Respond;
            f.Http.Respond = (request, ct) => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/session"
                ? Task.FromResult(Json("{\"data\":{\"id\":\"ses_created\"}}")) : respond(request, ct);
            await f.Page.CreateBlankSession();
            Check(f.Page.Created == "ses_created" && f.Http.Posts == 2 && !f.Page.Snapshot.HasDraft,
                "unchanged exact new binding still creates and auto-sends once; guard does not reject every handoff");
            Check(f.Http.Calls.Count == 3 && f.Page.Refreshes == (ready ? 0 : 1),
                "PF2 first send retains create + fresh revert GET + prompt POST; only covered post-action refresh is skipped");
            Console.WriteLine($"PF2 first-send composition ready={ready}: measured composer calls=3, binding=5/1 RTT (pagination fixture), post-action refreshes={f.Page.Refreshes}; total calls 12 -> {(ready ? 8 : 12)}, sequential RTTs 11 -> {(ready ? 4 : 5)} for known directory/no correction.");
        }
        Console.WriteLine($"PASS blank-tab delayed-refresh ABA: {checks - before} assertions; actual create/handoff methods, fake HTTP/refresh.");
    }

    private static void BlankResolvedSelection()
    {
        using var f = new Fixture(); f.Page.OpenBlankTab();
        var agent = new OpenCodeAgentInfo { Id = "catalog-agent", Name = "weAVER", Color = "#ab12cd", Mode = OpenCodeAgentModes.Primary,
            Model = System.Text.Json.JsonDocument.Parse("{\"id\":\"agent-model\",\"providerID\":\"p\",\"variant\":\"reasoned\"}").RootElement.Clone() };
        var agentModel = new OpenCodeModelInfo { Id = "agent-model", ProviderId = "p", Name = "Agent model", Enabled = true, Variants = new() { new() { Id = "reasoned" } } };
        var configured = new OpenCodeModelInfo { Id = "config-model", ProviderId = "p", Name = "Config model", Enabled = true, Variants = new() { new() { Id = "listed-level" } } };
        var directory = new OpenCodeModelInfo { Id = "directory-model", ProviderId = "p", Name = "Directory model", Enabled = true, Variants = new() };
        var models = new[] { agentModel, configured, directory };
        var configRef = new OpenCodeModelRef { Id = "config-model", ProviderId = "p" };
        f.Page.ResolveBlankDefaults(new[] { agent }, models, "catalog-agent", configRef, directory);
        var resolved = f.Page.BlankPreferences;
        Check(resolved.Agent == "catalog-agent" && resolved.Model?.Id == "agent-model" && resolved.Model.Variant == "reasoned", "actual selected agent model/variant outranks both configured and directory defaults");
        Check(OpenCodePage.StrengthCaption(agentModel, resolved.Model!) == "reasoned", "actual offered selected variant is the only strength label");
        var manual = resolved with { Model = configRef, Version = resolved.Version + 1 };
        f.Page.UseBlankSelection(manual);
        f.Page.ResolveBlankDefaults(new[] { agent }, models, "catalog-agent", configRef, directory, chosenModel: true);
        var selected = f.Page.BlankPreferences;
        Check(selected.Model?.Id == "config-model" && selected.Model.Variant is null, "explicit pending model outranks agent configuration and retains a real omitted variant");
        Check(OpenCodePage.StrengthCaption(configured, selected.Model!) == "默认", "A2 default strength label is 默认 while the wire Variant remains null");
        f.Page.ResolveBlankDefaults(new[] { agent }, models, "catalog-agent", configRef, directory, basis: resolved);
        Check(f.Page.BlankPreferences == selected, "late captured-basis defaults cannot overwrite a later explicit selection/version");
        Check(f.Http.Posts == 0 && f.Http.Gets == 0, "applying actual parsed directory metadata performs no creation or transport");
    }

    private static async Task BlankConfiguredPreferences()
    {
        using (var f = new Fixture())
        {
            f.Page.OpenBlankTab();
            Check(f.Http.Posts == 0, "actual OpenNewSessionTab requests no creation; native placement/default-metadata reads are a boundary");
            f.Page.Text("exact preferences");
            f.Page.SetupBlankPreferences("/remote/worktree", "build", new() { Id = "catalog-id", ProviderId = "provider", Variant = "high" });
            var respond = f.Http.Respond;
            string? creation = null;
            f.Http.Respond = (request, ct) =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/session")
                {
                    creation = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                    return Task.FromResult(Json("{\"data\":{\"id\":\"ses_configured\"}}"));
                }
                return respond(request, ct);
            };
            await f.Page.CreateBlankSession();
            using var body = System.Text.Json.JsonDocument.Parse(creation!);
            Check(body.RootElement.GetProperty("location").GetProperty("directory").GetString() == "/remote/worktree", "actual first create captures exact remote directory");
            Check(body.RootElement.GetProperty("agent").GetString() == "build" && body.RootElement.GetProperty("model").GetProperty("variant").GetString() == "high"
                && body.RootElement.GetProperty("model").GetProperty("id").GetString() == "catalog-id"
                && body.RootElement.GetProperty("model").GetProperty("providerID").GetString() == "provider", "actual first create includes immutable agent/model/variant atomically");
            Check(f.Http.Posts == 2 && !f.Page.Snapshot.HasDraft, "configured create once followed by ordinary guarded single submit, no post-create switch");
        }
        using (var f = new Fixture())
        {
            f.Page.OpenBlankTab(); f.Page.Text("keep unknown creation");
            f.Http.Respond = (_, _) => throw new HttpRequestException("synthetic creation uncertainty");
            await f.Page.CreateBlankSession();
            Check(f.Http.Posts == 1 && f.Page.Snapshot.HasDraft, "uncertain actual creation preserves draft");
            f.Page.Text("edited but same draft");
            await f.Page.CreateBlankSession();
            Check(f.Http.Posts == 1, "editing unknown-result draft cannot issue another create");
        }
        using (var f = new Fixture())
        {
            f.Page.OpenBlankTab(); f.Page.Text("stale source choice"); f.Page.StaleBlankSource();
            await f.Page.CreateBlankSession();
            Check(f.Http.Posts == 0 && f.Page.Snapshot.HasDraft, "stale runtime source preferences never create or discard draft");
        }
        using (var f = new Fixture())
        {
            f.Page.OpenBlankTab(); f.Page.Text("source ABA while creation held");
            var started = Signal<bool>(); var held = Signal<HttpResponseMessage>();
            f.Http.Respond = (_, _) => { started.TrySetResult(true); return held.Task; };
            var create = f.Page.CreateBlankSession();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            f.Page.BlankSourceAba();
            held.SetResult(Json("{\"data\":{\"id\":\"ses_created_old\"}}"));
            await create;
            Check(f.Http.Posts == 1 && f.Page.Created is null && !f.Page.Snapshot.HasDraft&&f.Page.RecoveryPending&&f.Page.Composer.Text=="source ABA while creation held", "source A→B→A rejects old create handoff even when reference returns, preserving readonly recovery instead of active old payload");
            f.Page.SetupBlankPreferences(); await f.Page.CreateBlankSession();
            Check(f.Http.Posts == 1, "explicitly reselecting directory after source ABA cannot create same draft twice at same stable source");
        }
    }

    private static void PermissionDismissalFocus()
    {
        int before = checks;
        foreach (bool focused in new[] { true, false })
        {
            using var f = new Fixture(); f.Page.Text("retained Chinese draft 中文");
            f.Page.SetPermissionDismissal(focused);
            Check(f.Page.DismissPermission() == 0, "last permission removed by Sync");
            f.Page.DrainDispatcher();
            Check(f.Page.ComposerFocuses == (focused ? 1 : 0), "restore only previously overlay-owned focus, captured before removal");
            Check(f.Page.Snapshot.Text == "retained Chinese draft 中文", "dismissal never resets draft");
        }
        Console.WriteLine($"PASS permission dismissal focus: {checks - before} assertions; production Sync method, detaching presenter boundary.");
    }

    private static async Task RoutingAndProtection()
    {
        using (var f = new Fixture())
        {
            f.Page.Text("/review literal"); await f.Page.Send();
            Check(f.Http.Posts == 1 && f.Http.Path!.EndsWith("/prompt", StringComparison.Ordinal), "unselected slash uses real legacy sender");
            Check(f.Http.Body!.Contains("/review literal", StringComparison.Ordinal), "legacy literal preserved");
            Check(f.Page.Snapshot.Text == "" && f.Page.Receipts == 1 && f.Page.Tracked == 1 && f.Page.Working == 1, "legacy acceptance clears captured token and publishes real receipt");
            f.Page.PublicSubmit(); Check(f.Http.Posts == 1 && f.Page.Composer.Text == "", "rapid next submit cannot re-push accepted legacy editor text before render");
            using var lease = await f.Lease(); Check(lease.GetComposerSubmissionProtection() is null, "legacy acceptance writes no enhanced protection");
        }
        using (var f = new Fixture())
        {
            // Wave 3 Tab completion through the REAL page bridge: catalog-proven trigger edit only, never a send.
            f.Page.Text("/r"); await f.Page.LoadMenu(); f.Page.DrainView();
            var handle = f.Page.Snapshot.CatalogHandle!;
            Check(!f.Page.ViewComplete(f.Page.Snapshot.Token, handle, "x"), "completion must extend the displayed trigger");
            Check(!f.Page.ViewComplete(f.Page.Snapshot.Token, handle, "rzz"), "completion must be a catalog prefix");
            var before = f.Page.Snapshot.Token;
            Check(f.Page.ViewComplete(before, handle, "rev"), "page bridge accepts a catalog-proven completion");
            f.Page.DrainView();
            Check(f.Page.Snapshot.Text == "/rev" && f.Page.Composer.Text == "/rev" && f.Page.Snapshot.Token != before
                && f.Page.Snapshot.CatalogHandle == handle && f.Page.Snapshot.SelectedCommand is null,
                "completed text reaches the editor through guarded reconcile; same opening, nothing staged");
            Check(!f.Page.ViewComplete(before, handle, "revi"), "stale token cannot complete");
            Check(f.Http.Posts == 0, "completion never POSTs");
        }
        using (var f = new Fixture())
        {
            // Wave 3: clicking a catalog-declared argument-free server command goes through the REAL page submission bridge.
            await f.Page.Command("");
            Check(f.Http.Posts == 0 && f.Page.Snapshot.SelectedCommand == "review", "staged command, nothing sent yet");
            f.Page.ClickFreeCommand();
            for (var i = 0; i < 200 && f.Http.Posts == 0; i++) await Task.Delay(10);
            Check(f.Http.Posts == 1 && f.Http.Path!.EndsWith("/command", StringComparison.Ordinal), "argument-free click sends once via the ordinary command route");
            f.Page.ClickFreeCommand();
            await Task.Delay(50);
            Check(f.Http.Posts == 1, "a repeated click with nothing staged sends nothing more");
        }
        foreach (var command in new[] { true, false })
        {
            using var f = new Fixture();
            if (command) await f.Page.Command(""); else await f.Page.File();
            Check(f.Page.Snapshot.HasDraft && f.Page.SendEnabled && !f.Page.StopMode, "command/file-only draft uses Send predicate");
            await f.Page.Send();
            Check(f.Http.Posts == 1 && f.Http.Path!.EndsWith(command ? "/command" : "/prompt", StringComparison.Ordinal), "empty-text enhanced route performs one intended POST");
            Check(!f.Page.Snapshot.HasDraft, "enhanced success cleared once by controller");
        }
        using (var f = new Fixture())
        {
            await f.Page.Command("arguments"); f.Page.RemoveCommand();
            Check(f.Page.Snapshot.IsBoundDraft && f.Page.Snapshot.SelectedCommand is null, "removing command retains bound text");
            await f.Page.Send(); Check(f.Http.Posts == 1 && f.Http.Path!.EndsWith("/prompt", StringComparison.Ordinal), "bound-only text still uses legacy route");
        }
        using (var f = new Fixture())
        {
            await f.Page.Command("arguments");
            f.Http.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic disconnect"));
            await f.Page.Send();
            using (var lease = await f.Lease())
            {
                Check(lease.GetComposerSubmissionProtection() is { Kind: ComposerSubmissionKind.Command }, "unknown enhanced request retained typed command protection");
                Check(!lease.CanSwitchAgent(), "actual gate blocks agent switch for typed composer protection");
            }
            await f.Page.Send();
            Check(f.Http.Posts == 1 && f.Page.Snapshot.HasDraft && !f.Page.SendEnabled,
                "DD1 unknown result retains draft and blocks another explicit send; removing discard never resends it");
            f.Page.RemoveCommand();
            await f.Page.SendLegacyDirect();
            Check(f.Http.Posts == 1 && f.Page.Snapshot.Text == "arguments", "actual legacy sender rejects typed protection before zero additional POST");
            f.Page.Discard(); f.Page.Text("another plain prompt"); await f.Page.Send();
            Check(f.Http.Posts == 1 && f.Page.Snapshot.Text == "another plain prompt", "discard cannot bypass typed protection");
        }
        using (var f = new Fixture())
        {
            using (var lease = await f.Lease()) { lease.BeginMutation("form_1", NotificationNavigationKind.Form); lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown); }
            f.Page.Text("plain allowed"); await f.Page.Send();
            Check(f.Http.Posts == 1, "legacy does not blanket replace policy with CanSwitchAgent");
            using var result = await f.Lease();
            Check(result.GetComposerSubmissionProtection() is null && result.GetProtectionStatus("form_1", NotificationNavigationKind.Form) == NotificationInteractionStatus.OutcomeUnknown, "plain send preserves preexisting form protection");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("keep on disconnect");
            f.Http.Respond = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic disconnect"));
            await f.Page.Send(); using var lease = await f.Lease();
            Check(f.Http.Posts == 1 && lease.GetComposerSubmissionProtection() is null && f.Page.Snapshot.Text == "keep on disconnect", "legacy failure keeps draft and exact no-new-protection policy");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("event send"); var events = 0;
            f.Page.Controller.SubmitRequested += (_, _) => events++;
            f.Page.PublicSubmit();
            Check(events == 1 && f.Http.Posts == 1, "button bridge emits RequestSubmit/SubmitRequested once, page routes authoritatively");
        }
    }
    private static async Task ReceiptsAndOwners()
    {
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new Fixture(); if (enhanced) await f.Page.File(); f.Page.Text("steer now");
            f.Http.Respond = (_, _) => Task.FromResult(Json("{\"data\":{\"id\":\"msg_admitted\",\"sessionID\":\"ses_test\",\"type\":\"user\",\"delivery\":\"steer\",\"payload\":{\"text\":\"  admitted\\nraw  \",\"files\":[{\"name\":\"image.png\"}]}}}"));
            await f.Page.Send();
            Check(f.Page.SteerProjection is { Count: 1 } && f.Page.SteerProjection[0].Id == "msg_admitted"
                && f.Page.SteerProjection[0].Payload!.Value.GetProperty("text").GetString() == "  admitted\nraw  ",
                "actual plain/enhanced durable steer receipt immediately projects exact admitted payload before inbox refresh");
            f.Page.ReprojectSteer(null);
            Check(f.Page.SteerProjection is { Count: 1 } && f.Http.Posts == 1,
                "absent not-yet-observed inbox retains admitted chat without resending");
            f.Page.Switch("ses_other"); f.Page.Switch("ses_test"); f.Page.ReprojectSteer(null);
            Check(f.Page.SteerProjection is null, "steer receipt binding fences session ABA");
        }
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new Fixture(); if (enhanced) await f.Page.File(); f.Page.Text("sent");
            var response = Signal<HttpResponseMessage>(); var refresh = Signal<bool>();
            var started = Signal<bool>();
            f.Http.Respond = (_, _) => { started.TrySetResult(true); return response.Task; }; f.Page.RefreshPending = refresh.Task;
            var send = f.Page.Send();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Http.Posts == 1 && f.Page.Pending && f.Page.Sending, "owner lifetime begins once before POST");
            Check(!f.Page.SendEnabled && !f.Page.ShouldStop(), "local send disables both Send and Stop");
            f.Page.Text("later edit"); response.SetResult(Receipt());
            await f.Page.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.Page.Snapshot.Text == "later edit", "later draft revision survives accepted receipt");
            Check(f.Page.Receipts == 1 && f.Page.Tracked == 1 && f.Page.Working == 1 && f.Page.WaitingUpdates == 1, "receipt publication survives later text edit");
            Check(f.Page.Pending && f.Page.Sending, "local agent owner lives through receipt publication and refresh");
            using (var lease = await f.Lease()) Check(lease.GetComposerSubmissionProtection() is null, "lease released before receipt refresh");
            refresh.SetResult(true); await send;
            Check(!f.Page.Pending && !f.Page.Sending && f.Page.Begins == 1, "only owning finally clears local pending, one Begin");
        }
        using (var f = new Fixture())
        {
            await f.Page.Command(""); await f.Page.Send();
            Check(f.Page.Receipts == 0 && f.Page.Tracked == 0 && f.Page.Working == 0 && f.Page.Refreshes == 1 && f.Page.WaitingUpdates == 0, "204 acknowledgement creates no receipt/delivery/PromptSent/Working; exactly one read-only refresh");
            Check(f.Page.Messages.Count == 0 && !f.Page.Missing
                && ReferenceEquals(FluentCode.Motion.ActionFeedback.LastSuccessControl, f.Page.SendStopButton)
                && FluentCode.Motion.ActionFeedback.LastSuccessAnnouncement is { } acknowledgement
                && acknowledgement.Contains("命令回调", StringComparison.Ordinal) && acknowledgement.Contains("不代表模型执行完成", StringComparison.Ordinal),
                "204 acknowledgement is acting-button feedback with exact non-completion semantics, never a banner");
        }
        using (var f = new Fixture())
        {
            await f.Page.Command("keep");
            f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            await f.Page.Send();
            Check(!f.Page.Missing && f.Page.Snapshot.SelectedCommand == "review" && f.Page.Snapshot.Text == "keep", "command404 rejection is NOT generic session-missing");
            Check(f.Page.Receipts == 0 && f.Page.Tracked == 0, "rejected command no receipt publication");
            Check(f.Http.Posts == 1 && f.Page.SendEnabled, "DD1 rejected draft remains sendable without a discard action or automatic retry");
            f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await f.Page.Send();
            Check(f.Http.Posts == 2 && !f.Page.Snapshot.HasDraft,
                "DD1 existing Send retries preserved rejected command once after an explicit user action");
        }
        using (var f = new Fixture())
        {
            await f.Page.File(); f.Page.Text("original");
            var response = Signal<HttpResponseMessage>(); f.Http.Respond = (_, _) => response.Task;
            var context = new QueuedContext(); var old = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context); var send = f.Page.Send(); SynchronizationContext.SetSynchronizationContext(old);
            response.SetResult(Receipt()); await context.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            f.Page.Switch("ses_other"); context.Drain(); await send;
            Check(f.Page.Receipts == 0 && f.Page.Tracked == 0 && f.Page.Working == 0, "queued old UI after settlement cannot publish into new context");
            Check(!f.Page.Sending && !f.Page.Pending, "navigation invalidates old local owner");
        }
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new Fixture(); if (enhanced) await f.Page.File(); f.Page.Text("old");
            var oldResponse = Signal<HttpResponseMessage>(); var newResponse = Signal<HttpResponseMessage>();
            var oldStarted = Signal<bool>();
            f.Http.Respond = (r, _) =>
            {
                if (r.RequestUri!.AbsolutePath.Contains("ses_other", StringComparison.Ordinal)) return newResponse.Task;
                oldStarted.TrySetResult(true); return oldResponse.Task;
            };
            var oldSend = f.Page.Send();
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Page.Switch("ses_other"); if (enhanced) f.Page.Discard(); f.Page.Text("new");
            var newSend = f.Page.Send(); oldResponse.SetResult(Receipt()); await oldSend;
            Check(f.Page.Sending && f.Page.Pending && !f.Page.SendEnabled, "late old finally cannot clear newer submission owner/buttons");
            Check(f.Page.Receipts == 0 && f.Page.Snapshot.Text == "new", "stale response never clears current editor or adds receipts");
            newResponse.SetResult(Receipt("ses_other")); await newSend;
            Check(!f.Page.Sending && f.Page.Receipts == 1 && f.Page.Tracked == 1, "new owner publishes and ends itself");
        }
        using (var f = new Fixture())
        {
            await f.Page.File();
            using var lease = await f.Lease(); var pending = f.Page.Send(); f.Page.Switch("ses_other"); lease.Dispose(); await pending;
            Check(f.Http.Posts == 0 && f.Page.Snapshot.IsActive && f.Page.Snapshot.Attachments.Count == 0, "navigation while awaiting gate is NotStarted and activates independent empty target slot");
            f.Page.Switch("ses_test");Check(f.Page.Snapshot.Attachments.Count==1,"original inactive file payload remains only in original slot");
        }
    }
    private static async Task LifecycleAndHooks(string repo)
    {
        using (var f = new Fixture())
        {
            await f.Page.Command("retained"); var token = f.Page.Snapshot.Token; f.Page.Switch("ses_other");
            Check(f.Page.Snapshot.IsActive && f.Page.Snapshot.Text == "", "binding switch activates independent target slot instead of carrying inactive command text");
            f.Page.Switch("ses_test"); Check(f.Page.Snapshot.IsActive && f.Page.Snapshot.Token != token, "exact return activates a new scope");
            await f.Page.RouteToken(token); Check(f.Http.Posts == 0, "stale public submit token cannot send after A-B-A");
            foreach (var field in new[] { "page", "sourceGeneration", "sessionGeneration", "revision", "client", "session", "source", "missing", "active" })
                Check(f.Page.CheckBindingField(field), "pure owner rejects changed field: " + field);
            f.Page.UnloadFixture(); Check(!f.Page.Snapshot.IsActive && !f.Page.Snapshot.HasDraft&&f.Page.Composer.Text=="retained"&&f.Page.RecoveryPending, "unload null-triple releases core authority and keeps visible readonly recovery");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("snapshot old"); f.Page.Text("snapshot latest"); f.Page.DrainView();
            Check(f.Page.ViewQueue == 0, "StateChanged/button refresh does not recurse into RequestRender");
            f.Page.InitializeAgain(); f.Page.Text("one subscription"); f.Page.DrainView();
            Check(f.Page.RenderRequestsForLastChange == 1, "initialization guard prevents duplicate snapshot subscriptions");
        }
        RealRenderOwnership();
        using (var f = new Fixture())
        {
            f.Page.Text("queued submit"); f.Page.DispatcherQueue.HasThreadAccess = false;
            f.Page.PublicSubmit(); f.Page.Switch("ses_other"); f.Page.DispatcherQueue.HasThreadAccess = true;
            while (f.Page.DispatcherQueue.Queue.TryDequeue(out var callback)) callback();
            Check(f.Http.Posts == 0, "queued SubmitRequested rejects token invalidated before UI dispatch");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("menu args"); await f.Page.LoadMenu();
            f.Page.Key(VirtualKey.Enter); Check(f.Http.Posts == 0 && f.Page.Snapshot.SelectedCommand == "review", "actual page key hook accepts menu only");
            f.Page.Key(VirtualKey.Enter); Check(f.Http.Posts == 0, "actual page latch consumes held Enter after menu accept");
            f.Page.KeyUp(VirtualKey.Enter); f.Page.Key(VirtualKey.Enter); Check(f.Http.Posts == 1, "fresh eligible Enter submits selected command exactly once");
        }
        using (var f = new Fixture())
        {
            f.Page.SetWorking(); f.Page.Key(VirtualKey.Enter);
            Check(f.Http.Posts == 0 && f.Page.Stops == 0 && f.Page.ShouldStop(), "Enter on empty working composer never Stop");
            f.Page.Click(); Check(f.Page.Stops == 1, "explicit click alone invokes Stop");
            f.Page.Text("not sent"); f.Page.Composing = true; f.Page.Key(VirtualKey.Enter); f.Page.Composing = false;
            f.Page.Shift = true; f.Page.Key(VirtualKey.Enter); f.Page.Shift = false;
            Check(f.Http.Posts == 1, "page hook preserves IME and Shift+Enter after the one explicit Stop");
        }
        using (var f = new Fixture())
        {
            f.Page.SetWorking(); FluentCode.App.Owner = null; var before = Windows.Storage.Pickers.FileOpenPicker.Opens;
            await f.Page.Pick(); Check(Windows.Storage.Pickers.FileOpenPicker.Opens == before && !f.Page.Snapshot.IsBusy, "missing owner opens no native dialog/ticket");
            FluentCode.App.Owner = new(); var files = Signal<IReadOnlyList<Windows.Storage.StorageFile>?>();
            Windows.Storage.Pickers.FileOpenPicker.Next = () => files.Task;
            var pick = f.Page.Pick(); Check(!f.Page.SendEnabled && !f.Page.ShouldStop() && f.Page.Snapshot.IsBusy, "actual page picker busy disables Send AND Stop");
            var focusBeforeSwitch = f.Page.PickFocusAttempts;
            f.Page.Switch("ses_other"); files.SetResult(Array.Empty<Windows.Storage.StorageFile>()); await pick;
            Check(f.Page.PickFocusAttempts == focusBeforeSwitch && !f.Page.Snapshot.IsBusy && f.Http.Posts == 0, "late picker completion suppresses focus, completes ticket, never submits");
            Windows.Storage.Pickers.FileOpenPicker.Next = () => throw new InvalidOperationException("synthetic native error");
            await f.Page.Pick(); Check(!f.Page.Snapshot.IsBusy && f.Page.Snapshot.Attachments.Count == 0, "native error reaches adapter completion without attachments");
            Windows.Storage.Pickers.FileOpenPicker.Next = () => Task.FromResult<IReadOnlyList<Windows.Storage.StorageFile>?>(new[] { new Windows.Storage.StorageFile("synthetic.txt") });
            await f.Page.Pick(); Check(f.Page.Snapshot.Attachments.Count == 1 && f.Http.Posts == 0, "real adapter and bounded loader attach synthetic bytes only, never submit");
        }
        // Structural lifecycle supplement, explicitly not execution of the full WinUI page. Production
        // methods above are compiled verbatim. These guards ensure actual mutation paths call binding.
        var page = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.xaml.cs"));
        foreach (var method in new[] { "OnPageLoaded", "PausePage", "ResumeCachedSource", "ApplyCurrentSource", "InvalidateForSourceChange", "SwitchToSessionAsync", "NavigateToSessionAsync", "HandleApiException", "DisposeOwnClient" })
        {
            var body = Member(page, method);
            Check(body.Contains("BindComposer();", StringComparison.Ordinal), "actual lifecycle hook binds: " + method);
        }
        var unload = Member(page, "OnPageUnloaded");
        Check(unload.Contains("if (IsLoaded) return;", StringComparison.Ordinal)
            && unload.Contains("PausePage();", StringComparison.Ordinal), "temporary unload ignores late events and delegates to pause");
        var pause = Member(page, "PausePage");
        var suspend = pause.IndexOf("SuspendDraftSlotForPageUnload();", StringComparison.Ordinal);
        Check(suspend >= 0 && suspend < pause.IndexOf("_pageActive = false;", StringComparison.Ordinal)
            && pause.IndexOf("_sessionGeneration++;", StringComparison.Ordinal) < pause.IndexOf("BindComposer();", StringComparison.Ordinal),
            "pause captures the draft before deactivation and rebinds after advancing the session fence");
        Check(new[] { "_pageGeneration++;", "UpdateViewAcknowledgementVisibility(false);", "InvalidateViewAcknowledgement();",
            "StopEventSync();", "pageCts?.Cancel();", "CancelSessionOperations();", "DetachConversationEvents();" }
            .All(call => pause.Contains(call, StringComparison.Ordinal)), "pause revokes view proof, requests and subscriptions");
        var pausedLifecycle = unload + pause;
        Check(new[] { "DiscardDraft", "ReleaseDraftSlot(", "DisposeOwnClient();", "ClearSessionReturnContext();", "ShutdownPage();", "PageReleased?.Invoke" }
            .All(call => !pausedLifecycle.Contains(call, StringComparison.Ordinal)),
            "temporary pause retains cached draft actors, client and return context instead of final cleanup");
        var shutdown = Member(page, "ShutdownPage");
        var released = shutdown.IndexOf("_pageReleased = true;", StringComparison.Ordinal);
        Check(shutdown.Contains("if (_pageReleased) return;", StringComparison.Ordinal)
            && released >= 0 && released < shutdown.IndexOf("PausePage();", StringComparison.Ordinal)
            && shutdown.Contains("PageReleased?.Invoke(this, EventArgs.Empty);", StringComparison.Ordinal)
            && shutdown.Contains("PageReleased = null;", StringComparison.Ordinal), "final release pauses even before native unload and releases callbacks once");
        Check(new[] { "_source = null;", "DisposeOwnClient();", "ClearSessionReturnContext();", "_conversationView.ClearMessages();",
            "ReleaseDraftSlot(slot)", "_activeDraftSlot = null;", "_draftRecoverySlot = null;", "controller.DiscardDraft(controller.Snapshot.Token)" }
            .All(call => shutdown.Contains(call, StringComparison.Ordinal)), "final release disposes client, return context, conversation and retained composer payloads");
        var reset = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.SessionInsights.cs"));
        Check(reset.Contains("partial void OnMessageHistoryReset() => ResetSessionInsights();", StringComparison.Ordinal)
            && !Member(reset, "ResetSessionInsights").Contains("DiscardDraft", StringComparison.Ordinal), "history reset does not discard composer");
        Check(Member(page, "UpdateInteractionsSection").Contains("RefreshComposerView();", StringComparison.Ordinal), "gate protection refresh hook exists outside buttons");
        Check(!Member(page, "UpdateComposerButtons").Contains("RefreshComposerView", StringComparison.Ordinal), "no button/render feedback loop");
        var picker = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeComposerFilePicker.cs"));
        Check(picker.Split("await controller.AddFilesAsync(", StringSplitOptions.None).Length == 2, "adapter has exactly one ticket completion invocation site");
        var view = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeComposerView.cs"));
        // Static wiring proof only: the real WinUI view is not compiled by this harness. Match the
        // deferred source read, not the old statement terminator (the callback is now an expression lambda).
        // RealRenderOwnership executes the production lifetime/ledger with an explicit scheduler/editor seam.
        Check(System.Text.RegularExpressions.Regex.IsMatch(view,
            @"\b_lifetime\s*=\s*new\s+OpenCodeComposerViewLifetime\s*\(\s*new\s+DispatcherScheduler\s*\(\s*_dispatcher\s*\)\s*,\s*\(\s*\)\s*=>\s*Render\s*\(\s*_source\s*\(\s*\)\s*\)\s*\)\s*;"),
            "static real-view wiring: lifetime callback reads current source at execution, not a captured snapshot");
        Check(System.Text.RegularExpressions.Regex.IsMatch(view,
            @"\binternal\s+void\s+RequestRender\s*\(\s*\)\s*=>\s*_lifetime\s*\.\s*RequestRender\s*\(\s*\)\s*;"),
            "static real-view wiring: render requests delegate to production lifetime owner");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(view,
            @"\b(?:OpenCodeClient|HttpClient|HttpMessageInvoker|OpenCodePageSource|OpenCodeInteractionGate|OpenCodeComposerBinding|OpenCodeComposerController)\b"),
            "static real-view boundary: no transport/client/source/gate/binding/controller type dependency");
        var composer = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Composer.cs"));
        Check(new[] { "DiscardRequested", "_statusDiscardButton", "_discardFlyout", "BuildDiscardFlyout", "OpenCodeComposerInactiveDiscard", "OpenCodeComposerDiscardConfirm", "丢弃草稿" }
            .All(removed => !(view + composer).Contains(removed, StringComparison.Ordinal)),
            "DD1 no discard UI, confirmation, event or page hookup remains");
        Check(composer.Split("TextEdited +=", StringSplitOptions.None).Length == 2
            && composer.Contains("e.Acknowledgment =", StringComparison.Ordinal)
            && !composer.Contains("_composerController.SetText(", StringComparison.Ordinal), "one acknowledged bridge; no parallel SetText or manual clear");
        Check(!System.Text.RegularExpressions.Regex.IsMatch(composer + page, @"\bComposer\.Text\s*=(?!=)"), "all production Page editor assignments removed");
        var button = Member(page, "SendStopButton_Click");
        Check(button.Contains("PrepareSubmission()", StringComparison.Ordinal)
            && button.IndexOf("PrepareSubmission()", StringComparison.Ordinal) < button.IndexOf("ShouldStopSelectedSession()", StringComparison.Ordinal), "actual button prepares before Send/Stop predicate");
    }
    private static void RealRenderOwnership()
    {
        // Executes REAL controller, ledger and lifetime. Only the queued scheduler, editor storage and
        // render callback are test seams; this is not execution of OpenCodeComposerView/WinUI rendering.
        using var f = new Fixture();
        var before = checks;
        var editor = string.Empty;
        var renders = 0;
        OpenCodeComposerSnapshot? rendered = null;
        var sync = new OpenCodeComposerEditorSync(() => f.Page.Snapshot, () => editor,
            text => editor = text, () => false, f.Page.Controller.TryApplyEditorInput);
        var scheduler = new OpenCodeComposerView.HandoffScheduler { Hold = true };
        var lifetime = new OpenCodeComposerViewLifetime(scheduler, () =>
        {
            sync.Reconcile();
            rendered = f.Page.Snapshot;
            renders++;
        });
        f.Page.Controller.SetText(f.Page.Snapshot.Token, "queued old");
        lifetime.RequestRender();
        f.Page.Controller.SetText(f.Page.Snapshot.Token, "queued latest");
        lifetime.RequestRender();
        Check(renders == 0 && editor == string.Empty, "real lifetime defers rendering/editor reconciliation until dispatch");
        scheduler.Drain();
        Check(renders == 1, "real lifetime coalesces multiple render requests");
        Check(rendered?.Token == f.Page.Snapshot.Token && rendered.Text == "queued latest"
            && editor == "queued latest" && sync.BaselineToken == rendered.Token,
            "real lifetime callback and ledger read latest controller snapshot, not enqueue-time state");
        scheduler.Hold = true;
        f.Page.Controller.SetText(f.Page.Snapshot.Token, "after unload");
        lifetime.RequestRender();
        lifetime.OnUnloaded();
        scheduler.Drain();
        Check(renders == 1 && editor == "queued latest", "real lifetime rejects queued render from unloaded generation");
        scheduler.Hold = true;
        lifetime.OnLoaded();
        scheduler.Drain();
        Check(renders == 2 && editor == "after unload" && sync.BaselineToken == f.Page.Snapshot.Token,
            "real lifetime resumes with current snapshot and ledger baseline");
        Console.WriteLine($"PASS: real render-ownership helpers, {checks - before} assertions; scheduler/editor/render callback seams, not native view execution.");
    }
    private static string Member(string text, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^    (?:private|public) [^\r\n]*\b" + name + @"\(");
        if (!match.Success) throw new InvalidOperationException("Missing production method " + name);
        var end = text.IndexOf("\n    }", match.Index, StringComparison.Ordinal);
        return text.Substring(match.Index, end + 6 - match.Index);
    }

    private static async Task AcknowledgmentAndHandoffs()
    {
        using (var f = new Fixture())
        {
            f.Page.Text("cycle"); var basis = f.Page.Snapshot.Token;
            f.Page.Text("cycle");
            Check(f.Page.Snapshot.Token != basis && f.Page.InputState == OpenCodeComposerInputState.Clean,
                "single synchronous page ACK accepts a same-string observed edit cycle");
            Check(f.Page.LastEditorAck?.Token == f.Page.Snapshot.Token, "page returns exact controller acknowledgment");
        }
        using (var f = new Fixture())
        {
            f.Page.SetWorking(); f.Page.ObserveText("pending before button");
            f.Page.Click();
            Check(f.Http.Posts == 1 && f.Page.Stops == 0, "button prepares observed input before Send-versus-Stop decision");
            Check(f.Http.Body!.Contains("pending before button", StringComparison.Ordinal), "button sends acknowledged observed text");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("old"); var stale = f.Page.Snapshot.Token;
            f.Page.ObserveText("new before route"); await f.Page.RouteToken(stale);
            Check(f.Http.Posts == 0 && f.Page.Snapshot.Text == "new before route", "routing prepares first and rejects token invalidated by ACK");
            f.Page.PublicSubmit(); Check(f.Http.Posts == 1, "new public request captures token after preparation");
        }
        using (var f = new Fixture())
        {
            f.Page.ObserveText("pending legacy"); await f.Page.SendLegacyDirect();
            Check(f.Http.Posts == 1 && f.Http.Body!.Contains("pending legacy", StringComparison.Ordinal), "direct legacy entry prepares before capturing submitted token/text");
        }
        using (var f = new Fixture())
        {
            await f.Page.Command("basis"); f.Page.ObserveText("held visible");
            f.Page.Controller.SetText(f.Page.Snapshot.Token, "intervening controller edit");
            f.Page.Click(); f.Page.PublicSubmit(); await f.Page.SendLegacyDirect();
            Check(f.Http.Posts == 0 && f.Page.Stops == 0, "rejected pending ACK blocks all page submission and Stop routing");
            Check(f.Page.InputState == OpenCodeComposerInputState.HeldStale && f.Page.Composer.Text == "held visible", "held input remains visible without fresh-token rebase");
        }
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new Fixture(); if (enhanced) await f.Page.File(); f.Page.Text("submitted");
            var submitted = f.Page.Snapshot.Token;
            f.Page.HoldHandoff(); var send = f.Page.Send();
            await f.Page.HandoffEntered.WaitAsync(TimeSpan.FromSeconds(10));
            Check(f.Page.HandoffToken == submitted && f.Page.HandoffCalls == 1, "actual page passes captured token to one awaited view handoff");
            Check(!send.IsCompleted && f.Page.Pending && f.Page.Sending && f.Page.Receipts == 0, "local owner spans handoff before receipt publication");
            using (var lease = await f.Lease()) Check(lease.GetComposerSubmissionProtection() is null, "lease released and enhanced success settled before handoff");
            f.Page.ObserveText("observed before clear"); // Deliberately withhold asynchronous TextChanged.
            f.Page.ReleaseHandoff(); await send;
            Check(f.Page.Snapshot.Text == "observed before clear" && f.Page.Composer.Text == "observed before clear", "handoff flushes early input before captured-token clear");
            Check(f.Page.Snapshot.Attachments.Count == (enhanced ? 1 : 0), "later input retains the complete submitted draft");
            Check(f.Page.Receipts == 1 && !f.Page.Pending && !f.Page.Sending, "valid receipt survives later revision; local owner ends afterward");
            Check(f.Page.ReplayFinalizer() == OpenCodeComposerDraftFinalization.AlreadyFinalized, "closed finalizer cannot erase a later draft");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("retain on local cancellation"); f.Page.HoldHandoff(); var send = f.Page.Send();
            await f.Page.HandoffEntered.WaitAsync(TimeSpan.FromSeconds(10));
            f.Page.CancelPageLifetime(); await send;
            Check(f.Page.Snapshot.Text == "retain on local cancellation" && f.Page.Receipts == 1, "local lifetime cancellation retains legacy draft without invalidating owned receipt");
            Check(!f.Page.Pending && f.Page.Messages.Count == 0, "cancelled local clearing is not reported as a transport failure");
            f.Page.ReleaseHandoff();
            Check(f.Page.Snapshot.Text == "retain on local cancellation" && f.Http.Posts == 1, "late cancelled handoff neither clears nor retries");
        }
        using (var f = new Fixture())
        {
            f.Page.Text("old accepted"); f.Page.HoldHandoff(); var send = f.Page.Send();
            await f.Page.HandoffEntered.WaitAsync(TimeSpan.FromSeconds(10));
            f.Page.Switch("ses_other"); await send; f.Page.ReleaseHandoff();
            Check(f.Page.Receipts == 0 && f.Page.Snapshot.Text == "", "legacy handoff lost origin cannot clear or publish receipt into independent target slot");
            f.Page.Switch("ses_test");Check(f.Page.Snapshot.Text=="old accepted"&&f.Http.Posts==1,"original accepted text retained conservatively in its own slot, never auto-resubmitted");
        }
    }
}

namespace FluentCode.Pages
{
    public sealed partial class OpenCodePage
    {
        internal IReadOnlyList<OpenCodeInboxItem>? SteerProjection;
        private void UpdateQueuedPrompts() => SteerProjection = ProjectSteerReceipts(null);
        internal void ReprojectSteer(IReadOnlyList<OpenCodeInboxItem>? items) => SteerProjection = ProjectSteerReceipts(items);
        // Existing page fields/services, not a replacement composer implementation.
        private OpenCodePageSource? _source;
        private OpenCodeClient? _client;
        private long _sourceRevision;
        private int _pageGeneration = 1, _sourceGeneration = 1, _sessionGeneration = 1;
        private bool _pageActive = true, _sessionMissing, _isSending, _isComposing, _enterDown, _shiftDown;
        private DateTimeOffset _lastCompositionEndedAt = DateTimeOffset.MinValue;
        private string? _selectedSessionId = "ses_test";
        // This composer-only fixture has no descendant permission widgets; real owner routing is
        // source-linked with the existing permission card in opencode-event-sync.
        private string? _sessionRecoveryTargetId;
        private bool IsInteractionSession(string? sessionId) => sessionId == _selectedSessionId;
        private string? GetInteractionSession(bool isForm, string id) => _selectedSessionId;
        internal string? InteractionRecoverySession => _sessionRecoveryTargetId;
        private CancellationTokenSource? _pageCts = new(), _sessionCts = new();
        private readonly List<string> _pendingAckIds = new();
        private readonly HashSet<string> _workingSessions = new();
        private void RaiseExecutionState(OpenCodeExecutionStateChangedEventArgs args) { }
        private bool _deliveredNoticeShown;
        private readonly OpenCodeConversationView _conversationView = new();
        // Native switch timing/frame scheduling is outside this composer-only host.
        private Task TranscriptFirstFrame => Task.CompletedTask;
        private void RecordRetainedSwitchApply(long started) { }
        private void RecordRestSwitchApply(long started) { }
        private enum ConnectionState { Connected, Failed }
        internal readonly TextBox Composer = new();
        internal readonly Button SendStopButton = new();
        internal readonly Button ComposerQueueButton = new(), ComposerExpandButton = new();
        private bool _composerOverFiveLines = false;
        private void InitializeComposerLayout() { }
        private bool FullScreenComposerActive => false; // Native overlay boundary, exercised by app-open check.
        private bool FullScreenControlDown => false;
        private void TryPendingFullScreenClose() { }
        private void CloseFullScreenComposer(bool focus) => throw new InvalidOperationException("Native full-screen close is outside this inert page host.");
        private void SubmitFullScreenComposer() => throw new InvalidOperationException("Native full-screen submit is outside this inert page host.");
        private void Composer_TextChanged(object sender, TextChangedEventArgs args) => Composer_TextChanged();
        internal bool IsChildComposerSession { get; set; }
        internal readonly Panel ChildStopHost = new();
        internal readonly Button ChildStopButton = new();
        internal readonly TextBox ChildStopLabel = new();
        internal void RefreshChildStop() => RefreshChildStopButton();
        internal Task StopChild() => InterruptSelectedSessionAsync(childStop: true);
        private void RefreshChildComposerVisibility() { AssertComposerUiThread(); }
        private void OpenFullScreenComposer() { FullScreenOpens++; }
        internal int FullScreenOpens;
        internal void AdvanceSourceGeneration() => _sourceGeneration++;
        private void QueueComposerLayout() { }
        private void RefreshWslQuickAccess() { } // B12 quick access is outside this hookup host.
        private void OpenTerminalView(string shellId) { } // AT6 terminal page is outside this hookup host.
        internal readonly ProgressRing SendProgress = new();
        internal readonly Panel ComposerCompanionHost = new(), AgentSelectorHost = new();
        internal readonly Dispatcher DispatcherQueue = new();
        internal readonly List<string> Messages = new();
        internal readonly TaskCompletionSource<bool> Refreshed = Program.Signal<bool>();
        internal Task RefreshPending = Task.CompletedTask;
        internal int Tracked, Working, Refreshes, WaitingUpdates;
        internal Func<int> CountStops = () => 0;
        internal int Stops => CountStops();
        internal bool StopMode;
        internal OpenCodeComposerSendMode SendMode;
        // Production field lives in Pages/OpenCodePage.ComposerOverlays.cs (WinUI overlay glue, not compiled here).
        private bool _composerCoveredByPermission;
        internal bool CoveredByPermission { set { _composerCoveredByPermission = value; UpdateComposerButtons(); } }
        // Production member lives in Pages/OpenCodePage.QuestionOverlay.cs (L13, WinUI overlay glue, not compiled here);
        // the question overlay is not modelled by this harness, so only the permission cover contributes.
        private bool ComposerCoveredByOverlay => _composerCoveredByPermission;
        // Actual IME overlay wake-up + both overlay partials are exercised by opencode-form-view, not this composer host.
        private void QueueDeferredComposerOverlayRefresh() { }
        internal bool ComposerEnabled => Composer.IsEnabled;
        internal OpenCodePage(OpenCodePageSource source, OpenCodeClient client)
        {
            _source = source; _sourceRevision = source.Revision; _client = client;
            // This host starts after an explicit empty, authoritative no-stage history fixture.
            // Initial unread selection is exercised via the actual latest-GET pagination harness.
            _messageHistoryBoundaryKnown = true; _messageHistoryNeedsState = false;
            InitializeAgentSelector(); InitializeComposer();
            _messageBinding = new(source, _sourceRevision, _sourceGeneration, _pageGeneration, client, _selectedSessionId!, "/synthetic");
            BindComposer();
            _composerView!.StateChanged += (_, _) => { };
            DrainView(); ConfigureAgentSelectorForSession("ses_test"); UpdateComposerButtons();
        }
        internal OpenCodeComposerController Controller => _composerController!;
        internal OpenCodeComposerSnapshot Snapshot => GetComposerSnapshot();
        internal bool Pending => _agentSelector.Pending;
        internal void PendingAgent(Func<Task<bool>> apply) { _agentSelector.HasPendingSelection = true; _agentSelector.ApplySelection = apply; }
        internal void SelectionCompletion(Action complete) => _agentSelector.SelectionCompleted = complete;
        internal bool SelectionPending => _agentSelector.HasPendingSelection;
        internal void CachedModels(OpenCodeModelInfo model) => _agentSelector.ModelChoices = new[] { model };
        internal OpenCodeModelRef? PendingModel => _agentSelector.CurrentModel;
        internal int AgentApplyCalls => _agentSelector.ApplyCalls;
        internal bool Sending => _isSending;
        internal bool SendEnabled => SendStopButton.IsEnabled;
        internal bool Missing => _sessionMissing;
        internal long Begins => _localSubmissionSequence;
        internal int Receipts => _pendingAckIds.Count;
        internal bool Composing { set => _isComposing = value; }
        internal bool Shift { set => _shiftDown = value; }
        internal int ViewQueue => _composerView!.QueueCount;
        internal int RenderRequestsForLastChange => _composerView!.RequestCount;
        internal int PickFocusAttempts => _composerView!.FocusAttempts;
        internal OpenCodeComposerInputState InputState => _composerView!.Sync.State;
        internal OpenCodeComposerEditorAck? LastEditorAck => _composerView!.LastAck;
        internal Task HandoffEntered => _composerView!.HandoffEntered.Task;
        internal int HandoffCalls => _composerView!.HandoffCalls;
        internal OpenCodeComposerToken HandoffToken => _composerView!.HandoffToken;
        internal void HoldHandoff() => _composerView!.Scheduler.Hold = true;
        internal void ReleaseHandoff() => _composerView!.Scheduler.Drain();
        internal OpenCodeComposerDraftFinalization ReplayFinalizer() => _composerView!.CapturedFinalizer!();
        internal void CancelPageLifetime() => _pageCts!.Cancel();
        internal void DrainView() => _composerView!.Drain();
        internal void InitializeAgain() { InitializeComposer(); _composerView!.RequestCount = 0; }
        internal void ObserveText(string text) { Composer.Text = text; _composerView!.Evidence.RecordTextChanging(); }
        internal void Text(string text) { ObserveText(text); Composer_TextChanged(Composer, new()); }
        internal bool ApplyStagedImage(OpenCodeComposerImagePaste paste) => _composerView!.ApplyStagedImage(paste);
        internal void AppendDocumentText(string text) => _composerView!.AppendDocumentText(text);
        internal void ClearHeldFromView() => _composerView!.ClearHeldFromView();
        internal async Task Command(string args) { Text("/review " + args); await Controller.LoadCatalogAsync(Snapshot.Token, _composerView!.Lifetime); _composerView.Choose(Snapshot.Token, Snapshot.CatalogHandle!, new OpenCodeComposerCommandIdentity.Server("review")); DrainView(); UpdateComposerButtons(); }
        internal async Task File() { var ticket = Controller.BeginPick(Snapshot.Token)!; await Controller.AddFilesAsync(ticket, new[] { new OpenCodeComposerFileSource("synthetic.txt", null, _ => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }))) }); DrainView(); UpdateComposerButtons(); }
        internal void RemoveCommand() { Controller.RemoveCommand(Snapshot.Token); DrainView(); UpdateComposerButtons(); }
        // Fixture-only core reset to probe protection persistence; no corresponding composer UI action.
        internal void Discard() { Controller.DiscardDraft(Snapshot.Token); _composerView!.Sync.Reconcile(); UpdateComposerButtons(); }
        internal Task Send(string? delivery = null) => RouteComposerSubmissionAsync(Snapshot.Token, delivery ?? RequestedComposerDelivery);
        internal Task SendLegacyDirect() => SendPromptAsync();
        internal Task RouteToken(OpenCodeComposerToken token) => RouteComposerSubmissionAsync(token, RequestedComposerDelivery);
        internal void PublicSubmit(string? delivery = null) => RequestComposerSubmission(delivery);
        internal void ClickQueue() => ComposerQueueButton_Click(ComposerQueueButton, new());
        internal void ClickFreeCommand() => _composerView!.ClickArgumentFreeCommand();
        internal Task Pick() => PickComposerFilesAsync(Snapshot.Token);
        internal Task LoadMenu() => Controller.LoadCatalogAsync(Snapshot.Token, _composerView!.Lifetime);
        internal bool ViewComplete(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle, string completion) => _composerView!.Complete(token, handle, completion);
        internal void Key(VirtualKey key) { var e = new KeyRoutedEventArgs { Key = key }; Composer_PreviewKeyDown(Composer, e); Composer_KeyDown(Composer, e); }
        internal void KeyUp(VirtualKey key) => Composer_KeyUp(Composer, new() { Key = key });
        internal void Click() => SendStopButton_Click(SendStopButton, new());
        internal bool ShouldStop() => ShouldStopSelectedSession();
        internal void SetWorking() { _workingSessions.Add(_selectedSessionId!); UpdateComposerButtons(); }
        internal void ObserveExecution(List<OpenCodeMessage> rows) => ObserveExecutionState(_selectedSessionId!, rows);
        internal void RebaselineExecution(string acceptedId) { _historyExecutionRebaseline = true; _historyAcceptedPromptId = acceptedId; }
        internal void Switch(string session)
        {
            if(!TryPrepareDraftSlotNavigation())return;
            _sessionMissing = false; // Real SwitchToSessionAsync clears the previous session's missing flag.
            _selectedSessionId = session; _sessionGeneration++; BeginSessionMessageBinding(session,"/synthetic");BindComposer();
            _sessionCts?.Cancel(); _sessionCts = new(); ConfigureAgentSelectorForSession(session);
            _pendingAckIds.Clear(); UpdateComposerButtons();
            DrainView();
        }
        internal bool BindingUnbound => OpenCodeComposerController.IsUnbound(_composerBinding);
        internal bool RecoveryPending=>_draftRecoveryPending;
        internal void EnterBlank()
        {
            if(!TryPrepareDraftSlotNavigation())return;
            if(_newSessionTab?.Tag is not Guid id||!_blankTabs.ContainsKey(id))
            {
                id=Guid.NewGuid();_newSessionTab=new(){Tag=id,Header="新会话",IsClosable=true};
                _blankTabs.Add(id,new(id,_draftSourceEpoch,_newSessionTab));SessionTabs.TabItems.Add(_newSessionTab);
            }
            _activeBlankSlotId=id;
            _newSessionDraftActive = true; _newSessionCreatePending = false;
            _selectedSessionId = null; _sessionGeneration++; BindComposer();
            SetupBlankPreferences();
            _sessionCts?.Cancel(); _sessionCts = new(); _pendingAckIds.Clear(); UpdateComposerButtons();
            DrainView();
        }
        internal void LeaveBlankTo(string session)
        {
            Switch(session);
            _newSessionDraftActive = false; BindComposer();
            _sessionCts?.Cancel(); _sessionCts = new(); ConfigureAgentSelectorForSession(session);
            _pendingAckIds.Clear(); UpdateComposerButtons();
            DrainView();
        }
        internal void UnloadFixture() { ForceSuspendDraftSlot("Fixture page unloaded");_pageActive = false; _pageGeneration++; _sessionGeneration++; BindComposer(); _pageCts?.Cancel(); _sessionCts?.Cancel(); ClearAgentSelector(); }
        internal bool CheckBindingField(string field)
        {
            var b = _composerBinding!;
            return field switch
            {
                "page" => !OwnsComposerBinding(b with { PageGeneration = b.PageGeneration + 1 }),
                "sourceGeneration" => !OwnsComposerBinding(b with { SourceGeneration = b.SourceGeneration + 1 }),
                "sessionGeneration" => !OwnsComposerBinding(b with { SessionGeneration = b.SessionGeneration + 1 }),
                "revision" => !OwnsComposerBinding(b with { Revision = b.Revision + 1 }),
                "client" => !OwnsComposerBinding(b with { Client = null! }),
                "session" => !OwnsComposerBinding(b with { SessionId = "ses_other" }),
                "source" => !OwnsComposerBinding(b with { Source = null! }),
                "missing" => MissingOwnerCheck(b),
                _ => ActiveOwnerCheck(b),
            };
        }
        private bool MissingOwnerCheck(OpenCodeComposerBinding b) { _sessionMissing = true; var result = !OwnsComposerBinding(b); _sessionMissing = false; return result; }
        private bool ActiveOwnerCheck(OpenCodeComposerBinding b) { _pageActive = false; var result = !OwnsComposerBinding(b); _pageActive = true; return result; }
        private bool IsShiftDown() => _shiftDown;
        private void UpdateComposerLayout() { }
        private void SetSendStopMode(OpenCodeComposerSendMode mode, bool hasDraft) { SendMode = mode; StopMode = mode == OpenCodeComposerSendMode.Working; }
        private Task RefreshMessagesAsync(bool forceFollow = false, bool explicitRefresh = false) { Refreshes++; Refreshed.TrySetResult(true); return RefreshPending; }
        internal Task? EventCoverage;
        internal void CompleteBindingSnapshot() { _messageHistoryNeedsState = false; _messageHistoryBoundaryKnown = true; }
        private Task? CaptureHealthyEventStream() => EventCoverage;
        private bool HasContinuousEventCoverage(Task? coverage) => coverage is not null && ReferenceEquals(coverage, EventCoverage) && !_messageHistoryNeedsState;
        private void ScrollToBottom(bool animated) { }
        private void UpdateWaitingText() => WaitingUpdates++;
        private void RaiseSessionTracked(string session, OpenCodeSessionTrackReason reason) => Tracked++;
        private void MarkWorking(string session) { Working++; _workingSessions.Add(session); }
        private void SetConnectionState(ConnectionState state) { }
        private void ShowMessage(InfoBarSeverity severity, string title, string message) => Messages.Add(title + message);
        private void HandleApiException(OpenCodeApiException ex, string operation) { if (ex.StatusCode == 404) { _sessionMissing = true; BindComposer(); } }
        private void UpdateInteractionsSection() => RefreshComposerView();
        private void OnSubagentSessionRequested(object? sender, EventArgs args) { }
        // Native card dispatch is outside this composer-only host; wire the real typed events but fail if invoked.
        private void OnNativeUiAssistantRequested(object? sender, NativeUiAssistantRequestedEventArgs args)
            => throw new InvalidOperationException("Native card dispatch is outside this inert composer host.");
        private void OnNativeUiBrowserRequested(object? sender, NativeUiBrowserRequestedEventArgs args)
            => throw new InvalidOperationException("Native card browser dispatch is outside this inert composer host.");
    }
}
namespace FluentCode
{
    internal sealed class App : Application
    {
        internal static Window? Owner = new();
        internal Window? OpenCodeOwnerWindow => Owner;
    }
}
namespace Microsoft.UI.Xaml
{
    public class FrameworkElement { }
    public enum Visibility { Visible, Collapsed }
    public enum FocusState { Unfocused, Programmatic }
    public class Window { }
    public class XamlRoot { }
    public class Application { public static Application Current { get; } = new FluentCode.App(); }
    public class RoutedEventArgs : EventArgs { }
    public static class VisualStateManager { public static bool GoToState(object owner,string state,bool transitions)=>true; }
    public sealed class Dispatcher
    {
        internal bool HasThreadAccess = true;
        internal readonly Queue<Action> Queue = new();
        internal bool TryEnqueue(Action action) { Queue.Enqueue(action); return true; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Informational, Warning, Error, Success }
    public sealed class TextChangedEventArgs : EventArgs { }
    public sealed class TextBox { public string Text = ""; public bool IsEnabled; public FocusState FocusState; public int Focuses; public int SelectionStart, SelectionLength; public void Select(int start, int length) { SelectionStart = start; SelectionLength = length; } public void Focus(FocusState state) { FocusState = state; Focuses++; } }
    public sealed class Button { public bool IsEnabled; public Visibility Visibility; }
    public sealed class TabViewItem { public object? Tag; public object? Header; public object? IconSource; public bool IsClosable; }
    public sealed class FontIconSource { public string? Glyph; }
    public static class ToolTipService { public static void SetToolTip(object target, object text) { } }
    public sealed class TabsStub { public List<object> TabItems { get; } = new(); public object? SelectedItem {get;set;} }
    public sealed class ProgressRing { public bool IsActive; public Visibility Visibility; }
    public sealed class Panel { public List<object> Children = new(); public Visibility Visibility; }
}
namespace Microsoft.UI.Xaml.Input { public sealed class KeyRoutedEventArgs : EventArgs { public VirtualKey Key; public bool Handled; } }
namespace Microsoft.UI.Xaml.Automation { public static class AutomationProperties { public static void SetAutomationId(object value, string id) { } public static void SetName(object value, string name) { } } }
namespace Windows.System { public enum VirtualKey { Enter, Shift, Up, Down, Escape, Divide } }
namespace Windows.Storage
{
    public sealed class StorageFile(string name)
    {
        public string Name => name;
        public Task<Stream> OpenStreamForReadAsync() => Task.FromResult<Stream>(new MemoryStream(new byte[] { 4, 5 }));
    }
}
namespace Windows.Storage.Pickers
{
    public enum PickerViewMode { List }
    public enum PickerLocationId { DocumentsLibrary }
    public sealed class FileOpenPicker
    {
        internal static int Opens;
        internal static Func<Task<IReadOnlyList<StorageFile>?>> Next = () => Task.FromResult<IReadOnlyList<StorageFile>?>(Array.Empty<StorageFile>());
        public PickerViewMode ViewMode; public PickerLocationId SuggestedStartLocation; public string CommitButtonText = "";
        public List<string> FileTypeFilter = new();
        public Task<IReadOnlyList<StorageFile>?> PickMultipleFilesAsync() { Opens++; return Next(); }
    }
}
namespace WinRT.Interop
{
    public static class WindowNative { public static IntPtr GetWindowHandle(Window owner) => new(1); }
    public static class InitializeWithWindow { public static void Initialize(object picker, IntPtr handle) { } }
}
namespace FluentCode.Pages.OpenCode
{
    public sealed class OpenCodeConversationView {
        public void PreserveReadingAnchor(Microsoft.UI.Xaml.Controls.ScrollViewer viewport, Func<bool> ownerCurrent) { }
        public event EventHandler? SubagentSessionRequested;
        public event Action<string>? TerminalRequested;
        internal event EventHandler<NativeUiAssistantRequestedEventArgs>? NativeUiAssistantRequested;
        internal event EventHandler<NativeUiBrowserRequestedEventArgs>? NativeUiBrowserRequested;
        public List<OpenCodeMessage> Last = new();
        public bool ContainsRenderedMessage(string id) => Last.Any(message => message.Id == id);
        public void ApplyMessages(List<OpenCodeMessage> messages) => Last = messages;
        internal bool TryApplyRefreshedMessages(IReadOnlyList<OpenCodeMessage> messages) => false; // Keep the explicit full-apply boundary.
        internal long EstimateMessageBytes(OpenCodeMessage message)
            => 256 + 2L * System.Text.Json.JsonSerializer.Serialize(message, OpenCodeJson.Options).Length + 2L * (message.InstructionBody?.Length ?? 0);
        public void ClearMessages() => Last.Clear();
    }
    public sealed class OpenCodeAgentSwitchReportedEventArgs : EventArgs { public AgentSwitchStatus Status; public string Message = ""; }
    public sealed class OpenCodeAgentSelector
    {
        public event EventHandler<OpenCodeAgentSwitchReportedEventArgs>? SwitchReported;
        public event EventHandler? DisplayChanged { add { } remove { } }
        public bool HasPendingSelection { get; set; }
        internal Func<Task<bool>> ApplySelection = () => Task.FromResult(true);
        internal int ApplyCalls;
        public bool HasPendingModel { get; private set; }
        public string? CurrentAgentId { get; set; }
        public OpenCodeModelRef? CurrentModel { get; private set; }
        public IReadOnlyList<OpenCodeModelInfo> ModelChoices { get; set; } = Array.Empty<OpenCodeModelInfo>();
        public void SelectPendingModel(OpenCodeModelRef model) { CurrentModel = model; HasPendingModel = true; HasPendingSelection = true; }
        public async Task<bool> ApplyPendingSelectionAsync(bool forNextSend = false) { ApplyCalls++; return await ApplySelection(); }
        internal Action? SelectionCompleted;
        public void CompletePendingSelection() { HasPendingSelection = false; HasPendingModel = false; SelectionCompleted?.Invoke(); }
        internal bool Pending;
        public void Clear() => Pending = false;
        public void Configure(OpenCodePageSource source, long revision, string session, long generation, Task? transcriptFrame = null) { }
        public void SetLocalSubmissionPending(bool value) => Pending = value;
        public int Opens;
        internal bool OpenSelection() { Opens++; return true; }
        internal int DisplayRefreshes;
        internal void RefreshDisplay() => DisplayRefreshes++;
    }
    public sealed class OpenCodeComposerTokenEventArgs(OpenCodeComposerToken token) : EventArgs { public OpenCodeComposerToken Token => token; }
    public sealed class OpenCodeComposerTextEditedEventArgs(OpenCodeComposerToken token, string text) : EventArgs
    {
        public OpenCodeComposerToken Token => token;
        public string Text => text;
        internal bool AllowUnboundCarry { get; init; }
        internal OpenCodeComposerImagePaste? ImagePaste { get; init; }
        internal OpenCodeComposerLinkSpan? LinkPaste { get; init; }
        internal OpenCodeComposerDocumentObservation? Observation { get; init; }
        internal OpenCodeComposerEditorAck? Acknowledgment { get; set; }
    }
    public sealed class OpenCodeComposerAttachmentRemovedEventArgs(OpenCodeComposerToken token, Guid id) : EventArgs { public OpenCodeComposerToken Token => token; public Guid Id => id; }
    internal sealed class OpenCodeComposerAttachmentImageEventArgs : EventArgs { }
    internal static class OpenCodeComposerAttachmentImageViewer { internal static void Open(OpenCodeComposerAttachmentImageEventArgs e) { } } // Native viewer; page wiring only.
    internal sealed class OpenCodeComposerView(Func<OpenCodeComposerSnapshot> source,
        Func<OpenCodeComposerCatalogLifetime>? begin = null, Action<OpenCodeComposerCatalogLifetime>? end = null)
    {
        // View/native scheduler double around REAL ledger/handoff/lifetime helpers. It does not emulate
        // WinUI event delivery or rendering. Immediate scheduling is deliberate, with a hold seam below.
        internal event EventHandler? StateChanged;
        internal event EventHandler? CompositionSettled;
        internal event Action<bool>? CompositionChanged;
        internal Func<OpenCodeComposerMentionSearch?>? MentionSearchProvider { get; set; }
        internal void RefreshMentionCandidates() { } // Native mention-list requery is outside this inert view.
        internal Func<string?>? SplashModelIdProvider { get; set; }
        internal bool IsAutocompleteVisible => false;
        internal event Action<OpenCodeComposerToken, string, bool, int>? ContextChosen;
        internal event Action<OpenCodeComposerToken, bool>? ModelUndoRequested;
        internal event Action<OpenCodeComposerToken, OpenCodeComposerSelection>? DocumentSelectionChanged;
        internal void UndoDocument(bool redo = false) => ModelUndoRequested?.Invoke(source().Token, redo);
        internal void SelectDocument(OpenCodeComposerSelection selection) => DocumentSelectionChanged?.Invoke(source().Token, selection);
        internal event Action? RecoveryClearRequested;
        internal void ClearHeldFromView() => RecoveryClearRequested?.Invoke();
        internal bool TryClearHeldInput(Func<OpenCodeComposerToken, OpenCodeComposerEditorAck?> clear)
        { var ok = Sync.TryClearHeldInput(clear); Evidence.Refresh(); return ok; }
        private OpenCodeComposerImagePaste? imagePaste;
        internal bool ApplyStagedImage(OpenCodeComposerImagePaste paste)
        {
            if (!Sync.Prepare() || source().Token != paste.Token || editor!.Text != paste.BeforeText) return false;
            imagePaste = paste;
            editor.Text = paste.Text;
            Evidence.RecordTextChanging();
            var clean = Sync.Flush() == OpenCodeComposerInputState.Clean;
            imagePaste = null;
            return clean;
        }
        internal event EventHandler<OpenCodeComposerTextEditedEventArgs>? TextEdited;
        internal event EventHandler<OpenCodeComposerTokenEventArgs>? CommandRemoved, PickRequested;
        internal event EventHandler<OpenCodeComposerCatalogOpenRequestedEventArgs>? CatalogOpenRequested;
        internal event EventHandler<OpenCodeComposerCatalogDismissRequestedEventArgs>? CatalogDismissRequested;
        internal event EventHandler<OpenCodeComposerCommandChosenEventArgs>? CommandChosen;
        internal event EventHandler<OpenCodeComposerClientIntentEventArgs>? ClientIntentDismissed, ClientActionInvoked;
        internal event EventHandler<OpenCodeComposerTriggerCompletionEventArgs>? CompletionRequested;
        internal event EventHandler? SubmitRequested;
        internal void ClickArgumentFreeCommand() => SubmitRequested?.Invoke(this, EventArgs.Empty);
        internal bool Complete(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle, string completion) {
            var args = new OpenCodeComposerTriggerCompletionEventArgs(token, handle, completion); CompletionRequested?.Invoke(this, args); return args.Accepted;
        }
        internal OpenCodeComposerCatalogLifetime Lifetime = begin!();
        internal void EndLifetime() => end!(Lifetime);
        internal void OpenCatalog() => CatalogOpenRequested?.Invoke(this, new(source().Token, Lifetime, false));
        internal bool Choose(OpenCodeComposerToken token, OpenCodeComposerCatalogHandle handle, OpenCodeComposerCommandIdentity identity) {
            var args = new OpenCodeComposerCommandChosenEventArgs(token, handle, identity); CommandChosen?.Invoke(this, args); return args.Accepted;
        }
        internal bool Invoke(OpenCodeComposerClientActionKind displayKind) {
            var s = source(); var args = new OpenCodeComposerClientIntentEventArgs(s.Token, s.ClientIntent!.Id, displayKind);
            ClientActionInvoked?.Invoke(this, args); return args.Accepted;
        }
        internal void DismissIntent() { var s = source(); ClientIntentDismissed?.Invoke(this, new(s.Token, s.ClientIntent!.Id, s.ClientIntent.Kind)); }
        internal void Dismiss(OpenCodeComposerCatalogHandle handle) => CatalogDismissRequested?.Invoke(this, new(handle));
        internal event EventHandler<OpenCodeComposerAttachmentRemovedEventArgs>? AttachmentRemoved;
#pragma warning disable CS0067 // Raised only by the native view.
        internal event EventHandler<OpenCodeComposerAttachmentImageEventArgs>? AttachmentImageOpened;
#pragma warning restore CS0067
        internal int RequestCount, QueueCount, FocusAttempts;
        private TextBox? editor;
        private Func<bool> composing = () => false;
        internal OpenCodeComposerEditorSync Sync = null!;
        internal OpenCodeComposerInputEvidenceSource Evidence = null!;
        internal OpenCodeComposerInputEvidence InputEvidence => Evidence.Current;
        internal bool Live = true;
        internal int LifetimeGeneration = 1;
        private OpenCodeComposerAcceptedDraftHandoffs handoffs = null!;
        internal readonly HandoffScheduler Scheduler = new();
        internal readonly TaskCompletionSource<bool> HandoffEntered = Program.Signal<bool>();
        internal int HandoffCalls;
        internal OpenCodeComposerToken HandoffToken;
        internal Func<OpenCodeComposerDraftFinalization>? CapturedFinalizer;
        internal OpenCodeComposerEditorAck? LastAck;
        private OpenCodeComposerDocumentObservation? documentObservation;
        private OpenCodeComposerDocumentObservation? ReadDocumentObservation()
        {
            var observation = documentObservation;
            documentObservation = null;
            return observation;
        }
        internal void AppendDocumentText(string text)
        {
            var before = LastAck?.Document ?? source().Document;
            var next = before.Replace(before.Length, 0, text);
            documentObservation = new(before, next, new(next.Length, next.Length));
            editor!.Text = next.Serialize().Text;
            Evidence.RecordTextChanging();
            NotifyEditorTextChanged();
        }
        internal sealed class HandoffScheduler : IOpenCodeComposerUiScheduler
        {
            internal bool Hold;
            private readonly ConcurrentQueue<Action> queue = new();
            public bool TryEnqueue(Action callback) { if (Hold) queue.Enqueue(callback); else callback(); return true; }
            internal void Drain() { Hold = false; while (queue.TryDequeue(out var callback)) callback(); }
        }
        internal void AttachEditor(TextBox value, Func<bool> isComposing)
        {
            editor = value; composing = isComposing;
            Sync = new(source, () => editor.Text, text => { editor.Text = text; Evidence?.RecordTextChanging(); },
                composing, (token, text, carry) => {
                    var paste = imagePaste; imagePaste = null;
                    var e = new OpenCodeComposerTextEditedEventArgs(token, text) { AllowUnboundCarry = carry, ImagePaste = paste,
                        Observation = Sync.SubmittedObservation };
                    TextEdited?.Invoke(this, e);
                    return LastAck = e.Acknowledgment;
                }, ReadDocumentObservation);
            handoffs = new(new OpenCodeComposerViewLifetime(Scheduler, () => { }), () => Sync);
            Evidence = new(() => Sync, () => Live, () => LifetimeGeneration, composing);
            Evidence.LifetimeChanged();
        }
        internal void RequestRender() { RequestCount++; QueueCount = 1; }
        internal void Drain() { if (QueueCount == 0) return; QueueCount = 0; Sync.Reconcile(); Evidence.Refresh(); StateChanged?.Invoke(this, EventArgs.Empty); }
        internal void NotifyEditorTextChanged() { Sync.Flush(); Evidence.Refresh(); }
        internal bool PrepareSubmission() { var clean = Sync.Prepare(); Evidence.Refresh(); return clean; }
        internal bool PrepareClientTextApplication() => PrepareSubmission();
        internal void BeginDraftSlotTransition(){}
        internal void EndDraftSlotTransition()=>RequestRender();
        internal bool TryActivateDraftSlot(OpenCodeComposerToken token,int start,int length)
        {if(!Sync.TryActivateClean(token))return false;editor!.Select(Math.Clamp(start,0,editor.Text.Length),Math.Clamp(length,0,editor.Text.Length-Math.Clamp(start,0,editor.Text.Length)));Lifetime=begin!();Evidence.Refresh();return true;}
        internal OpenCodeComposerEditorRecovery SuspendDraftSlotForRecovery(string reason){var r=Sync.SuspendForRecovery();Evidence.Refresh();return r;}
        internal void ResolveDraftSlotRecovery(){Sync.ResolveRecovery();Evidence.Refresh();}
        internal bool TryPresentDraftSlotRecovery(string text){var ok=Sync.TryPresentRecovery(text);Evidence.Refresh();return ok;}
        internal bool PrepareDraftSlotPageUnload()=>Sync.Prepare(); // This harness's native view is live/inert; actual live/unloaded View method is linked in opencode-draft-slots.
        internal void ShowUnavailableDraftSlot(string reason){if(Sync.State==OpenCodeComposerInputState.Clean&&!Sync.IsSuspendedForRecovery)Sync.WriteSnapshot(source());Evidence.Refresh();}
        internal void RevokeSlotGestures(){LifetimeGeneration++;end!(Lifetime);Evidence.LifetimeChanged();}
        internal Task HandoffAcceptedDraftAsync(OpenCodeComposerToken submittedToken,
            Func<OpenCodeComposerDraftFinalization> finalize, CancellationToken lifetimeToken)
        {
            HandoffCalls++; HandoffToken = submittedToken; CapturedFinalizer = finalize;
            var task = handoffs.Begin(submittedToken, finalize, lifetimeToken);
            HandoffEntered.TrySetResult(true);
            return task;
        }
        internal void ReportPickOutcome(OpenCodeComposerPickOutcome outcome) { if (!composing()) FocusAttempts++; }
        internal OpenCodeComposerKeyDecision HandleEditorKey(KeyRoutedEventArgs e, bool shift, bool ime, TimeSpan sinceIme, bool latched, bool busy)
        {
            var s = source(); var key = e.Key == VirtualKey.Enter ? OpenCodeComposerKey.Enter : OpenCodeComposerKey.Other;
            var result = OpenCodeComposerInputPolicy.DecideKey(new(key, shift, false, ime, sinceIme, latched, s.CatalogState == OpenCodeComposerCatalogState.Ready, s.Commands.Count > 0, busy || s.IsBusy));
            if (result.Handled) e.Handled = true;
            if (result.Action == OpenCodeComposerKeyAction.MenuAccept && s.CatalogHandle is { } handle)
                Choose(s.Token, handle, s.Commands.First(c => c.Name == "review").Identity);
            return result;
        }
    }
}
