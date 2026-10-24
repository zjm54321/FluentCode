using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static class Windowing
{
    internal static void Run()
    {
        var checks = 0;
        void Check(bool value, string label)
        { checks++; if (!value) throw new InvalidOperationException("window." + label); }

        var history = Enumerable.Range(0, 2000).Select(i => new OpenCodeMessage
        { Id = "msg_" + i, Type = OpenCodeMessageTypes.User, Text = "row " + i }).ToList();
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        view.ApplyMessages(history);
        Check(view.RealizedRowCount > 0 && view.RealizedRowCount <= 40, "2000-history-bounded-realized-row-count");
        Check(ConversationViewTestProbe.Records.Count(r => r.Operation == "Construct" && r.Subject is OpenCodeRichTextPresenter) <= 40,
            "unrealized-rows-do-not-construct-body-presenters");
        Check(view.ContainsRenderedMessage("msg_0") && !view.ContainsRenderedMessage("msg_1999"), "rendered-membership-excludes-placeholders");
        var first = view.Children[0];
        history.Add(new() { Id = "msg_2000", Type = OpenCodeMessageTypes.User, Text = "stream tail" });
        view.ApplyMessages(history);
        Check(view.RealizedRowCount <= 40 && ReferenceEquals(first, view.Children[0]), "streaming-does-not-replace-older-reading-row");
        view.ClearMessages();
        Check(view.RealizedRowCount == 0, "clear-releases-realized-rows");

        // Real production extent/anchor math. Measurements intentionally include shrinkage.
        var window = new OpenCodeConversationWindow();
        var keys = history.Select(m => m.Id!).ToList();
        window.SetKeys(keys);
        const double offset = 80017;
        var anchor = window.Capture(offset);
        Check(anchor is { Key: "msg_1000", Offset: 17 }, "capture-key-and-intra-row-offset");
        keys.InsertRange(0, Enumerable.Range(0, 200).Select(i => "older_" + i));
        window.SetKeys(keys);
        Check(window.Restore(anchor, -1) == offset + 16000, "prepend-keeps-item-and-offset");
        window.Measure("older_0", 12);
        window.Measure("older_1", 140);
        window.Measure("msg_0", 20);
        Check(window.Restore(anchor, -1) == offset + 16000 - 68 + 60 - 60, "measure-shrink-and-growth-above-anchor-both-corrected");
        var stable = window.Restore(anchor, -1);
        window.Measure("msg_1999", 800);
        Check(window.Restore(anchor, -1) == stable, "growth-below-reader-does-not-move-anchor");
        var range = window.Select(stable, 800, 800);
        Check(range.End - range.Start <= 40 && range.Start > 0 && range.End < keys.Count, "middle-window-bounded");
        var tail = window.Select(Math.Max(0, window.Extent - 800), 800, 800);
        Check(tail.End == keys.Count && tail.End - tail.Start <= 40, "bottom-follow-realizes-tail");
        keys.Add("msg_stream"); window.SetKeys(keys);
        window.Measure("msg_stream", 1600);
        tail = window.Select(Math.Max(0, window.Extent - 800), 800, 800);
        Check(tail.End == keys.Count && keys[tail.End - 1] == "msg_stream", "bottom-follow-absorbs-streaming-height");
        Check(window.Restore(anchor, -1) == stable, "streaming-below-reader-keeps-key-and-offset");

        // Exercise the real view's realization/measurement/scroll owner with deterministic native
        // boundary stimuli. This is not a claim about native WinUI layout or input dispatch.
        var scrolling = new OpenCodeConversationView { IsLoaded = true };
        var viewport = new ScrollViewer { ViewportHeight = 800 };
        scrolling.TestTransform = _ => new GeneralTransform(-viewport.VerticalOffset);
        var heights = new Dictionary<string, double>();
        var entriesField = typeof(OpenCodeConversationView).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var windowField = typeof(OpenCodeConversationView).GetField("_window", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actualWindow = (OpenCodeConversationWindow)windowField.GetValue(scrolling)!;
        viewport.LayoutForTest = () =>
        {
            foreach (var entry in (IEnumerable)entriesField.GetValue(scrolling)!)
            {
                var type = entry.GetType();
                var element = (FrameworkElement?)type.GetProperty("Element")!.GetValue(entry);
                if (element is null) continue;
                var key = (string)type.GetProperty("Key")!.GetValue(entry)!;
                element.ActualHeight = (heights.GetValueOrDefault(key, 80)) - element.Margin.Top - element.Margin.Bottom;
            }
            var extent = scrolling.Padding.Top + scrolling.Padding.Bottom;
            foreach (var element in scrolling.Children.OfType<FrameworkElement>())
                if (element.Visibility == Visibility.Visible)
                    extent += element is Border { Child: null } ? element.Height
                        : element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
            viewport.ScrollableHeight = Math.Max(0, extent - viewport.ViewportHeight);
        };
        var live = history.ToList();
        scrolling.ApplyMessages(live);
        void Settle() { for (var pass = 0; pass < 4; pass++) scrolling.RefreshViewport(viewport); }
        Settle();
        var selectedBubble = scrolling.Children.OfType<Border>().First(b => b.Child is not null);
        var selection = new TextBlock { Text = "selectable row", SelectedText = "selectable" };
        ((Microsoft.UI.Xaml.Controls.Panel)selectedBubble.Child!).Children.Add(selection);
        viewport.ChangeView(null, 80021, null, true);
        heights["msg_995"] = 12;
        Settle();
        Check(scrolling.Children.Contains(selectedBubble), "native-text-selection-pins-row-outside-window");
        selection.SelectedText = string.Empty;
        Microsoft.UI.Xaml.Input.FocusManager.FocusedForTest = selection;
        Settle();
        Check(scrolling.Children.Contains(selectedBubble), "keyboard-focus-pins-row-outside-window");
        Microsoft.UI.Xaml.Input.FocusManager.FocusedForTest = null;
        Settle();
        Check(!scrolling.Children.Contains(selectedBubble), "row-recycles-after-selection-and-focus-release");
        Check(scrolling.RealizedRowCount <= 42, "real-view-middle-realization-bounded");
        Check(actualWindow.Capture(viewport.VerticalOffset - scrolling.Padding.Top) is { Key: "msg_1000", Offset: 17 },
            "real-view-estimate-shrink-keeps-key-and-offset");
        scrolling.PreserveReadingAnchor(viewport, () => true);
        live.InsertRange(0, Enumerable.Range(0, 200).Select(i => new OpenCodeMessage
        { Id = "older_" + i, Type = OpenCodeMessageTypes.User, Text = "older row" }));
        scrolling.ApplyMessages(live); Settle();
        Check(actualWindow.Capture(viewport.VerticalOffset - scrolling.Padding.Top) is { Key: "msg_1000", Offset: 17 },
            "real-view-prepend-restores-anchor");
        scrolling.FollowToBottom(viewport, () => true); Settle();
        Check(Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) <= 0.5 && scrolling.ContainsRenderedMessage("msg_2000"),
            "real-view-bottom-follow-realizes-tail");
        heights["msg_2000"] = 500;
        live[^1] = live[^1] with { Text = "streaming enlarged tail" };
        scrolling.ApplyMessages(live); Settle();
        Check(Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) <= 0.5, "real-view-streaming-follows-measurement-growth");
        viewport.ChangeView(null, 10021, null, true); Settle();
        var reading = actualWindow.Capture(viewport.VerticalOffset - scrolling.Padding.Top);
        live[^1] = live[^1] with { Text = "streaming while reading history" };
        scrolling.ApplyMessages(live); Settle();
        Check(actualWindow.Capture(viewport.VerticalOffset - scrolling.Padding.Top) == reading,
            "real-view-user-scroll-cancels-follow-and-streaming-does-not-jump");
        Check(scrolling.RealizedRowCount <= 42, "real-view-remains-bounded-after-prepend-and-streaming");
        var beforeFence = viewport.VerticalOffset;
        scrolling.FollowToBottom(viewport, () => false); Settle();
        Check(viewport.VerticalOffset == beforeFence, "stale-owner-cannot-scroll");
        viewport.ChangeView(null, 20021, null, true); Settle();
        Check(scrolling.RealizedRowCount <= 42 && viewport.VerticalOffset == 20021,
            "new-user-scroll-supersedes-stale-owner");

        // Native composition pans independently of the UI-thread VerticalOffset. Vary the
        // viewport transform between ViewChanged and layout, with NO height/extent change:
        // the old owner counter-scrolled on every frame (the real-app wheel loop).
        viewport.Content = new Grid();
        double compositorLead = 0;
        scrolling.TestTransform = target => new GeneralTransform(ReferenceEquals(target, viewport)
            ? -viewport.VerticalOffset - compositorLead : 0);
        viewport.ProgrammaticOffsetsForTest.Clear();
        for (var frame = 0; frame < 12; frame++)
        {
            var nativeOffset = 20021 - (frame + 1) * 173;
            compositorLead = 5;
            viewport.ScrollForTest(nativeOffset, intermediate: true);
            compositorLead = 51;
            scrolling.RefreshViewport(viewport);
            Check(viewport.VerticalOffset == nativeOffset && viewport.ProgrammaticOffsetsForTest.Count == 0,
                "inertial-frame-" + frame + "-never-counter-scrolls-to-compositor-transform");
        }
        viewport.ScrollForTest(viewport.VerticalOffset, intermediate: false);
        compositorLead = 19;
        Settle();
        Check(viewport.ProgrammaticOffsetsForTest.Count == 0, "inertia-final-layout-does-not-replay-stale-pan-anchor");

        // Deferred measurement above the live reader must not cancel native inertia either.
        var panWindow = (OpenCodeConversationWindow)windowField.GetValue(scrolling)!;
        var panAnchor = panWindow.Capture(viewport.VerticalOffset - scrolling.Padding.Top)!.Value;
        var precedingKey = live[live.FindIndex(m => m.Id == panAnchor.Key) - 1].Id!;
        heights[precedingKey] = 143;
        viewport.ScrollForTest(viewport.VerticalOffset - 7, intermediate: true);
        scrolling.RefreshViewport(viewport);
        Check(viewport.ProgrammaticOffsetsForTest.Count == 0, "lazy-height-during-inertia-never-issues-ChangeView");
        viewport.ScrollForTest(viewport.VerticalOffset, intermediate: false);
        Settle();
        Check(viewport.ProgrammaticOffsetsForTest.Count == 0, "lazy-height-pan-end-adopts-live-reader-instead-of-snapping-back");
        scrolling.ClearMessages();

        // WG1: a manual running-group choice belongs to the retained part key, not its realized Expander.
        var groupHistory = history.ToList();
        groupHistory.Add(System.Text.Json.JsonSerializer.Deserialize<OpenCodeMessage>("""
            {"id":"wg1_message","type":"assistant","time":{"created":1700000000000},"content":[
              {"id":"wg1_first","type":"tool","name":"read","state":{"status":"completed","input":{"path":"first.cs"}}},
              {"id":"wg1_current","type":"tool","name":"read","state":{"status":"running","input":{"path":"current.cs"}}}
            ]}
            """, OpenCodeJson.Options)!);
        scrolling.ApplyMessages(groupHistory);
        scrolling.FollowToBottom(viewport, () => true); Settle();
        OpenCodeToolGroupView CurrentGroup() => ConversationViewTestProbe.Records
            .Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeToolGroupView>()
            .Last(g => g.GroupId == "wg1_first");
        var originalGroup = CurrentGroup();
        originalGroup.ToggleForTest();
        Check(!originalGroup.IsExpanded && originalGroup.Summary!.IsActive, "WG1.running-group-manually-collapsed");
        viewport.ChangeView(null, 21, null, true); Settle();
        Check(originalGroup.DisposeCount == 1, "WG1.running-group-unrealized-away-from-reader");
        scrolling.FollowToBottom(viewport, () => true); Settle();
        var realizedGroup = CurrentGroup();
        Check(!ReferenceEquals(realizedGroup, originalGroup) && !realizedGroup.IsExpanded && realizedGroup.Summary!.IsActive,
            "WG1.manual-collapse-survives-window-re-realization");
        Check(realizedGroup.CurrentStep!.Contains("current.cs", StringComparison.Ordinal), "WG1.re-realized-collapsed-group-keeps-current-step");
        scrolling.ClearMessages();

        // Observe actual child geometry after EACH transaction, not the cached height model
        // after four settling passes. Native ViewChanged also fires for extent-only changes.
        var jitterView = new OpenCodeConversationView { IsLoaded = true };
        var jitterViewport = new ScrollViewer { ViewportHeight = 800 };
        jitterView.TestTransform = _ => new GeneralTransform(-jitterViewport.VerticalOffset);
        var jitterRows = Enumerable.Range(1000, 2000).Select(i => new OpenCodeMessage
        { Id = "jitter_" + i, Type = OpenCodeMessageTypes.User, Text = "row " + i }).ToList();
        var jitterHeights = new Dictionary<string, double>();
        var lateLayoutPass = 0;
        var deliverLateHeight = false;
        double lateHeight = 230;
        var viewportEvents = new List<bool>();
        jitterView.HistoryViewportChanged += restoring => viewportEvents.Add(restoring);
        IEnumerable<(string Key, FrameworkElement Element)> JitterEntries()
            => ((IEnumerable)entriesField.GetValue(jitterView)!).Cast<object>().Select(entry =>
                ((string)entry.GetType().GetProperty("Key")!.GetValue(entry)!,
                 (FrameworkElement)entry.GetType().GetProperty("Element")!.GetValue(entry)!)).Where(row => row.Item2 is not null);
        jitterViewport.LayoutForTest = () =>
        {
            if (deliverLateHeight && ++lateLayoutPass == 2) jitterHeights["jitter_older_195"] = lateHeight;
            foreach (var (key, element) in JitterEntries())
                element.ActualHeight = jitterHeights.GetValueOrDefault(key, 80) - element.Margin.Top - element.Margin.Bottom;
            jitterViewport.ScrollableHeight = jitterView.Padding.Top + jitterView.Padding.Bottom
                + jitterView.Children.OfType<FrameworkElement>().Sum(Height) - jitterViewport.ViewportHeight;
        };
        static double Height(FrameworkElement element) => element.Visibility == Visibility.Collapsed ? 0
            : element is Border { Child: null } ? element.Height : element.ActualHeight + element.Margin.Top + element.Margin.Bottom;
        double ScreenOffset()
        {
            var model = (OpenCodeConversationWindow)windowField.GetValue(jitterView)!;
            var top = jitterView.Padding.Top;
            foreach (var entry in ((IEnumerable)entriesField.GetValue(jitterView)!).Cast<object>())
            {
                var key = (string)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
                if (key == "jitter_1000") break;
                var element = (FrameworkElement?)entry.GetType().GetProperty("Element")!.GetValue(entry);
                top += element is null ? model.Height(key) : Height(element);
            }
            return top - jitterViewport.VerticalOffset;
        }
        jitterView.ApplyMessages(jitterRows);
        jitterView.RefreshViewport(jitterViewport);
        jitterViewport.ChangeView(null, 21, null, true);
        for (var pass = 0; pass < 4; pass++) jitterView.RefreshViewport(jitterViewport);
        var anchorScreen = ScreenOffset();
        Check(viewportEvents.Contains(false), "real-user-offset-change-is-published-to-pagination");
        viewportEvents.Clear();
        double maxJitter = 0;
        void Observe(string phase)
        {
            var delta = Math.Abs(ScreenOffset() - anchorScreen);
            maxJitter = Math.Max(maxJitter, delta);
            Console.WriteLine($"ANCHOR {phase}: screen={ScreenOffset():F2} baseline={anchorScreen:F2} displacement={delta:F2} DIP");
        }
        jitterView.PreserveReadingAnchor(jitterViewport, () => true);
        // Newly realized rows above the anchor differ from their 80-DIP estimates.
        jitterHeights["jitter_older_190"] = 12;
        jitterHeights["jitter_older_191"] = 140;
        jitterViewport.DeferChangeForTest = true;
        jitterRows.InsertRange(0, Enumerable.Range(0, 200).Select(i => new OpenCodeMessage
        { Id = "jitter_older_" + i, Type = OpenCodeMessageTypes.User, Text = "older row" }));
        jitterView.ApplyMessages(jitterRows);
        // WinUI ChangeView completes asynchronously. Extent notifications can arrive first;
        // let queued layout run before the requested viewport has been committed.
        jitterViewport.NotifyExtentForTest();
        jitterView.RefreshViewport(jitterViewport);
        var pendingAnchorRetained = jitterView.ContainsRenderedMessage("jitter_1000");
        Console.WriteLine($"ANCHOR pending-ChangeView retains visible row: {pendingAnchorRetained}");
        jitterViewport.CommitChangeForTest();
        jitterViewport.DeferChangeForTest = false;
        Observe("prepend");
        for (var pass = 0; pass < 3; pass++) { jitterView.RefreshViewport(jitterViewport); Observe("prepend-settle-" + pass); }
        // Deferred Markdown/tool layout changes without moving the user's viewport.
        deliverLateHeight = true;
        jitterViewport.NotifyExtentForTest();
        jitterView.RefreshViewport(jitterViewport);
        Observe("late-layout");
        for (var pass = 0; pass < 3; pass++) { jitterView.RefreshViewport(jitterViewport); Observe("late-settle-" + pass); }
        // An unrelated extent notification must not re-capture an outdated height model.
        jitterViewport.NotifyExtentForTest();
        jitterView.RefreshViewport(jitterViewport);
        Observe("extent-only");
        lateLayoutPass = 0;
        lateHeight = 20;
        jitterView.RefreshViewport(jitterViewport);
        Observe("late-shrink");
        for (var page = 0; page < 3; page++)
        {
            jitterView.PreserveReadingAnchor(jitterViewport, () => true);
            jitterRows.InsertRange(0, Enumerable.Range(0, 25).Select(i => new OpenCodeMessage
            { Id = $"jitter_page_{page}_{i}", Type = OpenCodeMessageTypes.User, Text = "older page" }));
            jitterView.ApplyMessages(jitterRows);
            Observe("additional-page-" + page);
        }
        Console.WriteLine($"ANCHOR maximum transaction displacement: {maxJitter:F2} DIP");
        Check(maxJitter <= 1, "each-prepend-and-lazy-layout-transaction-keeps-actual-anchor-within-one-pixel");
        Check(pendingAnchorRetained, "extent-notification-before-ChangeView-ack-does-not-recycle-visible-anchor");
        Check(viewportEvents.Count > 0 && viewportEvents.All(restoring => restoring),
            "extent-prepend-shrink-and-ChangeView-ack-events-are-not-user-pagination-triggers");
        jitterView.ClearMessages();

        ConversationViewTestProbe.Reset();
        var chained = new OpenCodeConversationView();
        var calls = Enumerable.Range(0, 2000).Select(i => new OpenCodeMessage
        {
            Id = "step_" + i, Type = OpenCodeMessageTypes.Assistant,
            Time = new() { Created = 1700000000000d + i, Completed = 1700000000001d + i },
            Content = new() { new() { Id = "call_" + i, Type = OpenCodePartTypes.Tool, Name = "read",
                State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "completed", input = new { path = "file.cs" } }) } },
        }).ToList();
        chained.ApplyMessages(calls);
        var chainGroups = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct")
            .Select(r => r.Subject).OfType<OpenCodeToolGroupView>().ToArray();
        var chainLeader = chainGroups.Single(g => g.HeaderVisible);
        Check(chainLeader.Summary is { SuccessCount: 2000, IsActive: false }, "offscreen-chain-leaves-contribute-to-leader-summary");
        Check(chained.RealizedRowCount <= 40 && !ConversationViewTestProbe.Records.Any(r => r.Operation == "Construct" && r.Subject is OpenCodeToolPartView),
            "collapsed-long-chain-no-tool-controllers");
        chainLeader.ToggleForTest();
        Check(chainGroups.All(g => g.IsExpanded) && chained.RealizedRowCount <= 40,
            "b18-expansion-propagates-through-full-logical-chain");
        Check(ConversationViewTestProbe.Records.Count(r => r.Operation == "Construct" && r.Subject is OpenCodeToolPartView) <= 40,
            "expanding-chain-realizes-only-windowed-rows");
        chainLeader.ToggleForTest();
        chained.ApplyMessages(calls);
        Check(!chainLeader.IsExpanded, "manual-chain-collapse-survives-snapshot");
        chained.ClearMessages();

        ConversationViewTestProbe.Reset();
        var single = new OpenCodeConversationView();
        single.ApplyMessages(new[] { calls[0] });
        var singleGroup = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct")
            .Select(r => r.Subject).OfType<OpenCodeToolGroupView>().Single();
        Check(!ConversationViewTestProbe.Records.Any(r => r.Subject is OpenCodeToolPartView), "single-leaf-controller-lazy-before-interaction");
        singleGroup.EnterLeaf!(); singleGroup.OpenLeaf!();
        var singleTool = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct")
            .Select(r => r.Subject).OfType<OpenCodeToolPartView>().Single();
        Check(ReferenceEquals(singleTool.DetailAnchorOverride, singleGroup.DetailAnchor)
            && ConversationViewTestProbe.Records.Any(r => r.Operation == "HoverLeaf" && ReferenceEquals(r.Subject, singleTool))
            && ConversationViewTestProbe.Records.Any(r => r.Operation == "OpenLeaf" && ReferenceEquals(r.Subject, singleTool)),
            "single-leaf-hover-click-share-on-demand-controller-and-header-anchor");
        var staleOpen = singleGroup.OpenLeaf;
        single.ClearMessages();
        staleOpen!();
        Check(ConversationViewTestProbe.Records.Count(r => r.Operation == "Construct" && r.Subject is OpenCodeToolPartView) == 1,
            "disposed-lazy-detail-callback-cannot-recreate-controller");
        // TL2: same call projects into stable distinct leaves, including offscreen chain accounting.
        var patchPart = new OpenCodeContentPart
        {
            Id = "call_patch", Type = OpenCodePartTypes.Tool, Name = "patch",
            State = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                status = "completed", metadata = new { files = Enumerable.Range(0, 16).Select(i => new
                { file = $"src/file{i}.cs", status = "modified", additions = i, deletions = 1, patch = "@@ -1 +1 @@\n-old\n+new" }).ToArray() },
            }),
        };
        var patchMessage = calls[0] with { Id = "msg_patch", Content = new() { patchPart } };
        ConversationViewTestProbe.Reset();
        var patches = new OpenCodeConversationView();
        patches.ApplyMessages(new[] { patchMessage });
        var patchGroup = ConversationViewTestProbe.Records.Select(r => r.Subject).OfType<OpenCodeToolGroupView>().Distinct().Single();
        Check(patchGroup.Summary is { SuccessCount: 16 } && patchGroup.OpenLeaf is null, "multi-file-call-counts-sixteen-not-one-call-or-single-leaf");
        patchGroup.ToggleForTest();
        var fileViews = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeToolPartView>().ToArray();
        Check(fileViews.Length == 16 && fileViews.Select(v => v.FileIndex).SequenceEqual(Enumerable.Range(0, 16).Select(i => (int?)i)),
            "all-sixteen-rows-have-own-file-specific-controller");
        Check(fileViews.All(v => v.Element.Visibility == Visibility.Visible), "call-dedup-does-not-hide-file-rows");
        patches.ApplyMessages(new[] { patchMessage with { Content = new() { patchPart with { } } } });
        Check(fileViews.All(v => !v.IsDisposed) && ConversationViewTestProbe.Records.Count(r => r.Operation == "Construct" && r.Subject is OpenCodeToolPartView) == 16,
            "file-index-keys-retain-controller-identity-across-snapshots");
        patches.ClearMessages();
        Check(fileViews.All(v => v.IsDisposed), "file-rows-disposed-together");

        ConversationViewTestProbe.Reset();
        var offscreenPatches = new OpenCodeConversationView();
        var patchHistory = calls.ToList(); patchHistory[^1] = patchMessage;
        offscreenPatches.ApplyMessages(patchHistory);
        var patchLeader = ConversationViewTestProbe.Records.Select(r => r.Subject).OfType<OpenCodeToolGroupView>().Distinct().Single(g => g.HeaderVisible);
        Check(patchLeader.Summary is { SuccessCount: 2015 }, "offscreen-sixteen-file-call-uses-same-per-file-summary");
        offscreenPatches.ClearMessages();

        ConversationViewTestProbe.Reset();
        var transitioning = new OpenCodeConversationView();
        var runningPatch = patchPart with { State = System.Text.Json.JsonSerializer.SerializeToElement(new { status = "running", input = new { patchText = "*** Begin Patch" } }) };
        transitioning.ApplyMessages(new[] { patchMessage with { Content = new() { runningPatch } } });
        transitioning.ApplyMessages(new[] { patchMessage });
        Check(ConversationViewTestProbe.Records.Select(r => r.Subject).OfType<OpenCodeToolGroupView>().Distinct().Any(g => g.Summary is { SuccessCount: 16 }),
            "streaming-aggregate-transitions-to-file-rows");
        transitioning.ClearMessages();
        Console.WriteLine($"PASS conversation window: {checks} assertions; 2,000-row production view + production anchor/extent math; inert XAML only.");
    }
}
