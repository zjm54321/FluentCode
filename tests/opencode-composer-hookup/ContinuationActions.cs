// Actual Page workflow/legacy attempt, real Core/coordinator/gate/HTTP/ledger/evidence publisher.
// Controls, native dialogs, transport responses and queued UI handoffs are explicit doubles.
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml;

internal static class ContinuationTests
{
    private static int checks;
    private static void Check(bool value, string note) { checks++; if (!value) throw new Exception(note); }
    internal static async Task Run()
    {
        await ConsentAndInput(); var a = checks;
        await OutcomesAndPolicy(); var b = checks - a;
        await HistoryAndGuards();
        Console.WriteLine($"PASS: 3 Page-continuation groups, {checks} assertions ({a}/{b}/{checks-a-b}); actual Page/core/backend/input publisher, HTTP/WinUI/dialog/scheduler doubles; no native/live actions.");
        var prior = checks; await ReviewRegressions();
        Console.WriteLine($"PASS: Page review regressions, {checks-prior} assertions; actual source/controller locks, owned uncertainty and no-start/origin-lost contrasts.");
    }
    private static void Stage(NativeTests.Fixture f) => f.Stage = "{\"messageID\":\"msg_newer\"}";
    private static void Confirm() => OpenCodeComposerContinuationDialog.Next = p => Task.FromResult(new OpenCodeContinuationResult(OpenCodeContinuationOutcome.Confirmed, p));
    private static async Task ConsentAndInput()
    {
        foreach (var changed in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); Stage(f); f.Page.Text("continue");
            OpenCodeComposerContinuationQuestion? first = null; var questions = 0;
            OpenCodeComposerContinuationDialog.Next = async p => {
                var q = (OpenCodeComposerContinuationQuestion)p.Basis; questions++;
                using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
                    Check(lease.GetSessionControlProtection() is null && lease.GetComposerSubmissionProtection() is null, "dialog is outside actual shared lease; no pre-confirm Begin");
                if (first is null) { first = q; f.Stage = changed ? "{\"messageID\":\"msg_older\"}" : null; }
                else {
                    Check(ReferenceEquals(first.Origin, q.Origin) && ReferenceEquals(first.Basis, q.Basis), "one exact origin/basis/page owner retained across questions");
                    Check(p.HasStagedRevert == changed && (q.ConsentToUse is null) == !changed, "cleared-stage question truthful and null-consent re-entry");
                }
                return new(OpenCodeContinuationOutcome.Confirmed, p);
            };
            f.Mutation = _ => Task.FromResult(Program.Receipt());
            await f.Page.Send();
            Check(questions == 2 && f.Http.Posts == 1 && f.Page.Receipts == 1, "changed/cleared state needs second explicit gesture and exactly one prompt");
            Check(f.MutationPaths.All(p => !p.Contains("commit")), "no explicit rollback commit endpoint");
        }
        foreach (var change in new[] { "unflushed", "ime-cycle", "source", "draft" })
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); if (enhanced) await f.Page.File(); f.Page.Text("basis");
            var gateRead = Program.Signal<bool>(); var respond = f.Http.Respond;
            f.Http.Respond = async (r, ct) => { if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/session/ses_test") await gateRead.Task; return await respond(r, ct); };
            var token = f.Page.Snapshot.Token; var send = f.Page.Send();
            if (change == "unflushed") { f.Page.ObserveText("withheld input"); Check(f.Page.Snapshot.Token == token, "real publisher invalidates while controller token still unchanged"); }
            if (change == "ime-cycle") { f.Page.Ime(true); f.Page.Ime(false); }
            if (change == "source") f.Source.Revoke();
            if (change == "draft") { f.Page.DrainView(); f.Page.Text("different"); }
            gateRead.SetResult(true); await send;
            Check(f.Http.Posts == 0, $"{change}/{enhanced}: withheld state read resumes with zero POST");
        }
        using (var f = new NativeTests.Fixture())
        {
            Stage(f); f.Page.Text("cancel"); await f.Page.Send();
            Check(f.Http.Posts == 0 && f.Page.Snapshot.Text == "cancel" && !f.Page.HistoryDeferred, "default/cancel leaves draft and closes workflow history deferral");
        }
        using (var f = new NativeTests.Fixture())
        {
            f.Page.Text("unknown state"); var questions = 0;
            OpenCodeComposerContinuationDialog.Next = p => { questions++; return Task.FromResult(new OpenCodeContinuationResult(OpenCodeContinuationOutcome.Confirmed, p)); };
            f.Http.Respond = (_, _) => Task.FromResult(Program.Json("{}")); await f.Page.Send();
            Check(questions == 0 && f.Http.Posts == 0, "unknown/read-failed state is NotStarted, never an overridable question");
        }
        using (var f = new NativeTests.Fixture())
        {
            Stage(f); f.Page.Text("exact question");
            OpenCodeComposerContinuationDialog.Next = p => Task.FromResult(new OpenCodeContinuationResult(OpenCodeContinuationOutcome.Confirmed, p with { }));
            await f.Page.Send(); Check(f.Http.Posts == 0, "copied dialog presentation cannot authorize the outstanding question");
        }
    }
    private static async Task OutcomesAndPolicy()
    {
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); Stage(f); Confirm(); if (enhanced) await f.Page.File(); f.Page.Text("submitted");
            var response = Program.Signal<HttpResponseMessage>(); var entered = Program.Signal<bool>();
            f.Mutation = _ => { entered.TrySetResult(true); return response.Task; };
            var send = f.Page.Send();
            // Attached prompts prepare their request off-thread; Send need not reach HTTP synchronously.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Http.Posts == 1 && f.Page.HistoryDeferred, "staged send holds history through HTTP");
            f.Page.DrainView(); f.Page.Text("later input"); response.SetResult(Program.Receipt()); await send;
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Check(lease.GetSessionControlProtection() is null && lease.GetComposerSubmissionProtection() is null, "stable post-start owner settles exact protections despite edit");
            Check(f.Page.Snapshot.Text == "later input" && f.Page.Receipts == 1 && f.Page.Tracked == 1, "accepted receipt survives edit; original draft finalizer does not erase newer input");
            Check(f.Page.HistoryNeedsState && !f.Page.HistoryDeferred, "staged effects reset retained history but require fresh authoritative boundary");
        }
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); if (enhanced) await f.Page.File(); f.Page.Text("ordinary policy");
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
            { lease.BeginMutation("form_x", NotificationNavigationKind.Form); lease.CompleteMutation(NotificationInteractionStatus.OutcomeUnknown); }
            f.Mutation = _ => Task.FromResult(Program.Receipt()); await f.Page.Send();
            using var after = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Check(f.Http.Posts == 1 && f.Page.Receipts == 1 && !f.Page.HistoryNeedsState, "NoStage preserves ordinary/enhanced Form-only unknown policy and retained history");
            Check(after.GetProtectionStatus("form_x", NotificationNavigationKind.Form) == NotificationInteractionStatus.OutcomeUnknown, "NoStage never unlocks unrelated Form protection");
        }
        foreach (var failure in new[] { "command204", "ordinary400", "enhanced400", "disconnect" })
        {
            using var f = new NativeTests.Fixture(); Stage(f); Confirm();
            if (failure == "command204") await f.Page.ServerModelsCommand();
            else { if (failure == "enhanced400") await f.Page.File(); f.Page.Text("submit"); }
            f.Mutation = _ => failure == "disconnect" ? Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic"))
                : Task.FromResult(new HttpResponseMessage(failure == "command204" ? HttpStatusCode.NoContent : HttpStatusCode.BadRequest));
            // The command acknowledgement is in-place feedback (a38fac1), not a banner. Clear what an earlier test left.
            FluentCode.Motion.ActionFeedback.LastSuccessAnnouncement = null;
            await f.Page.Send();
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
                Check(lease.GetSessionControlProtection() is not null && lease.GetComposerSubmissionProtection() is null, "command204/generic4xx/unknown retain honest rollback protection separately");
            Check(f.Http.Posts == 1 && f.Page.HistoryNeedsState && f.Page.Receipts == 0 && f.Page.Working == 0, "unresolved effects reset history, no fabricated receipt/Working");
            if (failure == "command204") Check(FluentCode.Motion.ActionFeedback.LastSuccessAnnouncement is { } acknowledgement && acknowledgement.Contains("命令回调") && f.Page.Messages.Any(m => m.Contains("撤回影响")), "command identity retained alongside unresolved rollback evidence");
            f.Page.Discard(); f.Page.Text("must not retry"); await f.Page.Send(); Check(f.Http.Posts == 1, "retained control protection cannot be bypassed by another Send");
        }
    }
    private static async Task HistoryAndGuards()
    {
        using (var f = new NativeTests.Fixture())
        {
            Stage(f); Confirm(); f.Page.Text("handoff"); f.Page.HoldHandoff(); f.Mutation = _ => Task.FromResult(Program.Receipt());
            var send = f.Page.Send(); await f.Page.HandoffEntered.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Page.HistoryDeferred && f.Page.Receipts == 0, "history remains deferred through whole accepted-draft handoff");
            f.Page.ReleaseHandoff(); await send; Check(!f.Page.HistoryDeferred && f.Page.Receipts == 1, "history resumes only after handoff and original-owner settlement");
        }
        foreach (var kind in new[] { "native", "stop" })
        {
            using var f = new NativeTests.Fixture();
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Task action;
            if (kind == "native") { await f.Page.StageNative(OpenCodeComposerClientActionKind.Undo, "undo"); action = f.Page.RunNative(); }
            else { f.Page.SetWorking(); action = f.Page.StopDirect(); }
            f.Page.ObserveText("withheld while gate pending"); lease.Dispose(); await action;
            Check(f.Http.Posts == 0, "native/actual Stop checks published unflushed input at final guard");
        }
        using (var f = new NativeTests.Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.ChooseModel, "models");
            var read = Program.Signal<bool>(); var respond = f.Http.Respond;
            f.Http.Respond = async (r, ct) => { if (r.RequestUri!.AbsolutePath == "/api/session/ses_test") await read.Task; return await respond(r, ct); };
            var action = f.Page.RunNative(); var token = f.Page.Snapshot.Token;
            f.Page.ObserveText("unflushed during native preflight"); read.SetResult(true); await action;
            Check(f.Http.Posts == 0 && f.Page.Snapshot.Token == token, "native exact capture rejects real unpublished-to-Core input during awaited state read");
        }
        using (var f = new NativeTests.Fixture())
        {
            Stage(f); Confirm(); f.Page.Text("old"); var response = Program.Signal<HttpResponseMessage>(); f.Mutation = _ => response.Task;
            var old = f.Page.Send(); f.Page.Switch("ses_other"); f.Page.AcceptHistory(null); var epoch = f.Page.Epoch;
            response.SetResult(Program.Receipt()); await old;
            Check(f.Page.Epoch == epoch && f.Page.Receipts == 0, "origin-lost response/finally cannot reset or publish into newer page");
        }
    }

    private static async Task ReviewRegressions()
    {
        using (var f = new NativeTests.Fixture())
        {
            f.Page.Text("lock proof");
            var context = f.Page.CreateReviewSendContext();
            var coreLock = typeof(OpenCodeComposerController).GetField("_sync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(f.Page.Controller)!;
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var holdingSource = Task.Run(() => f.Source.TryExecute(CancellationToken.None, () =>
            { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("bounded source hold expired"); }));
            Check(entered.Wait(TimeSpan.FromSeconds(2)), "actual Source lock acquired by independent thread");
            Task<bool>? evaluation = null;
            try
            {
                evaluation = Task.Run(() => { lock (coreLock) return context.OwnsOperation(context.Origin) && context.CanStart(context.Origin); });
                var winner = await Task.WhenAny(evaluation, Task.Delay(TimeSpan.FromSeconds(2)));
                Check(ReferenceEquals(winner, evaluation) && await evaluation,
                    "actual Page delegates finish under controller lock while another thread holds Source lock");
            }
            finally
            {
                release.Set(); await holdingSource.WaitAsync(TimeSpan.FromSeconds(5));
                if (evaluation is not null) await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        foreach (var originLost in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); var response = Program.Signal<HttpResponseMessage>();
            f.Mutation = _ => response.Task;
            await f.Page.StageNative(OpenCodeComposerClientActionKind.Undo, "undo"); var action = f.Page.RunNative();
            var epoch = f.Page.Epoch;
            if (originLost) { f.Page.Switch("ses_other"); epoch = f.Page.Epoch; }
            response.SetException(new HttpRequestException("synthetic lost rollback response")); await action;
            Check(f.Http.Posts == 1 && (originLost ? f.Page.Epoch == epoch : f.Page.Epoch > epoch && f.Page.HistoryNeedsState),
                "uncertain started rollback invalidates only its live origin and requires boundary reconciliation");
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Check(lease.GetSessionControlProtection() is not null, "uncertain rollback keeps exact protection; no retry/unlock");
        }
        using (var f = new NativeTests.Fixture())
        {
            await f.Page.StageNative(OpenCodeComposerClientActionKind.Undo, "undo"); var epoch = f.Page.Epoch;
            OpenCodeRollbackConfirmationDialog.Next = p => Task.FromResult(new OpenCodeRollbackConfirmationResult(OpenCodeRollbackOutcome.Cancelled, p, false, false));
            await f.Page.RunNative(); Check(f.Http.Posts == 0 && f.Page.Epoch == epoch, "cancelled/not-started rollback does not invalidate history");
        }
    }
}
namespace FluentCode.Pages
{
    public sealed partial class OpenCodePage
    {
        internal bool HistoryDeferred => _messageHistoryWorkflowOwner is not null;
        internal bool HistoryNeedsState => _messageHistoryNeedsState;
        internal OpenCodeComposerSendContext CreateReviewSendContext()
        {
            if (!_composerView!.PrepareSubmission()) throw new Exception("input not clean");
            return CreateComposerSendContext(Controller, _composerView, Controller.CaptureDraft(Snapshot.Token)!,
                _composerView.InputEvidence, BeginLocalAgentSubmission());
        }
        internal void Ime(bool value) { _isComposing = value; _composerView!.Evidence.CompositionChanged(); }
        internal async Task ServerModelsCommand()
        {
            Text("/models arguments"); await Controller.LoadCatalogAsync(Snapshot.Token, _composerView!.Lifetime);
            if (!_composerView.Choose(Snapshot.Token, Snapshot.CatalogHandle!, new OpenCodeComposerCommandIdentity.Server("models"))) throw new Exception("server command selection failed");
            DrainView();
        }
    }
}
namespace FluentCode.Pages.OpenCode
{
    internal static class OpenCodeComposerContinuationDialog
    {
        internal static Func<OpenCodeContinuationPresentation, Task<OpenCodeContinuationResult>> Next = null!;
        internal static Task<OpenCodeContinuationResult> ShowAsync(XamlRoot? root, OpenCodeContinuationPresentation p, CancellationToken ct = default) => Next(p);
    }
}
