using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class ChildPermissionTests
{
    private static int _checks;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); _checks++; }
    private static async Task Until(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception(label); await Task.Delay(1); }
    }
    private static OpenCodeEvent Event(string type, object data) => OpenCodeEvent.Parse(JsonSerializer.Serialize(new { type, data }));
    private static IEnumerable<UIElement> Elements(UIElement root)
    {
        yield return root;
        if (root is Panel panel) foreach (var child in panel.Children) foreach (var descendant in Elements(child)) yield return descendant;
        if (root is ContentControl { Content: UIElement content }) foreach (var descendant in Elements(content)) yield return descendant;
    }
    private static Button Button(OpenCodePage page, string prefix)
        => Elements(page.CurrentPermission!.Presenter).OfType<Button>().Single(b => b.AutomationId == prefix + page.CurrentPermission.Id);
    private static async Task Main()
    {
        await LivePermissionAndReply();
        await ExistingReconnectAndNavigation();
        await WorkflowImeBusyAndStaleRead();
        await ChildRejectUnknownRecovery();
        Console.WriteLine($"PASS child permissions: {_checks} assertions; production page routing/card/overlay/gate/client, fake HTTP and inert native controls only.");
    }

    private static async Task LivePermissionAndReply()
    {
        using var fixture = new Fixture();
        fixture.Backend.Add("ses_root", "per_parent");
        await fixture.Page.Select("ses_root"); await Until(() => fixture.Page.ChildReadsIdle, "initial child snapshot");
        var parentCard = fixture.Page.CurrentPermission;
        fixture.Page.Draft = "parent draft";
        var parentReads = fixture.Backend.PermissionReads.GetValueOrDefault("ses_root");
        fixture.Backend.Add("ses_child", "per_child");
        fixture.Page.Receive(Event("permission.asked", new { sessionID = "ses_child", id = "per_child" }));
        await Until(() => fixture.Page.PermissionCount == 2 && fixture.Page.ChildReadsIdle, "child asked while parent selected must appear");
        fixture.Page.FocusPermission("per_child");
        Check(fixture.Page.PermissionVisible && fixture.Page.CurrentPermission!.Request.SessionId == "ses_child", "existing permission presenter is visible and owns child request");
        Check(fixture.Page.Selected == "ses_root" && fixture.Page.Draft == "parent draft" && fixture.Page.BottomScrolls == 0,
            "showing child permission preserves parent session, draft and timeline position");
        Check(fixture.Backend.PermissionReads.GetValueOrDefault("ses_root") == parentReads, "child ask does not refetch parent transcript or pending endpoints");
        using (var parentLease = await OpenCodeInteractionGate.Shared.EnterAsync(fixture.Source.Identity, "ses_root"))
        {
            Button(fixture.Page, "OpenCodePermissionOnce_").ClickForTest();
            await fixture.Page.LastDecision.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Backend.Posts.Single().Path == "/api/session/ses_child/permission/per_child/reply"
                && fixture.Backend.Posts.Single().Body.Contains("\"decision\":\"once\"", StringComparison.Ordinal),
                "actual card button replies once to child session plus exact request ID, independently of parent gate lease");
            Check(parentLease.GetProtectionStatus("per_child", NotificationNavigationKind.Permission) is null, "child decision never writes a parent gate record");
        }
        using (var childLease = await OpenCodeInteractionGate.Shared.EnterAsync(fixture.Source.Identity, "ses_child"))
            Check(childLease.GetProtectionStatus("per_child", NotificationNavigationKind.Permission) == NotificationInteractionStatus.Duplicate, "204 settles the owning child gate record");
        fixture.Page.Receive(Event("permission.replied", new { sessionID = "ses_child", requestID = "per_child" }));
        fixture.Page.Receive(Event("permission.replied", new { sessionID = "ses_child", requestID = "per_child" }));
        await Until(() => fixture.Page.ChildReadsIdle, "duplicate child replied drain");
        Check(fixture.Page.PermissionCount == 1 && ReferenceEquals(parentCard, fixture.Page.CurrentPermission) && fixture.Backend.Posts.Count == 1,
            "reply removes the child once without rebuilding parent card or resending");
        fixture.Backend.Add("ses_foreign", "per_foreign");
        fixture.Page.Receive(Event("permission.asked", new { sessionID = "ses_foreign", id = "per_foreign" }));
        await Until(() => fixture.Page.ChildReadsIdle, "foreign ownership verification");
        Check(fixture.Page.PermissionCount == 1 && fixture.Backend.PermissionReads.GetValueOrDefault("ses_foreign") == 0,
            "unrelated root asks are excluded before pending reads or rendering");
        Button(fixture.Page, "OpenCodePermissionOnce_").ClickForTest(); await fixture.Page.LastDecision;
        Check(fixture.Backend.Posts.Last().Path == "/api/session/ses_root/permission/per_parent/reply" && fixture.Page.PermissionCount == 0,
            "parent permission behavior remains actionable and unchanged");
    }

    private static async Task ExistingReconnectAndNavigation()
    {
        using var fixture = new Fixture();
        fixture.Backend.Add("ses_child", "per_existing"); fixture.Backend.Add("ses_grand", "per_grand");
        await fixture.Page.Select("ses_root"); await Until(() => fixture.Page.PermissionCount == 2 && fixture.Page.ChildReadsIdle, "existing descendant pending on load");
        Check(fixture.Page.PermissionVisible && fixture.Page.Selected == "ses_root", "initial load restores pre-existing child and grandchild permission cards without navigation");
        Check(fixture.Backend.ChildQueries.Contains("ses_child") && fixture.Backend.ChildQueries.Contains("ses_grand"), "pending discovery traverses child-of-child owner-filtered session pages");
        fixture.Page.FocusPermission("per_grand");
        Check(fixture.Page.CurrentPermission!.Request.SessionId == "ses_grand", "grandchild request retains its exact owning session");
        var previousCancellation = fixture.Page.ChildCancellation!;
        var previousToken = previousCancellation.Token;
        await fixture.Page.Select("ses_child"); await Until(() => fixture.Page.ChildReadsIdle, "selected child descendants");
        var disposed = false;
        try { _ = previousCancellation.Token; } catch (ObjectDisposedException) { disposed = true; }
        Check(previousToken.IsCancellationRequested && disposed, "replacing child interaction binding cancels and disposes its owned linked registrations");
        Check(fixture.Page.PermissionVisible && fixture.Page.ChildEditorHidden && fixture.Page.Selected == "ses_child",
            "navigating into child keeps existing permission overlay visible even though its composer editor is hidden");
        await fixture.Page.Select("ses_root"); await Until(() => fixture.Page.ChildReadsIdle, "return to parent pending");
        Check(fixture.Page.PermissionCount == 2 && fixture.Page.Selected == "ses_root", "return to parent restores pending descendants without duplicating cards");
        fixture.Backend.Add("ses_grand", "per_gap"); fixture.Page.Connected(reconnect: true);
        await Until(() => fixture.Page.PermissionCount == 3 && fixture.Page.ChildReadsIdle, "reconnect child pending resync");
        Check(fixture.Page.Selected == "ses_root" && fixture.Backend.Posts.Count == 0, "reconnect restores existing grandchild request read-only, never auto-approves");
        var childQueries = fixture.Backend.ChildQueries.Count;
        fixture.Backend.Add("ses_child", "per_fallback"); fixture.Page.Poll(connected: true);
        Check(fixture.Backend.ChildQueries.Count == childQueries && fixture.Page.PermissionCount == 3, "healthy SSE still suppresses recurring child snapshot polling");
        fixture.Page.Poll(connected: false);
        await Until(() => fixture.Page.PermissionCount == 4 && fixture.Page.ChildReadsIdle, "fallback polling restores child pending without SSE");
        Check(fixture.Page.Selected == "ses_root" && fixture.Backend.Posts.Count == 0, "unhealthy feed fallback reads discover pending children without mutation or navigation");
        fixture.Backend.Forms["ses_grand"] = [new() { Id = "frm_child", SessionId = "ses_grand", Title = "Question", Fields = [new() { Key = "answer", Type = "string" }] }];
        var formEvent = Event("form.created", new { form = new { sessionID = "ses_grand", id = "frm_child" } });
        Check(formEvent.SessionId == "ses_grand", "native form.created nested owner is routed like permissions");
        fixture.Page.Receive(formEvent); await Until(() => fixture.Page.Forms.Count == 1 && fixture.Page.ChildReadsIdle, "grandchild form pending");
        fixture.Page.Forms.Single().SubmitForTest(new Dictionary<string, JsonElement> { ["answer"] = JsonSerializer.SerializeToElement("explicit synthetic answer") });
        await fixture.Page.LastDecision;
        Check(fixture.Backend.Posts.Single().Path == "/api/session/ses_grand/form/frm_child/reply" && fixture.Page.Selected == "ses_root", "child form submission uses owning session without losing parent context");
    }

    private static async Task WorkflowImeBusyAndStaleRead()
    {
        using var fixture = new Fixture(); await fixture.Page.Select("ses_root"); await Until(() => fixture.Page.ChildReadsIdle, "IME initial load");
        fixture.Page.Draft = "组合中的父会话草稿"; fixture.Page.BeginComposition(); fixture.Page.BeginWorkflow();
        fixture.Backend.Add("ses_child", "per_ime"); fixture.Page.Receive(Event("permission.asked", new { sessionID = "ses_child", id = "per_ime" }));
        await Until(() => fixture.Page.PermissionCount == 1 && fixture.Page.ChildReadsIdle, "parent workflow does not discard child prompt");
        Check(!fixture.Page.PermissionVisible && fixture.Page.ComposerEnabled && fixture.Page.Draft == "组合中的父会话草稿",
            $"IME defers overlay coverage but retains child request and draft during parent workflow: visible={fixture.Page.PermissionVisible}, enabled={fixture.Page.ComposerEnabled}, draft={fixture.Page.Draft}");
        fixture.Page.EndComposition();
        Check(fixture.Page.PermissionVisible && !fixture.Page.ComposerEnabled && fixture.Backend.Posts.Count == 0, "composition end immediately displays existing card without waiting for polling or sending a decision");
        fixture.Page.EndWorkflow();
        using (var childLease = await OpenCodeInteractionGate.Shared.EnterAsync(fixture.Source.Identity, "ses_child"))
        {
            Button(fixture.Page, "OpenCodePermissionOnce_").ClickForTest();
            await Until(() => fixture.Page.Busy, "child interaction gate wait");
            fixture.Backend.Add("ses_grand", "per_busy"); fixture.Page.Receive(Event("permission.asked", new { sessionID = "ses_grand", id = "per_busy" }));
            await Until(() => fixture.Page.PermissionCount == 2 && fixture.Page.ChildReadsIdle, "new child request while interaction busy"); fixture.Page.FocusPermission("per_busy");
            Check(!Button(fixture.Page, "OpenCodePermissionOnce_").IsEnabled, "newly discovered cards inherit the existing interaction busy deferral");
        }
        await fixture.Page.LastDecision; fixture.Page.FocusPermission("per_busy");
        Check(Button(fixture.Page, "OpenCodePermissionOnce_").IsEnabled, "busy completion restores child card actions");
        fixture.Backend.Add("ses_child", "per_race"); fixture.Backend.DelayNextList("ses_child");
        fixture.Page.Receive(Event("permission.asked", new { sessionID = "ses_child", id = "per_race" }));
        await fixture.Backend.ListStarted!.Task;
        fixture.Backend.Permissions["ses_child"].Clear();
        fixture.Page.Receive(Event("permission.replied", new { sessionID = "ses_child", requestID = "per_race" }));
        fixture.Backend.ListFinish!.SetResult(); await Until(() => fixture.Page.ChildReadsIdle, "stale child snapshot replay");
        Check(fixture.Page.PermissionCount == 1 && fixture.Page.CurrentPermission!.Id == "per_busy", "replied event beats stale in-flight child pending snapshot and cannot resurrect a settled request");
        fixture.Backend.Add("ses_child", "per_late"); fixture.Backend.DelayNextList("ses_child");
        fixture.Page.Receive(Event("permission.asked", new { sessionID = "ses_child", id = "per_late" })); await fixture.Backend.ListStarted!.Task;
        await fixture.Page.Select("ses_foreign"); fixture.Backend.ListFinish!.SetResult();
        await Task.Yield();
        Check(fixture.Page.Selected == "ses_foreign" && fixture.Page.PermissionCount == 0, "late child read cannot cross parent session generation replacement");
    }

    private static async Task ChildRejectUnknownRecovery()
    {
        using var fixture = new Fixture();
        fixture.Backend.Add("ses_root", "per_parent"); fixture.Backend.Add("ses_child", "per_reject"); fixture.Backend.Add("ses_child", "per_peer");
        await fixture.Page.Select("ses_root"); await Until(() => fixture.Page.ChildReadsIdle, "reject initial pending"); fixture.Page.FocusPermission("per_reject");
        Button(fixture.Page, "OpenCodePermissionReject_").ClickForTest();
        var confirmation = Elements(fixture.Page.CurrentPermission!.Presenter).OfType<InfoBar>().Single(b => b.IsOpen);
        Check(confirmation.Message!.Contains("其他 1 个", StringComparison.Ordinal), "reject confirmation counts only other permissions in the same owning child session");
        fixture.Backend.FailNextReply = true; Button(fixture.Page, "OpenCodePermissionConfirm_").ClickForTest(); await fixture.Page.LastDecision;
        using (var root = await OpenCodeInteractionGate.Shared.EnterAsync(fixture.Source.Identity, "ses_root"))
            Check(root.GetSessionProtection() is null && root.CanSubmitComposer(), "unknown child reject does not protect or invalidate the parent session");
        using (var child = await OpenCodeInteractionGate.Shared.EnterAsync(fixture.Source.Identity, "ses_child"))
            Check(child.GetSessionProtection()?.RequestId == "per_reject", "ambiguous child reject stays OutcomeUnknown in the exact child gate");
        fixture.Page.FocusPermission("per_parent"); Check(Button(fixture.Page, "OpenCodePermissionOnce_").IsEnabled, "child reject side effects never block a parent permission card");
        fixture.Page.FocusPermission("per_peer"); Check(!Elements(fixture.Page.CurrentPermission!.Presenter).OfType<Button>().Any(b => b.AutomationId.StartsWith("OpenCodePermissionOnce_", StringComparison.Ordinal) && b.Parent is UIElement { Visibility: Visibility.Visible }),
            "unknown reject blocks sibling permissions only within the child");
        await fixture.Page.Recheck("per_reject");
        Check(fixture.Page.RecoveryConfirm && fixture.Page.RecoverySession == "ses_child" && fixture.Page.Selected == "ses_root", "explicit recheck opens child-owned whole-session recovery while retaining parent context");
        await fixture.Page.Recover(retry: true);
        using (var child = await OpenCodeInteractionGate.Shared.EnterAsync(fixture.Source.Identity, "ses_child"))
            Check(child.GetSessionProtection() is null, "explicit recovery clears only the confirmed child protection");
        Check(fixture.Backend.Posts.Count == 1 && fixture.Page.Selected == "ses_root", "unknown-outcome recovery never automatically resends or approves");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly Backend Backend = new();
        internal readonly HttpClient Http;
        internal readonly OpenCodePageSource Source;
        internal readonly OpenCodePage Page;
        internal Fixture()
        {
            Http = new(Backend);
            Source = new("child-permission-test-" + Guid.NewGuid().ToString("N"), 1,
                () => new OpenCodeClient(Http, new() { BaseUrl = "http://fixture.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, transportSafetyConfirmed: true));
            Page = new(Source);
        }
        public void Dispose() { Page.DisposeFixture(); Http.Dispose(); }
    }
    private sealed class Backend : HttpMessageHandler
    {
        internal readonly Dictionary<string, string?> Parents = new() { ["ses_root"] = null, ["ses_child"] = "ses_root", ["ses_grand"] = "ses_child", ["ses_foreign"] = null };
        internal readonly Dictionary<string, List<OpenCodePermissionRequest>> Permissions = new();
        internal readonly Dictionary<string, List<OpenCodeFormInfo>> Forms = new();
        internal readonly Dictionary<string, int> PermissionReads = new();
        internal readonly List<string> ChildQueries = new();
        internal readonly List<(string Path, string Body)> Posts = new();
        internal bool FailNextReply;
        private string? _delaySession;
        internal TaskCompletionSource? ListStarted, ListFinish;
        internal void Add(string session, string id)
        {
            if (!Permissions.TryGetValue(session, out var pending)) Permissions[session] = pending = new();
            pending.Add(new() { Id = id, SessionId = session, Action = "external_directory", Resources = ["/synthetic/" + id], Save = ["/synthetic/*"] });
        }
        internal void DelayNextList(string session) { _delaySession = session; ListStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); ListFinish = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var parts = path.Split('/');
            if (path == "/api/session")
            {
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                var parent = query["parentID"]; ChildQueries.Add(parent);
                var sessions = Parents.Where(p => p.Value == parent).Select(p => new OpenCodeSessionInfo { Id = p.Key, ParentId = p.Value }).ToList();
                if (parent == "ses_root") sessions.Add(new() { Id = "ses_foreign", ParentId = null }); // Server-side wrong-owner entry must not leak into the family.
                return Json(new { data = sessions });
            }
            if (parts.Length < 4) throw new Exception("Unexpected fake route " + path);
            var session = parts[3];
            if (parts.Length == 4) return Json(new { data = new OpenCodeSessionInfo { Id = session, ParentId = Parents.GetValueOrDefault(session) } });
            var resource = parts[4];
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(token); Posts.Add((path, body));
                if (FailNextReply) { FailNextReply = false; throw new HttpRequestException("Synthetic ambiguous reply"); }
                if (resource == "permission")
                {
                    if (JsonDocument.Parse(body).RootElement.GetProperty("decision").GetString() == "reject") Permissions[session].Clear();
                    else Permissions[session].RemoveAll(p => p.Id == parts[5]);
                }
                else Forms[session].RemoveAll(f => f.Id == parts[5]);
                return new(HttpStatusCode.NoContent);
            }
            if (resource == "permission")
            {
                if (parts.Length == 6)
                {
                    var permission = Permissions.GetValueOrDefault(session)?.SingleOrDefault(p => p.Id == parts[5]);
                    return permission is null ? new(HttpStatusCode.NotFound) : Json(new { data = permission });
                }
                PermissionReads[session] = PermissionReads.GetValueOrDefault(session) + 1;
                var response = Json(new { data = Permissions.GetValueOrDefault(session)?.ToArray() ?? [] });
                if (_delaySession == session) { _delaySession = null; var finish = ListFinish!; ListStarted!.SetResult(); await finish.Task; }
                return response;
            }
            if (resource == "form")
            {
                if (parts.Length == 6)
                {
                    var form = Forms[session].Single(f => f.Id == parts[5]);
                    return Json(new { data = new OpenCodeFormDetail { Id = form.Id, SessionId = form.SessionId, Title = form.Title, Fields = form.Fields, State = new() { Status = "pending" } } });
                }
                return Json(new { data = Forms.GetValueOrDefault(session)?.ToArray() ?? [] });
            }
            if (resource == "inbox") return Json(new { data = Array.Empty<object>() });
            throw new Exception("Unexpected fake route " + path);
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
