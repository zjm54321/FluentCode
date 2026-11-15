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
        prior = checks; SendPromptModel(); await SendAfterUndo();
        Console.WriteLine($"PASS: send after undo, {checks-prior} assertions; no prompt after a files-free undo that is on screen, one plain confirmation otherwise, changed or unreadable stages never committed silently.");
    }
    // The page in these fixtures starts with no undo on screen, so the groups above send into a stage the page was
    // NOT showing: they keep asking. Shown() puts the undo on screen the way an accepted undo does.
    private const string SnapshotOnlyStage = "{\"messageID\":\"msg_newer\",\"snapshot\":\"snap\",\"files\":[]}";
    private const string FileStage = "{\"messageID\":\"msg_newer\",\"snapshot\":\"snap\",\"files\":["
        + "{\"file\":\"a.txt\",\"patch\":\"\",\"additions\":1,\"deletions\":0,\"status\":\"modified\"},"
        + "{\"file\":\"b.txt\",\"patch\":\"\",\"additions\":0,\"deletions\":2,\"status\":\"deleted\"}]}";
    private static void Shown(NativeTests.Fixture f, string stage) { f.Stage = stage; f.Page.AcceptHistory("msg_newer"); }
    private static int Questions;
    private static void CountQuestions(Action<OpenCodeContinuationPresentation>? seen = null, OpenCodeContinuationOutcome answer = OpenCodeContinuationOutcome.Cancelled)
    {
        Questions = 0;
        OpenCodeComposerContinuationDialog.Next = p => { Questions++; seen?.Invoke(p); return Task.FromResult(new OpenCodeContinuationResult(answer, p)); };
    }

    // What the one remaining confirmation says, and which button is the default (WinUI-free state; the dialog only renders it).
    private static void SendPromptModel()
    {
        object basis = new();
        var files = new OpenCodeContinuationConfirmationState(new(false, 2, basis));
        Check(files.Title == "发送这条消息？" && OpenCodeContinuationConfirmationState.PrimaryText == "发送"
            && OpenCodeContinuationConfirmationState.CloseText == "取消" && !files.ConfirmIsDefault,
            "a question as title, the verb on the button, and 取消 as the default button");
        Check(files.Summary == "发送后，已撤回的消息会被永久删除，撤回时恢复的 2 个文件保持现在的样子，不能再用“恢复撤回”还原。"
            && files.Cautions().Count == 0 && files.StageNote is null && files.CommandNote is null,
            "one sentence: the undone messages are deleted for good and the restored files stay as they are; no general caution lines: " + files.Summary);
        Check(!(files.Title + files.Summary).Contains("ses_", StringComparison.Ordinal)
            && typeof(OpenCodeContinuationPresentation).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .All(property => property.PropertyType != typeof(string)),
            "the confirmation has no place for a session id or any other caption");
        var noFiles = new OpenCodeContinuationConfirmationState(new(false, 0, basis));
        Check(noFiles.Summary == "发送后，已撤回的消息会被永久删除，不能再用“恢复撤回”还原。", "without restored files nothing is said about files");

        var command = new OpenCodeContinuationConfirmationState(new(true, 0, basis));
        Check(command.Title == "发送这条命令？" && command.Summary.Contains("可能被永久删除", StringComparison.Ordinal)
            && command.Cautions().Count == 1 && command.Cautions()[0] == command.CommandNote
            && command.CommandNote!.Contains("无法确认", StringComparison.Ordinal) && command.CommandNote.Contains("重启 FluentCode", StringComparison.Ordinal)
            && !command.ConfirmIsDefault,
            "a server command: one caution line that names its own consequence: " + command.CommandNote);

        foreach (var (view, word) in new[] { (OpenCodeContinuationStageView.Moved, "位置"), (OpenCodeContinuationStageView.NotShown, "还没有显示"), (OpenCodeContinuationStageView.Unknown, "还没有加载完") })
        {
            var differs = new OpenCodeContinuationConfirmationState(new(false, 0, basis) { StageView = view });
            Check(differs.Cautions().Count == 1 && differs.StageNote!.Contains(word, StringComparison.Ordinal) && !differs.ConfirmIsDefault,
                $"an undo that is not the one on screen ({view}) is said in one line: " + differs.StageNote);
        }
        var gone = new OpenCodeContinuationConfirmationState(new(true, 3, basis) { HasStagedRevert = false, StageView = OpenCodeContinuationStageView.Moved });
        Check(gone.Title == "仍然发送这条命令？" && gone.Summary.Contains("已经不存在", StringComparison.Ordinal) && gone.Cautions().Count == 0
            && !gone.Summary.Contains("永久删除", StringComparison.Ordinal) && !gone.Summary.Contains("文件", StringComparison.Ordinal),
            "an undo that disappeared while the question was open: nothing is promised about messages or files");

        // The dialog's inputs come from the coordinator's own predicates.
        var source = new OpenCodePageSource("model-" + Guid.NewGuid(), 1, () => throw new Exception("no client"));
        var origin = new SessionControlOrigin(source, 1, "ses_test", 1) { ObservedStage = new("msg_newer") };
        var snapshotOnly = new FluentCode.Services.OpenCode.SessionControlRevert("msg_newer", null, true, 0, new string('A', 64));
        var described = OpenCodeContinuationPresentation.Describe(origin, snapshotOnly with { FileCount = 2 }, false, basis);
        Check(described is { RestoredFileCount: 2, IsServerCommand: false, HasStagedRevert: true, StageView: OpenCodeContinuationStageView.AsShown }
            && ReferenceEquals(described.Basis, basis), "files restored, as shown");
        Check(snapshotOnly.MayHaveFileEffects && OpenCodeContinuationPresentation.Describe(origin, snapshotOnly, true, basis) is { RestoredFileCount: 0, IsServerCommand: true },
            "a snapshot alone counts no restored file");
        Check(OpenCodeContinuationPresentation.Describe(origin, snapshotOnly with { MessageId = "msg_older" }, false, basis).StageView == OpenCodeContinuationStageView.Moved
            && OpenCodeContinuationPresentation.Describe(origin with { ObservedStage = new(null) }, snapshotOnly, false, basis).StageView == OpenCodeContinuationStageView.NotShown
            && OpenCodeContinuationPresentation.Describe(origin with { ObservedStage = null }, snapshotOnly, false, basis).StageView == OpenCodeContinuationStageView.Unknown
            && OpenCodeContinuationPresentation.Describe(origin, null, false, basis) is { HasStagedRevert: false, RestoredFileCount: 0, StageView: OpenCodeContinuationStageView.AsShown },
            "moved / not shown / unknown / gone are told apart");
        // Same fact as the redo prompt: it asks exactly when the send does, for the same stage.
        foreach (var stage in new[] { snapshotOnly, snapshotOnly with { FileCount = 1 }, snapshotOnly with { HasSnapshot = false } })
        {
            var redo = new OpenCodeRollbackPreviewPresentation(OpenCodeRollbackKind.Redo, null, stage.FileCount, false, false, true, basis);
            Check(OpenCodeRollbackConfirmationState.RequiresPrompt(redo) == SessionControlContinuationRule.RestoredFiles(stage)
                && SessionControlContinuationRule.SendsWithoutAsking(origin, stage, ComposerSubmissionKind.Prompt) != SessionControlContinuationRule.RestoredFiles(stage),
                "redo and send ask about files for the same stages");
        }

        // One completion, first wins; closing never confirms.
        var closed = new OpenCodeContinuationConfirmationState(new(false, 1, basis));
        Check(closed.Finish().Outcome == OpenCodeContinuationOutcome.Cancelled && !closed.TryConfirm()
            && closed.Result!.Outcome == OpenCodeContinuationOutcome.Cancelled, "Enter/Esc/取消 cancel, and a late 发送 cannot replace that");
        var sent = new OpenCodeContinuationConfirmationState(new(false, 1, basis));
        Check(sent.TryConfirm() && !sent.MarkStale() && sent.Finish() is { Outcome: OpenCodeContinuationOutcome.Confirmed } result
            && ReferenceEquals(result.Presentation, sent.Presentation), "only the explicit 发送 confirms, with the presentation it was shown with");
        var stale = new OpenCodeContinuationConfirmationState(new(false, 1, basis));
        Check(stale.MarkStale() && !stale.TryConfirm() && stale.Finish().Outcome == OpenCodeContinuationOutcome.Stale, "a question the page withdrew cannot be confirmed");
    }

    private static async Task SendAfterUndo()
    {
        // 1. An ordinary message after an undo that restored no file and is on screen: no question at all.
        foreach (var stage in new[] { "{\"messageID\":\"msg_newer\"}", SnapshotOnlyStage })
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); Shown(f, stage); CountQuestions(answer: OpenCodeContinuationOutcome.Confirmed);
            if (enhanced) await f.Page.File(); f.Page.Text("edited and sent");
            await f.Page.Send();
            Check(Questions == 0 && f.Http.Posts == 1 && f.LastMutationPath!.EndsWith("/prompt", StringComparison.Ordinal)
                && f.MutationPaths.All(path => !path.Contains("commit")) && f.Page.Receipts == 1 && f.Page.Messages.Count == 0,
                $"send after a files-free undo goes straight through (attachment={enhanced}, questions={Questions}, posts={f.Http.Posts}, notices={string.Join(";", f.Page.Messages)})");
            Check(f.Page.HistoryNeedsState && !f.Page.HistoryDeferred, "the committed undo is re-read before the transcript is shown again");
            using var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test");
            Check(lease.GetSessionControlProtection() is null && lease.GetComposerSubmissionProtection() is null,
                "an accepted prompt settles the continuation protection it started under");
        }

        // 2. The undo restored files: one confirmation, and 取消 leaves everything as it was.
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); Shown(f, FileStage); OpenCodeContinuationPresentation? asked = null;
            CountQuestions(p => asked = p);
            if (enhanced) await f.Page.File(); f.Page.Text("keep me");
            var attachments = f.Page.Snapshot.Attachments.Count; var boundary = f.Page.Boundary;
            await f.Page.Send();
            Check(Questions == 1 && asked is { RestoredFileCount: 2, IsServerCommand: false, HasStagedRevert: true, StageView: OpenCodeContinuationStageView.AsShown },
                $"an undo that restored files asks once, with the number of files (questions={Questions})");
            Check(f.Http.Posts == 0 && f.Stage == FileStage && f.Page.Snapshot.Text == "keep me" && f.Page.Snapshot.Attachments.Count == attachments
                && f.Page.Receipts == 0 && f.Page.Messages.Count == 0 && !f.Page.HistoryDeferred && f.Page.Boundary == boundary,
                "取消: nothing sent, the undo still pending, the draft and the hidden rows unchanged, no notice: " + string.Join(";", f.Page.Messages));
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
                Check(lease.CanControlSession(), "取消 leaves no protection behind: 恢复撤回 still works");
            CountQuestions(answer: OpenCodeContinuationOutcome.Confirmed);
            await f.Page.Send();
            Check(Questions == 1 && f.Http.Posts == 1 && f.Page.Receipts == 1, "发送 on the same question sends exactly once");
        }

        // 3. The undo is not the one that was on screen when Send was pressed: never committed silently.
        foreach (var change in new[] { "undone-further", "files-now", "during-read" })
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); Shown(f, "{\"messageID\":\"msg_newer\"}"); OpenCodeContinuationPresentation? asked = null;
            CountQuestions(p => asked = p);
            if (enhanced) await f.Page.File(); f.Page.Text("old assumption");
            if (change == "undone-further") f.Stage = "{\"messageID\":\"msg_older\"}"; // Another window undid one more message.
            if (change == "files-now") f.Stage = FileStage; // Another window redid and undid again, with files.
            if (change == "during-read")
            {
                var read = Program.Signal<bool>(); var respond = f.Http.Respond;
                f.Http.Respond = async (r, ct) => { if (r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/session/ses_test") await read.Task; return await respond(r, ct); };
                var send = f.Page.Send();
                f.Stage = "{\"messageID\":\"msg_older\"}"; read.SetResult(true); await send;
            }
            else await f.Page.Send();
            Check(Questions == 1 && f.Http.Posts == 0 && f.Page.Snapshot.Text == "old assumption", $"{change}/{enhanced}: asks instead of sending (questions={Questions}, posts={f.Http.Posts})");
            Check(change == "files-now" ? asked is { RestoredFileCount: 2, StageView: OpenCodeContinuationStageView.AsShown }
                : asked is { RestoredFileCount: 0, StageView: OpenCodeContinuationStageView.Moved }, $"{change}/{enhanced}: the question says what was found");
        }
        foreach (var state in new[] { "unreadable", "earlier-unknown" })
        foreach (var enhanced in new[] { false, true })
        {
            using var f = new NativeTests.Fixture(); Shown(f, "{\"messageID\":\"msg_newer\"}"); CountQuestions(answer: OpenCodeContinuationOutcome.Confirmed);
            if (enhanced) await f.Page.File(); f.Page.Text("refused");
            if (state == "unreadable")
            {
                var respond = f.Http.Respond;
                f.Http.Respond = (r, ct) => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/session/ses_test"
                    ? Task.FromResult(Program.Json("{}")) : respond(r, ct);
            }
            else using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
            {
                var earlier = Guid.NewGuid();
                lease.BeginSessionControl(earlier, SessionControlKind.StageRevert, new string('A', 64));
                lease.CompleteSessionControl(earlier, NotificationInteractionStatus.OutcomeUnknown);
            }
            await f.Page.Send();
            Check(Questions == 0 && f.Http.Posts == 0 && f.Page.Snapshot.Text == "refused" && f.Page.Receipts == 0,
                $"{state}/{enhanced}: refused as before, not asked and not sent (questions={Questions}, posts={f.Http.Posts})");
        }

        // 4. A server command after the same plain undo still asks, and this is why: a 204 does not say whether the
        //    undo was committed, so the session stays protected and refuses the next send.
        using (var f = new NativeTests.Fixture())
        {
            Shown(f, "{\"messageID\":\"msg_newer\"}"); OpenCodeContinuationPresentation? asked = null; CountQuestions(p => asked = p);
            await f.Page.ServerModelsCommand(); await f.Page.Send();
            Check(Questions == 1 && f.Http.Posts == 0 && asked is { IsServerCommand: true, RestoredFileCount: 0, StageView: OpenCodeContinuationStageView.AsShown }
                && new OpenCodeContinuationConfirmationState(asked).CommandNote is not null,
                $"a server command after a files-free undo asks, with its own line (questions={Questions}, posts={f.Http.Posts})");
            CountQuestions(answer: OpenCodeContinuationOutcome.Confirmed);
            f.Mutation = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await f.Page.Send();
            using (var lease = await OpenCodeInteractionGate.Shared.EnterAsync(f.Source.Identity, "ses_test"))
                Check(f.Http.Posts == 1 && f.LastMutationPath!.EndsWith("/command", StringComparison.Ordinal)
                    && lease.GetSessionControlProtection()?.Kind == SessionControlKind.RevertContinuation && !lease.CanControlSession(),
                    "the acknowledged command leaves the undo's outcome unknown and the session protected");
            f.Mutation = null; f.Page.Discard(); f.Page.Text("next message"); await f.Page.Send();
            Check(f.Http.Posts == 1 && f.Page.Receipts == 0, "which is the consequence the line names: the next message is not sent");
        }

        // 5. The undo the page was showing is gone (恢复撤回 in another window): an ordinary send, as before this change.
        using (var f = new NativeTests.Fixture())
        {
            Shown(f, "{\"messageID\":\"msg_newer\"}"); f.Stage = null; CountQuestions(); f.Page.Text("no undo left");
            await f.Page.Send();
            Check(Questions == 0 && f.Http.Posts == 1 && f.Page.Receipts == 1 && f.Page.HistoryNeedsState,
                "nothing is committed, nothing is asked, and the stale cut is re-read");
        }
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
            OpenCodeRollbackConfirmationDialog.Next = p => Task.FromResult(new OpenCodeRollbackConfirmationResult(OpenCodeRollbackOutcome.Cancelled, p, false));
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
