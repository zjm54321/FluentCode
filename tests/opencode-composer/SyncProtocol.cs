using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static partial class Program
{
    private static async Task LegacyFinalization()
    {
        await LegacyNormalClear();
        await LegacyCommittedRevocation();
        await LegacyOwnershipAndClosure();
    }

    private static async Task LegacyNormalClear()
    {
        foreach (var bound in new[] { false, true })
        {
            using var f = new Fixture();
            if (bound) { await f.Command("legacy text"); f.Core.RemoveCommand(f.Core.Snapshot.Token); }
            else f.Text("legacy text");
            Check(f.Core.Snapshot.IsBoundDraft == bound, "legacy fixture distinguishes bound text-only from unbound text");
            var submitted = f.Core.Snapshot.Token;
            OpenCodeInboxUser receipt;
            using (var lease = await f.Gate.EnterAsync(f.Source.Identity, "ses_test"))
                receipt = await f.Client.SendPromptAsync("ses_test", "legacy text", CancellationToken.None);
            var eventsOutsideLocks = false;
            f.Core.SnapshotChanged += (_, e) =>
            {
                if (e.Snapshot.HasDraft) return;
                eventsOutsideLocks = Task.Run(() => f.Source.TryExecute(CancellationToken.None, () => _ = f.Core.Snapshot))
                    .Wait(TimeSpan.FromSeconds(5));
            };
            await f.Core.FinalizeAcceptedLegacyDraftAsync(submitted, f.Binding, () => true, CancellationToken.None,
                async (token, finalize, _) =>
                {
                    Check(token == submitted && !f.Core.Snapshot.IsBusy && await f.Protection().WaitAsync(TimeSpan.FromSeconds(5)) is null,
                        "legacy handoff starts after caller lease release, without enhanced submission owner or protection");
                    Check(finalize() == OpenCodeComposerDraftFinalization.Cleared && finalize() == OpenCodeComposerDraftFinalization.AlreadyFinalized,
                        "legacy plain/bound-text uses existing single-use clear capability");
                });
            Check(receipt.Id == "msg_real" && !f.Core.Snapshot.HasDraft && eventsOutsideLocks && f.Handler.Posts == 1 && await f.Protection() is null,
                "legacy receipt remains accepted; clear publishes outside source/controller locks without extra HTTP/protection");
        }
        using (var f = new Fixture())
        {
            await f.File(); var token = f.Core.Snapshot.Token;
            await f.Core.FinalizeAcceptedLegacyDraftAsync(token, f.Binding, () => true, CancellationToken.None, (_, finalize, _) =>
            {
                Check(finalize() == OpenCodeComposerDraftFinalization.OriginLost, "unchanged enhanced draft cannot misuse legacy clearing");
                return Task.CompletedTask;
            });
            Check(f.Core.Snapshot.Attachments.Count == 1 && f.Handler.Posts == 0, "legacy misuse leaves enhanced files and transport untouched");
            var threw = false;
            try { await f.Core.FinalizeAcceptedLegacyDraftAsync(token, f.Binding, () => true, CancellationToken.None, null!); }
            catch (ArgumentNullException) { threw = true; }
            Check(threw, "legacy wrapper requires a non-null handoff");
        }
    }

    private static async Task LegacyCommittedRevocation()
    {
        using var f = new Fixture(); f.Text("accepted before revoke"); var submitted = f.Core.Snapshot.Token;
        var receipt = await f.Client.SendPromptAsync("ses_test", "accepted before revoke", CancellationToken.None);
        // Deterministic test-only state injection: reproduce Revoke's committed locked prefix on the
        // REAL source fields, without running its later CancelAsync. No source method is substituted.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = f.Source.GetType();
        var sourceLock = type.GetField("_sync", flags)!.GetValue(f.Source)!;
        lock (sourceLock)
        {
            type.GetField("_revoked", flags)!.SetValue(f.Source, true);
            type.GetField("_createClient", flags)!.SetValue(f.Source, null);
        }
        Check(f.Source.IsRevoked && !f.Source.RevocationToken.IsCancellationRequested,
            "actual committed revoked flag is set while actual source cancellation remains uncancelled");
        await f.Core.FinalizeAcceptedLegacyDraftAsync(submitted, f.Binding, () => true, CancellationToken.None, (_, finalize, ct) =>
        {
            Check(!ct.IsCancellationRequested && finalize() == OpenCodeComposerDraftFinalization.OriginLost,
                "real Source.TryExecute rejects clear in committed-revocation/cancellation gap");
            return Task.CompletedTask;
        });
        Check(receipt.Id == "msg_real" && f.Core.Snapshot.Text == "accepted before revoke" && f.Handler.Posts == 1 && await f.Protection() is null,
            "revoked legacy clear returns normally, retains input and does not manufacture send failure/protection/HTTP");
    }

    private static async Task LegacyOwnershipAndClosure()
    {
        using (var f = new Fixture())
        {
            f.Text("submitted"); var token = f.Core.Snapshot.Token; await f.File();
            await f.Core.FinalizeAcceptedLegacyDraftAsync(token, f.Binding, () => true, CancellationToken.None, (_, finalize, _) =>
            {
                Check(finalize() == OpenCodeComposerDraftFinalization.Retained, "later enhancement reaches Retained rather than losing files");
                return Task.CompletedTask;
            });
        }
        foreach (var change in new[] { "edit", "cancel", "ABA", "owner", "failure", "busy" })
        {
            using var f = new Fixture(); f.Text("submitted"); var token = f.Core.Snapshot.Token; var binding = f.Binding;
            using var cancellation = new CancellationTokenSource(); var localOwner = true;
            var entered = Signal<Func<OpenCodeComposerDraftFinalization>>(); var release = Signal<bool>();
            var pending = f.Core.FinalizeAcceptedLegacyDraftAsync(token, binding, () => localOwner, cancellation.Token,
                async (_, finalize, _) => { entered.SetResult(finalize); await release.Task; });
            var finalize = await entered.Task;
            if (change == "edit")
            {
                f.Core.TryApplyEditorInput(token, "later", false);
                Check(finalize() == OpenCodeComposerDraftFinalization.Retained, "legacy later input reaches shared finalizer Retained");
            }
            else if (change == "cancel") cancellation.Cancel();
            else if (change == "ABA") { f.Switch("ses_other"); f.Switch("ses_test"); }
            else if (change == "owner")
            {
                localOwner = false;
                Check(finalize() == OpenCodeComposerDraftFinalization.OriginLost, "legacy replaced local owner prevents clear");
            }
            else if (change == "busy")
            {
                var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
                Check(finalize() == OpenCodeComposerDraftFinalization.OriginLost, "legacy clear cannot borrow controller picker/submission busy ownership");
                await f.Core.AddFilesAsync(ticket, Array.Empty<OpenCodeComposerFileSource>());
            }
            if (change == "failure") release.SetException(new InvalidOperationException("synthetic local legacy handoff failure"));
            else if (change is not ("cancel" or "ABA")) release.SetResult(true);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(finalize() == OpenCodeComposerDraftFinalization.AlreadyFinalized && f.Core.Snapshot.HasDraft && f.Handler.Posts == 0 && await f.Protection() is null,
                "legacy " + change + " closes callback without clearing/retrying/reprotecting or surfacing local failure");
            release.TrySetResult(true);
        }
        using (var f = new Fixture())
        {
            f.Text("origin"); var token = f.Core.Snapshot.Token; var binding = f.Binding; var called = false;
            f.Switch("ses_other"); f.Switch("ses_test");
            await f.Core.FinalizeAcceptedLegacyDraftAsync(token, f.Binding, () => true, CancellationToken.None,
                (_, _, _) => { called = true; return Task.CompletedTask; });
            await f.Core.FinalizeAcceptedLegacyDraftAsync(f.Core.Snapshot.Token, binding, () => true, CancellationToken.None,
                (_, _, _) => { called = true; return Task.CompletedTask; });
            Check(!called && f.Core.Snapshot.Text == "origin", "admission refuses stale supplied scope and stale supplied full binding without substitution");
        }
    }

    private static async Task SyncProtocol()
    {
        await AcknowledgedInput();
        await AcceptedDraftCutover();
        await HandoffLifetime();
    }

    private static async Task AcknowledgedInput()
    {
        using (var f = new Fixture())
        {
            f.Text("original");
            var basis = f.Core.Snapshot.Token;
            var ack = f.Core.TryApplyEditorInput(basis, "original", false);
            Check(ack is { Text: "original" } && ack.Value.Token != basis, "same-string observed edit cycle gets a new acknowledged token");
            Check(f.Core.TryApplyEditorInput(basis, "stale", false) is null && f.Core.Snapshot.Text == "original", "stale acknowledgment never rebases onto current token");

            OpenCodeComposerEditorAck? reentrant = null;
            var observe = true;
            f.Core.SnapshotChanged += (_, _) =>
            {
                if (!observe) return;
                observe = false;
                reentrant = f.Core.TryApplyEditorInput(f.Core.Snapshot.Token, "newer", false);
            };
            var first = f.Core.TryApplyEditorInput(ack!.Value.Token, "first", false);
            Check(first is { Text: "first" } && reentrant is { Text: "newer" }
                && first.Value.Token != reentrant.Value.Token, "acknowledgment captured atomically before reentrant publish edits");
            Check(f.Core.Snapshot.Text == "newer" && f.Core.Snapshot.Token == reentrant!.Value.Token, "ack return does not replace newer input with stale snapshot text");
        }
        using (var f = new Fixture())
        {
            f.Text("plain"); var basis = f.Core.Snapshot.Token;
            f.Switch("ses_other");
            Check(f.Core.TryApplyEditorInput(basis, "carry", false) is null, "scope-only plaintext carry requires explicit request");
            var ack = f.Core.TryApplyEditorInput(basis, "carry", true);
            Check(ack is { Text: "carry" } && ack.Value.Token == f.Core.Snapshot.Token, "controller admits only unchanged unbound draft/edit across scope");
            Check(f.Core.TryApplyEditorInput(basis, "old", true) is null, "successful input invalidates repeated old unbound carry");
            var beforeDiscard = f.Core.Snapshot.Token; f.Core.DiscardDraft(beforeDiscard);
            Check(f.Core.TryApplyEditorInput(beforeDiscard, "resurrect", true) is null && !f.Core.Snapshot.HasDraft, "discard changes draft identity and forbids carry");
        }
        using (var f = new Fixture())
        {
            f.Text("unbound"); var unbound = f.Core.Snapshot.Token; await f.File();
            Check(f.Core.TryApplyEditorInput(unbound, "carry through enhancement", true) is null, "enhancement invalidates plaintext carry");
            var bound = f.Core.Snapshot.Token; f.Switch("ses_other");
            Check(f.Core.TryApplyEditorInput(bound, "bound edit", true) is null, "inactive bound input cannot carry");
            f.Switch("ses_test");
            Check(f.Core.TryApplyEditorInput(bound, "ABA edit", true) is null && f.Core.Snapshot.Attachments.Count == 1, "bound A-B-A never rebases stale input");
        }
        using (var f = new Fixture())
        {
            f.Text("owned"); var basis = f.Core.Snapshot.Token; f.Core.Bind(null);
            Check(f.Core.TryApplyEditorInput(basis, "inactive", true) is null, "unbound carry still requires current active owned binding");
            f.Core.Bind(f.Binding); f.Source.Revoke();
            Check(f.Core.TryApplyEditorInput(f.Core.Snapshot.Token, "revoked", true) is null, "revocation rejects editor acknowledgment");
        }
        using (var f = new Fixture())
        {
            f.Text("read owner"); var ticket = f.Core.BeginPick(f.Core.Snapshot.Token)!;
            var ack = f.Core.TryApplyEditorInput(f.Core.Snapshot.Token, "read owner", false);
            await f.Core.AddFilesAsync(ticket, new[] { FileSource("ignored.txt", 1) });
            Check(ack is not null && f.Core.Snapshot.Attachments.Count == 0, "observed same-string input invalidates earlier picker ticket");
        }
    }

    private static async Task AcceptedDraftCutover()
    {
        using (var f = new Fixture())
        {
            await f.File(); f.Text("submitted"); var submitted = f.Core.Snapshot.Token;
            var entered = Signal<Func<OpenCodeComposerDraftFinalization>>(); var release = Signal<bool>();
            var pending = f.SubmitAsync(submitted, () => true, acceptedDraftHandoff: async (token, finalize, lifetime) =>
            {
                Check(token == submitted && !lifetime.IsCancellationRequested, "handoff captures exact submitted token and live lifetime");
                entered.SetResult(finalize); await release.Task;
            });
            var finalize = await entered.Task;
            Check(f.Core.Snapshot.IsBusy && f.Core.Snapshot.Text == "submitted" && f.Core.Snapshot.Attachments.Count == 1 && !pending.IsCompleted,
                "settled receipt does not clear draft or release controller owner before handoff");
            Check(await f.Protection().WaitAsync(TimeSpan.FromSeconds(5)) is null, "lease released and exact protection settled before awaiting handoff");
            Check(await f.SubmitAsync(submitted, () => true) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 1,
                "retained controller owner blocks a second submit while UI handoff waits");
            Check(finalize() == OpenCodeComposerDraftFinalization.Cleared && !f.Core.Snapshot.HasDraft && f.Core.Snapshot.IsBusy,
                "single synchronous finalizer clears but retains submission lifetime until handoff finishes");
            Check(finalize() == OpenCodeComposerDraftFinalization.AlreadyFinalized, "duplicate finalizer is inert");
            Check(f.Core.TryApplyEditorInput(submitted, "stale editor", true) is null, "accepted clear invalidates even unbound-carry replay");
            var newAck = f.Core.TryApplyEditorInput(f.Core.Snapshot.Token, "after cutover", false);
            release.SetResult(true);
            Check(await pending is OpenCodeComposerSubmitOutcome.PromptAccepted && newAck is not null && f.Core.Snapshot.Text == "after cutover",
                "new input after UI cutover survives completion and real receipt remains accepted");
            Check(!f.Core.Snapshot.IsBusy && finalize() == OpenCodeComposerDraftFinalization.AlreadyFinalized && f.Handler.Posts == 1,
                "completed handoff closes capability and does not redo HTTP");
        }
        foreach (var sameStringCycle in new[] { false, true })
        {
            using var f = new Fixture(); await f.Command("arguments"); await f.File();
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var submitted = f.Core.Snapshot.Token;
            var result = await f.SubmitAsync(submitted, () => true, acceptedDraftHandoff: (token, finalize, _) =>
            {
                var ack = f.Core.TryApplyEditorInput(token, sameStringCycle ? "arguments" : "later text", false);
                Check(ack is not null && finalize() == OpenCodeComposerDraftFinalization.Retained,
                    "pre-cutover observed input retains accepted command draft, same-string=" + sameStringCycle);
                return Task.CompletedTask;
            });
            Check(result is OpenCodeComposerSubmitOutcome.CommandAcknowledged && f.Core.Snapshot.SelectedCommand == "review" && f.Core.Snapshot.Attachments.Count == 1,
                "accepted edit retains entire enhanced draft without reconstructing files");
            Check(await f.Protection() is null && f.Handler.Posts == 1, "retained accepted draft does not re-protect or repeat command");
        }
        using (var f = new Fixture())
        {
            await f.Command(); var eventOutsideLocks = false;
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            f.Core.SnapshotChanged += (_, e) =>
            {
                if (e.Snapshot.HasDraft) return;
                // A worker can enter the source/controller while the event is running. Waiting here
                // would time out if finalization published under either lock.
                eventOutsideLocks = Task.Run(() => f.Source.TryExecute(CancellationToken.None, () => _ = f.Core.Snapshot))
                    .Wait(TimeSpan.FromSeconds(5));
            };
            Check(await f.SubmitAsync(f.Core.Snapshot.Token, () => true) is OpenCodeComposerSubmitOutcome.CommandAcknowledged
                && !f.Core.Snapshot.HasDraft && eventOutsideLocks, "optional-null headless path finalizes after lease release and publishes outside locks");
        }
    }

    private static async Task HandoffLifetime()
    {
        foreach (var failure in new[] { "throw", "return-without-finalize", "cancel", "scope", "revoke", "local-owner" })
        {
            using var f = new Fixture(); await f.File(); f.Text("retain");
            using var cancellation = new CancellationTokenSource();
            Func<OpenCodeComposerDraftFinalization>? saved = null;
            var entered = Signal<bool>(); var release = Signal<bool>(); var localOwner = true;
            var pending = f.SubmitAsync(f.Core.Snapshot.Token, () => localOwner, cancellation.Token,
                async (_, finalize, _) => { saved = finalize; entered.SetResult(true); await release.Task; });
            await entered.Task;
            Check(await f.Protection().WaitAsync(TimeSpan.FromSeconds(5)) is null, "successful receipt already settled before " + failure);
            if (failure == "cancel") cancellation.Cancel();
            else if (failure == "scope") f.Switch("ses_other");
            else if (failure == "revoke") f.Source.Revoke();
            else if (failure == "local-owner")
            {
                localOwner = false;
                Check(saved!() == OpenCodeComposerDraftFinalization.OriginLost, "finalizer rechecks current local owner independently of submitted token");
            }
            if (failure == "throw") release.SetException(new InvalidOperationException("synthetic handoff failure"));
            else if (failure is "return-without-finalize" or "local-owner") release.SetResult(true);
            var outcome = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Check(outcome is OpenCodeComposerSubmitOutcome.PromptAccepted or OpenCodeComposerSubmitOutcome.OriginLost,
                "handoff failure/lifetime loss cannot reclassify settled success as Unknown: " + failure);
            Check(saved!() == OpenCodeComposerDraftFinalization.AlreadyFinalized && f.Core.Snapshot.Text == "retain" && f.Core.Snapshot.Attachments.Count == 1,
                "abandoned/finished finalizer cannot clear retained draft: " + failure);
            Check(!f.Core.Snapshot.IsBusy && await f.Protection() is null && f.Handler.Posts == 1,
                "handoff cleanup releases only local owner, never re-protects/retries: " + failure);
            release.TrySetException(new InvalidOperationException("late UI callback failure"));
        }
        using (var f = new Fixture())
        {
            await f.File(); Func<OpenCodeComposerDraftFinalization>? saved = null;
            var outcome = await f.SubmitAsync(f.Core.Snapshot.Token, () => true, acceptedDraftHandoff: (_, finalize, _) =>
            {
                saved = finalize;
                Check(finalize() == OpenCodeComposerDraftFinalization.Cleared, "clear may complete before UI setter throws");
                throw new InvalidOperationException("synthetic setter failure");
            });
            Check(outcome is OpenCodeComposerSubmitOutcome.PromptAccepted && !f.Core.Snapshot.HasDraft && await f.Protection() is null,
                "post-finalization UI exception cannot undo settlement or classify Unknown");
            Check(saved!() == OpenCodeComposerDraftFinalization.AlreadyFinalized, "throwing handoff closes consumed capability");
        }
        using (var f = new Fixture())
        {
            await f.File(); using var cancellation = new CancellationTokenSource();
            var entered = Signal<Func<OpenCodeComposerDraftFinalization>>(); var abandoned = Signal<bool>();
            var first = f.SubmitAsync(f.Core.Snapshot.Token, () => true, cancellation.Token,
                async (_, finalize, _) => { entered.SetResult(finalize); await abandoned.Task; });
            var stale = await entered.Task; cancellation.Cancel(); await first;
            var second = await f.SubmitAsync(f.Core.Snapshot.Token, () => true, acceptedDraftHandoff: (_, finalize, _) =>
            {
                Check(stale() == OpenCodeComposerDraftFinalization.AlreadyFinalized && f.Core.Snapshot.HasDraft,
                    "abandoned callback cannot borrow newer operation ownership even with unchanged draft token");
                Check(finalize() == OpenCodeComposerDraftFinalization.Cleared, "new accepted operation owns its own finalizer");
                return Task.CompletedTask;
            });
            Check(second is OpenCodeComposerSubmitOutcome.PromptAccepted && f.Handler.Posts == 2 && await f.Protection() is null,
                "only explicit new submit causes second HTTP, stale finalizer performs none");
            abandoned.SetResult(true);
        }
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.InternalServerError })
        {
            using var f = new Fixture(); await f.File(); var called = false;
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(status));
            var result = await f.SubmitAsync(f.Core.Snapshot.Token, () => true,
                acceptedDraftHandoff: (_, _, _) => { called = true; return Task.CompletedTask; });
            Check(!called && f.Core.Snapshot.Attachments.Count == 1
                && (status == HttpStatusCode.BadRequest ? result is OpenCodeComposerSubmitOutcome.Rejected : result is OpenCodeComposerSubmitOutcome.Unknown),
                "rejected/unknown paths never invoke successful clearing handoff: " + status);
        }
    }
}
