using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;

// Executes the linked production renderer and algorithms against inert presenter/WinUI probes.
// Reflection is read-only. Failure labels are fixed: never include captured message contents.
internal static class ConversationLifecycle
{
    private const string Secret = "SECRET_PAYLOAD";
    private static int _assertions;

    internal static int Run()
    {
        _assertions = 0;
        SanitizationPrecedesTitleFingerprintAndPresenter();
        SameIdReplacementAndCleanup();
        ToolGroupingAndQuestionAnchors();
        ReasoningMergeAndCrossMessageChains();
        RunningExpansionRules();
        InternalInitiatorChips();
        StepRunFormatting();
        ConversationViewTestProbe.Reset();
        return _assertions;
    }

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(label);
    }

    private static OpenCodeMessage Message(object value) =>
        JsonSerializer.Deserialize<OpenCodeMessage>(JsonSerializer.Serialize(value), OpenCodeJson.Options)
        ?? throw new InvalidOperationException("lifecycle.fixture.deserialize");

    private static OpenCodeMessage Ordinary(string id) => Message(new
    {
        id,
        type = "assistant",
        content = new object[]
        {
            new { id = "part_text", type = "text", text = "PUBLIC_BODY" },
            new
            {
                id = "part_tool", type = "tool", name = "subagent",
                state = new
                {
                    status = "completed",
                    input = new { agent = "general", description = "PUBLIC_TASK", prompt = "PUBLIC_PROMPT" },
                    metadata = new { sessionID = "ses_conflict", status = "completed" },
                },
            },
        },
        metadata = new { source = "subagent", childID = "ses_conflict", state = "succeeded" },
    });

    private static object Tool(string id, string name, string status = "completed") => new
    {
        id, type = "tool", name,
        state = new { status, input = new { path = "src/" + id + ".cs" }, content = new object[] { new { type = "text", text = "1: x" } } },
    };

    private static OpenCodeMessage Grouped(string id, int tools, string lastStatus = "completed", bool withQuestion = true, string questionStatus = "running")
    {
        var parts = new List<object> { new { id = "part_text", type = "text", text = "PUBLIC_BODY" } };
        for (var i = 0; i < tools; i++)
        {
            parts.Add(Tool("part_t" + i, i == 3 ? "edit" : "read", i == tools - 1 ? lastStatus : "completed"));
        }

        if (withQuestion)
        {
            parts.Add(new
            {
                id = "part_q", type = "tool", name = "question",
                state = new { status = questionStatus, input = new { questions = new[] { new { header = "PUBLIC_Q", question = "?" } } }, metadata = new { } },
            });
        }

        return Message(new { id, type = "assistant", content = parts });
    }

    // Scenario 3 (F5): every run of non-text parts between visible outputs folds into ONE group (any count,
    // keyed by its first part): expanded while running, auto-collapsed on finish, post-finish toggle remembered. A run that
    // becomes empty disposes its group. Question tool parts (wave5 G3/D3) are ordinary tool rows INSIDE the group that
    // additionally keep a hidden anchor; anchor add/remove raises QuestionAnchorsChanged exactly once per apply.
    private static void ToolGroupingAndQuestionAnchors()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var anchorEvents = 0;
        view.QuestionAnchorsChanged += (_, _) => anchorEvents++;

        view.ApplyMessages(new[] { Grouped("msg_g", 4, lastStatus: "running") });
        var groups = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeToolGroupView>().ToList();
        var tools = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeToolPartView>().ToList();
        Check(groups.Count == 1 && tools.Count == 5, "group.single-group-five-rows-question-is-a-row");
        var group = groups[0];
        Check(group.GroupId == "part_t0" && group.ItemsHost.Children.Count == 5
              && tools.Take(4).All(t => ReferenceEquals(t.Element.Parent, group.ItemsHost)), "group.rows-inside-group-host");
        Check(group.IsExpanded && group.HeaderVisible
              && group.Summary is { ActiveCount: 2, IsActive: true, ErrorCount: 0, SuccessCount: 3 }
              && group.Summary.HeaderText.StartsWith("处理中…", StringComparison.Ordinal)
              && group.Summary.DetailText == "5 个工具调用 · 读取 ×3 · 编辑 · 提问",
            "group.running-auto-expanded-live-summary");
        var anchor = view.FindQuestionAnchor("msg_g", "part_q");
        Check(anchor is not null && anchorEvents == 1 && view.FindQuestionAnchor("msg_g", "part_t0") is null, "anchor.question-registered-once");
        // G3: the question row sits inside the same group (no standalone timeline card); its anchor is a hidden
        // sibling of the row so a placed form card never renders as a separate card in the timeline.
        var questionRow = tools[4];
        Check(questionRow.Element.Parent is Microsoft.UI.Xaml.Controls.StackPanel questionRoot && ReferenceEquals(questionRoot.Parent, group.ItemsHost)
              && ReferenceEquals(anchor!.Parent, questionRoot) && anchor.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed
              && !view.Children.Contains(anchor), "question.row-inside-group-anchor-hidden");

        // Finished: auto-collapses, header switches to the processed label; user expands after finish → remembered across refresh.
        view.ApplyMessages(new[] { Grouped("msg_g", 4) });
        Check(group.Summary is { IsActive: true, ActiveCount: 1 } && anchorEvents == 1, "group.pending-question-keeps-group-live");
        view.ApplyMessages(new[] { Grouped("msg_g", 4, questionStatus: "completed") });
        Check(group.IsExpanded && group.Summary is { IsActive: true }, "group.step-completion-is-not-turn-terminal");
        view.SetCurrentTurnActivity(false);
        Check(!group.IsExpanded && group.Summary is { IsActive: false, ActiveCount: 0, SuccessCount: 5, ErrorCount: 0 }
              && group.Summary.HeaderText == "已处理" && anchorEvents == 1, "group.finish-auto-collapses");
        group.ToggleForTest();
        view.ApplyMessages(new[] { Grouped("msg_g", 4, lastStatus: "error", questionStatus: "completed") });
        Check(group.IsExpanded && group.Summary is { ErrorCount: 1, SuccessCount: 4 } && group.DisposeCount == 0
              && tools.Take(4).All(t => t.DisposeCount == 0 && ReferenceEquals(t.Element.Parent, group.ItemsHost))
              && questionRow.DisposeCount == 0, "group.user-expansion-remembered-and-rows-reused");

        // Any count groups: shrinking to 2 tools keeps the same group (and its expansion); removed rows are disposed.
        view.ApplyMessages(new[] { Grouped("msg_g", 2, questionStatus: "completed") });
        Check(group.DisposeCount == 0 && group.IsExpanded && group.ItemsHost.Children.Count == 3
              && tools[2].DisposeCount == 1 && tools[3].DisposeCount == 1
              && tools[0].DisposeCount == 0 && ReferenceEquals(tools[0].Element.Parent, group.ItemsHost)
              && ConversationViewTestProbe.IsAttachedTo(tools[0].Element, view), "group.any-count-keeps-group");

        // Run now led by the question: the old group (keyed by part_t0) is disposed and detached; question anchor untouched.
        view.ApplyMessages(new[] { Grouped("msg_g", 0) });
        Check(group.DisposeCount == 1 && !ConversationViewTestProbe.IsAttachedTo(group.Element, view)
              && tools[0].DisposeCount == 1 && tools[1].DisposeCount == 1 && anchorEvents == 1
              && questionRow.DisposeCount == 0 && ConversationViewTestProbe.IsAttachedTo(questionRow.Element, view), "group.empty-run-disposed");

        // Question removed → anchor unregistered; clear → no anchors.
        view.ApplyMessages(new[] { Grouped("msg_g", 2, withQuestion: false) });
        Check(view.FindQuestionAnchor("msg_g", "part_q") is null && anchorEvents == 2, "anchor.removed-raises-once");
        view.ApplyMessages(new[] { Grouped("msg_g", 3) });
        Check(view.FindQuestionAnchor("msg_g", "part_q") is not null && anchorEvents == 3, "anchor.readded");
        view.ClearMessages();
        Check(view.FindQuestionAnchor("msg_g", "part_q") is null && anchorEvents == 4 && view.Children.Count == 0, "anchor.clear");
    }

    private const double T0 = 1_700_000_000_000;

    private static object TimedTool(string id, double created, double? completed, string status = "completed") => new
    {
        id, type = "tool", name = "read",
        state = new { status, input = new { path = "src/" + id + ".cs" }, content = new object[] { new { type = "text", text = "1: x" } } },
        time = new { created, completed },
    };

    private static OpenCodeMessage Step(string id, double created, double? completed, params object[] parts)
        => Message(new { id, type = "assistant", content = parts, time = new { created, completed } });

    // Scenario 4 (F4/F5): consecutive reasoning parts merge into one "思考" row; a turn split across several
    // assistant messages (one per step) forms ONE chain: the leader shows the aggregate "已处理 X 分 Y 秒" header,
    // followers hide their header and follow the leader's expansion; an entry holding only a collapsed follower
    // collapses entirely; the open tail of an unfinished last message is live ("处理中…").
    private static void ReasoningMergeAndCrossMessageChains()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var stepA = Step("msg_a1", T0, T0 + 5_000,
            new { id = "r1", type = "reasoning", text = "THINK_ONE" },
            new { id = "r2", type = "reasoning", text = "THINK_TWO" },
            TimedTool("part_a", T0 + 1_000, T0 + 2_000));
        var stepB = Step("msg_a2", T0 + 5_000, T0 + 65_000,
            new { id = "r3", type = "reasoning", text = "  " },
            TimedTool("part_b", T0 + 6_000, T0 + 64_000),
            new { id = "t_done", type = "text", text = "DONE" });
        view.ApplyMessages(new[] { stepA, stepB });

        var rows = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeReasoningRowView>().ToList();
        var groups = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeToolGroupView>().ToList();
        Check(rows.Count == 3 && groups.Count == 2, "chain.rows-and-groups-constructed");
        Check(!ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is "THINK_ONE" or "THINK_TWO"),
            "reasoning.not-rendered-inline");
        var leader = groups.Single(g => g.GroupId == "r1");
        var follower = groups.Single(g => g.GroupId == "part_b");
        Check(rows[0].Text == "THINK_ONE\n\nTHINK_TWO" && rows[0].RowKey == "r1" && !rows[0].Streaming
              && ReferenceEquals(rows[0].Element.Parent, leader.ItemsHost)
              && rows[1].Element.Parent is null && rows[2].Element.Parent is null, "reasoning.consecutive-merged-blank-skipped");
        Check(leader.ItemsHost.Children.Count == 2 && follower.ItemsHost.Children.Count == 1, "chain.rows-per-segment");
        Check(leader.HeaderVisible && !follower.HeaderVisible && ReferenceEquals(leader.Summary, follower.Summary)
              && leader.Summary is { IsActive: false, ReasoningOnly: false, ErrorCount: 0 }
              && leader.Summary.HeaderText == "已处理 1 分 4 秒"
              && leader.Summary.DetailText.StartsWith("思考 · 2 个工具调用", StringComparison.Ordinal),
            "chain.leader-aggregate-summary");
        Check(!leader.IsExpanded && !follower.IsExpanded
              && view.Children.All(c => c.Visibility == Microsoft.UI.Xaml.Visibility.Visible), "chain.default-collapsed");
        leader.ToggleForTest();
        Check(leader.IsExpanded && follower.IsExpanded, "chain.follower-follows-leader-expand");
        leader.ToggleForTest();
        Check(!leader.IsExpanded && !follower.IsExpanded, "chain.follower-follows-leader-collapse");

        // Unfinished last step holding only a follower segment: live header, entry collapsed until expanded.
        var stepC = Step("msg_a3", T0 + 5_000, null, TimedTool("part_c", T0 + 6_000, null, "running"));
        view.ApplyMessages(new[] { stepA, stepC });
        var tail = groups.Concat(ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeToolGroupView>())
            .Distinct().Single(g => g.GroupId == "part_c");
        Check(follower.DisposeCount == 1 && !tail.HeaderVisible && leader.Summary is { IsActive: true }
              && leader.Summary.HeaderText.StartsWith("处理中…", StringComparison.Ordinal)
              && ReferenceEquals(tail.Summary, leader.Summary), "chain.live-tail");
        Check(!leader.IsExpanded && !tail.IsExpanded && view.Children.Count == 2
              && view.Children[1].Visibility == Microsoft.UI.Xaml.Visibility.Collapsed, "b18.existing-group-choice-survives-return-to-running");
        leader.ToggleForTest();
        Check(leader.IsExpanded && tail.IsExpanded
              && view.Children[1].Visibility == Microsoft.UI.Xaml.Visibility.Visible, "b18.active-turn-manual-expand-shows-follower");
        var stepC2 = Step("msg_a3", T0 + 5_000, null, TimedTool("part_c", T0 + 6_000, null, "running"),
            new { id = "r_more", type = "reasoning", text = "MORE" });
        view.ApplyMessages(new[] { stepA, stepC2 });
        Check(leader.IsExpanded && tail.IsExpanded && leader.Summary is { IsActive: true }, "b18.active-turn-keeps-manual-expansion-on-refresh");
        leader.ToggleForTest();
        Check(!leader.IsExpanded && !tail.IsExpanded && view.Children[1].Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
            "b18.follower-entry-hidden-when-collapsed");

        // A user message breaks the chain: each segment gets its own visible header.
        var user = Message(new { id = "msg_u", type = "user", text = "NEXT" });
        view.ApplyMessages(new[] { stepA, user, stepC });
        Check(leader.HeaderVisible && tail.HeaderVisible && !ReferenceEquals(leader.Summary, tail.Summary)
              && leader.Summary is { IsActive: false } && tail.Summary is { IsActive: true }, "chain.broken-by-user-message");
        Check(!leader.IsExpanded && tail.IsExpanded, "b18.finished-leader-retains-manual-collapse-new-running-leader-expands");
        view.ClearMessages();
        Check(rows.All(r => r.DisposeCount == 1) && leader.DisposeCount == 1 && tail.DisposeCount == 1, "chain.clear-disposes");

        // Reasoning-only segment with message times: "思考了 N 秒".
        ConversationViewTestProbe.Reset();
        var thinking = new OpenCodeConversationView();
        thinking.ApplyMessages(new[] { Step("msg_think", T0, T0 + 8_000, new { id = "r_only", type = "reasoning", text = "PONDER" }) });
        var only = Constructed<OpenCodeToolGroupView>();
        Check(only.Summary is { ReasoningOnly: true, IsActive: false } && only.Summary.HeaderText == "思考了 8 秒"
              && only.Summary.DetailText == "思考" && !only.IsExpanded, "reasoning-only.summary");
        thinking.ClearMessages();
    }

    // Scenario 4b (F5 expansion rules): history loaded finished starts collapsed; a manual toggle during a run is
    // preserved for the whole group lifetime, including finish and repeated activity changes.
    private static void RunningExpansionRules()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        view.ApplyMessages(new[] { Grouped("msg_h", 2, withQuestion: false) });
        var group = Constructed<OpenCodeToolGroupView>();
        Check(!group.IsExpanded && group.Summary is { IsActive: false }, "rules.history-collapsed");
        view.ApplyMessages(new[] { Grouped("msg_h", 3, withQuestion: false) });
        Check(!group.IsExpanded, "rules.history-refresh-stays-collapsed");

        view.ApplyMessages(new[] { Grouped("msg_h", 4, lastStatus: "running", withQuestion: false) });
        Check(group.IsExpanded && group.Summary is { IsActive: true }, "rules.transition-into-running-expands");
        group.ToggleForTest();
        Check(!group.IsExpanded, "b18.active-turn-collapse-is-accepted");
        view.ApplyMessages(new[] { Grouped("msg_h", 5, lastStatus: "running", withQuestion: false) });
        Check(!group.IsExpanded && group.Summary is { IsActive: true }, "b18.whole-turn-remains-manually-collapsed");
        group.ToggleForTest();
        view.ApplyMessages(new[] { Grouped("msg_h", 5, lastStatus: "running", withQuestion: false) });
        Check(group.IsExpanded, "rules.manual-expand-during-run-respected");
        view.ApplyMessages(new[] { Grouped("msg_h", 5, withQuestion: false) });
        Check(group.IsExpanded && group.Summary is { IsActive: true }, "wave6.ended-step-awaits-turn-terminal");
        view.SetCurrentTurnActivity(false);
        Check(group.IsExpanded && group.Summary is { IsActive: false }, "b18.finish-preserves-manual-expansion");

        group.ToggleForTest();
        view.ApplyMessages(new[] { Grouped("msg_h", 5, lastStatus: "error", withQuestion: false) });
        view.ApplyMessages(new[] { Grouped("msg_h", 5, withQuestion: false) });
        Check(!group.IsExpanded, "b18.post-finish-collapse-remembered");
        group.ToggleForTest();
        view.SetCurrentTurnActivity(true);
        view.ApplyMessages(new[] { Grouped("msg_h", 6, lastStatus: "running", withQuestion: false) });
        Check(group.IsExpanded, "b18.re-entering-running-preserves-manual-expansion");
        view.ApplyMessages(new[] { Grouped("msg_h", 6, withQuestion: false) });
        view.SetCurrentTurnActivity(false);
        Check(group.IsExpanded && group.DisposeCount == 0, "b18.second-finish-preserves-choice");
        view.ClearMessages();
    }

    // Scenario 5 (F8): synthetic internal-initiator messages render as ONE compact chip (label only; full text
    // reserved for the hover detail), never through the rich-text presenter; flipping kind replaces the entry.
    private static void InternalInitiatorChips()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        const string body = "[SLIM_INTERNAL_INITIATOR] continue the background task PUBLIC_DETAIL";
        var initiator = Message(new { id = "msg_init", type = "user", text = body });
        var phrase = Message(new { id = "msg_phrase", type = "synthetic", text = "Sent by OH-MY-OPENCODE-SLIM internal initiator." });
        view.ApplyMessages(new[] { initiator, phrase });
        var chips = ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<OpenCodeTimelineChipView>().ToList();
        Check(chips.Count == 2 && chips.All(c => c.Label == "内部自动消息"), "initiator.chip-per-message");
        Check(chips[0].DetailText == body && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(chips[0].Element) == "OpenCodeInternalInitiator",
            "initiator.full-text-hover-only");
        Check(!ConversationViewTestProbe.Records.Any(r => r.Operation == "Render")
              && !ConversationViewTestProbe.Records.Any(r => r.Operation == "Text" && r.Input is string t && t.Contains("PUBLIC_DETAIL", StringComparison.Ordinal)),
            "initiator.no-inline-body");
        var chipEntry = Entry(view, "msg_init");
        Check(Property<bool>(chipEntry, "IsInitiatorChip") && view.Children.Count == 2
              && ReferenceEquals(view.Children[0], chips[0].Element), "initiator.entry-flag-and-tree");

        view.ApplyMessages(new[] { Message(new { id = "msg_init", type = "user", text = "ordinary prompt" }), phrase });
        var replaced = Entry(view, "msg_init");
        Check(!ReferenceEquals(chipEntry, replaced) && !Property<bool>(replaced, "IsInitiatorChip")
              && chips[0].DisposeCount == 1 && !ConversationViewTestProbe.IsAttachedTo(chips[0].Element, view)
              && ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is "ordinary prompt"),
            "initiator.kind-flip-replaces-entry");
        view.ClearMessages();
        Check(chips[1].DisposeCount == 1, "initiator.clear-disposes");
    }

    // Pure formatting used by the group header and chips.
    private static void StepRunFormatting()
    {
        Check(OpenCodeStepRunPresentation.FormatDuration(300) == "1 秒"
              && OpenCodeStepRunPresentation.FormatDuration(59_400) == "59 秒"
              && OpenCodeStepRunPresentation.FormatDuration(65_000) == "1 分 5 秒"
              && OpenCodeStepRunPresentation.FormatDuration(3_660_000) == "1 小时 1 分", "format.duration");
        Check(OpenCodeStepRunPresentation.FormatActiveHeader(null, T0) == "处理中…"
              && OpenCodeStepRunPresentation.FormatActiveHeader(T0, T0 + 12_000) == "处理中… 12 秒"
              && OpenCodeStepRunPresentation.FormatActiveHeader(42.5, T0) == "处理中…", "format.active-header");
        var none = OpenCodeStepRunPresentation.Build(Array.Empty<OpenCodeToolRowModel>(), 1, 42.5, 43.0, false, T0);
        Check(none.HeaderText == "已思考" && none.StartedAt is null, "format.implausible-times-ignored");
        var notice = new OpenCodeInstructionNotice("m", "s", System.Collections.Immutable.ImmutableArray.Create("/repo/AGENTS.md", "core/x"), null);
        Check(OpenCodeStepRunPresentation.DescribeInstructionChipLabel(notice) == "已加载指令 · AGENTS.md 等 2 项"
              && OpenCodeStepRunPresentation.DescribeInstructionChipLabel(notice with { SourceKeys = System.Collections.Immutable.ImmutableArray<string>.Empty }) == "已加载指令",
            "format.instruction-chip-label");
        Check(OpenCodeStepRunPresentation.IsInternalInitiatorText("x SLIM_INTERNAL_INITIATOR y")
              && !OpenCodeStepRunPresentation.IsInternalInitiatorText("slim_internal_initiator")
              && OpenCodeStepRunPresentation.IsInternalInitiatorText("Oh-My-OpenCode-Slim Internal Initiator")
              && !OpenCodeStepRunPresentation.IsInternalInitiatorText(null), "format.initiator-detection");
        Check(OpenCodeStepRunPresentation.DescribeReasoningPreview("\n## **Plan** the work\nmore") == "Plan the work", "format.reasoning-preview");
    }

    private static OpenCodeMessage Notice(string id, string secret = Secret) => Message(new
    {
        id,
        type = "system",
        description = secret,
        text = secret,
        agent = secret,
        content = new object[]
        {
            new { id = "secret_text", type = "text", text = secret },
            new { id = "secret_tool", type = "tool", name = "subagent", state = new { status = "completed", input = secret } },
        },
        providerState = new { token = secret },
        providerContext = new { token = secret },
        snapshot = secret,
        error = new { type = "error", message = secret },
        metadata = new
        {
            notice = "instructions", instructionSources = new[] { "core/instructions" },
            source = "subagent", childID = "ses_conflict", state = "succeeded", token = secret,
        },
        time = new { created = 42.5, completed = 43.0 },
    });

    private static T Field<T>(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) is T value
            ? value : throw new InvalidOperationException("lifecycle.reflection.field");

    private static object Entry(OpenCodeConversationView view, string id) =>
        Field<IDictionary>(view, "_byKey")[id]
        ?? throw new InvalidOperationException("lifecycle.reflection.entry");

    private static T Property<T>(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance) is T value
            ? value : throw new InvalidOperationException("lifecycle.reflection.property");

    private static T Constructed<T>() where T : class
    {
        var instances = ConversationViewTestProbe.Records
            .Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<T>().ToArray();
        Check(instances.Length == 1, "lifecycle.construct.exactly-one");
        return instances[0];
    }

    private static bool Recorded(string operation, object subject) => ConversationViewTestProbe.Records
        .Any(r => r.Operation == operation && ReferenceEquals(r.Subject, subject));

    private static void SanitizationPrecedesTitleFingerprintAndPresenter()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var ordinary = Ordinary("msg_task");
        var notice = Notice("msg_notice");
        // Positive controls ensure the fixture really conflicts under the linked real parser/title algorithm.
        Check(OpenCodeSubagentPresentation.TryReadCompletionMetadata(notice.Metadata, out _), "order.fixture.real-completion");
        Check(OpenCodeSubagentTaskTitles.Build(new[] { ordinary, notice }).TryGetValue("msg_notice", out var title)
            && title == "PUBLIC_TASK", "order.fixture.real-title");
        view.ApplyMessages(new[] { ordinary });
        var initialElement = view.Children[0];
        Check(ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is "PUBLIC_BODY"),
            "order.ordinary-body");

        ConversationViewTestProbe.Reset();
        view.ApplyMessages(new[] { ordinary, notice });
        var presenter = Constructed<OpenCodeInstructionNoticeView>();
        Check(!ConversationViewTestProbe.Records.Any(r => r.Operation == "Construct"
            && r.Subject is OpenCodeRichTextPresenter or OpenCodeToolPartView or OpenCodeSubagentCompletionView),
            "order.no-unsafe-presenter-construction");
        Check(!ConversationViewTestProbe.Records.Any(r => r.Operation == "Render"), "order.no-rich-render");
        var updates = ConversationViewTestProbe.Records
            .Where(r => r.Operation == "Update" && ReferenceEquals(r.Subject, presenter)).ToArray();
        Check(updates.Length == 1 && updates[0].Input is OpenCodeInstructionNotice, "order.safe-dto-only");
        var dto = (OpenCodeInstructionNotice)updates[0].Input!;
        Check(dto.MessageId == "msg_notice" && dto.Summary == "指令已更新，来源：core/instructions"
            && dto.SourceKeys.Length == 1 && dto.SourceKeys[0] == "core/instructions" && dto.Created == 42.5,
            "order.safe-dto-fields");
        Check(!JsonSerializer.Serialize(dto).Contains(Secret, StringComparison.Ordinal), "order.safe-dto-no-secret");
        Check(!ConversationViewTestProbe.Records.Any(r =>
            r.Input is string text && text.Contains(Secret, StringComparison.Ordinal)), "order.no-secret-text-input");
        var entry = Entry(view, "msg_notice");
        var fingerprint = Property<string>(entry, "Fingerprint");
        Check(!fingerprint.Contains(Secret, StringComparison.Ordinal), "order.fingerprint-no-secret");
        using var json = JsonDocument.Parse(fingerprint);
        Check(json.RootElement.GetProperty("metadata").TryGetProperty("notice", out var kind)
            && kind.GetString() == "instructions", "order.fingerprint-is-notice-json");
        Check(!json.RootElement.GetProperty("metadata").TryGetProperty("source", out _), "order.fingerprint-no-conflict");
        Check(!Field<IReadOnlyDictionary<string, string>>(view, "_subagentTaskTitles").ContainsKey("msg_notice"),
            "order.no-notice-task-title");
        Check(view.Children.Count == 2 && ReferenceEquals(view.Children[0], initialElement)
            && ConversationViewTestProbe.IsAttachedTo(presenter.Element, view.Children[1]), "order.stable-ordinary-and-notice-process-tree");
        Check(notice.Text == Secret && notice.Content is { Count: 2 }, "order.caller-input-unchanged");
        view.ClearMessages();
    }

    private static void SameIdReplacementAndCleanup()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var navigationCount = 0;
        view.SubagentSessionRequested += (_, _) => navigationCount++;
        var ordinary = Ordinary("msg_swap");
        var notice = Notice("msg_swap");
        view.ApplyMessages(new[] { ordinary });
        var oldTool = Constructed<OpenCodeToolPartView>();
        var oldCompletion = Constructed<OpenCodeSubagentCompletionView>();
        var oldEntry = Entry(view, "msg_swap");
        Check(oldTool.SubscriberCount == 1, "replace.initial-subscription");
        oldTool.RaiseNavigationForTest("ses_conflict");
        oldCompletion.RaiseNavigationForTest("ses_conflict");
        Check(navigationCount == 2, "replace.live-navigation-positive-control");

        ConversationViewTestProbe.Reset();
        view.ApplyMessages(new[] { notice });
        var safeView = Constructed<OpenCodeInstructionNoticeView>();
        var safeEntry = Entry(view, "msg_swap");
        Check(!ReferenceEquals(oldEntry, safeEntry) && Property<bool>(safeEntry, "IsInstructionNotice"), "replace.new-notice-entry");
        Check(oldTool.DisposeCount == 1 && oldCompletion.DisposeCount == 1, "replace.old-dispose-once");
        Check(oldTool.SubscriberCount == 0 && Recorded("Unsubscribe", oldTool), "replace.old-unsubscribed");
        Check(!ConversationViewTestProbe.IsAttachedTo(oldTool.Element, view)
            && !ConversationViewTestProbe.IsAttachedTo(oldCompletion.Element, view), "replace.old-elements-detached");
        Check(Recorded("Remove", oldCompletion.Element), "replace.old-root-removal-recorded");
        Check(view.Children.Count == 1 && ConversationViewTestProbe.IsAttachedTo(safeView.Element, view.Children[0]), "replace.notice-only-process-tree");
        oldTool.RaiseNavigationForTest("ses_conflict");
        oldCompletion.RaiseNavigationForTest("ses_conflict");
        Check(navigationCount == 2, "replace.no-stale-navigation");

        ConversationViewTestProbe.Reset();
        view.ApplyMessages(new[] { notice });
        Check(ReferenceEquals(Entry(view, "msg_swap"), safeEntry)
            && ConversationViewTestProbe.IsAttachedTo(safeView.Element, view.Children[0]), "replace.repeat-reuses-notice");
        Check(!ConversationViewTestProbe.Records.Any(r => r.Operation is "Construct" or "Update" or "Remove"),
            "replace.repeat-no-work");
        view.ApplyMessages(new[] { Notice("msg_swap", "SECRET_PAYLOAD_CHANGED") });
        Check(ReferenceEquals(Entry(view, "msg_swap"), safeEntry)
            && !ConversationViewTestProbe.Records.Any(r => r.Operation is "Construct" or "Update"), "replace.raw-only-change-ignored");

        ConversationViewTestProbe.Reset();
        view.ApplyMessages(new[] { ordinary });
        var newTool = Constructed<OpenCodeToolPartView>();
        var newCompletion = Constructed<OpenCodeSubagentCompletionView>();
        var restoredEntry = Entry(view, "msg_swap");
        Check(!ReferenceEquals(restoredEntry, safeEntry) && !Property<bool>(restoredEntry, "IsInstructionNotice"),
            "replace.new-ordinary-entry");
        Check(!ConversationViewTestProbe.IsAttachedTo(safeView.Element, view) && Recorded("Remove", Property<FrameworkElement>(safeEntry, "Element")),
            "replace.notice-detached-without-dispose-contract");
        Check(!ReferenceEquals(oldTool, newTool) && !ReferenceEquals(oldCompletion, newCompletion), "replace.fresh-resources");
        Check(newTool.DisposeCount == 0 && newCompletion.DisposeCount == 0 && newTool.SubscriberCount == 1,
            "replace.new-resources-live");
        Check(ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is "PUBLIC_BODY"),
            "replace.ordinary-body-restored");
        newTool.RaiseNavigationForTest("ses_conflict");
        newCompletion.RaiseNavigationForTest("ses_conflict");
        Check(navigationCount == 4, "replace.new-navigation-live");
        view.ApplyMessages(new[] { ordinary });
        Check(ReferenceEquals(Entry(view, "msg_swap"), restoredEntry), "replace.ordinary-index-retained");

        view.ClearMessages();
        Check(newTool.DisposeCount == 1 && newCompletion.DisposeCount == 1 && newTool.SubscriberCount == 0,
            "clear.remaining-resources-once");
        Check(oldTool.DisposeCount == 1 && oldCompletion.DisposeCount == 1, "clear.old-resources-not-redisposed");
        Check(view.Children.Count == 0 && Field<IDictionary>(view, "_byKey").Count == 0
            && Field<IReadOnlyDictionary<string, string>>(view, "_subagentTaskTitles").Count == 0,
            "clear.tree-and-indexes-empty");
        Check(!ConversationViewTestProbe.IsAttachedTo(newTool.Element, view)
            && !ConversationViewTestProbe.IsAttachedTo(newCompletion.Element, view), "clear.remaining-elements-detached");
        newTool.RaiseNavigationForTest("ses_conflict");
        newCompletion.RaiseNavigationForTest("ses_conflict");
        Check(navigationCount == 4, "clear.no-stale-navigation");
        view.ClearMessages();
        Check(newTool.DisposeCount == 1 && newCompletion.DisposeCount == 1
            && oldTool.DisposeCount == 1 && oldCompletion.DisposeCount == 1, "clear.idempotent-disposal");
    }
}
