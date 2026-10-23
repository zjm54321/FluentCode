// Real sidebar visibility expression, real event drain, real insights page/controller.
// Synthetic surrounding page/native controls; offline HTTP 404 exercises honest unavailable sections.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using UiQueue = Microsoft.UI.Dispatching.DispatcherQueue;

internal static class AutomaticInsightsTests
{
    internal static IEnumerable<(string Name, Action Body)> Cases() => new (string, Action)[]
    {
        ("auto insights: resident pane loads all sections; streaming/status/to do burst refreshes once", ResidentAndEvents),
        ("auto insights: hidden/drawer/session/source fences cancel queued and late reads", VisibilityAndFences),
    };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private sealed class Http : HttpMessageHandler
    {
        internal int Reads;
        internal TaskCompletionSource<HttpResponseMessage>? Held;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Reads++;
            return Held?.Task ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private static (OpenCodePage Page, OpenCodePageSource Source, Http Http) Fixture()
    {
        var http = new Http();
        var client = new OpenCodeClient(new HttpClient(http), new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var source = new OpenCodePageSource("auto-insights", 1, () => client);
        var page = new OpenCodePage();
        page.Activate();
        page.InvalidateAndInstall(source, client);
        page.SelectSession("ses_a");
        page.BeginStream();
        return (page, source, http);
    }
    private static OpenCodeEvent Event(string type, string id = "ses_a")
        => OpenCodeEvent.Parse("{\"type\":\"" + type + "\",\"data\":{\"sessionID\":\"" + id + "\"}}");
    private static OpenCodeSidebarSurface Wide => new OpenCodeSidebarLayout().Resize(1600).ForSession(true);
    private static void ResidentAndEvents()
    {
        using var pump = new Pump();
        var (page, _, http) = Fixture();
        Check(http.Reads == 0, "hidden initial panel performs no full load");
        page.ApplyInsightsSurface(Wide);
        pump.Until(() => !page.InsightsRefreshPending);
        var snapshot = page.Insights.Snapshot;
        Check(http.Reads == 1 && snapshot.SampledAt is not null, "resident panel loads without a click");
        Check(snapshot.Context.State != OpenCodeInsightState.NotLoaded && snapshot.Cost.State != OpenCodeInsightState.NotLoaded
            && snapshot.Pricing.State != OpenCodeInsightState.NotLoaded && snapshot.Capabilities.State != OpenCodeInsightState.NotLoaded
            && snapshot.Todo.State != OpenCodeInsightState.NotLoaded && snapshot.Children.State != OpenCodeInsightState.NotLoaded,
            "every ordinary section leaves not-loaded even when backend is unavailable");
        page.ApplyInsightsSurface(Wide);
        Check(http.Reads == 1, "repeated layout does not reload");
        for (var i = 0; i < 50; i++) page.AcceptEvent(Event("session.text.delta"));
        page.AcceptEvent(Event("message.part.updated"));
        page.AcceptEvent(Event("todo.updated"));
        page.AcceptEvent(Event("session.status"));
        page.AcceptEvent(Event("session.idle"));
        Check(http.Reads == 1, "event burst has no immediate reload storm");
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 2, "current-session burst coalesces into exactly one full refresh");
        foreach (var type in new[] { "message.part.updated", "todo.updated", "session.status" })
        {
            var before = http.Reads;
            for (var i = 0; i < 10; i++) page.AcceptEvent(Event(type));
            pump.Until(() => !page.InsightsRefreshPending);
            Check(http.Reads == before + 1, type + " independently refreshes once");
        }
        page.AcceptEvent(Event("session.execution.started"));
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 5, "busy state suppresses full reload even after a quiet streaming gap");
        page.AcceptEvent(Event("message.part.updated"));
        page.ApplyInsightsSurface(Wide);
        pump.Until(() => !page.InsightsRefreshPending);
        page.ApplyInsightsSurface(Wide);
        Check(http.Reads == 5, "streaming part updates/layout keep dirty state without expensive reads");
        page.AcceptEvent(Event("session.idle"));
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 6, "real idle flushes the accumulated streaming changes once");
        page.AcceptEvent(Event("session.idle", "ses_other"));
        Check(!page.InsightsRefreshPending && http.Reads == 6, "other-session events cannot schedule a read");
        Check(page.Insights.BillionReadsStarted == 0, "automatic ordinary sections preserve Billion consent/read boundary");
        page.Unload();
    }
    private static void VisibilityAndFences()
    {
        using var pump = new Pump();
        var (page, source, http) = Fixture();
        var narrow = new OpenCodeSidebarLayout().Resize(900);
        page.ApplyInsightsSurface(narrow.ForSession(true));
        page.AcceptEvent(Event("todo.updated"));
        Check(http.Reads == 0 && !page.InsightsRefreshPending, "hidden events wait for visibility");
        page.ApplyInsightsSurface(narrow.Toggle(OpenCodeSidebarDrawer.Right).ForSession(true));
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 1, "narrow drawer auto loads on open");
        page.AcceptEvent(Event("session.idle"));
        page.ApplyInsightsSurface(narrow.ForSession(true));
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 1, "close cancels queued refresh");
        page.ApplyInsightsSurface(Wide);
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 2, "reopen catches hidden dirty state once");
        page.AcceptEvent(Event("session.idle"));
        page.SelectSession("ses_b");
        UiQueue.Shared.Drain();
        pump.Until(() => !page.InsightsRefreshPending);
        Check(http.Reads == 3 && page.Insights.Snapshot.SessionId == "ses_b", "visible session switch auto loads new session, drops old debounce");
        http.Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.AcceptEvent(Event("session.idle", "ses_b"));
        pump.Until(() => http.Reads == 4);
        page.ApplyInsightsSurface(Wide);
        page.OpenFlyout();
        Check(http.Reads == 4, "layout and explicit open share single in-flight read");
        var lateLoad = page.InsightsLoad!;
        source.Revoke();
        page.Unload();
        http.Held.SetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        pump.Until(() => lateLoad.IsCompleted && page.Insights.Snapshot.SessionId is null && !page.InsightsRefreshPending);
        Check(http.Reads == 4 && page.Insights.Snapshot.SampledAt is null, "late revoked-source response cannot repopulate panel or launch followups");
    }
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly SynchronizationContext? _previous = Current;
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _work = new();
        internal Pump() => SetSynchronizationContext(this);
        public override void Post(SendOrPostCallback d, object? state) { lock (_work) _work.Enqueue((d, state)); }
        internal void Until(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                (SendOrPostCallback Callback, object? State)? next = null;
                lock (_work) if (_work.Count > 0) next = _work.Dequeue();
                if (next is { } item) { item.Callback(item.State); continue; }
                if (done()) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("automatic insights did not settle");
                Thread.Sleep(1);
            }
        }
        public void Dispose() => SetSynchronizationContext(_previous);
    }
}
