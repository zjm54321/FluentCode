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
        scrolling.ClearMessages();

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
        Console.WriteLine($"PASS conversation window: {checks} assertions; 2,000-row production view + production anchor/extent math; inert XAML only.");
    }
}
