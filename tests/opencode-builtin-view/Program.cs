using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

// Offline, production-linked UI-policy evidence for the builtin composer UI. REAL: controller/contracts, EditorSync,
// InputPolicy, CommandMenuSession (the exact object the WinUI view delegates its menu/keyboard/click decisions to) and
// the native dialog state models. SYNTHETIC (explicitly labelled): HTTP handler, a string-backed editor that makes the
// same ledger calls as the view's TextChanging/TextChanged handlers, and a minimal "PageStandIn" that maps view events to
// the controller methods documented in the handoff. This is NOT the WinUI view, NOT the real Page, NOT native.
internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string note)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAILED: " + note);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private const string CatalogJson = "{\"data\":[{\"name\":\"review\",\"description\":\"Review code\"},{\"name\":\"release\",\"description\":\"Cut a release\"},{\"name\":\"new\",\"description\":\"Server-side new\"}]}";

    private sealed class Handler : HttpMessageHandler
    {
        internal int Gets, Posts;
        internal TaskCompletionSource? Gate;
        internal string Catalog = CatalogJson;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) Gets++; else Posts++;
            if (request.Method != HttpMethod.Get) return Json("{}");
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            return Json(Catalog);
        }
    }

    /// <summary>Real controller + real ledger + real menu session over a string editor (synthetic editor surface).</summary>
    private sealed class Rig : IDisposable
    {
        internal readonly Handler Http = new();
        internal readonly HttpClient HttpClient;
        internal readonly OpenCodeClient Client;
        internal readonly OpenCodePageSource Source;
        internal OpenCodeComposerBinding Binding;
        internal readonly OpenCodeComposerController Core;
        internal readonly OpenCodeComposerEditorSync Sync;
        internal readonly OpenCodeComposerCommandMenuSession Menu;
        internal readonly OpenCodeComposerCatalogLifetime Lifetime;
        internal string Editor;
        internal bool Composing;
        internal readonly List<OpenCodeComposerCatalogOpenRequestedEventArgs> Opens = new();
        internal readonly List<Task> Loads = new();
        internal int Submits, Completions;
        internal OpenCodeComposerClientActionTicket? Ticket;
        internal bool WireInvoke = true;

        internal Rig(string initialEditorText = "")
        {
            HttpClient = new(Http);
            Client = new(HttpClient, new() { BaseUrl = "https://offline.invalid", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(20) }, true);
            Source = new("endpoint_" + Guid.NewGuid().ToString("N"), 7, () => Client);
            Binding = new(Source, 7, 1, Client, 1, 1, "ses_view");
            Core = new(b => b == Binding, new OpenCodeInteractionGate());
            Core.Bind(Binding);
            Core.SubmitRequested += (_, _) => Submits++;
            Editor = initialEditorText;
            Sync = new(() => Core.Snapshot, () => Editor, t => Editor = t, () => Composing,
                (basis, text, carry) => Core.TryApplyEditorInput(basis, text, carry));
            Menu = new(() => Core.Snapshot, () => Sync.Flush() == OpenCodeComposerInputState.Clean,
                () => Sync.LastAcknowledgment, () => Sync.State, () => Composing);
            // PageStandIn: the documented one-line mappings, nothing more.
            Menu.OpenRequested += (_, e) => { Opens.Add(e); Loads.Add(Core.LoadCatalogAsync(e.Token, e.Lifetime, e.Refresh)); };
            Menu.DismissRequested += (_, e) => Core.DismissCatalog(e.Handle);
            Menu.CommandChosen += (_, e) => e.Accepted = Core.TrySelectCommand(e.Token, e.Handle, e.Identity);
            Menu.ClientIntentDismissed += (_, e) => Core.DismissClientIntent(e.Token, e.IntentId);
            Menu.CompletionRequested += (_, e) => { Completions++; e.Accepted = Core.TryCompleteTrigger(e.Token, e.Handle, e.Completion); };
            Menu.ClientActionInvoked += (_, e) =>
            {
                if (!WireInvoke) return;
                Ticket = Core.TryTakeClientAction(e.Token, e.IntentId);
                e.Accepted = Ticket is not null;
            };
            // View.AttachEditor order: ledger exists → menu watermark initialised → catalog lifetime begun.
            Menu.Initialize(Sync.InputRevision);
            Lifetime = Core.BeginCatalogLifetime();
            Menu.Lifetime = Lifetime;
            Render();
        }

        /// <summary>View.Render: ledger reconcile → acknowledged evaluation → shared presentation.</summary>
        internal OpenCodeComposerMenuPresentation Render()
        {
            Sync.Reconcile();
            Menu.EvaluateAcknowledged(this);
            return Menu.Present(Core.Snapshot);
        }
        /// <summary>Editor TextChanging only (recorded, not flushed).</summary>
        internal void Record(string text) { Editor = text; Sync.OnTextChanging(); }
        /// <summary>Editor TextChanging + TextChanged (View.NotifyEditorTextChanged), then render.</summary>
        internal OpenCodeComposerMenuPresentation Type(string text)
        {
            Record(text);
            Sync.Flush();
            Menu.EvaluateAcknowledged(this);
            return Render();
        }
        internal (OpenCodeComposerKeyDecision Decision, OpenCodeComposerMenuKeyOutcome Outcome) Key(OpenCodeComposerKey key,
            bool latched = false, bool control = false)
        {
            var result = Menu.HandleKey(this, key, false, control, Composing, TimeSpan.FromSeconds(10), latched, false);
            Render();
            return result;
        }
        internal async Task Settle() { foreach (var t in Loads.ToArray()) await t; Render(); }
        internal void Highlight(OpenCodeComposerClientActionKind kind)
        {
            var p = Menu.Displayed;
            Menu.SetHighlight(p.Handle, new OpenCodeComposerCommandIdentity.Client(kind));
        }
        public void Dispose() { Client.Dispose(); HttpClient.Dispose(); }
    }

    private static OpenCodeComposerCommandIdentity Native(OpenCodeComposerClientActionKind k) => new OpenCodeComposerCommandIdentity.Client(k);
    private static OpenCodeComposerCommandIdentity Server(string n) => new OpenCodeComposerCommandIdentity.Server(n);

    private static async Task<int> Main()
    {
        var groups = new (string Name, Func<Task> Run)[]
        {
            ("A typed menu / ACK watermark / Enter evidence / episodes / zero selection HTTP", GroupA),
            ("B native dialog selection + confirmation constraints", GroupB),
            ("C expanded editor Apply/Cancel/stale buffer/IME, never Send", GroupC),
        };
        var failed = 0;
        foreach (var (name, run) in groups)
        {
            var before = checks;
            try { await run(); Console.WriteLine($"PASS {name} ({checks - before} checks)"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
        Console.WriteLine($"RESULT groups={groups.Length} failed={failed} checks={checks}");
        return failed == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------------------------------------------------
    private static async Task GroupA()
    {
        // A1: attach-time and programmatic text never open a menu.
        using (var r = new Rig("/re"))
        {
            Check(r.Sync.InputRevision == 1 && r.Core.Snapshot.Text == "/re", "attach-time editor text is recorded/acknowledged as pre-existing input");
            Check(r.Opens.Count == 0, "attach-time bare trigger does not open (watermark initialised at attach)");
            r.Core.SetText(r.Core.Snapshot.Token, "/review"); // e.g. draft restore / handoff — a controller-side write.
            r.Render();
            Check(r.Editor == "/review" && r.Opens.Count == 0, "programmatic echo through the guarded writer does not open");
        }

        using (var r = new Rig())
        {
            r.Http.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // A2: genuine "/" opens once with the acknowledged token; native rows usable while server rows load.
            var p = r.Type("/");
            Check(r.Opens.Count == 1 && r.Opens[0].Token == r.Sync.LastAcknowledgment!.Value.Token && !r.Opens[0].Refresh
                && ReferenceEquals(r.Opens[0].Lifetime, r.Lifetime), "typed '/' raises ONE open carrying the ack token + view lifetime");
            Check(p.Visible && p.Status == OpenCodeComposerMenuStatus.Loading && p.Rows.Count == 7 && p.Rows.All(x => x.Origin == OpenCodeComposerCommandOrigin.AIFrontend),
                "loading menu shows the seven native rows");
            Check(p.Rows.Single(x => x.Identity == Native(OpenCodeComposerClientActionKind.NewSession)).Selectable
                && p.Rows.All(x => x.Description is { Length: > 0 }), "native rows are usable during loading and carry descriptions");
            p = r.Type("/r");
            Check(r.Opens.Count == 1 && p.Filter == "r" && string.Join(",", p.Rows.Select(x => x.Name)) == "redo,editor",
                "typing inside the trigger refilters (prefix, then contains) without another open");
            p = r.Type("/re");
            var handleWhileLoading = p.Handle;
            r.Http.Gate.SetResult();
            await r.Settle();
            p = r.Menu.Displayed;
            Check(p.Status == OpenCodeComposerMenuStatus.Ready && ReferenceEquals(p.Handle, handleWhileLoading) && r.Http.Gets == 1,
                "server completion lands under the same opening; exactly one GET");
            Check(string.Join(",", p.Rows.Select(x => x.Name)) == "redo,review,release", "prefix-then-contains filtering consistent with core: " + string.Join(",", p.Rows.Select(x => x.Name)));
            Check(p.Rows[0].Origin == OpenCodeComposerCommandOrigin.AIFrontend && p.Rows[1].Origin == OpenCodeComposerCommandOrigin.Server,
                "origins are explicit per row");

            // A3: same-name collision is two typed rows, both flagged, never a name-only winner.
            p = r.Type("/new");
            var news = p.Rows.Where(x => x.Name == "new").ToArray();
            Check(news.Length == 2 && news.All(x => x.NameCollides) && news.Select(x => x.Origin).Distinct().Count() == 2
                && news[0].Identity != news[1].Identity, "same visible name from server and AIFrontend stays two distinct flagged identities");

            // A4: stale Enter — recorded-but-unflushed input changes the basis; Enter is consumed, nothing selected.
            p = r.Type("/re");
            Check(p.Highlight == Native(OpenCodeComposerClientActionKind.Redo), "highlight starts at the first selectable row");
            r.Record("/rev"); // TextChanging only; TextChanged not yet delivered.
            var (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(d.Handled && d.LatchEnter && d.Action != OpenCodeComposerKeyAction.Submit && o == OpenCodeComposerMenuKeyOutcome.Rejected,
                "Enter over a changed basis is consumed+latched and rejected");
            Check(r.Core.Snapshot.ClientIntent is null && r.Core.Snapshot.SelectedCommand is null && r.Core.Snapshot.Text == "/rev"
                && r.Menu.Note is { Length: > 0 }, "stale evidence selects nothing and explains why");

            // A5: unchanged evidence selects exactly the highlighted typed identity (server row), never executes.
            Check(r.Menu.Displayed.Rows.Count == 1 && r.Menu.Displayed.Highlight == Server("review"), "re-rendered '/rev' shows only server review");
            (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(o == OpenCodeComposerMenuKeyOutcome.Selected && d.Handled && d.LatchEnter, "fresh evidence: Enter stages the row and latches");
            Check(r.Core.Snapshot.SelectedCommand == "review" && r.Core.Snapshot.Text == "" && r.Editor == ""
                && r.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed, "server command staged; trigger consumed via guarded write");
            (d, _) = r.Key(OpenCodeComposerKey.Enter, latched: true);
            Check(d.Action == OpenCodeComposerKeyAction.Swallow && r.Submits == 0 && r.Http.Posts == 0, "held Enter after selection is swallowed; no submission/POST");
            r.Core.RemoveCommand(r.Core.Snapshot.Token);
            r.Render();

            // A6: no-match visible menu consumes Enter (never falls through to Send).
            r.Type("/zz");
            Check(r.Opens.Count == 2 && r.Http.Gets == 1, "new episode reopens from cache: no new GET");
            Check(r.Menu.Displayed.Visible && r.Menu.Displayed.Rows.Count == 0, "no-match menu stays visible");
            (d, _) = r.Key(OpenCodeComposerKey.Enter);
            Check(d.Handled && d.LatchEnter && d.Action != OpenCodeComposerKeyAction.Submit, "no-match menu swallows Enter");

            // A7: Esc suppresses the episode; breaking and re-forming the trigger opens again (cache, unique handle).
            var handleBefore = r.Menu.Displayed.Handle;
            (_, o) = r.Key(OpenCodeComposerKey.Escape);
            Check(o == OpenCodeComposerMenuKeyOutcome.Dismissed && r.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed && r.Menu.Suppressed,
                "Esc dismisses the displayed handle and suppresses the episode");
            r.Type("/zzz");
            Check(r.Opens.Count == 2 && !r.Menu.Displayed.Visible, "typing within the suppressed episode does not reopen");
            (d, _) = r.Key(OpenCodeComposerKey.Enter);
            Check(d.Action == OpenCodeComposerKeyAction.Submit, "with no menu, ordinary Enter is left to the page's existing route");
            r.Type("/zzz x");
            r.Type("/re");
            Check(r.Opens.Count == 3 && r.Menu.Displayed.Visible && !ReferenceEquals(r.Menu.Displayed.Handle, handleBefore) && r.Http.Gets == 1,
                "after breaking the trigger a new episode opens with a fresh handle from cache");

            // A8: stale dismissal handle no-ops against a newer opening.
            r.Core.DismissCatalog(handleBefore!);
            Check(r.Core.Snapshot.CatalogState != OpenCodeComposerCatalogState.Closed, "old handle cannot dismiss the current opening");

            // A8b: menu visible at Enter arrival but closed by the Enter-time flush: Enter is still consumed, never Send.
            r.Record("/re now");
            (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(d.Handled && d.LatchEnter && d.Action != OpenCodeComposerKeyAction.Submit && !r.Menu.Displayed.Visible && r.Submits == 0,
                "a menu that closed during the Enter-time flush still consumes and latches that Enter");

            // A9: a non-bare acknowledged edit closes the menu (unified rule).
            r.Type("/re now");
            Check(r.Core.Snapshot.CatalogState == OpenCodeComposerCatalogState.Closed && !r.Menu.Displayed.Visible, "arguments after the trigger close the menu");

            // A10: Enter arriving before TextChanged for a fresh "/" — the flush opens the menu, so Enter is consumed.
            r.Type("x");
            r.Record("/");
            (d, _) = r.Key(OpenCodeComposerKey.Enter);
            Check(r.Opens.Count == 4 && d.Handled && d.Action != OpenCodeComposerKeyAction.Submit,
                "menu opened by the Enter-time flush consumes that Enter");

            // A11: IME — Enter while composing passes to the IME untouched, never selects.
            r.Composing = true;
            (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(d.Action == OpenCodeComposerKeyAction.PassThrough && !d.Handled && o == OpenCodeComposerMenuKeyOutcome.None, "composing Enter passes through");
            r.Composing = false;
            d = r.Menu.HandleKey(r, OpenCodeComposerKey.Enter, false, false, false, TimeSpan.FromMilliseconds(30), false, false).Decision;
            Check(d.Action == OpenCodeComposerKeyAction.PassThrough, "Enter within 80 ms of composition end passes through");
            d = r.Menu.HandleKey(r, OpenCodeComposerKey.Enter, true, false, false, TimeSpan.FromSeconds(10), false, false).Decision;
            Check(d.Action == OpenCodeComposerKeyAction.PassThrough, "Shift+Enter passes through");

            // A12: explicit Ctrl+/ opening always refreshes; selection never caused POSTs.
            r.Type("x");
            (_, o) = r.Key(OpenCodeComposerKey.Slash, control: true);
            await r.Settle();
            Check(o == OpenCodeComposerMenuKeyOutcome.Opened && r.Opens.Last().Refresh && r.Http.Gets == 2, "Ctrl+/ is explicit and refreshes server rows");
            Check(r.Http.Posts == 0 && r.Submits == 0, "zero POST/submission across all menu activity");
        }

        // A13 (wave 3 F1): confirming a native row executes it in the SAME gesture through the staged intent + fresh ticket
        // (never Submit); an unwired invocation fails closed, keeping the intent staged so a fresh Enter can retry.
        foreach (var wired in new[] { true, false })
        {
            using var r = new Rig();
            r.WireInvoke = wired;
            r.Type("/mod");
            await r.Settle();
            var (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(d.Action == OpenCodeComposerKeyAction.MenuAccept && d.Handled && d.LatchEnter, "menu Enter is consumed and latched, never Submit");
            Check(r.Core.Snapshot.SelectedCommand is null && r.Core.Snapshot.Text == "", "native confirmation consumed the trigger only");
            if (wired)
                Check(o == OpenCodeComposerMenuKeyOutcome.Invoked && r.Ticket is not null && r.Core.Snapshot.ClientIntent is null,
                    "one Enter on a native row: the page takes exactly one ticket (no separate 执行 step)");
            else
            {
                Check(o == OpenCodeComposerMenuKeyOutcome.InvokeRejected && r.Core.Snapshot.ClientIntent?.Kind == OpenCodeComposerClientActionKind.ChooseModel
                    && r.Menu.Note is not null, "unwired invocation fails closed with a visible note; ChooseModel intent kept");
                (d, _) = r.Key(OpenCodeComposerKey.Enter, latched: true);
                Check(d.Action == OpenCodeComposerKeyAction.Swallow && r.Ticket is null, "held Enter does not re-invoke");
                (d, o) = r.Key(OpenCodeComposerKey.Enter);
                Check(d.Action == OpenCodeComposerKeyAction.Swallow && d.Handled && d.LatchEnter && o == OpenCodeComposerMenuKeyOutcome.InvokeRejected,
                    "fresh Enter with a staged intent retries the invocation, never Submit");
            }
            Check(r.Submits == 0 && r.Http.Posts == 0 && r.Http.Gets == 1, "native invocation path: zero submission/POST");
        }

        // A13b: pointer/automation click on a native row (TrySelect from rendered evidence) executes it the same way.
        using (var r = new Rig())
        {
            r.Type("/new");
            await r.Settle();
            var p = r.Menu.Displayed;
            var o = r.Menu.TrySelect(r, new OpenCodeComposerMenuEvidence(true, p.Handle, p.Token, Native(OpenCodeComposerClientActionKind.NewSession)));
            Check(o == OpenCodeComposerMenuKeyOutcome.Invoked && r.Ticket is not null && r.Core.Snapshot.ClientIntent is null && r.Submits == 0,
                "click on a native row invokes immediately through a fresh ticket; no submission");
        }

        // A13c: Tab — shell-style completion over the displayed opening; editing only, never a submission.
        using (var r = new Rig())
        {
            r.Type("/r");
            await r.Settle();
            Check(string.Join(",", r.Menu.Displayed.Rows.Select(x => x.Name)) == "redo,editor,review,release", "'/r' rows: " + string.Join(",", r.Menu.Displayed.Rows.Select(x => x.Name)));
            var (d, o) = r.Key(OpenCodeComposerKey.Tab);
            Check(d.Action == OpenCodeComposerKeyAction.MenuComplete && d.Handled && !d.LatchEnter, "Tab over rows is ours, no Enter latch");
            Check(o == OpenCodeComposerMenuKeyOutcome.Completed && r.Core.Snapshot.Text == "/re" && r.Editor == "/re" && r.Completions == 1,
                "redo/review/release share 're': Tab extends the trigger through the guarded writer");
            Check(r.Menu.Displayed.Visible && r.Menu.Displayed.Filter == "re" && r.Core.Snapshot.ClientIntent is null && r.Core.Snapshot.SelectedCommand is null,
                "menu stays open and refilters; nothing staged");
            r.Type("/rev");
            (d, o) = r.Key(OpenCodeComposerKey.Tab);
            Check(o == OpenCodeComposerMenuKeyOutcome.Selected && r.Core.Snapshot.SelectedCommand == "review" && r.Core.Snapshot.Text == ""
                && r.Completions == 1, "single prefix match: Tab confirms the server row as the /review chip (arguments may follow)");
            Check(r.Submits == 0 && r.Http.Posts == 0, "Tab never submits");
            r.Core.RemoveCommand(r.Core.Snapshot.Token);
            r.Render();

            // Explicit highlight wins over prefix extension.
            r.Type("/re");
            r.Key(OpenCodeComposerKey.Down);
            Check(r.Menu.Displayed.Highlight == Server("review"), "Down moved the highlight to review");
            (_, o) = r.Key(OpenCodeComposerKey.Tab);
            Check(o == OpenCodeComposerMenuKeyOutcome.Selected && r.Core.Snapshot.SelectedCommand == "review", "Tab confirms the user-chosen row");
            r.Core.RemoveCommand(r.Core.Snapshot.Token);
            r.Render();

            // IME / no menu / stale evidence.
            r.Type("/re");
            Check(r.Menu.Displayed.Visible, "'/re' reopened");
            Check(r.Menu.HandleKey(r, OpenCodeComposerKey.Tab, false, false, true, TimeSpan.FromSeconds(10), false, false).Decision.Action
                == OpenCodeComposerKeyAction.PassThrough && r.Core.Snapshot.Text == "/re", "Tab during IME composition passes through");
            Check(r.Menu.HandleKey(r, OpenCodeComposerKey.Tab, false, false, false, TimeSpan.FromMilliseconds(30), false, false).Decision.Action
                == OpenCodeComposerKeyAction.PassThrough, "Tab within 80 ms of composition end passes through");
            r.Type("hello");
            Check(r.Key(OpenCodeComposerKey.Tab).Decision.Action == OpenCodeComposerKeyAction.PassThrough, "Tab without a menu is focus navigation");
            r.Type("/r");
            r.Record("/re"); // TextChanging only: evidence captured against '/r' is now stale (rows remain).
            (d, o) = r.Key(OpenCodeComposerKey.Tab);
            Check(d.Handled && o == OpenCodeComposerMenuKeyOutcome.Rejected && r.Core.Snapshot.Text == "/re" && r.Completions == 1
                && r.Core.Snapshot.SelectedCommand is null && r.Core.Snapshot.ClientIntent is null, "stale Tab completes/selects nothing");
            r.Record("/rx"); // No rows left after the Tab-time flush: Tab is plain focus navigation, still nothing changes.
            (d, o) = r.Key(OpenCodeComposerKey.Tab);
            Check(d.Action == OpenCodeComposerKeyAction.PassThrough && r.Core.Snapshot.Text == "/rx" && r.Completions == 1, "no-row Tab passes through");
        }

        // A13d: server argument metadata. Known-empty hints → confirmation hands the page an ordinary Submit (all existing
        // send checks run there); non-empty or unknown hints → the "/cmd" chip waits for arguments and a fresh Enter.
        using (var r = new Rig())
        {
            r.Http.Catalog = "{\"data\":[{\"name\":\"init\",\"hints\":[]},{\"name\":\"fix\",\"hints\":[\"$1\",\"$ARGUMENTS\"]},{\"name\":\"plain\"}]}";
            r.Type("/ini");
            await r.Settle();
            var row = r.Menu.Displayed.Rows.Single();
            Check(row.KnownArgumentFree && row.ArgumentHints is { Count: 0 }, "declared argument-free row");
            var (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(o == OpenCodeComposerMenuKeyOutcome.SelectedSubmit && d == new OpenCodeComposerKeyDecision(OpenCodeComposerKeyAction.Submit, true, true)
                && r.Core.Snapshot.SelectedCommand == "init" && r.Core.Snapshot.Text == "" && r.Editor == "",
                "Enter on an argument-free command stages it and returns the page's ordinary Submit (latched)");
            Check(r.Submits == 0 && r.Http.Posts == 0, "the menu itself never submits or POSTs; only the page route does");
            r.Core.RemoveCommand(r.Core.Snapshot.Token);
            r.Render();

            r.Type("/ini");
            (d, o) = r.Key(OpenCodeComposerKey.Tab);
            Check(o == OpenCodeComposerMenuKeyOutcome.SelectedSubmit && d.Action == OpenCodeComposerKeyAction.Submit && !d.LatchEnter,
                "Tab-confirm of an argument-free command also routes to Submit (no Enter latch)");
            r.Core.RemoveCommand(r.Core.Snapshot.Token);
            r.Render();

            r.Type("/ini");
            var p = r.Menu.Displayed;
            o = r.Menu.TrySelect(r, new OpenCodeComposerMenuEvidence(true, p.Handle, p.Token, Server("init")));
            Check(o == OpenCodeComposerMenuKeyOutcome.SelectedSubmit, "click reports SelectedSubmit (the view raises SubmitRequested)");
            r.Core.RemoveCommand(r.Core.Snapshot.Token);
            r.Render();

            foreach (var name in new[] { "fix", "plain" })
            {
                r.Type("/" + name);
                row = r.Menu.Displayed.Rows.Single(x => x.Name == name);
                Check(!row.KnownArgumentFree && (name == "fix" ? row.ArgumentHints!.SequenceEqual(new[] { "$1", "$ARGUMENTS" }) : row.ArgumentHints is null),
                    name + ": hints carried to the row (unknown stays null)");
                (d, o) = r.Key(OpenCodeComposerKey.Enter);
                Check(o == OpenCodeComposerMenuKeyOutcome.Selected && d.Action == OpenCodeComposerKeyAction.MenuAccept
                    && r.Core.Snapshot.SelectedCommand == name, name + ": chip staged, waits for arguments; never Submit");
                r.Core.RemoveCommand(r.Core.Snapshot.Token);
                r.Render();
            }
        }

        // A14: ineligible native row is shown with a reason and cannot be staged.
        using (var r = new Rig())
        {
            r.Type("/un");
            await r.Settle();
            r.Type("/un x");   // Breaks the trigger (closes) — then explicit open over text with arguments.
            r.Key(OpenCodeComposerKey.Slash, control: true);
            await r.Settle();
            var row = r.Menu.Displayed.Rows.Single(x => x.Identity == Native(OpenCodeComposerClientActionKind.Undo));
            Check(!row.Selectable && row.Unavailable is { Length: > 0 } && r.Menu.Displayed.Highlight == row.Identity,
                "undo with leftover text is shown, highlighted and explained, not hidden");
            var (d, o) = r.Key(OpenCodeComposerKey.Enter);
            Check(o == OpenCodeComposerMenuKeyOutcome.Rejected && d.Handled && r.Core.Snapshot.ClientIntent is null && r.Menu.Note == row.Unavailable,
                "Enter on an ineligible row is consumed with its reason; nothing staged");
        }

        // A15: ACK watermark is the SUBMITTED revision, not the later global input revision.
        using (var r = new Rig())
        {
            r.Record("/r");
            r.Sync.Flush();
            var ack1 = r.Sync.LastAcknowledgment!.Value;
            r.Record("/re");
            Check(ack1.Revision == 1 && r.Sync.InputRevision == 2 && r.Sync.LastAcknowledgment!.Value.Revision == 1
                && r.Sync.LastAcknowledgment!.Value.Text == "/r" && r.Sync.LastAcknowledgment!.Value.Token == ack1.Token,
                "pending newer input does not advance the acknowledged revision/token/text");
            r.Core.SetText(r.Core.Snapshot.Token, "/x"); // Controller-side edit makes the pending basis stale.
            Check(r.Sync.Flush() == OpenCodeComposerInputState.HeldStale && r.Sync.LastAcknowledgment!.Value.Revision == 1,
                "rejected input never becomes an acknowledged observation");
            r.Menu.EvaluateAcknowledged(null);
            Check(r.Opens.Count == 0, "held/stale editor input blocks opening, even for the earlier acknowledged trigger");
        }

        // A16: reentrant input recorded DURING the controller apply: the intermediate acknowledgment names the submitted
        // revision (1, "/r"), never the newer global revision (2) that it did not acknowledge.
        using (var r = new Rig())
        {
            var injected = false;
            OpenCodeComposerInputAcknowledgment? midway = null;
            r.Core.SnapshotChanged += (_, _) =>
            {
                if (!injected) { injected = true; r.Record("/rx"); }
                else midway ??= r.Sync.LastAcknowledgment;
            };
            r.Record("/r");
            Check(r.Sync.Flush() == OpenCodeComposerInputState.Clean, "reentrant input is rebased onto its predecessor's ack and accepted");
            Check(midway is { Revision: 1, Text: "/r" } && r.Sync.LastAcknowledgment is { Revision: 2, Text: "/rx" },
                "acknowledgment watermark = submitted revision + accepted text at every step");
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    private static Task GroupB()
    {
        var a = new OpenCodeModelPickerRow("prov", "model-a", "Model A", "Provider", true, new[] { "high", "low" }, null);
        var b = new OpenCodeModelPickerRow("prov", "model-b", "Model B", "Provider", false, Array.Empty<string>(), null);
        var c = new OpenCodeModelPickerRow("other", "model-c", "Model C", "Other", true, Array.Empty<string>(), null);
        var basis = new object();
        var explicitUnavailable = new OpenCodeModelPickerPresentation("会话", new[] { a, b, c },
            new OpenCodeModelCurrent.Explicit(new("prov", "model-b", null), false), null, basis);
        var state = new OpenCodeModelPickerState(explicitUnavailable);
        Check(OpenCodeModelPickerState.Describe(explicitUnavailable.Current, explicitUnavailable.Rows).Contains("不可用", StringComparison.Ordinal)
            && state.SelectedRow is null && !state.CanConfirm, "explicit unavailable model is shown as such; nothing preselected or substituted");
        Check(!state.SelectModel("prov", "model-b"), "unavailable row cannot be chosen");
        Check(state.SelectModel("prov", "model-a") && state.SelectedVariant is null && state.Choice == new OpenCodeModelChoiceKey("prov", "model-a", null),
            "row selection is local; base variant (omitted) by default");
        Check(!state.SelectVariant("max") && state.SelectedVariant is null, "variant not offered by this model is refused");
        Check(state.SelectVariant("high") && state.CanConfirm, "offered variant accepted");
        var confirmed = state.Confirm();
        Check(confirmed.Outcome == OpenCodeModelPickerOutcome.Confirmed && confirmed.Choice == new OpenCodeModelChoiceKey("prov", "model-a", "high")
            && ReferenceEquals(confirmed.Presentation, explicitUnavailable) && ReferenceEquals(confirmed.Presentation!.Basis, basis),
            "confirmation carries the exact key and the exact presentation it was chosen from");
        Check(state.SelectModel("other", "model-c") && state.SelectedVariant is null && !state.SelectVariant("high"),
            "switching rows resets the variant; another model's variants are not accepted");

        var inheritedNone = new OpenCodeModelPickerPresentation("会话", new[] { a }, new OpenCodeModelCurrent.Inherited(null), null, null);
        Check(OpenCodeModelPickerState.Describe(inheritedNone.Current, inheritedNone.Rows).Contains("没有默认模型", StringComparison.Ordinal),
            "inherited with no default is stated, not invented");
        var inheritedDefault = new OpenCodeModelPickerPresentation("会话", new[] { a }, new OpenCodeModelCurrent.Inherited(new("prov", "model-a", null)), null, null);
        var s2 = new OpenCodeModelPickerState(inheritedDefault);
        Check(s2.SelectModel("prov", "model-a") && s2.CanConfirm, "choosing the default explicitly is a real change from inheritance");
        var sameExplicit = new OpenCodeModelPickerPresentation("会话", new[] { a }, new OpenCodeModelCurrent.Explicit(new("prov", "model-a", "low"), true), null, null);
        var s3 = new OpenCodeModelPickerState(sameExplicit);
        Check(s3.SelectModel("prov", "model-a") && s3.SelectVariant("low") && !s3.CanConfirm && s3.Confirm().Outcome == OpenCodeModelPickerOutcome.Cancelled,
            "re-choosing the current explicit model+variant is not a confirmable change");
        // B-refresh-1 (pure state, the same object the dialog uses): refresh keeps an unchanged pending choice, but WITHDRAWS
        // a choice whose exact model/variant disappeared — never silently retargeted to base or another variant.
        s3.SelectVariant("high");
        var refreshedSame = sameExplicit with { Rows = new[] { a with { Name = "Model A (renamed)" } } };
        s3.Replace(refreshedSame);
        Check(s3.Choice == new OpenCodeModelChoiceKey("prov", "model-a", "high") && s3.CanConfirm && s3.ChoiceWithdrawnNotice is null,
            "unchanged model+variant survives a refresh");
        s3.Replace(sameExplicit with { Rows = new[] { a with { Variants = new[] { "low" } } } });
        Check(s3.SelectedRow is null && s3.Choice is null && !s3.CanConfirm && s3.ChoiceWithdrawnNotice is not null
            && s3.Confirm().Outcome == OpenCodeModelPickerOutcome.Cancelled,
            "removed variant withdraws the pending choice (no base substitution); confirmation lost");
        Check(s3.Presentation.Current is OpenCodeModelCurrent.Explicit { Key.Variant: "low" }, "session's explicit state stays distinct from the withdrawn pending choice");
        Check(s3.SelectModel("prov", "model-a") && s3.ChoiceWithdrawnNotice is null && s3.SelectVariant(null) && s3.CanConfirm,
            "only a fresh explicit choice (here: base) makes it confirmable again");
        s3.Replace(sameExplicit with { Rows = new[] { a with { Available = false } } });
        Check(s3.SelectedRow is null && !s3.CanConfirm && s3.ChoiceWithdrawnNotice is not null, "refresh that makes the model unavailable withdraws the choice");

        // B-refresh-2 (pure state + SIMULATED callback ordering; real WinUI ContentDialog event ordering not executed):
        // confirmation is refused while a refresh is outstanding; a result frozen at the primary gesture is never
        // retargeted by a late completion; a closed dialog ignores late completions; superseded tickets are ignored.
        var p0 = new OpenCodeModelPickerPresentation("会话", new[] { a, c }, new OpenCodeModelCurrent.Inherited(null), null, new object());
        var race = new OpenCodeModelPickerState(p0);
        race.SelectModel("prov", "model-a");
        race.SelectVariant("high");
        var t1 = race.BeginRefresh();
        Check(t1 is not null && race.BeginRefresh() is null && !race.CanConfirm && race.TryFreezeConfirmation() is null,
            "single-flight refresh; no confirmation while it is outstanding");
        var p1 = p0 with { Rows = new[] { a with { Variants = new[] { "high", "low", "max" } }, c }, Basis = new object() };
        Check(race.CompleteRefresh(t1!.Value, p1) && ReferenceEquals(race.Presentation, p1) && race.CanConfirm, "current completion applies");
        var frozen = race.TryFreezeConfirmation(); // The primary gesture.
        var t2 = race.BeginRefresh();               // e.g. refresh clicked/queued around the same time…
        race.Close();                               // …then the dialog closes (ShowAsync continuation).
        var p2 = p0 with { Rows = new[] { c }, Basis = new object() };
        Check(t2 is null || !race.CompleteRefresh(t2.Value, p2), "late completion after close is ignored");
        Check(frozen is { Outcome: OpenCodeModelPickerOutcome.Confirmed } && ReferenceEquals(frozen.Presentation, p1)
            && frozen.Choice == new OpenCodeModelChoiceKey("prov", "model-a", "high") && ReferenceEquals(race.Presentation, p1)
            && race.Choice == frozen.Choice, "frozen result keeps the exact clicked presentation+choice; closed state unchanged");
        Check(!race.SelectModel("other", "model-c") && race.BeginRefresh() is null && race.TryFreezeConfirmation() is null,
            "closed dialog accepts no further selection/refresh/confirmation");
        var sup = new OpenCodeModelPickerState(p0);
        sup.SelectModel("other", "model-c");
        var s1 = sup.BeginRefresh()!.Value;
        Check(!sup.CompleteRefresh(s1 + 1, p2) && sup.IsRefreshing && sup.CompleteRefresh(s1, null) && ReferenceEquals(sup.Presentation, p0)
            && sup.CanConfirm, "mismatched ticket ignored; failed/null refresh keeps the presentation and choice");

        var identity = new object();
        var undo = new OpenCodeRollbackPreviewPresentation(OpenCodeRollbackKind.Undo, "会话", "fix the bug", null,
            HasExistingStage: true, PriorStageMayHaveFileEffects: true, TargetHasNonTextReferences: true, ReferencesIncomplete: false,
            DraftEmpty: true, identity);
        var u = new OpenCodeRollbackConfirmationState(undo);
        Check(!u.RestoreFiles && u.OffersFileRestoration, "files restoration offered for undo and OFF by default");
        Check(u.RequiresTextOnlyAcknowledgment && !u.CanConfirm, "non-text references require explicit text-only acknowledgment");
        var warnings = u.Warnings();
        Check(warnings.Any(w => w.Contains("之前的撤回可能已经改动过文件", StringComparison.Ordinal))
            && warnings.Any(w => w.Contains("尽力而为", StringComparison.Ordinal))
            && warnings.Any(w => w.Contains("覆盖", StringComparison.Ordinal))
            && warnings.Any(w => w.Contains("只会恢复文字", StringComparison.Ordinal)), "prior-stage file effects, best-effort/no-CAS, overwrite and text-only risks shown");
        u.SetTextOnlyAcknowledged(true);
        var ur = u.Confirm();
        Check(u.CanConfirm && ur.Outcome == OpenCodeRollbackOutcome.Confirmed && ReferenceEquals(ur.Preview, undo) && ReferenceEquals(ur.Preview!.Identity, identity)
            && !ur.RestoreFiles && ur.TextOnlyAcknowledged, "confirmation is bound to the exact preview instance; files stay false unless chosen");
        u.SetRestoreFiles(true);
        Check(u.Confirm().RestoreFiles, "explicit files opt-in is carried");
        var unknownRefs = new OpenCodeRollbackConfirmationState(undo with { TargetHasNonTextReferences = false, ReferencesIncomplete = true });
        Check(unknownRefs.RequiresTextOnlyAcknowledgment, "incomplete projection cannot establish 'no references'");
        var dirty = new OpenCodeRollbackConfirmationState(undo with { DraftEmpty = false, TargetHasNonTextReferences = false });
        Check(!dirty.CanConfirm && dirty.BlockingReason is not null, "undo requires an empty underlying draft");
        var redo = new OpenCodeRollbackConfirmationState(new(OpenCodeRollbackKind.Redo, "会话", null, null, true, true, false, false, false, identity));
        redo.SetRestoreFiles(true);
        Check(redo.CanConfirm && !redo.OffersFileRestoration && !redo.Confirm().RestoreFiles, "redo: no files choice, does not need an empty draft");
        Check(redo.Warnings().Any(w => w.Contains("整个撤回状态", StringComparison.Ordinal)) && redo.Warnings().Any(w => w.Contains("输入框中的内容保持不变", StringComparison.Ordinal)),
            "redo discloses whole-stage clear and that the draft is kept");
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------------------------------------------------
    private static async Task<OpenCodeComposerClientActionTicket> InvokeEditor(Rig r)
    {
        r.Key(OpenCodeComposerKey.Slash, control: true); // Explicit open over whatever text is there.
        await r.Settle();
        r.Highlight(OpenCodeComposerClientActionKind.EditDraft);
        var before = r.Ticket;
        var (_, invoked) = r.Key(OpenCodeComposerKey.Enter);
        Check(invoked == OpenCodeComposerMenuKeyOutcome.Invoked && r.Ticket is not null && !ReferenceEquals(r.Ticket, before)
            && r.Core.Snapshot.ClientIntent is null, "one Enter on EditDraft invokes immediately; page holds a new ticket");
        return r.Ticket!;
    }

    private static async Task GroupC()
    {
        using var r = new Rig();
        // Underlying enhanced draft: server command + one file + text.
        r.Type("/rev");
        await r.Settle();
        r.Key(OpenCodeComposerKey.Enter);
        var pick = r.Core.BeginPick(r.Core.Snapshot.Token)!;
        await r.Core.AddFilesAsync(pick, new[] { new OpenCodeComposerFileSource("notes.txt", null, _ => Task.FromResult<Stream>(new MemoryStream(new byte[3]))) });
        r.Render();
        r.Type("/edi keep this");
        Check(r.Opens.Count == 1, "text with arguments (and a selected command) does not auto-open");
        var ticket = await InvokeEditor(r);
        Check(r.Core.Snapshot.Text == "keep this" && r.Core.Snapshot.SelectedCommand == "review" && r.Core.Snapshot.Attachments.Count == 1,
            "EditDraft intent consumed only the trigger; text/command/file preserved");

        // Page stand-in for Apply: prepare main editor (flush/reconcile) → exact-ticket text seam → guarded reconcile.
        OpenCodeDraftEditorApplyOutcome ApplyVia(OpenCodeComposerClientActionTicket t, string text)
        {
            if (!r.Sync.Prepare()) return OpenCodeDraftEditorApplyOutcome.Stale;
            if (r.Core.TryApplyClientText(t, text) is null) return OpenCodeDraftEditorApplyOutcome.Stale;
            r.Render();
            return OpenCodeDraftEditorApplyOutcome.Applied;
        }

        var session = new OpenCodeDraftEditorSession(r.Core.Snapshot.Text); // Initialised after the view's flush.
        session.Write(() => session.OnTextChanging(r.Core.Snapshot.Text + " (echo)"));
        Check(!session.Dirty && session.Buffer == "keep this", "programmatic initialization echo suppressed");
        session.OnTextChanging("/foo");
        Check(session.Dirty, "ordinary dirty tracking");
        session.OnCompositionStarted();
        var applyCalls = 0;
        Check(session.Apply(t => { applyCalls++; return ApplyVia(ticket, t); }) is null && applyCalls == 0, "no Apply while composing");
        var now = DateTimeOffset.UtcNow;
        session.OnCompositionEnded(now);
        Check(!session.KeyboardApplyAllowed(now + TimeSpan.FromMilliseconds(20)) && session.KeyboardApplyAllowed(now + TimeSpan.FromMilliseconds(120)),
            "keyboard Apply right after composition end is ignored (80 ms IME guard)");
        var gets = r.Http.Gets;
        var opens = r.Opens.Count;
        Check(session.Apply(t => { applyCalls++; return ApplyVia(ticket, t); }) == OpenCodeDraftEditorApplyOutcome.Applied && applyCalls == 1,
            "explicit Apply goes through the exact-ticket seam once");
        Check(r.Core.Snapshot.Text == "/foo" && r.Editor == "/foo" && r.Core.Snapshot.SelectedCommand == "review" && r.Core.Snapshot.Attachments.Count == 1,
            "Apply replaced text only and the main editor took it through the guarded writer");
        Check(r.Opens.Count == opens && r.Http.Gets == gets && !r.Menu.Displayed.Visible, "applied '/foo' is programmatic: no slash menu opens");
        Check(r.Core.TryApplyClientText(ticket, "again") is null, "ticket is single use");

        // Stale: the main draft changes while the editor is open → Apply rejected/disabled, buffer kept, never written later.
        r.Type("plain");
        var ticket2 = await InvokeEditor(r);
        var stale = new OpenCodeDraftEditorSession(r.Core.Snapshot.Text);
        stale.OnTextChanging("edited in dialog");
        r.Type(r.Core.Snapshot.Text + "!");
        Check(stale.Apply(t => ApplyVia(ticket2, t)) == OpenCodeDraftEditorApplyOutcome.Stale && stale.Stale && !stale.CanApply
            && stale.Buffer == "edited in dialog", "changed draft: Apply rejected, disabled, buffer retained");
        Check(stale.Apply(t => ApplyVia(ticket2, t)) is null && r.Core.Snapshot.Text == "plain!", "stale buffer never overwrites the draft later");

        // Session ABA: ticket from session A; switch to B and back to the equal binding → never revived.
        r.Type("abc");
        var ticket3 = await InvokeEditor(r);
        var aba = new OpenCodeDraftEditorSession(r.Core.Snapshot.Text);
        aba.OnTextChanging("from A");
        var original = r.Binding;
        r.Binding = original with { SessionId = "ses_other", SessionGeneration = 2 };
        r.Core.Bind(r.Binding);
        Check(!r.Core.OwnsClientAction(ticket3), "session switch revokes the ticket");
        aba.MarkStale("会话已切换");
        r.Binding = original;
        r.Core.Bind(original);
        Check(aba.Stale && aba.Apply(t => ApplyVia(ticket3, t)) is null && r.Core.TryApplyClientText(ticket3, "from A") is null
            && aba.Buffer == "from A", "returning to the original session does not revive the old ticket; buffer retained");

        // Cancel: discards only the buffer.
        var before = r.Core.Snapshot.Text;
        var cancel = new OpenCodeDraftEditorSession(before);
        cancel.OnTextChanging("thrown away");
        Check(r.Core.Snapshot.Text == before, "Cancel leaves the draft untouched");
        Check(r.Submits == 0 && r.Http.Posts == 0, "editor flow never submits or POSTs");
    }
}
