using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.LinkChips;

internal static partial class Program
{
    /// <summary>Offline stand-in for api.github.com / gitlab.com: every request is recorded, nothing leaves the process.</summary>
    private sealed class StatusHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond = _ => Task.FromResult(StatusJson("{}"));
        internal readonly ConcurrentQueue<HttpRequestMessage> Requests = new();
        private int _active;
        internal int MaxActive;
        internal int Count => Requests.Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref MaxActive)) && Interlocked.CompareExchange(ref MaxActive, now, seen) != seen) { }
            try { return await Respond(request).WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static HttpResponseMessage StatusJson(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static OpenCodeLinkChip LinkChip(string url) => OpenCodeLinkChipParser.Parse(url)!;

    private static async Task LinkStatus()
    {
        var before = checks;
        string? Api(string url) => OpenCodeLinkStatusService.ApiUri(LinkChip(url))?.AbsoluteUri;

        // ---- link → API address (only github.com / gitlab.com, only validated segments)
        Check(Api("https://github.com/alvinunreal/oh-my-opencode-slim/issues/1591") == "https://api.github.com/repos/alvinunreal/oh-my-opencode-slim/issues/1591", "GitHub issue API");
        Check(Api("https://github.com/sst/opencode/pull/4821") == "https://api.github.com/repos/sst/opencode/pulls/4821"
            && Api("https://www.github.com/sst/opencode/pulls/4821/files") == "https://api.github.com/repos/sst/opencode/pulls/4821", "GitHub PR uses /pulls/{n} (merged flag)");
        Check(Api("https://gitlab.com/g/p/-/issues/9") == "https://gitlab.com/api/v4/projects/g%2Fp/issues/9", "GitLab issue: encoded project path");
        Check(Api("https://gitlab.com/group/sub/proj/-/merge_requests/17") == "https://gitlab.com/api/v4/projects/group%2Fsub%2Fproj/merge_requests/17",
            "GitLab nested group: whole namespace is one %2F-encoded segment: " + Api("https://gitlab.com/group/sub/proj/-/merge_requests/17"));
        Check(Api("https://gitlab.com/a/b/c/d/-/issues/3#note_1") == "https://gitlab.com/api/v4/projects/a%2Fb%2Fc%2Fd/issues/3", "deep GitLab group, fragment dropped");
        var secret = Api("https://user:hunter2@github.com/o/r/issues/7?access_token=abc#x");
        Check(secret == "https://api.github.com/repos/o/r/issues/7", "user info, query and fragment never reach the API: " + secret);
        foreach (var url in new[]
        {
            "https://github.com/sst/opencode", "https://github.com/sst/opencode/commit/d38b26e9a1b2", "https://gitee.com/o/r/pulls/12",
            "https://codeberg.org/o/r/issues/3", "https://gitlab.example.com/g/p/-/issues/1", "https://github.com.evil.io/o/r/issues/1",
            "https://github.com/a%2F..%2Fb/r/issues/1", "https://github.com/o/r%3Fx=1/issues/1", "https://gitlab.com/g/p%2F..%2Fq/-/issues/1",
            "https://github.com/-bad/r/issues/1", "https://github.com/o/r/issues/0", "https://acme.atlassian.net/browse/OPS-42",
        })
            Check(Api(url) is null, "not looked up: " + url);
        Check(OpenCodeLinkStatusService.CacheKey(LinkChip("https://github.com/O/R/pull/1"))
            == OpenCodeLinkStatusService.CacheKey(LinkChip("https://github.com/o/r/pulls/1?w=1#diff")), "canonical cache key: case, pull/pulls, query, fragment");
        Check(OpenCodeLinkStatusService.CacheKey(LinkChip("https://github.com/o/r/issues/1"))
            != OpenCodeLinkStatusService.CacheKey(LinkChip("https://github.com/o/r/pull/1")), "issue and PR endpoints stay separate");

        // ---- each state mapping
        var ghIssue = LinkChip("https://github.com/o/r/issues/1");
        var ghPull = LinkChip("https://github.com/o/r/pull/2");
        var glIssue = LinkChip("https://gitlab.com/g/s/p/-/issues/3");
        var glMr = LinkChip("https://gitlab.com/g/s/p/-/merge_requests/4");
        OpenCodeLinkState? State(OpenCodeLinkChip chip, string json) => OpenCodeLinkStatusService.Parse(chip, json)?.State;
        Check(State(ghIssue, """{"title":"t","state":"open"}""") == OpenCodeLinkState.Open, "GitHub issue open");
        Check(State(ghIssue, """{"title":"t","state":"closed","state_reason":"completed"}""") == OpenCodeLinkState.Completed
            && State(ghIssue, """{"title":"t","state":"closed","state_reason":null}""") == OpenCodeLinkState.Completed, "GitHub issue completed (and legacy null reason)");
        Check(State(ghIssue, """{"title":"t","state":"closed","state_reason":"not_planned"}""") == OpenCodeLinkState.NotPlanned
            && State(ghIssue, """{"title":"t","state":"closed","state_reason":"duplicate"}""") == OpenCodeLinkState.NotPlanned, "GitHub issue not planned / duplicate");
        Check(State(ghIssue, """{"title":"t","state":"open","state_reason":"reopened"}""") == OpenCodeLinkState.Open, "reopened issue is open");
        Check(State(ghPull, """{"title":"t","state":"open","draft":false,"merged":false}""") == OpenCodeLinkState.Open, "GitHub PR open");
        Check(State(ghPull, """{"title":"t","state":"open","draft":true,"merged":false}""") == OpenCodeLinkState.Draft, "GitHub PR draft");
        Check(State(ghPull, """{"title":"t","state":"closed","draft":false,"merged":true}""") == OpenCodeLinkState.Merged, "GitHub PR merged");
        Check(State(ghPull, """{"title":"t","state":"closed","draft":false,"merged":false}""") == OpenCodeLinkState.Closed, "GitHub PR closed unmerged");
        Check(State(ghIssue, """{"title":"t","state":"closed","pull_request":{"merged_at":"2026-01-01T00:00:00Z"}}""") == OpenCodeLinkState.Merged
            && State(ghIssue, """{"title":"t","state":"closed","pull_request":{"merged_at":null}}""") == OpenCodeLinkState.Closed,
            "an /issues/N link that is a PR reads pull_request.merged_at");
        Check(State(glIssue, """{"title":"t","state":"opened"}""") == OpenCodeLinkState.Open && State(glIssue, """{"title":"t","state":"closed"}""") == OpenCodeLinkState.Completed, "GitLab issue opened / closed");
        Check(State(glMr, """{"title":"t","state":"opened","draft":false}""") == OpenCodeLinkState.Open, "GitLab MR opened");
        Check(State(glMr, """{"title":"t","state":"opened","draft":true}""") == OpenCodeLinkState.Draft
            && State(glMr, """{"title":"t","state":"opened","work_in_progress":true}""") == OpenCodeLinkState.Draft, "GitLab MR draft (and legacy WIP)");
        Check(State(glMr, """{"title":"t","state":"locked"}""") == OpenCodeLinkState.Open, "GitLab MR locked (merging) counts as open");
        Check(State(glMr, """{"title":"t","state":"merged"}""") == OpenCodeLinkState.Merged && State(glMr, """{"title":"t","state":"closed"}""") == OpenCodeLinkState.Closed, "GitLab MR merged / closed");
        foreach (var bad in new[] { """{"title":"t","state":"weird"}""", """{"state":"open"}""", """{"title":"  ","state":"open"}""", "[]", "not json", """{"title":1,"state":"open"}""" })
            Check(OpenCodeLinkStatusService.Parse(ghIssue, bad) is null, "unrecognised body → no state: " + bad);
        var spoofed = OpenCodeLinkStatusService.Parse(ghIssue, "{\"title\":\"Fix\\u202E\\u200B the\\r\\n\\tbuild\\u0007 \",\"state\":\"closed\"}")!;
        Check(spoofed.Title == "Fix the build", "title is one line without bidi / zero-width / control characters: " + spoofed.Title);
        var longTitle = OpenCodeLinkStatusService.Sanitize(new string('x', 1000));
        Check(longTitle.Length == OpenCodeLinkStatusService.MaxTitle && longTitle.EndsWith('…'), "title bounded");
        var states = Enum.GetValues<OpenCodeLinkState>();
        Check(states.Select(s => new OpenCodeLinkStatus(s, "t").StateText).Distinct().Count() == states.Length, "every state has its own word");

        // ---- Narrator
        var named = ghIssue.AccessibleNameWith(new OpenCodeLinkStatus(OpenCodeLinkState.Completed, "Fix the build").Spoken);
        Check(named == "链接，GitHub issue，r 1，已完成：Fix the build", "Narrator name carries state and title: " + named);
        Check(ghIssue.AccessibleNameWith(null) == ghIssue.AccessibleName, "no state → today's Narrator name");
        var credName = LinkChip("https://u:p@github.com/o/r/pull/5").AccessibleNameWith("已合并：x");
        Check(credName.EndsWith("，已合并：x，包含用户名或令牌", StringComparison.Ordinal), "state precedes the credentials warning: " + credName);

        // ---- request hygiene, cache and TTL
        var now = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var handler = new StatusHandler { Respond = _ => Task.FromResult(StatusJson("""{"title":"Live title","state":"open"}""")) };
        var service = new OpenCodeLinkStatusService(handler, () => now);
        var first = await service.GetAsync(LinkChip("https://user:tok@github.com/o/r/issues/1?token=abc"), true);
        Check(first is { State: OpenCodeLinkState.Open, Title: "Live title" } && handler.Count == 1, "fetch maps live title and state");
        handler.Requests.TryPeek(out var sent);
        Check(sent!.RequestUri!.AbsoluteUri == "https://api.github.com/repos/o/r/issues/1" && sent.Headers.Authorization is null
            && !sent.Headers.Contains("Cookie") && sent.Headers.UserAgent.ToString().Contains("FluentCode", StringComparison.Ordinal)
            && sent.Headers.Accept.ToString().Contains("application/vnd.github+json", StringComparison.Ordinal),
            "anonymous GET with a User-Agent, no credentials or cookies, nothing from the link but owner/repo/number");
        Check(await service.GetAsync(ghIssue, true) == first && handler.Count == 1, "cache hit: no second request");
        Check(service.TryGetCached(ghIssue, true, out var hit) && hit == first, "realized chip can apply a cached state synchronously (no flash)");
        now += TimeSpan.FromMinutes(9);
        Check(await service.GetAsync(ghIssue, true) == first && handler.Count == 1, "still fresh at 9 minutes");
        now += TimeSpan.FromMinutes(1);
        Check(!service.TryGetCached(ghIssue, true, out _), "expired at the 10-minute TTL");
        handler.Respond = _ => Task.FromResult(StatusJson("""{"title":"Live title","state":"closed","state_reason":"not_planned"}"""));
        Check((await service.GetAsync(ghIssue, true))?.State == OpenCodeLinkState.NotPlanned && handler.Count == 2, "after TTL the state is fetched again");

        // ---- setting off: no requests, no cached state shown
        Check(await service.GetAsync(LinkChip("https://github.com/o/r/issues/99"), false) is null && handler.Count == 2, "setting off → no request");
        Check(!service.TryGetCached(ghIssue, false, out _), "setting off → cached state is not used");
        service.Clear();
        Check(service.Count == 0 && !service.TryGetCached(ghIssue, true, out _), "turning the setting off clears the cache");
        Check(await service.GetAsync(LinkChip("https://github.com/sst/opencode"), true) is null && handler.Count == 2, "non-issue links never ask");

        // ---- in-flight dedupe
        var gate = Signal<bool>();
        var dedupe = new StatusHandler { Respond = async _ => { await gate.Task; return StatusJson("""{"title":"PR","state":"closed","merged":true}"""); } };
        var shared = new OpenCodeLinkStatusService(dedupe, () => now);
        var waiting = Enumerable.Range(0, 6).Select(i => shared.GetAsync(LinkChip(i % 2 == 0 ? "https://github.com/o/r/pull/2" : "https://github.com/O/R/pulls/2"), true)).ToArray();
        await Task.Delay(50);
        gate.TrySetResult(true);
        var results = await Task.WhenAll(waiting);
        Check(dedupe.Count == 1 && results.All(r => r is { State: OpenCodeLinkState.Merged }), "concurrent chips for one PR share one request: " + dedupe.Count);
        using (var cancel = new CancellationTokenSource())
        {
            var slow = Signal<bool>();
            dedupe.Respond = async _ => { await slow.Task; return StatusJson("""{"title":"t","state":"open"}"""); };
            var gone = shared.GetAsync(LinkChip("https://github.com/o/r/issues/40"), true, cancel.Token);
            var stay = shared.GetAsync(LinkChip("https://github.com/o/r/issues/40"), true);
            cancel.Cancel();
            Check(await gone is null, "a cancelled waiter gets nothing and never throws");
            slow.TrySetResult(true);
            Check((await stay)?.State == OpenCodeLinkState.Open && dedupe.Count == 2, "the shared fetch still completes for the other chip");
        }
        var burst = Signal<bool>();
        var many = new StatusHandler { Respond = async _ => { await burst.Task; return StatusJson("""{"title":"t","state":"open"}"""); } };
        var limited = new OpenCodeLinkStatusService(many, () => now);
        var flood = Enumerable.Range(1, 12).Select(i => limited.GetAsync(LinkChip("https://github.com/o/r/issues/" + i), true)).ToArray();
        await Task.Delay(100);
        burst.TrySetResult(true);
        await Task.WhenAll(flood);
        Check(many.Count == 12 && many.MaxActive <= OpenCodeLinkStatusService.MaxConcurrent, "a long message never opens more than 4 requests at once: " + many.MaxActive);

        // ---- rate limits: back off per API host until the reset
        var reset = now + TimeSpan.FromMinutes(20);
        var rate = new StatusHandler
        {
            Respond = r => Task.FromResult(r.RequestUri!.Host == "api.github.com"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Headers = { { "x-ratelimit-remaining", "0" }, { "x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString() } } }
                : StatusJson("""{"title":"MR","state":"merged"}""")),
        };
        var backoff = new OpenCodeLinkStatusService(rate, () => now);
        Check(await backoff.GetAsync(ghIssue, true) is null && backoff.IsBackingOff("api.github.com") && rate.Count == 1, "403 rate limit → no state, host backs off");
        Check(await backoff.GetAsync(ghPull, true) is null && await backoff.GetAsync(LinkChip("https://github.com/x/y/issues/5"), true) is null
            && rate.Count == 1, "while backing off, no GitHub request at all");
        Check((await backoff.GetAsync(glMr, true))?.State == OpenCodeLinkState.Merged && rate.Count == 2, "GitLab is not blocked by GitHub's limit");
        Check(!backoff.TryGetCached(ghIssue, true, out _), "a limited reply is not cached as the link's state");
        now = reset + TimeSpan.FromSeconds(1);
        rate.Respond = _ => Task.FromResult(StatusJson("""{"title":"t","state":"open"}"""));
        Check((await backoff.GetAsync(ghIssue, true))?.State == OpenCodeLinkState.Open && rate.Count == 3, "after the reset time requests resume");
        HttpResponseMessage Limited(HttpStatusCode code, params (string Name, string Value)[] headers)
        {
            var response = new HttpResponseMessage(code);
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
        Check(OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.TooManyRequests, ("Retry-After", "120")), now) == now + TimeSpan.FromSeconds(120), "429 Retry-After seconds");
        Check(OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.TooManyRequests, ("RateLimit-Reset", (now + TimeSpan.FromMinutes(3)).ToUnixTimeSeconds().ToString())), now)
            == now + TimeSpan.FromMinutes(3), "GitLab RateLimit-Reset epoch");
        Check(OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.Forbidden), now) == now + OpenCodeLinkStatusService.DefaultBackoff, "403 without hints → default backoff");
        Check(OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.OK, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds().ToString())), now)
            == now + TimeSpan.FromMinutes(5), "a spent quota on a 200 also waits for the reset");
        Check(OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.OK, ("x-ratelimit-remaining", "12")), now) is null
            && OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.NotFound), now) is null, "ordinary replies do not back off");
        Check(OpenCodeLinkStatusService.BackoffUntil(Limited(HttpStatusCode.Forbidden, ("Retry-After", "999999")), now) <= now + TimeSpan.FromHours(2), "backoff is bounded");
        var quota = new StatusHandler
        {
            Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"title":"last","state":"open"}""", Encoding.UTF8, "application/json"),
                Headers = { { "x-ratelimit-remaining", "0" }, { "x-ratelimit-reset", (now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds().ToString() } },
            }),
        };
        var spent = new OpenCodeLinkStatusService(quota, () => now);
        Check((await spent.GetAsync(ghIssue, true))?.Title == "last" && spent.IsBackingOff("api.github.com")
            && await spent.GetAsync(ghPull, true) is null && quota.Count == 1, "the last allowed reply is used, then the host waits");

        // ---- failures: silent, cached, the chip stays as it is
        var failures = new (string Name, Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond)[]
        {
            ("404 private or missing", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))),
            ("500", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))),
            ("301 renamed repository (redirects are not followed)", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://evil.example/x") } })),
            ("network error", _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))),
            ("timeout", _ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"))),
            ("malformed JSON", _ => Task.FromResult(StatusJson("{\"title\":"))),
            ("HTML error page", _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>", Encoding.UTF8, "text/html") })),
            ("oversized body", _ => Task.FromResult(StatusJson("{\"title\":\"t\",\"state\":\"open\",\"body\":\"" + new string('x', OpenCodeLinkStatusService.MaxResponseBytes) + "\"}"))),
        };
        foreach (var (name, respond) in failures)
        {
            var failing = new StatusHandler { Respond = respond };
            var failed = new OpenCodeLinkStatusService(failing, () => now);
            var chip = LinkChip("https://github.com/o/r/issues/7");
            var label = chip.Label;
            var spoken = chip.AccessibleName;
            Check(await failed.GetAsync(chip, true) is null && chip.Label == label && chip.AccessibleName == spoken, "failure → no state, chip unchanged: " + name);
            Check(failed.TryGetCached(chip, true, out var negative) && negative is null && await failed.GetAsync(chip, true) is null && failing.Count == 1,
                "failure cached for the TTL → no retry storm: " + name);
            Check(!failed.IsBackingOff("api.github.com"), "an ordinary failure does not block other links: " + name);
        }

        // ---- production wiring (source checks; WinUI is not executed here)
        var repo = Environment.GetEnvironmentVariable("FC_SPLASH_REPO")!;
        string Src(string relative) => File.ReadAllText(Path.Combine(repo, "src/FluentCode", relative));
        var view = Src("Pages/OpenCode/OpenCodeLinkChipView.cs");
        var status = Src("Pages/OpenCode/OpenCodeLinkChipView.Status.cs");
        Check(view.Contains("AttachStatus(button, icon, chip, dark);") && view.Contains("AddStatus(panel, chip);"), "transcript chip and hover card both host the state");
        Check(status.Contains("button.Loaded +=") && status.Contains("TryGetCached") && status.Contains("icon.Child = CreateStateIcon")
            && status.Contains("Visibility = Visibility.Collapsed") && status.Contains("AccessibleNameWith(status.Spoken)"),
            "lazy on realize, cached swap in the same icon box, collapsed card slot until known, Narrator name with state");
        Check(status.Contains("Monochrome") && status.Contains("is { } status) Apply(status)"), "High Contrast / monochrome glyphs; only a known state changes the chip");
        var settings = Src("Pages/Settings/SettingsView.LinkChips.cs");
        var model = Src("Models/AppSettings.cs");
        Check(settings.Contains("IssueStatus = _linkIssueStatusToggle.IsOn") && settings.Contains("OpenCodeLinkStatusService.Shared.Clear()")
            && model.Contains("public bool IssueStatus { get; init; } = true;"), "setting persists, defaults on, and clears the cache when turned off");
        Console.WriteLine($"PASS link status: {checks - before} assertions; API addresses, state mapping, cache/TTL, dedupe, concurrency, rate-limit backoff, setting off, failures.");
    }
}
