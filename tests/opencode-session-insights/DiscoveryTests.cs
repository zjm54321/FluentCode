using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private const string DiscoveredOrigin = "http://127.0.0.1:43127";
    private static string Ready(string origin = DiscoveredOrigin)
        => "{\"output\":{\"protocolVersion\":1,\"status\":\"ready\",\"origin\":\"" + origin + "\"}}";

    private sealed class DiscoveryWire : HttpMessageHandler
    {
        public OpenCodePageSource? Source;
        public string? Directory = "/trusted project/位置";
        public string Reply = Ready();
        public string HelloReply = "{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"billion.origin.v1\",\"model.favorites.v1\"]}}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Func<CancellationToken, Task<HttpResponseMessage>>? RpcReply;
        public Action? BeforeSessionInfo;
        public readonly List<(string Method, Uri Uri, string? Auth, string Body)> Requests = new();
        public CancellationToken RpcToken;
        public int RpcCount => Requests.Count(r => r.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/billion.origin"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Source is not null) Check(Monitor.IsEntered(SourceLock(Source)), "discovery.actual-request-under-captured-source-lock");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                if (path == "/prefix/api/rpc/opencode.desktop-bridge/hello")
                {
                    Check(body == "{\"input\":{\"protocolVersions\":[1]}}", "discovery.bridge-hello-first");
                    return Json(HelloReply);
                }
                Check(path == "/prefix/api/rpc/opencode.desktop-bridge/billion.origin" && body == "{\"input\":{}}", "discovery.only-exact-metadata-POST");
                RpcToken = ct;
                return RpcReply is null ? new(Status) { Content = Json(Reply).Content } : await RpcReply(ct);
            }
            Check(request.Method == HttpMethod.Get && !path.Contains("/__bili/") && !path.EndsWith("/message"), "discovery.no-engine-or-session-content-read");
            if (path.StartsWith("/prefix/api/session/", StringComparison.Ordinal))
            {
                BeforeSessionInfo?.Invoke();
                var data = Session(path.Split('/').Last(), null, directory: Directory ?? "");
                return Json("{\"data\":" + data + "}");
            }
            return path switch
            {
                "/prefix/api/session" => Json("{\"data\":[],\"cursor\":{}}"),
                "/prefix/api/model" => Json(Models),
                "/prefix/api/mcp" => Json(Mcp),
                "/prefix/api/plugin" => Json(Plugins(false, false)),
                _ => new(HttpStatusCode.NotFound),
            };
        }
    }

    private sealed class DiscoveryRig : IDisposable
    {
        public readonly DiscoveryWire Wire = new();
        private readonly HttpClient _http;
        public readonly PageStub Page;
        public readonly OpenCodeSessionInsightsController Controller;
        public int EngineFactories;
        public DiscoveryRig(string endpoint = "http://127.0.0.1:43100/prefix")
        {
            _http = new(Wire);
            var client = new OpenCodeClient(_http, new()
            {
                BaseUrl = endpoint, AuthenticationMode = OpenCodeAuthenticationMode.Basic,
                Username = "fixture-user", Secret = "fixture-secret",
            }, true);
            Page = new() { Client = client, SessionId = "ses_discovery", Revision = 11 };
            Page.Source = new("ep-discovery", 11, () => client);
            Wire.Source = Page.Source;
            Controller = new(Page.IsCurrent, _ => { EngineFactories++; throw new InvalidOperationException("Discovery cannot create an engine client."); });
            Controller.Changed += (_, _) => Check(!Monitor.IsEntered(SourceLock(Page.Source!)), "discovery.Changed-outside-source-lock");
        }
        public Task Load(CancellationToken ct = default)
            => Controller.LoadAsync(Page.Capture(), Array.Empty<OpenCodeMessage>(), true, ct);
        public void Dispose() { Page.Client?.Dispose(); _http.Dispose(); }
    }

    private static async Task DiscoveryWireAndLoad()
    {
        using var rig = new DiscoveryRig();
        rig.Controller.BindBillionContext(rig.Page.Capture());
        Check(rig.Wire.Requests.Count == 0, "discovery.binding-alone-still-pure");
        await rig.Load();
        var snapshot = rig.Controller.Snapshot;
        Check(snapshot.BillionOrigin == DiscoveredOrigin && snapshot.BillionOriginState == OpenCodeBillionOriginState.Unconfirmed
            && !snapshot.BillionConfirmed && snapshot.BillionDiscovery?.Status == OpenCodeBillionDiscoveryStatus.Ready,
            "discovery.automatic-origin-without-manual-URL-or-consent");
        Check(snapshot.BillionOriginNote.Contains("从当前 OpenCode 进程声明中检测到 Billion 地址")
            && snapshot.BillionOriginNote.Contains("尚未验证健康状态或引擎归属")
            && snapshot.BillionOriginNote.Contains("请确认此地址与当前 OpenCode 来源的关联"),
            "discovery.process-declaration-provenance-remains-unverified-and-needs-confirmation");
        var rpc = rig.Wire.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/billion.origin"));
        Check(rig.Wire.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/hello")) == 1, "discovery.one-source-handshake-before-origin");
        Check(rpc.Uri.Host == "127.0.0.1" && rpc.Uri.Port == 43100 && rpc.Uri.AbsolutePath == "/prefix/api/rpc/opencode.desktop-bridge/billion.origin"
            && Uri.UnescapeDataString(rpc.Uri.Query) == "?location[directory]=/trusted project/位置", "discovery.same-source-prefix-and-authoritative-location");
        Check(rig.Wire.Requests.All(r => r.Auth == "Basic Zml4dHVyZS11c2VyOmZpeHR1cmUtc2VjcmV0")
            && rpc.Body == "{\"input\":{}}" && !rpc.Uri.ToString().Contains("fixture-secret"), "discovery.same-source-auth-never-query/body");
        Check(rig.EngineFactories == 0 && rig.Controller.BillionReadsStarted == 0 && snapshot.Billion.Health is null && snapshot.Billion.Stats is null,
            "discovery.metadata-does-not-read-health/details-or-construct-engine-client");
        await rig.Controller.ReadBillionAsync(rig.Page.Capture(), default, snapshot.BillionContext);
        Check(rig.EngineFactories == 0 && rig.Controller.Snapshot.Billion.Phase == OpenCodeBillionPhase.NeedsConfirmation,
            "discovery.existing-explicit-confirmation-remains-required");
        Check(rig.Controller.SetBillionConfirmed(rig.Page.Capture(), true, rig.Controller.Snapshot.BillionContext), "discovery.explicit-consent");
        var context = rig.Controller.Snapshot.BillionContext;
        await rig.Load();
        Check(rig.Wire.RpcCount == 2 && rig.Controller.Snapshot.BillionConfirmed && rig.Controller.Snapshot.BillionContext == context,
            "discovery.same-auto-origin-preserves-consent-and-origin/request-epoch");
        rig.Wire.Reply = Ready("http://127.0.0.1:43128");
        await rig.Load();
        Check(rig.Controller.Snapshot.BillionOrigin == "http://127.0.0.1:43128" && !rig.Controller.Snapshot.BillionConfirmed
            && rig.Controller.Snapshot.BillionContext!.OriginGeneration > context!.OriginGeneration, "discovery.changed-auto-origin-requires-new-consent");
        Check(rig.Controller.SetBillionOrigin(rig.Page.Capture(), rig.Controller.Snapshot.BillionContext!, "http://localhost:43200")
            && rig.Controller.SetBillionConfirmed(rig.Page.Capture(), true, rig.Controller.Snapshot.BillionContext), "discovery.manual-override-accepted");
        int before = rig.Wire.RpcCount;
        rig.Wire.Status = HttpStatusCode.Unauthorized;
        await rig.Load();
        Check(rig.Wire.RpcCount == before && rig.Controller.Snapshot.BillionOrigin == "http://localhost:43200" && rig.Controller.Snapshot.BillionConfirmed,
            "discovery.manual-good-origin-and-consent-never-cleared-by-refresh");
        Check(rig.EngineFactories == 0, "discovery.no-engine-calls-throughout-loads-and-edits");
    }

    private static async Task DiscoveryFailures()
    {
        foreach (var hello in new[] {
            "{\"output\":{\"protocolVersion\":null,\"supportedVersions\":[1],\"capabilities\":[]}}",
            "{\"output\":{\"protocolVersion\":1,\"capabilities\":[]}}",
            "{\"output\":{\"protocolVersion\":1,\"capabilities\":[\"model.favorites.v1\"]}}" })
        {
            using var rig = new DiscoveryRig(); rig.Wire.HelloReply = hello;
            await rig.Load();
            Check(rig.Wire.RpcCount == 0 && rig.Controller.Snapshot.BillionDiscovery?.Status == OpenCodeBillionDiscoveryStatus.UnsupportedProtocol
                && rig.Controller.Snapshot.BillionOrigin is null && rig.EngineFactories == 0, "discovery.bridge-capability-gates-origin-metadata-request");
        }
        foreach (var (body, expected) in new[]
        {
            ("{\"output\":{\"protocolVersion\":1,\"status\":\"unavailable\",\"reason\":\"not-ready\"}}", OpenCodeBillionDiscoveryStatus.NotReady),
            ("{\"output\":{\"protocolVersion\":1,\"status\":\"unavailable\",\"reason\":\"disabled\"}}", OpenCodeBillionDiscoveryStatus.Disabled),
            (Ready().Replace("\"protocolVersion\":1", "\"protocolVersion\":2"), OpenCodeBillionDiscoveryStatus.UnsupportedProtocol),
            (Ready().Replace("\"protocolVersion\":1", "\"protocolVersion\":\"1\""), OpenCodeBillionDiscoveryStatus.Malformed),
            (Ready().Replace("\"status\":\"ready\"", "\"status\":\"ready\",\"status\":\"ready\""), OpenCodeBillionDiscoveryStatus.Malformed),
            ("{\"data\":{}}", OpenCodeBillionDiscoveryStatus.Malformed),
            (Ready("http://elsewhere.invalid:43127"), OpenCodeBillionDiscoveryStatus.Malformed),
            (Ready("http://127.0.0.1:43127/hidden/.."), OpenCodeBillionDiscoveryStatus.Malformed),
            (Ready("http://secret@127.0.0.1:43127"), OpenCodeBillionDiscoveryStatus.Malformed),
            ("{\"output\":{\"protocolVersion\":1,\"status\":\"unavailable\",\"reason\":\"disabled\",\"origin\":\"http://127.0.0.1:43127\"}}", OpenCodeBillionDiscoveryStatus.Malformed),
            (new string('x', 4097), OpenCodeBillionDiscoveryStatus.Malformed),
        })
        {
            using var rig = new DiscoveryRig(); rig.Wire.Reply = body;
            await rig.Load();
            Check(rig.Controller.Snapshot.BillionDiscovery?.Status == expected && rig.Controller.Snapshot.BillionOrigin is null
                && !rig.Controller.Snapshot.BillionConfirmed && rig.EngineFactories == 0 && rig.Wire.RpcCount == 1,
                "discovery.unavailable-malformed-version-no-fallback-or-automatic-retry");
            Check(!rig.Controller.Snapshot.BillionOriginNote.Contains("secret"), "discovery.never-echoes-invalid-response");
        }
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            using var rig = new DiscoveryRig(); rig.Wire.Status = status; rig.Wire.Reply = "PRIVATE ERROR BODY";
            await rig.Load();
            Check(rig.Controller.Snapshot.BillionDiscovery is { Status: OpenCodeBillionDiscoveryStatus.HttpError } result && result.HttpStatus == (int)status
                && rig.Controller.Snapshot.BillionOrigin is null && !rig.Controller.Snapshot.BillionOriginNote.Contains("PRIVATE"),
                "discovery.HTTP-status-preserved-without-raw-error-or-assumed-installed-RPC");
        }
        using (var remote = new DiscoveryRig("https://remote.invalid/prefix"))
        {
            await remote.Load();
            Check(remote.Controller.Snapshot.BillionDiscovery?.Status == OpenCodeBillionDiscoveryStatus.UnsupportedSource
                && remote.Wire.RpcCount == 0 && remote.EngineFactories == 0 && remote.Controller.Snapshot.BillionOrigin is null,
                "discovery.remote-source-cannot-bind-unrelated-localhost-or-rewrite-host");
        }
        using (var missing = new DiscoveryRig())
        {
            missing.Wire.Directory = null;
            await missing.Load();
            Check(missing.Controller.Snapshot.BillionDiscovery?.Status == OpenCodeBillionDiscoveryStatus.InvalidLocation && missing.Wire.RpcCount == 0,
                "discovery.missing-session-directory-never-guesses-host-filesystem/default-location");
        }
    }

    private static async Task DiscoveryOwnership()
    {
        using (var rig = new DiscoveryRig())
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Wire.RpcReply = _ => response.Task; // Deliberately ignores cancellation; stale response must still not apply.
            var pending = rig.Load();
            Check(rig.Wire.RpcCount == 1 && rig.Controller.Snapshot.BillionDiscoveryLoading, "discovery.held-RPC-started");
            var oldContext = rig.Controller.Snapshot.BillionContext!;
            Check(rig.Controller.SetBillionOrigin(rig.Page.Capture(), oldContext, "http://127.0.0.1:43999")
                && rig.Controller.SetBillionConfirmed(rig.Page.Capture(), true, rig.Controller.Snapshot.BillionContext), "discovery.newer-manual-edit-and-consent");
            Check(rig.Wire.RpcToken.IsCancellationRequested, "discovery.manual-edit-cancels-metadata-only");
            response.SetResult(Json(Ready())); await pending;
            Check(rig.Controller.Snapshot.BillionOrigin == "http://127.0.0.1:43999" && rig.Controller.Snapshot.BillionConfirmed
                && !rig.Controller.Snapshot.BillionDiscoveryLoading && rig.EngineFactories == 0, "discovery.late-RPC-never-overwrites-newer-manual-origin/consent");
        }
        using (var rig = new DiscoveryRig())
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Wire.RpcReply = _ => response.Task;
            var pending = rig.Load();
            var original = rig.Page.Capture();
            // Hold tokens live independently of Reset to prove generation/owner fencing, not cancellation alone.
            foreach (var field in new[] { "_loadCts", "_discoveryCts" })
                typeof(OpenCodeSessionInsightsController).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig.Controller, null);
            rig.Page.Source = new("ep-other", 12, () => rig.Page.Client!); rig.Page.Revision = 12; rig.Page.SessionId = null;
            rig.Controller.Reset(clearBillionOrigin: true); // Source B has no session and is never bound.
            rig.Page.Source = original.Source; rig.Page.Revision = original.Revision; rig.Page.SessionId = original.SessionId;
            rig.Controller.BindBillionContext(original);
            var fresh = rig.Controller.Snapshot;
            Check(!rig.Wire.RpcToken.IsCancellationRequested, "discovery.ABA-test-keeps-old-transport-token-live");
            response.SetResult(Json(Ready())); await pending;
            Check(ReferenceEquals(rig.Controller.Snapshot, fresh) && fresh.BillionOrigin is null && !fresh.BillionConfirmed,
                "discovery.A-to-no-session-B-to-exact-A-late-result-and-finally-inert");
        }
        using (var rig = new DiscoveryRig())
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Wire.RpcReply = _ => response.Task;
            var old = rig.Load();
            foreach (var field in new[] { "_loadCts", "_discoveryCts" })
                typeof(OpenCodeSessionInsightsController).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig.Controller, null);
            rig.Wire.RpcReply = null; rig.Wire.Reply = Ready("http://127.0.0.1:43129");
            await rig.Load(); var latest = rig.Controller.Snapshot;
            response.SetResult(Json(Ready())); await old;
            Check(ReferenceEquals(rig.Controller.Snapshot, latest) && latest.BillionOrigin == "http://127.0.0.1:43129"
                && !latest.BillionDiscoveryLoading, "discovery.latest-load/snapshot-epoch-wins-over-late-old-result/finally");
        }
        using (var rig = new DiscoveryRig())
        using (var cancel = new CancellationTokenSource())
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Wire.RpcReply = _ => response.Task;
            var pending = rig.Load(cancel.Token); cancel.Cancel(); response.SetResult(Json(Ready())); await pending;
            Check(rig.Controller.Snapshot.BillionOrigin is null && !rig.Controller.Snapshot.BillionDiscoveryLoading && rig.EngineFactories == 0,
                "discovery.cancelled-late-success-does-not-populate-origin");
        }
        using (var rig = new DiscoveryRig())
        {
            bool edited = false;
            // The test-only reentrant edit publishes under this deliberately hostile handler callback; do not
            // confuse it with the production controller's unlocked discovery Changed publication.
            // Use a separate controller without the Changed-under-lock assertion for this artificial callback.
            var controller = new OpenCodeSessionInsightsController(rig.Page.IsCurrent, _ => throw new InvalidOperationException("No engine"));
            rig.Wire.BeforeSessionInfo = () => { if (edited) return; edited = true;
                controller.SetBillionOrigin(rig.Page.Capture(), controller.Snapshot.BillionContext!, "http://127.0.0.1:43998"); };
            await controller.LoadAsync(rig.Page.Capture(), Array.Empty<OpenCodeMessage>(), true, default);
            Check(rig.Wire.RpcCount == 0 && controller.Snapshot.BillionOrigin == "http://127.0.0.1:43998", "discovery.edit-during-session-info-preflight-wins-before-RPC");
        }
    }
}
