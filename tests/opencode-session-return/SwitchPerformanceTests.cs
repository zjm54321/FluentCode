using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

internal static partial class Program
{
    private static OpenCodeMessage WindowSnapshot(OpenCodeConversationView view, int index)
    {
        var entries = (IList)typeof(OpenCodeConversationView).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var entry = entries[index]!;
        return (OpenCodeMessage)entry.GetType().GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!;
    }

    private static void RefreshedWindowProjection()
    {
        var view = new OpenCodeConversationView();
        var messages = new[]
        {
            new OpenCodeMessage { Id = "msg_refresh_user_old", Type = "user", Text = "old turn" },
            new OpenCodeMessage { Id = "msg_refresh_old", Type = "assistant", Content = new() { new() { Id = "part_refresh_old", Type = "text", Text = "old reply" } } },
            new OpenCodeMessage { Id = "msg_refresh_user", Type = "user", Text = "latest turn" },
            new OpenCodeMessage { Id = "msg_refresh_latest", Type = "assistant", Content = new()
                { new() { Id = "part_refresh_text", Type = "text", Text = "reply" }, new() { Id = "part_refresh_reasoning", Type = "reasoning", Text = "thinking" } } },
        };
        view.ApplyMessages(messages);
        var applies = view.FullApplyCount;
        var version = view.RenderVersion;
        var owner = new object();
        view.BindIncrementalSnapshot(owner, 4);
        var equal = messages.Select(message => message with { Content = message.Content?.Select(part => part with { }).ToList() }).ToArray();
        Check(view.TryApplyRefreshedMessages(equal), "refresh.same-authoritative-window-is-accepted");
        Check(view.FullApplyCount == applies && view.RenderVersion == version, "refresh.unchanged-window-does-not-project-or-invalidate-layout");
        Check(!view.TryApplyMessageChanges(new[] { equal[3] }, owner, 4, 5), "refresh.unchanged-REST-retires-reducer-baseline");
        var projections = OpenCodeConversationView.MessageProjectionsComputed;
        view.ClearMessages();
        view.ApplyMessages(equal);
        Check(OpenCodeConversationView.MessageProjectionsComputed == projections, "refresh.equal-GET-versions-reuse-presentations-on-next-cached-return");
        applies = view.FullApplyCount;
        var text = equal.ToArray();
        text[3] = equal[3] with { Content = equal[3].Content!.Select(part => part.Type == "text" ? part with { Text = "reply grew" } : part).ToList() };
        Check(view.TryApplyRefreshedMessages(text) && view.FullApplyCount == applies && view.FastApplyCount == 1,
            "refresh.latest-text-change-uses-sparse-path");
        Check(WindowSnapshot(view, 3).Content![0].Text == "reply grew", "refresh.sparse-text-publishes-accepted-snapshot");
        var reasoning = text.ToArray();
        reasoning[3] = text[3] with { Content = text[3].Content!.Select(part => part.Type == "reasoning" ? part with { Text = "thinking grew" } : part).ToList() };
        Check(view.TryApplyRefreshedMessages(reasoning) && view.FullApplyCount == applies && view.FastApplyCount == 2,
            "refresh.latest-reasoning-change-uses-sparse-path");
        version = view.RenderVersion;
        var historical = reasoning.ToArray();
        historical[1] = historical[1] with { Content = new() { historical[1].Content![0] with { Text = "historical correction" } } };
        Check(!view.TryApplyRefreshedMessages(historical), "refresh.historical-correction-requires-full-path");
        Check(view.RenderVersion == version && WindowSnapshot(view, 1).Content![0].Text == "old reply",
            "refresh.failed-sparse-validation-leaves-visual-snapshot-unchanged");
        var metadata = reasoning.ToArray();
        metadata[3] = metadata[3] with { Metadata = JsonSerializer.SerializeToElement(new { summary = "authoritative correction" }) };
        Check(!view.TryApplyRefreshedMessages(metadata), "refresh.metadata-change-requires-full-path");
        var membership = reasoning.ToArray();
        membership[3] = membership[3] with { Content = membership[3].Content!.Append(new OpenCodeContentPart { Id = "part_added", Type = "text", Text = "new part" }).ToList() };
        Check(!view.TryApplyRefreshedMessages(membership), "refresh.part-membership-change-requires-full-path");
        Check(!view.TryApplyRefreshedMessages(reasoning.Reverse().ToArray()), "refresh.message-order-change-requires-full-path");
        Check(!view.TryApplyRefreshedMessages(reasoning.Take(3).ToArray()), "refresh.message-removal-requires-full-path");
        var instruction = reasoning.ToArray();
        instruction[3] = instruction[3] with { InstructionBody = "new nonserialized instruction body" };
        Check(!view.TryApplyRefreshedMessages(instruction), "refresh.nonserialized-instruction-change-requires-full-path");
        Check(view.RenderVersion == version, "refresh.all-structural-fallbacks-validate-before-visual-mutation");

        var tools = reasoning.ToArray();
        tools[3] = tools[3] with { Content = new() { new() { Id = "part_refresh_tool", Type = "tool", Name = "bash",
            State = JsonSerializer.SerializeToElement(new { status = "running", input = new { command = "echo fixture" } }) } } };
        view.ApplyMessages(tools);
        version = view.RenderVersion;
        var completed = tools.ToArray();
        completed[3] = tools[3] with { Content = new() { tools[3].Content![0] with { State = JsonSerializer.SerializeToElement(new { status = "completed", output = "fixture" }) } } };
        Check(!view.TryApplyRefreshedMessages(completed), "refresh.tool-state-change-requires-full-path");
        Check(view.RenderVersion == version, "refresh.tool-fallback-leaves-running-presentation-intact");
        Check(view.EstimateMessageBytes(new OpenCodeMessage { Id = "msg_instruction_budget", Type = "system", InstructionBody = new string('x', 2 * 1024 * 1024) })
            > 2 * 1024 * 1024, "refresh.cache-byte-budget-includes-JSON-ignored-instruction-body");
        Console.WriteLine("PASS authoritative refresh: equal-version presentation reuse, latest text/reasoning sparse apply, structural/history/tool fallback, instruction-body budget.");
    }

    private static async Task DenseCachedSwitch(string repo)
    {
        using var fixture = new Fixture("endpoint_dense_" + Guid.NewGuid().ToString("N"));
        var page = fixture.Page;
        var history = Enumerable.Range(0, 600).Select(index => new OpenCodeMessage
        {
            Id = "msg_dense_" + index, Type = index % 2 == 0 ? "user" : "assistant",
            Time = new() { Created = index + 1, Completed = index + 2 },
            Text = index % 2 == 0 ? "dense question " + index : null,
            Content = index % 2 == 0 ? null : new()
            {
                new() { Id = "part_dense_" + index, Type = "text", Text = new string('x', 160) },
            },
        }).ToList();
        page.FixtureMessageWindows["ses_dense_A"] = history;
        // The aggregate retained-row budget is 600: an empty B leaves all 600 cached A rows resident.
        page.FixtureMessageWindows["ses_dense_B"] = new();
        var view = page.Conversation;
        view.IsLoaded = true;
        var viewport = new ScrollView { Content = view, ViewportHeight = 480 };
        viewport.LayoutForTest = () =>
        {
            foreach (var element in view.Children.OfType<FrameworkElement>())
                if (element.Height <= 0) element.ActualHeight = 80;
            viewport.ScrollableHeight = Math.Max(0, view.Children.OfType<FrameworkElement>()
                .Sum(element => (element.Height > 0 ? element.Height : element.ActualHeight)
                    + element.Margin.Top + element.Margin.Bottom) - viewport.ViewportHeight);
            view.RaiseLayoutUpdatedForTest();
        };
        view.RefreshViewport(viewport);
        await page.Switch("ses_dense_A");
        viewport.LayoutForTest();
        CompositionTarget.Frame();
        var serializations = page.MessageWindowSerializationCount;
        await page.Switch("ses_dense_B");
        viewport.LayoutForTest();
        CompositionTarget.Frame();
        // A fresh authoritative DTO version, as on a real GET; cached A still owns its original buffers.
        page.FixtureMessageWindows["ses_dense_A"] = history.Select(message => message with
        {
            Content = message.Content?.Select(part => part with { }).ToList(),
        }).ToList();
        var applies = view.FullApplyCount;
        var layouts = view.UpdateLayoutCount;
        var fingerprints = view.MessageFingerprintRequestCount;
        var parts = OpenCodeConversationView.PartFingerprintsComputed;
        var projections = OpenCodeConversationView.MessageProjectionsComputed;
        var started = Stopwatch.GetTimestamp();
        await page.Switch("ses_dense_A");
        for (var frame = 0; frame < 4; frame++)
        {
            viewport.LayoutForTest(); // Native layout precedes CompositionTarget.Rendering.
            CompositionTarget.Frame();
        }
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Before measurements: artifacts/switchperf-return-before-20261107T0504, same 600-row fixture.
        Console.WriteLine($"SWITCHPERF dense A-B-A before/after: full_applies=2/{view.FullApplyCount - applies} forced_layouts=5/{view.UpdateLayoutCount - layouts} outgoing_buffer_serializations=2/{page.MessageWindowSerializationCount - serializations} fingerprint_requests=1200/{view.MessageFingerprintRequestCount - fingerprints} part_fingerprints=2/{OpenCodeConversationView.PartFingerprintsComputed - parts} elapsed_ms=12.43/{elapsed:F2} (informational)");
        Console.WriteLine($"SWITCHPERF accepted-version reuse: new_message_projections={OpenCodeConversationView.MessageProjectionsComputed - projections} offscreen_parts_skipped={view.UnrealizedPartsSkipped}");
        Check(((ICollection)typeof(OpenCodeConversationView).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(view)!).Count == 600, "dense.cached-return-preserves-all-600-rows");
        Check(view.FullApplyCount - applies <= 1, "dense.cached-return-at-most-one-full-apply");
        Check(view.UpdateLayoutCount - layouts <= 1, "dense.cached-return-at-most-one-forced-layout");
        Check(page.MessageWindowSerializationCount == serializations, "dense.outgoing-buffers-not-serialized");
        Check(!File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.MessageBinding.cs"))
            .Contains("JsonSerializer.Serialize", StringComparison.Ordinal), "dense.production-retention-has-no-JSON-serialization");
        var offset = viewport.VerticalOffset;
        var renderVersion = view.RenderVersion;
        Check(view.TryApplyRefreshedMessages(page.FixtureMessageWindows["ses_dense_A"]), "dense.unchanged-post-frame-refresh-is-accepted");
        Check(viewport.VerticalOffset == offset && view.RenderVersion == renderVersion, "dense.same-window-refresh-keeps-reader-anchor-and-layout-version");
        view.IsLoaded = false;
        view.RaiseUnloadedForTest();
    }
}
