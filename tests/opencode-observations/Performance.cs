using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

static partial class Checks
{
    private static async Task IncrementalHistoryAndBackoff()
    {
        var messages = Enumerable.Range(1, 300).Reverse().Select(i => Idle("msg_perf_" + i, i)).ToList();
        int reads = 0, pendingReads = 0;
        bool running = false;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (path.EndsWith("/active")) return Task.FromResult(Json(new { data = running
                ? new Dictionary<string, object> { ["ses_test"] = new { type = "running" } } : new() }));
            if (path.EndsWith("/message"))
            {
                reads++;
                var cursor = System.Web.HttpUtility.ParseQueryString(uri.Query)["cursor"];
                int offset = cursor is null ? 0 : int.Parse(cursor);
                var data = messages.Skip(offset).Take(100).ToArray();
                return Task.FromResult(Json(new { data, cursor = new { next = offset + 100 < messages.Count ? (offset + 100).ToString() : null } }));
            }
            if (path.EndsWith("/permission")) pendingReads++;
            if (path == "/api/session/ses_test") return Task.FromResult(Json(new { data = new { id = "ses_test", outcome = "succeeded", time = new { idle = messages[0].Time!.Created } } }));
            return Task.FromResult(Json(new { data = Array.Empty<object>() }));
        }));
        using var client = Client(http);
        using var monitor = new OpenCodeNotificationMonitor((CompletionNotificationService?)null, new("perf-incremental", client));
        monitor.TrackSession("ses_test");
        OpenCodeSessionObservation? last = null;
        monitor.SessionObserved += (_, e) => last = e.Observation;
        await monitor.PollOnceAsync();
        Check(reads == 3 && last?.Projection.State == OpenCodeExecutionState.Idle, "cold history reads all 300 rows once");
        await monitor.PollOnceAsync();
        Check(reads == 4 && last?.Projection.State == OpenCodeExecutionState.Idle, "unchanged history stops after newest page and preserves complete proof");
        var permissions = pendingReads;
        await monitor.PollOnceAsync(scheduled: true);
        Check(reads == 4 && pendingReads == permissions, "scheduled idle poll backs off expensive per-session reads");
        running = true;
        await monitor.PollOnceAsync(scheduled: true);
        Check(reads == 5 && pendingReads > permissions && last?.Projection.State == OpenCodeExecutionState.Working, "active snapshot bypasses backoff without waiting 15 seconds");
        running = false; messages.Insert(0, Idle("msg_perf_301", 301));
        await monitor.PollOnceAsync(scheduled: true);
        Check(reads == 6 && last?.IdleWatermark == 301 && last.Projection.State == OpenCodeExecutionState.Idle, "newest-page delta merges at anchor and accepts actual new terminal");
        // Notification delivery anchors remain independent from this incremental observation cache.
        monitor.TrackSession("ses_test");
        await monitor.PollOnceAsync(scheduled: true);
        Check(reads == 7, "explicit session interaction wakes idle session immediately");
    }
}
