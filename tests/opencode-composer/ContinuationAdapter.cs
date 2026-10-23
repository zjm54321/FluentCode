using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static HttpResponseMessage ControlState(string sessionId = "ses_test", string? stage = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["id"] = sessionId, ["projectID"] = "project", ["cost"] = 0, ["tokens"] = new { },
            ["time"] = new { created = 1, updated = 1 }, ["location"] = new { directory = "/workspace" }
        };
        if (stage is not null) data["revert"] = new { messageID = stage };
        return Json(JsonSerializer.Serialize(new { data }));
    }

    private static async Task ContinuationAdapter()
    {
        await ContinuationCaptureAndConsent();
        await ContinuationSettlement();
        await ContinuationLegacyAndPolicy();
    }

    private static async Task<OpenCodeComposerContinuationQuestion> Question(Fixture f, OpenCodeComposerSendContext context)
    {
        var result = await f.Core.SubmitAsync(context);
        Check(result is OpenCodeComposerSubmitOutcome.ConfirmationRequired { MutationStarted: false, Effects: null },
            "known stage returns a non-started question, never a permit or mutation effects");
        var question = ((OpenCodeComposerSubmitOutcome.ConfirmationRequired)result).Question;
        Check(ReferenceEquals(question.Origin, context.Origin) && ReferenceEquals(question.Basis, context.Basis),
            "question preserves exact origin and authoritative basis across Page re-entry");
        using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test").WaitAsync(TimeSpan.FromSeconds(5));
        Check(lease.GetComposerSubmissionProtection() is null && lease.GetSessionControlProtection() is null
            && !f.Core.Snapshot.IsBusy, "question returns only after original lease/local attempt owner released");
        return question;
    }

    private static async Task ContinuationCaptureAndConsent()
    {
        using (var f = new Fixture())
        {
            f.Text(" exact\r\n文本 "); await f.File("first.txt", 5); await f.File("second.txt", 2);
            var one = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
            var two = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
            Check(one.Draft == two.Draft && one.Token == two.Token && one.Text == " exact\r\n文本 " && !one.Draft.Empty,
                "identical authoritative captures preserve exact strings/token and canonical fingerprint");
            var expected = SessionControlJson.Hash(new
            {
                version = 1, scope = one.Token.Scope, draftId = one.Token.Draft, edit = one.Token.Edit,
                text = one.Text, command = one.ServerCommand,
                files = f.Core.Snapshot.Attachments.Select(a => new { id = a.Id, name = a.Name, mime = a.Mime, bytes = a.Bytes }).ToArray()
            });
            Check(one.Draft.Fingerprint == expected, "fingerprint version/schema uses ordered persistent IDs and byte COUNTS, existing canonical helper");
            var context = f.Context();
            foreach (var forged in new[] { context.Basis with { }, context.Basis with { Text = "injected" },
                context.Basis with { HasFiles = false, Draft = new("fake", true) } })
                Check(await f.Core.SubmitAsync(context with { Basis = forged }) is OpenCodeComposerSubmitOutcome.NotStarted,
                    "record construction/copy cannot mint authoritative draft capability");
            Check(f.Handler.Posts == 0 && f.Handler.StateReads == 0, "forged bases cause neither preflight nor transport");
            f.Text("other"); f.Text(one.Text);
            Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Draft.Fingerprint != one.Draft.Fingerprint
                && await f.Core.SubmitAsync(context) is OpenCodeComposerSubmitOutcome.NotStarted, "edit then original text invalidates old consent basis");
            var beforeFileCycle = f.Core.CaptureDraft(f.Core.Snapshot.Token)!;
            await f.File("cycle.txt"); f.Core.RemoveAttachment(f.Core.Snapshot.Token, f.Core.Snapshot.Attachments.Last().Id);
            Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Draft.Fingerprint != beforeFileCycle.Draft.Fingerprint,
                "add/remove file cycle changes edit fingerprint even with original attachment inventory");
        }
        using (var f = new Fixture())
        {
            Check(f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Draft.Empty, "truly empty draft captured as empty");
            f.Text(" \t\n");
            Check(!f.Core.CaptureDraft(f.Core.Snapshot.Token)!.Draft.Empty, "whitespace is NOT empty for Undo/continuation basis");
            CommitRevocationWithoutCancellation(f);
            Check(f.Core.CaptureDraft(f.Core.Snapshot.Token) is null, "capture source boundary rejects committed revocation before cancellation");
        }
        using (var f = new Fixture())
        {
            await f.Command(); var stage = "msg_stage_a";
            f.Handler.ReadState = (_, _) => Task.FromResult(ControlState(stage: stage));
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var context = f.Context(); var a = await Question(f, context);
            stage = "msg_stage_b";
            var b = await Question(f, context with { Consent = a.ConsentToUse });
            Check(a.ConsentToUse != b.ConsentToUse && f.Handler.Posts == 0, "changed staged boundary requires a new explicit confirmation, zero POST");
            stage = null!;
            var cleared = await Question(f, context with { Consent = b.ConsentToUse });
            Check(cleared.ObservedState.Revert is null && cleared.ConsentToUse is null, "cleared-stage question explicitly requires null consent");
            var sent = await f.Core.SubmitAsync(context with { Consent = cleared.ConsentToUse });
            Check(sent is OpenCodeComposerSubmitOutcome.CommandAcknowledged { MutationStarted: true,
                Effects.Revert: OpenCodeComposerRevertEvidence.NoStageObserved } && f.Handler.StateReads == 4 && f.Handler.Posts == 1,
                "explicit cleared-stage re-entry performs a new read, then original unstaged command exactly once");
            Check(!f.Handler.Paths.Any(p => p.Contains("revert/commit", StringComparison.Ordinal)), "confirmation cycles never call explicit revert commit");
        }
        foreach (var change in new[] { "edit", "source", "unknown-read", "input-guard", "operation-owner" })
        {
            using var f = new Fixture(); await f.File(); f.Text("captured");
            var response = Signal<HttpResponseMessage>(); var readStarted = Signal<bool>();
            f.Handler.ReadState = (_, _) => { readStarted.TrySetResult(true); return response.Task; };
            var canStart = true; var owns = true;
            var context = f.Context() with { CanStart = _ => canStart, OwnsOperation = _ => owns };
            var pending = f.Core.SubmitAsync(context);
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Handler.StateReads == 1, "fresh stage read begins before mutation: " + change);
            if (change == "edit") f.Text("later");
            if (change == "source") { f.Switch("ses_other"); f.Switch("ses_test"); }
            if (change == "input-guard") canStart = false;
            if (change == "operation-owner") owns = false;
            response.SetResult(change == "unknown-read" ? Json("{\"data\":{}}") : ControlState(stage: "msg_a"));
            Check(await pending is OpenCodeComposerSubmitOutcome.NotStarted { MutationStarted: false, Effects: null } && f.Handler.Posts == 0,
                "changed draft/source/ownership or unknown stage is not an overridable question: " + change);
        }
        Console.WriteLine("PASS continuation group 1: authoritative canonical capture, forged/cycled bases, fresh stage consent/cleared-stage re-entry, source and input authority.");
    }

    private static async Task ContinuationSettlement()
    {
        using (var f = new Fixture())
        {
            await f.File(); f.Text("submitted");
            f.Handler.ReadState = (_, _) => Task.FromResult(ControlState(stage: "msg_a"));
            var context = f.Context(); var question = await Question(f, context);
            var response = Signal<HttpResponseMessage>(); var postStarted = Signal<bool>();
            f.Handler.Send = (_, _) => { postStarted.TrySetResult(true); return response.Task; };
            var canStart = true;
            context = context with { Consent = question.ConsentToUse, CanStart = _ => canStart };
            var enteredHandoff = false;
            var pending = f.Core.SubmitAsync(context, acceptedDraftHandoff: async (token, finalize, _) =>
            {
                enteredHandoff = true;
                using (var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test").WaitAsync(TimeSpan.FromSeconds(5)))
                    Check(lease.GetComposerSubmissionProtection() is null && lease.GetSessionControlProtection() is null,
                        "staged prompt settled BOTH protections and released original lease before handoff");
                Check(token == context.Basis.Token && finalize() == OpenCodeComposerDraftFinalization.Retained,
                    "later edited staged prompt retains entire enhanced draft at accepted handoff");
            });
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var paired = ProtectedState(f);
            Check(paired.Composer is not null && paired.Control is not null && paired.Composer.OperationId == paired.Control.OperationId
                && paired.Control.Kind == SessionControlKind.RevertContinuation && f.Handler.Posts == 1,
                "real validated starter installs paired exact operation IDs before one POST");
            f.Text("later draft"); canStart = false; response.SetResult(Json(Receipt));
            var result = await pending;
            Check(result is OpenCodeComposerSubmitOutcome.PromptAccepted { MutationStarted: true, Effects: {
                Revert: OpenCodeComposerRevertEvidence.StagedPromptAccepted, ProtectionRetained: false } }
                && result.Effects.ConsentedStageFingerprint == question.ConsentToUse!.StageFingerprint,
                "valid staged receipt settles via stable operation ownership, independent of failed later CanStart");
            Check(enteredHandoff && f.Core.Snapshot.Text == "later draft" && f.Core.Snapshot.Attachments.Count == 1
                && f.Handler.Posts == 1 && f.Handler.StateReads == 2, "acceptance needs no display GET/current-stage-empty claim and never repeats POST");
        }
        foreach (var responseKind in new[] { "command204", "400", "404", "409", "transport", "bad-receipt", "settlement-failure", "origin-loss", "handoff-failure", "publish-failure" })
        {
            using var f = new Fixture();
            if (responseKind == "command204") await f.Command(); else { await f.File(); f.Text("draft"); }
            f.Handler.ReadState = (_, _) => Task.FromResult(ControlState(stage: "msg_a"));
            var context = f.Context(); var question = await Question(f, context);
            var response = Signal<HttpResponseMessage>(); var postStarted = Signal<bool>();
            f.Handler.Send = (_, _) => { postStarted.TrySetResult(true); return response.Task; };
            var pending = f.Core.SubmitAsync(context with { Consent = question.ConsentToUse }, acceptedDraftHandoff: (_, _, _) =>
                responseKind == "handoff-failure" ? Task.FromException(new InvalidOperationException("synthetic display failure")) : Task.CompletedTask);
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Handler.Posts == 1, "staged failure scenario reached one actual synthetic POST: " + responseKind);
            if (responseKind == "origin-loss") f.Switch("ses_other");
            if (responseKind == "settlement-failure")
            {
                // Test-only injection makes the real paired gate's exact-ID settlement throw; no gate substitute.
                var state = GateState(f);
                state.ControlUnknown = state.ControlUnknown! with { OperationId = Guid.NewGuid() };
            }
            if (responseKind == "publish-failure") f.Core.SnapshotChanged += (_, _) => throw new InvalidOperationException("synthetic refresh failure");
            if (responseKind == "transport") response.SetException(new HttpRequestException("synthetic uncertain admission"));
            else response.SetResult(responseKind switch
            {
                "command204" => new(HttpStatusCode.NoContent),
                "400" => new(HttpStatusCode.BadRequest), "404" => new(HttpStatusCode.NotFound), "409" => new(HttpStatusCode.Conflict),
                "bad-receipt" => Json("{\"data\":{}}"), _ => Json(Receipt)
            });
            var result = await pending;
            var expectedAccepted = responseKind is "settlement-failure" or "handoff-failure" or "publish-failure";
            Check(responseKind switch
            {
                "command204" => result is OpenCodeComposerSubmitOutcome.CommandAcknowledged,
                "400" or "404" or "409" => result is OpenCodeComposerSubmitOutcome.Rejected,
                "origin-loss" => result is OpenCodeComposerSubmitOutcome.OriginLost,
                _ => expectedAccepted ? result is OpenCodeComposerSubmitOutcome.PromptAccepted : result is OpenCodeComposerSubmitOutcome.Unknown
            }, "transport acceptance/rejection identity is separate from rollback evidence: " + responseKind);
            var resolved = responseKind is "handoff-failure" or "publish-failure";
            Check(result.MutationStarted && result.Effects?.Revert == (resolved ? OpenCodeComposerRevertEvidence.StagedPromptAccepted : OpenCodeComposerRevertEvidence.Unresolved)
                && result.Effects.ProtectionRetained == !resolved && f.Handler.Posts == 1,
                "honest staged effects and retained protections, no generic-4xx continuation unlock: " + responseKind);
            if (!resolved)
            {
                using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
                Check(lease.GetSessionControlProtection() is not null && !lease.CanSubmitComposer()
                    && lease.CanSubmit("form_new", NotificationNavigationKind.Form), "uncertain rollback blocks composer but not blanket Form interactions: " + responseKind);
            }
            Check(!f.Handler.Paths.Any(p => p.Contains("revert/commit", StringComparison.Ordinal)), "zero explicit commit: " + responseKind);
        }
        Console.WriteLine("PASS continuation group 2: paired staged initiation, held response/later edits, honest command204/4xx/uncertain evidence, accepted receipt survival and origin loss.");
    }

    // Inspection only, while the tested request owns the lease. Used to assert actual paired Begin,
    // and one deliberate mismatch fault injection; not a gate implementation or authorization bypass.
    private static OpenCodeInteractionGate.SessionState GateState(Fixture f)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sessions = (Dictionary<(string, string), OpenCodeInteractionGate.SessionState>)typeof(OpenCodeInteractionGate).GetField("_sessions", flags)!.GetValue(f.Gate)!;
        return sessions[(f.Source.Identity, "ses_test")];
    }
    private static (ComposerSubmissionProtection? Composer, SessionControlProtection? Control) ProtectedState(Fixture f)
    { var state = GateState(f); return (state.ComposerUnknown, state.ControlUnknown); }

    // Synthetic Page orchestration only. Production Core helper/coordinator/gate/guarded transport run;
    // actual Page confirmation/history/receipt publication is owned and tested by the Page lane.
    private static async Task<OpenCodeComposerSubmitOutcome> LegacyAttempt(Fixture f, OpenCodeComposerSendContext context,
        bool duplicateStarter = false)
    {
        var coordinator = new OpenCodeSessionControlCoordinator(f.Gate);
        SessionControlContinuationPermit? permit = null;
        var started = false; var settled = false; var retained = false;
        OpenCodeComposerSubmitOutcome outcome = new OpenCodeComposerSubmitOutcome.NotStarted();
        using (var lease = await f.Gate.EnterAsync(context.Origin.Source.Identity, context.Origin.SessionId))
        {
            var preparation = await coordinator.PrepareContinuationAsync(context.Origin, lease, context.Basis.Draft,
                context.Consent, o => context.OwnsOperation(o) && context.CanStart(o));
            permit = preparation.Permit;
            if (preparation.Status == SessionControlStatus.ConfirmationRequired && preparation.State is not null)
                return new OpenCodeComposerSubmitOutcome.ConfirmationRequired(new(context.Origin, context.Basis, preparation.State, preparation.RequiredConsent));
            if (preparation.Status is not (SessionControlStatus.NoStage or SessionControlStatus.Ready)) return outcome;
            var operation = Guid.NewGuid();
            var receipt = await f.Client.SendPromptGuardedAsync(context.Origin.SessionId, context.Basis.Text, null, send =>
            {
                bool Start() => f.Core.TryStartLegacySend(context, lease, () =>
                {
                    if (permit is not null) return coordinator.TryStartContinuation(permit, lease, operation,
                        context.Basis.Draft, null, context.CanStart, () => { started = true; send(); });
                    started = true; send(); return true; // The unstaged test sender adds NO composer/control protection.
                });
                var first = Start();
                if (duplicateStarter) Check(!Start(), "legacy helper rejects duplicate starter before a second callback/POST");
                return first;
            }, default);
            outcome = new OpenCodeComposerSubmitOutcome.PromptAccepted(receipt);
            settled = permit is null || coordinator.CompleteContinuation(permit, lease, operation,
                NotificationInteractionStatus.Succeeded, NotificationInteractionStatus.Succeeded, context.OwnsOperation);
            retained = lease.GetSessionControlProtection() is not null;
        }
        return outcome with { MutationStarted = started,
            Effects = OpenCodeComposerController.DescribeSendEffects(permit, started, outcome, settled, retained) };
    }

    private static async Task ContinuationLegacyAndPolicy()
    {
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new Fixture(); f.Text("plain"); if (enhanced) await f.File();
            using (var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test"))
            {
                lease.BeginMutation("form_unknown", NotificationNavigationKind.Form);
                lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown);
            }
            var result = enhanced ? await f.Core.SubmitAsync(f.Context()) : await LegacyAttempt(f, f.Context(), duplicateStarter: true);
            Check(result is OpenCodeComposerSubmitOutcome.PromptAccepted { MutationStarted: true,
                Effects: { Revert: OpenCodeComposerRevertEvidence.NoStageObserved, ProtectionRetained: false } }
                && f.Handler.StateReads == 1 && f.Handler.Posts == 1, "ordinary/enhanced NoStage each read fresh stage and preserve Form-only-unknown policy: " + enhanced);
            using var check = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            Check(check.GetProtectionStatus("form_unknown", NotificationNavigationKind.Form) == NotificationInteractionStatus.OutcomeUnknown
                && check.GetComposerSubmissionProtection() is null && check.GetSessionControlProtection() is null,
                "NoStage sender leaves existing Form protection intact and no new retained protection: " + enhanced);
        }
        using (var f = new Fixture())
        {
            f.Text("ordinary staged"); f.Handler.ReadState = (_, _) => Task.FromResult(ControlState(stage: "msg_a"));
            var context = f.Context(); var q = (OpenCodeComposerSubmitOutcome.ConfirmationRequired)await LegacyAttempt(f, context);
            Check(!q.MutationStarted && q.Effects is null && f.Handler.Posts == 0, "synthetic ordinary workflow asks without POST");
            var result = await LegacyAttempt(f, context with { Consent = q.Question.ConsentToUse }, duplicateStarter: true);
            Check(result is OpenCodeComposerSubmitOutcome.PromptAccepted { Effects.Revert: OpenCodeComposerRevertEvidence.StagedPromptAccepted }
                && f.Handler.StateReads == 2 && f.Handler.Posts == 1 && !f.Handler.Paths.Any(p => p.Contains("revert/commit", StringComparison.Ordinal)),
                "ordinary staged helper uses same held lease/one control operation/one POST/zero commit");
        }
        using (var f = new Fixture())
        {
            await f.File(); var context = f.Context();
            Check(await f.Core.SubmitAsync((OpenCodeComposerSendContext)null!) is OpenCodeComposerSubmitOutcome.NotStarted
                && f.Handler.StateReads == 0 && f.Handler.Posts == 0, "missing context cannot bypass stage preflight");
            var forged = context with { Origin = context.Origin with { SessionId = "ses_other" } };
            Check(await f.Core.SubmitAsync(forged) is OpenCodeComposerSubmitOutcome.NotStarted, "origin/basis route binding mismatch rejected");
            f.Core.RemoveAttachment(f.Core.Snapshot.Token, f.Core.Snapshot.Attachments.Single().Id); f.Text("ordinary");
            Check(await f.Core.SubmitAsync(f.Context()) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.StateReads == 0,
                "plain text is never silently routed through enhanced policy");
        }
        foreach (var reason in new[] { "wrong-lease", "disposed-lease", "stale-basis", "forged-basis", "intent", "ticket", "files", "command", "can-start", "owner", "control", "missing-context" })
        {
            using var f = new Fixture(); f.Text("ordinary");
            if (reason == "files") await f.File();
            if (reason == "command") await f.Command();
            var context = f.Context();
            if (reason is "intent" or "ticket")
            {
                var intent = await Stage(f, OpenCodeComposerClientActionKind.EditDraft);
                if (reason == "ticket") f.Core.TryTakeClientAction(f.Core.Snapshot.Token, intent.Id);
                context = f.Context();
            }
            if (reason == "stale-basis") { f.Text("changed"); f.Text("ordinary"); }
            if (reason == "forged-basis") context = context with { Basis = context.Basis with { } };
            if (reason == "can-start") context = context with { CanStart = _ => false };
            if (reason == "owner") context = context with { OwnsOperation = _ => false };
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, reason == "wrong-lease" ? "ses_other" : "ses_test");
            if (reason == "disposed-lease") lease.Dispose();
            if (reason == "control")
            {
                var operation = Guid.NewGuid();
                lease.BeginSessionControl(operation, SessionControlKind.ClearRevert, new string('A', 64));
                lease.CompleteSessionControl(operation, ProtectedUnknown);
            }
            var calls = 0;
            Check(!f.Core.TryStartLegacySend(reason == "missing-context" ? null! : context, lease, () => { calls++; return true; })
                && calls == 0 && f.Handler.Posts == 0, "ordinary helper refuses invalid route/lease/captured authority before supplied callback: " + reason);
        }
        using (var f = new Fixture())
        {
            await f.File();
            using (var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test"))
            {
                var operation = Guid.NewGuid();
                lease.BeginSessionControl(operation, SessionControlKind.ClearRevert, new string('A', 64));
                lease.CompleteSessionControl(operation, ProtectedUnknown);
            }
            Check(await f.Core.SubmitAsync(f.Context()) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0,
                "enhanced NoStage cannot bypass retained session-control protection");
        }
        using (var f = new Fixture())
        {
            await f.File(); f.Handler.ReadState = (_, _) => Task.FromResult(ControlState(stage: "msg_a"));
            var context = f.Context(); var coordinator = new OpenCodeSessionControlCoordinator(f.Gate);
            using var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test");
            var q = await coordinator.PrepareContinuationAsync(context.Origin, lease, context.Basis.Draft, null, context.CanStart);
            var ready = await coordinator.PrepareContinuationAsync(context.Origin, lease, context.Basis.Draft, q.RequiredConsent, context.CanStart);
            var op = Guid.NewGuid(); var starts = 0;
            Check(coordinator.TryStartContinuation(ready.Permit!, lease, op, context.Basis.Draft, ComposerSubmissionKind.Prompt, context.CanStart, () => starts++), "real same-lease paired permit starts");
            Check(!coordinator.TryStartContinuation(ready.Permit!, lease, op, context.Basis.Draft, ComposerSubmissionKind.Prompt, context.CanStart, () => starts++)
                && !coordinator.CompleteContinuation(ready.Permit!, lease, Guid.NewGuid(), NotificationInteractionStatus.Succeeded,
                    NotificationInteractionStatus.Succeeded, context.OwnsOperation), "paired permit cannot restart or settle a different operation");
            Check(OpenCodeComposerController.DescribeSendEffects(ready.Permit, false, new OpenCodeComposerSubmitOutcome.NotStarted(), false, true) is null,
                "no transport means no effects even when a stage/retained protection is known");
            Check(OpenCodeComposerController.DescribeSendEffects(ready.Permit, true, new OpenCodeComposerSubmitOutcome.CommandAcknowledged(), true, false)!.Revert
                == OpenCodeComposerRevertEvidence.Unresolved, "command204 alone never becomes commitment evidence, even with no retained protection");
            Check(coordinator.CompleteContinuation(ready.Permit!, lease, op, NotificationInteractionStatus.Succeeded,
                NotificationInteractionStatus.Succeeded, context.OwnsOperation) && starts == 1 && lease.GetComposerSubmissionProtection() is null
                && lease.GetSessionControlProtection() is null, "exact paired settlement clears both slots atomically");
        }
        Console.WriteLine("PASS continuation group 3: real ordinary start guard, same-lease coordinator, original NoStage/Form policy, protected/invalid routes, paired exactness, no explicit commit.");
    }
    private const NotificationInteractionStatus ProtectedUnknown = NotificationInteractionStatus.OutcomeUnknown;
}
