using System;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

// Wave5 timeline lane (W5-T): H1 independent group toggles, H2 result tally, S2 live empty reasoning row,
// G3 question row (pure model), G4 async completion rows + originating row state, H5 instruction row, H14 user bubble.
// Linked production renderer/algorithms with inert presenter probes; failure labels never include message contents.
internal static class Wave5Timeline
{
    private const string Secret = "SECRET_INSTRUCTION_BODY";
    private const double T0 = 1_700_000_000_000;
    private static int _assertions;

    internal static int Run()
    {
        _assertions = 0;
        IndependentGroupToggles();
        LiveEmptyReasoningRow();
        QuestionRowModel();
        AsyncCompletionRows();
        InstructionRows();
        UserBubble();
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
        ?? throw new InvalidOperationException("wave5.fixture.deserialize");

    private static T[] Constructed<T>() where T : class => ConversationViewTestProbe.Records
        .Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<T>().Distinct().ToArray();

    // H1: v2 reasoning/text parts carry no id, so every message's fallback key is "reasoning:0". Group ids must be
    // message-qualified, otherwise the remembered expansion is shared and one click toggles every group.
    private static void IndependentGroupToggles()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        OpenCodeMessage Turn(string id, string tool) => Message(new
        {
            id, type = "assistant",
            content = new object[]
            {
                new { type = "reasoning", text = "PLAN" },
                new { id = tool, type = "tool", name = "read", state = new { status = "completed", input = new { path = "a.cs" } } },
                new { type = "text", text = "DONE" },
            },
            time = new { created = T0, completed = T0 + 1_000 },
        });
        var user = Message(new { id = "msg_u", type = "user", text = "NEXT" });
        view.ApplyMessages(new[] { Turn("msg_1", "call_1"), user, Turn("msg_2", "call_2") });
        var groups = Constructed<OpenCodeToolGroupView>();
        Check(groups.Length == 2 && groups[0].GroupId != groups[1].GroupId
              && groups.All(g => g.GroupId.EndsWith("/reasoning:0", StringComparison.Ordinal))
              && groups[0].GroupId.StartsWith("msg_1/", StringComparison.Ordinal), "h1.group-ids-message-qualified");
        var rows = Constructed<OpenCodeReasoningRowView>();
        Check(rows.Length == 2 && rows[0].RowKey != rows[1].RowKey, "h1.reasoning-row-keys-unique");
        Check(!groups[0].IsExpanded && !groups[1].IsExpanded, "h1.history-collapsed");
        groups[1].ToggleForTest();
        Check(groups[1].IsExpanded && !groups[0].IsExpanded, "h1.toggle-affects-only-clicked-group");
        view.ApplyMessages(new[] { Turn("msg_1", "call_1"), user, Turn("msg_2", "call_2") });
        Check(groups[1].IsExpanded && !groups[0].IsExpanded, "h1.independent-state-survives-refresh");
        groups[0].ToggleForTest();
        groups[1].ToggleForTest();
        Check(groups[0].IsExpanded && !groups[1].IsExpanded, "h1.each-group-independent");
        // H2 tally inputs: one successful read per group, no errors.
        Check(groups.All(g => g.Summary is { SuccessCount: 1, ErrorCount: 0 }), "h2.success-tally");
        view.ClearMessages();
    }

    // S2: in-progress reasoning (message incomplete, part time end missing / last part) shows a "思考中…" row at once,
    // even with empty text; finished empty reasoning (encrypted provider state only) stays hidden.
    private static void LiveEmptyReasoningRow()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var live = Message(new
        {
            id = "msg_live", type = "assistant",
            content = new object[] { new { type = "reasoning", text = "" } },
            time = new { created = T0 },
        });
        view.ApplyMessages(new[] { live });
        var row = Constructed<OpenCodeReasoningRowView>().Single();
        var group = Constructed<OpenCodeToolGroupView>().Single();
        Check(row.Streaming && row.Text.Length == 0 && ReferenceEquals(row.Element.Parent, group.ItemsHost), "s2.empty-live-reasoning-row-shown");
        Check(group.IsExpanded && group.Summary is { IsActive: true, ReasoningOnly: true }, "s2.live-group-expanded");

        var done = Message(new
        {
            id = "msg_live", type = "assistant",
            content = new object[] { new { type = "reasoning", text = "" }, new { type = "text", text = "ANSWER" } },
            time = new { created = T0, completed = T0 + 2_000 },
        });
        view.ApplyMessages(new[] { done });
        Check(row.Element.Parent is null && group.DisposeCount == 1, "s2.finished-empty-reasoning-hidden");

        var withText = Message(new
        {
            id = "msg_live", type = "assistant",
            content = new object[] { new { type = "reasoning", text = "**step**" }, new { id = "call_x", type = "tool", name = "read", state = new { status = "running", input = new { path = "b" } } } },
            time = new { created = T0 },
        });
        view.ApplyMessages(new[] { withText });
        Check(!row.Streaming && row.Text == "**step**", "s2.reasoning-followed-by-tool-not-streaming");
        view.ClearMessages();
    }

    // G3: the question tool row model (no live view needed): name "提问", detail = question + chosen answer property list.
    private static void QuestionRowModel()
    {
        var part = JsonSerializer.Deserialize<OpenCodeContentPart>(JsonSerializer.Serialize(new
        {
            id = "call_q", type = "tool", name = "question",
            state = new
            {
                status = "completed",
                input = new { questions = new[] { new { header = "选择", question = "用哪个？", options = new object[] { new { label = "甲" }, new { label = "乙" } } } } },
                metadata = new { answers = new[] { new[] { "甲" } } },
                content = new object[] { new { type = "text", text = "User has answered your questions" } },
            },
        }), OpenCodeJson.Options)!;
        var model = OpenCodeToolRowPresentation.Build(part);
        Check(model.Kind == OpenCodeToolKind.Question && model.Name == "提问" && model.Status == OpenCodeToolRowStatus.Completed
              && model.Target == "选择", "g3.question-row-model");
        Check(model.InputProperties.Any(p => p.Key == "问题" && p.Value == "用哪个？")
              && model.InputProperties.Any(p => p.Key == "选项" && p.Value == "甲、乙")
              && model.InputProperties.Any(p => p.Key == "回答" && p.Value == "甲")
              && model.OutputText is null && model.OutputProperties.Count == 0, "g3.question-detail-property-list");
    }

    // G4: background shell / subagent completions render as tool-like rows; the originating call reflects completion.
    private static void AsyncCompletionRows()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var navigation = 0;
        view.SubagentSessionRequested += (_, e) => { if (e.ChildSessionId == "ses_child1") navigation++; };
        var origin = Message(new
        {
            id = "msg_bg", type = "assistant",
            content = new object[]
            {
                new
                {
                    id = "call_sh", type = "tool", name = "shell",
                    state = new
                    {
                        status = "completed", input = new { command = "build.py", background = true },
                        metadata = new { status = "running", shellID = "sh_1" },
                        content = new object[] { new { type = "text", text = "Command moved to the background" } },
                    },
                },
                new { type = "text", text = "STARTED" },
            },
            time = new { created = T0, completed = T0 + 100 },
        });
        view.ApplyMessages(new[] { origin });
        var shellRow = Constructed<OpenCodeToolPartView>().Single();
        var pending = OpenCodeToolRowPresentation.Build(origin.Content![0]);
        Check(shellRow.AsyncCompletion is null && pending.Summary == "后台运行中" && pending.Status == OpenCodeToolRowStatus.Running
            && pending.Name == "终端（后台）", "g4.origin-pending-background-not-success");

        var shellDone = Message(new
        {
            id = "msg_shell_done", type = "synthetic",
            text = "<shell id=\"sh_1\" state=\"completed\" command=\"build.py\">\nBUILD OK\n</shell>",
            description = "build.py",
            metadata = new { source = "shell", shellID = "sh_1", jobID = "sh_1", state = "completed", exit = 0 },
            time = new { created = T0 + 5_000 },
        });
        var childDone = Message(new
        {
            id = "msg_child_done", type = "synthetic",
            text = "<subagent sessionID=\"ses_child1\" state=\"completed\" description=\"构建\">\n<summary>**done**</summary>\n</subagent>",
            metadata = new { source = "subagent", childID = "ses_child1", agent = "fixer", state = "completed" },
            time = new { created = T0 + 6_000 },
        });
        var taskDone = Message(new
        {
            id = "msg_task_done", type = "synthetic",
            text = "<task id=\"ses_task1\" state=\"completed\">\n<summary>Background task completed: 视频修复</summary>\n</task>",
            metadata = new JsonObjectShim(),
            time = new { created = T0 + 7_000 },
        });
        var updatesBefore = shellRow.UpdateCount;
        view.ApplyMessages(new[] { origin, shellDone, childDone, taskDone });
        Check(shellRow.UpdateCount == updatesBefore + 1 && shellRow.AsyncCompletion is { Kind: OpenCodeAsyncCompletionKind.Shell, ExitCode: 0 },
            "g4.origin-row-refreshed-with-completion");
        var completed = OpenCodeToolRowPresentation.Build(origin.Content![0], shellRow.AsyncCompletion);
        Check(completed.Summary == "后台已完成 · 退出码 0" && completed.Status == OpenCodeToolRowStatus.Completed && completed.ExitCode == 0,
            "g4.origin-row-reflects-completed");
        var chips = Constructed<OpenCodeTimelineChipView>().Where(c => c.Content.Name.Length > 0).ToArray();
        Check(chips.Length == 3 && chips.All(c => c.Content is not null), "g4.completion-rows-constructed");
        Check(chips[0].Label == "终端（后台）" && chips[0].Content!.Target == "build.py" && chips[0].Content!.Summary == "已完成 · 退出码 0"
              && chips[0].Content!.Status == OpenCodeToolRowStatus.Completed && chips[0].DetailText == "BUILD OK"
              && chips[0].LinkInvoked is null, "g4.shell-completion-row");
        Check(chips[1].Label == "子代理（后台）" && chips[1].Content!.Target == "fixer 构建" && chips[1].Content!.DetailMarkdown
              && chips[1].Content!.LinkText == "打开子会话" && chips[1].LinkInvoked is not null, "g4.subagent-completion-row");
        chips[1].LinkInvoked!();
        Check(navigation == 1, "g4.subagent-row-navigates-validated-child");
        Check(chips[2].Label == "后台任务" && chips[2].Content!.Target == "视频修复", "g4.task-completion-row");
        // Only the assistant origin owns a completion-card wrapper; synthetic completions never construct one.
        Check(Constructed<OpenCodeSubagentCompletionView>().Length == 1 && view.Children.Count == 4,
            "g4.no-standalone-completion-card-for-synthetic");

        var failed = Message(new
        {
            id = "msg_shell_done", type = "synthetic",
            text = "<shell id=\"sh_1\" state=\"error\" command=\"build.py\">\nFAIL\n</shell>",
            metadata = new { source = "shell", shellID = "sh_1", state = "error", exit = 2 },
            time = new { created = T0 + 5_000 },
        });
        view.ApplyMessages(new[] { origin, failed, childDone, taskDone });
        Check(shellRow.AsyncCompletion is { ExitCode: 2 } && chips[0].Content!.Status == OpenCodeToolRowStatus.Error
              && OpenCodeToolRowPresentation.Build(origin.Content![0], shellRow.AsyncCompletion).Status == OpenCodeToolRowStatus.Error,
            "g4.failed-completion-marks-origin-and-row-error");
        view.ClearMessages();
        chips[1].LinkInvoked!();
        Check(navigation == 1 && chips.All(c => c.DisposeCount == 1), "g4.clear-disposes-no-stale-navigation");
    }

    // H5: synthetic "Instructions from:" messages and system notices render as the "指令" row (file list in detail);
    // the instruction body never reaches any presenter, text probe, or fingerprint.
    private static void InstructionRows()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var synthetic = Message(new
        {
            id = "msg_instr", type = "synthetic",
            text = "Instructions from: /repo/AGENTS.md\n" + Secret,
            metadata = new { instruction = new { paths = new[] { "/repo/AGENTS.md", "/repo/sub/AGENTS.md" } } },
            time = new { created = T0 },
        });
        view.ApplyMessages(new[] { synthetic });
        var notice = Constructed<OpenCodeInstructionNoticeView>().Single();
        var dto = (OpenCodeInstructionNotice)ConversationViewTestProbe.Records.First(r => r.Operation == "Update" && ReferenceEquals(r.Subject, notice)).Input!;
        Check(dto.SourceKeys.SequenceEqual(new[] { "/repo/AGENTS.md", "/repo/sub/AGENTS.md" }) && dto.MessageId == "msg_instr",
            "h5.synthetic-instruction-paths-projected");
        Check(OpenCodeStepRunPresentation.DescribeInstructionTarget(dto) == "AGENTS.md 等 2 项", "h5.instruction-row-target");
        Check(!ConversationViewTestProbe.Records.Any(r => r.Input is string t && t.Contains(Secret, StringComparison.Ordinal))
              && !ConversationViewTestProbe.Records.Any(r => r.Operation == "Render"), "h5.no-instruction-body-rendered");
        var entry = ((System.Collections.IDictionary)typeof(OpenCodeConversationView)
            .GetField("_byKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!)["msg_instr"]!;
        var fingerprint = (string)entry.GetType().GetProperty("Fingerprint")!.GetValue(entry)!;
        Check(!fingerprint.Contains(Secret, StringComparison.Ordinal) && !fingerprint.Contains("Instructions from", StringComparison.Ordinal),
            "h5.fingerprint-without-body");
        Check(synthetic.Text!.Contains(Secret, StringComparison.Ordinal), "h5.caller-input-unchanged");
        view.ClearMessages();
    }

    // H14: user messages are hosted by the two-line bubble controller around the same presenter root.
    private static void UserBubble()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        view.ApplyMessages(new[] { Message(new { id = "msg_user", type = "user", text = "line1\nline2\nline3" }) });
        var bubble = Constructed<OpenCodeUserBubbleView>().Single();
        var presenter = Constructed<OpenCodeRichTextPresenter>().Single();
        Check(ReferenceEquals(view.Children[0], bubble.Element) && ReferenceEquals(presenter.Element.Parent, bubble.Element),
            "h14.user-message-in-bubble-controller");
        view.ApplyMessages(new[] { Message(new { id = "msg_user", type = "user", text = "edited" }) });
        Check(Constructed<OpenCodeUserBubbleView>().Length == 1
              && ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && ReferenceEquals(r.Subject, presenter) && r.Input is "edited"),
            "h14.bubble-reused-on-update");
        view.ClearMessages();
    }

    // Serializes to the slim plugin's metadata key without a C# identifier for it.
    private sealed class JsonObjectShim
    {
        [System.Text.Json.Serialization.JsonPropertyName("oh-my-opencode-slim.internalInitiator")]
        public bool Initiator { get; init; } = true;
    }
}
