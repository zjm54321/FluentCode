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
    private static (ProgressRing Ring, Ellipse Dot) Indicators(ListViewItem item)
    {
        var panel = (StackPanel)((Grid)item.Content!).Children[1];
        return ((ProgressRing)panel.Children[0], (Ellipse)panel.Children[1]);
    }
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
        TruthAndStableElements(); ScopeLifecycleAndQueues(); ListDecorationAndSubscriptions();
        UnknownHintsAndScopeIsolation();
        IndicatorTones();
        VerifyExpiredHintCleanup(unload: true);
        VerifyExpiredHintCleanup(unload: false);
        Console.WriteLine($"PASS: 5 session-state UI groups, {_assertions} assertions; real UI partial/State records; inert WinUI and App snapshot provider.");
        Console.WriteLine("BOUNDARIES: no real App mapping, source capability, WinUI layout/animation or list business algorithms are asserted here.");
    }
    private static void TruthAndStableElements()
    {
        var (app, page, _) = Fixture();
        var root = page.Add("ses_root"); var unknown = page.Add("ses_unknown"); page.SelectedItem = root;
        var message = page.MessageElement;
        app.Snapshot = Snapshot(1, State("ses_child", true), State("ses_root", true, true),
            State("ses_unknown", null, null, true, reason: OpenCodeStateUnknownReason.Expired));
        app.Emit("endpoint", 1, 1); page.DispatcherQueue.Drain();
        Check(!page.Items.ContainsKey("ses_child") && page.IsSelectedSessionWorkingIndicated, "truth.off-list-child-working");
        var firstWrapper = root.Content; var ring = Indicators(root).Ring;
        Check(ring.IsActive && Indicators(root).Dot.Visibility == Visibility.Visible, "truth.root-working-pending");
        Check(!Indicators(unknown).Ring.IsActive && Indicators(unknown).Dot.Visibility == Visibility.Collapsed,
            "truth.unknown-protection-no-indicators");
        app.Snapshot = Snapshot(2, State("ses_child", false), State("ses_root", false, false,
            completion: new("succeeded", 10), viewed: 9), State("ses_unknown", false, false, true));
        app.Emit("endpoint", 1, 2); page.DispatcherQueue.Drain();
        Check(!page.IsSelectedSessionWorkingIndicated,
            "truth.false-hides-current-animation");
        Check(!ring.IsActive && Indicators(root).Dot.Visibility == Visibility.Visible, "truth.unread-only-dot");
        Check(Indicators(unknown).Dot.Visibility == Visibility.Collapsed, "truth.protection-not-pending");
        app.Snapshot = Snapshot(3, State("ses_root", completion: new("succeeded", 10), viewed: 10));
        app.Emit("endpoint", 1, 3); page.DispatcherQueue.Drain();
        Check(Indicators(root).Dot.Visibility == Visibility.Collapsed, "truth.read-completion-no-dot");
        Check(ReferenceEquals(root.Content, firstWrapper) && ReferenceEquals(Indicators(root).Ring, ring)
            && ReferenceEquals(page.SelectedItem, root), "truth.row-selection-stable");
        Check(ReferenceEquals(page.MessageElement, message) && page.MessageExpanded, "truth.message-and-expansion-untouched");
        page.Deactivate();
    }

    private static void VerifyExpiredHintCleanup(bool unload)
    {
        var (app, page, source) = Fixture();
        var row = page.Add("ses_cleanup");
        app.Snapshot = Snapshot(1, State("ses_cleanup", reason: OpenCodeStateUnknownReason.Expired));
        app.Emit("endpoint", 1, 1); page.DispatcherQueue.Drain();
        Check(FullHint(Hint(row)).Contains("已过期"), "hint.cleanup-expired-positive-control");
        if (unload) page.Deactivate();
        else { source.IsRevoked = true; page.Refresh(); }
        Check(FullHint(Hint(row)).Length == 0 && Hint(row).Text.Length == 0
            && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(Hint(row)).Length == 0,
            "hint.cleanup-clears-tooltip-name-text");
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
    private static void ListDecorationAndSubscriptions()
    {
        var (app, page, _) = Fixture();
        app.Snapshot = Snapshot(1, State("ses_a", true, true), State("ses_b", true)); page.Refresh();
        var a = page.Add("ses_a"); var original = ((Grid)a.Content!).Children[0]; var wrapper = a.Content;
        var aRing = Indicators(a).Ring;
        var b = page.Add("ses_b");
        Check(aRing.IsActive && Indicators(b).Ring.IsActive, "list.new-items-use-existing-snapshot");
        page.Refresh(); page.Refresh();
        Check(ReferenceEquals(wrapper, a.Content) && ReferenceEquals(original, ((Grid)a.Content!).Children[0]),
            "list.no-repeated-wrapping");
        page.Remove("ses_a");
        Check(!aRing.IsActive, "list.removed-ring-stopped");
        var replacement = page.Add("ses_a");
        Check(!ReferenceEquals(replacement, a) && Indicators(replacement).Ring.IsActive, "list.new-instance-decorated");
        app.Snapshot = null; page.Refresh();
        Check(!Indicators(b).Ring.IsActive && Indicators(b).Dot.Visibility == Visibility.Collapsed,
            "list.null-snapshot-clears-row");
        Check(!page.IsSelectedSessionWorkingIndicated && page.RowTones.Count > 0,
            "list.null-snapshot-current-not-working-tones-reported");
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

        var (app, page, _) = Fixture();
        var row = page.Add("ses_tone");
        app.Snapshot = Snapshot(1, State("ses_tone", true, true)); app.Emit("endpoint", 1, 1); page.DispatcherQueue.Drain();
        var ring = Indicators(row).Ring;
        Check(page.RowTones[^1].Tone == OpenCodePage.SessionIndicatorTone.WorkingNeedsAction && ReferenceEquals(page.RowTones[^1].Ring, ring)
            && ring.IsActive, "tone.row-hook-receives-caution-for-working-pending");
        app.Snapshot = Snapshot(2, State("ses_tone", true, false)); app.Emit("endpoint", 1, 2); page.DispatcherQueue.Drain();
        Check(page.RowTones[^1].Tone == OpenCodePage.SessionIndicatorTone.Working && ring.IsActive, "tone.row-hook-accent-when-working");
        page.Deactivate();
    }

    private static TextBlock Hint(ListViewItem item) => (TextBlock)((StackPanel)((Grid)item.Content!).Children[1]).Children[2];
    private static string FullHint(TextBlock hint) => ToolTipService.GetToolTip(hint) as string ?? string.Empty;

    private static void UnknownHintsAndScopeIsolation()
    {
        var (app, page, source) = Fixture();
        var row = page.Add("ses_root");
        Check(page.SessionStateHint.Text.Contains("尚未观测") && FullHint(Hint(row)).Contains("尚未观测"), "hint.no-snapshot-neutral-unknown");
        var wrapper = row.Content; var hint = Hint(row); var ring = Indicators(row).Ring;
        long version = 1;
        foreach (var pair in new[]
        {
            (OpenCodeStateUnknownReason.NotObserved, "尚未观测"),
            (OpenCodeStateUnknownReason.Expired, "信息已过期"),
            (OpenCodeStateUnknownReason.Incomplete, "信息不完整"),
        })
        {
            app.Snapshot = Snapshot(version, State("ses_child", reason: pair.Item1), State("ses_root", reason: pair.Item1));
            app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
            Check(page.SessionStateHint.Text.Contains(pair.Item2) && FullHint(hint).Contains(pair.Item2), "hint.each-reason-current-and-row");
            Check(!page.IsSelectedSessionWorkingIndicated && !ring.IsActive && Indicators(row).Dot.Visibility == Visibility.Collapsed,
                "hint.reason-not-working-or-pending");
        }
        var combined = OpenCodeStateUnknownReason.NotObserved | OpenCodeStateUnknownReason.Expired | OpenCodeStateUnknownReason.Incomplete;
        app.Snapshot = Snapshot(version, State("ses_child", reason: combined), State("ses_root", reason: combined));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("尚未观测") && page.SessionStateHint.Text.Contains("信息已过期")
            && page.SessionStateHint.Text.Contains("信息不完整") && FullHint(hint) == page.SessionStateHint.Text, "hint.combined-reasons-preserved");
        Check(hint.Text == "多项未知" && hint.MaxWidth == 72 && hint.TextWrapping == TextWrapping.NoWrap
            && hint.TextTrimming == TextTrimming.CharacterEllipsis, "hint.compact-bounded-single-line");
        Check(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(hint) == FullHint(hint), "hint.automation-preserves-full-combination");
        app.Snapshot = Snapshot(version); app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("尚未观测") && FullHint(hint).Contains("尚未观测") && hint.Text == "未观测", "hint.missing-session-unknown");
        app.Snapshot = Snapshot(version, State("ses_child", false, false, reason: OpenCodeStateUnknownReason.None),
            State("ses_root", false, false, reason: OpenCodeStateUnknownReason.None));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Length == 0 && page.SessionStateHint.Visibility == Visibility.Collapsed
            && hint.Text.Length == 0 && hint.Visibility == Visibility.Collapsed, "hint.reliable-false-clears-caption");
        app.Snapshot = Snapshot(version, State("ses_child", true, null, true, reason: OpenCodeStateUnknownReason.Incomplete),
            State("ses_root", true, null, true, reason: OpenCodeStateUnknownReason.Incomplete));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.IsSelectedSessionWorkingIndicated && ring.IsActive && FullHint(hint).Contains("信息不完整") && hint.Text == "不完整"
            && Indicators(row).Dot.Visibility == Visibility.Collapsed, "hint.partial-unknown-keeps-working-no-fake-dot");
        Check(ReferenceEquals(wrapper, row.Content) && ReferenceEquals(hint, Hint(row)) && page.MessageExpanded,
            "hint.stable-row-and-existing-content");
        app.Snapshot = Snapshot(version, State("ses_child", false, null, reason: OpenCodeStateUnknownReason.None));
        app.Emit("endpoint", 1, version++); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("信息不完整"), "hint.null-field-even-with-none-reason");
        app.Snapshot = Snapshot(version, State("ses_child", reason: OpenCodeStateUnknownReason.Expired));
        app.Emit("endpoint", 1, version++);
        page.Deactivate(); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Length == 0 && hint.Text.Length == 0, "hint.unload-and-queued-refresh-clear");
        var replacement = new OpenCodePageSource("endpoint", 2);
        page.Bind(replacement, "ses_child"); app.CurrentOpenCodePageSource = replacement; app.Snapshot = null;
        page.Activate(); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Contains("尚未观测") && !page.SessionStateHint.Text.Contains("已过期"),
            "hint.new-scope-no-old-reason");
        app.Emit("endpoint", 1, 999, Snapshot(999, State("ses_child", reason: OpenCodeStateUnknownReason.Expired)));
        page.DispatcherQueue.Drain();
        Check(!page.SessionStateHint.Text.Contains("已过期"), "hint.late-revision-payload-cannot-revive");
        app.Snapshot = new("endpoint", 2, 1000, ImmutableArray.Create(State("ses_child", false, false, reason: OpenCodeStateUnknownReason.None)));
        app.Emit("endpoint", 2, 1000); page.DispatcherQueue.Drain();
        Check(page.SessionStateHint.Text.Length == 0, "hint.current-scope-reliable-state-clears");
        source.IsRevoked = true; replacement.IsRevoked = true; page.Refresh();
        Check(page.SessionStateHint.Text.Length == 0 && Hint(row).Text.Length == 0, "hint.invalid-source-clears-all");
        page.Deactivate();
    }
}
