using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;

internal static class ComposerActivityTests
{
    internal static async Task Run()
    {
        Program.Check(!OpenCodeComposerHeightPolicy.UseBottomToolbar(20, 20)
            && OpenCodeComposerHeightPolicy.UseBottomToolbar(40, 20)
            && !OpenCodeComposerHeightPolicy.CanExpand(100, 20)
            && OpenCodeComposerHeightPolicy.CanExpand(120, 20)
            && OpenCodeComposerHeightPolicy.ViewportHeight(20, 18) == 118,
            "native measured visual-line policy: inline one / toolbar two / scroll five / expand six; no newline heuristic");
        foreach (var filesOnly in new[] { false, true })
        foreach (var queued in new[] { false, true })
        {
            using var f = new Program.Fixture();
            f.Page.Foreground("ses_test", true);
            if (filesOnly) await f.Page.File(); else f.Page.Text("real button delivery");
            Program.Check(f.Page.SendEnabled && f.Page.SendMode == OpenCodeComposerSendMode.Send
                && f.Page.ComposerQueueButton.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                && f.Page.ComposerQueueButton.IsEnabled,
                "working sendable text/attachment-only draft exposes primary direct + separate left clock");
            if (queued) f.Page.ClickQueue(); else f.Page.Click();
            await f.Page.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Program.Check(f.Http.Posts == 1 && f.Http.Interrupts == 0
                && f.Http.Body!.Contains("\"delivery\":\"" + (queued ? "queue" : "steer") + "\"", StringComparison.Ordinal)
                && (!filesOnly || f.Http.Body.Contains("\"files\"", StringComparison.Ordinal)),
                "actual button -> immutable basis -> guarded real prompt transport exact delivery, no interrupt: " + filesOnly + "/" + queued);
            f.Page.Text("next direct gesture");
            await f.Page.Send();
            Program.Check(f.Http.Posts == 2 && f.Http.Body!.Contains("\"delivery\":\"steer\"", StringComparison.Ordinal)
                && f.Page.RequestedComposerDelivery == "steer", "queue gesture never leaks into the following default/direct send");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Text("dispatcher-bound queue"); f.Page.Foreground("ses_test", true);
            f.Page.DispatcherQueue.HasThreadAccess = false;
            f.Page.ClickQueue();
            Program.Check(f.Http.Posts == 0, "queue gesture captured before dispatcher route, no premature mutation");
            f.Page.DispatcherQueue.HasThreadAccess = true;
            f.Page.DrainDispatcher();
            await f.Page.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Program.Check(f.Http.Body!.Contains("\"delivery\":\"queue\"", StringComparison.Ordinal)
                && f.Page.RequestedComposerDelivery == "steer", "queued dispatcher callback carries immutable explicit queue, default remains steer");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Text("held source"); f.Page.Foreground("ses_test", true);
            f.Page.DispatcherQueue.HasThreadAccess = false; f.Page.ClickQueue();
            f.Page.Switch("ses_other"); f.Page.Switch("ses_test");
            f.Page.DispatcherQueue.HasThreadAccess = true; f.Page.DrainDispatcher();
            Program.Check(f.Http.Posts == 0, "queued real button cannot cross source/session ABA before dispatch");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Foreground("ses_test", true); await f.Page.Command("args");
            Program.Check(f.Page.ComposerQueueButton.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
                "server command has no invented queue capability");
            f.Page.ClickQueue(); f.Page.PublicSubmit("queue");
            Program.Check(f.Http.Posts == 0, "unsupported queue command cannot start even with forged button gesture");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Text("long editor draft"); await f.Page.File();
            var before = f.Page.Snapshot;
            var ticket = f.Page.Controller.TryTakeDraftEditorAction(before.Token);
            Program.Check(ticket is not null && ticket.Kind == OpenCodeComposerClientActionKind.EditDraft
                && f.Page.Controller.TryTakeDraftEditorAction(before.Token) is null,
                "expand uses exact local editor capability once, not slash insertion or remote endpoint");
            Program.Check(f.Page.Snapshot.Text == before.Text && f.Page.Snapshot.Attachments.Count == 1 && f.Http.Posts == 0,
                "taking expand capability leaves actual full draft, attachment and source untouched");
            f.Page.Switch("ses_other");
            Program.Check(!f.Page.Controller.OwnsClientAction(ticket!)
                && f.Page.Controller.TryApplyClientText(ticket!, "stale large buffer") is null,
                "existing editor capability stale-source apply guard remains effective");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Text("sidebar capability draft"); await f.Page.File();
            var before = f.Page.Snapshot;
            var ticket = f.Page.Controller.TryTakeModelPickerAction(before.Token);
            Program.Check(ticket is { Kind: OpenCodeComposerClientActionKind.ChooseModel }
                && f.Page.Controller.TryTakeModelPickerAction(before.Token) is null,
                "sidebar model capability is single-flight and preserves exact current session token");
            Program.Check(f.Page.Snapshot.Text == before.Text && f.Page.Snapshot.Attachments.Count == before.Attachments.Count
                && f.Http.Posts == 0, "sidebar capability cannot submit or mutate draft contents");
            f.Page.Switch("ses_other"); f.Page.Switch("ses_test");
            Program.Check(!f.Page.Controller.OwnsClientAction(ticket!)
                && f.Page.Controller.TryTakeModelPickerAction(before.Token) is null, "sidebar ticket cannot cross session ABA");
            f.Page.EnterBlank();
            Program.Check(f.Page.Controller.TryTakeModelPickerAction(f.Page.Snapshot.Token) is null,
                "blank preferences never masquerade as a persisted session model action");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Foreground("ses_other", true);
            Program.Check(!f.Page.SendEnabled && !f.Page.ShouldStop(), "other terminal's other session cannot expose selected STOP");
            f.Page.Foreground("ses_test", true);
            Program.Check(f.Page.SendEnabled && f.Page.ShouldStop() && f.Page.StopMode && f.Page.Tracked == 0,
                "external running exposes usable STOP without fabricated local request");
            f.Page.Click();
            await f.Page.Refreshed.Task;
            Program.Check(f.Http.Interrupts == 1 && f.Http.Path == "/api/session/ses_test/interrupt",
                "actual production button STOP path calls real client for exact selected external session");
            using (var lease = await f.Lease()) Program.Check(lease.GetSessionControlProtection() is null,
                "verified interrupt boolean settles exact protection");
            f.Page.SetWorking(); f.Page.Foreground("ses_test", false);
            Program.Check(!f.Page.SendEnabled && !f.Page.ShouldStop(), "authoritative external idle overrides stale local working");
            f.Page.Text("keep this draft"); f.Page.Foreground("ses_test", true);
            Program.Check(f.Page.SendEnabled && f.Page.SendMode == OpenCodeComposerSendMode.Send && !f.Page.ShouldStop()
                && f.Page.Snapshot.Text == "keep this draft", "external running preserves typed draft and primary direct route");
            f.Page.Foreground("ses_test", false);
            Program.Check(f.Page.SendMode == OpenCodeComposerSendMode.Send && f.Page.Snapshot.Text == "keep this draft",
                "external idle preserves draft and restores send presentation");
            f.Page.Discard(); f.Page.Foreground("ses_test", true); f.Page.Switch("ses_other");
            Program.Check(f.Page.ShouldStop(), "new selected session uses its own known running observation");
            await f.Page.StopDirect();
            Program.Check(f.Http.Path == "/api/session/ses_other/interrupt", "session switch interrupts only new selection");
        }
        foreach (var failure in new[] { "transport", "protocol" })
        {
            using var f = new Program.Fixture(); f.Page.Foreground("ses_test", true);
            f.Http.Respond = (_, _) => failure == "transport"
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic disconnect"))
                : Task.FromResult(Program.Json("{}"));
            await f.Page.StopDirect(); await f.Page.StopDirect();
            Program.Check(f.Http.Interrupts == 1 && !f.Page.SendEnabled, "unknown interrupt prevents automatic/explicit duplicate: " + failure);
            using var lease = await f.Lease();
            Program.Check(lease.GetSessionControlProtection() is { Kind: SessionControlKind.Interrupt }
                && lease.GetComposerSubmissionProtection() is null && !lease.CanSubmitComposer() && !lease.CanSwitchAgent(),
                "unknown interrupt retains shared whole-session mutation protection: " + failure);
        }
        foreach (var status in new[] { 400, 401, 404 })
        {
            using var f = new Program.Fixture(); f.Page.Foreground("ses_test", true);
            var tag = status == 400 ? "InvalidRequestError" : status == 401 ? "UnauthorizedError" : "SessionNotFoundError";
            var body = "{\"_tag\":\"" + tag + "\",\"message\":\"not echoed\""
                + (status == 404 ? ",\"sessionID\":\"ses_test\"" : "") + "}";
            f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            await f.Page.StopDirect();
            using (var lease = await f.Lease()) Program.Check(lease.GetSessionControlProtection() is null && lease.CanSubmitComposer(),
                "verified pre-effect interrupt rejection settles exact real gate: " + status);
            Program.Check(f.Http.Interrupts == 1, "definitive rejection never automatically retries: " + status);
            if (status == 404)
            {
                Program.Check(!f.Page.SendEnabled, "deleted current session stays unavailable despite settled interrupt gate");
                f.Page.Switch("ses_other");
            }
            f.Page.Text("retained unrelated draft");
            Program.Check(f.Page.SendEnabled && f.Page.Snapshot.Text == "retained unrelated draft",
                "definitive rejection does not permanently block unrelated draft/send eligibility: " + status);
        }
        foreach (var body in new[]
        {
            "{\"_tag\":\"FutureError\",\"message\":\"unknown 400\"}",
            "{\"_tag\":\"InvalidRequestError\",\"message\":\"unverified shape\",\"extra\":true}",
        })
        {
            using var f = new Program.Fixture(); f.Page.Foreground("ses_test", true);
            f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            await f.Page.StopDirect();
            using var lease = await f.Lease();
            Program.Check(lease.GetSessionControlProtection() is { Kind: SessionControlKind.Interrupt } && !lease.CanSubmitComposer(),
                "HTTP status alone/unverified error identity never clears interrupt protection");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Foreground("ses_test", true);
            f.Http.Respond = (_, _) =>
            {
                var rejected = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Unauthorized") };
                rejected.Headers.WwwAuthenticate.Add(new("Basic", "realm=\"Secure Area\""));
                return Task.FromResult(rejected);
            };
            await f.Page.StopDirect();
            using var lease = await f.Lease();
            Program.Check(lease.GetSessionControlProtection() is null && lease.CanSubmitComposer(),
                "documented plain-text Basic-auth 401 challenge is pre-route rejection, not ambiguous JSON");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Foreground("ses_test", true);
            using var held = await f.Lease();
            var task = f.Page.StopDirect();
            f.Page.InvalidateHistoryForComposerTest();
            held.Dispose(); await task;
            Program.Check(f.Http.Interrupts == 0, "occupied shared lease blocks interrupt before history changes");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Foreground("ses_test", true);
            using var held = await f.Lease(); var task = f.Page.StopDirect();
            f.Page.Switch("ses_other"); held.Dispose(); await task;
            Program.Check(f.Http.Interrupts == 0, "occupied shared lease and session switch cannot start stale interrupt");
        }
        using (var f = new Program.Fixture())
        {
            f.Page.Foreground("ses_test", true);
            using var held = await f.Lease(); var task = f.Page.StopDirect();
            f.Source.Revoke(); held.Dispose(); await task;
            Program.Check(f.Http.Interrupts == 0, "occupied shared lease and source revocation cannot start stale interrupt");
        }
        foreach (var foreground in new bool?[] { null, false, true })
        foreach (var local in new[] { false, true })
        {
            Program.Check(OpenCodeComposerActivityPolicy.IsWorking(true, foreground, local) == (foreground ?? local), "authoritative/local precedence");
            Program.Check(!OpenCodeComposerActivityPolicy.IsWorking(false, foreground, local), "stale origin never working");
        }
        Program.Check(OpenCodeComposerActivityPolicy.QueueHeight(100) == 68 && OpenCodeComposerActivityPolicy.QueueHeight(1000) == 320
            && OpenCodeComposerActivityPolicy.QueueHeight(10) == 1, "queue height clamps to real space above without minimum overflow");
        foreach (var change in new[] { "session", "source" })
        {
            using var f = new Program.Fixture(); f.Page.Foreground("ses_test", true);
            var response = Program.Signal<HttpResponseMessage>();
            f.Http.Respond = (_, _) => response.Task;
            var task = f.Page.StopDirect();
            Program.Check(f.Http.Interrupts == 1, "captured interrupt really started before " + change + " changed");
            if (change == "session") f.Page.Switch("ses_other"); else f.Source.Revoke();
            response.SetResult(Program.Json("{\"interrupted\":true}")); await task;
            using var lease = await f.Lease();
            Program.Check(lease.GetSessionControlProtection() is not null && f.Page.Messages.Count == 0,
                "late old-origin result cannot clear protection or publish into changed " + change);
        }
        foreach (var change in new[] { "session", "source" })
        {
            using var f = new Program.Fixture(); f.Page.Foreground("ses_test", true);
            var response = Program.Signal<HttpResponseMessage>();
            f.Http.Respond = (_, _) => response.Task;
            var task = f.Page.StopDirect();
            if (change == "session") f.Page.Switch("ses_other"); else f.Source.Revoke();
            response.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                { Content = new StringContent("{\"_tag\":\"SessionNotFoundError\",\"message\":\"gone\",\"sessionID\":\"ses_test\"}", Encoding.UTF8, "application/json") });
            await task;
            using var lease = await f.Lease();
            Program.Check(lease.GetSessionControlProtection() is { Kind: SessionControlKind.Interrupt },
                "even typed rejection cannot settle an operation after stale " + change);
        }
        using (var f = new Program.Fixture())
        {
            var controls = new OpenCodeSessionControlCoordinator(OpenCodeInteractionGate.Shared);
            var rejected = false;
            try { await controls.PreviewAsync(new(f.Source, f.Source.Revision, "ses_test", 1), new(SessionControlKind.Interrupt), new("draft", true), _ => true); }
            catch (ArgumentException) { rejected = true; }
            Program.Check(rejected && f.Http.Interrupts == 0, "Interrupt is not accidentally offered through model/history preview dispatch");
        }
        Console.WriteLine("PASS composer external activity, actual interrupt transport/gates/unknown protection and upward height policy");
    }
}

namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly Dictionary<string, (bool Working, long Version)> _liveSessionStatuses = new(StringComparer.Ordinal);
        internal void Foreground(string session, bool running) { _liveSessionStatuses[session] = (running, 1); RefreshComposerSessionActivity(); }
        internal void InvalidateHistoryForComposerTest() => _messageHistoryEpoch++;
        // Definition only; implementation is the compiled real production integration partial.
        partial void BeginComposerInterruptProtection(OpenCodeInteractionGate.Lease lease, Guid operationId,
            string fingerprint, ref bool started);
    }
}
