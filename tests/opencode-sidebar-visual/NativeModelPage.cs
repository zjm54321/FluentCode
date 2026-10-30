using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages;

// Real Page read/present/commit members are linked verbatim by run.ps1. Only unrelated Page/draft/selector
// chrome is bounded here; actual client, Source, coordinator, shared lease and fake HTTP execute together.
public sealed partial class OpenCodePage
{
    private sealed record Binding(OpenCodeClient Client);
    private sealed record Ticket(Binding Binding);
    private sealed record NativeActionOwner(Ticket Ticket, SessionControlOrigin Origin, SessionControlDraft Draft, Guid OperationId);
    private sealed record ModelPickerBasis(NativeActionOwner Owner, SessionControlState State, SessionControlModels Catalog, SessionControlDefaultModel? Inherited, bool DeferSelection = false);
    private sealed record DeferredModelPickerBasis(NativeActionOwner Owner);
    private readonly OpenCodeSessionControlCoordinator _sessionControls = new(new OpenCodeInteractionGate());
    private readonly SelectorBoundary _agentSelector;
    private readonly NativeActionOwner _owner;
    private bool _current = true;
    internal SessionControlStatus? LastStatus;
    internal string VisibleModel { get; private set; } = "model-A";
    internal readonly ModelHttp Http = new();
    private readonly TextBlock WaitingText = new();
    private readonly HashSet<string> _pendingAckIds = new();
    private bool _deliveredNoticeShown;
    private void RefreshSessionStateUi() { }
    private void UpdateQueuedPrompts() { }
    internal string WaitingCopy(int pending, bool delivered)
    {
        _pendingAckIds.Clear(); for (var index = 0; index < pending; index++) _pendingAckIds.Add("fixture-" + index);
        _deliveredNoticeShown = delivered; UpdateWaitingText(); return WaitingText.Text;
    }
    internal OpenCodePage()
    {
        var client = Http.Client();
        var source = SidebarVisual.Support.BackendAccess.Source("native-model-fixture-" + Guid.NewGuid().ToString("N"), 7, Http.Client);
        _owner = new(new(new(client)), new(source, 7, "ses_native", 1), new("controlled-draft", true), Guid.NewGuid());
        _agentSelector = new(client, () => _current, value => VisibleModel = value);
    }
    internal Task<OpenCodeModelPickerPresentation?> ReadModelsAsync() => ReadNativeModelsAsync(_owner, CancellationToken.None);
    internal async Task CommitAsync(OpenCodeModelPickerResult choice)
    { await CommitNativeModelAsync(_owner, choice, CancellationToken.None); await _agentSelector.RefreshTask; }
    internal void Revoke() { _current = false; SidebarVisual.Support.BackendAccess.Revoke(_owner.Origin.Source); }
    private bool CanStartNativeAction(NativeActionOwner owner, SessionControlOrigin origin)
        => _current && ReferenceEquals(owner, _owner) && ReferenceEquals(origin.Source, _owner.Origin.Source) && !origin.Source.IsRevoked;
    private bool OwnsNativeOperation(NativeActionOwner owner, SessionControlOrigin origin) => CanStartNativeAction(owner, origin);
    private bool CanPublishNativeOperation(NativeActionOwner owner) => CanStartNativeAction(owner, owner.Origin);
    private bool IsComposerSessionWorking(string id) => false;
    private void ReportNativeControlStatus(SessionControlStatus status) => LastStatus = status;
    private void ShowMessage(InfoBarSeverity severity, string title, string detail) => LastStatus = SessionControlStatus.Accepted;
    private sealed class SelectorBoundary(OpenCodeClient client, Func<bool> current, Action<string> update)
    {
        internal bool HasPendingModel => false;
        internal OpenCodeModelRef? CurrentModel => null;
        internal IReadOnlyList<OpenCodeModelInfo> ModelChoices => Array.Empty<OpenCodeModelInfo>();
        internal Task RefreshTask { get; private set; } = Task.CompletedTask;
        internal void SelectPendingModel(OpenCodeModelRef model) => throw new Exception("Working-session route is a separate actual selector regression, not simulated here.");
        internal void RefreshDisplay() => RefreshTask = RefreshAsync();
        private async Task RefreshAsync()
        {
            if (!current()) return;
            var state = await client.GetSessionControlStateAsync("ses_native", CancellationToken.None);
            if (current() && state.Model is { } model) update(model.Id);
        }
    }
}

internal sealed class ModelHttp : HttpMessageHandler
{
    private readonly HttpClient _http;
    internal readonly List<(string Method, string Path, string Query)> Requests = new();
    internal int Writes;
    internal string LastBody = "";
    internal string Model = "{\"id\":\"model-A\",\"providerID\":\"provider\"}";
    internal bool FailWrite = false;
    internal ModelHttp() => _http = new(this, false);
    internal OpenCodeClient Client() => new(_http, new() { BaseUrl = "https://native-fixture.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!; Requests.Add((request.Method.Method, uri.AbsolutePath, uri.Query));
        if (request.Method != HttpMethod.Get)
        {
            if (uri.AbsolutePath != "/api/session/ses_native/model") throw new Exception("Unexpected model mutation route");
            Writes++; LastBody = await request.Content!.ReadAsStringAsync(ct);
            if (FailWrite) throw new HttpRequestException("controlled lost response");
            using var body = JsonDocument.Parse(LastBody); Model = body.RootElement.GetProperty("model").GetRawText();
            return new(HttpStatusCode.NoContent);
        }
        if (uri.AbsolutePath == "/api/model")
        {
            string Row(string id) => "{\"id\":\"" + id + "\",\"modelID\":\"provider-" + id + "\",\"providerID\":\"provider\",\"name\":\"" + id + "\",\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]},\"variants\":[],\"time\":{\"released\":1},\"cost\":[],\"status\":\"active\",\"enabled\":true,\"limit\":{\"context\":1000,\"output\":100}}";
            return Json("{\"location\":{\"directory\":\"/fixture space\"},\"data\":[" + Row("model-A") + "," + Row("model-B") + "]}");
        }
        if (uri.AbsolutePath == "/api/session/ses_native") return Json("{\"data\":{\"id\":\"ses_native\",\"projectID\":\"project\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/fixture space\"},\"model\":" + Model + "}}");
        if (uri.AbsolutePath == "/api/session/active") return Json("{\"data\":{}}");
        if (uri.AbsolutePath.EndsWith("/permission") || uri.AbsolutePath.EndsWith("/form") || uri.AbsolutePath.EndsWith("/inbox")) return Json("{\"data\":[]}");
        throw new Exception("Unexpected native fake HTTP read: " + uri.AbsolutePath);
    }
}
