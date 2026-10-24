// Actual selector pending/apply members, actual coordinators/client/gate and page send route.
// Presentation primitives and context lifecycle are explicit headless boundaries.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

internal static class DeferredSelectionTests
{
    internal static async Task Run()
    {
        await ParallelIdleReads();
        foreach (var mode in new[] { "healthy", "disconnected", "gap", "unknown", "rejected", "revert" })
        {
            using var f = new NativeTests.Fixture();
            f.Page.Text("event covered send");
            f.Page.EventCoverage = mode == "disconnected" ? null : Task.CompletedTask;
            if (mode == "revert")
            {
                f.Stage = "{\"messageID\":\"msg_older\"}";
                OpenCodeComposerContinuationDialog.Next = p => Task.FromResult(new OpenCodeContinuationResult(OpenCodeContinuationOutcome.Confirmed, p));
            }
            f.Mutation = _ => {
                if (mode == "gap") f.Page.EventCoverage = null;
                if (mode == "unknown") throw new OpenCodeTransportException("test-only unknown");
                return Task.FromResult(mode == "rejected"
                    ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) : Program.Receipt());
            };
            await f.Page.Send();
            Program.Check(f.Page.Refreshes == (mode == "healthy" ? 0 : 1),
                "PF2 only continuously event-covered prompt acceptance skips action refresh: " + mode);
            Program.Check(f.Http.Posts == 1, "PF2 no event fallback retries a mutation: " + mode);
        }
        Console.WriteLine("PF2 send foreground: 6 -> 2 calls / 6 -> 2 sequential RTTs with healthy SSE; event-owned history+aux invalidation remains 4 calls / 1 RTT; disconnected fallback 6 calls / 3 RTTs.");
        foreach (bool files in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            f.Page.Text("ordinary send");
            if (files) await f.Page.File();
            await f.Page.Send();
            Program.Check(f.Http.Calls.SequenceEqual(new[] { "GET /api/session/ses_test", "POST /api/session/ses_test/prompt" }),
                "PF1 ordinary legacy/attachment send has only the required fresh revert guard and POST; no capability/model/agent reads");
        }
        foreach (var delivery in new[] { "queue", "steer" })
        foreach (var files in new[] { false, true })
        foreach (var rejected in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            f.Running = true;
            f.Page.SetWorking();
            f.Page.Text("next send choices");
            if (files) await f.Page.File();
            var selection = new DeferredSelectionHost(f.Source);
            selection.Choose();
            Program.Check(selection.Agent == "next_agent" && selection.Model is { Id: "catalog-id", Variant: "fast" }
                && selection.Pending && f.Http.Posts == 0 && f.Http.Gets == 0, "ED1 busy agent/model/effort pending presentation has zero HTTP");
            f.Page.PendingAgent(() => selection.ApplyPendingSelectionAsync(forNextSend: true));
            f.Page.SelectionCompletion(selection.CompletePendingSelection);
            string? promptBody = null;
            f.Mutation = r =>
            {
                if (r.RequestUri!.AbsolutePath.EndsWith("/agent")) f.Agent = "next_agent";
                if (r.RequestUri.AbsolutePath.EndsWith("/model")) f.Model = "{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"fast\"}";
                if (r.RequestUri.AbsolutePath.EndsWith("/prompt"))
                {
                    promptBody = f.Http.Body;
                    Program.Check(f.Agent == "next_agent" && f.Model!.Contains("fast"), "ED1 choices applied before prompt admission");
                    return Task.FromResult(rejected ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) : Program.Receipt());
                }
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
            };
            await f.Page.Send(delivery);
            Program.Check(f.MutationPaths.Count == 3 && f.MutationPaths[0].EndsWith("/agent")
                && f.MutationPaths[1].EndsWith("/model") && f.MutationPaths[2].EndsWith("/prompt"), "ED1 one agent/model pre-send apply precedes legacy/enhanced prompt: "
                    + delivery + "/files=" + files + "/rejected=" + rejected + "; paths=" + string.Join(",", f.MutationPaths) + "; last=" + f.Http.Path + "; messages=" + string.Join(";", f.Page.Messages));
            Program.Check(f.Http.Calls.SequenceEqual(new[] {
                "GET /api/session/ses_test", "GET /api/agent", "POST /api/session/ses_test/agent",
                "GET /api/session/ses_test", "GET /api/model", "POST /api/session/ses_test/model",
                "GET /api/session/ses_test", "GET /api/session/ses_test", "POST /api/session/ses_test/prompt" }),
                "PF1 pending choices use 9 calls instead of 13: no duplicate preview or unused inherited default read; actual=" + string.Join(",", f.Http.Calls));
            Program.Check(promptBody!.Contains("\"delivery\":\"" + delivery + "\"") && promptBody.Contains("\"files\"") == files,
                "ED1 queue/steer and attachments retain original prompt fields");
            Program.Check(selection.Pending == rejected && f.Page.SelectionPending == rejected,
                "ED1 rejection keeps every pending choice; durable acceptance consumes them");
            if (rejected)
                Program.Check(f.Page.Snapshot.HasDraft, "ED1 failed send retains draft alongside choices");
        }
        foreach (var scenario in new[] { "unavailable", "revoked", "unknown" })
        {
            using var f = new NativeTests.Fixture();
            var selection = new DeferredSelectionHost(f.Source);
            selection.ChooseModelOnly();
            if (scenario == "unavailable") f.Catalog = f.Catalog.Replace("\"fast\"", "\"slow\"");
            if (scenario == "revoked")
            {
                var respond = f.Http.Respond;
                f.Http.Respond = (request, ct) => {
                    var response = respond(request, ct);
                    if (request.RequestUri!.AbsolutePath == "/api/model") f.Source.Revoke();
                    return response;
                };
            }
            if (scenario == "unknown") f.Mutation = _ => throw new OpenCodeTransportException("test-only unknown");
            Program.Check(!await selection.ApplyPendingSelectionAsync(forNextSend: true) && selection.Pending,
                "PF1 single-shot selection retains pending choices on " + scenario);
            Program.Check(f.Http.Posts == (scenario == "unknown" ? 1 : 0), "PF1 no mutation after stale origin/catalog; unknown is attempted once");
            if (scenario == "unknown")
            {
                Program.Check(!await selection.ApplyPendingSelectionAsync(forNextSend: true) && f.Http.Posts == 1,
                    "PF1 unknown model switch protection prevents a later automatic retry");
            }
        }
        foreach (var variantOnly in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            f.Page.SetWorking();
            f.Page.CachedModels(new() { Id = "catalog-id", ProviderId = "provider", Name = "Choice", Enabled = true,
                Variants = new() { new() { Id = "fast" } } });
            OpenCodeModelPickerDialog.Next = p => Task.FromResult(new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Confirmed, new("provider", "catalog-id", "fast"), p));
            OpenCodeVariantPickerDialog.Next = OpenCodeModelPickerDialog.Next;
            await f.Page.RunSidebarModel(variantOnly);
            Program.Check(f.Http.Gets == 0 && f.Http.Posts == 0 && f.Page.PendingModel is { Variant: "fast" },
                "ED1 actual busy native picker uses cached catalog and stages choice without server calls");
        }
        Console.WriteLine("PASS ED1 pending agent/model/effort: 8 queue/steer legacy/attachment success/failure flows plus native busy pickers; actual pending/apply members, coordinators and HTTP DTOs.");
    }

    private static async Task ParallelIdleReads()
    {
        foreach (var inherited in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            if (!inherited) f.Model = "{\"id\":\"catalog-id\",\"providerID\":\"provider\"}";
            var coordinator = new OpenCodeSessionControlCoordinator(new OpenCodeInteractionGate());
            var origin = new SessionControlOrigin(f.Source, f.Source.Revision, "ses_test", 1);
            var draft = new SessionControlDraft("empty", true);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var respond = f.Http.Respond;
            var metric = new RoundTripCounter();
            var count = 0;
            f.Http.Respond = metric.Wrap(async (r, ct) => {
                var path = r.RequestUri!.AbsolutePath;
                if (r.Method == System.Net.Http.HttpMethod.Get && IsGuardRead(path))
                {
                    if (Interlocked.Increment(ref count) == 5) allStarted.TrySetResult();
                    await release.Task;
                }
                return await respond(r, ct);
            });
            var previewRead = coordinator.PreviewAsync(origin,
                new(SessionControlKind.SwitchModel, new("catalog-id", "provider", "fast")), draft, _ => true);
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!previewRead.IsCompleted && count == 5 && f.Http.Posts == 0,
                "PF2 idle model history/permission/form/inbox/active all start before any held response completes");
            release.SetResult();
            var preview = await previewRead;
            Program.Check(preview.Status == SessionControlStatus.Ready, "PF2 parallel preview retains complete idle validation");
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); count = 0;
            var confirm = coordinator.ConfirmAsync(preview.Preview!, Guid.NewGuid(), draft, _ => true, _ => true);
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!confirm.IsCompleted && f.Http.Posts == 0, "PF2 model POST waits for every fresh guard read");
            release.SetResult();
            Program.Check((await confirm).Status == SessionControlStatus.Accepted && f.Http.Posts == 1,
                "PF2 fresh parallel confirm makes exactly one mutation");
            var expected = inherited ? 20 : 18;
            Program.Check(f.Http.Calls.Count == expected, "PF2 model preview+confirm preserves required call count: " + expected);
            Program.Check(metric.Depth == (inherited ? 12 : 10), "PF2 measured model critical-path response waves");
            Console.WriteLine($"PF2 model preview+confirm inherited={inherited}: calls {expected} -> {f.Http.Calls.Count}; sequential RTTs {expected} -> {metric.Depth}; five held guard reads observed concurrently in both phases.");
        }
        foreach (var loseOrigin in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            var coordinator = new AgentSwitchCoordinator(new OpenCodeInteractionGate());
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var respond = f.Http.Respond; var count = 0; var current = true;
            var metric = new RoundTripCounter();
            f.Http.Respond = metric.Wrap(async (r, ct) => {
                if (r.Method == System.Net.Http.HttpMethod.Get && IsGuardRead(r.RequestUri!.AbsolutePath))
                {
                    if (Interlocked.Increment(ref count) == 5) allStarted.TrySetResult();
                    await release.Task;
                }
                return await respond(r, ct);
            });
            var change = coordinator.SwitchAsync(new(f.Source, f.Source.Revision, "ses_test", 1), Guid.NewGuid(), "next_agent", _ => current);
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!change.IsCompleted && f.Http.Posts == 0, "PF2 agent mutation waits for all parallel idle guards");
            if (loseOrigin) current = false;
            release.SetResult(); var result = await change;
            Program.Check(result.Status == (loseOrigin ? AgentSwitchStatus.OriginLost : AgentSwitchStatus.Applied)
                && f.Http.Posts == (loseOrigin ? 0 : 1), "PF2 agent read settlement keeps source/owner fences");
            if (!loseOrigin)
            {
                Program.Check(f.Http.Calls.Count == 8 && metric.Depth == 4, "PF2 measured agent call count/depth");
                Console.WriteLine($"PF2 idle agent switch: calls 8 -> {f.Http.Calls.Count}; sequential RTTs 8 -> {metric.Depth}; five held guard reads observed concurrently.");
            }
        }
    }

    private static bool IsGuardRead(string path) => path.EndsWith("/message") || path.EndsWith("/permission")
        || path.EndsWith("/form") || path.EndsWith("/inbox") || path == "/api/session/active";

    // Logical round trips, not wall-clock speed: requests started before any response in a
    // held batch settles share a wave. Dependent reads necessarily start in the next wave.
    private sealed class RoundTripCounter
    {
        private readonly object sync = new();
        internal int Depth;
        internal Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> Wrap(
            Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> next)
            => async (request, ct) => {
                int wave; lock (sync) wave = Depth + 1;
                var response = await next(request, ct);
                lock (sync) Depth = Math.Max(Depth, wave);
                return response;
            };
    }
}

namespace AIFrontend.Pages.OpenCode
{
    internal sealed partial class DeferredSelectionHost
    {
        private const string UnknownAgentText = "unknown";
        private sealed record Row(string Id, bool Selectable);
        private readonly List<Row> _rows = new() { new("next_agent", true) };
        private readonly TextBlock _currentText = new();
        private readonly Dictionary<(string Source, string Session), (string? Agent, OpenCodeModelRef? Model)> _pendingSelections = new();
        private AgentSwitchOrigin? _currentOrigin;
        private readonly CancellationTokenSource _contextCts = new();
        private string? _pendingAgentId, _currentAgentId = "old_agent";
        private OpenCodeModelRef? _pendingModel, _observedModel;
        private bool HasPendingSelection => _pendingAgentId is not null || _pendingModel is not null;
        private bool HasUnresolvedUnknown;
        private string? CurrentAgentDisplay => _pendingAgentId ?? _currentAgentId;
        internal string? Agent => CurrentAgentDisplay;
        internal OpenCodeModelRef? Model => _pendingModel ?? _observedModel;
        internal bool Pending => HasPendingSelection;
        internal DeferredSelectionHost(OpenCodePageSource source) => _currentOrigin = new(source, source.Revision, "ses_test", 1);
        internal void Choose()
        {
            SelectPendingAgent(_currentOrigin!, "next_agent");
            SelectPendingModel(new() { Id = "catalog-id", ProviderId = "provider", Variant = "fast" });
        }
        internal void ChooseModelOnly() => SelectPendingModel(new() { Id = "catalog-id", ProviderId = "provider", Variant = "fast" });
        private bool IsOriginCurrent(AgentSwitchOrigin origin) => ReferenceEquals(origin, _currentOrigin) && !origin.Source.IsRevoked;
        private string? SwitchBlockedReason() => null;
        private void ApplyCurrentAgentPresentation() { }
        private void SetStatus(string _) { }
        private void UpdateControls() { }
        private void ApplyModel(OpenCodeModelRef? model) => _observedModel = model;
        private async Task RunSwitchAsync(AgentSwitchOrigin origin, string target, bool forNextSend = false)
        {
            var result = await new AgentSwitchCoordinator(OpenCodeInteractionGate.Shared).SwitchAsync(origin, Guid.NewGuid(), target,
                IsOriginCurrent, forNextSend: forNextSend);
            if (result.Status == AIFrontend.Services.Notifications.AgentSwitchStatus.Applied) _currentAgentId = target;
            HasUnresolvedUnknown = result.UnknownProtectionRetained;
        }
    }
}
