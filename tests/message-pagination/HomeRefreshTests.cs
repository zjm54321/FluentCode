using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static async Task HomeRefreshRegressions(Action<bool, string>? check = null)
    {
        check ??= Check;
        var durable = new OpenCodeSessionInfo { Id = "ses_unopened", Outcome = "failed", Time = new() { Updated = 200, Idle = 200, Viewed = 100 } };
        var mark = OpenCodePage.TestAdaptHomeStatus(null, durable);
        check(mark is { IsUnread: true, Outcome: "failed", CompletedAt: 200 }, "home.unopened-completion: durable outcome/idle/viewed reaches Home without notification tracking");
        var viewed = new OpenCodeSessionUiState("ses_unopened", false, false, false, new("failed", 200), 200, OpenCodeStateUnknownReason.None);
        check(OpenCodePage.TestAdaptHomeStatus(viewed, durable)?.IsUnread == false, "home.viewed-monotonic: an accepted local viewed receipt beats older server metadata");
        check(OpenCodePage.TestAdaptHomeStatus(viewed, durable with { Outcome = "succeeded", Time = new() { Updated = 300, Idle = 300, Viewed = 200 } })
            is { IsUnread: true, Outcome: "succeeded", CompletedAt: 300 }, "home.newer-completion: fresh metadata replaces an older app completion watermark");

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            var old = Enumerable.Range(0, 100).Select(i => HomeRefreshServer.Info("ses_" + i.ToString("D3"), 1000 - i)).ToArray();
            foreach (var row in old) server.Put(row);
            var page = server.Page(client); page.TestSeedCatalog(old, "cursor_100");
            server.Put(HomeRefreshServer.Info("ses_new", 5000));
            await page.TestCatalogRefresh();
            check(page.TestCatalogCount == 101 && page.TestHomeRootTitle("ses_099") is not null,
                "home.loaded-depth: a new first-page root cannot evict the already loaded displaced last row");
            check(server.MaxConcurrent == 1 && server.ListReads == 2 && server.InfoReads == 1,
                "home.depth-budget: re-read only loaded pages plus the exact displaced fringe, with one HTTP read at a time");
            check(page.TestCatalogCursor == "cursor_100", "home.cursor-frontier: reconciliation keeps a usable refreshed depth cursor");
        }

        foreach (var kind in new[] { "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted", "session.viewed", "session.moved", "session.archived", "session.unarchived", "session.forked", "session.metadata.updated", "session.usage.updated" })
        {
            using var server = new HomeRefreshServer(); using var client = server.CreateClient();
            var original = HomeRefreshServer.Info("ses_root", 100);
            server.Put(original);
            var page = server.Page(client); var stream = page.TestBeginEvents(); page.TestSeedCatalog([original]);
            page.TestEnableHomeRefresh(); await page.TestHomeRefreshCompletion();
            var changed = kind switch
            {
                "session.execution.failed" => original with { Outcome = "failed", Time = new() { Created = 1, Updated = 200, Idle = 200 } },
                "session.execution.interrupted" => original with { Outcome = "interrupted", Time = new() { Created = 1, Updated = 200, Idle = 200 } },
                "session.execution.succeeded" => original with { Outcome = "succeeded", Time = new() { Created = 1, Updated = 200, Idle = 200 } },
                "session.viewed" => original with { Outcome = "succeeded", Time = new() { Created = 1, Updated = 200, Idle = 200, Viewed = 200 } },
                "session.moved" => original with { Location = new() { Directory = "/other-project/worktree" }, Time = new() { Created = 1, Updated = 200 } },
                "session.archived" => original with { Time = new() { Created = 1, Updated = 200, Archived = 200 } },
                "session.forked" => HomeRefreshServer.Info("ses_fork", 200, "/other-project/worktree"),
                _ => original with { Title = "server updated", Time = new() { Created = 1, Updated = 200 } },
            };
            if (kind == "session.unarchived") page.TestSeedCatalog([]);
            server.Put(changed);
            page.TestReceive(stream, HomeEvent(kind, kind == "session.forked" ? "ses_root" : changed.Id!));
            page.TestHomeTimer(makeDue: true); await page.TestHomeRefreshCompletion();
            var row = page.TestHomeRow(changed.Id!);
            var followed = kind switch
            {
                "session.archived" => row is null,
                "session.moved" => row?.Session.Project.Directory == "/other-project/worktree",
                "session.viewed" => row is { IsUnread: false } && row.Session.Status?.CompletedAt == 200,
                "session.execution.succeeded" or "session.execution.failed" or "session.execution.interrupted"
                    => row?.Session.Status is { Working: false, IsUnread: true, CompletedAt: 200 } && row.Session.Status.Outcome == changed.Outcome,
                _ => row?.Session.Info.Title == changed.Title && row?.Updated?.ToUnixTimeMilliseconds() == 200,
            };
            check(followed, "home.native-" + kind + ": an unopened/global-directory root follows the server's accepted metadata");
            check(server.MaxConcurrent == 1 && server.NonGetReads == 0, "home.read-only-" + kind + ": event reconciliation is single-flight and never sends prompts/ACK/mutations");
        }

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            foreach (var i in Enumerable.Range(0, 150)) server.Put(HomeRefreshServer.Info("ses_" + i.ToString("D3"), 1000 - i));
            server.SetActive("ses_149", true);
            var page = server.Page(client); page.TestSeedCatalog(server.Rows.Take(50));
            page.TestEnableHomeRefresh(); await page.TestHomeRefreshCompletion();
            await page.TestActiveAsync();
            check(page.TestHomeRow("ses_149")?.Session.Status?.Working == true && page.TestCatalogCount == 51,
                "home.active-beyond-pages: foreground status discovers a root older than loaded pages and its real project");
        }

        foreach (var boundary in new[] { "home-return", "window-return", "reconnect", "disconnected-fallback", "healthy-foreground" })
        {
            using var server = new HomeRefreshServer(); using var client = server.CreateClient();
            var original = HomeRefreshServer.Info("ses_root", 100); server.Put(original); server.SetActive("ses_root", true);
            var page = server.Page(client); var stream = page.TestBeginEvents(); page.TestSeedCatalog([original]);
            page.TestEnableHomeRefresh(); await page.TestHomeRefreshCompletion();
            await page.TestActiveAsync(); // Also seed the base fixture's existing status publisher.
            if (boundary == "healthy-foreground") { page.TestReady(stream); await page.TestHomeRefreshCompletion(); }
            if (boundary == "home-return") page.TestLeaveHome();
            if (boundary == "window-return") page.NotifyHostWindowVisibility(false);
            server.Put(original with { Outcome = "succeeded", Time = new() { Created = 1, Updated = 200, Idle = 200 } });
            server.SetActive("ses_root", false); // Deliberately no lifecycle event: simulate a missed stream interval.
            if (boundary == "home-return") page.TestReturnHome();
            else if (boundary == "window-return") page.NotifyHostWindowVisibility(true);
            else if (boundary == "reconnect") page.TestReady(stream, reconnect: true);
            else if (boundary == "healthy-foreground") page.TestHomeTimer(makeDue: true, statusDue: true);
            else { page.TestDisconnected(stream); page.TestHomeTimer(makeDue: true); }
            await page.TestHomeRefreshCompletion();
            check(page.TestHomeRow("ses_root")?.Session.Status is { Working: false, Outcome: "succeeded", CompletedAt: 200 },
                "home." + boundary + ": missed working-to-idle metadata/status is repaired without a selection change");
        }

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            var original = HomeRefreshServer.Info("ses_root", 100); server.Put(original);
            var page = server.Page(client); page.TestSeedCatalog([original], "cursor_50");
            page.NotifyHostWindowVisibility(false);
            await page.TestCatalogRefresh(); await page.TestSessionPage(); await page.TestActiveAsync(); page.TestHomeTimer(makeDue: true);
            check(server.TotalReads == 0, "home.hidden: no first-page, paging, status or Home timer requests while hidden");
        }

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            var original = HomeRefreshServer.Info("ses_root", 100); server.Put(original);
            var page = server.Page(client); page.TestSeedCatalog([original], "cursor_50");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeReply = route => { if (route == "list" && server.ListReads == 1) { entered.TrySetResult(); return release.Task; } return Task.CompletedTask; };
            var paging = page.TestSessionPage(); await entered.Task;
            var refresh = page.TestCatalogRefresh();
            check(server.ListReads == 1 && server.MaxConcurrent == 1, "home.paging-flight: catalog refresh waits for the existing cursor HTTP request");
            release.TrySetResult(); await Task.WhenAll(paging, refresh);
            check(server.MaxConcurrent == 1, "home.shared-flight: first page, depth, keyed metadata, active status and paging never overlap");
        }

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            var original = HomeRefreshServer.Info("ses_root", 100); server.Put(original);
            var page = server.Page(client); page.TestSeedCatalog([original]);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeReply = route => { if (route == "list") { entered.TrySetResult(); return release.Task; } return Task.CompletedTask; };
            var first = page.TestCatalogRefresh(); await entered.Task;
            var same = page.TestCatalogRefresh();
            check(server.ListReads == 1, "home.refresh-coalescing: concurrent same-source refresh callers share one read");
            page.TestReplaceSource(); release.TrySetResult(); await Task.WhenAll(first, same);
            check(page.MetadataApplications == 0 && ReferenceEquals(page.TestHomeRow("ses_root")?.Session.Info, original),
                "home.source-fence: an old source/revision response cannot publish rows into a replacement binding");
        }

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            var original = HomeRefreshServer.Info("ses_root", 100); server.Put(original);
            var page = server.Page(client); var stream = page.TestBeginEvents(); page.TestSeedCatalog([original]);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.BeforeReply = route => { if (route == "list") { entered.TrySetResult(); return release.Task; } return Task.CompletedTask; };
            var refresh = page.TestCatalogRefresh(); await entered.Task;
            server.Remove("ses_root"); page.TestReceive(stream, HomeEvent("session.deleted", "ses_root"));
            release.TrySetResult(); await refresh;
            check(page.TestHomeRow("ses_root") is null, "home.delete-race: a newer native deletion is not resurrected by the held older list response");
        }

        using (var server = new HomeRefreshServer())
        using (var client = server.CreateClient())
        {
            var original = HomeRefreshServer.Info("ses_root", 100); server.Put(original);
            var page = server.Page(client); var stream = page.TestBeginEvents(); page.TestSeedCatalog([original]);
            page.TestEnableHomeRefresh(); await page.TestHomeRefreshCompletion();
            var retained = page.TestHomeRow("ses_root")!.Session.Info;
            server.FailInfo = true; page.TestReceive(stream, HomeEvent("session.metadata.updated", "ses_root"));
            page.TestHomeTimer(makeDue: true); await page.TestHomeRefreshCompletion();
            var reads = server.TotalReads;
            page.TestHomeTimer(); page.TestHomeTimer(); await page.TestHomeRefreshCompletion();
            check(page.TestHomeReadFailures == 1 && page.TestHomeRetryAt > DateTimeOffset.UtcNow && server.TotalReads == reads
                && ReferenceEquals(page.TestHomeRow("ses_root")?.Session.Info, retained),
                "home.failure-backoff: a failed keyed read retains rows and does not spin/retry before its due time");
            server.FailInfo = false; server.Put(original with { Title = "recovered" });
            page.TestHomeTimer(makeDue: true); await page.TestHomeRefreshCompletion();
            check(page.TestHomeReadFailures == 0 && page.TestHomeRootTitle("ses_root") == "recovered", "home.failure-recovery: the retained dirty ID repairs after bounded retry");
        }
    }

    private static async Task HomeBaseCounterexamples()
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        void Observe(bool condition, string message)
        {
            if (condition) return;
            var id = message.Split(':', 2)[0];
            failed.Add(id);
        }
        await HomeRefreshRegressions(Observe);
        HomeViewRefreshRegressions(Observe);
        var expected = new[]
        {
            "home.unopened-completion", "home.newer-completion", "home.loaded-depth",
            "home.native-session.execution.succeeded", "home.native-session.execution.failed", "home.native-session.execution.interrupted",
            "home.native-session.viewed", "home.native-session.moved", "home.native-session.archived", "home.native-session.unarchived",
            "home.native-session.forked", "home.native-session.metadata.updated", "home.native-session.usage.updated",
            "home.active-beyond-pages", "home.home-return", "home.window-return", "home.reconnect", "home.disconnected-fallback", "home.healthy-foreground",
            "home.hidden", "home.paging-flight", "home.shared-flight", "home.refresh-coalescing", "home.source-fence",
            "home.row-identity", "home.row-terminal", "home.row-focus", "home.viewport-anchor", "home.open-menu", "home.menu-revision",
            "home.project-ring", "home.calendar", "home.moved-row-identity", "home.project-identity",
        };
        foreach (var id in expected)
        {
            Check(failed.Contains(id), "Wave base must reproduce the behavioral counterexample: " + id);
            Console.WriteLine("BASE_STALE " + id);
        }
        Console.WriteLine("PASS BASE 9ef16e2: " + expected.Length + " behavioral counterexamples reproduced; compilation succeeded; identical synthetic inputs/native seams.");
    }

    private static OpenCodeEvent HomeEvent(string kind, string id)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { sessionID = id }));
        return new(kind, data.RootElement.Clone(), DateTimeOffset.UtcNow);
    }

    private sealed class HomeRefreshServer : IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, OpenCodeSessionInfo> _rows = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private int _inFlight;
        internal int MaxConcurrent, ListReads, InfoReads, ActiveReads, NonGetReads;
        internal Func<string, Task> BeforeReply = _ => Task.CompletedTask;
        internal bool FailInfo;
        internal int TotalReads => ListReads + InfoReads + ActiveReads;
        internal IReadOnlyList<OpenCodeSessionInfo> Rows { get { lock (_sync) return _rows.Values.OrderByDescending(row => row.Time!.Updated).ToArray(); } }
        internal void Put(OpenCodeSessionInfo info) { lock (_sync) _rows[info.Id!] = info; }
        internal void Remove(string id) { lock (_sync) _rows.Remove(id); }
        internal void SetActive(string id, bool value) { lock (_sync) { if (value) _active.Add(id); else _active.Remove(id); } }
        internal static OpenCodeSessionInfo Info(string id, double updated, string directory = "/project/worktree")
            => new() { Id = id, ProjectId = "p", Title = id, Location = new() { Directory = directory }, Time = new() { Created = 1, Updated = updated } };
        internal OpenCodePage Page(OpenCodeClient client)
        {
            var page = new OpenCodePage(); page.TestActivate("ses_other", client, CreateClient); return page;
        }
        internal OpenCodeClient CreateClient() => new(new HttpClient(new HomeRefreshHandler(this)),
            new() { BaseUrl = "https://home-refresh.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        public void Dispose() { }

        private static object Wire(OpenCodeSessionInfo info)
        {
            var time = new Dictionary<string, object> { ["created"] = info.Time!.Created, ["updated"] = info.Time.Updated };
            if (info.Time.Idle is { } idle) time["idle"] = idle;
            if (info.Time.Viewed is { } viewed) time["viewed"] = viewed;
            if (info.Time.Archived is { } archived) time["archived"] = archived;
            var row = new Dictionary<string, object> { ["id"] = info.Id!, ["projectID"] = "p", ["title"] = info.Title!, ["cost"] = 0,
                ["tokens"] = new { }, ["time"] = time, ["location"] = new { directory = info.Location!.Directory! } };
            if (info.Outcome is { } outcome) row["outcome"] = outcome;
            if (info.ParentId is { } parent) row["parentID"] = parent;
            return row;
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            string route, json;
            HttpStatusCode status = HttpStatusCode.OK;
            lock (_sync)
            {
                _inFlight++; MaxConcurrent = Math.Max(MaxConcurrent, _inFlight);
                if (request.Method != HttpMethod.Get) NonGetReads++;
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/api/session/active")
                {
                    route = "active"; ActiveReads++;
                    json = JsonSerializer.Serialize(new { data = _active.ToDictionary(id => id, _ => new { type = "running" }) });
                }
                else if (path == "/api/session")
                {
                    route = "list"; ListReads++;
                    var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
                    Check(query.GetValueOrDefault("parentID") == "null" && query.GetValueOrDefault("limit") == "50"
                        && (!query.ContainsKey("cursor") || !query.ContainsKey("order")), "Home reconciliation keeps the real roots/limit/opaque-cursor contract");
                    var offset = query.TryGetValue("cursor", out var cursor) ? int.Parse(cursor[7..]) : 0; // Fake server only; client never parses.
                    var rows = _rows.Values.Where(row => row.Time?.Archived is null).OrderByDescending(row => row.Time!.Updated).ToArray();
                    var data = rows.Skip(offset).Take(50).Select(Wire).ToArray();
                    json = JsonSerializer.Serialize(new { data, cursor = new { next = offset + data.Length < rows.Length ? "cursor_" + (offset + data.Length) : null } });
                }
                else if (path.StartsWith("/api/session/ses_", StringComparison.Ordinal))
                {
                    route = "info"; InfoReads++;
                    if (FailInfo) { status = HttpStatusCode.ServiceUnavailable; json = "{}"; }
                    else if (_rows.TryGetValue(path[13..], out var info)) json = JsonSerializer.Serialize(new { data = Wire(info) });
                    else { status = HttpStatusCode.NotFound; json = "{}"; }
                }
                else throw new InvalidOperationException("Unexpected Home refresh route " + path);
            }
            try
            {
                await BeforeReply(route); // Capture the wire response first, to reproduce a genuinely stale held GET.
                return new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            finally { lock (_sync) _inFlight--; }
        }
        private sealed class HomeRefreshHandler(HomeRefreshServer owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.Send(request, ct);
        }
    }
}
