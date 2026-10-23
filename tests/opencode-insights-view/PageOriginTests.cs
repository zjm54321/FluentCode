// Source-linked Billion-origin UI/Page integration cases. REAL: OpenCodeSessionInsightsView.cs, OpenCodeSessionInsights.cs
// (controller, default origin->BillionContextClient factory), BillionContextClient.cs, OpenCodePageSource.cs,
// OpenCodePage.SessionInsights.cs and extracted OpenCodePage.xaml.cs members (SourceReady, CreateOperationToken,
// InvalidateForSourceChange). SYNTHETIC: WinUI shim (incl. synchronous TextChanging, focus, manual DispatcherQueue),
// PageHost.cs (fields, modelled session-switch/unload/install orderings, service no-ops), an offline OpenCode HTTP
// handler (404 for every GET except the discovery fixture's Session.Info) and a single-thread SynchronizationContext
// pump. Discovery POST replies are held/supplied in memory; no live RPC. Typing/clicks are simulated calls,
// not native input. No Billion HTTP is ever started: no case reads with a confirmed, current context, and
// BillionReadsStarted plus the OpenCode handler's paths are asserted. Raw candidate text is asserted, never printed.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using UiQueue = Microsoft.UI.Dispatching.DispatcherQueue;

internal static class PageOriginTests
{
    public static int Assertions { get; private set; }

    public static IEnumerable<(string Name, Action Body)> Cases() => new (string, Action)[]
    {
        ("origin A: immediate consent invalidation, raw draft kept, zero implicit Billion HTTP", ImmediateInvalidation),
        ("origin B: captured context, session retain, no-session source ABA + unload clear, stale callbacks", ContextAndLifecycle),
        ("origin C: persistent ownership, queued render outside TextChanging, focus/caret preserved", OwnershipAndFocus),
        ("origin D: clear manual override, reject delayed discovery, explicit refresh autofills without consent/read", ClearOverrideAndRediscover),
        ("agents E: child navigation only for a child in the current snapshot; otherwise no read, no switch", ChildNavigationGate),
    };

    // ------------------------------------------------------------------ A

    private static void ImmediateInvalidation()
    {
        var f = Fixture();
        var (insights, box, confirm, read) = (f.Page.Insights, Box(f), Confirm(f), Read(f));
        Check(insights.Snapshot.BillionContext is not null && insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing,
            "flyout load adopted the binding: context present, origin missing (no default)");
        Check(box.IsEnabled && box.Text.Length == 0 && !confirm.IsEnabled && !read.IsEnabled, "empty enabled box; confirm/read disabled");

        const string target = "http://127.0.0.1:1";
        var generation = insights.Snapshot.BillionContext!.OriginGeneration;
        for (var i = 1; i <= target.Length; i++)
        {
            box.SimulateTyping(target[..i]);
            Check(box.Text == target[..i], "partial input kept verbatim (no snapping)");
            Check(insights.Snapshot.BillionContext!.OriginGeneration == generation + i, "every keystroke accepted immediately with the newly acknowledged context");
            Check(UiQueue.Shared.Pending <= 1, "visual updates coalesced into one queued callback");
        }

        Check(ShimTree.ForbiddenMutations == 0, "no tree mutation inside TextChanging");
        Check(insights.Snapshot.BillionOrigin == target && insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Unconfirmed,
            "core holds canonical origin, unconfirmed");
        Check(UiQueue.Shared.Drain() == 1, "single deferred render ran");
        Check(Status(f).StartsWith("当前生效地址：" + target + "（未确认关联）", StringComparison.Ordinal), "status shows the effective origin and state");
        Check(confirm.IsEnabled && !read.IsEnabled, "valid origin enables confirmation only");

        // Clicking confirm while a visual update is still queued is stale: rejected and reverted.
        box.SimulateTyping(target + "0");
        box.SimulateTyping(target);
        confirm.IsChecked = true;
        Check(!insights.Snapshot.BillionConfirmed && confirm.IsChecked == false, "confirm against stale display rejected, checkbox reverted");
        UiQueue.Shared.Drain();

        confirm.IsChecked = true;
        Check(insights.Snapshot.BillionConfirmed && insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Confirmed,
            "explicit user confirmation reaches core");
        Check(read.IsEnabled, "read enabled only after confirmation");

        box.SimulateTyping(target + "0");
        Check(!insights.Snapshot.BillionConfirmed && insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Unconfirmed
            && insights.Snapshot.BillionOrigin == target + "0", "next keystroke revokes consent immediately (before any render)");
        Check(confirm.IsChecked == true, "visual still pending inside the input turn");
        UiQueue.Shared.Drain();
        Check(confirm.IsChecked == false && !read.IsEnabled, "deferred render shows revoked consent");

        const string secret = "s3cr3tpw";
        box.SimulateTyping("http://user:" + secret + "@127.0.0.1:1");
        Check(insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Invalid && insights.Snapshot.BillionOrigin is null,
            "credential-bearing input is invalid; core keeps no candidate");
        UiQueue.Shared.Drain();
        Check(box.Text.Contains(secret, StringComparison.Ordinal), "raw draft stays only in its own box");
        Check(!AllText(f.Page.View).Any(t => t.Contains(secret, StringComparison.Ordinal)), "no label/automation text echoes the raw draft");
        Check(!confirm.IsEnabled, "invalid origin cannot be confirmed");

        read.SimulateClick();
        f.Pump.RunUntil(() => true);
        Check(insights.Snapshot.Billion.Phase == OpenCodeBillionPhase.InvalidOrigin, "explicit read with invalid origin reports state, sends nothing");

        var before = insights.Snapshot.BillionContext!.OriginGeneration;
        f.Page.OpenFlyout();
        f.Pump.RunUntil(() => !insights.Snapshot.Loading);
        f.Page.View.Render(insights.Snapshot);
        Check(insights.Snapshot.BillionContext!.OriginGeneration == before, "programmatic renders/open raise no edit");
        Check(insights.BillionReadsStarted == 0 && f.Http.Paths.All(path => !path.Contains("__bili", StringComparison.Ordinal)),
            "zero Billion requests; OpenCode handler saw no Billion route");
    }

    // ------------------------------------------------------------------ B

    private static void ContextAndLifecycle()
    {
        var f = Fixture();
        var (insights, box, confirm) = (f.Page.Insights, Box(f), Confirm(f));
        const string origin = "http://127.0.0.1:1";
        box.SimulateTyping(origin);
        UiQueue.Shared.Drain();
        confirm.IsChecked = true;
        var stale = insights.Snapshot.BillionContext!;
        Check(insights.Snapshot.BillionConfirmed, "setup confirmed");

        // Same source, other session: choice + consent retained, old result/context not.
        f.Page.SelectSession("ses_b");
        // Redesign §10: the right pane stays open across session switches; the reset still drops results and context.
        Check(ReferenceEquals(f.Page.PaneHost.Child, f.Page.View) && insights.Snapshot.BillionContext is null,
            "session reset keeps the single pane host and drops context");
        OpenAndSettle(f);
        Check(insights.Snapshot.BillionOrigin == origin && insights.Snapshot.BillionConfirmed && box.Text == origin,
            "same-source session switch retains selection, consent and draft");

        // Stale rendered context replayed through the REAL Page subscribers is rejected.
        var gen = insights.Snapshot.BillionContext!.OriginGeneration;
        Check(!RaiseEdit(f, stale, "http://127.0.0.1:2"), "stale edit rejected at Page/core boundary");
        RaiseConfirmEvent(f, stale, false);
        f.Page.ReadWith(stale).GetAwaiter().GetResult();
        Check(insights.Snapshot.BillionConfirmed && insights.Snapshot.BillionContext!.OriginGeneration == gen
            && insights.Snapshot.Billion.Phase == OpenCodeBillionPhase.Idle, "stale confirm/read changed nothing");

        // A -> B (no selected session) -> A (same object/revision, deliberate ABA).
        var (sourceB, clientB) = NewSource("ep-b", 7);
        f.Page.InvalidateAndInstall(sourceB, clientB);
        Check(box.Text.Length == 0, "old source draft not shown under B");
        f.Page.OpenFlyout();
        Check(insights.Snapshot.UnavailableReason is not null && insights.Snapshot.BillionContext is null, "B without session: no context");
        f.Page.InvalidateAndInstall(f.Source, f.Client);
        f.Page.SelectSession("ses_a");
        OpenAndSettle(f);
        Check(insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing && insights.Snapshot.BillionOrigin is null
            && !insights.Snapshot.BillionConfirmed && box.Text.Length == 0, "A->B(no session)->A cleared origin, consent and draft");
        Check(!RaiseEdit(f, stale, origin), "pre-ABA context cannot edit");

        // Unload (source removed without generation increment) also clears.
        box.SimulateTyping(origin);
        UiQueue.Shared.Drain();
        confirm.IsChecked = true;
        Check(insights.Snapshot.BillionConfirmed, "re-confirmed before unload");
        f.Page.Unload();
        Check(box.Text.Length == 0 && Field<string?>(insights, "_origin") is null
            && Field<OpenCodePageSource?>(insights, "_originSource") is null,
            "unload (source removed, generation unchanged) clears origin and draft at the reset itself");
        f.Page.Activate();
        f.Page.InvalidateAndInstall(f.Source, f.Client);
        f.Page.SelectSession("ses_a");
        OpenAndSettle(f);
        Check(insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing && box.Text.Length == 0, "unload/reload cleared origin");

        // Stale queued callbacks: a synchronous reset supersedes the queued render; Unloaded cancels; Loaded flushes.
        box.SimulateTyping("http://localhost:3");
        Check(UiQueue.Shared.Pending == 1, "render queued");
        f.Page.SelectSession("ses_c");
        var afterReset = Field<OpenCodeSessionInsightsSnapshot>(f.Page.View, "_visual");
        UiQueue.Shared.Drain();
        Check(ReferenceEquals(Field<OpenCodeSessionInsightsSnapshot>(f.Page.View, "_visual"), afterReset)
            && ReferenceEquals(afterReset, insights.Snapshot), "superseded queued callback was a no-op");

        f.Page.SelectSession("ses_a");
        OpenAndSettle(f);
        box.SimulateTyping("http://localhost:4");
        f.Page.View.SimulateUnloaded();
        UiQueue.Shared.Drain();
        Check(Field<bool>(f.Page.View, "_visualPending"), "queued callback after Unloaded did nothing");
        f.Page.View.SimulateLoaded();
        Check(!Field<bool>(f.Page.View, "_visualPending") && Status(f).StartsWith("当前生效地址：http://localhost:4", StringComparison.Ordinal),
            "Loaded flushes the latest state once");

        UiQueue.Shared.Available = false;
        box.SimulateTyping("http://localhost:5");
        Check(UiQueue.Shared.Pending == 0 && Field<bool>(f.Page.View, "_visualPending"), "enqueue failure leaves render pending, no retry loop");
        box.SimulateLostFocus();
        Check(!Field<bool>(f.Page.View, "_visualPending"), "focus loss flushes the pending render");
        Check(insights.BillionReadsStarted == 0 && ShimTree.ForbiddenMutations == 0, "no Billion HTTP, no forbidden mutation");
    }

    // ------------------------------------------------------------------ C

    private static void OwnershipAndFocus()
    {
        var f = Fixture();
        var view = f.Page.View;
        var box = Box(f);
        var panel = Field<StackPanel>(view, "_billionOriginPanel");
        var body = Field<StackPanel>(view, "_billionPage");
        var section = body.Children[0];
        var content = (StackPanel)((StackPanel)section).Children[1];
        Check(ReferenceEquals(panel.Owner, content) && content.Children.IndexOf(panel) == 1, "origin panel hosted once in Billion content");

        box.Focus(FocusState.Keyboard);
        foreach (var text in new[] { "h", "ht", "htt", "http://[::1]:8" })
        {
            box.SimulateTyping(text);
        }

        Check(ShimTree.ForbiddenMutations == 0, "TextChanging never mutated the tree");
        UiQueue.Shared.Drain();
        Check(ReferenceEquals(body.Children[0], section) && ReferenceEquals(panel.Owner, content), "Billion-only update in place (no reparent)");
        Check(box.FocusState != FocusState.Unfocused && box.SelectionStart == "http://[::1]:8".Length, "focus and caret kept");
        Check(f.Page.Insights.Snapshot.BillionOrigin == "http://[::1]:8", "IPv6 loopback accepted canonically");

        // A non-Billion change while focused forces a full rebuild: same instances, single owner, focus/caret restored.
        box.SelectionStart = 3;
        box.SelectionLength = 2;
        _ = f.Page.Reload(); // layout D: automatic page read (no refresh button on the card)
        f.Pump.RunUntil(() => !f.Page.Insights.Snapshot.Loading);
        var newContent = (StackPanel)((StackPanel)body.Children[0]).Children[1];
        Check(!ReferenceEquals(newContent, content) && ReferenceEquals(panel.Owner, newContent)
            && ReferenceEquals(Field<TextBox>(view, "_billionOriginBox"), box), "full rebuild re-hosts the same persistent panel once");
        Check(box.FocusState != FocusState.Unfocused && box.SelectionStart == 3 && box.SelectionLength == 2, "focus/selection restored after rebuild");
        Check(box.Text == "http://[::1]:8", "draft text untouched by rebuild");
        Check(AutomationProperties.GetAutomationId(box) == "OpenCodeInsightsBillionOrigin", "AutomationId stable");

        // Unavailable render releases every persistent control before clearing.
        f.Page.SelectSession(null);
        f.Page.OpenFlyout();
        Check(Field<StackPanel[]>(view, "_pages").All(page => page.Children.Count == 0)
            && panel.Owner is null && Field<CheckBox>(view, "_billionConfirm").Owner is null
            && Field<Button>(view, "_billionButton").Owner is null, "unavailable: all Billion controls released");
        Check(ShimTree.ForbiddenMutations == 0 && f.Page.Insights.BillionReadsStarted == 0, "no forbidden mutation, no Billion HTTP");
    }

    // ------------------------------------------------------------------ D

    private static void ClearOverrideAndRediscover()
    {
        const string staleOrigin = "http://127.0.0.1:43127";
        const string freshOrigin = "http://127.0.0.1:43128";
        const string manualOrigin = "http://localhost:43999";
        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new OfflineOpenCode { Discovery = true, RpcReply = _ => delayed.Task }; // Deliberately ignores cancellation.
        var f = Fixture(http, settle: false);
        var insights = f.Page.Insights;
        var view = f.Page.View;
        var box = Box(f);
        f.Pump.RunUntil(() => http.DiscoveryRequests == 1);
        Check(insights.Snapshot.BillionDiscoveryLoading && insights.Snapshot.BillionOrigin is null,
            "initial real Page load holds one discovery reply without an origin");

        var edits = 0;
        view.BillionOriginEdited += (_, edit) => { Check(edit.Accepted, "real Page subscriber accepted the user edit"); edits++; };
        var body = Field<StackPanel>(view, "_billionPage");
        var panel = Field<StackPanel>(view, "_billionOriginPanel");
        var section = body.Children[0];
        var content = panel.Owner;
        box.Focus(FocusState.Keyboard);
        box.SimulateTyping(manualOrigin);
        Check(insights.Snapshot.BillionOrigin == manualOrigin && !insights.Snapshot.BillionDiscoveryLoading
            && http.RpcToken.IsCancellationRequested, "manual override cancels the held metadata attempt immediately");
        UiQueue.Shared.Drain();
        Check(ReferenceEquals(body.Children[0], section) && ReferenceEquals(panel.Owner, content),
            "discovery-loading to manual edit updates in place without reparent");
        Check(box.FocusState != FocusState.Unfocused && box.SelectionStart == manualOrigin.Length,
            "manual edit keeps focus and caret while discovery state changes");

        box.SimulateTyping(string.Empty);
        Check(insights.Snapshot.BillionOrigin is null && insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing,
            "empty edit clears the manual override immediately");
        UiQueue.Shared.Drain();
        Check(!Field<bool>(view, "_draftActive"), "accepted empty edit releases the draft latch");
        box.SimulateTyping(" \t ");
        UiQueue.Shared.Drain();
        Check(insights.Snapshot.BillionOrigin is null && box.Text.Length == 0 && !Field<bool>(view, "_draftActive"),
            "accepted whitespace also releases the draft latch and renders the missing origin");
        Check(http.DiscoveryRequests == 1 && edits == 3, "clearing does not retry discovery or emit programmatic origin edits");

        delayed.SetResult(OfflineOpenCode.Ready(staleOrigin));
        f.Pump.RunUntil(() => !insights.Snapshot.Loading);
        UiQueue.Shared.Drain();
        Check(insights.Snapshot.BillionOrigin is null && box.Text.Length == 0
            && insights.Snapshot.BillionDiscovery is null && !insights.Snapshot.BillionDiscoveryLoading,
            "old delayed discovery is rejected after the override was cleared");
        Check(http.DiscoveryRequests == 1 && edits == 3, "late completion starts no retry or origin edit");

        http.RpcReply = _ => Task.FromResult(OfflineOpenCode.Ready(freshOrigin));
        _ = f.Page.Reload(); // layout D: automatic page read (no refresh button on the card)
        f.Pump.RunUntil(() => !insights.Snapshot.Loading);
        UiQueue.Shared.Drain();
        Check(http.DiscoveryRequests == 2 && insights.BillionDiscoveryAttemptsStarted == 2
            && insights.Snapshot.BillionDiscovery?.Status == OpenCodeBillionDiscoveryStatus.Ready,
            "explicit Page refresh starts exactly one new discovery and accepts its ready result");
        Check(insights.Snapshot.BillionOrigin == freshOrigin && box.Text == freshOrigin
            && Status(f).StartsWith("当前生效地址：" + freshOrigin + "（未确认关联）", StringComparison.Ordinal),
            "newly discovered origin reaches the real View Text and status after clearing the draft");
        Check(!insights.Snapshot.BillionConfirmed && insights.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Unconfirmed
            && Confirm(f).IsChecked == false && Confirm(f).IsEnabled && !Read(f).IsEnabled,
            "discovery never supplies consent or enables an engine read");
        Check(edits == 3 && insights.BillionReadsStarted == 0 && insights.Snapshot.Billion.Health is null
            && insights.Snapshot.Billion.Stats is null && http.Paths.All(path => !path.Contains("__bili", StringComparison.Ordinal)),
            "programmatic autofill emits no origin edit and no engine read");
        Check(ShimTree.ForbiddenMutations == 0, "discovery/edit sequence never mutates the tree inside TextChanging");
    }

    // ------------------------------------------------------------------ E

    private static void ChildNavigationGate()
    {
        var f = Fixture();
        Check(ReferenceEquals(f.Page.PaneHost.Child, f.Page.View), "view hosted once in the pane host");
        // Offline fixture: OMO is not verified, so the snapshot lists no children for ses_a.
        f.Page.OpenChild("ses_not_listed").GetAwaiter().GetResult();
        f.Page.OpenChild("ses_other").GetAwaiter().GetResult(); // the view no longer raises child requests (left tree owns them)
        f.Pump.RunUntil(() => true);
        Check(f.Page.ReadsStarted == 0 && f.Page.Switches.Count == 0 && f.Page.SelectedItems.Count == 0 && f.Page.Messages.Count == 0,
            "unlisted child id: no ancestry read, no selection, no switch");
        f.Page.SelectSession(null);
        f.Page.OpenChild("ses_not_listed").GetAwaiter().GetResult();
        Check(f.Page.ReadsStarted == 0 && f.Page.Switches.Count == 0, "no session: nothing");
    }

    // ------------------------------------------------------------------ fixture

    private sealed record Fx(OpenCodePage Page, OpenCodePageSource Source, OpenCodeClient Client, OfflineOpenCode Http, PumpContext Pump);

    private static Fx Fixture(OfflineOpenCode? http = null, bool settle = true)
    {
        var pump = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(pump);
        http ??= new OfflineOpenCode();
        var (source, client) = NewSource("ep-a", 1, http);
        var page = new OpenCodePage();
        page.Activate();
        page.InvalidateAndInstall(source, client);
        page.SelectSession("ses_a");
        var f = new Fx(page, source, client, http, pump);
        if (settle) OpenAndSettle(f);
        else page.OpenFlyout();
        return f;
    }

    private static (OpenCodePageSource, OpenCodeClient) NewSource(string identity, long revision, OfflineOpenCode? http = null)
    {
        var client = new OpenCodeClient(new HttpClient(http ?? new OfflineOpenCode()), new OpenCodeClientOptions
        {
            BaseUrl = http?.Discovery == true ? "http://127.0.0.1:43100/prefix" : "https://offline.invalid/prefix",
            AuthenticationMode = OpenCodeAuthenticationMode.None,
        }, true);
        return (new OpenCodePageSource(identity, revision, () => client), client);
    }

    private static void OpenAndSettle(Fx f)
    {
        f.Page.OpenFlyout();
        f.Pump.RunUntil(() => !f.Page.Insights.Snapshot.Loading);
        UiQueue.Shared.Drain();
    }

    private static TextBox Box(Fx f) => Field<TextBox>(f.Page.View, "_billionOriginBox");
    private static CheckBox Confirm(Fx f) => Field<CheckBox>(f.Page.View, "_billionConfirm");
    private static Button Read(Fx f) => Field<Button>(f.Page.View, "_billionButton");
    private static string Status(Fx f) => Field<TextBlock>(f.Page.View, "_billionOriginStatus").Text;

    /// <summary>Invokes the REAL Page subscriber of the view event with an arbitrary (e.g. stale) context.</summary>
    private static bool RaiseEdit(Fx f, OpenCodeBillionContext context, string candidate)
    {
        var edit = new OpenCodeBillionOriginEdit(context, candidate);
        EventField<EventHandler<OpenCodeBillionOriginEdit>>(f.Page.View, "BillionOriginEdited")!.Invoke(f.Page.View, edit);
        return edit.Accepted;
    }

    private static void RaiseConfirmEvent(Fx f, OpenCodeBillionContext context, bool confirmed)
        => EventField<EventHandler<OpenCodeBillionConfirmRequest>>(f.Page.View, "BillionConfirmationChanged")!
            .Invoke(f.Page.View, new OpenCodeBillionConfirmRequest(context, confirmed));

    private static IEnumerable<string> AllText(UIElement root)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item is DependencyObject d)
            {
                foreach (var value in d.Attached.Values.OfType<string>()) yield return value;
            }

            switch (item)
            {
                case TextBlock text: yield return text.Text; break;
                case TextBox box: yield return box.PlaceholderText; break; // the box's own Text is the draft itself
                case Panel panel: foreach (var child in panel.Children) stack.Push(child); break;
                case Border border when border.Child is not null: stack.Push(border.Child); break;
                case UserControl user when user.Content is not null: stack.Push(user.Content); break;
                case ContentControl content when content.Content is not null: stack.Push(content.Content); break;
            }
        }
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static T? EventField<T>(object target, string name) where T : Delegate
        => (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Check(bool condition, string what)
    {
        Assertions++;
        if (!condition) throw new InvalidOperationException("assertion: " + what);
    }

    private sealed class OfflineOpenCode : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public bool Discovery;
        public Func<CancellationToken, Task<HttpResponseMessage>>? RpcReply;
        public int DiscoveryRequests;
        public CancellationToken RpcToken;

        internal static HttpResponseMessage Ready(string origin) => Json("{\"output\":{\"protocolVersion\":1,\"status\":\"ready\",\"origin\":\"" + origin + "\"}}");

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            if (Discovery && request.Method == HttpMethod.Post)
            {
                Check(request.RequestUri!.AbsolutePath == "/prefix/api/rpc/opencode.windows-frontend/billion.origin"
                    && Uri.UnescapeDataString(request.RequestUri.Query) == "?location[directory]=C:/view-fixture"
                    && await request.Content!.ReadAsStringAsync(token) == "{\"input\":{}}", "fixture receives only the exact discovery RPC/location/body");
                DiscoveryRequests++;
                RpcToken = token;
                return await RpcReply!(token);
            }
            if (Discovery && request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/prefix/api/session/ses_a")
                return Json("{\"data\":{\"id\":\"ses_a\",\"location\":{\"directory\":\"C:/view-fixture\"}}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Single-thread context: controller continuations run on the test thread, like the UI thread.</summary>
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _work = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_work) _work.Enqueue((d, state));
        }

        public void RunUntil(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                (SendOrPostCallback, object?)? next = null;
                lock (_work) if (_work.Count > 0) next = _work.Dequeue();
                if (next is { } item)
                {
                    item.Item1(item.Item2);
                    continue;
                }

                if (done()) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("pump did not settle");
                Thread.Sleep(1);
            }
        }
    }
}
