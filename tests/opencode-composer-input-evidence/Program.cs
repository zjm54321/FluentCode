using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;

// Source-linked evidence for the composer input-evidence seam. REAL: controller, EditorSync ledger, ViewLifetime,
// CommandMenuSession and OpenCodeComposerInputEvidenceSource (the object the WinUI view's handlers call). STAND-INS
// (labelled): a string editor whose programmatic assignment raises TextChanging synchronously (assumed WinUI behaviour,
// with "\n"→"\r" alias normalisation), a manual UI scheduler, and a thin "ViewWiring" replaying the exact view call
// order (TextChanging→RecordTextChanging; composition→CompositionChanged; flush/Prepare/render→Refresh; lifetime).
// The view's actual registrations are checked STATICALLY. Native WinUI event ordering is unknown/not executed.
internal static class Program
{
    private static int checks;
    private static void Check(bool c, string note) { checks++; if (!c) throw new InvalidOperationException("FAILED: " + note); }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class ManualScheduler : IOpenCodeComposerUiScheduler
    {
        public bool TryEnqueue(Action callback) => true; // Queued renders are irrelevant here (dropped).
    }

    private sealed class Rig : IDisposable
    {
        internal readonly Handler Http = new();
        internal readonly HttpClient HttpClient;
        internal readonly OpenCodeClient Client;
        internal readonly OpenCodeComposerBinding Binding;
        internal readonly OpenCodeComposerController Core;
        internal readonly OpenCodeComposerViewLifetime Lifetime = new(new ManualScheduler(), () => { });
        internal readonly OpenCodeComposerInputEvidenceSource Evidence;
        internal readonly OpenCodeComposerCommandMenuSession Menu;
        internal OpenCodeComposerEditorSync Sync = null!;
        internal string Editor;
        internal bool Composing;
        internal int Opens;

        internal Rig(string initialEditorText = "")
        {
            HttpClient = new(Http);
            Client = new(HttpClient, new() { BaseUrl = "https://offline.invalid", Secret = "synthetic", Timeout = TimeSpan.FromSeconds(5) }, true);
            var source = new OpenCodePageSource("endpoint_" + Guid.NewGuid().ToString("N"), 3, () => Client);
            Binding = new(source, 3, 1, Client, 1, 1, "ses_input");
            Core = new(b => b == Binding, new OpenCodeInteractionGate());
            Core.Bind(Binding);
            Editor = initialEditorText;
            Evidence = new(() => Sync, () => Lifetime.IsLive, () => Lifetime.Generation, () => Composing);
            Lifetime.Invalidated += Evidence.LifetimeChanged;           // = view constructor wiring
            Attach();
            Menu = new(() => Core.Snapshot, Flush, () => Sync.LastAcknowledgment, () => Sync.State, () => Composing);
            Menu.OpenRequested += (_, _) => Opens++;
            Menu.Initialize(Sync.InputRevision);
            Menu.Lifetime = Core.BeginCatalogLifetime();
        }
        /// <summary>= View.AttachEditor: new ledger over the (stand-in) editor, then lifetime.OnEditorAttached.</summary>
        internal void Attach()
        {
            Sync = new(() => Core.Snapshot, () => Editor,
                text => { Editor = text.Replace("\n", "\r", StringComparison.Ordinal); Evidence.RecordTextChanging(); },
                () => Composing, (basis, text, carry) => Core.TryApplyEditorInput(basis, text, carry));
            Composing = false;
            Lifetime.OnEditorAttached(); // Raises Invalidated → Evidence.LifetimeChanged (new ledger published).
        }
        internal void Type(string text) { Editor = text; Evidence.RecordTextChanging(); }           // TextChanging only
        internal bool Flush() { var clean = Sync.Flush() == OpenCodeComposerInputState.Clean; Evidence.Refresh(); return clean; }
        internal bool Prepare() { var ready = Sync.Prepare(); Evidence.Refresh(); return ready; }     // = PrepareSubmission
        internal void Render() { Sync.Reconcile(); Menu.EvaluateAcknowledged(null); Menu.Present(Core.Snapshot); Evidence.Refresh(); }
        internal void Compose(bool on) { Composing = on; Evidence.CompositionChanged(); }
        internal OpenCodeComposerInputEvidence Capture()
        {
            Check(Prepare(), "clean Prepare before capture");
            var e = Evidence.Current;
            Check(e.IsClean, "capture is clean");
            return e;
        }
        public void Dispose() { Client.Dispose(); HttpClient.Dispose(); }
    }

    private static int Main(string[] args)
    {
        var failed = 0;
        foreach (var (name, run) in new (string, Action)[]
        {
            ("G1 unflushed/held input and edit-return invalidate while Core text/token can be unchanged", G1),
            ("G2 IME start/end provenance, unload/resume/reattach availability, late callbacks inert", G2),
            ("G3 guarded writes / alias normalisation / attach+flush stable, no auto-open; static view wiring", () => G3(args.Length > 0 ? args[0] : null)),
        })
        {
            var before = checks;
            try { run(); Console.WriteLine($"PASS {name} ({checks - before} checks)"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
        }
        Console.WriteLine($"RESULT groups=3 failed={failed} checks={checks}");
        return failed == 0 ? 0 : 1;
    }

    private static void G1()
    {
        using var r = new Rig();
        r.Type("abc");
        var start = r.Capture();
        var token = r.Core.Snapshot.Token;
        Check(OpenCodeComposerInputEvidence.StillCurrent(start, r.Evidence.Current), "fresh capture is current");
        r.Type("abcd"); // Unflushed real TextChanging during a background preflight.
        Check(r.Core.Snapshot.Token == token && r.Core.Snapshot.Text == "abc", "Core token/text alone cannot see the unflushed edit");
        Check(!r.Evidence.Current.IsClean && r.Evidence.Current.State == OpenCodeComposerInputState.Pending
            && !OpenCodeComposerInputEvidence.StillCurrent(start, r.Evidence.Current), "evidence is stale immediately (synchronous publication)");
        r.Type("abc"); // Edit returns to the same text.
        Check(r.Flush() && r.Core.Snapshot.Text == "abc" && r.Evidence.Current.IsClean, "after flush the ledger is clean with the same text");
        Check(!OpenCodeComposerInputEvidence.StillCurrent(start, r.Evidence.Current), "returning to the same text never re-authorises the old capture");
        var again = r.Capture();
        Check(OpenCodeComposerInputEvidence.StillCurrent(again, r.Evidence.Current) && again.Sequence > start.Sequence, "a NEW clean capture is required (monotonic sequence)");

        // Held input: a Core-side edit makes the pending basis stale → HeldStale, never clean.
        r.Type("abc!");
        r.Core.SetText(r.Core.Snapshot.Token, "other");
        Check(!r.Flush() && r.Evidence.Current.State == OpenCodeComposerInputState.HeldStale && !r.Evidence.Current.IsClean
            && !OpenCodeComposerInputEvidence.StillCurrent(again, r.Evidence.Current), "held input is published and blocks any capture");
        Check(r.Http.Requests == 0, "no HTTP");
    }

    private static void G2()
    {
        using var r = new Rig();
        r.Type("hello");
        var start = r.Capture();
        r.Compose(true);
        Check(r.Evidence.Current.Composing && !r.Evidence.Current.IsClean, "composition start publishes not-clean");
        r.Compose(false); // start→end with no text change and no acknowledgment
        Check(r.Evidence.Current.IsClean && r.Evidence.Current.InputRevision == start.InputRevision
            && !OpenCodeComposerInputEvidence.StillCurrent(start, r.Evidence.Current), "start→end without input still invalidates (composition epoch)");

        var beforeUnload = r.Capture();
        r.Lifetime.OnLoaded();      // first load (no resume)
        r.Lifetime.OnUnloaded();    // Invalidated → LifetimeChanged
        Check(!r.Evidence.Current.Available && !OpenCodeComposerInputEvidence.StillCurrent(beforeUnload, r.Evidence.Current), "unload → unavailable");
        r.Compose(true); r.Compose(false); r.Evidence.Refresh(); // late callbacks of the unloaded view
        Check(!r.Evidence.Current.Available && !r.Evidence.Current.IsClean, "late callbacks cannot publish fresh ready evidence while unloaded");
        r.Lifetime.OnLoaded(); r.Evidence.LifetimeChanged(); // = view Loaded handler (resume)
        Check(r.Evidence.Current.Available && r.Evidence.Current.IsClean && !OpenCodeComposerInputEvidence.StillCurrent(beforeUnload, r.Evidence.Current),
            "resume is available again but the pre-unload capture stays stale");

        // Reattach: a new ledger restarts InputRevision; equal revision numbers must not match an old capture.
        var oldLedger = r.Capture();
        r.Attach();
        while (r.Sync.InputRevision < oldLedger.InputRevision) r.Type("v" + r.Sync.InputRevision);
        r.Flush();
        var cur = r.Evidence.Current;
        Check(cur.IsClean && cur.InputRevision == oldLedger.InputRevision && !OpenCodeComposerInputEvidence.StillCurrent(oldLedger, cur),
            "reattach with an equal InputRevision does not match the old capture");
        Check(r.Http.Requests == 0, "no HTTP");
    }

    private static void G3(string? repo)
    {
        using var r = new Rig();
        r.Type("draft");
        var start = r.Capture();
        var ack = r.Sync.LastAcknowledgment;
        r.Core.SetText(r.Core.Snapshot.Token, "/x\nline"); // Controller-side write (restore/handoff/apply).
        r.Render();                                         // Guarded WriteSnapshot raises TextChanging while writing.
        Check(r.Editor == "/x\rline" && r.Sync.InputRevision == start.InputRevision && r.Sync.LastAcknowledgment == ack,
            "guarded write + alias read-back is not a user edit (ledger revision/ack unchanged)");
        Check(OpenCodeComposerInputEvidence.StillCurrent(start, r.Evidence.Current),
            "evidence is not invalidated by programmatic writes (draft change is Core's token guard, not this seam)");
        r.Render();
        Check(OpenCodeComposerInputEvidence.StillCurrent(start, r.Evidence.Current) && r.Opens == 0, "alias stays stable across renders; no slash auto-open");

        using var pre = new Rig("/pre");
        Check(pre.Evidence.Current.Available && pre.Evidence.Current.State == OpenCodeComposerInputState.Pending, "attach-time editor text is published as pending input");
        Check(pre.Flush() && pre.Evidence.Current.IsClean && pre.Opens == 0, "flush makes it clean; attach-time '/pre' opens nothing");
        pre.Render();
        Check(pre.Opens == 0 && pre.Evidence.Current.IsClean, "render after attach stays clean, no auto-open");

        if (repo is null) { Check(false, "repo path for static wiring check not supplied"); return; }
        var view = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs"));
        string Member(string name) => Regex.Match(view, @"(?ms)^    private void " + name + @"\(.*?^    \}").Value;
        var changing = Member("OnEditorTextChanging");
        Check(changing.Contains("_inputEvidence.RecordTextChanging();") && !changing.Contains("_sync?.OnTextChanging()")
            && !Regex.IsMatch(view, @"_sync\??\.OnTextChanging\(\)"), "STATIC: the only ledger observation entry goes through the evidence source");
        Check(Member("OnEditorCompositionStarted").Contains("_inputEvidence.CompositionChanged();")
            && Member("OnEditorCompositionEnded").Contains("_inputEvidence.CompositionChanged();")
            && Member("OnEditorLostFocus").Contains("_inputEvidence.CompositionChanged();"), "STATIC: IME start/end/focus-loss publish provenance");
        Check(view.Contains("_lifetime.Invalidated += _inputEvidence.LifetimeChanged;") && view.Contains("_inputEvidence.LifetimeChanged(); // Resume")
            && view.Contains("internal OpenCodeComposerInputEvidence InputEvidence => _inputEvidence.Current;"), "STATIC: lifetime wiring and pure accessor");
    }
}
