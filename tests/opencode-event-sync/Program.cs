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
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static OpenCodeEvent Event(string type, string data) => OpenCodeEvent.Parse("{\"type\":\"" + type + "\",\"data\":" + data + "}");
    private static OpenCodeMessage Message(string id, string text) => new() { Id = id, Type = "assistant", Content = new() { new() { Id = "p1", Type = "text", Text = text } }, Time = new() { Created = 10 } };
    private static string Text(OpenCodeEventReducer reducer) => reducer.Messages.Single().Content!.Single().Text!;
    private static async Task Main()
    {
        var reducer = new OpenCodeEventReducer("ses_test");
        reducer.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_a","id":"p1","type":"text","text":"hello"}}"""));
        Check(Text(reducer) == "hello", "part.updated inserts without message");
        reducer.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_a","id":"p1","type":"text","text":"replace"}}"""));
        Check(Text(reducer) == "replace", "part.updated replaces by stable part ID");
        reducer.Apply(Event("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_a","partID":"p1","field":"text","delta":" delta"}"""));
        Check(Text(reducer) == "replace delta", "delta appends");
        Check(!reducer.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_other","messageID":"msg_a","id":"p1","type":"text","text":"wrong"}}""")), "other session ignored");

        var early = new OpenCodeEventReducer("ses_test");
        early.Apply(Event("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_a","partID":"p1","field":"text","delta":" after"}"""));
        early.Apply(Event("message.part.updated", """{"part":{"sessionID":"ses_test","messageID":"msg_a","id":"p1","type":"text","text":"before"}}"""));
        Check(Text(early) == "before after", "out-of-order delta retained until part arrives");

        var fetch = reducer.Version;
        reducer.Apply(Event("message.part.delta", """{"sessionID":"ses_test","messageID":"msg_a","partID":"p1","field":"text","delta":" live"}"""));
        reducer.MergeSnapshot(new[] { Message("msg_a", "stale") }, fetch);
        Check(Text(reducer) == "replace delta live", "events during fetch win over stale snapshot");
        reducer.MergeSnapshot(new[] { Message("msg_a", "reconnected") }, reducer.Version);
        Check(Text(reducer) == "reconnected", "reconnect snapshot repairs missed deltas");

        var native = new OpenCodeEventReducer("ses_test");
        native.Apply(Event("session.reasoning.started", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0}"""));
        Check(native.Messages.Single().Content!.Single().Type == "reasoning" && Text(native) == "", "native reasoning begins immediately with empty text");
        Check(native.Messages.Single().Content!.Single().Time?.Completed is null, "reasoning remains in progress");
        native.Apply(Event("session.reasoning.delta", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"delta":"thinking"}"""));
        Check(Text(native) == "thinking", "native reasoning delta appends");
        native.Apply(Event("session.reasoning.ended", """{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"text":"thought"}"""));
        Check(Text(native) == "thought" && native.Messages.Single().Content!.Single().Time?.Completed is not null, "native reasoning end replaces final text and completes");

        await Transport();
        Console.WriteLine($"PASS event-sync: {checks} assertions (insert/replace, delta, out-of-order, fetch race, native reasoning, stream EOF/reconnect/cancel/auth).");
    }

    private static async Task Transport()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenCodeClient(http, new() { BaseUrl = "http://localhost:4096", AuthenticationMode = OpenCodeAuthenticationMode.Basic, Username = "opencode", Secret = "secret" }, true);
        var reconnects = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await foreach (var item in client.SubscribeEventsAsync())
                {
                    Check(item.Type == "session.text.delta", "SSE event type from data envelope");
                    var received = item.ReceivedAt;
                    await Task.Delay(50);
                    var model = new OpenCodeEventReducer("ses_test");
                    model.Apply(item);
                    var applied = DateTimeOffset.UtcNow;
                    Check(model.Messages.Single().Content!.Single().Text == "live", "stream event applied by production reducer");
                    Console.WriteLine($"HEADLESS event→reducer apply received_utc={received:O} applied_utc={applied:O} latency_ms={(applied-received).TotalMilliseconds:F1}; 50ms coalescing, not WinUI render");
                }
            }
            catch (OpenCodeTransportException) { reconnects++; }
        }
        Check(reconnects == 2 && handler.Calls == 2, "EOF observable; caller resubscribes");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await foreach (var _ in client.SubscribeEventsAsync(cancelled.Token)) { } throw new Exception("missing cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++;
            Check(request.RequestUri!.AbsolutePath == "/api/event", "verified V2 SSE path");
            Check(request.Headers.Authorization?.Scheme == "Basic", "SSE per-request Basic auth");
            var json = """{"type":"session.text.delta","data":{"sessionID":"ses_test","assistantMessageID":"msg_n","ordinal":0,"delta":"live"}}""";
            if (Calls == 2) json = JsonSerializer.Serialize(json); // Effect JSON-string envelope also supported.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: session.text.delta\ndata: " + json + "\n\n", Encoding.UTF8, "text/event-stream") });
        }
    }
}
