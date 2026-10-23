using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private sealed class BindingRead
    {
        internal readonly TaskCompletionSource<bool> Started = Signal<bool>();
        internal readonly TaskCompletionSource<HttpResponseMessage> Result = Signal<HttpResponseMessage>();
    }

    private sealed class BindingBackend : HttpMessageHandler
    {
        internal readonly Dictionary<string, string> Directories = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string?> Boundaries = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, List<OpenCodeMessage>> Rows = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, string?> Cursors = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, int> StateCount = new(StringComparer.Ordinal), MessageCount = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<BindingRead>> _held = new(StringComparer.Ordinal);
        internal BindingRead Hold(string id, bool messages)
        {
            var key = id + (messages ? "/message" : "");
            if (!_held.TryGetValue(key, out var queue)) _held[key] = queue = new();
            var read = new BindingRead(); queue.Enqueue(read); return read;
        }
        internal HttpResponseMessage State(string id) => new(HttpStatusCode.OK) { Content = new StringContent(
            StateResponse(Boundaries.GetValueOrDefault(id)).Replace("ses_test", id, StringComparison.Ordinal)
                .Replace(JsonSerializer.Serialize("/synthetic"), JsonSerializer.Serialize(Directories.GetValueOrDefault(id, "/" + id[4..])), StringComparison.Ordinal)) };
        internal HttpResponseMessage Messages(string id) => Json(new { data = Rows.GetValueOrDefault(id, new() { Live(id) }), cursor = new { next = Cursors.GetValueOrDefault(id) } });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/api/session/", StringComparison.Ordinal)) throw new InvalidOperationException(path);
            var key = path[13..]; var messages = key.EndsWith("/message", StringComparison.Ordinal);
            var id = messages ? key[..^8] : key;
            Check(!request.RequestUri.Query.Contains("directory", StringComparison.Ordinal), "message/state GET never invents a directory query parameter");
            var counts = messages ? MessageCount : StateCount;
            counts[id] = counts.GetValueOrDefault(id) + 1;
            if (_held.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var read = queue.Dequeue(); read.Started.TrySetResult(true);
                return read.Result.Task; // Deliberately ignore cancellation; real client still enforces its contract.
            }
            return Task.FromResult(messages ? Messages(id) : State(id));
        }
        internal OpenCodeClient Client() => new(new HttpClient(this), new() { BaseUrl = "https://offline.invalid" }, true);
    }

    private static async Task BindingFirstBodyWithoutNetworkWait()
    {
        foreach (var home in new[] { false, true })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            await page.TestSwitchAsync("ses_A", "/A"); await page.TestSwitchAsync("ses_B", "/B");
            var selected = home ? page.TestPrepareHomeClick("ses_A", "/A") : null;
            var state = backend.Hold("ses_A", false); var messages = backend.Hold("ses_A", true);
            var returning = home ? page.TestOpenHome(7, "/A", "ses_A") : page.TestSwitchAsync("ses_A", "/A");
            Check(!returning.IsCompleted && !state.Result.Task.IsCompleted && !messages.Result.Task.IsCompleted
                && LiveText(page) == "ses_A" && page.TestAppliedCount == 1,
                (home ? "Home consumer" : "direct switch") + " applies first actual cached text body synchronously while BOTH network responses remain held");
            if (home) Check(page.TestHomeMetadataIs(selected!) && page.HomeMirrors == 1,
                "accepted Home metadata routes directly to real binding and body restore");
            state.Result.SetResult(backend.State("ses_A"));
            await messages.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            messages.Result.SetResult(backend.Messages("ses_A")); await returning;
        }
    }

    private static async Task BindingColdReadOverlap()
    {
        var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
        var state = backend.Hold("ses_A", false); var messages = backend.Hold("ses_A", true);
        var load = page.TestSwitchAsync("ses_A", "/A");
        Console.WriteLine($"CONTROLLED cold while state held: messagesStarted={messages.Started.Task.IsCompleted}; applications={page.TestAppliedCount}");
        Check(state.Started.Task.IsCompleted && messages.Started.Task.IsCompleted,
            "cold state and messages GET must both start before either response completes (no serial state RTT)");
        messages.Result.SetResult(backend.Messages("ses_A"));
        Check(!load.IsCompleted && page.TestAppliedCount == 0 && page.TestEmptyText == "正在加载消息…",
            "completed speculative body cannot Apply before authoritative state validates directory and rollback fence");
        backend.Boundaries["ses_A"] = "msg_not_in_window";
        state.Result.SetResult(backend.State("ses_A")); await load;
        Check(page.TestLastApplied().Count == 0 && backend.MessageCount["ses_A"] == 1,
            "same-identity parallel response is reused once but unknown rollback side stays hidden");
    }

    private static async Task BindingCorrectedParallelRead()
    {
        foreach (var correction in new[] { "directory", "boundary", "unknown directory" })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            if (correction == "boundary")
            {
                await page.TestSwitchAsync("ses_A", "/A"); await page.TestSwitchAsync("ses_B", "/B");
                backend.Boundaries["ses_A"] = "msg_cut";
            }
            if (correction == "directory") backend.Directories["ses_A"] = "/corrected";
            var state = backend.Hold("ses_A", false); var stale = backend.Hold("ses_A", true); var fresh = backend.Hold("ses_A", true);
            var load = page.TestSwitchAsync("ses_A", correction == "unknown directory" ? null : "/A");
            Check(state.Started.Task.IsCompleted && stale.Started.Task.IsCompleted, correction + " starts concurrent read under original owner");
            var epoch = page.TestEpoch;
            state.Result.SetResult(backend.State("ses_A")); await fresh.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!load.IsCompleted && page.TestAppliedCount == 0 && backend.MessageCount["ses_A"] >= 2,
                correction + " invalidates speculative read and immediately starts a fresh read without waiting for stale transport");
            if (correction != "unknown directory") Check(page.TestEpoch > epoch, correction + " authoritative correction advances history epoch");
            fresh.Result.SetResult(correction == "boundary"
                ? Json(new { data = new[] { new OpenCodeMessage { Id = "msg_cut", Type = "user" }, Live("validated body") }, cursor = new { next = (string?)null } })
                : Snapshot("validated body"));
            await load;
            Check(LiveText(page) == "validated body" && !page.TestRefreshBusy, correction + " only corrected-owner body applies");
            stale.Result.SetResult(Snapshot("stale body"));
            Check(LiveText(page) == "validated body" && page.Notices.Count == 0, correction + " abandoned response cannot publish over fresh success");
        }
    }

    private static async Task BindingParallelStateFences()
    {
        foreach (var invalidation in new[] { "state failure", "revision", "page", "source", "unload", "revocation", "workflow" })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var state = backend.Hold("ses_A", false); var messages = backend.Hold("ses_A", true);
            var load = page.TestSwitchAsync("ses_A", "/A");
            Check(state.Started.Task.IsCompleted && messages.Started.Task.IsCompleted, invalidation + " starts both held reads");
            if (invalidation == "revision") page.TestAdvanceRevisionOnly();
            if (invalidation == "page") page.TestAdvancePageGeneration();
            if (invalidation == "source") page.TestForeignSource();
            if (invalidation == "unload") page.TestUnload();
            if (invalidation == "revocation") page.TestRevokeSource();
            if (invalidation == "workflow") page.TestBeginSend(71);
            state.Result.SetResult(invalidation == "state failure"
                ? new(HttpStatusCode.OK) { Content = new StringContent("malformed") } : backend.State("ses_A"));
            await load.WaitAsync(TimeSpan.FromSeconds(2));
            Check(page.TestAppliedCount == 0 && !page.TestRefreshBusy && !messages.Result.Task.IsCompleted,
                invalidation + " rejects publication and releases refresh without waiting for ignored message cancellation");
            if (invalidation == "state failure") Check(page.TestNeedsState && page.TestEmptyText.Contains("未完成"),
                "state failure stays retryable, never successful empty or accepted history");
            messages.Result.SetException(new HttpRequestException("late abandoned speculative transport failure"));
        }
    }

    private static async Task BindingLateAfterSuccess()
    {
        foreach (var phase in new[] { "boundary", "messages" })
        foreach (var fail in new[] { false, true })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var held = backend.Hold("ses_A", phase == "messages"); var old = page.TestSwitchAsync("ses_A", "/A");
            await page.TestSwitchAsync("ses_B", "/B");
            var applied = page.TestAppliedCount;
            held.Result.SetResult(fail ? new(HttpStatusCode.ServiceUnavailable) : phase == "messages" ? backend.Messages("ses_A") : backend.State("ses_A"));
            await old;
            Check(LiveText(page) == "ses_B" && page.TestAppliedCount == applied && !page.TestRefreshBusy && !page.TestRefreshQueued && page.Notices.Count == 0,
                "old " + phase + (fail ? " error" : " success") + "/finally after new success cannot change body, loading, errors or owner");
        }
    }

    private static async Task BindingImmediateAndEmpty()
    {
        var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage();
        page.TestActivate("ses_seed", client);
        var boundary = backend.Hold("ses_A", false); var messages = backend.Hold("ses_A", true);
        var first = page.TestSwitchAsync("ses_A", "/A");
        Check(boundary.Started.Task.IsCompleted && page.TestRefreshBusy, "real Switch starts authoritative boundary immediately without poll tick");
        Check(page.TestEmptyText == "正在加载消息…", "slow boundary cannot flash empty");
        boundary.Result.SetResult(backend.State("ses_A")); await messages.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(page.TestEmptyText == "正在加载消息…", "slow messages cannot flash empty");
        messages.Result.SetResult(backend.Messages("ses_A")); await first;
        Check(LiveText(page) == "ses_A" && page.TestEmptyText == "" && !page.TestRefreshBusy, "first accepted nonempty window displays immediately");
        backend.Rows["ses_empty"] = new(); await page.TestNavigateAsync("ses_empty");
        Check(page.TestEmptyText == "该会话暂无消息。" && backend.MessageCount["ses_empty"] == 1, "real Navigate immediately reads; successful empty array alone enables empty text");
        var callbackLoading = false;
        var failure = backend.Hold("ses_failure", true); var failed = page.TestSwitchAsync("ses_failure", onBound: () => callbackLoading = page.TestEmptyText == "正在加载消息…");
        Check(callbackLoading, "binding callback already sees loading, never previous session's successful empty text");
        failure.Result.SetResult(new(HttpStatusCode.ServiceUnavailable)); await failed;
        Check(!page.TestEmptyText.Contains("暂无消息") && page.TestEmptyText.Contains("未完成"), "API failure remains retry/error, not successful empty");
        backend.Boundaries["ses_hidden"] = "msg_not_in_window"; await page.TestSwitchAsync("ses_hidden");
        Check(page.TestLastApplied().Count == 0 && page.TestEmptyText.Contains("当前没有可显示") && !page.TestEmptyText.Contains("暂无消息"),
            "nonempty response hidden by rollback projection is not an empty response");
    }

    private static async Task BindingLateOwners()
    {
        foreach (var phase in new[] { "boundary", "messages" })
        foreach (var fail in new[] { false, true })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var oldRead = backend.Hold("ses_A", phase == "messages"); var old = page.TestSwitchAsync("ses_A", "/A");
            Check(oldRead.Started.Task.IsCompleted, "old A message read held");
            var newRead = backend.Hold("ses_B", true); var fresh = page.TestSwitchAsync("ses_B", "/B");
            Check(newRead.Started.Task.IsCompleted && backend.StateCount["ses_B"] == 1, "held old message read never blocks B boundary/message GET");
            await page.TestLatestAsync();
            Check(page.TestRefreshQueued && backend.MessageCount["ses_B"] == 1, "same binding merges into one single-flight refresh");
            oldRead.Result.SetResult(fail ? new(HttpStatusCode.ServiceUnavailable) : phase == "messages" ? backend.Messages("ses_A") : backend.State("ses_A")); await old;
            Check(page.TestRefreshBusy && page.TestRefreshQueued && page.TestEmptyText == "正在加载消息…" && page.Notices.Count == 0,
                "old success/failure/finally cannot clear B busy, queue, loading or publish errors");
            newRead.Result.SetResult(backend.Messages("ses_B")); await fresh;
            Check(LiveText(page) == "ses_B" && !page.TestRefreshBusy && !page.TestRefreshQueued && backend.MessageCount["ses_B"] == 2,
                "only B owner drains its queued refresh after first accepted window");
        }
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var auxiliary = Signal<bool>(); page.Auxiliary = () => auxiliary.Task;
            var old = page.TestSwitchAsync("ses_A", "/A");
            Check(LiveText(page) == "ses_A" && page.TestEmptyText == "" && page.TestRefreshBusy, "first paint completes before held auxiliary work");
            page.Auxiliary = () => Task.CompletedTask;
            var held = backend.Hold("ses_B", true); var fresh = page.TestNavigateAsync("ses_B");
            Check(held.Started.Task.IsCompleted, "real Navigate bypasses previous binding's held auxiliary work");
            auxiliary.SetResult(true); await old;
            Check(page.TestRefreshBusy && page.TestEmptyText == "正在加载消息…", "auxiliary late finally cannot clear new refresh owner");
            held.Result.SetResult(backend.Messages("ses_B")); await fresh;
            Check(LiveText(page) == "ses_B", "auxiliary stale binding cannot replace B");
        }
    }

    private static async Task BindingWindowReuse()
    {
        var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
        await page.TestSwitchAsync("ses_A", "/A"); var older = page.TestOlderBuffer; var latest = page.TestLatestBuffer;
        await page.TestSwitchAsync("ses_B", "/B");
        var state = backend.Hold("ses_A", false); var back = page.TestSwitchAsync("ses_A", "/A");
        Check(LiveText(page) == "ses_A" && page.TestEmptyText == "" && state.Started.Task.IsCompleted,
            "A-B-A reuses accepted message window synchronously and still starts authoritative boundary");
        Check(ReferenceEquals(older, page.TestOlderBuffer) && ReferenceEquals(latest, page.TestLatestBuffer), "original two buffers transfer ownership; no parallel flattened message cache");
        Check(page.AckPublications == 0 && page.ExecutionPublications == 0, "visual reuse requests review but never ACKs without actual viewport proof or publishes execution observation");
        state.Result.SetResult(backend.State("ses_A")); await back;
        foreach (var id in new[] { "ses_C", "ses_D", "ses_E", "ses_F", "ses_G" }) await page.TestSwitchAsync(id, "/" + id[4..]);
        Check(page.TestCachedBindings <= 3 && page.TestCachedRows <= 600 && page.TestCachedBytes <= 2 * 1024 * 1024, "retained binding windows obey all three page budgets");
        state = backend.Hold("ses_A", false); back = page.TestSwitchAsync("ses_A", "/A");
        Check(page.TestEmptyText == "正在加载消息…", "LRU-evicted window cannot reappear");
        state.Result.SetResult(backend.State("ses_A")); await back;
        backend.Rows["ses_huge"] = Enumerable.Range(0, 601).Select(i => Live("row" + i) with { Id = "msg_" + i }).ToList();
        await page.TestSwitchAsync("ses_huge", "/huge");
        Check(page.TestRetainedRows.Count == 601, "cache row budget never truncates active/live pagination window");
        await page.TestSwitchAsync("ses_B", "/B"); var large = backend.Hold("ses_huge", false); var again = page.TestSwitchAsync("ses_huge", "/huge");
        Check(page.TestEmptyText == "正在加载消息…", "oversized live window is not retained as a cache slot");
        large.Result.SetResult(backend.State("ses_huge")); await again;
        backend.Rows["ses_bytes"] = new() { Live(new string('x', 2 * 1024 * 1024 + 1)) };
        await page.TestSwitchAsync("ses_bytes", "/bytes"); await page.TestSwitchAsync("ses_B", "/B");
        var bytes = backend.Hold("ses_bytes", false); var byteLoad = page.TestSwitchAsync("ses_bytes", "/bytes");
        Check(page.TestEmptyText == "正在加载消息…", "payload budget prevents retaining huge messages without affecting accepted live content");
        bytes.Result.SetResult(backend.State("ses_bytes")); await byteLoad;
    }

    private static async Task BindingFailureAndRapidReturn()
    {
        foreach (var kind in new[] { "transport", "timeout", "protocol", "cancel" })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var held = backend.Hold("ses_A", true); var load = page.TestSwitchAsync("ses_A", "/A");
            if (kind == "protocol") held.Result.SetResult(new(HttpStatusCode.OK) { Content = new StringContent("malformed") });
            else if (kind == "transport") held.Result.SetException(new HttpRequestException("offline fixture"));
            else if (kind == "cancel") held.Result.SetCanceled();
            else held.Result.SetException(new TaskCanceledException("fixture timeout"));
            await load;
            Check(page.TestEmptyText.Contains("未完成") && !page.TestEmptyText.Contains("暂无消息") && !page.TestRefreshBusy,
                kind + " cannot certify successful empty; current owner releases busy");
        }
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var first = backend.Hold("ses_A", true); var old = page.TestSwitchAsync("ses_A", "/A");
            await page.TestSwitchAsync("ses_B", "/B");
            var second = backend.Hold("ses_A", true); var fresh = page.TestSwitchAsync("ses_A", "/A");
            first.Result.SetResult(backend.Messages("ses_A")); await old;
            Check(page.TestRefreshBusy && page.TestEmptyText == "正在加载消息…" && page.TestAppliedCount == 0, "rapid A-B-A rejects first A request even though exact source/directory/session tuple matches again");
            second.Result.SetResult(Snapshot("new A")); await fresh;
            Check(LiveText(page) == "new A", "only newest A generation can publish after rapid return");
        }
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            backend.Cursors["ses_A"] = "older-A";
            await page.TestSwitchAsync("ses_A", "/A");
            var older = backend.Hold("ses_A", true); var prepend = page.TestLoadOlderAsync();
            older.Result.SetResult(Json(new { data = new[] { Live("old row") with { Id = "msg_old" } }, cursor = new { next = (string?)null } })); await prepend;
            Check(page.TestRetainedRows.Count == 2 && page.TestNextCursor is null, "real continuation establishes retained older buffer and exhausted chain");
            var oldBuffer = page.TestOlderBuffer; var latestBuffer = page.TestLatestBuffer;
            await page.TestSwitchAsync("ses_B", "/B"); var state = backend.Hold("ses_A", false); var back = page.TestSwitchAsync("ses_A", "/A");
            Check(page.TestLastApplied().Count == 2 && ReferenceEquals(oldBuffer, page.TestOlderBuffer) && ReferenceEquals(latestBuffer, page.TestLatestBuffer)
                && page.TestNextCursor is null, "cache transfers both accepted segments and exhausted cursor state, not only latest window");
            state.Result.SetResult(backend.State("ses_A")); await back;
            Check(page.TestRetainedRows.Count == 2 && page.TestNextCursor is null, "authoritative ordinary refresh after reuse retains pagination history without reviving exhausted cursor");
        }
    }

    private static async Task BindingExactIdentity()
    {
        var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
        await page.TestSwitchAsync("ses_A", "/A"); await page.TestSwitchAsync("ses_B", "/B");
        backend.Directories["ses_A"] = "/other";
        var state = backend.Hold("ses_A", false); var load = page.TestSwitchAsync("ses_A", "/other");
        Check(page.TestEmptyText == "正在加载消息…", "same session ID with different real directory cannot reuse A's window");
        state.Result.SetResult(backend.State("ses_A")); await load;
        var count = backend.StateCount["ses_A"];
        backend.Directories["ses_A"] = "/third";
        await page.TestSwitchAsync("ses_A", "/third");
        Check(backend.StateCount["ses_A"] == count + 1 && page.TestBindingDirectory == "/third", "same-ID early return includes directory identity");
        await page.TestSwitchAsync("ses_B", "/B");
        state = backend.Hold("ses_A", false); load = page.TestSwitchAsync("ses_A");
        Check(page.TestBindingDirectory is null && page.TestEmptyText == "正在加载消息…", "unknown directory never guesses a cache key from current/Home directory");
        var message = backend.Hold("ses_A", true); state.Result.SetResult(backend.State("ses_A")); await message.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(page.TestBindingDirectory == "/third" && LiveText(page) == "ses_A", "backend directory resolves legitimate read and only then allows exact visual reuse");
        message.Result.SetResult(backend.Messages("ses_A")); await load;
        Check(page.TestEmptyText == "" && !page.TestRefreshBusy, "directory resolution does not invalidate its own successful read");
        await page.TestSwitchAsync("ses_B", "/B"); page.TestReplaceSource();
        state = backend.Hold("ses_A", false); load = page.TestSwitchAsync("ses_A", "/third");
        Check(page.TestEmptyText == "正在加载消息…" && page.TestCachedBindings == 0, "same source identity with new instance/revision cannot resurrect accepted slots");
        state.Result.SetResult(backend.State("ses_A")); await load;
        // Backend correction must also discard a mismatched already-visible cached window.
        await page.TestSwitchAsync("ses_B", "/B"); backend.Directories["ses_A"] = "/corrected";
        message = backend.Hold("ses_A", true); load = page.TestSwitchAsync("ses_A", "/third");
        await message.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(page.TestBindingDirectory == "/corrected" && page.TestEmptyText == "正在加载消息…", "authoritative changed directory removes wrong cached window before messages");
        message.Result.SetResult(backend.Messages("ses_A")); await load;
    }

    private static async Task BindingIndependentGenerations()
    {
        foreach (var identity in new[] { "revision", "page", "source" })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            await page.TestSwitchAsync("ses_A", "/A"); await page.TestSwitchAsync("ses_B", "/B");
            if (identity == "revision") page.TestAdvanceRevisionOnly();
            if (identity == "page") page.TestAdvancePageGeneration();
            if (identity == "source") page.TestForeignSource();
            var state = backend.Hold("ses_A", false); var load = page.TestSwitchAsync("ses_A", "/A");
            Check(page.TestCachedBindings == 0 && page.TestEmptyText == "正在加载消息…", identity + " change independently rejects all otherwise matching windows");
            state.Result.SetResult(backend.State("ses_A")); await load;
        }
    }

    private static async Task BindingInvalidation()
    {
        foreach (var kind in new[] { "rollback", "workflow", "revocation", "unload" })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            await page.TestSwitchAsync("ses_A", "/A"); await page.TestSwitchAsync("ses_B", "/B"); await page.TestSwitchAsync("ses_A", "/A");
            if (kind == "rollback") page.TestAcceptedHistory("msg_missing");
            if (kind == "workflow") { page.TestBeginSend(45); page.TestEndSend(45, reset: true); }
            if (kind == "revocation") page.TestRevokeSource();
            if (kind == "unload") page.TestUnload();
            if (kind is "revocation" or "unload")
            {
                Check(page.TestCachedBindings == 0 && !page.TestRefreshBusy, kind + " releases all page slots and request owner");
                continue;
            }
            await page.TestSwitchAsync("ses_B", "/B"); var state = backend.Hold("ses_A", false); var again = page.TestSwitchAsync("ses_A", "/A");
            Check(page.TestEmptyText == "正在加载消息…", kind + " cannot revive pre-reset accepted message window");
            state.Result.SetResult(backend.State("ses_A")); await again;
        }
        foreach (var revoke in new[] { false, true })
        {
            var backend = new BindingBackend(); using var client = backend.Client(); var page = new OpenCodePage(); page.TestActivate("ses_seed", client);
            var held = backend.Hold("ses_A", true); var load = page.TestSwitchAsync("ses_A", "/A");
            if (revoke) page.TestRevokeSource(); else page.TestUnload();
            held.Result.SetResult(backend.Messages("ses_A")); await load;
            Check(!page.TestRefreshBusy && page.TestAppliedCount == 0 && page.Notices.Count == 0, "late read/finally after revocation/unload cannot publish");
        }
    }
}
