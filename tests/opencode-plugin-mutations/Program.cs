using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    // Independent frozen schema literals: preset-rpc.ts:4-28; clear-encrypted/src/rpc.ts:2-56.
    private const string ApplyRoute = "/prefix/api/rpc/oh-my-opencode-slim.session-preset.v1/apply";
    private const string ClearRoute = "/prefix/api/rpc/opencode.qol.clear-encrypted-contents/clear";
    private const string Directory = "/trusted project/位置&literal=1";
    private const string Name = "  MiXeD exact / 中文  ";
    private const string Preset = "{\"output\":{\"sessionID\":\"ses_fixture\",\"status\":\"bound\",\"presetName\":\"exact\",\"affects\":\"future-children-only\"}}";
    private const string Clear = "{\"output\":{\"ok\":true,\"changedMessages\":0,\"changedEvents\":0,\"removedFields\":0}}";
    private static int _checks;
    private static int _cases;
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        _checks++;
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal bool IsPreset;
        internal string ExactName = Name;
        internal string Reply = Clear;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal int Sends;
        internal bool Block;
        internal bool Disconnect;
        internal bool Chunked;
        internal bool FailRefresh;
        internal CancellationToken SentToken;
        internal Action? OnSend;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            SentToken = ct;
            var uri = request.RequestUri!;
            if (FailRefresh && request.Method == HttpMethod.Get && uri.AbsolutePath == "/prefix/api/session/ses_fixture")
                return new(HttpStatusCode.InternalServerError) { Content = new StringContent("fixture secret failed refresh") };
            Check(request.Method == HttpMethod.Post && uri.Host == "127.0.0.1" && uri.Port == 43100
                && uri.AbsolutePath == (IsPreset ? ApplyRoute : ClearRoute), "exact mutation endpoint and configured source prefix");
            Check(uri.Query == "?" + Uri.EscapeDataString("location[directory]") + "=" + Uri.EscapeDataString(Directory), "captured directory is exact deepObject query");
            Check(request.Headers.Authorization?.ToString() == "Basic Zml4dHVyZS11c2VyOmZpeHR1cmUtc2VjcmV0"
                && request.Content?.Headers.ContentType?.MediaType == "application/json", "authenticated JSON transport");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var input = body.RootElement.GetProperty("input");
            Check(Count(body.RootElement) == 1 && input.GetProperty("sessionID").GetString() == "ses_fixture"
                && Count(input) == (IsPreset ? 2 : 1), "exact input keys; no invented operation id or clear/name list");
            if (IsPreset) Check(input.GetProperty("presetName").GetString() == ExactName, "name preserved without trim or case folding");
            OnSend?.Invoke();
            Started.TrySetResult();
            if (Block) await Release.Task.WaitAsync(ct);
            if (Disconnect) throw new HttpRequestException("fixture secret backend transport text");
            return new(Status) { Content = Chunked ? new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(Reply))) : new StringContent(Reply) };
        }
    }

    private static int Count(JsonElement value)
    {
        var count = 0;
        foreach (var property in value.EnumerateObject()) count++;
        return count;
    }

    private sealed class Rig : IDisposable
    {
        internal readonly Wire Wire = new();
        internal readonly HttpClient Http;
        internal readonly OpenCodeInteractionGate Gate = new();
        internal readonly OpenCodePluginMutationCoordinator Coordinator;
        internal readonly OpenCodePageSource Source;
        internal readonly PluginMutationOrigin Origin;
        internal PluginMutationOrigin Current;
        internal Action? OnCreate;
        internal readonly TimeSpan Timeout;
        internal Rig(bool preset = false, TimeSpan? timeout = null, long revision = 7)
        {
            Wire.IsPreset = preset;
            Wire.Reply = preset ? Preset : Clear;
            Http = new(Wire);
            Timeout = timeout ?? TimeSpan.FromSeconds(5);
            Source = new("mutation-fixture", revision, () => { OnCreate?.Invoke(); return Client(); });
            Origin = new(Source, Source.Revision, "ses_fixture", Directory, 11);
            Current = Origin;
            Coordinator = new(Gate);
        }
        internal OpenCodeClient Client() => new(Http, new()
        {
            BaseUrl = "http://127.0.0.1:43100/prefix", AuthenticationMode = OpenCodeAuthenticationMode.Basic,
            Username = "fixture-user", Secret = "fixture-secret", Timeout = Timeout,
        }, transportSafetyConfirmed: true);
        internal bool CanStart(PluginMutationOrigin origin) => origin == Current;
        internal Task<PluginMutationResult> Run(CancellationToken ct = default) => Wire.IsPreset
            ? Coordinator.ApplyPresetAsync(Origin, Wire.ExactName, CanStart, ct)
            : Coordinator.ClearEncryptedAsync(Origin, CanStart, ct);
        public void Dispose() { Source.Revoke(); Http.Dispose(); }
    }

    private static async Task Protection(Rig rig, bool retained)
    {
        using var lease = await rig.Gate.EnterAsync(rig.Source.Identity, rig.Origin.SessionId);
        Check((lease.GetSessionControlProtection() is not null) == retained, "exact control protection state");
        Check(lease.CanSubmitComposer() == !retained && lease.CanSwitchAgent() == !retained
            && lease.CanControlSession() == !retained, "unknown blocks composer, agent and session controls");
        Check(lease.CanSubmit("per_fixture", NotificationNavigationKind.Permission) == !retained
            && lease.CanSubmit("form_fixture", NotificationNavigationKind.Form) == !retained, "unknown blocks permission and form replies");
    }

    private static async Task Success()
    {
        using (var rig = new Rig(preset: true))
        {
            using var client = rig.Client();
            var begins = 0;
            var state = await client.ApplySessionPresetGuardedAsync("ses_fixture", Directory, Name, send => { begins++; send(); return true; });
            Check(begins == 1 && state.IsFor(client, "ses_fixture", Directory)
                && !state.IsFor(client, "ses_fixture", Directory + "/other"), "preset uses existing client-owned, exact-directory parser");
            using var foreign = rig.Client();
            Check(!state.IsFor(foreign, "ses_fixture", Directory), "identical configured URLs do not share preset snapshot ownership");
            var result = await rig.Run();
            Check(result.Status == PluginMutationStatus.Accepted && result.Preset?.Status == OpenCodeSessionPresetStatus.Bound
                && result.RequestStarted && !result.ProtectionRetained && result.OperationId != Guid.Empty, "valid preset acceptance");
            await Protection(rig, false);
            _cases++;
        }
        foreach (var name in new[] { "   ", "EXACT" })
        {
            using var rig = new Rig(preset: true);
            rig.Wire.ExactName = name;
            rig.Wire.Reply = Preset.Replace("\"bound\"", "\"unbound\"").Replace("\"exact\"", "null");
            Check((await rig.Run()).Status == PluginMutationStatus.Accepted, "schema permits unbound/null output and all nonempty exact names");
            _cases++;
        }
        foreach (var counts in new[] { (0L, 0L, 0L), (2L, 3L, 8L), (9007199254740991L, 0L, 1L) })
        {
            using var rig = new Rig();
            rig.Wire.Reply = JsonSerializer.Serialize(new { output = new { ok = true, changedMessages = counts.Item1, changedEvents = counts.Item2, removedFields = counts.Item3 } });
            var result = await rig.Run();
            Check(result.Status == PluginMutationStatus.Accepted && result.Clear == new ClearEncryptedResult(counts.Item1, counts.Item2, counts.Item3), "zero/nonzero/safe maximum clear counters");
            // Refresh is deliberately external to the mutation coordinator and cannot rewrite its acceptance.
            rig.Wire.FailRefresh = true;
            using var readClient = rig.Source.CreateClient();
            try { await readClient.GetSessionControlStateAsync("ses_fixture"); throw new InvalidOperationException("refresh should fail"); }
            catch (OpenCodeException) { }
            Check(result.Status == PluginMutationStatus.Accepted, "read refresh failure does not change confirmed acceptance");
            await Protection(rig, false);
            _cases++;
        }
    }

    private static async Task InvalidOutputs()
    {
        foreach (var reply in new[]
        {
            Preset.Replace("ses_fixture", "ses_foreign"), Preset.Replace("future-children-only", "current-session"),
            Preset.Replace("\"bound\"", "\"BOUND\""), Preset.Replace("\"exact\"", "42"),
            Preset.Replace("\"sessionID\":", "\"sessionID\":\"ses_fixture\",\"sessionID\":"), "{\"data\":null}",
        })
        {
            using var rig = new Rig(preset: true);
            rig.Wire.Reply = reply;
            var result = await rig.Run();
            Check(result.Status == PluginMutationStatus.OutcomeUnknown && result.Preset is null, "malformed/foreign preset success stays unknown");
            await Protection(rig, true);
            _cases++;
        }
        foreach (var reply in new[]
        {
            Clear.Replace(":true", ":false"), Clear.Replace("\"changedMessages\":0", "\"changedMessages\":-1"),
            Clear.Replace("\"changedEvents\":0", "\"changedEvents\":1.5"),
            Clear.Replace("\"removedFields\":0", "\"removedFields\":9007199254740992"),
            Clear.Replace("\"changedMessages\":0,", ""), Clear.Replace("\"changedMessages\":0", "\"changedMessages\":\"0\""),
            Clear.Replace("\"ok\":true", "\"ok\":true,\"extra\":1"),
            Clear.Replace("\"changedMessages\":0", "\"changedMessages\":0,\"changedMessages\":1"),
            "not JSON fixture secret", "[]", "{\"output\":null}", new string('x', 17000),
        })
        {
            using var rig = new Rig();
            rig.Wire.Reply = reply;
            rig.Wire.Chunked = reply.Length > 16000;
            var result = await rig.Run();
            Check(result.Status == PluginMutationStatus.OutcomeUnknown && result.Clear is null
                && !result.SafeReason!.Contains("fixture secret"), "invalid/oversize success is sanitized unknown");
            await Protection(rig, true);
            _cases++;
        }
    }

    private static string Error(string code, string message = "fixture secret arbitrary backend message", string tag = "RpcError", bool data = false)
        => data ? JsonSerializer.Serialize(new { _tag = tag, type = code, message, data = (object?)null })
            : JsonSerializer.Serialize(new { _tag = tag, type = code, message });

    private static async Task Failures()
    {
        foreach (var item in new[]
        {
            (400, Error("rpc.invalid_input"), true), (400, Error("rpc.method_not_found"), true),
            (401, "{\"_tag\":\"UnauthorizedError\",\"message\":\"fixture secret\"}", true),
            (401, "unauthorized fixture secret", false), (403, "{}", false), (404, "{}", false),
            (500, Error("rpc.internal", tag: "RpcInternalError"), false),
            (500, Error("rpc.invalid_output", tag: "RpcInternalError"), false),
            (400, Error("rpc.unavailable"), false), (500, Error("rpc.unavailable"), false),
            (400, Error("rpc.invalid_input", tag: "Unexpected"), false), (500, Error("rpc.invalid_input"), false),
            (400, Error("unknown"), false), (302, "{}", false),
        })
        {
            using var rig = new Rig(preset: true);
            rig.Wire.Status = (HttpStatusCode)item.Item1;
            rig.Wire.Reply = item.Item2;
            var result = await rig.Run();
            Check(result.Status == (item.Item3 ? PluginMutationStatus.Rejected : PluginMutationStatus.OutcomeUnknown)
                && result.RequestStarted && !result.SafeReason!.Contains("fixture secret"), "only recognized structured pre-handler rejection releases protection");
            await Protection(rig, !item.Item3);
            _cases++;
        }
        var rejected = new[]
        {
            "Current OpenCode host lacks required clearing interface", "Unable to determine session location",
            "Unable to determine local database", "Standalone Bun runtime is unavailable or incompatible",
            "Clearing operation is already in progress for this session",
        };
        var unknown = new[]
        {
            "Failed to stop or wait for session to end", "Operation was cancelled or timed out",
            "Clearing process was not confirmed, check database status", "Operation failed, please check service status",
            "Clear was committed, but UI refresh failed; please reopen session", "fixture secret arbitrary reason",
        };
        foreach (var reasons in new[] { rejected, unknown })
        foreach (var reason in reasons)
        {
            using var rig = new Rig();
            rig.Wire.Status = HttpStatusCode.BadRequest;
            rig.Wire.Reply = Error("failed", reason, data: true);
            var proven = ReferenceEquals(reasons, rejected);
            var result = await rig.Run();
            Check(result.Status == (proven ? PluginMutationStatus.Rejected : PluginMutationStatus.OutcomeUnknown)
                && !result.SafeReason!.Contains("fixture secret"), "clear exact pre-effect reason allowlist vs interruption/helper uncertainty");
            await Protection(rig, !proven);
            _cases++;
        }
        using (var rig = new Rig())
        {
            rig.Wire.Status = HttpStatusCode.BadRequest;
            rig.Wire.Reply = Error("failed", rejected[0]); // Missing declared null data is not the plugin contract.
            Check((await rig.Run()).Status == PluginMutationStatus.OutcomeUnknown, "unstructured safe-looking text is not proof");
            _cases++;
        }
        using (var rig = new Rig())
        {
            rig.Wire.Disconnect = true;
            var result = await rig.Run();
            Check(result.Status == PluginMutationStatus.OutcomeUnknown && !result.SafeReason!.Contains("fixture secret"), "disconnect after initiation is sanitized unknown");
            _cases++;
        }
        using (var rig = new Rig())
        {
            var effectOccurred = false;
            rig.Wire.OnSend = () => effectOccurred = true;
            rig.Wire.Status = HttpStatusCode.BadRequest;
            rig.Wire.Reply = Error("rpc.unavailable");
            Check((await rig.Run()).Status == PluginMutationStatus.OutcomeUnknown && effectOccurred,
                "rpc.unavailable can race handler effect and never proves no dispatch");
            await Protection(rig, true);
            _cases++;
        }
    }

    private static async Task OwnershipAndLifetime()
    {
        using (var rig = new Rig())
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check((await rig.Run(cancelled.Token)).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "pre-initiation cancellation sends zero RPC");
            _cases++;
        }
        using (var rig = new Rig())
        {
            rig.Current = rig.Origin with { OwnerGeneration = 12 };
            Check((await rig.Run()).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "navigation during confirmation sends zero RPC");
            await Protection(rig, false);
            _cases++;
        }
        using (var rig = new Rig())
        {
            using var held = await rig.Gate.EnterAsync(rig.Source.Identity, rig.Origin.SessionId);
            var pending = rig.Run();
            rig.Current = rig.Origin with { Directory = "/other" };
            held.Dispose();
            Check((await pending).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "binding changes while gate queued are checked at actual initiation");
            _cases++;
        }
        using (var rig = new Rig())
        {
            rig.OnCreate = rig.Source.Revoke;
            Check((await rig.Run()).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "revocation after async workflow setup but before HTTP initiation sends zero");
            _cases++;
        }
        using (var rig = new Rig())
        using (var cancelled = new CancellationTokenSource())
        {
            var result = await rig.Coordinator.ClearEncryptedAsync(rig.Origin, _ => { cancelled.Cancel(); return true; }, cancelled.Token);
            Check(result.Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "cancellation inside binding check still precedes dispatch");
            await Protection(rig, false);
            _cases++;
        }
        using (var rig = new Rig())
        using (var view = new CancellationTokenSource())
        {
            rig.Wire.Block = true;
            var pending = rig.Run(view.Token);
            await rig.Wire.Started.Task;
            rig.Current = rig.Origin with { OwnerGeneration = 12 };
            view.Cancel();
            Check(!rig.Wire.SentToken.IsCancellationRequested && !pending.IsCompleted, "view navigation/cancellation does not cancel dispatched source-owned request");
            rig.Wire.Release.SetResult();
            var result = await pending;
            Check(result.Status == PluginMutationStatus.Accepted && rig.Wire.Sends == 1, "known acceptance settles original lease after navigation");
            await Protection(rig, false);
            _cases++;
        }
        using (var rig = new Rig())
        {
            rig.Wire.Block = true;
            var pending = rig.Run();
            await rig.Wire.Started.Task;
            rig.Source.Revoke();
            Check((await pending).Status == PluginMutationStatus.OutcomeUnknown, "source-owned post-dispatch cancellation retains unknown");
            await Protection(rig, true);
            _cases++;
        }
        using (var rig = new Rig(timeout: TimeSpan.FromMilliseconds(75)))
        {
            rig.Wire.Block = true;
            Check((await rig.Run()).Status == PluginMutationStatus.OutcomeUnknown && rig.Wire.Sends == 1, "bounded source operation timeout is unknown");
            _cases++;
        }
        using (var rig = new Rig())
        {
            var stale = rig.Origin with { Revision = 8 };
            Check((await rig.Coordinator.ClearEncryptedAsync(stale, _ => true)).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "source revision mismatch sends zero");
            Check((await rig.Coordinator.ApplyPresetAsync(rig.Origin, "", rig.CanStart)).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == 0, "empty name has no clear fallback");
            _cases++;
        }
    }

    private static async Task AcknowledgementAndOldKinds()
    {
        foreach (var preset in new[] { false, true })
        {
            using var rig = new Rig(preset);
            rig.Wire.Status = HttpStatusCode.InternalServerError;
            rig.Wire.Reply = Error("rpc.internal", tag: "RpcInternalError");
            var result = await rig.Run();
            Check(result.Status == PluginMutationStatus.OutcomeUnknown, "unknown preset name/internal cannot be called rejected");
            var before = rig.Wire.Sends;
            Check((await rig.Run()).Status == PluginMutationStatus.NotStarted && rig.Wire.Sends == before, "unknown prevents another plugin write without retry");
            Check(await rig.Coordinator.AcknowledgeUnknownAsync(rig.Origin, Guid.NewGuid(), rig.CanStart) is null, "wrong operation cannot release");
            Check(await rig.Coordinator.AcknowledgeUnknownAsync(rig.Origin with { OwnerGeneration = 12 }, result.OperationId, rig.CanStart) is null, "non-current replacement owner cannot release");
            Check(await rig.Coordinator.AcknowledgeUnknownAsync(rig.Origin, result.OperationId, _ => false) is null, "stale current binding cannot release");
            using (var lease = await rig.Gate.EnterAsync(rig.Source.Identity, rig.Origin.SessionId))
            {
                var protection = lease.GetSessionControlProtection()!;
                var wrongKind = preset ? SessionControlKind.ClearEncryptedContents : SessionControlKind.ApplySessionPreset;
                Check(!lease.TryAcknowledgePluginMutation(result.OperationId, wrongKind, protection.TargetFingerprint)
                    && !lease.TryAcknowledgePluginMutation(result.OperationId, protection.Kind, new string('A', 64)), "ack requires exact kind and target fingerprint");
            }
            await Protection(rig, true);
            rig.Current = rig.Origin with { OwnerGeneration = 12 };
            Check(await rig.Coordinator.GetProtectionAsync(rig.Origin, rig.CanStart) is null, "old view cannot inspect new current binding");
            var metadata = await rig.Coordinator.GetProtectionAsync(rig.Current, rig.CanStart);
            Check(metadata is not null && metadata.OperationId == result.OperationId && metadata.Outcome == result
                && metadata.Kind == (preset ? SessionControlKind.ApplySessionPreset : SessionControlKind.ClearEncryptedContents)
                && rig.Wire.Sends == before, "replacement view can query exact source/session/kind/operation and recorded unknown without RPC");
            Check(await rig.Coordinator.AcknowledgeUnknownAsync(rig.Origin, result.OperationId, rig.CanStart) is null,
                "stale original view cannot acknowledge after navigation");
            Check(await rig.Coordinator.GetProtectionAsync(rig.Current with { Directory = Directory + "/other" }, _ => true) is null
                && await rig.Coordinator.AcknowledgeUnknownAsync(rig.Current with { SessionId = "ses_other" }, result.OperationId, _ => true) is null,
                "foreign session/directory cannot inspect or release protected scope");
            var foreignSource = new OpenCodePageSource("different-endpoint", rig.Source.Revision, rig.Client);
            Check(await rig.Coordinator.GetProtectionAsync(rig.Current with { Source = foreignSource }, _ => true) is null
                && await rig.Coordinator.AcknowledgeUnknownAsync(rig.Current with { Source = foreignSource }, result.OperationId, _ => true) is null,
                "different endpoint cannot inspect or release");
            foreignSource.Revoke();
            await Protection(rig, true); // Lookup/refresh alone never acknowledges.
            var acknowledged = await rig.Coordinator.AcknowledgeUnknownAsync(rig.Current, result.OperationId, rig.CanStart);
            Check(acknowledged is { Status: PluginMutationStatus.OutcomeUnknown, ProtectionRetained: false }
                && result.ProtectionRetained && acknowledged.OperationId == result.OperationId && rig.Wire.Sends == before,
                "exact acknowledgement releases protection, retains recorded unknown and sends zero RPC");
            Check(await rig.Coordinator.GetProtectionAsync(rig.Current, rig.CanStart) is null, "acknowledged protection no longer reported");
            await Protection(rig, false);
            var later = preset
                ? await rig.Coordinator.ApplyPresetAsync(rig.Current, Name, rig.CanStart)
                : await rig.Coordinator.ClearEncryptedAsync(rig.Current, rig.CanStart);
            Check(later.Status == PluginMutationStatus.OutcomeUnknown && later.OperationId != result.OperationId, "later attempt owns a new exact operation");
            var afterLater = rig.Wire.Sends;
            Check(await rig.Coordinator.AcknowledgeUnknownAsync(rig.Current, result.OperationId, rig.CanStart) is null
                && (await rig.Coordinator.GetProtectionAsync(rig.Current, rig.CanStart))?.OperationId == later.OperationId
                && rig.Wire.Sends == afterLater, "old acknowledgement cannot clear newer operation or send retry");
            await Protection(rig, true);
            _cases++;
        }
        using (var rig = new Rig())
        {
            var id = Guid.NewGuid();
            using var lease = await rig.Gate.EnterAsync(rig.Source.Identity, rig.Origin.SessionId);
            lease.BeginSessionControl(id, SessionControlKind.SwitchModel, new string('B', 64));
            lease.CompleteSessionControl(id, NotificationInteractionStatus.OutcomeUnknown);
            Check(lease.CanSubmit("per_fixture", NotificationNavigationKind.Permission)
                && lease.CanSubmit("form_fixture", NotificationNavigationKind.Form), "old session control permission/form semantics unchanged");
            Check(!lease.TryAcknowledgePluginMutation(id, SessionControlKind.SwitchModel, new string('B', 64)), "plugin acknowledgement never releases old controls");
            _cases++;
        }
    }

    private static async Task ReconnectAcknowledgement()
    {
        using var rig = new Rig(revision: 1);
        rig.Wire.Status = HttpStatusCode.InternalServerError;
        rig.Wire.Reply = Error("rpc.internal", tag: "RpcInternalError");
        var original = await rig.Run();
        Check(original.Status == PluginMutationStatus.OutcomeUnknown && original.ProtectionRetained,
            "revision-one operation leaves endpoint/session tombstone");
        rig.Source.Revoke();
        var reconnected = new OpenCodePageSource(rig.Source.Identity, 2, rig.Client);
        try
        {
            rig.Current = rig.Origin with { Source = reconnected, Revision = 2, OwnerGeneration = 22 };
            var before = rig.Wire.Sends;
            await Protection(rig, true);
            var metadata = await rig.Coordinator.GetProtectionAsync(rig.Current, rig.CanStart);
            Check(metadata is not null && metadata.OperationId == original.OperationId && metadata.Outcome == original
                && metadata.Kind == SessionControlKind.ClearEncryptedContents && rig.Wire.Sends == before,
                "same-endpoint revision-two current owner can inspect original exact token without RPC");
            await Protection(rig, true);
            Check(await rig.Coordinator.GetProtectionAsync(rig.Origin, _ => true) is null
                && await rig.Coordinator.AcknowledgeUnknownAsync(rig.Origin, original.OperationId, _ => true) is null,
                "revoked original source has no query or acknowledgement authority");
            foreach (var wrong in new[]
            {
                rig.Current with { Directory = Directory + "/other" },
                rig.Current with { SessionId = "ses_other" },
                rig.Current with { Revision = 1 },
            })
            {
                Check(await rig.Coordinator.GetProtectionAsync(wrong, _ => true) is null
                    && await rig.Coordinator.AcknowledgeUnknownAsync(wrong, original.OperationId, _ => true) is null,
                    "reconnect does not authorize different directory/session or stale current revision");
            }
            var staleOwner = rig.Current with { OwnerGeneration = 21 };
            Check(await rig.Coordinator.GetProtectionAsync(staleOwner, rig.CanStart) is null
                && await rig.Coordinator.AcknowledgeUnknownAsync(staleOwner, original.OperationId, rig.CanStart) is null,
                "stale current view cannot query or acknowledge reconnect tombstone");
            var differentEndpoint = new OpenCodePageSource("different-endpoint", 2, rig.Client);
            try
            {
                var wrong = rig.Current with { Source = differentEndpoint };
                Check(await rig.Coordinator.GetProtectionAsync(wrong, _ => true) is null
                    && await rig.Coordinator.AcknowledgeUnknownAsync(wrong, original.OperationId, _ => true) is null,
                    "reconnect never crosses endpoint identity");
            }
            finally { differentEndpoint.Revoke(); }
            await Protection(rig, true);
            var acknowledged = await rig.Coordinator.AcknowledgeUnknownAsync(rig.Current, original.OperationId, rig.CanStart);
            Check(acknowledged is { Status: PluginMutationStatus.OutcomeUnknown, ProtectionRetained: false }
                && acknowledged.OperationId == original.OperationId && original.ProtectionRetained && rig.Wire.Sends == before,
                "explicit current-owner acknowledgement after reconnect releases only old exact token without RPC or success claim");
            await Protection(rig, false);
            var newer = await rig.Coordinator.ClearEncryptedAsync(rig.Current, rig.CanStart);
            var afterNewer = rig.Wire.Sends;
            Check(newer.Status == PluginMutationStatus.OutcomeUnknown && newer.OperationId != original.OperationId
                && await rig.Coordinator.AcknowledgeUnknownAsync(rig.Current, original.OperationId, rig.CanStart) is null
                && (await rig.Coordinator.GetProtectionAsync(rig.Current, rig.CanStart))?.OperationId == newer.OperationId
                && rig.Wire.Sends == afterNewer, "old reconnect acknowledgement cannot release a newer different operation");
            await Protection(rig, true);
            _cases++;
        }
        finally { reconnected.Revoke(); }
    }

    private static async Task Main()
    {
        await Success();
        await InvalidOutputs();
        await Failures();
        await OwnershipAndLifetime();
        await AcknowledgementAndOldKinds();
        await ReconnectAcknowledgement();
        Console.WriteLine($"PASS: {_cases} plugin-mutation cases; {_checks} checks (offline HTTP/source/gate contract).");
    }
}
