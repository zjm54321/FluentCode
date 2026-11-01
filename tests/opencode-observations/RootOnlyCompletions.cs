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
    private static async Task RootOnlyCompletionNotifications()
    {
        foreach (var (name, parent, baselineFails, completionFails) in new (string, string?, bool, bool)[]
        {
            ("root", null, false, false),
            ("cached-child", "ses_parent", false, true),
            ("newly-known-child", "ses_parent", true, false),
            ("unknown-on-failure", null, true, true)
        })
        {
            bool child = parent is not null, failInfo = baselineFails, pending = false;
            int infoReads = 0, writes = 0, completions = 0;
            uint native = 0;
            OpenCodeMessage[] history = [Idle("msg_old", 1)];
            var question = new OpenCodeFormDetail
            {
                Id = "frm_child", SessionId = "ses_test", Title = "Questions",
                Metadata = JsonSerializer.SerializeToElement(new { kind = "question" }),
                State = new() { Status = "pending" },
                Fields = [new() { Key = "answer", Type = "string", Title = "继续吗？", Required = true }]
            };
            using var http = new HttpClient(new Handler((request, _) =>
            {
                if (request.Method != HttpMethod.Get) writes++;
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/active")) return Task.FromResult(Json(new { data = new Dictionary<string, object>() }));
                if (path.EndsWith("/message")) return Task.FromResult(Json(new { data = history }));
                if (path.EndsWith("/permission")) return Task.FromResult(Json(new { data = pending ? new[] { Permission() } : [] }));
                if (path.EndsWith("/permission/per_test")) return Task.FromResult(Json(new { data = Permission() }));
                if (path.EndsWith("/form")) return Task.FromResult(Json(new { data = pending ? new[] { question } : [] }));
                if (path.EndsWith("/form/frm_child")) return Task.FromResult(Json(new { data = question }));
                if (path == "/api/session/ses_test")
                {
                    infoReads++;
                    if (failInfo) throw new HttpRequestException("synthetic session info failure");
                    return Task.FromResult(Json(new { data = new { id = "ses_test", parentID = parent, title = name,
                        outcome = "succeeded", time = new { idle = history[0].Time!.Created } } }));
                }
                return Task.FromResult(Json(new { data = Array.Empty<object>() }));
            }));
            using var client = Client(http);
            var source = new TrustedOpenCodeNotificationSource("ep-root-only-" + name, client);
            var plans = new List<NotificationInteractionPlan>();
            var removed = new List<uint>();
            using var service = new CompletionNotificationService(TimeProvider.System,
                (_, _) => { completions++; return new(++native, "completion", "test"); },
                plan => { plans.Add(plan); return new(++native, "interaction", "test"); },
                id => { removed.Add(id); return Task.CompletedTask; });
            using var monitor = new OpenCodeNotificationMonitor(service, source);
            var observations = new List<OpenCodeNotificationObservation>();
            OpenCodeSessionObservation? sessionObservation = null;
            monitor.Observed += (_, args) => observations.Add(args.Observation);
            monitor.SessionObserved += (_, args) => sessionObservation = args.Observation;
            monitor.TrackSession("ses_test");
            await monitor.PollOnceAsync();
            Check(completions == 0, name + ": initial history remains suppressed");

            failInfo = completionFails;
            history = [Idle("msg_done", 2), Idle("msg_old", 1)];
            await monitor.PollOnceAsync();
            Check(completions == (child ? 0 : 1), name + ": only root or unknown ancestry submits a completion toast");
            var dispatches = observations.Last(o => o.Kind == OpenCodeNotificationObservationKind.Messages).Dispatches!;
            Check(child ? dispatches.Count == 0 : dispatches.Count == 1 && dispatches[0].IsSubmitted,
                name + ": suppressed completion has no dispatch or retry result");
            if (name == "cached-child")
                Check(infoReads == 2, "cached child skips completion metadata lookup while execution reads may fail");
            if (name == "newly-known-child")
                Check(sessionObservation?.Outcome == "succeeded", "child execution/status evidence remains published");

            // Drop the old anchor from bounded history. A suppressed child completion must have
            // advanced it to msg_done, otherwise this poll would create an anchor-missing gap.
            history = [Idle("msg_done", 2)];
            failInfo = true;
            await monitor.PollOnceAsync();
            Check(!observations.Any(o => o.Kind == OpenCodeNotificationObservationKind.Gap),
                name + ": completion advances anchor even when no toast is sent");
            Check(completions == (child ? 0 : 1), name + ": unchanged history never re-fires after metadata failure");
            Check(service.ObserveTrustedOpenCodeMessages(source, "ses_test", history, false).Count == 0,
                name + ": completion ID is consumed in the real notification service");
            var acknowledged = await service.AcknowledgeSessionAsync(NotificationSource.OpenCode, source.Identity, "ses_test");
            Check(acknowledged.AcknowledgedCount == (child ? 0 : 1)
                && acknowledged.RemovalAttemptCount == (child ? 0 : 1), name + ": suppressed completion has no pending display/view state");

            history = [Idle("msg_later", 3), Idle("msg_done", 2)];
            await monitor.PollOnceAsync();
            Check(completions == (child ? 0 : 2), name + ": cached ancestry survives later metadata failure");
            Check(service.ObserveTrustedOpenCodeMessages(source, "ses_test", [history[0]], false).Count == 0,
                name + ": later completion also cannot replay");

            if (child)
            {
                failInfo = false;
                pending = true;
                await monitor.PollOnceAsync();
                var interactions = observations.Where(o => o.Kind == OpenCodeNotificationObservationKind.Interaction)
                    .Select(o => o.Interaction!).ToArray();
                Check(plans.Count == 2 && interactions.Any(r => r.Kind == NotificationNavigationKind.Permission
                    && r.Status == NotificationInteractionStatus.Submitted), name + ": child permission still submits a toast");
                Check(interactions.Any(r => r.Kind == NotificationNavigationKind.Form
                    && r.Status == NotificationInteractionStatus.Submitted), name + ": child question/form still submits a toast");
                Check(completions == 0 && removed.Count == 0, name + ": interactions create no completion or completion removal");
                await monitor.PollOnceAsync();
                Check(plans.Count == 2, name + ": unchanged child interactions remain deduplicated");
            }
            Check(writes == 0, name + ": observation and suppression never mutate the backend");
        }
    }
}
