// Actual selector state methods are extracted by run.ps1. Only native controls/presentation and
// recovery rendering are replaced here; all HTTP, coordinators, gates and state transitions are real.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

internal enum Visibility { Collapsed, Visible }
internal sealed class SelectorElement
{
    internal string Text = "";
    internal bool IsActive, IsEnabled;
    internal Visibility Visibility;
}
internal static class AutomationProperties
{
    internal static void SetName(SelectorElement _, string value) { }
    internal static void SetHelpText(SelectorElement _, string value) { }
}
internal static class ToolTipService { internal static void SetToolTip(SelectorElement _, string value) { } }
internal static class OpenCodeAgentPresentation { internal static void Apply(SelectorElement _, string? value) { } }
internal static class OpenCodeAgentIcons { internal static string DisplayName(string value) => value; }
internal sealed record OpenCodeAgentPickerRow(string Id, string Name, string? Description, bool Selectable, string? DisabledReason, string? Color);

internal sealed partial class OpenCodeAgentSelector
{
    private readonly List<OpenCodeAgentPickerRow> _rows = new();
    private readonly SelectorElement _currentText = new(), _modelText = new(), _separator = new(), _progress = new(),
        _recoveryEntryButton = new(), _reconcileNoRetryButton = new(), _reconcileAllowButton = new(), _recoveryCancelButton = new(),
        _recoveryEntryPanel = new(), _recoveryConfirmPanel = new();
    private readonly AgentSwitchCoordinator _coordinator = new(OpenCodeInteractionGate.Shared);
    private OpenCodeClient? _client;
    private AgentSwitchOrigin? _currentOrigin;
    private CancellationTokenSource? _contextCts;
    private Guid? _retainedUnknownOperationId;
    private string? _currentAgentId, _pendingAgentId;
    private bool _busy, _localSubmissionPending, _displayRefreshPending;
    private Task<bool>? _pendingApplication;
    private long _displayReadVersion;
    private OpenCodeModelRef? _pendingModel, _observedModel;
    private readonly Dictionary<(string Source, string Session), (string? Agent, OpenCodeModelRef? Model)> _pendingSelections = new();
    private const string UnknownAgentText = "智能体：未知", UnknownModelText = "模型：未知";
    private bool HasPendingSelection => _pendingAgentId is not null || _pendingModel is not null;
    private bool HasUnresolvedUnknown => _retainedUnknownOperationId is not null;
    private string? CurrentAgentId => _pendingAgentId ?? _currentAgentId;
    private string? CurrentAgentDisplay => DisplayName(CurrentAgentId);
    private string? CurrentModelDisplay;
    private OpenCodeModelRef? CurrentModel => _pendingModel ?? _observedModel;
    private OpenCodeSessionInfo? CurrentSessionInfo;
    private IReadOnlyList<OpenCodeAgentInfo>? AgentCatalog; // Accepted catalog from the extracted production ReloadAsync.
    private IReadOnlyList<OpenCodeModelInfo> ModelChoices = Array.Empty<OpenCodeModelInfo>();
    private event EventHandler? DisplayChanged;
    private string _status = "";
    private void SetStatus(string value) => _status = value;
    private void UpdateStatusVisibility() { }
    private void SyncRecoveryBar() { }
    private void HideRecovery() { _recoveryConfirmPanel.Visibility = _recoveryEntryPanel.Visibility = Visibility.Collapsed; }
    private void ShowRecovery(string value) => _status = value;
    private void RaiseReport(AgentSwitchResult _) { }
    private Task SurfaceProtectionAsync(AgentSwitchOrigin _) => Task.CompletedTask;
    private Task<Guid?> ReadProtectedOperationIdAsync(AgentSwitchOrigin _) => Task.FromResult<Guid?>(null);

    private static int _checks;
    private static void Check(bool value, string label) { _checks++; if (!value) throw new Exception(label); }
    private static OpenCodeAgentSelector Create(SelectionServer server)
    {
        var source = new OpenCodePageSource("selector-" + Guid.NewGuid(), 1, server.Client);
        return new() { _client = server.Client(), _currentOrigin = new(source, 1, "ses_selection", 1), _contextCts = new(), _localSubmissionPending = false };
    }
    internal static async Task VerifyStateAsync()
    {
        foreach (bool running in new[] { false, true })
        {
            using var server = new SelectionServer { Running = running }; var selector = Create(server);
            await selector.ReloadAsync(selector._currentOrigin!, false);
            Check(selector._currentAgentId == "plan" && selector._observedModel?.Id == "initial", "initial authoritative selector read: " + selector._status
                + "; agent=" + selector._currentAgentId + "; model=" + selector._observedModel?.Id);
            selector.SelectUserAgent("build");
            Check(await selector.ApplyPendingSelectionAsync(true), "send joins immediate agent application");
            Check(server.Agent == "build" && server.Writes == 1 && selector._currentAgentId == "build" && !selector.HasPendingSelection,
                "agent click persists without any prompt; 204 clears pending state, running=" + running);
            var target = new OpenCodeModelRef { Id = "alias", ProviderId = "provider", Variant = "fast" };
            selector.SelectPendingModel(target);
            Check(await selector.ApplyPendingSelectionAsync(true), "send joins immediate model application");
            Check(server.Model == target && server.Writes == 2 && selector._observedModel == target && !selector.HasPendingSelection,
                "model/variant click persists without prompt; read-back owns accepted display, running=" + running);
            server.Agent = "plan"; server.Model = new() { Id = "external", ProviderId = "provider", Variant = "external-variant" };
            await selector.ReloadAsync(selector._currentOrigin!, false);
            Check(selector.CurrentAgentId == "plan" && selector.CurrentModel == server.Model && server.Writes == 2,
                "authoritative external reload replaces accepted model/agent/variant without mutation");
        }
        {
            using var server = new SelectionServer(); var selector = Create(server);
            await selector.ReloadAsync(selector._currentOrigin!, false);
            foreach (var rejection in new[] {
                (400, "{\"_tag\":\"InvalidRequestError\",\"message\":\"private\",\"field\":\"agent\"}"),
                (401, "{\"_tag\":\"UnauthorizedError\",\"message\":\"private\"}"),
                (404, "{\"_tag\":\"SessionNotFoundError\",\"message\":\"private\",\"sessionID\":\"ses_selection\"}"),
                (404, "{\"_tag\":\"LocationNotFoundError\",\"message\":\"private\",\"location\":{\"directory\":\"/selector\"}}") })
            {
                var writes = server.Writes; server.RejectionStatus = rejection.Item1; server.RejectionBody = rejection.Item2;
                selector.SelectUserAgent("build");
                Check(!await selector._pendingApplication! && selector.HasPendingSelection && !selector.HasUnresolvedUnknown
                    && selector._currentAgentId == "plan" && server.Writes == writes + 1,
                    "documented agent rejection is pending/not accepted, never permanent unknown or automatic retry");
                using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(selector._currentOrigin!.Source.Identity, "ses_selection");
                Check(lease.CanSwitchAgent(), "exact documented rejection releases only that agent operation");
            }
            server.RejectionStatus = 0;
            selector.SelectUserAgent("build");
            Check(await selector._pendingApplication! && !selector.HasPendingSelection && selector._currentAgentId == "build",
                "explicitly reselecting a previously rejected choice can be accepted without an unlock button");
        }
        {
            using var server = new SelectionServer(); var selector = Create(server);
            await selector.ReloadAsync(selector._currentOrigin!, false);
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HeldWrite = response.Task;
            var target = new OpenCodeModelRef { Id = "alias", ProviderId = "provider", Variant = "fast" };
            selector.SelectPendingModel(target);
            var pending = selector.ApplyPendingSelectionAsync(true);
            Check(server.Writes == 1 && selector.HasPendingSelection && !pending.IsCompleted && selector._observedModel?.Id == "initial",
                "inflight choice is pending, not manufactured backend acceptance");
            Check(ReferenceEquals(pending, selector.ApplyPendingSelectionAsync(true)), "concurrent send shares exact selection application");
            var reads = server.SessionReads;
            selector.RefreshDisplay();
            Check(server.SessionReads == reads, "external event coalesces while a mutation owns the selector");
            response.SetResult(SelectionServer.Json(204, ""));
            Check(await pending && server.Writes == 1 && !selector.HasPendingSelection && selector._observedModel == target
                && server.SessionReads > reads, "pending event drains after acceptance without duplicate POST");
        }
        {
            using var server = new SelectionServer(); var selector = Create(server);
            await selector.ReloadAsync(selector._currentOrigin!, false);
            var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.HeldRead = held.Task;
            var old = selector.ReloadAsync(selector._currentOrigin!, false);
            server.HeldRead = null; server.Agent = "build"; server.Model = new() { Id = "newer", ProviderId = "provider" };
            await selector.ReloadAsync(selector._currentOrigin!, false);
            held.SetResult(SelectionServer.Json(200, server.SessionJson("plan", new() { Id = "stale", ProviderId = "provider" })));
            await old;
            Check(selector._currentAgentId == "build" && selector._observedModel?.Id == "newer", "late pre-event read cannot overwrite newer authoritative selector state");
            held = new(TaskCreationOptions.RunContinuationsAsynchronously); server.HeldRead = held.Task;
            old = selector.ReloadAsync(selector._currentOrigin!, false);
            selector._currentOrigin = selector._currentOrigin! with { SelectionGeneration = 2 };
            held.SetResult(SelectionServer.Json(200, server.SessionJson("plan", new() { Id = "wrong-owner", ProviderId = "provider" })));
            await old;
            Check(selector._currentAgentId == "build" && selector._observedModel?.Id == "newer", "retired selector origin cannot publish");
        }
        await VerifyInstalledDefaultVariantContractAsync();
        Console.WriteLine($"PASS actual selector persistence/read-back/pending/concurrency/external reload/default contract: {_checks} assertions; native controls are boundaries.");
    }

    private static async Task VerifyInstalledDefaultVariantContractAsync()
    {
        // Installed server evidence: session/session.ts:110-117 deduplicates omitted/default;
        // session/info.ts:26-30 canonicalizes reads; model-resolver.ts:141 reserves default as base.
        // Do not model a named "default" overlay as distinct: even a catalog entry with that ID
        // is bypassed by the installed resolver. The request still preserves omission vs literal ID.
        foreach (string? variant in new string?[] { null, "default" })
        {
            using var server = new SelectionServer
            {
                CanonicalDefaultVariant = true,
                Model = new() { Id = "alias", ProviderId = "provider", Variant = "fast" },
                ModelCatalog = Server.ModelInfo.Replace("[{\"id\":\"fast\"}]", "[{\"id\":\"fast\"},{\"id\":\"default\"}]")
            };
            var selector = Create(server);
            await selector.ReloadAsync(selector._currentOrigin!, false);
            selector.SelectPendingModel(new() { Id = "alias", ProviderId = "provider", Variant = variant });
            Check(await selector._pendingApplication! && server.Writes == 1 && server.Model.Variant == variant,
                "choosing omitted or literal default from a named variant sends/persists the exact requested representation");
            using (var body = JsonDocument.Parse(server.LastModelBody))
            {
                var hasVariant = body.RootElement.GetProperty("model").TryGetProperty("variant", out var wireVariant);
                Check(variant is null ? !hasVariant : hasVariant && wireVariant.GetString() == "default",
                    "wire omits true null, preserves literal default, and does not rewrite opaque IDs");
            }
            Check(selector.CurrentModel?.Variant == "default" && !selector.HasPendingSelection,
                "accepted display uses canonical Session.Info read-back, not the requested representation");
            selector.SelectPendingModel(new() { Id = "alias", ProviderId = "provider", Variant = variant is null ? "default" : null });
            Check(await selector._pendingApplication! && server.Writes == 1 && !selector.HasPendingSelection && selector.CurrentModel?.Variant == "default",
                "already-applied base/default equivalence follows installed server semantics without duplicate POST");

            foreach (string? externalVariant in new string?[] { null, "default", "fast", "Default" })
            {
                server.Model = new() { Id = "alias", ProviderId = "provider", Variant = externalVariant };
                await selector.ReloadAsync(selector._currentOrigin!, false);
                Check(selector.CurrentModel?.Variant == (externalVariant ?? "default") && !selector.HasPendingSelection && server.Writes == 1,
                    "same model/provider external update is authoritative; only omitted/default is normalized, external=" + (externalVariant ?? "<omitted>"));
            }
            selector.SelectPendingModel(new() { Id = "alias", ProviderId = "provider" });
            Check(await selector._pendingApplication! && server.Writes == 2 && selector.CurrentModel?.Variant == "default" && !selector.HasPendingSelection,
                "opaque case-sensitive Default is not falsely considered an applied base variant");
        }
        var baseModel = new OpenCodeModelRef { Id = "alias", ProviderId = "provider" };
        Check(!ModelsMatch(baseModel, baseModel with { Id = "other", Variant = "default" })
            && !ModelsMatch(baseModel, baseModel with { ProviderId = "other", Variant = "default" })
            && !ModelsMatch(baseModel, baseModel with { Variant = "default/opaque" }),
            "default equivalence never crosses model/provider identity or opaque variant values");
    }
}

internal sealed class SelectionServer : HttpMessageHandler
{
    internal string Agent = "plan";
    internal OpenCodeModelRef Model = new() { Id = "initial", ProviderId = "provider" };
    internal bool Running;
    internal int Writes, SessionReads;
    internal int RejectionStatus;
    internal string RejectionBody = "";
    internal bool CanonicalDefaultVariant;
    internal string ModelCatalog = Server.ModelInfo;
    internal string LastModelBody = "";
    internal Task<HttpResponseMessage>? HeldWrite, HeldRead;
    internal OpenCodeClient Client() => new(new HttpClient(this, false), new() { BaseUrl = "https://selector.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
    internal string SessionJson(string? agent = null, OpenCodeModelRef? model = null)
    {
        var observed = model ?? Model;
        if (CanonicalDefaultVariant && observed.Variant is null) observed = observed with { Variant = "default" };
        return JsonSerializer.Serialize(new
        {
            data = new { id = "ses_selection", agent = agent ?? Agent, model = observed, projectID = "p", cost = 0,
                tokens = new { }, time = new { created = 1, updated = 3 }, location = new { directory = "/selector" } },
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
    internal static HttpResponseMessage Json(int status, string json) => new((HttpStatusCode)status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get)
        {
            Writes++;
            if (RejectionStatus != 0) return Json(RejectionStatus, RejectionBody);
            var json = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            var body = document.RootElement;
            if (path.EndsWith("/agent")) Agent = body.GetProperty("agent").GetString()!;
            else if (path.EndsWith("/model"))
            {
                LastModelBody = json;
                var requested = JsonSerializer.Deserialize<OpenCodeModelRef>(body.GetProperty("model"))!;
                if (!CanonicalDefaultVariant || requested.Id != Model.Id || requested.ProviderId != Model.ProviderId
                    || (requested.Variant ?? "default") != (Model.Variant ?? "default")) Model = requested;
            }
            else throw new Exception("Unexpected selection mutation " + path);
            return HeldWrite is { } held ? await held : Json(204, "");
        }
        if (path == "/api/session/ses_selection") { SessionReads++; return HeldRead is { } held ? await held : Json(200, SessionJson()); }
        if (path == "/api/agent") return Json(200, JsonSerializer.Serialize(new { location = new { directory = "/selector" }, data = new[] { "plan", "build" }
            .Select(id => new { id, name = id, mode = "primary", hidden = false, request = new { }, permissions = new { } }) }));
        if (path == "/api/model") return Json(200, "{\"location\":{\"directory\":\"/selector\"},\"data\":[" + ModelCatalog + "]}");
        if (path == "/api/session/active") return Json(200, Running ? "{\"data\":{\"ses_selection\":{\"type\":\"running\"}}}" : "{\"data\":{}}");
        throw new Exception("Selection unexpectedly consulted rollback/execution endpoint " + path);
    }
}
