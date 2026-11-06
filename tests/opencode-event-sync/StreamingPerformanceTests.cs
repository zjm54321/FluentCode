using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using FluentCode.Services.Rendering;
using Markdig;

internal static class StreamingPerformanceTests
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }

    [ModuleInitializer]
    internal static void Run()
    {
        Reconciliation();
        ChangePublications();
        MarkdownCadence();
        InsightsProjection();
        Measure(20); // JIT/serializer warmup, outside the measurements.
        var small = Measure(1000);
        var large = Measure(2000);
        Console.WriteLine($"PERF reducer N=1000 chars=100000 allocated={small.Bytes} elapsed_ms={small.Milliseconds:F2}");
        Console.WriteLine($"PERF reducer N=2000 chars=200000 allocated={large.Bytes} elapsed_ms={large.Milliseconds:F2}");
        Check(large.Bytes < 24_000_000, "Delta allocation budget excludes quadratic copying.");
        Check(large.Bytes < small.Bytes * 2.8, "Delta accumulation scales linearly.");
        Console.WriteLine($"PASS streaming performance: {_checks} assertions (reconciliation, cadence/final Markdown, insights reuse, scaling).");
    }

    private static (long Bytes, double Milliseconds) Measure(int count)
    {
        var reducer = new OpenCodeEventReducer("ses_perf");
        var chunk = new string('x', 100);
        var delta = OpenCodeEvent.Parse(JsonSerializer.Serialize(new { type = "session.text.delta",
            data = new { sessionID = "ses_perf", assistantMessageID = "msg_perf", ordinal = 0, delta = chunk } }));
        var started = OpenCodeEvent.Parse("""{"type":"session.text.started","data":{"sessionID":"ses_perf","assistantMessageID":"msg_perf","ordinal":0}}""");
        reducer.Apply(started);
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++) reducer.Apply(delta);
        var text = reducer.Messages.Single().Content!.Single().Text;
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check(text == new string('x', count * 100), "Buffered text differs from the event sequence.");
        Check(reducer.StringMaterializationCount == 1, "One publication materializes the accumulated prefix once.");
        _ = reducer.Messages;
        Check(reducer.StringMaterializationCount == 1, "Cached publication does not rematerialize strings.");
        return (bytes, elapsed);
    }

    private static OpenCodeEvent Event(string type, object payload) => OpenCodeEvent.Parse(JsonSerializer.Serialize(new { type, data = payload }));
    private static object TextData(string delta) => new { sessionID = "ses_perf", assistantMessageID = "msg_perf", ordinal = 0, delta };
    private static object EndData(string text) => new { sessionID = "ses_perf", assistantMessageID = "msg_perf", ordinal = 0, text };
    private static object ToolData(string delta) => new { sessionID = "ses_perf", assistantMessageID = "msg_perf", id = "tool_perf", name = "bash", delta };
    private static OpenCodeMessage Message(string text) => new() { Id = "msg_perf", Type = "assistant",
        Content = new() { new() { Type = "text", Text = text } }, Time = new() { Created = 10 } };

    private static void Reconciliation()
    {
        var reducer = new OpenCodeEventReducer("ses_perf");
        reducer.MergeSnapshot(new[] { Message("prefix\r\n") }, 0);
        var version = reducer.Version;
        foreach (var delta in new[] { "中文", "😀", "\r\n", "e\u0301", "", "tail" })
            reducer.Apply(Event("session.text.delta", TextData(delta)));
        Check(reducer.StringMaterializationCount == 0, "Deltas do not materialize before publication.");
        var live = reducer.Messages.Single();
        Check(live.Content!.Single().Text == "prefix\r\n中文😀\r\ne\u0301tail", "Unicode/newline bytes are retained.");
        reducer.Apply(Event("session.text.delta", TextData(" raced")));
        var merged = reducer.MergeSnapshot(new[] { Message("stale") }, version);
        Check(merged.Single().Content!.Single().Text == "prefix\r\n中文😀\r\ne\u0301tail raced", "Unpublished deltas beat a racing snapshot.");
        Check(live.Content!.Single().Text == "prefix\r\n中文😀\r\ne\u0301tail", "Published DTO is immutable.");
        reducer.Apply(Event("session.text.delta", TextData(" discarded")));
        var count = reducer.StringMaterializationCount;
        reducer.Apply(Event("session.text.ended", EndData("authoritative")));
        Check(reducer.Messages.Single().Content!.Single().Text == "authoritative" && reducer.StringMaterializationCount == count,
            "Ended discards pending chunks without materializing obsolete text.");
        reducer.Apply(Event("session.text.delta", TextData(" after")));
        Check(reducer.Messages.Single().Content!.Single().Text == "authoritative after", "A later delta starts at authoritative text.");
        reducer.MergeSnapshot(new[] { Message("REST") }, reducer.Version);
        reducer.Apply(Event("session.text.delta", TextData(" repaired")));
        Check(reducer.Messages.Single().Content!.Single().Text == "REST repaired", "REST replacement resets buffer ownership.");

        var partial = new OpenCodeEventReducer("ses_perf");
        partial.Apply(Event("session.text.delta", TextData(" missing prefix")));
        partial.MergeSnapshot(new[] { Message("snapshot prefix") }, 0);
        partial.Apply(Event("session.text.delta", TextData(" suffix")));
        Check(partial.Messages.Single().Content!.Single().Text == "snapshot prefix suffix", "Partial merge discards the observed-only prefix and rebases.");

        var tool = new OpenCodeEventReducer("ses_perf");
        tool.Apply(Event("session.tool.input.delta", ToolData("{\"x\":")));
        tool.Apply(Event("session.tool.input.delta", ToolData("1}")));
        Check(tool.Messages.Single().Content!.Single().State!.Value.GetProperty("input").GetString() == "{\"x\":1}", "Tool input chunks publish exactly.");
        tool.Apply(Event("session.tool.input.delta", ToolData("obsolete")));
        tool.Apply(Event("session.tool.input.ended", new { sessionID = "ses_perf", assistantMessageID = "msg_perf", id = "tool_perf", text = "final" }));
        tool.Apply(Event("session.tool.input.delta", ToolData("!")));
        Check(tool.Messages.Single().Content!.Single().State!.Value.GetProperty("input").GetString() == "final!", "Tool ended resets its buffer.");
        tool.Apply(Event("session.tool.input.delta", ToolData("obsolete")));
        tool.Apply(Event("session.tool.called", new { sessionID = "ses_perf", assistantMessageID = "msg_perf", id = "tool_perf", input = new { x = 2 } }));
        Check(tool.Messages.Single().Content!.Single().State!.Value.GetProperty("input").GetProperty("x").GetInt32() == 2, "Called retains authoritative structured input.");
        var batches = OpenCodeEventDrainMetrics.BatchCount;
        OpenCodeEventDrainMetrics.Record(0, 1);
        OpenCodeEventDrainMetrics.Record(4, 2.5);
        Check(OpenCodeEventDrainMetrics.BatchCount == batches + 1 && OpenCodeEventDrainMetrics.LastMilliseconds == 2.5,
            "Drain metrics count nonempty batches and record duration.");
    }

    private static void ChangePublications()
    {
        var reducer = new OpenCodeEventReducer("ses_perf");
        reducer.MergeSnapshot(new[] { Message("base") }, 0);
        var initial = reducer.TakeChanges();
        Check(!initial.KnownTextPartsOnly, "A REST snapshot requires full publication.");
        reducer.Apply(Event("session.text.delta", TextData(" delta")));
        var delta = reducer.TakeChanges();
        Check(delta.KnownTextPartsOnly && delta.BaseVersion == initial.Version && delta.Version == reducer.Version
            && delta.Updates.Count == 1 && delta.Updates[0].Index == 0 && delta.Updates[0].Message.Content!.Single().Text == "base delta",
            "Known-part delta publishes only its identity/index/DTO with an exact version fence.");
        reducer.Apply(Event("session.text.delta", new { sessionID = "ses_perf", assistantMessageID = "msg_new", ordinal = 0, delta = "new" }));
        Check(!reducer.TakeChanges().KnownTextPartsOnly, "A new message disables incremental publication.");
        reducer.Apply(Event("session.reasoning.delta", TextData("new part")));
        Check(!reducer.TakeChanges().KnownTextPartsOnly, "A new part disables incremental publication.");
        reducer.Apply(Event("session.tool.input.delta", ToolData("tool")));
        Check(!reducer.TakeChanges().KnownTextPartsOnly, "Tool changes retain full cross-message projection.");
        reducer.MergeSnapshot(new[] { Message("repair") }, reducer.Version);
        Check(!reducer.TakeChanges().KnownTextPartsOnly, "Reconnect/snapshot membership repair disables incremental publication.");
    }

    private static void MarkdownCadence()
    {
        var schedule = new OpenCodeStreamingRenderSchedule();
        Check(schedule.Request("a", false, 0), "Initial source renders immediately.");
        schedule.Rendered(0, 80);
        Check(schedule.IntervalMilliseconds == 240, "Cadence adapts to three times the last render duration.");
        Check(!schedule.Request("ab", false, 100) && schedule.HasPending, "Growth before the adaptive deadline is coalesced.");
        Check(schedule.Request("ab", false, 110), "A repeated source (end notification) flushes pending growth immediately.");
        schedule.Rendered(110, 1);
        Check(!schedule.Request("abc", false, 120), "Minimum cadence is 100ms.");
        Check(schedule.Request("replacement", false, 121), "A changed prefix never waits.");
        schedule.Rendered(121, 1);
        Check(schedule.Request("replacement!", true, 122), "Lifecycle/style/image-context changes never wait.");

        schedule = new OpenCodeStreamingRenderSchedule();
        var renderCount = 0;
        var growing = "";
        for (var index = 0; index < 2000; index++)
        {
            growing += "x";
            if (!schedule.Request(growing, false, index * 100)) continue;
            renderCount++;
            schedule.Rendered(index * 100 + 50, 50);
        }
        if (schedule.HasPending) renderCount++; // idle timer's final flush
        Check(renderCount <= 1001, "A 50ms renderer at the unchanged 100ms event cadence coalesces half the renders.");
        Console.WriteLine($"PERF cadence simulated_updates=2000 baseline_full_renders=2000 adaptive_full_renders={renderCount} last_render_ms=50");

        var fixtures = new[] { "# heading\n\nfirst **bold**\n\n```cs\nvar x = 1;\n```\n\nlast",
            "| A | B |\n|---|---|\n| x | y |\n\nlast", "- first\n- second\n  - nested\n\n1. ordered\n2. next",
            "settled\n\n```csharp\nvar unfinished = \"<tag>\";\n", "[later][ref]\n\n[ref]: https://example.test/",
            "before\n\n![alt](image.png)\n\nafter <strong>safe</strong><script>bad()</script>" };
        var renderer = new SafeContentRenderer();
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
        foreach (var fixture in fixtures)
        {
            schedule = new OpenCodeStreamingRenderSchedule();
            string? rendered = null;
            for (var end = 1; end <= fixture.Length; end++)
            {
                var source = fixture[..end];
                if (!schedule.Request(source, false, end)) continue;
                rendered = source;
                _ = Convert(source);
                schedule.Rendered(end, 1);
            }
            // The presenter's one-shot DispatcherQueueTimer renders the newest request even when no further deltas arrive.
            if (schedule.HasPending) { rendered = fixture; _ = Convert(fixture); schedule.Rendered(1000, 1); }
            Check(rendered == fixture, "Idle flush retains every source byte, including an incomplete trailing fence.");
            var actual = Convert(rendered!);
            var expected = string.Concat(OpenCodeMarkdownImagePlan.Build(fixture).Select(segment => segment.IsImage
                ? "IMAGE:" + segment.Text + ":" + segment.Alt
                : renderer.SanitizeHtml(Markdown.ToHtml(segment.Text, pipeline))));
            Check(actual == expected, "Coalesced final Markdown equals full conversion (fences/tables/lists/references/images/safety).");
        }

        static string Convert(string source) => string.Concat(OpenCodeMarkdownImagePlan.Build(source)
            .Select(segment => segment.IsImage ? "IMAGE:" + segment.Text + ":" + segment.Alt : OpenCodeMarkdownTextPlan.Build(segment.Text).SafeHtml));
    }

    private static void InsightsProjection()
    {
        var source = new OpenCodePageSource("perf", 1, () => new OpenCodeClient(new() { BaseUrl = "http://127.0.0.1:1" }));
        using var client = source.CreateClient();
        var binding = new OpenCodeInsightsBinding(source, 1, 1, client, 1, 1, "ses_perf");
        var controller = new OpenCodeSessionInsightsController(candidate => candidate == binding);
        var messages = Enumerable.Range(0, 1000).Select(i => new OpenCodeMessage { Id = "msg_" + i, Type = "user", Text = "old", Time = new() { Created = i + 1 } }).ToList();
        messages.Add(Message("initial"));
        controller.UpdateContext(binding, messages, true, OpenCodeTodoHistory.NoRevert);
        var contextCount = controller.ContextProjectionCount;
        var todoCount = controller.TodoProjectionCount;
        var inspections = controller.MessageInputInspectionCount;
        var copies = controller.MessageArrayCopyCount;
        var snapshot = controller.Snapshot;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 2000; i++)
        {
            messages[^1] = Message("stream " + i);
            controller.UpdateContext(binding, messages, true, OpenCodeTodoHistory.NoRevert);
        }
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Console.WriteLine($"PERF insights 1001 messages x 2000 updates allocated={bytes} elapsed_ms={elapsed:F2} inspected={controller.MessageInputInspectionCount - inspections} array_copies={controller.MessageArrayCopyCount - copies}");
        Check(controller.ContextProjectionCount == contextCount && controller.TodoProjectionCount == todoCount,
            "Text deltas reuse both context and todo projections.");
        Check(ReferenceEquals(snapshot, controller.Snapshot), "Text-only accepted snapshots retain projection identity.");
        Check(controller.MessageInputInspectionCount == inspections + 2000 && controller.MessageArrayCopyCount == copies,
            "Only changed DTO inputs are inspected; membership-stable publications do not copy message arrays.");
        Check(bytes < 10_000_000, "Insights streaming allocations exclude full-history signatures/copies.");
        messages[^1] = messages[^1] with { Tokens = new() { Input = 123, Output = 45, Reasoning = 0, Cache = new() { Read = 0, Write = 0 } }, Finish = "stop", Time = new() { Created = 10, Completed = 20 } };
        controller.UpdateContext(binding, messages, true, OpenCodeTodoHistory.NoRevert);
        Check(controller.ContextProjectionCount == contextCount + 1 && controller.TodoProjectionCount == todoCount,
            $"Usage completion invalidates only context projection (context={controller.ContextProjectionCount}/{contextCount}, todo={controller.TodoProjectionCount}/{todoCount}).");
        controller.UpdateContext(binding, messages, false, new(false, true, "msg_500"));
        Check(controller.ContextProjectionCount == contextCount + 2 && controller.TodoProjectionCount == todoCount + 1,
            "Completeness/revert changes invalidate both projections.");
        Check(controller.OpenCodeReadsStarted == 0 && controller.BillionReadsStarted == 0, "Local projection never starts HTTP.");
        var identityChecks = controller.MessageIdentityCheckCount;
        var sparseInspections = controller.MessageInputInspectionCount;
        messages[^1] = messages[^1] with { Content = new() { new() { Type = "text", Text = "sparse delta" } } };
        Check(controller.TryUpdateContextMessages(binding, new[] { messages[^1] }, false, new(false, true, "msg_500")),
            "Known changed IDs use sparse insights publication.");
        Check(controller.MessageIdentityCheckCount == identityChecks && controller.MessageInputInspectionCount == sparseInspections + 1
            && controller.SparseUpdateCount == 1, "Sparse insights publication has zero full-history identity checks.");
        var lateHistory = new OpenCodeSessionInsightsController(candidate => candidate == binding);
        lateHistory.UpdateContext(binding, messages, true);
        lateHistory.UpdateContext(binding, messages, true, OpenCodeTodoHistory.NoRevert);
        Check(lateHistory.TodoProjectionCount == 1, "First supplied todo history projects even if messages are unchanged.");
        var beforeTodo = lateHistory.TodoProjectionCount;
        messages[^1] = messages[^1] with { Content = new() { new() { Type = "tool", Name = "todowrite", Id = "todo",
            State = JsonSerializer.SerializeToElement(new { status = "completed", input = new { todos = new[] { new { content = "done", status = "completed", priority = "medium" } } } }) } } };
        lateHistory.UpdateContext(binding, messages, true, OpenCodeTodoHistory.NoRevert);
        Check(lateHistory.TodoProjectionCount == beforeTodo + 1, "Changed todowrite state invalidates todo projection.");
        var canceledLoad = new OpenCodeSessionInsightsController(candidate => candidate == binding);
        canceledLoad.UpdateContext(binding, messages, false, OpenCodeTodoHistory.NoRevert);
        canceledLoad.LoadAsync(binding, messages, true, new CancellationToken(true), OpenCodeTodoHistory.NoRevert).GetAwaiter().GetResult();
        canceledLoad.UpdateContext(binding, messages, true, OpenCodeTodoHistory.NoRevert);
        Check(canceledLoad.Snapshot.HistoryComplete, "An explicit load's captured inputs still project after that load is canceled.");
        source.Revoke();
    }
}
