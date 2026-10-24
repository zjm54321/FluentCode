// Actual selector pending/apply members, actual coordinators/client/gate and page send route.
// Presentation primitives and context lifecycle are explicit headless boundaries.
using System;
using System.Collections.Generic;
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
            Program.Check(promptBody!.Contains("\"delivery\":\"" + delivery + "\"") && promptBody.Contains("\"files\"") == files,
                "ED1 queue/steer and attachments retain original prompt fields");
            Program.Check(selection.Pending == rejected && f.Page.SelectionPending == rejected,
                "ED1 rejection keeps every pending choice; durable acceptance consumes them");
            if (rejected)
                Program.Check(f.Page.Snapshot.HasDraft, "ED1 failed send retains draft alongside choices");
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
