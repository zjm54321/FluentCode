using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

static partial class Checks
{
    private static async Task ForegroundNotifications()
    {
        foreach (var (label, active, minimized, visible, selected) in new[] {
            ("foreground selected", true, false, true, "ses_test"),
            ("minimized", true, true, true, "ses_test"),
            ("deactivated", false, false, true, "ses_test"),
            ("other tab selected", true, false, true, "ses_other"),
            ("page hidden", true, false, false, "ses_test") })
        {
            uint native = 0; int completionShows = 0, interactionShows = 0, writes = 0;
            var removed = new List<uint>();
            var question = new OpenCodeFormDetail { Id = "frm_front", SessionId = "ses_test", Title = "Question",
                Metadata = JsonSerializer.SerializeToElement(new { kind = "question" }), State = new() { Status = "pending" },
                Fields = [new() { Key = "answer", Type = "string", Title = "Continue?", Required = true }] };
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (request.Method != HttpMethod.Get) writes++;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/permission/per_test")) return Task.FromResult(Json(new { data = Permission() }));
                if (path.EndsWith("/form/frm_front")) return Task.FromResult(Json(new { data = question }));
                if (path == "/api/session/ses_test") return Task.FromResult(Json(new { data = new { id = "ses_test" } }));
                return Task.FromResult(Json(new { data = Array.Empty<object>() }));
            }));
            using var client = Client(http);
            var source = new TrustedOpenCodeNotificationSource("ep-foreground-" + selected + "-" + label.Replace(' ', '-'), client);
            using var service = new CompletionNotificationService(TimeProvider.System,
                (_, _, _) => { completionShows++; return new(++native, "completion", "test"); },
                _ => { interactionShows++; return new(++native, "interaction", "test"); },
                id => { removed.Add(id); return Task.CompletedTask; });
            using var monitor = new OpenCodeNotificationMonitor(service, source); // binds the actual notification source
            var owner = new object();
            var foreground = active && !minimized && visible;
            await service.UpdateOpenCodeForegroundSession(owner, source.Identity, selected, foreground);
            service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_base", 1)], true);
            var dispatch = service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_done", 2)], false);
            var permission = await service.NotifyPermissionAsync(source, "ses_test", "per_test");
            var form = await service.NotifyFormAsync(source, "ses_test", "frm_front");
            bool suppress = foreground && selected == "ses_test";
            Check(completionShows == (suppress ? 0 : 1) && dispatch.Single().Status == (suppress
                ? NotificationDispatchStatus.Suppressed : NotificationDispatchStatus.Submitted), label + ": completion dispatch follows current foreground scope");
            Check(interactionShows == (suppress ? 0 : 2) && permission.Status == (suppress
                ? NotificationInteractionStatus.Suppressed : NotificationInteractionStatus.Submitted), label + ": permission is overlay-only in foreground");
            Check(form.Status == (suppress ? NotificationInteractionStatus.Suppressed : NotificationInteractionStatus.Submitted), label + ": question follows the same foreground policy");
            if (!suppress)
            {
                var acknowledged = await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_test", true);
                Check(acknowledged.AcknowledgedCount == 3 && acknowledged.RemovedCount == 3 && removed.Distinct().Count() == 3,
                    label + ": returning this session to foreground consumes/removes all three existing native toasts");
            }
            await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_test", false);
            var repeated = service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_done", 2)], false);
            var permissionAgain = await service.NotifyPermissionAsync(source, "ses_test", "per_test");
            var formAgain = await service.NotifyFormAsync(source, "ses_test", "frm_front");
            Check(repeated.Count == 0 && completionShows == (suppress ? 0 : 1), label + ": consumed completion never re-fires after deactivation");
            Check(interactionShows == (suppress ? 0 : 2) && permissionAgain.Status != NotificationInteractionStatus.Submitted
                && formAgain.Status != NotificationInteractionStatus.Submitted, label + ": consumed pending items never re-fire after deactivation");
            service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_later", 3)], false);
            Check(completionShows == (suppress ? 1 : 2), label + ": a NEW background completion still notifies");
            Check(writes == 0, label + ": foreground consumption never POSTs a viewed receipt, answer or permission");
        }
        await ForegroundDispatchRaces();
        await ForegroundAppFence();
        await ForegroundMonitorSeen();
    }

    private static async Task ForegroundDispatchRaces()
    {
        int shown = 0;
        TaskCompletionSource? read = null, finish = null;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/permission/per_test"))
            {
                read!.TrySetResult(); await finish!.Task;
                return Json(new { data = Permission() });
            }
            return Json(new { data = new { id = "ses_test" } });
        }));
        using var client = Client(http);
        var source = new TrustedOpenCodeNotificationSource("ep-foreground-race", client);
        using var service = new CompletionNotificationService(TimeProvider.System, (_, _, _) => new(0, "", ""),
            _ => { shown++; return new(1, "", ""); }, _ => Task.CompletedTask);
        using var monitor = new OpenCodeNotificationMonitor(service, source);
        var owner = new object();
        read = new(TaskCreationOptions.RunContinuationsAsynchronously); finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparing = service.NotifyPermissionAsync(source, "ses_test", "per_test");
        await read.Task;
        await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_test", true);
        finish.SetResult();
        Check((await preparing).Status == NotificationInteractionStatus.Suppressed && shown == 0,
            "becoming foreground during HTTP preparation suppresses at final native dispatch, not at request start");

        // A fresh source avoids dedupe from the first race; start foreground, leave during GET.
        var other = new TrustedOpenCodeNotificationSource("ep-foreground-race-other", client);
        service.TryBindNotificationSource(other);
        await service.UpdateOpenCodeForegroundSession(owner, other.Identity, "ses_test", true);
        read = new(TaskCreationOptions.RunContinuationsAsynchronously); finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        preparing = service.NotifyPermissionAsync(other, "ses_test", "per_test");
        await read.Task;
        await service.UpdateOpenCodeForegroundSession(owner, other.Identity, "ses_test", false);
        finish.SetResult();
        Check((await preparing).Status == NotificationInteractionStatus.Submitted && shown == 1,
            "leaving foreground during HTTP preparation notifies using final current state");
        var newOwner = new object();
        await service.UpdateOpenCodeForegroundSession(newOwner, other.Identity, "ses_test", true);
        await service.UpdateOpenCodeForegroundSession(owner, other.Identity, "ses_test", false);
        service.ObserveTrustedOpenCodeMessages(other, "ses_test", [Idle("msg_base", 1)], true);
        var result = service.ObserveTrustedOpenCodeMessages(other, "ses_test", [Idle("msg_done", 2)], false);
        Check(result.Single().Status == NotificationDispatchStatus.Suppressed, "late unload from old page cannot clear a newer foreground owner");
    }

    private static async Task ForegroundAppFence()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(new { data = Array.Empty<object>() }))));
        using var client = Client(http);
        var source = new TrustedOpenCodeNotificationSource("ep-foreground-app", client);
        using var service = new CompletionNotificationService(TimeProvider.System, (_, _, _) => new(1, "", ""), _ => new(2, "", ""), _ => Task.CompletedTask);
        using var monitor = new OpenCodeNotificationMonitor(service, source);
        var pageSource = new OpenCodePageSource(source.Identity, 1, () => client);
        var app = new FluentCode.App(); app.SetSource(pageSource, monitor); app.SetNotificationService(service);
        var owner = new object();
        app.UpdateOpenCodeForegroundSession(owner, pageSource, "ses_test", true);
        service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_base", 1)], true);
        Check(service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_front", 2)], false).Single().Status == NotificationDispatchStatus.Suppressed,
            "real App foreground bridge accepts the exact current source");
        var stale = new OpenCodePageSource(source.Identity, 1, () => client);
        app.UpdateOpenCodeForegroundSession(owner, stale, "ses_test", true);
        Check(service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_wrong_cap", 2.5)], false).Single().Status == NotificationDispatchStatus.Suppressed,
            "late stale-source publish cannot clear an already-current foreground owner");
        app.UpdateOpenCodeForegroundSession(owner, pageSource, "ses_test", false);
        app.UpdateOpenCodeForegroundSession(owner, stale, "ses_test", true);
        Check(service.ObserveTrustedOpenCodeMessages(source, "ses_test", [Idle("msg_stale", 3)], false).Single().Status == NotificationDispatchStatus.Submitted,
            "same endpoint/revision but wrong source capability cannot suppress a toast");
        await Task.CompletedTask;
    }

    private static async Task ForegroundMonitorSeen()
    {
        uint native = 0; int detailReads = 0;
        var permission = Permission();
        OpenCodeMessage[] history = [Idle("msg_old", 1)];
        var question = new OpenCodeFormDetail { Id = "frm_seen", SessionId = "ses_test", Title = "Questions",
            Metadata = JsonSerializer.SerializeToElement(new { kind = "question" }), State = new() { Status = "pending" },
            Fields = [new() { Key = "answer", Type = "string", Title = "Continue?", Required = true }] };
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/message")) return Task.FromResult(Json(new { data = history }));
            if (path.EndsWith("/active")) return Task.FromResult(Json(new { data = new Dictionary<string, object>() }));
            if (path.EndsWith("/permission")) return Task.FromResult(Json(new { data = new[] { permission } }));
            if (path.Contains("/permission/", StringComparison.Ordinal)) { detailReads++; return Task.FromResult(Json(new { data = permission })); }
            if (path.EndsWith("/form")) return Task.FromResult(Json(new { data = new[] { question } }));
            if (path.Contains("/form/", StringComparison.Ordinal)) { detailReads++; return Task.FromResult(Json(new { data = question })); }
            if (path == "/api/session/ses_test") return Task.FromResult(Json(new { data = new { id = "ses_test", outcome = "succeeded", time = new { idle = history[0].Time!.Created } } }));
            return Task.FromResult(Json(new { data = Array.Empty<object>() }));
        }));
        using var client = Client(http);
        var source = new TrustedOpenCodeNotificationSource("ep-foreground-monitor", client);
        using var service = new CompletionNotificationService(TimeProvider.System, (_, _, _) => new(++native, "", ""),
            _ => new(++native, "", ""), _ => Task.CompletedTask);
        using var monitor = new OpenCodeNotificationMonitor(service, source);
        var owner = new object(); monitor.TrackSession("ses_test");
        await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_test", true);
        await monitor.PollOnceAsync();
        history = [Idle("msg_front_done", 2), Idle("msg_old", 1)];
        await monitor.PollOnceAsync();
        Check(native == 0 && detailReads == 2, "real monitor consumes foreground completion/permission/question and avoids repeated detail preparation");
        await service.UpdateOpenCodeForegroundSession(owner, source.Identity, "ses_test", false);
        history = [Idle("msg_front_done", 2)];
        await monitor.PollOnceAsync();
        Check(native == 0 && detailReads == 2, "suppressed monitor anchors/prepared sets prevent later re-fire after foreground ends");
        history = [Idle("msg_background_done", 3), Idle("msg_front_done", 2)]; permission = permission with { Id = "per_later" };
        await monitor.PollOnceAsync();
        Check(native == 2 && detailReads == 3, "new background completion and permission still notify after foreground suppression");
    }
}
