using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Billion;
using AIFrontend.Services.Notifications;

internal static partial class Program
{
    private sealed class OriginFake : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        public Func<HttpRequestMessage, CancellationToken, Task>? Before;
        public OpenCodePageSource? Source;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(Monitor.IsEntered(SourceLock(Source!)), "origin.actual-http-under-source-lock");
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null
                && !request.Headers.Contains("Cookie"), "origin.GET-without-credentials");
            var uri = request.RequestUri!;
            Urls.Add(uri.AbsoluteUri);
            if (Before is { } before) await before(request, ct);
            return uri.AbsolutePath == "/__bili/health"
                ? Json("""{"ok":true,"version":"fixture","instanceId":"observed-only"}""")
                : Json("{\"id\":\"" + uri.AbsolutePath.Split('/')[3]
                    + "\",\"contextTokens\":123,\"handoffMd\":\"IGNORED_CONTENT\"}");
        }
    }

    private static void Choose(OpenCodeSessionInsightsController controller, PageStub page, string? origin, bool confirm = true)
    {
        var binding = page.Capture();
        var context = controller.BindBillionContext(binding)!;
        Check(controller.SetBillionOrigin(binding, context, origin), "origin.current-edit-accepted");
        if (confirm) Check(controller.SetBillionConfirmed(binding, true, controller.Snapshot.BillionContext), "origin.current-consent-accepted");
    }

    private static async Task OriginSelection()
    {
        var fake = new OriginFake();
        var origins = new List<string>();
        var (page, _, controller) = Fixture("ses_exact", origin =>
        {
            origins.Add(origin);
            return new BillionContextClient(origin, new NonDisposingHandler(fake));
        });
        fake.Source = page.Source;
        Check(controller.Snapshot.BillionOrigin is null && !controller.Snapshot.BillionConfirmed
            && controller.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing, "origin.default-is-missing");
        controller.BindBillionContext(page.Capture());
        Check(!controller.SetBillionConfirmed(page.Capture(), true, controller.Snapshot.BillionContext), "origin.cannot-confirm-missing");
        await ReadBillion(controller, page.Capture());
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.MissingOrigin && origins.Count == 0, "origin.no-silent-default");

        foreach (var invalid in new[] { "https://localhost:18788", "file:///tmp/engine", "http://example.invalid:18788",
            "http://user:secret@localhost:18788", "http://@localhost:18788", "http://localhost:18788/ui",
            "http://localhost:18788/ui/..", "http://localhost:18788/?", "http://localhost:18788/#",
            "http://localhost:18788/?x=1", "http://localhost:18788/#session", "http://localhost:0", "http://localhost:18788\\" })
        {
            Check(!BillionContextClient.TryNormalizeOrigin(invalid, out _), "origin.invalid-pure-validation");
            Choose(controller, page, invalid, false);
            await ReadBillion(controller, page.Capture());
            Check(controller.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Invalid
                && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.InvalidOrigin
                && !controller.Snapshot.Billion.Note.Contains("会话 ID")
                && controller.Snapshot.BillionOrigin is null, "origin.invalid-distinct-not-retained");
        }
        Check(origins.Count == 0 && fake.Urls.Count == 0, "origin.invalid-never-created-client-or-HTTP");
        Check(BillionContextClient.TryNormalizeOrigin(" HTTP://LOCALHOST:18788/ ", out var local)
            && local == "http://localhost:18788", "origin.canonical-localhost");
        Check(BillionContextClient.TryNormalizeOrigin("http://[::1]:18788/", out var ipv6)
            && ipv6 == "http://[::1]:18788", "origin.canonical-ipv6");
        Choose(controller, page, " http://127.0.0.1:18788/ ", false);
        await ReadBillion(controller, page.Capture());
        Check(controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.NeedsConfirmation && fake.Urls.Count == 0,
            "origin.valid-is-not-consent");
        controller.SetBillionConfirmed(page.Capture(), true, controller.Snapshot.BillionContext);
        await ReadBillion(controller, page.Capture());
        Check(origins.SequenceEqual(new[] { "http://127.0.0.1:18788" }) && fake.Urls.SequenceEqual(new[]
        { "http://127.0.0.1:18788/__bili/health", "http://127.0.0.1:18788/__bili/sessions/ses_exact/detail" }),
            "origin.same-captured-client-health-exact-detail-no-discovery");
        var oldView = controller.Snapshot.Billion;
        Check(oldView.Phase == OpenCodeBillionPhase.Loaded && oldView.Origin == origins[0]
            && oldView.Stats?.ContextTokens == 123 && oldView.Health?.InstanceId == "observed-only",
            "origin.result-attributed-with-numerical-projection");
        Choose(controller, page, null, false);
        Check(controller.Snapshot.Billion.Stats is null && controller.Snapshot.Billion.Health is null
            && controller.Snapshot.Billion.Origin is null && !controller.Snapshot.BillionConfirmed
            && oldView.Origin == "http://127.0.0.1:18788", "origin.clear-immediate-no-relabeling");

        // A trusted test factory still cannot silently substitute its own default origin.
        var (badPage, _, bad) = Fixture("ses_exact", _ => new BillionContextClient(handler: new NonDisposingHandler(fake)));
        Choose(bad, badPage, "http://127.0.0.1:18789");
        var count = fake.Urls.Count;
        await ReadBillion(bad, badPage.Capture());
        Check(fake.Urls.Count == count && bad.Snapshot.Billion.Phase == OpenCodeBillionPhase.Failed
            && !bad.Snapshot.Billion.Note.Contains("会话 ID"), "origin.factory-substitution-no-HTTP-not-invalid-session");
    }

    private static async Task OriginRaces()
    {
        // Change the selection between immutable capture and health initiation.
        {
            var fake = new OriginFake();
            Action? editAtFactory = null;
            string? received = null;
            var (page, _, controller) = Fixture("ses_factory", origin =>
            {
                received = origin;
                editAtFactory!();
                return new BillionContextClient(origin, fake);
            });
            fake.Source = page.Source;
            Choose(controller, page, "http://127.0.0.1:18788");
            editAtFactory = () => Choose(controller, page, "http://127.0.0.1:18789", false);
            await ReadBillion(controller, page.Capture());
            Check(received == "http://127.0.0.1:18788" && fake.Urls.Count == 0
                && controller.Snapshot.BillionOrigin == "http://127.0.0.1:18789"
                && controller.Snapshot.Billion.Origin is null, "origin.factory-gap-still-captured-zero-health");
        }

        // Delay cancellation independently of ownership using the existing test-only reflection seam.
        // Late health must not start detail; late detail/finally must not replace the new loading owner.
        foreach (var holdDetail in new[] { false, true })
        {
            var fake = new OriginFake();
            var captured = new List<string>();
            var (page, _, controller) = Fixture("ses_race", origin =>
            {
                captured.Add(origin);
                return new BillionContextClient(origin, new NonDisposingHandler(fake));
            });
            fake.Source = page.Source;
            var oldEntered = Signal(); var oldRelease = Signal();
            var newEntered = Signal(); var newRelease = Signal();
            CancellationToken oldToken = default;
            fake.Before = (request, ct) =>
            {
                var uri = request.RequestUri!;
                if (uri.Port == 18788 && uri.AbsolutePath.EndsWith(holdDetail ? "/detail" : "/health"))
                { oldToken = ct; oldEntered.TrySetResult(true); return oldRelease.Task; }
                if (uri.Port == 18789 && uri.AbsolutePath.EndsWith("/health"))
                { newEntered.TrySetResult(true); return newRelease.Task; }
                return Task.CompletedTask;
            };
            Choose(controller, page, "http://127.0.0.1:18788");
            var staleContext = controller.Snapshot.BillionContext!;
            var old = ReadBillion(controller, page.Capture());
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            typeof(OpenCodeSessionInsightsController).GetField("_billionCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(controller, null);
            Choose(controller, page, "http://127.0.0.1:18789", false);
            Check(!oldToken.IsCancellationRequested && controller.Snapshot.Billion.Stats is null
                && controller.Snapshot.Billion.Health is null && !controller.Snapshot.BillionConfirmed, "origin.edit-clears-before-await");
            Check(!controller.SetBillionConfirmed(page.Capture(), true, staleContext)
                && !controller.SetBillionOrigin(page.Capture(), staleContext, "http://127.0.0.1:18788"), "origin.old-render-cannot-reconfirm-or-edit");
            await controller.ReadBillionAsync(page.Capture(), CancellationToken.None, staleContext);
            controller.SetBillionConfirmed(page.Capture(), true, controller.Snapshot.BillionContext);
            var newer = ReadBillion(controller, page.Capture());
            await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var newest = controller.Snapshot;
            var before = fake.Urls.Count;
            oldRelease.SetResult(true);
            await old.WaitAsync(TimeSpan.FromSeconds(5));
            Check(ReferenceEquals(newest, controller.Snapshot) && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Loading
                && fake.Urls.Count == before, "origin.old-response-finally-zero-publication-or-second-HTTP");
            newRelease.SetResult(true);
            await newer.WaitAsync(TimeSpan.FromSeconds(5));
            Check(controller.Snapshot.Billion.Origin == "http://127.0.0.1:18789"
                && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.Loaded
                && captured.SequenceEqual(new[] { "http://127.0.0.1:18788", "http://127.0.0.1:18789" }), "origin.factory-captured-per-read");
        }

        // Ordinary (not detached) cancellation happens on edit, with zero follow-on detail.
        var cancelFake = new OriginFake();
        var (cancelPage, _, cancelController) = Fixture("ses_cancel", origin => new BillionContextClient(origin, cancelFake));
        cancelFake.Source = cancelPage.Source;
        var gate = Signal(); CancellationToken token = default;
        cancelFake.Before = (_, ct) => { token = ct; return gate.Task.WaitAsync(ct); };
        Choose(cancelController, cancelPage, "http://127.0.0.1:18788");
        var pending = ReadBillion(cancelController, cancelPage.Capture());
        Choose(cancelController, cancelPage, "", false);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(token.IsCancellationRequested && cancelFake.Urls.Count == 1 && cancelController.Snapshot.Billion.Origin is null,
            "origin.edit-cancels-real-linked-request");
    }

    private static async Task OriginSourceAndSession()
    {
        var fake = new OriginFake();
        var (page, _, controller) = Fixture("ses_a", origin => new BillionContextClient(origin, new NonDisposingHandler(fake)));
        fake.Source = page.Source;
        Choose(controller, page, "http://127.0.0.1:18788");
        await ReadBillion(controller, page.Capture());
        var renderedA = controller.Snapshot.BillionContext!;
        page.SessionGeneration++; page.SessionId = "ses_b";
        controller.Reset();
        controller.BindBillionContext(page.Capture());
        Check(controller.Snapshot.BillionOrigin == "http://127.0.0.1:18788" && controller.Snapshot.BillionConfirmed
            && controller.Snapshot.Billion.Stats is null && controller.Snapshot.Billion.Origin is null,
            "origin.same-source-session-keeps-choice-not-result");
        Check(!controller.SetBillionOrigin(page.Capture(), renderedA, "http://127.0.0.1:18789"), "origin.old-session-render-rejected");
        await ReadBillion(controller, page.Capture());
        Check(fake.Urls.Last().EndsWith("/ses_b/detail") && controller.Snapshot.Billion.SessionId == "ses_b", "origin.new-session-exact-read");

        var sourceA = page.Source!;
        var bindingA = page.Capture(); var contextA = controller.Snapshot.BillionContext!;
        // Same identity/revision but a different source reference must not inherit anything.
        page.Source = new OpenCodePageSource(sourceA.Identity, sourceA.Revision, () => page.Client!);
        page.SourceGeneration++;
        controller.Reset(); controller.BindBillionContext(page.Capture());
        Check(controller.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing && !controller.Snapshot.BillionConfirmed,
            "origin.new-reference-no-choice-or-consent");
        Check(!controller.SetBillionConfirmed(page.Capture(), true, contextA)
            && !controller.SetBillionOrigin(page.Capture(), contextA, "http://127.0.0.1:18788"), "origin.stale-render-new-source-rejected");
        page.Source = sourceA; page.SourceGeneration = bindingA.SourceGeneration;
        controller.Reset(); controller.BindBillionContext(page.Capture());
        Check(controller.Snapshot.BillionOrigin is null && !controller.Snapshot.BillionConfirmed
            && !controller.SetBillionOrigin(page.Capture(), contextA, "http://127.0.0.1:18788"), "origin.source-ABA-old-context-and-selection-rejected");
        var before = fake.Urls.Count;
        await controller.ReadBillionAsync(page.Capture(), CancellationToken.None, contextA);
        Check(fake.Urls.Count == before, "origin.source-ABA-no-stale-HTTP");
        Choose(controller, page, "http://127.0.0.1:18788");
        var beforeReset = controller.Snapshot.BillionContext!;
        controller.Reset(); controller.BindBillionContext(page.Capture());
        Check(!controller.SetBillionConfirmed(page.Capture(), false, beforeReset), "origin.reset-fences-even-identical-binding");

        // An actual late health response across source A -> B -> A cannot start old detail,
        // even when the full A binding is deliberately restored and its cancellation is delayed.
        var release = Signal(); var entered = Signal();
        CancellationToken oldToken = default;
        fake.Before = (_, ct) => { oldToken = ct; entered.TrySetResult(true); return release.Task; };
        var oldRead = ReadBillion(controller, page.Capture());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        typeof(OpenCodeSessionInsightsController).GetField("_billionCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, null);
        page.Source = new OpenCodePageSource(sourceA.Identity, sourceA.Revision, () => page.Client!);
        page.SourceGeneration++;
        controller.Reset(); controller.BindBillionContext(page.Capture());
        page.Source = sourceA; page.SourceGeneration = bindingA.SourceGeneration;
        controller.Reset(); controller.BindBillionContext(page.Capture());
        var fresh = controller.Snapshot;
        before = fake.Urls.Count;
        release.SetResult(true);
        await oldRead.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!oldToken.IsCancellationRequested && fake.Urls.Count == before && ReferenceEquals(fresh, controller.Snapshot)
            && controller.Snapshot.Billion.Stats is null, "origin.source-ABA-late-health-zero-detail-or-publication");

        // Source B can have no selected session at all: a source-only lifecycle reset must
        // revoke A's private choice without ever needing BindBillionContext(B).
        fake.Before = null;
        Choose(controller, page, "http://127.0.0.1:18788");
        var approvedA = controller.Snapshot.BillionContext!;
        var fullA = page.Capture();
        before = fake.Urls.Count;
        page.Source = new OpenCodePageSource("ep-no-session", fullA.Revision + 1, () => page.Client!);
        page.Revision = page.Source.Revision;
        page.SourceGeneration++;
        page.SessionId = null;
        page.SessionGeneration++;
        controller.Reset(clearBillionOrigin: true); // No B binding or flyout exists.
        Check(controller.Snapshot.BillionContext is null && controller.Snapshot.BillionOrigin is null
            && controller.Snapshot.BillionOriginState == OpenCodeBillionOriginState.Missing
            && !controller.IsBillionConfirmed(fullA), "origin.source-only-reset-revokes-private-consent");
        page.Source = fullA.Source; page.Revision = fullA.Revision; page.SourceGeneration = fullA.SourceGeneration;
        page.Client = fullA.Client; page.PageGeneration = fullA.PageGeneration;
        page.SessionId = fullA.SessionId; page.SessionGeneration = fullA.SessionGeneration;
        var reboundA = controller.BindBillionContext(page.Capture())!;
        Check(reboundA.OriginGeneration > approvedA.OriginGeneration
            && controller.Snapshot.BillionOrigin is null && !controller.Snapshot.BillionConfirmed
            && !controller.IsBillionConfirmed(fullA), "origin.source-only-reset-no-recovery-for-identical-A");
        Check(!controller.SetBillionOrigin(page.Capture(), approvedA, "http://127.0.0.1:18788")
            && !controller.SetBillionConfirmed(page.Capture(), true, approvedA), "origin.source-only-reset-old-events-rejected");
        await controller.ReadBillionAsync(page.Capture(), CancellationToken.None, approvedA);
        await controller.ReadBillionAsync(page.Capture(), CancellationToken.None, reboundA);
        Check(fake.Urls.Count == before && controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.MissingOrigin,
            "origin.source-only-reset-zero-HTTP-for-old-or-current-context");
    }
}
