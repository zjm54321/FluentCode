using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;

internal static partial class Program
{
    private static async Task BuiltinFoundation()
    {
        await CatalogCapabilities();
        await NativeIntentCapabilities();
        await ClientTextCapabilities();
    }

    private static Task Open(Fixture f, bool refresh = false)
        => f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime, refresh);
    private static OpenCodeComposerCommandIdentity.Client NativeId(OpenCodeComposerClientActionKind kind) => new(kind);
    private static async Task<OpenCodeComposerClientIntent> Stage(Fixture f, OpenCodeComposerClientActionKind kind)
    {
        await Open(f);
        Check(f.Core.TrySelectCommand(f.Core.Snapshot.Token, f.Core.Snapshot.CatalogHandle!, NativeId(kind)), "owned native row stages " + kind);
        return f.Core.Snapshot.ClientIntent!;
    }
    private static object SourceLock(Fixture f) => f.Source.GetType().GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Source)!;
    private static void CommitRevocationWithoutCancellation(Fixture f)
    {
        lock (SourceLock(f))
        {
            f.Source.GetType().GetField("_revoked", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Source, true);
            f.Source.GetType().GetField("_createClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Source, null);
        }
        Check(f.Source.IsRevoked && !f.Source.RevocationToken.IsCancellationRequested, "real source committed-revoke/cancellation gap reproduced");
    }

    private static async Task CatalogCapabilities()
    {
        using (var f = new Fixture())
        {
            f.Handler.Send = (_, _) => Task.FromResult(Json("{\"data\":[{\"name\":\"editor\"},{\"name\":\"models\"},{\"name\":\"review\"}]}"));
            var token = f.Core.Snapshot.Token;
            await Open(f);
            var s = f.Core.Snapshot;
            Check(s.Token == token && s.Commands.Count == 11 && s.Commands.Select(r => r.Identity).Distinct().Count() == 11,
                "eight closed native kinds (incl. /btw) coexist with three typed server identities without draft-token mutation");
            Check(s.Commands.Count(r => r.Name == "editor") == 2 && s.Commands.Count(r => r.Name == "models") == 2,
                "visible collisions have no silent identity winner");
            await Open(f);
            var reopened = f.Core.Snapshot;
            Check(f.Handler.Gets == 1 && reopened.Token == token && !ReferenceEquals(s.CatalogHandle, reopened.CatalogHandle),
                "successful server rows cached; every opening has a unique handle even at same input token");
            f.Core.DismissCatalog(s.CatalogHandle!);
            Check(ReferenceEquals(f.Core.Snapshot.CatalogHandle, reopened.CatalogHandle), "old handle cannot dismiss replacement");
            Check(!f.Core.TrySelectCommand(token, s.CatalogHandle!, new OpenCodeComposerCommandIdentity.Server("editor")), "old handle cannot select replacement row");
            Check(!f.Core.TrySelectCommand(token, new OpenCodeComposerCatalogHandle(), NativeId(OpenCodeComposerClientActionKind.EditDraft)), "unissued handle cannot select");
            Check(!f.Core.TrySelectCommand(token, reopened.CatalogHandle!, NativeId((OpenCodeComposerClientActionKind)999)), "unknown enum cannot become a native or server action");
            Check(f.Core.TrySelectCommand(token, reopened.CatalogHandle!, new OpenCodeComposerCommandIdentity.Server("editor")), "exact typed server collision can be selected");
            Check(f.Core.Snapshot.SelectedCommand == "editor" && f.Core.Snapshot.ClientIntent is null && f.Handler.Posts == 0,
                "server collision selection stages only server command, zero POST");
        }
        using (var f = new Fixture())
        {
            var response = Signal<HttpResponseMessage>();
            f.Handler.Send = (_, _) => response.Task;
            f.Text("/"); var openingToken = f.Core.Snapshot.Token;
            var load = Open(f); var handle = f.Core.Snapshot.CatalogHandle!;
            Check(f.Core.Snapshot.Commands.Count == 8 && f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Loading,
                "native rows independent of pending server catalog");
            f.Text("/rev edited arguments");
            response.SetResult(Catalog()); await load;
            Check(ReferenceEquals(f.Core.Snapshot.CatalogHandle, handle) && f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Ready,
                "request owner and cache survive ordinary input revision change");
            Check(!f.Core.TrySelectCommand(openingToken, handle, new OpenCodeComposerCommandIdentity.Server("review")), "selection still requires exact post-flush input token");
            Check(f.Core.TrySelectCommand(f.Core.Snapshot.Token, handle, new OpenCodeComposerCommandIdentity.Server("review"))
                && f.Core.Snapshot.Text == "edited arguments", "controller proves current trigger removal, no caller replacement string");
            await Open(f);
            Check(f.Handler.Gets == 1 && f.Handler.Posts == 0, "edited request cached once; selection never sends");
        }
        using (var f = new Fixture())
        {
            var response = Signal<HttpResponseMessage>(); f.Handler.Send = (_, _) => response.Task;
            var load = Open(f); var s = f.Core.Snapshot;
            Check(f.Core.TrySelectCommand(s.Token, s.CatalogHandle!, NativeId(OpenCodeComposerClientActionKind.EditDraft)), "native selection works during GET");
            response.SetResult(Catalog()); await load;
            Check(f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed && f.Core.Snapshot.ClientIntent is not null,
                "dismissed completion neither reopens menu nor overwrites native intent");
            await Open(f);
            Check(f.Handler.Gets == 2, "dismissed request cannot populate cache behind a later opening");
        }
        using (var f = new Fixture())
        {
            f.Handler.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            await Open(f);
            Check(f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Failed && f.Core.Snapshot.Commands.Count == 8, "GET failure retains eight independent native rows");
            await Open(f);
            Check(f.Handler.Gets == 2, "failed server response is not cached as success");
            Check(f.Core.TrySelectCommand(f.Core.Snapshot.Token, f.Core.Snapshot.CatalogHandle!, NativeId(OpenCodeComposerClientActionKind.EditDraft)), "native row remains actionable after GET failure");
            Check(f.Handler.Posts == 0 && f.Core.Snapshot.SelectedCommand is null, "failure does not substitute native name into server command");
        }
        using (var f = new Fixture())
        {
            await Open(f); var token = f.Core.Snapshot.Token; var oldLife = f.CatalogLifetime;
            f.CatalogLifetime = f.Core.BeginCatalogLifetime();
            Check(f.Core.Snapshot.Token == token && f.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed, "actual view lifetime reset does not mutate draft/handoff token");
            await Open(f); var handle = f.Core.Snapshot.CatalogHandle;
            f.Core.EndCatalogLifetime(oldLife);
            await f.Core.LoadCatalogAsync(token, oldLife);
            Check(f.Handler.Gets == 2 && ReferenceEquals(f.Core.Snapshot.CatalogHandle, handle), "old view cannot end or load through replacement lifetime");
            f.Switch("ses_other"); f.Switch("ses_test"); await Open(f);
            Check(f.Handler.Gets == 3, "binding ABA invalidates successful catalog cache");
            f.Core.DiscardDraft(f.Core.Snapshot.Token); await Open(f);
            Check(f.Handler.Gets == 4, "discard invalidates successful catalog cache");
            f.Core.EndCatalogLifetime(f.CatalogLifetime); await Open(f);
            Check(f.Handler.Gets == 4 && f.Core.Snapshot.CatalogHandle is null, "ended view cannot reopen without a new explicit lifetime");
        }
        using (var f = new Fixture())
        {
            var observed = new ManualResetEventSlim();
            Task load;
            lock (SourceLock(f))
            {
                f.OwnershipObserved = observed.Set;
                var old = f.Core.Snapshot.Token;
                observed.Reset();
                load = Task.Run(() => f.Core.LoadCatalogAsync(old, f.CatalogLifetime));
                Check(observed.Wait(TimeSpan.FromSeconds(5)), "opening passed preliminary ownership read before blocked source boundary");
                f.Text("later input"); // Would deadlock if controller lock acquired Source.IsRevoked.
            }
            await load.WaitAsync(TimeSpan.FromSeconds(5));
            Check(f.Handler.Gets == 0 && f.Core.Snapshot.CatalogHandle is null, "atomic source-locked start rejects superseded opening input");
            observed.Dispose(); f.OwnershipObserved = null;
        }
        foreach (var cached in new[] { false, true })
        {
            using var f = new Fixture();
            if (cached) await Open(f);
            var before = f.Core.Snapshot; var gets = f.Handler.Gets;
            CommitRevocationWithoutCancellation(f);
            await Open(f);
            Check(f.Handler.Gets == gets && ReferenceEquals(f.Core.Snapshot.CatalogHandle, before.CatalogHandle), "committed revocation rejects both fresh and cached opening atomically");
        }
        using (var f = new Fixture())
        {
            var response = Signal<HttpResponseMessage>(); f.Handler.Send = (_, _) => response.Task;
            var load = Open(f); CommitRevocationWithoutCancellation(f);
            response.SetResult(Catalog()); await load;
            Check(f.Core.Snapshot.CatalogState != OpenCodeComposerCatalogState.Ready && f.Core.Snapshot.Commands.All(r => r.Identity is OpenCodeComposerCommandIdentity.Client),
                "committed revocation blocks server result/cache installation despite uncancelled token");
        }
        using (var f = new Fixture())
        {
            var events = 0;
            f.Core.SnapshotChanged += (_, _) =>
            {
                Check(Task.Run(() => f.Source.TryExecute(default, () => _ = f.Core.Snapshot)).Wait(TimeSpan.FromSeconds(5)), "catalog publication outside source/controller locks");
                events++;
            };
            await Open(f); await Open(f);
            Check(events >= 3 && f.Handler.Gets == 1, "start/completion/cache-hit publish independently without extra GET");
        }
        Console.WriteLine("PASS builtin group 1: typed collisions, exact start/selection, unique handles, owned cache, lifetime/revocation, no selection HTTP.");
    }

    private static async Task NativeIntentCapabilities()
    {
        using (var f = new Fixture())
        {
            await f.Command("body"); await f.File(); f.Text(" \t/ed body\nsecond line");
            var file = f.Core.Snapshot.Attachments.Single();
            var intent = await Stage(f, OpenCodeComposerClientActionKind.EditDraft);
            var staged = f.Core.Snapshot;
            Check(staged.Text == "body\nsecond line" && staged.SelectedCommand == "review" && staged.Attachments.Single() == file && staged.IsBoundDraft,
                "native selection removes only proven trigger; server command/files/origin and remaining text survive");
            var forwarded = false; f.Core.SubmitRequested += (_, _) => forwarded = true;
            f.Core.RequestSubmit(staged.Token);
            Check(!staged.CanSubmit && staged.CanInvokeClientAction && !forwarded
                && await f.SubmitAsync(staged.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0,
                "pending native intent cannot fall into server submission even with underlying enhanced draft");
            Check(f.Core.TryTakeClientAction(default, intent.Id) is null && f.Core.TryTakeClientAction(staged.Token, Guid.NewGuid()) is null,
                "native invocation requires exact input and intent owner");
            var ticket = f.Core.TryTakeClientAction(staged.Token, intent.Id)!;
            Check(ticket is not null && ticket.Kind == OpenCodeComposerClientActionKind.EditDraft && ticket.Binding == f.Binding
                && ticket.Token == f.Core.Snapshot.Token && ticket.Token != staged.Token && ticket.Owner != Guid.Empty,
                "ticket captures full binding/action owner and POST-consumption draft token");
            Check(f.Core.Snapshot.ClientIntent is null && f.Core.TryTakeClientAction(f.Core.Snapshot.Token, intent.Id) is null,
                "native intent consumed exactly once without restaging or invoking");
            f.Core.RequestSubmit(ticket!.Token);
            Check(!forwarded && await f.SubmitAsync(ticket.Token, () => true) is OpenCodeComposerSubmitOutcome.NotStarted && f.Handler.Posts == 0,
                "outstanding native ticket also blocks accidental enhanced submission");
            var forged = new OpenCodeComposerClientActionTicket(ticket.Binding, ticket.Token, ticket.Kind, ticket.Owner, ticket.Lifetime);
            f.Core.CompleteClientAction(forged);
            Check(!f.Core.OwnsClientAction(forged) && f.Core.TryApplyClientText(forged, "forged") is null && f.Core.OwnsClientAction(ticket),
                "copied ticket fields do not mint action authority or complete real owner");
            Check(f.Core.TryApplyClientText(ticket, "") is { Text: "" } && f.Core.Snapshot.SelectedCommand == "review"
                && f.Core.Snapshot.Attachments.Single() == file && f.Core.Snapshot.IsBoundDraft,
                "editor may explicitly clear text but preserves command/files/origin");
            Check(f.Core.TryApplyClientText(ticket, "replay") is null && f.Handler.Posts == 0, "editor Apply single-use and zero HTTP");
        }
        foreach (var kind in Enum.GetValues<OpenCodeComposerClientActionKind>())
        {
            using var f = new Fixture();
            var intent = await Stage(f, kind); var token = f.Core.Snapshot.Token;
            var ticket = f.Core.TryTakeClientAction(token, intent.Id)!;
            Check(ticket is not null && f.Core.Snapshot.Text == "" && f.Handler.Posts == 0, "all eight kinds stage then explicitly take without mutation transport: " + kind);
            if (kind is not (OpenCodeComposerClientActionKind.EditDraft or OpenCodeComposerClientActionKind.Undo))
                Check(f.Core.TryApplyClientText(ticket!, "unauthorized") is null && f.Core.Snapshot.Text == "", "kind cannot acquire text-write authority: " + kind);
            f.Core.CompleteClientAction(ticket!);
            Check(!f.Core.OwnsClientAction(ticket!), "explicit completion closes only local action owner: " + kind);
        }
        foreach (var kind in new[] { OpenCodeComposerClientActionKind.NewSession, OpenCodeComposerClientActionKind.ChooseSession,
            OpenCodeComposerClientActionKind.ChooseAgent, OpenCodeComposerClientActionKind.Undo })
        {
            using var f = new Fixture(); f.Text("extra arguments"); await Open(f);
            Check(!f.Core.Snapshot.ClientActions.Single(a => a.Kind == kind).CanStage
                && !f.Core.TrySelectCommand(f.Core.Snapshot.Token, f.Core.Snapshot.CatalogHandle!, NativeId(kind))
                && f.Core.Snapshot.Text == "extra arguments", "conditional no-extra-arguments/empty eligibility: " + kind);
            f.Text("");
            Check(f.Core.Snapshot.ClientActions.Single(a => a.Kind == kind).CanStage && f.Handler.Gets == 1,
                "availability recomputed from live draft, not cached identity: " + kind);
        }
        foreach (var kind in new[] { OpenCodeComposerClientActionKind.ChooseModel, OpenCodeComposerClientActionKind.Redo })
        {
            using var f = new Fixture(); await f.Command("preserve"); await f.File();
            var before = f.Core.Snapshot; var intent = await Stage(f, kind);
            var ticket = f.Core.TryTakeClientAction(f.Core.Snapshot.Token, intent.Id)!;
            Check(f.Core.Snapshot.Text == before.Text && f.Core.Snapshot.SelectedCommand == before.SelectedCommand
                && f.Core.Snapshot.Attachments.SequenceEqual(before.Attachments) && f.Core.TryApplyClientText(ticket, "no") is null,
                "models/redo preserve underlying enhanced draft and have no text-write seam: " + kind);
        }
        using (var f = new Fixture())
        {
            f.Text("ordinary unbound body"); var intent = await Stage(f, OpenCodeComposerClientActionKind.EditDraft);
            Check(!f.Core.Snapshot.IsBoundDraft, "native selection does not convert plain text into a command-bound draft");
            var stale = f.Core.Snapshot.Token; f.Text("later body");
            f.Core.DismissClientIntent(stale, intent.Id);
            Check(f.Core.Snapshot.ClientIntent == intent, "stale intent dismissal cannot borrow current editor authority");
            f.Core.DismissClientIntent(f.Core.Snapshot.Token, intent.Id);
            Check(f.Core.Snapshot.ClientIntent is null && f.Core.Snapshot.Text == "later body" && f.Handler.Posts == 0, "explicit intent dismissal preserves underlying draft");
        }
        Console.WriteLine("PASS builtin group 2: seven typed native intents, underlying draft preservation, conditional eligibility, opaque single-use tickets, transport fail-closed.");
    }

    private static async Task ClientTextCapabilities()
    {
        using (var f = new Fixture())
        {
            var intent = await Stage(f, OpenCodeComposerClientActionKind.Undo);
            var beforeTake = f.Core.Snapshot.Token;
            var ticket = f.Core.TryTakeClientAction(beforeTake, intent.Id)!;
            var ack = f.Core.TryApplyClientText(ticket, "restored user text");
            Check(ack is { Text: "restored user text" } && ack.Value.Token == f.Core.Snapshot.Token && ack.Value.Token != ticket.Token
                && ticket.Token != beforeTake && f.Core.Snapshot.IsBoundDraft && f.Handler.Posts == 0, "undo exact-empty restoration uses consumed ticket, updates ACK, binds originating session, never HTTP");
            Check(f.Core.TryApplyClientText(ticket, "again") is null, "undo restoration is single-use");
            f.Switch("ses_other");
            Check(!f.Core.Snapshot.IsActive && f.Core.Snapshot.Text == "restored user text", "restored text remains bound to originating session");
        }
        foreach (var kind in new[] { OpenCodeComposerClientActionKind.EditDraft, OpenCodeComposerClientActionKind.Undo })
        foreach (var change in new[] { "edit-back", "files-back", "discard", "session-ABA", "source-generation", "view-ABA", "revoke", "complete" })
        {
            using var f = new Fixture();
            var intent = await Stage(f, kind); var ticket = f.Core.TryTakeClientAction(f.Core.Snapshot.Token, intent.Id)!;
            switch (change)
            {
                case "edit-back": f.Text("later"); f.Text(""); break;
                case "files-back": await f.File(); f.Core.RemoveAttachment(f.Core.Snapshot.Token, f.Core.Snapshot.Attachments.Single().Id); break;
                case "discard": f.Core.DiscardDraft(f.Core.Snapshot.Token); break;
                case "session-ABA": f.Switch("ses_other"); f.Switch("ses_test"); break;
                case "source-generation": f.Binding = f.Binding with { SourceGeneration = f.Binding.SourceGeneration + 1 }; f.Core.Bind(f.Binding); break;
                case "view-ABA": f.Core.EndCatalogLifetime(f.CatalogLifetime); f.CatalogLifetime = f.Core.BeginCatalogLifetime(); break;
                case "revoke": CommitRevocationWithoutCancellation(f); break;
                case "complete": f.Core.CompleteClientAction(ticket); break;
            }
            Check(f.Core.TryApplyClientText(ticket, "stale overwrite") is null && f.Core.Snapshot.Text == "" && f.Handler.Posts == 0,
                kind + " exact text ticket cannot revive after " + change);
        }
        using (var f = new Fixture())
        {
            var intent = await Stage(f, OpenCodeComposerClientActionKind.EditDraft);
            CommitRevocationWithoutCancellation(f);
            Check(f.Core.TryTakeClientAction(f.Core.Snapshot.Token, intent.Id) is null && f.Core.Snapshot.ClientIntent == intent,
                "committed source revocation prevents native consumption even before token cancellation");
        }
        using (var f = new Fixture())
        {
            var first = await Stage(f, OpenCodeComposerClientActionKind.EditDraft);
            var old = f.Core.TryTakeClientAction(f.Core.Snapshot.Token, first.Id)!;
            f.Core.CompleteClientAction(old);
            var second = await Stage(f, OpenCodeComposerClientActionKind.EditDraft);
            var current = f.Core.TryTakeClientAction(f.Core.Snapshot.Token, second.Id)!;
            f.Core.CompleteClientAction(old);
            Check(!f.Core.OwnsClientAction(old) && f.Core.OwnsClientAction(current) && f.Core.TryApplyClientText(old, "old") is null,
                "old completion/apply cannot borrow replacement action owner");
            var reenter = true;
            f.Core.SnapshotChanged += (_, _) => { if (reenter) { reenter = false; f.Text("newer observed input"); } };
            var ack = f.Core.TryApplyClientText(current, "accepted buffer");
            Check(ack is { Text: "accepted buffer" } && f.Core.Snapshot.Text == "newer observed input" && ack.Value.Token != f.Core.Snapshot.Token,
                "client Apply ACK is its accepted token/text, not a later reentrant snapshot");
        }
        using (var f = new Fixture())
        {
            await f.File(); f.Text("accepted");
            var result = await f.SubmitAsync(f.Core.Snapshot.Token, () => true, acceptedDraftHandoff: (token, finalize, _) =>
            {
                f.CatalogLifetime = f.Core.BeginCatalogLifetime();
                Check(f.Core.Snapshot.Token == token && finalize() == OpenCodeComposerDraftFinalization.Cleared,
                    "catalog lifetime-only change cannot manufacture later draft input or break accepted handoff");
                return Task.CompletedTask;
            });
            Check(result is OpenCodeComposerSubmitOutcome.PromptAccepted && f.Handler.Posts == 1 && !f.Core.Snapshot.HasDraft,
                "existing successful receipt/finalization remains one synthetic POST with owned clearing");
        }
        Console.WriteLine("PASS builtin group 3: editor/undo exact-token text seam, kind/owner checks, source/draft/view ABA, atomic ACK and accepted-handoff compatibility.");
    }
}
