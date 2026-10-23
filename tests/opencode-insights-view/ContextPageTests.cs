using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class ContextPageTests
{
    internal static IEnumerable<(string Name, Action Body)> Cases() => new (string, Action)[]
    {
        ("context: actual REST/SSE publication updates real header with pane/flyout closed, one catalog GET only", ClosedHeader),
        ("context: stale SSE session/owner and revoked source ignored; switch/reset clears header", Fences),
    };
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
    private sealed class Http : HttpMessageHandler
    {
        internal readonly List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath != "/api/model") throw new InvalidOperationException("Unexpected automatic full insights/discovery read");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"location":{"directory":"C:/work"},"data":[{"id":"a","providerID":"p","limit":{"context":200000}}]}""", Encoding.UTF8, "application/json") });
        }
    }
    private static (OpenCodePage Page, OpenCodePageSource Source, Http Http) Fixture()
    {
        ShimTree.ParentVisible = false;
        var http = new Http();
        var client = new OpenCodeClient(new HttpClient(http), new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var source = new OpenCodePageSource("context-test", 1, () => client);
        var page = new OpenCodePage();
        page.Activate();
        page.InvalidateAndInstall(source, client);
        page.SelectSession("ses_a");
        return (page, source, http);
    }
    private static OpenCodeMessage Usage(string id, int created, int input, string? text = null) => new()
    {
        Id = id, Type = "assistant", Model = new() { Id = "a", ProviderId = "p" }, Time = new() { Created = created },
        Tokens = new() { Input = input, Output = 0, Reasoning = 0, Cache = new() { Read = 0, Write = 0 } },
        Content = text is null ? new() : new() { new() { Type = "text", Text = text } },
    };
    private static OpenCodeEvent Updated(string session, OpenCodeMessage message)
        => OpenCodeEvent.Parse(JsonSerializer.Serialize(new { type = "message.updated", data = new { sessionID = session, info = message } }));
    private static void ClosedHeader()
    {
        var (page, _, http) = Fixture();
        var clicks = 0;
        page.View.ContextDetailsOpening += (_, _) => clicks++;
        page.ContextMetadata(new() { Id = "a", ProviderId = "p" });
        page.AcceptRest(Usage("msg_a", 10, 10000));
        Check(page.ContextSurfacesClosed && page.View.ContextRing.Ring.Value == 5, "closed header renders 10K/200K after accepted REST");
        Check(page.View.ContextRing.Summary.Contains("10K", StringComparison.Ordinal), "real header summary carries REST reference");
        page.BeginStream();
        var merges = page.MergeCalls;
        var projections = page.ProjectionCalls;
        page.AcceptEvent(Updated("ses_a", Usage("msg_b", 20, 50000)));
        Check(page.MergeCalls == merges + 1, "SSE reuses its single accepted merged projection; ring does not merge again");
        Check(page.ProjectionCalls == projections + 1, "latest-only SSE batch projects history exactly once");
        Check(ReferenceEquals(page.ProjectedWindow, page.ExecutionWindow)
            && ReferenceEquals(page.ExecutionWindow, page.AcknowledgementWindow),
            "execution and ACK reuse the accepted projected list without a second window copy");
        Check(page.View.ContextRing.Ring.Value == 25 && page.ContextSurfacesClosed, "closed header renders SSE usage before any click");
        Check(page.Insights.Snapshot.Context.Value?.LatestAssistantUsage?.MessageId == "msg_b", "newest reference, not cumulative sum");
        var renders = 0;
        page.Insights.Changed += (_, _) => renders++;
        var contextProjections = page.Insights.ContextProjectionCount;
        var todoProjections = page.Insights.TodoProjectionCount;
        for (var i = 0; i < 20; i++) page.AcceptEvent(Updated("ses_a", Usage("msg_b", 20, 50000, "delta-" + i)));
        Check(renders == 0, "unchanged usage publications do not redraw full insights/header");
        Check(page.Insights.ContextProjectionCount == contextProjections
            && page.Insights.TodoProjectionCount == todoProjections,
            "unchanged text/usage publications skip context and to do derivation");
        Check(http.Paths.Count == 1 && page.Insights.OpenCodeReadsStarted == 1 && clicks == 0
            && page.Insights.BillionReadsStarted == 0 && page.Insights.BillionDiscoveryAttemptsStarted == 0,
            "only bounded catalog metadata GET; no click/full reads/Billion/discovery per update");
    }
    private static void Fences()
    {
        var (page, source, _) = Fixture();
        page.ContextMetadata(new() { Id = "a", ProviderId = "p" });
        page.AcceptRest(Usage("msg_a", 10, 10000));
        var oldOwner = page.BeginStream();
        page.AcceptEvent(Updated("ses_other", Usage("msg_wrong", 99, 190000)));
        Check(page.View.ContextRing.Ring.Value == 5, "other-session SSE ignored");
        page.BeginStream();
        page.AcceptEvent(Updated("ses_a", Usage("msg_wrong", 99, 190000)), oldOwner);
        Check(page.View.ContextRing.Ring.Value == 5, "old SSE stream owner ignored");
        source.Revoke();
        page.AcceptEvent(Updated("ses_a", Usage("msg_wrong", 99, 190000)));
        Check(page.View.ContextRing.Ring.Value == 5, "revoked source cannot publish");
        page.SelectSession("ses_b");
        Check(page.View.ContextRing.Ring.Value == 0 && page.Insights.Snapshot.Context.Value is null, "session reset clears old header without click");
    }
}
