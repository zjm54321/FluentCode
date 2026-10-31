using System;
using System.IO;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;

internal static partial class Program
{
    private static async Task ImeComposition()
    {
        var before = checks;
        using var f = new Fixture();
        var composition = new OpenCodeComposerComposition();
        var text = string.Empty;
        var writes = 0;
        OpenCodeComposerEditorSync? sync = null;
        sync = new(() => f.Core.Snapshot, () => text, value =>
        {
            writes++;
            text = value;
            sync!.OnTextChanging(); // The real writer's synchronous native echo stays guarded.
        }, () => composition.IsActive, f.Core.TryApplyEditorInput);

        void Compose(string committed)
        {
            var initialWrites = writes;
            var basis = sync.BaselineToken;
            composition.Start();
            text += "nihao";
            sync.OnTextChanging();
            sync.WriteSnapshot(f.Core.Snapshot);
            Check(writes == initialWrites && text.EndsWith("nihao", StringComparison.Ordinal)
                && sync.Reconcile() && sync.BaselineToken == basis && !sync.Prepare(),
                "IME preedit blocks direct programmatic writes, baseline adoption and submission");

            var ended = composition.End(); // Native Ended precedes the last text and selection notifications.
            text = text[..^5] + committed;
            sync.OnTextChanging();
            sync.WriteSnapshot(f.Core.Snapshot);
            Check(composition.IsActive && writes == initialWrites && text.EndsWith(committed, StringComparison.Ordinal),
                "IME final TextChanged/SelectionChanged still belongs to the native commit, not application writers");
            Check(composition.TryComplete(ended) && sync.Prepare() && f.Core.Snapshot.Text == text
                && writes == initialWrites,
                "drained commit acknowledges Chinese text without Text reassignment or caret reset");
        }

        Compose("你好");
        Compose("你好");
        Check(text == "你好你好", "second pinyin composition appends rather than losing the IME context");

        var submitted = f.Core.Snapshot.Token;
        await f.Core.FinalizeAcceptedLegacyDraftAsync(submitted, f.Binding, () => true, default,
            (_, finalize, _) =>
            {
                Check(sync.FinalizeAccepted(submitted, finalize, out var wait) == OpenCodeComposerDraftFinalization.Cleared
                    && !wait && text.Length == 0, "send clears the accepted draft only at the idle boundary");
                return Task.CompletedTask;
            });
        Compose("你好");

        f.Text("@explorer ");
        sync.Reconcile();
        Check(f.Core.TryAddContext(f.Core.Snapshot.Token, "explorer", false, 0), "chosen mention staged");
        sync.Reconcile();
        Compose("你好");
        Check(f.Core.Snapshot.Mentions.Count == 1 && text == "@explorer 你好", "IME after a chosen mention preserves its context");

        f.Text("/");
        sync.Reconcile();
        await f.Core.LoadCatalogAsync(f.Core.Snapshot.Token, f.CatalogLifetime);
        f.Core.DismissCatalog(f.Core.Snapshot.CatalogHandle!);
        sync.Reconcile();
        Compose("你好");
        Check(text == "/你好", "IME works after slash catalog dismissal");

        composition.Start();
        var firstEnd = composition.End();
        composition.Start();
        Check(!composition.TryComplete(firstEnd) && composition.IsActive,
            "queued previous composition end cannot release a newer composition's guard");
        var secondEnd = composition.End();
        composition.Reset();
        Check(!composition.TryComplete(secondEnd) && !composition.IsActive,
            "reattachment invalidates a queued composition completion");

        var view = File.ReadAllText(Path.Combine(SplashRepo(), "src/AIFrontend/Pages/OpenCode/OpenCodeComposerView.cs"));
        Check(view.Contains("DispatcherQueuePriority.Low") && view.Contains("!_composition.TryComplete(version)")
            && view.Contains("if (_composing) { RequestRender(); return; }")
            && view.Contains("if (_composing) return;")
            && view.Contains("if (isComposing || _composing) return OpenCodeComposerKeyDecision.Pass;")
            && view.Contains("if (!string.Equals(editor.Text, text, StringComparison.Ordinal))"),
            "WinUI hookup defers commit release, flush/reconciliation and redundant native Text/selection writes");
        var page = File.ReadAllText(Path.Combine(SplashRepo(), "src/AIFrontend/Pages/OpenCodePage.Composer.cs"));
        Check(page.Contains("_composerView.CompositionSettled +=") && page.Contains("QueueDeferredComposerOverlayRefresh();"),
            "page overlays resume at the same drained composition boundary, never the raw Ended callback");
        Console.WriteLine($"PASS IME composition regression: {checks - before} assertions; production composition state + editor ledger/controller, simulated native event ordering, no physical IME automation.");
    }
}
