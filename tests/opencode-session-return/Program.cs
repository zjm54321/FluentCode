using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static partial class Program
{
    private static int _assertions;
    private static async Task Main()
    {
        SubagentCompletionNotices();
        PtyCompletionNotices();
        GroupFinalizesWhenFollowed();
        TranscriptMessageMenus();
        await ImageBindings();
        await ChildPreviewLifecycle();
        await CardReturnAndFailures();
        await NotificationParentAndFences();
        await ReturnRacesGateAndAcknowledgements();
        BlankTabRebuild();
        Console.WriteLine($"PASS: session-return + child-chat lifecycle, {_assertions} assertions; real linked page glue/navigation/client/ancestry/gate/ACK helper; fake HTTP/inert UI.");
        Console.WriteLine("BOUNDARIES: native child popup, parent message-load body, agent/recovery widgets and App ACK transport are stubs; real XAML layout and App POST reconciliation are not tested.");
    }
    // L2: a list rebuild never auto-selects a session while the blank "新会话" tab is shown; otherwise unchanged.
    private static void BlankTabRebuild()
    {
        using (var f = new Fixture("endpoint_blank_" + Guid.NewGuid().ToString("N")))
        {
            f.Page.BlankTabActive = true;
            Check(f.Page.Rebuild(new() { Session("ses_root"), Session("ses_other") }) is null
                && f.Page.Catalog.Count == 2 && f.Page.Viewed is null, "blank.rebuild-no-auto-select");
        }
        using (var f = new Fixture("endpoint_blank_" + Guid.NewGuid().ToString("N")))
        {
            Check(f.Page.Rebuild(new() { Session("ses_root"), Session("ses_other") }) == "ses_root"
                && f.Page.Catalog.ContainsKey("ses_root"), "blank.inactive-rebuild-still-auto-selects-first");
            Check(f.Page.NewSessionTabRequests == 0, "blank.sessions-present-no-blank-tab-request");
        }
        // A6: zero root sessions → Home, never an automatically manufactured blank tab.
        using (var f = new Fixture("endpoint_blank_" + Guid.NewGuid().ToString("N")))
        {
            Check(f.Page.Rebuild(new()) is null && f.Page.Viewed is null && f.Page.NewSessionTabRequests == 0 && f.Page.HomeTabActive, "home.zero-sessions-no-invented-blank-tab");
            Check(f.Page.Rebuild(new() { Session("ses_child", "ses_parent") }) is null && f.Page.NewSessionTabRequests == 0,
                "home.child-only-page-does-not-create-blank-tab");
        }
        using (var f = new Fixture("endpoint_blank_" + Guid.NewGuid().ToString("N")))
        {
            f.Page.BlankTabActive = true;
            Check(f.Page.Rebuild(new()) is null && f.Page.NewSessionTabRequests == 0, "blank.already-shown-no-duplicate-request");
        }
        using (var f = new Fixture("endpoint_blank_" + Guid.NewGuid().ToString("N")))
        {
            f.Page.HomeTabActive = true;
            Check(f.Page.Rebuild(new()) is null && f.Page.NewSessionTabRequests == 0 && f.Page.HomeTabActive, "home.closed-blank-not-reopened-by-refresh");
        }
    }
    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static OpenCodeSessionInfo Session(string id, string? parent = null) => new() { Id = id, ParentId = parent, Title = "PUBLIC_TITLE" };
    private static NotificationNavigationTarget Target(Fixture f, string id, NotificationNavigationKind kind = NotificationNavigationKind.Permission)
        => new(NotificationSource.OpenCode, f.Endpoint, id, "req_test", kind);
    private static readonly NotificationInteractionResult Accepted = new(NotificationInteractionStatus.Succeeded, "fixture");

    private static OpenCodeToolPartView RenderTool(OpenCodePage page, string messageId)
    {
        ConversationViewTestProbe.Reset();
        page.Conversation.ApplyMessages(new[]
        {
            new OpenCodeMessage
            {
                Id = messageId, Type = OpenCodeMessageTypes.Assistant,
                Content = new()
                {
                    new OpenCodeContentPart { Id = "part_body", Type = OpenCodePartTypes.Text, Text = "PUBLIC_BODY" },
                    new OpenCodeContentPart { Id = "part_tool", Type = OpenCodePartTypes.Tool, Name = "subagent" },
                },
            },
        });
        ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct")
            .Select(r => r.Subject).OfType<OpenCodeToolGroupView>().Single().OpenLeaf!();
        var tool = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct")
            .Select(r => r.Subject).OfType<OpenCodeToolPartView>().Single();
        tool.Element.IsExpanded = true;
        return tool;
    }

    private static async Task EnterChild(Fixture f, bool offPage = false)
    {
        f.Page.Rebuild(new() { Session(offPage ? "ses_other" : "ses_root") });
        await f.Page.Switch("ses_root");
        var parentTool = RenderTool(f.Page, "msg_parent");
        await f.Page.Card("msg_parent", "ses_child");
        Check(f.Page.Viewed == "ses_child" && f.Page.ReturnTarget == "ses_root", "card.validated-origin-not-direct-parent");
        Check(f.Page.CanReturn, "card.verified-origin-navigation-ready");
        Check(parentTool.DisposeCount == 1 && f.Page.Conversation.Children.Count == 0, "card.real-switch-clears-real-conversation");
    }

    private static async Task CardReturnAndFailures()
    {
        foreach (var offPage in new[] { false, true })
        {
            using var f = new Fixture("endpoint-card-" + (offPage ? "off" : "on"));
            await EnterChild(f, offPage);
            Check(f.Http.Paths.SequenceEqual(new[] { "/api/session/ses_child", "/api/session/ses_mid" }),
                "card.ancestry-only-no-extra-sidebar-get");
            Check(!f.Page.Catalog.ContainsKey("ses_child"), "card.child-not-in-sidebar");
            var content = RenderTool(f.Page, "msg_child");
            var element = f.Page.Conversation.Children[0];
            var context = f.Page.ContextIdentity;
            var version = f.Page.SessionGeneration;
            Check(f.Page.Rebuild(new() { Session("ses_other") }) is null && f.Page.Viewed == "ses_child", "card.refresh-preserves-child");
            Check(ReferenceEquals(context, f.Page.ContextIdentity), "card.refresh-preserves-return-context");
            Check(ReferenceEquals(element, f.Page.Conversation.Children[0]) && content.Element.IsExpanded && content.DisposeCount == 0,
                "card.refresh-keeps-content-and-expansion");
            await f.Page.Switch("ses_child");
            Check(ReferenceEquals(context, f.Page.ContextIdentity) && f.Page.SessionGeneration == version, "card.noop-switch-keeps-context");
            var count = f.Http.Paths.Count;
            await f.Page.Return();
            Check(f.Http.Paths.Skip(count).SequenceEqual(new[] { "/api/session/ses_root" }), "return.exactly-one-origin-get");
            Check(f.Page.Viewed == "ses_root" && !f.Page.Catalog.ContainsKey("ses_root"), "return.offpage-root-allowed");
            Check(content.DisposeCount == 1 && f.Page.Conversation.Children.Count == 0, "return.real-clear-after-success");
            Check(!f.Page.CanReturn, "return.root-clears-return-context");
        }

        foreach (var failure in new[] { "404", "403", "timeout" })
        {
            using var f = new Fixture("endpoint-fail-" + failure);
            await EnterChild(f);
            var tool = RenderTool(f.Page, "msg_child");
            var element = f.Page.Conversation.Children[0];
            var context = f.Page.ContextIdentity;
            var before = f.Http.Paths.Count;
            var hold = f.Http.Hold("ses_root");
            // The right-side button is gone; exercise the shared navigation used by the left tree.
            var navigation = f.Page.Return();
            await hold.Started.Task;
            Check(!f.Page.CanReturn && ReferenceEquals(context, f.Page.AttemptIdentity), "return.busy-context-retained");
            await f.Page.Return();
            Check(f.Http.Paths.Count == before + 1, "return.busy-prevents-second-get");
            if (failure == "timeout") hold.FailTimeout();
            else hold.ReleaseStatus((HttpStatusCode)int.Parse(failure));
            await navigation;
            Check(f.Page.Viewed == "ses_child" && ReferenceEquals(context, f.Page.ContextIdentity), "return.failure-retains-child-context");
            Check(ReferenceEquals(element, f.Page.Conversation.Children[0]) && tool.Element.IsExpanded && tool.DisposeCount == 0,
                "return.failure-retains-content-expansion");
            Check(f.Page.CanReturn && f.Page.AttemptIdentity is null && f.Page.MessageCount == 1, "return.failure-settles-attempt-and-warns");
            Check(f.Http.Paths.Count == before + 1, "return.failure-no-retry");
        }
    }

    private static async Task NotificationParentAndFences()
    {
        using (var f = new Fixture("endpoint-notify"))
        {
            await f.Page.Switch("ses_unrelated");
            await f.Page.Notify(Target(f, "ses_child"));
            Check(f.Page.Viewed == "ses_child" && f.Page.ReturnTarget == "ses_mid" && f.Page.CanReturn,
                "notify.parent-from-fenced-info-not-old-viewed");
            Check(f.Http.Paths.SequenceEqual(new[] { "/api/session" }), "notify.no-extra-parent-proof-get");
            await f.Page.Return();
            Check(f.Page.Viewed == "ses_mid" && f.Page.ReturnTarget == "ses_root", "notify.return-follows-current-get-parent");
            Check(f.Http.Paths.Last() == "/api/session/ses_mid", "notify.exact-parent-get");
        }
        foreach (var parent in new string?[] { null, "", "bad/parent", "ses_child" })
        {
            using var f = new Fixture("endpoint-invalid-parent");
            f.Http.Sessions["ses_child"] = Session("ses_child", parent);
            await f.Page.Switch("ses_unrelated");
            await f.Page.Notify(Target(f, "ses_child"));
            Check(f.Page.Viewed == "ses_child" && !f.Page.CanReturn,
                "notify.invalid-parent-no-return-entry");
        }

        // Same-ID forced navigation isolates captured-generation freshness; different ID also checks target routing.
        foreach (var sameId in new[] { false, true })
        foreach (var kind in new[] { NotificationNavigationKind.Permission, NotificationNavigationKind.Completion })
        {
            using var f = new Fixture("endpoint-notify-race");
            await f.Page.Switch("ses_unrelated");
            var heldLoad = Signal<bool>();
            f.Page.LoadEntered = Signal<string>();
            f.Page.MessageLoads.Enqueue(heldLoad.Task);
            var stale = f.Page.Notify(Target(f, "ses_child", kind), Accepted);
            Check(await f.Page.LoadEntered.Task == "ses_child", "notify.blocked-in-message-load");
            var oldGeneration = f.Page.SessionGeneration;
            var oldContext = f.Page.ContextIdentity;
            var nextId = sameId ? "ses_child" : "ses_second";
            await f.Page.Notify(Target(f, nextId, kind), Accepted);
            var newest = f.Page.ContextIdentity;
            Check(f.Page.SessionGeneration > oldGeneration && !ReferenceEquals(newest, oldContext), "notify.new-force-navigation-new-generation");
            var focus = f.Page.FocusCount;
            var scrolls = f.Page.BottomScrolls;
            var results = f.Page.ResultSuppressions;
            var handled = f.Page.NavigationStatuses.Count;
            Check(results == 1 && (kind == NotificationNavigationKind.Completion ? scrolls == 1 : focus == 1),
                "notify.new-navigation-positive-control");
            heldLoad.SetResult(true);
            await stale;
            Check(f.Page.Viewed == nextId && ReferenceEquals(newest, f.Page.ContextIdentity), "notify.stale-keeps-new-context");
            Check(f.Page.FocusCount == focus && f.Page.BottomScrolls == scrolls && f.Page.ResultSuppressions == results
                && f.Page.NavigationStatuses.Count == handled, "notify.stale-cannot-focus-scroll-or-apply-result");
        }
    }

    private static async Task ReturnRacesGateAndAcknowledgements()
    {
        foreach (var transition in new[] { "aba", "revision", "unload" })
        {
            using var f = new Fixture("endpoint-race-" + transition);
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Endpoint, "ses_root"))
            {
                lease.BeginMutation("req_unknown", NotificationNavigationKind.Permission, affectsSession: true);
                lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            }
            await f.Page.Switch("ses_root");
            var idle = new[] { new OpenCodeMessage { Id = "msg_idle", Type = OpenCodeMessageTypes.Idle,
                Outcome = OpenCodeSessionOutcomes.Succeeded, Time = new OpenCodeMessageTime { Created = 42 } } };
            f.Page.Conversation.ApplyMessages(idle);
            f.Page.NotifyViewAcknowledgementRendered(idle, windowVisible: true);
            f.Page.ProveViewport();
            Check(f.App.Acks.Count == 1 && f.App.Acks[0].Fence(), "ack.real-helper-initial-attempt");
            await EnterChild(f);
            Check(!f.App.Acks[0].Fence(), "ack.switch-invalidates-live-fence");
            var oldContext = f.Page.ContextIdentity;
            var oldHold = f.Http.Hold("ses_root");
            var oldReturn = f.Page.Return();
            await oldHold.Started.Task;
            await f.Page.Return();
            Check(ReferenceEquals(f.Page.AttemptIdentity, oldContext) && !f.Page.CanReturn, "race.old-attempt-blocks-reentry");

            if (transition == "aba")
                await f.Page.Switch("ses_other");
            else if (transition == "revision")
                f.Page.ChangeSource(f.NewSource(2));
            else
            {
                f.Page.Unload();
                // NAV2: the cached page keeps its return context across main navigation; pausing
                // still cancels the in-flight return so it can never complete after reload.
                Check(oldHold.Token.IsCancellationRequested, "race.unload-cancels-in-flight-return");
                Check(ReferenceEquals(oldContext, f.Page.ContextIdentity) && !f.Page.CanReturn,
                    "race.pause-retains-context-without-navigation-capability");
                var pausedPaths = f.Http.Paths.Count;
                await f.Page.Return();
                Check(f.Http.Paths.Count == pausedPaths, "race.paused-return-cannot-start-read");
                var hiddenIdle = new[] { new OpenCodeMessage { Id = "msg_hidden_idle", Type = OpenCodeMessageTypes.Idle,
                    Time = new OpenCodeMessageTime { Created = 43 } } };
                f.Page.Conversation.ApplyMessages(hiddenIdle);
                f.Page.NotifyViewAcknowledgementRendered(hiddenIdle, windowVisible: true);
                f.Page.ProveViewport();
                Check(f.App.Acks.Count == 1 && !f.App.Acks[0].Fence(), "ack.paused-page-cannot-ack-new-watermark");
                f.Page.BindFixture(f.NewSource(1));
            }
            await EnterChild(f);
            var current = f.Page.ContextIdentity;
            Check(!ReferenceEquals(current, oldContext) && f.Page.Viewed == "ses_child", "race.new-child-context-installed");
            Check(oldHold.Token.IsCancellationRequested, "race.old-operation-token-cancelled");
            var newHold = f.Http.Hold("ses_root");
            var newReturn = f.Page.Return();
            await newHold.Started.Task;
            var tracked = f.Page.Tracked.Count;
            oldHold.Release(Session("ses_root")); // Ignoring cancellation at fake transport stresses late completion.
            await oldReturn;
            Check(f.Page.Viewed == "ses_child" && ReferenceEquals(current, f.Page.ContextIdentity), "race.old-result-cannot-switch-or-install");
            Check(ReferenceEquals(current, f.Page.AttemptIdentity) && !f.Page.CanReturn && !newReturn.IsCompleted,
                "race.old-finally-cannot-clear-new-attempt");
            Check(f.Page.Tracked.Count == tracked && f.Page.MessageCount == 0, "race.old-result-no-tracking-or-error");
            newHold.Release(Session("ses_root"));
            await newReturn;
            Check(f.Page.Viewed == "ses_root" && f.Page.AttemptIdentity is null, "race.new-return-completes");
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Endpoint, "ses_root"))
            {
                Check(lease.GetSessionProtection() is { RequestId: "req_unknown" }
                    && lease.GetProtectionStatus("req_unknown", NotificationNavigationKind.Permission) == NotificationInteractionStatus.OutcomeUnknown
                    && !lease.CanSwitchAgent(), "gate.actual-unknown-survives-return-and-lifecycle");
            }
            f.Page.Conversation.ApplyMessages(idle);
            f.Page.NotifyViewAcknowledgementRendered(idle, windowVisible: true);
            f.Page.ProveViewport();
            Check(f.App.Acks.Count == 1, "ack.same-watermark-not-retried-after-return-revision-unload");
            var nextIdle = new[] { new OpenCodeMessage { Id = "msg_next_idle", Type = OpenCodeMessageTypes.Idle,
                Time = new OpenCodeMessageTime { Created = 43 } } };
            f.Page.Conversation.ApplyMessages(nextIdle);
            f.Page.NotifyViewAcknowledgementRendered(nextIdle, windowVisible: true);
            f.Page.ProveViewport();
            Check(f.App.Acks.Count == 2 && f.App.Acks[1].Watermark == 43, "ack.new-watermark-still-eligible");
        }
    }

    private static async Task ChildPreviewLifecycle()
    {
        using (var f = new Fixture("endpoint-child-preview"))
        {
            await f.Page.Switch("ses_root");
            RenderTool(f.Page, "msg_parent");
            var pending = f.Page.Preview("msg_parent", "ses_child");
            var window = OpenCodeChildChatWindow.Latest!;
            await window.ContentReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Page.Viewed == "ses_root" && window.Messages?.Data?.Single().Text == "CHILD_CONVERSATION_BODY",
                "preview.real-client-child-content-without-parent-navigation");
            Check(f.Http.Paths.SequenceEqual(new[] { "/api/session/ses_child", "/api/session/ses_mid", "/api/session/ses_child/message" }),
                "preview.ancestry-before-child-message-endpoint");
            window.Close(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Page.Viewed == "ses_root", "preview.close-preserves-parent-selection");

            pending = f.Page.Preview("msg_parent", "ses_child");
            window = OpenCodeChildChatWindow.Latest!;
            await window.ContentReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = f.Http.Paths.Count;
            await window.Maximize();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Page.Viewed == "ses_child" && f.Page.ReturnTarget == "ses_root" && !window.IsOpen,
                "preview.maximize-closes-and-enters-full-child-with-origin");
            Check(f.Http.Paths.Skip(before).SequenceEqual(new[] { "/api/session/ses_child", "/api/session/ses_mid" }),
                "preview.maximize-revalidates-server-ancestry");
        }
        using (var f = new Fixture("endpoint-unrelated-preview"))
        {
            await f.Page.Switch("ses_root"); RenderTool(f.Page, "msg_parent");
            var pending = f.Page.Preview("msg_parent", "ses_second");
            var window = OpenCodeChildChatWindow.Latest!;
            await window.ContentReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(window.Messages is null && window.Error is not null && !f.Http.Paths.Any(p => p.EndsWith("/message", StringComparison.Ordinal)),
                "preview.unrelated-child-fails-closed-without-message-fetch");
            window.Close(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var f = new Fixture("endpoint-preview-missing-child"))
        {
            await f.Page.Switch("ses_root"); RenderTool(f.Page, "msg_parent");
            var pending = f.Page.Preview("msg_parent", "");
            var window = OpenCodeChildChatWindow.Latest!;
            await window.ContentReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(window.Error is not null && window.Messages is null && f.Http.Paths.Count == 0,
                "preview.pending-child-shows-truthful-empty-state-without-any-request");
            window.Close(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using (var f = new Fixture("endpoint-preview-source-revoke"))
        {
            await f.Page.Switch("ses_root"); RenderTool(f.Page, "msg_parent");
            var held = f.Http.Hold("ses_child");
            var pending = f.Page.Preview("msg_parent", "ses_child");
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var window = OpenCodeChildChatWindow.Latest!;
            f.Page.ChangeSource(f.NewSource(2));
            Check(held.Token.IsCancellationRequested && window.Token.IsCancellationRequested,
                "preview.source-change-cancels-ancestry-and-window-lifetime");
            held.Release(Session("ses_child", "ses_root"));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(window.Messages is null && !window.IsOpen && !f.Http.Paths.Any(p => p.EndsWith("/message", StringComparison.Ordinal)),
                "preview.late-old-source-response-cannot-publish-or-fetch");
        }
    }

    private static async Task ImageBindings()
    {
        using var f = new Fixture("endpoint-images");
        await f.Page.Switch("ses_root");
        var main = f.Page.Images!;
        Check(main.IsCurrent && main.Identity.SessionId == "ses_root" && f.Http.Paths.Count == 0,
            "images.main-bound-before-first-render-without-eager-fetch");
        f.Page.RebindImages(); f.Page.ReplaceHistoryToken(); f.Page.RebindImages();
        Check(ReferenceEquals(main, f.Page.Images) && main.IsCurrent, "images.same-session-keeps-scope-across-history-operation-token");
        await main.ReadAsync("main.png", CancellationToken.None);
        Check(f.Http.ImageQueries.Single().Contains("%2Ffixture%2Fses_root", StringComparison.OrdinalIgnoreCase),
            "images.main-real-factory-fetches-exact-session-directory");

        RenderTool(f.Page, "msg_parent");
        var held = f.Http.Hold("ses_child");
        var preview = f.Page.Preview("msg_parent", "ses_child");
        await held.Started.Task;
        var window = OpenCodeChildChatWindow.Latest!;
        var child = window.Images!;
        Check(!child.IsCurrent && child.Identity.SessionId == "ses_child", "images.child-captures-child-id-but-not-authorized-before-ancestry");
        held.Release(f.Http.Sessions["ses_child"]);
        await window.ContentReady.Task;
        Check(child.IsCurrent, "images.existing-ancestry-success-authorizes-child-before-publication");
        await child.ReadAsync("child.png", CancellationToken.None);
        Check(f.Http.ImageQueries.Last().Contains("%2Ffixture%2Fses_child", StringComparison.OrdinalIgnoreCase),
            "images.child-real-factory-fetches-own-directory-never-parent");
        window.Close(); await preview;
        Check(!child.IsCurrent && main.IsCurrent, "images.child-close-revokes-only-child-scope");
        await f.Page.Switch("ses_other");
        Check(!main.IsCurrent && f.Page.Images is { IsCurrent: true } replacement && replacement.Identity.SessionId == "ses_other",
            "images.actual-session-switch-disposes-and-rebinds-main");
        var current = f.Page.Images!;
        f.Page.Unload();
        Check(!current.IsCurrent && f.Page.Images is null, "images.unload-disposes-and-unbinds-main");
        f.Page.BindFixture(f.NewSource(1));
        await f.Page.Switch("ses_other");
        Check(f.Page.Images is { IsCurrent: true } resumed && !ReferenceEquals(current, resumed)
            && resumed.Identity.SessionId == "ses_other" && !current.IsCurrent,
            "images.reload-rebinds-current-scope-without-reviving-paused-lease");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Endpoint { get; }
        internal FakeHttp Http { get; } = new();
        private readonly HttpClient _http;
        internal App App { get; } = new();
        internal OpenCodePage Page { get; }
        internal Fixture(string endpoint)
        {
            Endpoint = endpoint;
            _http = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
            Http.Sessions["ses_root"] = Session("ses_root") with { Location = new() { Directory = "/fixture/ses_root" } };
            Http.Sessions["ses_mid"] = Session("ses_mid", "ses_root");
            Http.Sessions["ses_child"] = Session("ses_child", "ses_mid") with { Location = new() { Directory = "/fixture/ses_child" } };
            Http.Sessions["ses_second"] = Session("ses_second", "ses_other");
            Http.Sessions["ses_other"] = Session("ses_other");
            Application.Current = App;
            Page = new OpenCodePage(NewSource(1));
        }
        internal OpenCodePageSource NewSource(long revision) => new(Endpoint, revision, () =>
            new OpenCodeClient(_http, new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "fixture-token" }, true));
        public void Dispose() { Page.Unload(); _http.Dispose(); Application.Current = null; }
    }

    private sealed class HeldResponse
    {
        internal readonly TaskCompletionSource<bool> Started = Signal<bool>();
        internal readonly TaskCompletionSource<HttpResponseMessage> Response = Signal<HttpResponseMessage>();
        internal CancellationToken Token;
        internal void Release(OpenCodeSessionInfo session) => Response.SetResult(Json(new { data = session }));
        internal void ReleaseStatus(HttpStatusCode status) => Response.SetResult(new(status) { Content = new StringContent("{}") });
        internal void FailTimeout() => Response.SetException(new TaskCanceledException("fixture timeout"));
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class FakeHttp : HttpMessageHandler
    {
        internal readonly Dictionary<string, OpenCodeSessionInfo> Sessions = new(StringComparer.Ordinal);
        internal readonly List<string> Paths = new();
        internal readonly List<string> ImageQueries = new();
        private readonly Dictionary<string, Queue<HeldResponse>> _held = new(StringComparer.Ordinal);
        internal HeldResponse Hold(string id)
        {
            var held = new HeldResponse();
            if (!_held.TryGetValue(id, out var queue)) _held[id] = queue = new();
            queue.Enqueue(held);
            return held;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("http.unexpected-non-get");
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.StartsWith("/api/fs/read/", StringComparison.Ordinal))
            {
                ImageQueries.Add(request.RequestUri.Query);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) });
            }
            if (path == "/api/session") return Task.FromResult(Json(new { data = Sessions.Values.ToArray() }));
            var id = path["/api/session/".Length..];
            if (_held.TryGetValue(id, out var queue) && queue.Count > 0)
            {
                var held = queue.Dequeue();
                held.Token = token;
                held.Started.TrySetResult(true);
                return held.Response.Task;
            }
            if (id == "ses_child/message") return Task.FromResult(Json(new { data = new[]
            {
                new OpenCodeMessage { Id = "msg_child_body", Type = OpenCodeMessageTypes.User, Text = "CHILD_CONVERSATION_BODY" },
            } }));
            if (Sessions.TryGetValue(id, out var session)) return Task.FromResult(Json(new { data = session }));
            throw new InvalidOperationException("http.unexpected-session");
        }
    }
}
