using System;
using System.Collections.Immutable;
using AIFrontend;
using AIFrontend.Pages;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool value, string label) { _assertions++; if (!value) throw new InvalidOperationException(label); }
    private static OpenCodeSessionUiState State(string id, bool? working = null, bool? pending = null,
        bool protection = false, OpenCodeCompletionMark? completion = null, double? viewed = null,
        OpenCodeStateUnknownReason reason = OpenCodeStateUnknownReason.NotObserved)
        => new(id, working, pending, protection, completion, viewed, reason);
    private static OpenCodeSessionUiSnapshot Snapshot(long version, params OpenCodeSessionUiState[] states)
        => new("endpoint", 1, version, ImmutableArray.CreateRange(states));
    private static (App App, OpenCodePage Page, OpenCodePageSource Source) Fixture()
    {
        var source = new OpenCodePageSource("endpoint", 1);
        var app = new App { CurrentOpenCodePageSource = source };
        Application.Current = app;
        var page = new OpenCodePage(); page.Bind(source, "ses_child"); page.Activate(); page.DispatcherQueue.Drain();
        return (app, page, source);
    }
    private static void Main()
    {
        TruthAndStableElements(); ScopeLifecycleAndQueues(); SnapshotSubscriptions();
        UnknownHintsAndScopeIsolation();
        IndicatorTones();
        VerifyExpiredHintCleanup(unload: true);
        VerifyExpiredHintCleanup(unload: false);
        Console.WriteLine($"PASS: 5 session-state UI groups without sidebar rows, {_assertions} assertions; real UI partial/State records; inert WinUI and App snapshot provider.");
        Console.WriteLine("BOUNDARIES: no real App mapping, source capability, WinUI layout/animation or list business algorithms are asserted here.");
    }
    private static void TruthAndStableElements()
    {
        var (app, page, _) = Fixture();
        app.Snapshot = Snapshot(1, State("ses_child", true), State("ses_root", true, true));
        app.Emit("endpoint", 1, 1); page.DispatcherQueue.Drain();
        Check(page.IsSelectedSessionWorkingIndicated && page.States["ses_root"].PendingAction == true, "truth.current-and-tabs-share-snapshot");
        app.Snapshot = Snapshot(2, State("ses_child", false)); app.Emit("endpoint", 1, 2); page.DispatcherQueue.Drain();
        Check(!page.IsSelectedSessionWorkingIndicated && !page.States.ContainsKey("ses_root"), "truth.new-snapshot-replaces-old");
        page.Deactivate();
    }

    private static void VerifyExpiredHintCleanup(bool unload)
    {
        var (app, page, source) = Fixture();
        app.Snapshot = Snapshot(1, State("ses_child", reason: OpenCodeStateUnknownReason.Expired));
        app.Emit("endpoint", 1, 1); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("已过期"), "hint.cleanup-positive-control");
        if (unload) page.Deactivate(); else { source.IsRevoked = true; page.Refresh(); }
        Check(page.SessionStateHint.Text.Length == 0 && page.States.Count == 0, "hint.cleanup-clears-current-and-tabs");
        page.Deactivate();
    }
    private static void ScopeLifecycleAndQueues()
    {
        var (app, page, source) = Fixture();
        app.Snapshot = Snapshot(10, State("ses_child", true)); app.Emit("endpoint", 1, 10); page.DispatcherQueue.Drain();
        var gets = app.Gets.Count;
        app.Emit("endpoint", 1, 9, Snapshot(9, State("ses_child", false)));
        app.Emit("wrong", 1, 11); app.Emit("endpoint", 2, 11);
        Check(page.DispatcherQueue.Count == 0 && app.Gets.Count == gets, "scope.old-version-identity-revision-rejected");
        app.Snapshot = Snapshot(11, State("ses_child", true));
        app.Emit("endpoint", 1, 11, Snapshot(11, State("ses_child", false))); page.DispatcherQueue.Drain();
        Check(page.IsSelectedSessionWorkingIndicated, "scope.event-payload-not-applied");
        Check(ReferenceEquals(app.Gets[^1].Source, source) && app.Gets[^1].Revision == 1, "scope.getter-current-reference");
        var replacement = new OpenCodePageSource("endpoint", 1);
        app.CurrentOpenCodePageSource = replacement;
        gets = app.Gets.Count; app.Emit("endpoint", 1, 12);
        Check(page.DispatcherQueue.Count == 0 && app.Gets.Count == gets, "scope.same-endpoint-new-object-rejected");
        page.Bind(replacement, "ses_child"); app.SourceChanged(); page.DispatcherQueue.Drain();
        Check(ReferenceEquals(app.Gets[^1].Source, replacement) && page.IsSelectedSessionWorkingIndicated,
            "scope.rebind-pulls-new-owner");
        app.Snapshot = null; app.Emit("endpoint", 1, 12); page.DispatcherQueue.Drain();
        Check(!page.IsSelectedSessionWorkingIndicated,
            "scope.null-getter-clears");
        app.Snapshot = Snapshot(13, State("ses_child", true)); app.Emit("endpoint", 1, 13);
        page.Deactivate();
        Check(app.StateSubscribers == 0 && app.SourceSubscribers == 0, "scope.unload-unsubscribes");
        page.DispatcherQueue.Drain();
        Check(!page.IsSelectedSessionWorkingIndicated, "scope.queued-old-refresh-cannot-revive-unloaded");
        page.Activate(); page.DispatcherQueue.Drain();
        Check(page.IsSelectedSessionWorkingIndicated && app.StateSubscribers == 1, "scope.reload-active-pull");
        var revised = new OpenCodePageSource("endpoint", 2); page.Bind(revised, "ses_child"); app.CurrentOpenCodePageSource = revised;
        app.Snapshot = new("endpoint", 2, 20, ImmutableArray.Create(State("ses_child", false)));
        app.SourceChanged(); app.Emit("endpoint", 1, 100, Snapshot(100, State("ses_child", true))); page.DispatcherQueue.Drain();
        Check(!page.IsSelectedSessionWorkingIndicated && app.Gets[^1].Revision == 2,
            "scope.old-revision-payload-cannot-revive");
        revised.IsRevoked = true; page.Refresh();
        Check(!page.IsSelectedSessionWorkingIndicated, "scope.revoked-clears");
        page.Deactivate();
    }
    private static void SnapshotSubscriptions()
    {
        var (app, page, _) = Fixture();
        page.Activate(); page.Activate(); page.DispatcherQueue.Drain();
        Check(app.StateSubscribers == 1 && app.SourceSubscribers == 1, "list.no-duplicate-subscriptions");
        app.Snapshot = Snapshot(2, State("ses_child", true));
        app.Emit("endpoint", 1, 2); app.Emit("endpoint", 1, 3); app.Emit("endpoint", 1, 4);
        Check(page.DispatcherQueue.Count == 1, "list.event-burst-coalesced");
        app.Snapshot = Snapshot(4, State("ses_child", false)); page.DispatcherQueue.Drain();
        Check(!page.IsSelectedSessionWorkingIndicated, "list.coalesced-refresh-pulls-current-not-payload");
        app.SourceChanged(); app.SourceChanged(); page.DispatcherQueue.Drain();
        Check(app.StateSubscribers == 1 && app.SourceSubscribers == 1, "list.source-events-no-duplicate-subscription");
        page.Deactivate(); page.Deactivate();
        Check(app.StateSubscribers == 0 && app.SourceSubscribers == 0, "list.repeated-unload-no-leak");
    }

    /// <summary>F6：工作环语义（颜色由 Tabs.cs 以 ThemeResource 样式套用；此处验证纯判定与行钩子输入）。</summary>
    private static void IndicatorTones()
    {
        Check(OpenCodePage.ClassifySessionIndicator(null) == OpenCodePage.SessionIndicatorTone.None, "tone.unknown-none");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", true, false)) == OpenCodePage.SessionIndicatorTone.Working, "tone.working-accent");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", true, true)) == OpenCodePage.SessionIndicatorTone.WorkingNeedsAction, "tone.working-pending-caution");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", true, null)) == OpenCodePage.SessionIndicatorTone.Working, "tone.unknown-pending-not-caution");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", false, true)) == OpenCodePage.SessionIndicatorTone.NeedsAction, "tone.idle-pending-caution");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", false, false, completion: new("failed", 10), viewed: 9))
            == OpenCodePage.SessionIndicatorTone.Failed, "tone.unread-failed-critical");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", false, false, completion: new("interrupted", 10)))
            == OpenCodePage.SessionIndicatorTone.Failed, "tone.unread-interrupted-critical");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", false, false, completion: new("failed", 10), viewed: 10))
            == OpenCodePage.SessionIndicatorTone.None, "tone.viewed-failure-cleared");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", false, false, completion: new("succeeded", 10)))
            == OpenCodePage.SessionIndicatorTone.Unread, "tone.unread-success-dot");
        Check(OpenCodePage.ClassifySessionIndicator(State("s", null, null, true, reason: OpenCodeStateUnknownReason.Expired))
            == OpenCodePage.SessionIndicatorTone.None, "tone.protection-unknown-none");


    }

    private static void UnknownHintsAndScopeIsolation()
    {
        var (app, page, _) = Fixture();
        Check(page.SessionStateHint.Text.Contains("尚未观测"), "hint.no-snapshot-unknown");
        long version = 1;
        foreach (var pair in new[] { (OpenCodeStateUnknownReason.NotObserved, "尚未观测"), (OpenCodeStateUnknownReason.Expired, "信息已过期"), (OpenCodeStateUnknownReason.Incomplete, "信息不完整") })
        {
            app.Snapshot = Snapshot(version, State("ses_child", reason: pair.Item1));
            app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
            Check(page.SessionStateHint.Text.Contains(pair.Item2) && !page.IsSelectedSessionWorkingIndicated, "hint.reason-without-invented-working");
        }
        var combined = OpenCodeStateUnknownReason.NotObserved | OpenCodeStateUnknownReason.Expired | OpenCodeStateUnknownReason.Incomplete;
        app.Snapshot = Snapshot(version, State("ses_child", reason: combined));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("尚未观测") && page.SessionStateHint.Text.Contains("信息已过期")
            && page.SessionStateHint.Text.Contains("信息不完整"), "hint.current-combined-reasons-preserved");
        app.Snapshot = Snapshot(version); app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("尚未观测"), "hint.missing-current-session-unknown");
        app.Snapshot = Snapshot(version, State("ses_child", false, false, reason: OpenCodeStateUnknownReason.None));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Length == 0 && page.SessionStateHint.Visibility == Visibility.Collapsed, "hint.known-false-clears");
        app.Snapshot = Snapshot(version, State("ses_child", true, null, true, reason: OpenCodeStateUnknownReason.Incomplete));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.IsSelectedSessionWorkingIndicated && page.SessionStateHint.Text.Contains("信息不完整"), "hint.partial-unknown-keeps-working");
        app.Snapshot = Snapshot(version, State("ses_child", false, null, reason: OpenCodeStateUnknownReason.None));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("信息不完整"), "hint.null-field-even-with-none-reason");
        app.Snapshot = Snapshot(version, State("ses_child", reason: OpenCodeStateUnknownReason.Expired));
        app.Emit("endpoint", 1, version++);
        page.Deactivate();
        page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Length == 0, "hint.unload-and-queued-refresh-clear");
        var replacement = new OpenCodePageSource("endpoint", 2);
        page.Bind(replacement, "ses_child"); app.CurrentOpenCodePageSource = replacement; app.Snapshot = null;
        page.Activate(); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("尚未观测") && !page.SessionStateHint.Text.Contains("已过期"), "hint.new-scope-no-old-reason");
        app.Emit("endpoint", 1, 999, Snapshot(999, State("ses_child", reason: OpenCodeStateUnknownReason.Expired)));
        page.DispatcherQueue.Drain();
        Check(!page.SessionStateHint.Text.Contains("已过期"), "hint.late-revision-cannot-revive");
        app.Snapshot = new("endpoint", 2, 1000, ImmutableArray.Create(State("ses_child", false, false, reason: OpenCodeStateUnknownReason.None)));
        app.Emit("endpoint", 2, 1000); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Length == 0, "hint.current-scope-reliable-state-clears");
        replacement.IsRevoked = true; page.Refresh();
        Check(page.SessionStateHint.Text.Length == 0, "hint.invalid-source-clears-current");
        page.Deactivate();
    }
}
