using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class StreamingApplyTests
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    private static OpenCodeMessage Assistant(string text) => new() { Id = "msg_499", Type = "assistant", Time = new() { Created = 500 },
        Content = new() { new() { Type = "text", Id = "part_stream", Text = text } } };

    [ModuleInitializer]
    internal static void Run()
    {
        ConversationViewTestProbe.Reset();
        var messages = Enumerable.Range(0, 499).Select(index => new OpenCodeMessage { Id = "msg_" + index,
            Type = index % 2 == 0 ? "user" : "assistant", Text = index % 2 == 0 ? "question" : null,
            Time = new() { Created = index + 1 }, Content = index % 2 == 0 ? null : new() { new() { Type = "text", Text = "reply" } } }).ToList();
        messages.Add(Assistant("initial"));
        var view = new OpenCodeConversationView { IsLoaded = true };
        var viewport = new ScrollView { Content = view, ViewportHeight = 1_000_000 };
        viewport.LayoutForTest = () =>
        {
            foreach (var child in view.Children.OfType<FrameworkElement>()) child.ActualHeight = 40;
            view.RaiseLayoutUpdatedForTest();
        };
        view.RefreshViewport(viewport);
        view.ApplyMessages(messages);
        view.RefreshViewport(viewport);
        Microsoft.UI.Xaml.Media.CompositionTarget.Frame();
        var owner = new object();
        messages[^1] = Assistant("full baseline delta");
        var requests = view.MessageFingerprintRequestCount;
        var chains = view.StepChainEntriesVisited;
        var layouts = view.UpdateLayoutCount;
        view.ApplyMessages(messages);
        var fullRequests = view.MessageFingerprintRequestCount - requests;
        var fullChains = view.StepChainEntriesVisited - chains;
        var fullLayouts = view.UpdateLayoutCount - layouts;
        view.BindIncrementalSnapshot(owner, 1);
        var realized = view.RealizedRowCount;
        var fullApplies = view.FullApplyCount;
        var computed = OpenCodeConversationView.MessageFingerprintsComputed;
        requests = view.MessageFingerprintRequestCount; chains = view.StepChainEntriesVisited; layouts = view.UpdateLayoutCount;
        messages[^1] = Assistant("full baseline delta + streaming delta");
        Check(view.TryApplyMessageChanges(new[] { messages[^1] }, owner, 1, 2), "Known latest-turn text update takes fast path.");
        Check(view.FastApplyCount == 1 && view.FullApplyCount == fullApplies, "Fast publication never invokes the full reconciler.");
        Check(view.MessageFingerprintRequestCount - requests == 1 && OpenCodeConversationView.MessageFingerprintsComputed - computed == 1,
            "Only the changed message is fingerprinted/serialized.");
        Check(view.StepChainEntriesVisited - chains == 1, "Only the affected latest turn is chained.");
        Check(view.RealizedRowCount == realized && view.UpdateLayoutCount - layouts <= 1,
            "An unchanged realized set issues at most one forced layout per batch.");
        Console.WriteLine($"PERF conversation 500 messages full/fast: fingerprint_requests={fullRequests}/{view.MessageFingerprintRequestCount - requests} step_entries={fullChains}/{view.StepChainEntriesVisited - chains} layouts={fullLayouts}/{view.UpdateLayoutCount - layouts}");
        var fast = Projection(view);
        view.ApplyMessages(messages);
        Check(fast.SequenceEqual(Projection(view)), "Fast entries equal a full reapply of the same snapshot.");
        Check(!view.TryApplyMessageChanges(new[] { messages[^1] }, owner, 2, 3), "A full REST/reconnect/pagination/revert paint retires the delta baseline.");
        view.BindIncrementalSnapshot(owner, 3);
        Check(!view.TryApplyMessageChanges(new[] { messages[1] }, owner, 3, 4), "Historical turn updates fall back before mutating entries.");
        Check(!view.TryApplyMessageChanges(new[] { messages[^1] }, owner, 2, 4), "A stale reducer version cannot cross a publication.");
        Check(!view.TryApplyMessageChanges(new[] { messages[^1] with { Content = new() } }, owner, 3, 4), "Part membership changes require full reconciliation.");
        var windowCalls = view.UpdateWindowCount;
        for (var i = 0; i < 20; i++) view.RaiseLayoutUpdatedForTest();
        Check(view.UpdateWindowCount == windowCalls, "LayoutUpdated never performs immediate window work.");
        Microsoft.UI.Xaml.Media.CompositionTarget.Frame();
        Check(view.UpdateWindowCount == windowCalls + 1, "Twenty layout notifications coalesce into one rendered-frame update.");
        var fallback = view.FullApplyCount;
        view.ApplyMessages(messages.Take(400).ToList());
        Check(view.FullApplyCount == fallback + 1 && Projection(view).Length == 400, "Rollback/removal uses the full path.");
        view.ApplyMessages(messages);
        Check(Projection(view).Length == 500, "A pagination/reconnect full snapshot restores identical membership.");
        view.IsLoaded = false;
        view.RaiseUnloadedForTest();
        Anchors(messages);
        ConversationViewTestProbe.Reset();
        Console.WriteLine($"PASS streaming apply: {_checks} assertions (500-message sparse updates, full fallback, frame/layout cadence).");
    }

    private static void Anchors(List<OpenCodeMessage> messages)
    {
        var view = new OpenCodeConversationView { TestTop = 0 };
        var viewport = new ScrollView { Content = view, ViewportHeight = 480 };
        var heights = messages.ToDictionary(message => message.Id!, _ => 80d, StringComparer.Ordinal);
        viewport.LayoutForTest = () =>
        {
            foreach (var entry in Entries(view))
            {
                var type = entry.GetType();
                var element = type.GetProperty("Element")!.GetValue(entry) as FrameworkElement;
                if (element is null) continue;
                var key = (string)type.GetProperty("Key")!.GetValue(entry)!;
                element.ActualHeight = Math.Max(0, heights.GetValueOrDefault(key, 80) - element.Margin.Top - element.Margin.Bottom);
            }
            viewport.ScrollableHeight = Math.Max(0, view.Padding.Top + view.Padding.Bottom
                + view.Children.OfType<FrameworkElement>().Sum(element => (element.Height > 0 ? element.Height : element.ActualHeight)
                    + element.Margin.Top + element.Margin.Bottom) - viewport.ViewportHeight);
            view.RaiseLayoutUpdatedForTest();
        };
        view.ApplyMessages(messages);
        view.IsLoaded = true;
        view.FollowToBottom(viewport, () => true);
        view.RefreshViewport(viewport);
        Microsoft.UI.Xaml.Media.CompositionTarget.Frame();
        Check(Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) < 0.5, "Virtualized initial realization follows bottom.");
        var owner = new object();
        view.BindIncrementalSnapshot(owner, 1);
        heights["msg_499"] = 160;
        messages[^1] = Assistant("growing bottom reply");
        Check(view.TryApplyMessageChanges(new[] { messages[^1] }, owner, 1, 2), "Virtualized bottom growth takes fast path.");
        Check(Math.Abs(viewport.VerticalOffset - viewport.ScrollableHeight) < 0.5, "Streaming height growth remains at the live bottom.");
        viewport.ScrollForTest(20_000, intermediate: false);
        view.RefreshViewport(viewport);
        Microsoft.UI.Xaml.Media.CompositionTarget.Frame();
        var readingOffset = viewport.VerticalOffset;
        heights["msg_499"] = 240;
        messages[^1] = Assistant("offscreen growth");
        var offscreenLayouts = view.UpdateLayoutCount;
        Check(view.TryApplyMessageChanges(new[] { messages[^1] }, owner, 2, 3), "Offscreen latest-turn growth stays incremental.");
        Check(view.UpdateLayoutCount == offscreenLayouts, "Entirely offscreen streaming does not force layout for an unchanged reader anchor.");
        Check(Math.Abs(viewport.VerticalOffset - readingOffset) < 0.5, "Streaming below the reader never jumps a scrolled-up anchor.");
        viewport.ScrollForTest(20_080, intermediate: true);
        var inputOffset = viewport.VerticalOffset;
        messages[^1] = Assistant("growth during inertia");
        Check(view.TryApplyMessageChanges(new[] { messages[^1] }, owner, 3, 4), "Streaming during inertia stays incremental.");
        Check(Math.Abs(viewport.VerticalOffset - inputOffset) < 0.5, "Native inertia owns the offset during streaming.");
        viewport.ScrollForTest(inputOffset, intermediate: false);
        view.RefreshViewport(viewport);
        viewport.ViewportHeight = 600;
        view.RefreshViewport(viewport);
        Check(Math.Abs(viewport.VerticalOffset - inputOffset) < 0.5, "Resize retains the scrolled-up anchor.");
        var before = messages.Select(message => message.Id).ToArray();
        var prepended = new OpenCodeMessage { Id = "msg_prepend", Type = "user", Text = "older", Time = new() { Created = 0.5 } };
        heights[prepended.Id!] = 80;
        view.ApplyMessages(new[] { prepended }.Concat(messages).ToList());
        Check(Math.Abs(viewport.VerticalOffset - (inputOffset + 80)) < 0.5, "Pagination prepend corrects only by the inserted height.");
        Check(messages.Select(message => message.Id).SequenceEqual(before), "Anchor corrections do not mutate the accepted snapshot.");
        view.IsLoaded = false;
        view.RaiseUnloadedForTest();
    }

    private static IEnumerable<object> Entries(OpenCodeConversationView view)
        => ((IEnumerable)typeof(OpenCodeConversationView).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Cast<object>();

    private static string[] Projection(OpenCodeConversationView view)
    {
        return Entries(view).Select(entry =>
        {
            var snapshot = (OpenCodeMessage)entry.GetType().GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!;
            return JsonSerializer.Serialize(snapshot, OpenCodeJson.Options);
        }).ToArray();
    }
}
