using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _checks;
    private static readonly SessionControlDraft Empty = new("captured-empty-draft-token", true);
    private static readonly SessionControlRequest ModelRequest = new(SessionControlKind.SwitchModel, new("alias", "provider", "fast"));
    private static readonly SessionControlRequest Undo = new(SessionControlKind.StageRevert);
    private static bool Current(SessionControlOrigin origin) => !origin.Source.IsRevoked;
    private static void Check(bool value, string label) { _checks++; if (!value) throw new Exception("FAIL " + label); }
    private static async Task<T> Throws<T>(Func<Task> action, string label) where T : Exception
    {
        _checks++;
        try { await action(); } catch (T e) { return e; }
        throw new Exception("FAIL missing " + typeof(T).Name + ": " + label);
    }
    public static async Task Main()
    {
        await Wire(); Console.WriteLine("PASS group 1: strict wire, model alias/variant/default/null, typed errors, guarded zero HTTP.");
        await Controls(); Console.WriteLine("PASS group 2: authoritative history, preview/confirmation/source, acceptance vs refresh, fail-closed protection.");
        await GateAndContinuation(); Console.WriteLine("PASS group 3: exact lease and paired continuation, existing request isolation, one prompt/zero explicit commit.");
        await Background(); Console.WriteLine("PASS group 4: session background wire, rejection/unknown gate and guarded origin.");
        await RootManagement(); Console.WriteLine("PASS group 5: real V2 root delete/rename, exact guard, rejection/unknown and stale metadata.");
        Console.WriteLine($"PASS {_checks} assertions; headless fake HTTP only, not Page/native/live proof.");
    }

    private static async Task RootManagement()
    {
        foreach (var kind in new[] { SessionControlKind.DeleteRoot, SessionControlKind.RenameRoot })
        {
            using var server = new Server { Title = "actual title" }; using var client = server.Client();
            Task Send(Func<Action, bool> start) => kind == SessionControlKind.DeleteRoot
                ? client.DeleteSessionGuardedAsync("ses_control", start) : client.RenameSessionGuardedAsync("ses_control", "new title", "/work space", start);
            await Throws<OperationCanceledException>(() => Send(_ => false), "root guard denied");
            Check(server.Requests.Count == 0, "denied root guard has zero HTTP");
            await Send(Start);
            Check(server.Requests.Last().Method == (kind == SessionControlKind.DeleteRoot ? "DELETE" : "PATCH")
                && server.Requests.Last().Path == "/api/session/ses_control" && server.Requests.Last().Query == ""
                && server.LastBody == (kind == SessionControlKind.DeleteRoot ? "" : "{\"title\":\"new title\"}"), "exact V2 method/path/body/204 " + kind);
            foreach (var rejection in new[] { (400, "InvalidRequestError"), (401, "UnauthorizedError"), (404, "SessionNotFoundError") })
            {
                server.OnMutation = _ => Task.FromResult(Server.Json(rejection.Item1, Error(rejection.Item2)));
                Check((await Throws<OpenCodeSessionControlException>(() => Send(Start), "typed root rejection")).VerifiedPreEffect,
                    "known root rejection releases exact slot " + rejection.Item1);
            }
            server.OnMutation = _ => Task.FromResult(Server.Json(400, "{\"_tag\":\"InvalidRequestError\",\"message\":\"PRIVATE\",\"kind\":null,\"field\":\"title\"}"));
            Check((await Throws<OpenCodeSessionControlException>(() => Send(Start), "documented nullable error fields")).VerifiedPreEffect,
                "root InvalidRequest accepts only documented optional typed kind/field");
            server.OnMutation = _ => Task.FromResult(Server.Json(400, "{\"_tag\":\"InvalidRequestError\",\"message\":\"PRIVATE\",\"field\":123}"));
            Check(!(await Throws<OpenCodeSessionControlException>(() => Send(Start), "malformed error field")).VerifiedPreEffect, "malformed root rejection cannot release protection");
            if (kind == SessionControlKind.RenameRoot)
            {
                server.OnMutation = _ => Task.FromResult(Server.Json(404, "{\"_tag\":\"LocationNotFoundError\",\"message\":\"PRIVATE\",\"location\":{\"directory\":\"/work space\"}}"));
                Check((await Throws<OpenCodeSessionControlException>(() => Send(Start), "documented rename location 404")).VerifiedPreEffect,
                    "rename location rejection must match captured actual directory");
                server.OnMutation = _ => Task.FromResult(Server.Json(404, "{\"_tag\":\"LocationNotFoundError\",\"message\":\"PRIVATE\",\"location\":{\"directory\":\"/foreign\"}}"));
                Check(!(await Throws<OpenCodeSessionControlException>(() => Send(Start), "foreign location 404")).VerifiedPreEffect,
                    "wrong-directory rejection retains protection");
            }
            server.OnMutation = _ => Task.FromResult(Server.Json(200, "{}"));
            await Throws<OpenCodeProtocolException>(() => Send(Start), "root success must be exactly 204, no Session.Info assumed");
        }
        foreach (var kind in new[] { SessionControlKind.DeleteRoot, SessionControlKind.RenameRoot })
        foreach (var outcome in new[] { "accepted", "rejected", "unknown" })
        {
            using var server = new Server { Title = "actual title" };
            var gate = new OpenCodeInteractionGate(); var coordinator = new OpenCodeSessionControlCoordinator(gate); var origin = server.Origin();
            var request = new SessionControlRequest(kind, Directory: "/work space", ExpectedTitle: "actual title", NewTitle: "new title");
            var preview = await Preview(server, coordinator, origin, request);
            server.OnMutation = _ => outcome switch
            {
                "accepted" => Task.FromResult(Server.Json(204, "")),
                "rejected" => Task.FromResult(Server.Json(400, Error("InvalidRequestError"))),
                _ => throw new HttpRequestException("PRIVATE transport"),
            };
            var result = await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current);
            Check(server.Mutations == 1 && result.Status == (outcome == "accepted" ? SessionControlStatus.Accepted
                : outcome == "rejected" ? SessionControlStatus.DefinitivelyRejected : SessionControlStatus.OutcomeUnknown), "single root mutation exact outcome " + kind + outcome);
            using var lease = await gate.EnterAsync(origin.Source.Identity, origin.SessionId);
            Check(lease.CanControlSession() == (outcome != "unknown") && lease.CanSubmitComposer() == (outcome != "unknown"), "root unknown protects composer; known settlement releases only its exact slot");
            Check(outcome != "unknown" || lease.GetSessionControlProtection()?.Kind == kind, "distinct exact root protection kind");
        }
        using (var server = new Server { Title = "actual title" })
        {
            var coordinator = new OpenCodeSessionControlCoordinator(new()); var origin = server.Origin();
            var request = new SessionControlRequest(SessionControlKind.DeleteRoot, Directory: "/work space", ExpectedTitle: "actual title");
            var preview = await Preview(server, coordinator, origin, request);
            server.Title = "foreign rename";
            Check((await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current)).Status == SessionControlStatus.ConfirmationRequired
                && server.Mutations == 0, "fresh server metadata must still match confirmed actual root title");
            server.Title = "actual title"; server.HasInbox = true;
            Check((await coordinator.PreviewAsync(origin, request, Empty, Current)).Status == SessionControlStatus.Blocked, "root queued input preflight stays protected");
            server.HasInbox = false; server.ParentId = "ses_parent";
            Check((await coordinator.PreviewAsync(origin, request, Empty, Current)).Status == SessionControlStatus.ConfirmationRequired, "root cannot become child while confirmation is open");
            server.ParentId = null; origin.Source.Revoke();
            Check((await coordinator.PreviewAsync(origin, request, Empty, Current)).Status != SessionControlStatus.Ready && server.Mutations == 0, "revoked root origin never starts a mutation");
        }
    }

    private static async Task Background()
    {
        using var server = new Server();
        using var client = server.Client();
        var calls = server.Mutations;
        await Throws<OperationCanceledException>(() => client.BackgroundGuardedAsync("ses_control", _ => false), "background denied guard");
        Check(server.Mutations == calls, "background rejected guard sends zero requests");
        await client.BackgroundGuardedAsync("ses_control", Start);
        Check(server.Requests.Last().Method == "POST" && server.Requests.Last().Path == "/api/session/ses_control/background"
            && server.LastBody == "" && server.Requests.Last().Query == "", "exact session-scoped bodyless background POST/204, no guessed tool or job ID");
        foreach (var rejection in new[] { (400, "InvalidRequestError"), (401, "UnauthorizedError"), (404, "SessionNotFoundError") })
        {
            server.OnMutation = _ => Task.FromResult(Server.Json(rejection.Item1, Error(rejection.Item2)));
            var error = await Throws<OpenCodeSessionControlException>(() => client.BackgroundGuardedAsync("ses_control", Start), "documented background rejection");
            Check(error.VerifiedPreEffect && error.StatusCode == rejection.Item1 && !error.ToString().Contains("PRIVATE"), "bounded typed background rejection " + rejection.Item1);
        }
        server.OnMutation = _ => Task.FromResult(Server.Json(200, "{}"));
        await Throws<OpenCodeProtocolException>(() => client.BackgroundGuardedAsync("ses_control", Start), "background requires exactly 204");
        var gate = new OpenCodeInteractionGate();
        var source = server.Origin().Source;
        var operation = Guid.NewGuid();
        using (var lease = await gate.EnterAsync(source.Identity, "ses_control"))
        {
            server.OnMutation = _ => throw new HttpRequestException("offline");
            await Throws<OpenCodeTransportException>(() => client.BackgroundGuardedAsync("ses_control", action => source.TryExecute(CancellationToken.None, () =>
            {
                lease.BeginSessionControl(operation, SessionControlKind.Background, new string('A', 64));
                action();
            })), "background ambiguous transport");
        }
        using (var lease = await gate.EnterAsync(source.Identity, "ses_control"))
        {
            Check(!lease.CanControlSession() && !lease.CanSubmitComposer() && lease.GetSessionControlProtection()?.Kind == SessionControlKind.Background,
                "ambiguous background retains distinct control protection; duplicate and composer cannot proceed");
            Check(lease.CanSubmit("frm_other", NotificationNavigationKind.Form), "background protection does not suppress unrelated form responses");
        }
        source.Revoke();
        calls = server.Mutations;
        await Throws<OperationCanceledException>(() => client.BackgroundGuardedAsync("ses_control", action => source.TryExecute(CancellationToken.None, action)), "revoked source background");
        Check(server.Mutations == calls, "revoked source starts zero background mutations");
    }

    private static async Task Wire()
    {
        using var server = new Server(); using var client = server.Client();
        var models = await client.GetSelectableModelsAsync("/work space");
        var offered = models.Models.Single();
        foreach (var variants in new[] { System.Collections.Immutable.ImmutableArray<string>.Empty, offered.Variants })
        {
            var catalog = offered with { Variants = variants };
            Check(catalog.Offers(new("alias", "provider")), "omitted variant offers base model regardless of variant list");
            Check(catalog.Offers(new("alias", "provider", "default")), "canonical session default is equivalent to omitted base variant");
            Check(!catalog.Offers(new("alias", "provider", "unlisted")), "unlisted named variant remains unavailable");
            Check(!catalog.Offers(new("alias", "provider", "Default")), "default equivalence does not invent case-insensitive aliases");
            Check(!(catalog with { Enabled = false }).Offers(new("alias", "provider", "default")), "default equivalence cannot enable disabled model");
            Check(!catalog.Offers(new("other", "provider", "default")) && !catalog.Offers(new("alias", "other", "default")), "default equivalence preserves model/provider identity");
        }
        Check(offered.Offers(new("alias", "provider", "fast")), "listed named variant still available");
        Check(models.Directory == "/work space" && models.Models.Single().Id == "alias", "authoritative directory/catalog alias");
        Check(models.Models[0].ModelId == "provider-facing" && models.Models[0].Variants.SequenceEqual(new[] { "fast" }), "provider model ID kept distinct");
        Check(Uri.UnescapeDataString(server.Requests.Last().Query) == "?location[directory]=/work space", "deepObject directory query");
        Check((await client.GetDefaultModelAsync("/work space")).Model?.Id == "alias", "default is complete Model.Info");
        server.Default = "null";
        Check((await client.GetDefaultModelAsync("/work space")).Model is null, "default null means none");
        server.Default = "{\"id\":\"alias\",\"providerID\":\"provider\"}";
        await Throws<OpenCodeProtocolException>(() => client.GetDefaultModelAsync("/work space"), "default ref not info");
        Check((await client.GetSessionControlStateAsync("ses_control")).Model is null, "absent selection inherits");
        server.Model = "null";
        await Throws<OpenCodeProtocolException>(() => client.GetSessionControlStateAsync("ses_control"), "wire-null selection is malformed");
        server.Model = "{\"id\":\"unavailable\",\"providerID\":\"old\"}";
        Check((await client.GetSessionControlStateAsync("ses_control")).Model?.Id == "unavailable", "explicit unavailable survives projection");
        server.Model = null;
        int requests = server.Requests.Count, starts = 0;
        await Throws<OperationCanceledException>(() => client.SwitchModelGuardedAsync("ses_control", new("alias", "provider"), a => { starts++; return false; }), "declined guard");
        Check(server.Requests.Count == requests && starts == 1, "guard zero HTTP");
        await Throws<ArgumentException>(() => client.SwitchModelGuardedAsync("ses_control", new("", "provider"), a => { starts++; a(); return true; }), "invalid ref before guard");
        Check(starts == 1 && server.Requests.Count == requests, "validation zero guard zero HTTP");
        await client.SwitchModelGuardedAsync("ses_control", new("alias", "provider"), Start);
        Check(server.LastBody == "{\"model\":{\"id\":\"alias\",\"providerID\":\"provider\"}}", "base omits variant; not modelID");
        await client.SwitchModelGuardedAsync("ses_control", new("alias", "provider", "fast"), Start);
        Check(server.LastBody.Contains("\"variant\":\"fast\"", StringComparison.Ordinal), "explicit variant ID");
        var stage = await client.StageRevertGuardedAsync("ses_control", "msg_a", false, Start);
        Check(stage.MessageId == "msg_a" && server.LastBody == "{\"messageID\":\"msg_a\",\"files\":false}", "definite false stage body");
        await client.ClearRevertGuardedAsync("ses_control", Start);
        Check(server.Requests.Last().Method == "DELETE" && server.LastBody == "", "whole-stage DELETE has no body");

        foreach (var item in new[] {
            (409, "SessionBusyError", SessionControlErrorKind.SessionBusy, true),
            (404, "SessionNotFoundError", SessionControlErrorKind.SessionNotFound, true),
            (404, "MessageNotFoundError", SessionControlErrorKind.MessageNotFound, false),
            (400, "InvalidRequestError", SessionControlErrorKind.InvalidRequest, false),
            (401, "UnauthorizedError", SessionControlErrorKind.Unauthorized, false),
            (404, "OtherError", SessionControlErrorKind.Unclassified, false) })
        {
            server.OnMutation = _ => Task.FromResult(Server.Json(item.Item1, Error(item.Item2)));
            var error = await Throws<OpenCodeSessionControlException>(() => client.StageRevertGuardedAsync("ses_control", "msg_a", false, Start), item.Item2);
            Check(error.Kind == item.Item3 && error.VerifiedPreEffect == item.Item4 && !error.ToString().Contains("PRIVATE"), "typed exact sanitized error " + item.Item2);
        }
        server.OnMutation = _ => Task.FromResult(Server.Json(409, Error("SessionBusyError")));
        var busy = await Throws<OpenCodeSessionControlException>(() => client.SwitchModelGuardedAsync("ses_control", new("alias", "provider"), Start), "undocumented model 409");
        Check(!busy.VerifiedPreEffect, "model busy is native policy, not server pre-effect contract");
        server.OnMutation = _ => Task.FromResult(Server.Json(404, Error("SessionNotFoundError").Replace("ses_control", "ses_other")));
        Check(!(await Throws<OpenCodeSessionControlException>(() => client.ClearRevertGuardedAsync("ses_control", Start), "error owner mismatch")).VerifiedPreEffect, "wrong owner retains unknown");
        server.OnMutation = _ => Task.FromResult(Server.Json(404, Error("SessionNotFoundError").Replace("\"_tag\"", "\"unexpected\":true,\"_tag\"")));
        Check(!(await Throws<OpenCodeSessionControlException>(() => client.ClearRevertGuardedAsync("ses_control", Start), "malformed documented error")).VerifiedPreEffect,
            "extra fields cannot masquerade as a verified pre-effect rejection");
        server.OnMutation = _ => Task.FromResult(Server.Json(200, "{\"data\":{\"messageID\":\"msg_other\"}}"));
        await Throws<OpenCodeProtocolException>(() => client.StageRevertGuardedAsync("ses_control", "msg_a", false, Start), "malformed/mismatched accepted body");
        server.OnMutation = _ => Task.FromResult(Server.Json(200, "{}"));
        await Throws<OpenCodeProtocolException>(() => client.ClearRevertGuardedAsync("ses_control", Start), "clear requires 204");
        server.History = "{\"data\":[],\"cursor\":{}}";
        Check((await client.GetSessionControlHistoryPageAsync("ses_control")).Next is null, "optional absent cursor ends chain");
        await ReviewHistoryCaseSafety();
    }

    private static async Task<SessionControlPreview> Preview(Server server, OpenCodeSessionControlCoordinator coordinator,
        SessionControlOrigin origin, SessionControlRequest? request = null)
    {
        var result = await coordinator.PreviewAsync(origin, request ?? Undo, Empty, Current);
        Check(result.Status == SessionControlStatus.Ready && result.Preview is not null, "ready preview " + result.Status);
        Check(server.Mutations == 0, "preview never mutates");
        return result.Preview!;
    }

    private static async Task Controls()
    {
        using (var server = new Server())
        {
            var gate = new OpenCodeInteractionGate(); var coordinator = new OpenCodeSessionControlCoordinator(gate); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin);
            Check(preview.Target?.Id == "msg_a" && preview.Target.Text == "newer", "server order, not lexical ID fallback");
            using (var free = await gate.EnterAsync(origin.Source.Identity, origin.SessionId)) Check(free.CanControlSession(), "no lease/protection held over dialog");
            var result = await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current);
            Check(result.Status == SessionControlStatus.Accepted && result.AcceptanceKnown && !result.ProtectionRetained, "stage accepted exact settlement");
            Check(result.Restoration?.Text == "newer" && result.Restoration.ExpectedDraftFingerprint == Empty.Fingerprint, "restoration candidate bound to original empty draft");
            Check((await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current)).Status == SessionControlStatus.ConfirmationRequired && server.Mutations == 1, "one-use preview no replay");
        }
        using (var server = new Server { Stage = "{\"messageID\":\"msg_a\",\"snapshot\":\"old-snapshot\"}" })
        {
            var coordinator = new OpenCodeSessionControlCoordinator(new()); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin);
            Check(preview.Target?.Id == "msg_z" && preview.RequiresTextOnlyConsent && preview.PriorStageMayHaveFileEffects, "exact latest user before stage; file/agent/skill and prior-stage risks");
            Check(preview.Target!.HasFiles && preview.Target.HasAgents && preview.Target.HasSkills && !preview.Request.Files, "minimal reference projection/default files off");
            Check(!server.Requests.Any(r => r.Host != "control.invalid"), "reference URI never fetched");
            server.Stage = "{\"messageID\":\"msg_z\"}";
            Check((await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current)).Status == SessionControlStatus.ConfirmationRequired && server.Mutations == 0, "changed stage requires new confirmation, never retarget");
        }
        foreach (var history in new[] {
            "{\"data\":[],\"cursor\":{\"next\":\"more\"}}",
            Server.Page(Server.User("msg_a", 2, "x"), "loop"),
            Server.Page(Server.User("msg_a", 2, "x") + "," + Server.User("msg_a", 2, "x")),
            Server.Page(Server.User("msg_a", 2, "x").Replace("\"text\":\"x\"", "\"text\":\"x\",\"files\":null")),
            Server.Page(Server.User("msg_a", 2, "x").Replace("\"text\":\"x\"", "\"text\":\"x\",\"futureAttachments\":[]")) })
        {
            using var server = new Server { History = history }; var coordinator = new OpenCodeSessionControlCoordinator(new());
            Check((await coordinator.PreviewAsync(server.Origin(), Undo, Empty, Current)).Status == SessionControlStatus.InsufficientHistory && server.Mutations == 0, "incomplete/cycle/duplicate/unknown references insufficient");
        }
        using (var server = new Server { Stage = "{\"messageID\":\"msg_missing\"}" })
            Check((await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(server.Origin(), Undo, Empty, Current)).Status == SessionControlStatus.InsufficientHistory, "missing boundary no fallback");
        using (var server = new Server { CursorBudget = true })
        {
            Check((await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(server.Origin(), Undo, Empty, Current)).Status == SessionControlStatus.InsufficientHistory, "budget exhaustion insufficient");
            Check(server.HistoryReads == 4, "bounded four pages");
        }
        using (var server = new Server { Model = "{\"id\":\"gone\",\"providerID\":\"old\"}" })
        {
            var coordinator = new OpenCodeSessionControlCoordinator(new()); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin, ModelRequest);
            Check(preview.ExplicitModelUnavailable && preview.Inherited is null && !server.Requests.Any(r => r.Path.EndsWith("/default")), "explicit unavailable never inherits");
            server.FailRefresh = true;
            var result = await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current);
            Check(result.Status == SessionControlStatus.Accepted && result.AcceptanceKnown && result.Observed is null && !result.ProtectionRetained, "204 retained across failed display GET");
        }
        using (var server = new Server())
        {
            var coordinator = new OpenCodeSessionControlCoordinator(new()); var origin = server.Origin();
            var bad = new SessionControlRequest(SessionControlKind.SwitchModel, new("provider-facing", "provider", "invented"));
            Check((await coordinator.PreviewAsync(origin, bad, Empty, Current)).Status == SessionControlStatus.Blocked && server.Mutations == 0, "alias/variant membership fails closed");
            var preview = await Preview(server, coordinator, origin);
            Check((await coordinator.ConfirmAsync(preview, Guid.NewGuid(), new("later-typing", false), Current, Current)).Status == SessionControlStatus.ConfirmationRequired && server.Mutations == 0, "later typing never uses fresh token");
        }
        foreach (bool busy in new[] { false, true })
        {
            using var server = new Server(); var gate = new OpenCodeInteractionGate(); var coordinator = new OpenCodeSessionControlCoordinator(gate); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin);
            server.OnMutation = _ => Task.FromResult(Server.Json(busy ? 409 : 400, Error(busy ? "SessionBusyError" : "InvalidRequestError")));
            var result = await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current);
            Check(result.Status == (busy ? SessionControlStatus.DefinitivelyRejected : SessionControlStatus.OutcomeUnknown) && result.ProtectionRetained == !busy, "only verified rejection clears exact slot");
            using var lease = await gate.EnterAsync(origin.Source.Identity, origin.SessionId);
            Check(lease.CanControlSession() == busy && lease.CanSubmit("frm_next", NotificationNavigationKind.Form), "unknown control is not blanket Form protection");
        }
        using (var server = new Server())
        {
            var gate = new OpenCodeInteractionGate(); var coordinator = new OpenCodeSessionControlCoordinator(gate); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin);
            server.OnMutation = _ => Task.FromResult(Server.Json(200, "{\"data\":{}}"));
            Check((await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current)).ProtectionRetained, "malformed accepted stage keeps protection");
            int before = server.Requests.Count;
            Check((await coordinator.PreviewAsync(origin, Undo, Empty, Current)).Status == SessionControlStatus.Blocked && server.Requests.Count == before, "no retry or read-reconcile unlock");
        }
        using (var server = new Server())
        {
            var gate = new OpenCodeInteractionGate(); var coordinator = new OpenCodeSessionControlCoordinator(gate); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin, ModelRequest);
            Check((await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, _ => false, _ => false)).Status == SessionControlStatus.OriginLost && server.Mutations == 0, "owner loss before start zero mutation");
            preview = await Preview(server, coordinator, origin, ModelRequest);
            server.OnMutation = _ => { origin.Source.Revoke(); return Task.FromResult(Server.Json(204, "")); };
            var result = await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, Current, Current);
            Check(result.Status == SessionControlStatus.OutcomeUnknown && result.ProtectionRetained && server.Mutations == 1, "source lost post-start retains exact protection");
            using var lease = await gate.EnterAsync(origin.Source.Identity, origin.SessionId);
            Check(!lease.CanSubmitComposer() && !lease.CanSwitchAgent(), "same endpoint/session protection survives source disposal");
        }
        using (var server = new Server { HasInbox = true, Stage = "{\"messageID\":\"msg_a\"}" })
        {
            var result = await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(server.Origin(), new(SessionControlKind.ClearRevert), Empty, Current);
            Check(result.Status == SessionControlStatus.Blocked && server.Mutations == 0, "redo requires no known inbox; never auto cancel/interrupt");
        }
        using (var server = new Server())
        {
            var origin = server.Origin() with { PreviouslyKnownUnresolvedChildSessionIds = System.Collections.Immutable.ImmutableArray.Create("ses_child") };
            Check((await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(origin, Undo, Empty, Current)).Status == SessionControlStatus.Blocked,
                "retained unresolved child survives absent launch row after rollback");
            origin = server.Origin() with { LocalSubmissionInProgress = true };
            Check((await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(origin, ModelRequest, Empty, Current)).Status == SessionControlStatus.Blocked,
                "known local submission blocks native model control");
        }
        using (var server = new Server { Stage = "{\"messageID\":\"msg_a\"}" })
        {
            var coordinator = new OpenCodeSessionControlCoordinator(new()); var origin = server.Origin();
            var redo = await Preview(server, coordinator, origin, new(SessionControlKind.ClearRevert));
            var result = await coordinator.ConfirmAsync(redo, Guid.NewGuid(), Empty, Current, Current);
            Check(result.AcceptanceKnown && result.Restoration is null && server.Requests.Last(r => r.Method != "GET").Method == "DELETE",
                "redo clears entire captured stage and never supplies draft replacement");
        }
        await ReviewSettlementOwnership();
    }

    private static async Task GateAndContinuation()
    {
        var gate = new OpenCodeInteractionGate(); string hash = new('A', 64); var id = Guid.NewGuid();
        using (var lease = await gate.EnterAsync("endpoint", "ses_gate"))
        {
            lease.BeginSessionControl(id, SessionControlKind.StageRevert, hash);
            Check(!lease.CanSubmitComposer() && !lease.CanSwitchAgent() && !lease.CanControlSession(), "all control/send paths blocked by typed slot");
            Check(lease.GetSessionProtection() is null && lease.GetAgentSwitchProtection() is null
                && lease.CanSubmit("frm_x", NotificationNavigationKind.Form) && lease.CanSubmit("per_x", NotificationNavigationKind.Permission), "request guards unchanged");
            await Throws<InvalidOperationException>(() => { lease.CompleteSessionControl(Guid.NewGuid(), NotificationInteractionStatus.Succeeded); return Task.CompletedTask; }, "wrong settlement ID");
            lease.CompleteSessionControl(id, NotificationInteractionStatus.OutcomeUnknown);
        }
        using (var lease = await gate.EnterAsync("endpoint", "ses_gate"))
            Check(lease.GetSessionControlProtection()?.OperationId == id && !lease.CanControlSession(), "unknown persists without TTL");
        using (var lease = await gate.EnterAsync("endpoint", "ses_pair"))
        {
            lease.BeginComposerContinuation(id, ComposerSubmissionKind.Command, hash);
            Check(lease.GetComposerSubmissionProtection()?.OperationId == lease.GetSessionControlProtection()?.OperationId, "atomic same operation ID");
            await Throws<InvalidOperationException>(() => { lease.CompleteComposerSubmission(id, NotificationInteractionStatus.Succeeded); return Task.CompletedTask; }, "no independent paired settlement");
            await Throws<InvalidOperationException>(() => { lease.BeginComposerSubmission(Guid.NewGuid(), ComposerSubmissionKind.Prompt); return Task.CompletedTask; }, "no self bypass");
            lease.CompleteComposerContinuation(id, NotificationInteractionStatus.Succeeded, NotificationInteractionStatus.OutcomeUnknown);
            Check(lease.GetComposerSubmissionProtection() is null && lease.GetSessionControlProtection() is not null, "command 204 does not prove commit");
        }
        using (var lease = await gate.EnterAsync("endpoint", "ses_old"))
        {
            lease.BeginMutation("per_reject", NotificationNavigationKind.Permission, true);
            lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            Check(!lease.CanSubmit("frm_other", NotificationNavigationKind.Form), "existing whole-session permission rejection semantics retained");
        }
        using var server = new Server { Stage = "{\"messageID\":\"msg_a\"}" };
        var continuationGate = new OpenCodeInteractionGate(); var coordinator = new OpenCodeSessionControlCoordinator(continuationGate); var origin = server.Origin();
        using var held = await continuationGate.EnterAsync(origin.Source.Identity, origin.SessionId);
        var first = await coordinator.PrepareContinuationAsync(origin, held, Empty, null, Current).WaitAsync(TimeSpan.FromSeconds(3));
        Check(first.Status == SessionControlStatus.ConfirmationRequired && first.RequiredConsent is not null && server.Mutations == 0, "fresh stage explicit consent, no lease reacquisition");
        var changed = await coordinator.PrepareContinuationAsync(origin, held, new("changed-draft", false), first.RequiredConsent, Current);
        Check(changed.Status == SessionControlStatus.ConfirmationRequired && changed.Permit is null, "exact draft consent required");
        server.Stage = "{\"messageID\":\"msg_z\"}";
        Check((await coordinator.PrepareContinuationAsync(origin, held, Empty, first.RequiredConsent, Current)).Status == SessionControlStatus.ConfirmationRequired, "new stage requires new consent");
        server.Stage = null;
        Check((await coordinator.PrepareContinuationAsync(origin, held, Empty, first.RequiredConsent, Current)).Status == SessionControlStatus.ConfirmationRequired,
            "cleared stage is also changed consent, not silent unstaged fallthrough");
        Check((await coordinator.PrepareContinuationAsync(origin, held, Empty, null, Current)).Status == SessionControlStatus.NoStage,
            "fresh unstaged invocation needs no continuation protection");
        server.Stage = "{\"messageID\":\"msg_a\"}";
        var permitted = await coordinator.PrepareContinuationAsync(origin, held, Empty, first.RequiredConsent, Current);
        Check(permitted.Status == SessionControlStatus.Ready && permitted.Permit is not null, "immutable permitted stage");
        using (var wrong = await continuationGate.EnterAsync(origin.Source.Identity, "ses_other"))
            Check((await coordinator.PrepareContinuationAsync(origin, wrong, Empty, first.RequiredConsent, Current)).Status == SessionControlStatus.Blocked, "wrong held lease rejected");
        using var client = server.Client();
        var operation = Guid.NewGuid();
        await client.SendPromptGuardedAsync(origin.SessionId, "continue", null,
            send => coordinator.TryStartContinuation(permitted.Permit!, held, operation, Empty, ComposerSubmissionKind.Prompt, Current, send), default);
        Check(server.Mutations == 1 && server.Requests.Count(r => r.Path.EndsWith("/prompt")) == 1
            && !server.Requests.Any(r => r.Path.EndsWith("/commit")), "one existing prompt POST; zero explicit commit");
        Check(!coordinator.TryStartContinuation(permitted.Permit!, held, operation, Empty, ComposerSubmissionKind.Prompt, Current, () => throw new Exception("duplicate")), "one-use permit");
        Check(coordinator.CompleteContinuation(permitted.Permit!, held, operation, NotificationInteractionStatus.Succeeded,
            NotificationInteractionStatus.Succeeded, Current) && held.CanControlSession(), "exact paired acceptance settlement");
        await ReviewUnstagedPolicy();
    }

    private static async Task ReviewSettlementOwnership()
    {
        foreach (bool rejected in new[] { false, true })
        {
            using var server = new Server(); var gate = new OpenCodeInteractionGate();
            var coordinator = new OpenCodeSessionControlCoordinator(gate); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin);
            var liveDraft = Empty;
            bool CanStart(SessionControlOrigin candidate) => Current(candidate) && candidate == origin && liveDraft == Empty;
            bool OwnsOperation(SessionControlOrigin candidate) => Current(candidate) && candidate == origin;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OnMutation = _ => { started.SetResult(true); return response.Task; };
            var operationId = Guid.NewGuid();
            var pending = coordinator.ConfirmAsync(preview, operationId, Empty, CanStart, OwnsOperation);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            liveDraft = new("edited-after-start-token", false);
            Check(!CanStart(origin) && OwnsOperation(origin) && server.Mutations == 1, "draft authority expires after one POST, operation owner remains");
            server.Stage = rejected ? null : "{\"messageID\":\"msg_a\"}";
            response.SetResult(Server.Json(rejected ? 409 : 200,
                rejected ? Error("SessionBusyError") : "{\"data\":" + server.Stage + "}"));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result.Status == (rejected ? SessionControlStatus.DefinitivelyRejected : SessionControlStatus.Accepted)
                && result.AcceptanceKnown == !rejected && !result.ProtectionRetained && result.OperationId == operationId,
                "known accepted/rejected outcome settles exact operation despite later draft edit");
            if (!rejected)
            {
                Check(result.Observed?.Revert?.MessageId == "msg_a" && result.Restoration?.ExpectedDraftFingerprint == Empty.Fingerprint,
                    "owner-only display refresh returns candidate tied to original draft, not a borrowed token");
            }
            using var lease = await gate.EnterAsync(origin.Source.Identity, origin.SessionId);
            Check(lease.GetSessionControlProtection() is null && lease.CanControlSession() && server.Mutations == 1,
                "settlement clears only own protection with no retry");
        }
        foreach (bool startAuthority in new[] { false, true })
        {
            using var server = new Server(); var coordinator = new OpenCodeSessionControlCoordinator(new()); var origin = server.Origin();
            var preview = await Preview(server, coordinator, origin);
            int reads = server.Requests.Count;
            var result = await coordinator.ConfirmAsync(preview, Guid.NewGuid(), Empty, _ => startAuthority, _ => !startAuthority);
            Check(result.Status == SessionControlStatus.OriginLost && server.Mutations == 0 && server.Requests.Count == reads,
                "both start authority and operation ownership required before fresh read/start");
        }
    }

    private static async Task ReviewUnstagedPolicy()
    {
        using var server = new Server(); var gate = new OpenCodeInteractionGate(); var origin = server.Origin();
        var coordinator = new OpenCodeSessionControlCoordinator(gate);
        using (var held = await gate.EnterAsync(origin.Source.Identity, origin.SessionId))
        {
            held.BeginMutation("frm_unrelated", NotificationNavigationKind.Form);
            held.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            Check(held.CanSubmitComposer() && !held.CanControlSession(), "Form-only unknown has existing sender policy, not control eligibility");
            var preparation = await coordinator.PrepareContinuationAsync(origin, held, Empty, null, Current);
            Check(preparation.Status == SessionControlStatus.NoStage && preparation.State?.Revert is null
                && preparation.Permit is null && server.Mutations == 0, "fresh NoStage is returned without inventing a permit or changing sender policy");
            Check(held.GetProtectionStatus("frm_unrelated", NotificationNavigationKind.Form) == NotificationInteractionStatus.OutcomeUnknown
                && held.GetSessionControlProtection() is null && held.CanSubmitComposer(), "Form protection unchanged after unstaged read");
            server.Stage = "{\"messageID\":\"msg_a\"}";
            Check((await coordinator.PrepareContinuationAsync(origin, held, Empty, null, Current)).Status == SessionControlStatus.Blocked,
                "actual staged continuation still requires stricter control eligibility");
            int reads = server.Requests.Count;
            Check((await coordinator.PrepareContinuationAsync(origin, held, Empty, null, _ => false)).Permit is null
                && server.Requests.Count == reads, "NoStage reader does not bypass source/start authority");
        }
        server.Stage = null;
        var protectedGate = new OpenCodeInteractionGate(); var protectedCoordinator = new OpenCodeSessionControlCoordinator(protectedGate);
        using var protectedLease = await protectedGate.EnterAsync(origin.Source.Identity, origin.SessionId);
        var operationId = Guid.NewGuid();
        protectedLease.BeginSessionControl(operationId, SessionControlKind.RevertContinuation, new string('B', 64));
        protectedLease.CompleteSessionControl(operationId, NotificationInteractionStatus.OutcomeUnknown);
        int before = server.Requests.Count;
        Check((await protectedCoordinator.PrepareContinuationAsync(origin, protectedLease, Empty, null, Current)).Status == SessionControlStatus.Blocked
            && server.Requests.Count == before && !protectedLease.CanSubmitComposer()
            && protectedLease.GetSessionControlProtection()?.OperationId == operationId,
            "retained control protection blocks even an unstaged preparation; no read-to-unlock");
    }

    private static async Task ReviewHistoryCaseSafety()
    {
        const string idle = "{\"id\":\"msg_idle\",\"type\":\"idle\",\"time\":{\"created\":4},\"outcome\":\"succeeded\"}";
        foreach (var rows in new[] {
            "{\"id\":\"msg_conflict\",\"type\":\"assistant\",\"Type\":\"idle\",\"time\":{\"created\":3},\"outcome\":\"succeeded\"}",
            idle.Replace("\"created\":4", "\"created\":4,\"Created\":3"),
            idle + ",{\"id\":\"msg_conflict\",\"type\":\"assistant\",\"time\":{\"created\":3},\"content\":[{\"id\":\"tool_1\",\"type\":\"tool\",\"Type\":\"text\",\"state\":{\"status\":\"running\",\"input\":{\"background\":true},\"metadata\":{\"sessionID\":\"ses_child\"}}}]}" })
        {
            using var server = new Server { History = Server.Page(rows + "," + Server.User("msg_a", 2, "restore")) };
            using var client = server.Client();
            await Throws<OpenCodeProtocolException>(() => client.GetSessionControlHistoryPageAsync("ses_control"), "case alias cannot override mapped history fields");
            var result = await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(server.Origin(), Undo, Empty, Current);
            Check(result.Status != SessionControlStatus.Ready && result.Preview is null && server.Mutations == 0,
                "contradictory top-level/nested case fields never manufacture mutation-ready idle evidence");
        }
        using var opaque = new Server { History = Server.Page(idle
            + ",{\"id\":\"msg_tool\",\"type\":\"assistant\",\"time\":{\"created\":3},\"providerState\":{\"Type\":\"opaque\",\"type\":\"unrelated\"},\"content\":[{\"id\":\"tool_1\",\"type\":\"tool\",\"state\":{\"status\":\"running\",\"Status\":\"completed\",\"input\":{\"background\":true,\"Background\":false},\"metadata\":{\"sessionID\":\"ses_child\",\"SessionID\":\"ses_other\"}}}]}"
            + "," + Server.User("msg_a", 2, "restore")) };
        using var opaqueClient = opaque.Client();
        var page = await opaqueClient.GetSessionControlHistoryPageAsync("ses_control");
        Check(page.Messages[1].ReferencesComplete && page.Messages[1].Execution.Children.Single().ChildSessionId == "ses_child"
            && !page.Messages[1].Execution.Children[0].Terminal, "opaque state keeps existing exact-key semantics; no blanket payload rejection");
        Check((await new OpenCodeSessionControlCoordinator(new()).PreviewAsync(opaque.Origin(), Undo, Empty, Current)).Status == SessionControlStatus.Blocked
            && opaque.Mutations == 0, "opaque case variants cannot hide known unresolved background execution");
    }

    private static bool Start(Action action) { action(); return true; }
    private static string Error(string tag) => $"{{\"_tag\":\"{tag}\",\"message\":\"PRIVATE server body\""
        + (tag is "SessionBusyError" or "SessionNotFoundError" or "MessageNotFoundError" ? ",\"sessionID\":\"ses_control\"" : "")
        + (tag == "MessageNotFoundError" ? ",\"messageID\":\"msg_a\"" : "") + "}";
}

internal sealed class Server : HttpMessageHandler
{
    internal const string ModelInfo = "{\"id\":\"alias\",\"modelID\":\"provider-facing\",\"providerID\":\"provider\",\"name\":\"Choice\",\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]},\"variants\":[{\"id\":\"fast\"}],\"time\":{\"released\":1},\"cost\":[],\"status\":\"active\",\"enabled\":true,\"limit\":{\"context\":1000,\"output\":100}}";
    private readonly HttpClient _http;
    internal string Default = ModelInfo;
    internal string? Model, Stage;
    internal string? Title, ParentId;
    internal bool FailRefresh, CursorBudget, HasInbox;
    internal int Mutations, HistoryReads;
    internal string LastBody = "";
    internal Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnMutation;
    internal readonly List<(string Method, string Path, string Query, string Host)> Requests = new();
    internal string History = Page("{\"id\":\"msg_i\",\"type\":\"idle\",\"time\":{\"created\":3},\"outcome\":\"succeeded\"},"
        + User("msg_a", 2, "newer") + "," + User("msg_z", 1, "older", true));
    internal Server() { _http = new(this, false); }
    internal OpenCodeClient Client() => new(_http, new() { BaseUrl = "https://control.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
    internal SessionControlOrigin Origin() => new(new OpenCodePageSource("control-endpoint", 7, Client), 7, "ses_control", 1);
    internal static string Page(string rows, string? next = null) => "{\"data\":[" + rows + "],\"cursor\":{\"next\":" + JsonSerializer.Serialize(next) + "}}";
    internal static string User(string id, int created, string text, bool references = false) => $"{{\"id\":\"{id}\",\"type\":\"user\",\"time\":{{\"created\":{created}}},\"text\":{JsonSerializer.Serialize(text)}"
        + (references ? ",\"files\":[{\"data\":\"\",\"mime\":\"text/plain\",\"source\":{\"type\":\"uri\",\"uri\":\"https://never-fetch.invalid/private\"}}],\"agents\":[{\"name\":\"agent\"}],\"skills\":[{\"id\":\"skill\",\"name\":\"Skill\"}]" : "") + "}";
    internal static HttpResponseMessage Json(int status, string json) => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!; Requests.Add((request.Method.Method, uri.AbsolutePath, uri.Query, uri.Host));
        if (request.Method != HttpMethod.Get)
        {
            Mutations++; LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (OnMutation is not null) return await OnMutation(request);
            if (uri.AbsolutePath.EndsWith("/revert/stage"))
            {
                using var body = JsonDocument.Parse(LastBody);
                Stage = "{\"messageID\":" + body.RootElement.GetProperty("messageID").GetRawText() + "}";
                return Json(200, "{\"data\":" + Stage + "}");
            }
            if (uri.AbsolutePath.EndsWith("/prompt"))
                return Json(200, "{\"data\":{\"id\":\"inb_test\",\"sessionID\":\"ses_control\",\"type\":\"user\",\"payload\":{\"text\":\"continue\"}}}");
            if (uri.AbsolutePath.EndsWith("/revert")) Stage = null;
            if (uri.AbsolutePath.EndsWith("/model")) { using var body = JsonDocument.Parse(LastBody); Model = body.RootElement.GetProperty("model").GetRawText(); }
            return Json(204, "");
        }
        if (uri.AbsolutePath == "/api/model") return Json(200, "{\"location\":{\"directory\":\"/work space\"},\"data\":[" + ModelInfo + "]}");
        if (uri.AbsolutePath == "/api/model/default") return Json(200, "{\"location\":{\"directory\":\"/work space\"},\"data\":" + Default + "}");
        if (uri.AbsolutePath == "/api/session/active") return Json(200, "{\"data\":{}}");
        if (uri.AbsolutePath == "/api/session/ses_control")
        {
            if (FailRefresh && Mutations != 0) return Json(500, "{}");
            return Json(200, "{\"data\":{\"id\":\"ses_control\",\"projectID\":\"project\",\"cost\":0,\"tokens\":{},\"time\":{\"created\":1,\"updated\":3},\"location\":{\"directory\":\"/work space\"}"
                + (Title is null ? "" : ",\"title\":" + JsonSerializer.Serialize(Title)) + (ParentId is null ? "" : ",\"parentID\":" + JsonSerializer.Serialize(ParentId))
                + (Model is null ? "" : ",\"model\":" + Model) + (Stage is null ? "" : ",\"revert\":" + Stage) + "}}");
        }
        if (uri.AbsolutePath.EndsWith("/message"))
        {
            HistoryReads++;
            return Json(200, CursorBudget ? Page(User("msg_page" + HistoryReads, 100 - HistoryReads, "x"), "cursor" + HistoryReads) : History);
        }
        if (uri.AbsolutePath.EndsWith("/inbox") && HasInbox)
            return Json(200, "{\"data\":[{\"id\":\"inb_pending\",\"sessionID\":\"ses_control\",\"type\":\"user\",\"payload\":{\"text\":\"pending\"}}]}");
        if (uri.AbsolutePath.EndsWith("/permission") || uri.AbsolutePath.EndsWith("/form") || uri.AbsolutePath.EndsWith("/inbox")) return Json(200, "{\"data\":[]}");
        throw new InvalidOperationException("Unexpected fake route.");
    }
    protected override void Dispose(bool disposing) { if (disposing) _http.Dispose(); base.Dispose(disposing); }
}
