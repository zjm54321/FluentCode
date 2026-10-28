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
        await SelectionRechecks();
        await ParallelRecoveryReads();
        await ImmediateSelections();
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
            using var selection = new DeferredSelectionHost(f.Source);
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
            await selection.ChooseAsync();
            Program.Check(await selection.ApplyPendingSelectionAsync(forNextSend: true) && !selection.Pending
                && f.Agent == "next_agent" && selection.Model is { Id: "catalog-id", Variant: "fast" }
                && f.Http.Posts == 2, "busy agent/model/effort choices persist immediately before send");
            f.Page.PendingAgent(() => selection.ApplyPendingSelectionAsync(forNextSend: true));
            f.Page.SelectionCompletion(selection.CompletePendingSelection);
            await f.Page.Send(delivery);
            Program.Check(f.MutationPaths.Count == 3 && f.MutationPaths[0].EndsWith("/agent")
                && f.MutationPaths[1].EndsWith("/model") && f.MutationPaths[2].EndsWith("/prompt"), "one immediate agent/model apply precedes legacy/enhanced prompt without send duplication: "
                    + delivery + "/files=" + files + "/rejected=" + rejected + "; paths=" + string.Join(",", f.MutationPaths) + "; last=" + f.Http.Path + "; messages=" + string.Join(";", f.Page.Messages));
            Program.Check(f.Http.Calls.SequenceEqual(new[] {
                "GET /api/session/ses_test", "GET /api/agent", "POST /api/session/ses_test/agent",
                "GET /api/session/ses_test", "GET /api/agent", "GET /api/model",
                "GET /api/session/ses_test", "GET /api/model", "POST /api/session/ses_test/model",
                "GET /api/session/ses_test", "GET /api/session/ses_test", "GET /api/agent", "GET /api/model",
                "GET /api/session/ses_test", "POST /api/session/ses_test/prompt" }),
                "immediate choices retain exact fresh guards and authoritative selection/catalog reloads; send adds no duplicate apply: "
                    + string.Join(",", f.Http.Calls));
            Program.Check(promptBody!.Contains("\"delivery\":\"" + delivery + "\"") && promptBody.Contains("\"files\"") == files,
                "ED1 queue/steer and attachments retain original prompt fields");
            Program.Check(!selection.Pending && f.Page.SelectionPending == rejected,
                "prompt rejection retains its draft admission state but does not undo already-accepted session choices");
            if (rejected)
                Program.Check(f.Page.Snapshot.HasDraft, "failed send retains draft without undoing accepted session choices");
        }
        foreach (var scenario in new[] { "unavailable", "revoked", "unknown" })
        {
            using var f = new NativeTests.Fixture();
            using var selection = new DeferredSelectionHost(f.Source);
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
            selection.ChooseModelOnly();
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
                "busy native picker uses cached catalog and delegates choice to the labelled selector boundary without duplicate HTTP");
        }
        Console.WriteLine("PASS immediate agent/model/effort: 8 queue/steer legacy/attachment success/failure flows plus native busy pickers; actual pending/apply/reload members, coordinators and HTTP DTOs.");
    }

    private static async Task ImmediateSelections()
    {
        foreach (var agent in new[] { true, false })
        {
            using var f = new NativeTests.Fixture();
            using var selection = new DeferredSelectionHost(f.Source);
            var started = Program.Signal<bool>();
            var release = Program.Signal<bool>();
            var route = agent ? "/agent" : "/model";
            f.Mutation = async request =>
            {
                Program.Check(request.RequestUri!.AbsolutePath.EndsWith(route), "immediate choice reaches the expected real mutation");
                started.TrySetResult(true);
                await release.Task;
                if (agent) f.Agent = "next_agent";
                else f.Model = "{\"id\":\"catalog-id\",\"providerID\":\"provider\",\"variant\":\"fast\"}";
                return new(System.Net.HttpStatusCode.NoContent);
            };
            if (agent) selection.ChooseAgentOnly(); else selection.ChooseModelOnly();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var pending = selection.ApplyPendingSelectionAsync(forNextSend: true);
            Program.Check(!pending.IsCompleted && ReferenceEquals(pending, selection.ApplyPendingSelectionAsync(forNextSend: true))
                && f.Http.Posts == 1, "choice starts immediately and repeated/send application joins the exact held task");
            f.Page.Text("send joins selection");
            f.Page.PendingAgent(() => selection.ApplyPendingSelectionAsync(forNextSend: true));
            var send = f.Page.Send();
            Program.Check(!send.IsCompleted && !f.MutationPaths.Any(path => path.EndsWith("/prompt")),
                "page send cannot admit a prompt before its immediate selection settles");
            f.Mutation = request => Task.FromResult(Program.Receipt());
            release.SetResult(true);
            Program.Check(await pending, "immediate selection completes successfully");
            await send;
            Program.Check(!selection.Pending && selection.Agent == f.Agent && selection.Model?.Id == (agent ? null : "catalog-id")
                && f.MutationPaths.Count(path => path.EndsWith(route)) == 1
                && f.MutationPaths.Count(path => path.EndsWith("/prompt")) == 1,
                "authoritative reread publishes accepted choice and joined send never duplicates the mutation");
        }
    }

    private static async Task SelectionRechecks()
    {
        foreach (var inherited in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            f.Running = true;
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
                if (r.Method == System.Net.Http.HttpMethod.Get && path == "/api/model")
                {
                    if (Interlocked.Increment(ref count) == 1) allStarted.TrySetResult();
                    await release.Task;
                }
                return await respond(r, ct);
            });
            var previewRead = coordinator.PreviewAsync(origin,
                new(SessionControlKind.SwitchModel, new("catalog-id", "provider", "fast")), draft, _ => true);
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!previewRead.IsCompleted && count == 1 && f.Http.Posts == 0,
                "subsequent-turn model preview awaits its fresh catalog, even while streaming");
            release.SetResult();
            var preview = await previewRead;
            Program.Check(preview.Status == SessionControlStatus.Ready, "streaming does not invalidate a model configuration choice");
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); count = 0;
            var confirm = coordinator.ConfirmAsync(preview.Preview!, Guid.NewGuid(), draft, _ => true, _ => true);
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!confirm.IsCompleted && f.Http.Posts == 0, "model POST waits for confirmation's independently fresh catalog");
            release.SetResult();
            Program.Check((await confirm).Status == SessionControlStatus.Accepted && f.Http.Posts == 1,
                "fresh model confirmation makes exactly one mutation");
            var expected = inherited ? 8 : 6;
            Program.Check(f.Http.Calls.Count == expected && !f.Http.Calls.Any(call => IsGuardRead(call.Split(' ')[1])),
                "model configuration keeps session/catalog/default/authoritative recheck, without idle-only calls: " + expected);
            Program.Check(metric.Depth == expected, "measured model configuration response waves");
        }
        foreach (var loseOrigin in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            f.Running = true;
            var coordinator = new AgentSwitchCoordinator(new OpenCodeInteractionGate());
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var respond = f.Http.Respond; var count = 0; var current = true;
            var metric = new RoundTripCounter();
            f.Http.Respond = metric.Wrap(async (r, ct) => {
                if (r.Method == System.Net.Http.HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/agent")
                {
                    if (Interlocked.Increment(ref count) == 1) allStarted.TrySetResult();
                    await release.Task;
                }
                return await respond(r, ct);
            });
            var change = coordinator.SwitchAsync(new(f.Source, f.Source.Revision, "ses_test", 1), Guid.NewGuid(), "next_agent", _ => current);
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!change.IsCompleted && f.Http.Posts == 0, "agent mutation waits for its fresh directory-scoped catalog");
            if (loseOrigin) current = false;
            release.SetResult(); var result = await change;
            Program.Check(result.Status == (loseOrigin ? AgentSwitchStatus.OriginLost : AgentSwitchStatus.Applied)
                && f.Http.Posts == (loseOrigin ? 0 : 1), "PF2 agent read settlement keeps source/owner fences");
            if (!loseOrigin)
            {
                Program.Check(f.Http.Calls.Count == 3 && metric.Depth == 3 && !f.Http.Calls.Any(call => IsGuardRead(call.Split(' ')[1])),
                    "subsequent-turn agent configuration is session/catalog/mutation, not idle-history gated");
            }
        }
    }

    private static async Task ParallelRecoveryReads()
    {
        foreach (var recovery in new[] { false, true })
        {
            using var f = new NativeTests.Fixture();
            var release = Program.Signal<bool>();
            var allStarted = Program.Signal<bool>();
            var respond = f.Http.Respond;
            var count = 0;
            f.Http.Respond = async (request, ct) =>
            {
                if (IsGuardRead(request.RequestUri!.AbsolutePath))
                {
                    if (Interlocked.Increment(ref count) == 5) allStarted.TrySetResult(true);
                    await release.Task;
                }
                return await respond(request, ct);
            };
            Task<bool> operation;
            if (recovery)
            {
                var id = Guid.NewGuid();
                using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
                {
                    lease.BeginAgentSwitch(id, "next_agent");
                    lease.CompleteAgentSwitch(id, NotificationInteractionStatus.OutcomeUnknown);
                }
                async Task<bool> Recover() => (await new AgentSwitchCoordinator(OpenCodeInteractionGate.Shared)
                    .ReconcileAsync(new(f.Source, f.Source.Revision, "ses_test", 1), id, _ => true)).Status == AgentSwitchStatus.Reconciled;
                operation = Recover();
            }
            else
            {
                async Task<bool> Rollback() => (await new OpenCodeSessionControlCoordinator(OpenCodeInteractionGate.Shared)
                    .PreviewAsync(new(f.Source, f.Source.Revision, "ses_test", 1), new(SessionControlKind.StageRevert),
                        new("empty", true), _ => true)).Status == SessionControlStatus.Ready;
                operation = Rollback();
            }
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(!operation.IsCompleted && count == 5 && f.Http.Posts == 0,
                "rollback/recovery retain all five parallel idle guard reads: recovery=" + recovery);
            release.SetResult(true);
            Program.Check(await operation && f.Http.Posts == 0, "read-only recovery/rollback guard settlement retains conservative checks");
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
    internal sealed partial class DeferredSelectionHost : IDisposable
    {
        private const string UnknownAgentText = "unknown";
        private const string UnknownModelText = "unknown model";
        private sealed record Row(string Id, bool Selectable);
        private readonly List<Row> _rows = new() { new("next_agent", true) };
        private readonly TextBlock _currentText = new();
        private readonly TextBlock _modelText = new();
        private readonly OpenCodeClient _client;
        private readonly AgentSwitchCoordinator _coordinator = new(OpenCodeInteractionGate.Shared);
        private bool _busy;
        private bool _localSubmissionPending = false;
        private Guid? _retainedUnknownOperationId;
        private Task<bool>? _pendingApplication;
        private long _displayReadVersion;
        private string? CurrentModelDisplay { get; set; }
        private OpenCodeSessionInfo? CurrentSessionInfo { get; set; }
        private IReadOnlyList<OpenCodeModelInfo> ModelChoices { get; set; } = Array.Empty<OpenCodeModelInfo>();
        private readonly Dictionary<(string Source, string Session), (string? Agent, OpenCodeModelRef? Model)> _pendingSelections = new();
        private AgentSwitchOrigin? _currentOrigin;
        private readonly CancellationTokenSource _contextCts = new();
        private string? _pendingAgentId, _currentAgentId = "old_agent";
        private OpenCodeModelRef? _pendingModel, _observedModel;
        private bool HasPendingSelection => _pendingAgentId is not null || _pendingModel is not null;
        private bool HasUnresolvedUnknown => _retainedUnknownOperationId is not null;
        private string? CurrentAgentDisplay => _pendingAgentId ?? _currentAgentId;
        internal string? Agent => CurrentAgentDisplay;
        internal OpenCodeModelRef? Model => _pendingModel ?? _observedModel;
        internal bool Pending => HasPendingSelection;
        internal DeferredSelectionHost(OpenCodePageSource source)
        {
            _currentOrigin = new(source, source.Revision, "ses_test", 1);
            _client = source.CreateClient();
        }
        public void Dispose() { _contextCts.Dispose(); _client.Dispose(); }
        internal async Task ChooseAsync()
        {
            SelectPendingAgent(_currentOrigin!, "next_agent");
            await ApplyPendingSelectionAsync(forNextSend: true);
            SelectPendingModel(new() { Id = "catalog-id", ProviderId = "provider", Variant = "fast" });
        }
        internal void ChooseModelOnly() => SelectPendingModel(new() { Id = "catalog-id", ProviderId = "provider", Variant = "fast" });
        internal void ChooseAgentOnly() => SelectPendingAgent(_currentOrigin!, "next_agent");
        private bool IsOriginCurrent(AgentSwitchOrigin origin) => ReferenceEquals(origin, _currentOrigin) && !origin.Source.IsRevoked;
        private string? SwitchBlockedReason() => _busy || HasUnresolvedUnknown ? "blocked" : null;
        private void ApplyCurrentAgentPresentation() { }
        private void SetStatus(string _) { }
        private void UpdateControls() { }
        private void ApplyModel(OpenCodeModelRef? model) => _observedModel = model;
        // Presentation/recovery boundaries only; real RunSwitchAsync, ReloadAsync, pending/apply,
        // coordinators, transport and guards are compiled verbatim by run.ps1.
        private void ApplyAgents(IReadOnlyList<OpenCodeAgentInfo> data, string? currentAgentId)
        {
            _currentAgentId = currentAgentId;
            _currentText.Text = currentAgentId ?? UnknownAgentText;
        }
        private Task SurfaceProtectionAsync(AgentSwitchOrigin _) => Task.CompletedTask;
        private void RaiseReport(AgentSwitchResult _) { }
        private async Task ApplySwitchResultAsync(AgentSwitchOrigin origin, AgentSwitchResult result)
        {
            if (result.Status == AgentSwitchStatus.Applied) await ReloadAsync(origin, surfaceProtection: false);
            if (result.UnknownProtectionRetained) _retainedUnknownOperationId = result.OperationId;
        }
    }
}
