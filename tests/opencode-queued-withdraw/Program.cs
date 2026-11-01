// Queued-message withdraw: REAL OpenCodeClient (inbox DELETE/PATCH + GET inbox/message) and the REAL WinUI-free
// OpenCodeQueuedPromptWithdrawal flow, driven through a fake HttpMessageHandler. No live server, no UI.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Notifications;

internal static class Program
{
    private const string Session = "ses_q1";
    private const string Inbox = "msg_q1";
    private static int checks;

    private static async Task<int> Main()
    {
        await ClientEndpoints();
        await AbsenceIsNotAReceipt();
        await AlreadyDeliveredBeforeDelete();
        await DeliveredRaceDetectedByMessages();
        await StillQueuedAfterDelete();
        await FailuresAreUncertain();
        await NotWithdrawable();
        JudgeDeliveryCoverage();
        await RevocationAndBindingFences();
        Console.WriteLine($"PASS 9 queued-withdraw groups; {checks} assertions; fake HTTP only; never re-sends.");
        return 0;
    }

    private static async Task ClientEndpoints()
    {
        var server = new FakeServer();
        using var client = server.Client();
        await client.CancelInboxItemAsync(Session, Inbox, CancellationToken.None);
        Check(server.Calls.Single() == "DELETE /api/session/ses_q1/inbox/msg_q1", "DELETE exact path");
        Check(server.Auth.All(a => a == "Bearer tok"), "per-request auth");
        await client.UpdateInboxDeliveryAsync(Session, Inbox, OpenCodeDeliveryModes.Steer, CancellationToken.None);
        Check(server.Calls[1] == "PATCH /api/session/ses_q1/inbox/msg_q1" && server.Bodies[1] == "{\"delivery\":\"steer\"}", "PATCH body = {delivery}");
        await Expect<ArgumentException>(() => client.UpdateInboxDeliveryAsync(Session, Inbox, "later", CancellationToken.None), "unknown delivery rejected locally");
        await Expect<ArgumentException>(() => client.CancelInboxItemAsync(Session, "msg_/x", CancellationToken.None), "path-unsafe id rejected");
        await Expect<ArgumentException>(() => client.CancelInboxItemAsync("bad", Inbox, CancellationToken.None), "bad session id rejected");
        Check(server.Calls.Count == 2, "rejected ids send nothing");

        var non204 = new FakeServer { DeleteStatus = HttpStatusCode.OK };
        using var c2 = non204.Client();
        var protocol = await Expect<OpenCodeProtocolException>(() => c2.CancelInboxItemAsync(Session, Inbox, CancellationToken.None), "200 is not the 204 contract");
        Check(protocol!.Message.Contains("{inboxID}") && !protocol.Message.Contains(Inbox), "error label templated, id not leaked");
        var notFound = new FakeServer { DeleteStatus = HttpStatusCode.NotFound };
        using var c3 = notFound.Client();
        var api = await Expect<OpenCodeApiException>(() => c3.CancelInboxItemAsync(Session, Inbox, CancellationToken.None), "404 surfaces as API error");
        Check(api!.Message.Contains("DELETE api/session/{sessionID}/inbox/{inboxID}"), "API error label");
        Check(notFound.Calls.Count == 1, "no automatic retry");
        Console.WriteLine("PASS group 1: inbox DELETE/PATCH endpoints, 204 contract, id validation, no retry");
    }

    private static Task<OpenCodeQueuedWithdrawResult> Withdraw(OpenCodeClient client) =>
        OpenCodeQueuedPromptWithdrawal.WithdrawAsync(client, Session, Inbox,
            new OpenCodePageSource("offline", 1, () => client), () => true, CancellationToken.None);

    private static async Task AbsenceIsNotAReceipt()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem(Inbox, "帮我改一下标题", 1000));
        server.Messages.Add(UserMessage("msg_old", "帮我改一下标题", 500)); // same text, BEFORE enqueue → not this item
        using var client = server.Client();
        var result = await Withdraw(client);
        Check(result.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && result.Text is null, "even complete history + absent inbox is not an atomic not-delivered receipt");
        Check(!result.HadAttachments, "text-only item");
        Check(server.Calls.SequenceEqual(new[]
        {
            "GET /api/session/ses_q1/inbox", "DELETE /api/session/ses_q1/inbox/msg_q1",
            "GET /api/session/ses_q1/inbox", "GET /api/session/ses_q1/message",
        }), "fresh pre-check → one DELETE → fresh inbox → fresh messages");
        Check(server.Queries[3].Contains("order=desc") && server.Queries[3].Contains("limit=50"), "latest message page read");
        Check(!server.Calls.Any(c => c.Contains("/prompt")), "never re-sends");
        Check(result.FreshInbox is { Count: 0 }, "fresh inbox returned for strip refresh");

        var withFiles = new FakeServer();
        withFiles.Inbox.Add(UserItem(Inbox, "看图", 1000, "\"files\":[{\"mime\":\"image/png\"}]"));
        using var c2 = withFiles.Client();
        var attached = await Withdraw(c2);
        Check(attached.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && attached.Text is null && attached.HadAttachments, "attachments flagged, neither text nor files restored without proof");
        var emptyArrays = new FakeServer();
        emptyArrays.Inbox.Add(UserItem(Inbox, "x", 1000, "\"files\":[],\"agents\":null"));
        using var c3 = emptyArrays.Client();
        Check(!(await Withdraw(c3)).HadAttachments, "empty/null attachment lists are not attachments");
        foreach (var cursor in new[] { "\"cursor\":{\"next\":\"opaque-next\"}", "", "\"cursor\":{\"next\":\"\"}" })
        {
            var partial = new FakeServer { CursorJson = cursor };
            partial.Inbox.Add(UserItem(Inbox, "hello", 1000));
            partial.Messages.Add(UserMessage("msg_old", "different", 900));
            using var cp = partial.Client();
            var uncertain = await Withdraw(cp);
            Check(uncertain.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && uncertain.Text is null,
                "short page with next/missing/invalid cursor cannot restore a possibly delivered prompt");
            Check(partial.Calls.Count(c => c.StartsWith("DELETE")) == 1 && !partial.Calls.Any(c => c.Contains("/prompt")), "uncertain cancellation never retries or re-sends");
        }
        Console.WriteLine("PASS group 2: absent inbox/history and incomplete pagination never fabricate cancellation proof");
    }

    private static async Task AlreadyDeliveredBeforeDelete()
    {
        var server = new FakeServer();
        server.Inbox.Add(UserItem("msg_other", "别的", 900));
        using var client = server.Client();
        var result = await Withdraw(client);
        Check(result.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && result.Text is null, "absent at fresh pre-check → unknown delivery, no text");
        Check(server.Calls.SequenceEqual(new[] { "GET /api/session/ses_q1/inbox" }), "no DELETE when already gone");
        Console.WriteLine("PASS group 3: already-delivered item is never deleted or restored");
    }

    private static async Task DeliveredRaceDetectedByMessages()
    {
        // Same id appears in history (delivered just before DELETE; DELETE was a 204 no-op).
        var byId = new FakeServer();
        byId.Inbox.Add(UserItem(Inbox, "hello", 1000));
        byId.OnDelete = s => s.Messages.Insert(0, UserMessage(Inbox, "hello", 1001));
        using var c1 = byId.Client();
        var r1 = await Withdraw(c1);
        Check(r1.Outcome == OpenCodeQueuedWithdrawOutcome.Delivered && r1.Text is null, "message with inbox id → delivered, not restored");

        // Different id, same text (trim-insensitive), created after enqueue.
        var byText = new FakeServer();
        byText.Inbox.Add(UserItem(Inbox, "  hello  ", 1000));
        byText.OnDelete = s => s.Messages.Insert(0, UserMessage("msg_server", "hello", 1000));
        using var c2 = byText.Client();
        var r2 = await Withdraw(c2);
        Check(r2.Outcome == OpenCodeQueuedWithdrawOutcome.Delivered, "same text after enqueue → delivered");
        Check(!byText.Calls.Any(c => c.Contains("/prompt")), "no re-send on delivered");
        Console.WriteLine("PASS group 4: 204-after-delivery race detected through fresh history");
    }

    private static async Task StillQueuedAfterDelete()
    {
        var server = new FakeServer { DeleteRemoves = false };
        server.Inbox.Add(UserItem(Inbox, "hello", 1000));
        using var client = server.Client();
        var result = await Withdraw(client);
        Check(result.Outcome == OpenCodeQueuedWithdrawOutcome.StillQueued && result.Text is null, "still in fresh inbox → still queued, nothing restored");
        Check(server.Calls.Count(c => c.StartsWith("DELETE")) == 1, "single DELETE, no retry");
        Check(!server.Calls.Any(c => c.EndsWith("/message")), "no history read needed");
        Console.WriteLine("PASS group 5: ineffective DELETE reported, no retry");
    }

    private static async Task FailuresAreUncertain()
    {
        foreach (var (name, fail) in new (string, Func<string, bool>)[]
        {
            ("pre-check GET", c => c == "GET /api/session/ses_q1/inbox#1"),
            ("DELETE", c => c.StartsWith("DELETE")),
            ("post GET inbox", c => c == "GET /api/session/ses_q1/inbox#2"),
            ("messages GET", c => c.EndsWith("/message#1")),
        })
        {
            var server = new FakeServer { Fail = fail };
            server.Inbox.Add(UserItem(Inbox, "hello", 1000));
            using var client = server.Client();
            var result = await Withdraw(client);
            Check(result.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && result.Text is null, name + " failure → uncertain, no text");
            Check(server.Calls.Count(c => c.StartsWith("DELETE")) <= 1 && !server.Calls.Any(c => c.Contains("/prompt")), name + ": no retry, no re-send");
        }

        var timeout = new FakeServer { Hang = c => c.StartsWith("DELETE") };
        timeout.Inbox.Add(UserItem(Inbox, "hello", 1000));
        using var tc = timeout.Client(TimeSpan.FromMilliseconds(200));
        var timed = await Withdraw(tc);
        Check(timed.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain, "DELETE timeout → uncertain");
        Console.WriteLine("PASS group 6: every failed/timed-out step is uncertain (可能已发送), never restored");
    }

    private static async Task NotWithdrawable()
    {
        var server = new FakeServer();
        server.Inbox.Add(new Item(Inbox, "compaction", "{}", 1000));
        using var client = server.Client();
        var r = await Withdraw(client);
        Check(r.Outcome == OpenCodeQueuedWithdrawOutcome.NotWithdrawable && !server.Calls.Any(c => c.StartsWith("DELETE")), "non-user item never deleted");
        var noText = new FakeServer();
        noText.Inbox.Add(new Item(Inbox, "user", "{\"files\":[]}", 1000));
        using var c2 = noText.Client();
        Check((await Withdraw(c2)).Outcome == OpenCodeQueuedWithdrawOutcome.NotWithdrawable,
            "user item without text payload not withdrawable");
        Check(!OpenCodeQueuedPromptWithdrawal.IsWithdrawableId(Session, "abc") && !OpenCodeQueuedPromptWithdrawal.IsWithdrawableId(Session, "msg_a b")
            && OpenCodeQueuedPromptWithdrawal.IsWithdrawableId(Session, Inbox), "id predicate mirrors client validation");
        Console.WriteLine("PASS group 7: non-text / non-user items are not withdrawable");
    }

    private static void JudgeDeliveryCoverage()
    {
        OpenCodeSessionMessagesResponse Page(List<OpenCodeMessage> messages, string? next = "older") => new() { Data = messages, Cursor = new() { Next = next } };
        var newer = new List<OpenCodeMessage> { Message("msg_a", "assistant", null, 1300), Message("msg_b", "assistant", null, 1200), Message("msg_c", "assistant", null, 1100) };
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(Page(newer), Inbox, "t", 1000) == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.Unknown,
            "continuation not read → unknown regardless of page length");
        var reaching = new List<OpenCodeMessage> { Message("msg_a", "assistant", null, 1300), Message("msg_b", "assistant", null, 1200), Message("msg_c", "user", "x", 900) };
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(Page(reaching), Inbox, "t", 1000) == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.Unknown,
            "timestamps crossing enqueue never certify an unread continuation");
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(Page(reaching, null), Inbox, "t", 1000) == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.NotObservedInCompleteSnapshot,
            "explicit terminal cursor proves only snapshot coverage, not non-delivery");
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(Page(new List<OpenCodeMessage> { Message("msg_u", "user", "t", 10) }), Inbox, "t", null)
            == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.Delivered, "unknown enqueue time: any same-text user message counts");
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(null, Inbox, "t", 1000) == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.Unknown,
            "missing page → unknown");
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(new() { Data = reaching }, Inbox, "t", 1000)
            == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.Unknown, "missing cursor never certifies even a short page");
        Check(OpenCodeQueuedPromptWithdrawal.JudgeDelivery(Page(new List<OpenCodeMessage> { Message("msg_s", "synthetic", "t", 2000) }, null), Inbox, "t", 1000)
            == OpenCodeQueuedPromptWithdrawal.DeliveryVerdict.NotObservedInCompleteSnapshot, "non-user same text is not observed delivery");
        Console.WriteLine("PASS group 8: cursor-based snapshot coverage, never page-length/timestamp proof");
    }

    private static async Task RevocationAndBindingFences()
    {
        foreach (var boundary in new[] { "source-revoked", "page-replaced", "session-ABA" })
        {
            var server = new FakeServer();
            server.Inbox.Add(UserItem(Inbox, "hello", 1000));
            using var client = server.Client();
            var source = new OpenCodePageSource("offline", 1, () => client);
            int page = 1, generation = 1;
            server.OnInboxRead = _ => { if (boundary == "source-revoked") source.Revoke(); else if (boundary == "page-replaced") page++; else generation += 2; };
            // No linked revocation token: the source lock itself must fence initiation.
            var result = await OpenCodeQueuedPromptWithdrawal.WithdrawAsync(client, Session, Inbox, source,
                () => page == 1 && generation == 1, CancellationToken.None);
            Check(result.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && result.Text is null, boundary + " prevents text restoration");
            Check(server.Calls.SequenceEqual(new[] { "GET /api/session/ses_q1/inbox" }), boundary + " after preflight starts ZERO DELETEs or later GETs");
        }
        var afterDelete = new FakeServer();
        afterDelete.Inbox.Add(UserItem(Inbox, "hello", 1000));
        using var postClient = afterDelete.Client();
        var postSource = new OpenCodePageSource("offline", 1, () => postClient);
        afterDelete.OnDelete = _ => postSource.Revoke();
        var post = await OpenCodeQueuedPromptWithdrawal.WithdrawAsync(postClient, Session, Inbox, postSource, () => true, CancellationToken.None);
        Check(post.Outcome == OpenCodeQueuedWithdrawOutcome.Uncertain && post.Text is null, "revocation after DELETE remains uncertain");
        Check(afterDelete.Calls.Count == 2 && afterDelete.Calls.Last().StartsWith("DELETE"), "revocation fences post-DELETE reconciliation GET too");
        Console.WriteLine("PASS group 9: source revocation/page replacement/session ABA fenced per request; preflight revocation sends zero DELETEs");
    }

    // ---------------------------------------------------------------- fake server

    private sealed record Item(string Id, string Type, string Payload, double Created);

    private static Item UserItem(string id, string text, double created, string? extra = null)
        => new(id, "user", "{\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + (extra is null ? "" : "," + extra) + "}", created);

    private static string UserMessage(string id, string text, double created)
        => "{\"id\":\"" + id + "\",\"type\":\"user\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + ",\"time\":{\"created\":" + created + "}}";

    private static OpenCodeMessage Message(string id, string type, string? text, double created)
        => new() { Id = id, Type = type, Text = text, Time = new OpenCodeMessageTime { Created = created } };

    private sealed class FakeServer
    {
        public List<Item> Inbox { get; } = new();
        public List<string> Messages { get; } = new();
        public List<string> Calls { get; } = new();
        public List<string> Queries { get; } = new();
        public List<string?> Bodies { get; } = new();
        public List<string?> Auth { get; } = new();
        public HttpStatusCode DeleteStatus { get; init; } = HttpStatusCode.NoContent;
        public bool DeleteRemoves { get; init; } = true;
        public Action<FakeServer>? OnDelete { get; set; }
        public Action<FakeServer>? OnInboxRead { get; set; }
        public string CursorJson { get; init; } = "\"cursor\":{\"previous\":null,\"next\":null}";
        public Func<string, bool> Fail { get; init; } = _ => false;
        public Func<string, bool> Hang { get; init; } = _ => false;
        private readonly Dictionary<string, int> _counts = new();

        public OpenCodeClient Client(TimeSpan? timeout = null)
        {
            var http = new HttpClient(new Handler(SendAsync));
            var options = new OpenCodeClientOptions { BaseUrl = "https://offline.invalid", Secret = "tok" };
            if (timeout is { } t) options = options with { Timeout = t };
            return new OpenCodeClient(http, options, true);
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = request.Method.Method + " " + request.RequestUri!.AbsolutePath;
            Calls.Add(call);
            Queries.Add(request.RequestUri.Query);
            Auth.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(token));
            _counts[call] = _counts.GetValueOrDefault(call) + 1;
            var numbered = call + "#" + _counts[call];
            if (Hang(call) || Hang(numbered)) await Task.Delay(Timeout.Infinite, token);
            if (Fail(call) || Fail(numbered)) return new HttpResponseMessage(HttpStatusCode.InternalServerError);

            if (request.Method == HttpMethod.Delete)
            {
                var id = request.RequestUri.AbsolutePath.Split('/').Last();
                if (DeleteRemoves) Inbox.RemoveAll(i => i.Id == id); // unavailable items: still 204 (server no-op)
                OnDelete?.Invoke(this);
                return new HttpResponseMessage(DeleteStatus);
            }

            if (request.Method == HttpMethod.Patch) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (call.EndsWith("/inbox"))
            {
                var items = Inbox.Select(i => "{\"id\":\"" + i.Id + "\",\"sessionID\":\"" + Session + "\",\"type\":\"" + i.Type
                    + "\",\"delivery\":\"queue\",\"time\":{\"created\":" + i.Created + "},\"payload\":" + i.Payload + "}");
                var response = Json("{\"data\":[" + string.Join(",", items) + "]}");
                OnInboxRead?.Invoke(this);
                return response;
            }

            if (call.EndsWith("/message"))
                return Json("{\"data\":[" + string.Join(",", Messages) + "]" + (CursorJson.Length == 0 ? "" : "," + CursorJson) + "}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private static async Task<T?> Expect<T>(Func<Task> action, string name) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T expected)
        {
            checks++;
            return expected;
        }

        throw new Exception("expected " + typeof(T).Name + ": " + name);
    }

    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }
}
