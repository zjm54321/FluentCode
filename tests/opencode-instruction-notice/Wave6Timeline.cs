using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Wave6Timeline
{
    private static IEnumerable<Microsoft.UI.Xaml.UIElement> Walk(Microsoft.UI.Xaml.UIElement root)
    {
        yield return root;
        if (root is Microsoft.UI.Xaml.Controls.Panel panel)
            foreach (var child in panel.Children) foreach (var element in Walk(child)) yield return element;
        else if (root is Microsoft.UI.Xaml.Controls.Border { Child: { } content })
            foreach (var element in Walk(content)) yield return element;
        else if (root is Microsoft.UI.Xaml.Controls.ContentControl { Content: Microsoft.UI.Xaml.UIElement child })
            foreach (var element in Walk(child)) yield return element;
    }
    private static int _checks;
    private static void Check(bool value, string label) { _checks++; if (!value) throw new InvalidOperationException(label); }
    private static OpenCodeMessage M(object value) => JsonSerializer.Deserialize<OpenCodeMessage>(JsonSerializer.Serialize(value), OpenCodeJson.Options)!;
    private static T[] Made<T>() => ConversationViewTestProbe.Records.Where(r => r.Operation == "Construct").Select(r => r.Subject).OfType<T>().Distinct().ToArray();
    private static OpenCodeMessage Step(string id, params object[] content) => M(new { id, type = "assistant", content, time = new { created = 1700000000000d, completed = 1700000001000d } });
    private static object Tool(string id) => new { id, type = "tool", name = "read", state = new { status = "completed", input = new { path = "file" } } };

    internal static void Run()
    {
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var one = Step("msg_one", Tool("leaf_one"), new { type = "text", text = "answer one" });
        var directory = M(new { id = "dir", type = "location-switched", location = new { directory = "/project" } });
        var instruction = M(new { id = "instruction", type = "synthetic", text = "Instructions from: /project/AGENTS.md", metadata = new { instruction = new { paths = new[] { "/project/AGENTS.md" } } } });
        var two = Step("msg_two", Tool("leaf_two"), new { type = "reasoning", text = "more thought" }, new { type = "text", text = "answer two" });
        view.ApplyMessages(new[] { one, directory, instruction, two });
        var groups = Made<OpenCodeToolGroupView>();
        Check(groups.Length == 3 && groups[0].OpenLeaf is not null && groups[1].OpenLeaf is not null, "rp3.chips-not-processing-leaves");
        groups[0].OpenLeaf!(); groups[0].EnterLeaf!(); groups[0].ExitLeaf!();
        var row = Made<OpenCodeToolPartView>()[0];
        Check(ConversationViewTestProbe.Records.Any(r => r.Operation == "OpenLeaf" && ReferenceEquals(r.Subject, row))
            && ConversationViewTestProbe.Records.Any(r => r.Operation == "HoverLeaf" && ReferenceEquals(r.Subject, row)), "wave6.single-header-routes-directly-to-actual-leaf-controller");
        Check(ReferenceEquals(row.DetailAnchorOverride, groups[0].DetailAnchor), "wave6.single-leaf-preview-uses-visible-header-anchor-not-hidden-row");
        var directoryChip = Made<OpenCodeTimelineChipView>().Single(c => c.Label == "目录");
        Check(groups.Any(g => g.ItemsHost.Children.Contains(directoryChip.Element)) && ConversationViewTestProbe.IsAttachedTo(directoryChip.Element, view), "wave7.directory-joins-processing-group");
        var instructionChip = Made<OpenCodeInstructionNoticeView>().Single();
        Check(view.Children.Count == 4 && ReferenceEquals(view.Children[2], instructionChip.Element)
            && groups.All(g => !g.ItemsHost.Children.Contains(instructionChip.Element)) && groups.Count(g => g.HeaderVisible) == 3,
            "rp3.instruction-chip-outside-and-above-following-processing-group");

        view.SetCurrentTurnActivity(true);
        Check(groups.All(g => g.IsExpanded && g.Summary!.IsActive && g.Summary.HeaderText.StartsWith("处理中", StringComparison.Ordinal)), "wave6.all-current-turn-groups-active-not-just-last-chain");
        view.ApplyMessages(new[] { one, directory, instruction, two });
        Check(groups.All(g => g.IsExpanded && g.Summary!.IsActive), "wave6.completed-tools-and-message-steps-do-not-end-turn");
        view.SetCurrentTurnActivity(false);
        Check(groups.All(g => !g.IsExpanded && !g.Summary!.IsActive), "wave6.authoritative-terminal-collapses-all-current-turn-groups");
        view.SetCurrentTurnActivity(null);
        Check(groups.All(g => !g.IsExpanded && !g.Summary!.IsActive), "wave6.unknown-state-after-terminal-cannot-revive-previous-observed-activity");
        groups[0].ToggleForTest();
        view.SetCurrentTurnActivity(false); view.ApplyMessages(new[] { one, directory, instruction, two });
        Check(groups[0].IsExpanded && !groups[1].IsExpanded, "wave6.terminal-repeat-does-not-recollapse-manual-history-or-toggle-siblings");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        view.ApplyMessages(new[] { Step("reasoning", new { type = "reasoning", text = "only thought" }) });
        var single = Made<OpenCodeToolGroupView>().Single();
        single.OpenLeaf!();
        var reasoning = Made<OpenCodeReasoningRowView>().Single();
        Check(single.OpenLeaf is not null && ReferenceEquals(reasoning.DetailAnchorOverride, single.DetailAnchor), "wave6.reasoning-single-leaf-direct-anchor");
        single.OpenLeaf!();
        Check(ConversationViewTestProbe.Records.Any(r => r.Operation == "OpenLeaf" && ReferenceEquals(r.Subject, reasoning)), "wave6.single-reasoning-click-direct-detail");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        var live = M(new { id = "live", type = "assistant", content = new[] { Tool("live_tool") }, time = new { created = 1700000000000d } });
        view.ApplyMessages(new[] { live });
        var latched = Made<OpenCodeToolGroupView>().Single();
        view.ApplyMessages(new[] { Step("live", Tool("live_tool")) });
        Check(latched.Summary!.IsActive && latched.IsExpanded, "wave6.inferred-live-turn-latches-through-step-completion");
        var idle = M(new { id = "idle", type = "idle", outcome = "succeeded", time = new { created = 1700000002000d } });
        view.ApplyMessages(new[] { Step("live", Tool("live_tool")), idle });
        Check(!latched.Summary!.IsActive && !latched.IsExpanded, "wave6.idle-is-authoritative-whole-turn-terminal");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        var runningGroup = M(new
        {
            id = "wg1_message", type = "assistant", time = new { created = 1700000000000d },
            content = new object[] { Tool("wg1_first"), new { id = "wg1_current", type = "tool", name = "read", state = new { status = "running", input = new { path = "current.cs" } } } },
        });
        view.ApplyMessages(new[] { runningGroup });
        var manuallyCollapsed = Made<OpenCodeToolGroupView>().Single();
        manuallyCollapsed.ToggleForTest();
        view.ApplyMessages(new[] { runningGroup });
        view.SetCurrentTurnActivity(true);
        Check(!manuallyCollapsed.IsExpanded && manuallyCollapsed.Summary!.IsActive, "WG1.timeline-manual-running-collapse-survives-live-snapshot-and-turn-refresh");
        Check(manuallyCollapsed.CurrentStep!.Contains("current.cs", StringComparison.Ordinal), "WG1.timeline-collapsed-current-step-uses-running-leaf");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        var requests = 0;
        view.SubagentSessionRequested += (_, e) => { Check(e.ParentMessageId == "child_done" && e.ChildSessionId == "ses_child", "wave6.child-entry-preserves-real-parent-and-child-ids"); requests++; };
        OpenCodeMessage Child(string state) => M(new { id = "child_done", type = "synthetic", text = "child body", metadata = new { source = "subagent", childID = "ses_child", state } });
        view.ApplyMessages(new[] { Child("completed") });
        var child = Made<OpenCodeTimelineChipView>().Single();
        Check(child.Label == "子智能体（后台）" && child.Content!.Status == OpenCodeToolRowStatus.Completed && ConversationViewTestProbe.IsAttachedTo(child.Element, view), "wave7.completed-child-retains-background-identity-in-process-group");
        child.LinkInvoked!();
        view.ApplyMessages(new[] { Child("failed") });
        Check(child.Label == "子智能体（后台）" && child.Content!.Status == OpenCodeToolRowStatus.Error && child.DisposeCount == 0, "wave7.failed-child-same-name-same-row-with-error-status");
        child.LinkInvoked!();
        Check(requests == 2, "wave6.success-failure-use-same-child-conversation-request");
        var invocation = JsonSerializer.Deserialize<OpenCodeContentPart>("{\"type\":\"tool\",\"name\":\"subagent\",\"state\":{\"status\":\"completed\",\"input\":{}}}", OpenCodeJson.Options)!;
        Check(OpenCodeToolRowPresentation.Build(invocation).Name == "子智能体", "v5.invocation-uses-approved-subagent-label");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        var childInvocation = M(new { id = "child_invocation", type = "assistant", content = new[] { new
        {
            id = "child_call", type = "tool", name = "subagent", state = new { status = "completed",
                input = new { agent = "fixer", description = "real child", background = true }, metadata = new { sessionID = "ses_child", status = "running" } }
        } } });
        view.ApplyMessages(new[] { childInvocation, Child("completed") });
        Check(Made<OpenCodeToolGroupView>().Count(g => g.HeaderVisible && g.Element.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                && ConversationViewTestProbe.IsAttachedTo(g.Element, view)) == 1
            && Made<OpenCodeToolGroupView>().First(g => g.OpenLeaf is not null).LeafStatus == OpenCodeToolRowStatus.Completed,
            "v5.subagent-completion-merges-into-original-call-without-second-visible-row");
        view.ApplyMessages(new[] { childInvocation, Child("failed") });
        Check(Made<OpenCodeToolGroupView>().First(g => g.OpenLeaf is not null).LeafStatus == OpenCodeToolRowStatus.Error,
            "v5.same-subagent-call-reflects-updated-completion-state");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        var backgroundOrigin = M(new { id = "bg_origin", type = "assistant", content = new[] { new
        {
            id = "bg_call", type = "tool", name = "shell", state = new { status = "completed",
                input = new { command = "build", background = true }, metadata = new { shellID = "job_one", status = "running" } }
        } }, time = new { created = 1700000000000d, completed = 1700000001000d } });
        OpenCodeMessage BackgroundResult(string state) => M(new { id = "bg_result", type = "synthetic", text = "actual output",
            metadata = new { source = "shell", shellID = "job_one", state } });
        view.ApplyMessages(new[] { backgroundOrigin, BackgroundResult("completed") });
        var backgroundGroups = Made<OpenCodeToolGroupView>();
        Check(backgroundGroups.Length == 2 && backgroundGroups.Count(g => g.HeaderVisible) == 1
            && backgroundGroups[0].OpenLeaf is not null && backgroundGroups[0].LeafName == "终端（后台）"
            && backgroundGroups[0].Summary!.SuccessCount == 1,
            "wave7.call-and-correlated-result-are-one-logical-operation-with-one-direct-header-and-one-success");
        view.ApplyMessages(new[] { backgroundOrigin, BackgroundResult("running") });
        Check(backgroundGroups[0].LeafStatus == OpenCodeToolRowStatus.Running && backgroundGroups[0].Summary!.SuccessCount == 0,
            "wave7.returned-invocation-with-running-background-result-never-gets-success-tick");
        view.ClearMessages();

        Check(OpenCodeToolRowPresentation.DisplayName("shell") == "终端" && OpenCodeToolRowPresentation.DisplayName("bash") == "终端"
            && OpenCodeToolRowPresentation.DisplayName("grep") == "搜索" && OpenCodeToolRowPresentation.DisplayName("read") == "读取"
            && OpenCodeToolRowPresentation.DisplayName("edit") == "编辑" && OpenCodeToolRowPresentation.DisplayName("write") == "写入"
            && OpenCodeToolRowPresentation.DisplayName("company_read") == "读取", "v5.renamed-built-in-display-names-are-localized");
        var unconfirmed = JsonSerializer.Deserialize<OpenCodeContentPart>("{\"id\":\"bg_unknown\",\"type\":\"tool\",\"name\":\"subagent\",\"state\":{\"status\":\"completed\",\"input\":{\"background\":true}}}", OpenCodeJson.Options)!;
        var unknownModel = OpenCodeToolRowPresentation.Build(unconfirmed);
        Check(unknownModel.Name == "子智能体" && unknownModel.IsBackground && unknownModel.Status == OpenCodeToolRowStatus.Unknown && unconfirmed.Name == "subagent",
            "wave7.background-return-without-job-state-is-unknown-not-success-and-protocol-is-unchanged");

        ConversationViewTestProbe.Reset();
        var foreground = M(new { id = "fg_origin", type = "assistant", content = new[] { new
        { id = "fg_call", type = "tool", name = "shell", state = new { status = "running", input = new { command = "work" } } } },
            time = new { created = 1700000000000d } });
        view.ApplyMessages(new[] { foreground });
        var foregroundGroup = Made<OpenCodeToolGroupView>().Single();
        Check(foregroundGroup.BackgroundAction is null, "wave7.background-action-hidden-until-real-transport-is-registered");
        var backgroundRequests = 0;
        view.SetBackgroundAction((parent, part) => { Check(parent == "fg_origin" && part.Id == "fg_call" && part.Name == "shell",
            "wave7.background-callback-preserves-current-parent-and-protocol-tool-snapshot"); backgroundRequests++; });
        foregroundGroup.BackgroundAction!();
        Check(backgroundRequests == 1 && foregroundGroup.Summary!.SuccessCount == 0 && foregroundGroup.LeafStatus == OpenCodeToolRowStatus.Running,
            "wave7.background-click-is-request-only-without-fake-local-completion-or-detachment");
        view.SetBackgroundAction(null);
        Check(foregroundGroup.BackgroundAction is null, "wave7.removing-background-transport-removes-action");

        var now = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
        Check(OpenCodeStepRunPresentation.FormatRetryCountdown(2, now.AddMilliseconds(2490), now) == "重试中 · 第 2 次 · 3 秒后重试"
            && OpenCodeStepRunPresentation.FormatRetryCountdown(2, now.AddMilliseconds(1), now).EndsWith("1 秒后重试", StringComparison.Ordinal)
            && OpenCodeStepRunPresentation.FormatRetryCountdown(2, now, now) == "正在重试…"
            && OpenCodeStepRunPresentation.FormatRetryCountdown(2, now.AddSeconds(-1), now) == "正在重试…",
            "RT1.retry-countdown-ceils-real-deadline-and-only-shows-attempting-after-expiry");
        view.IsLoaded = true;
        var retryDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
        view.SetCurrentRetry(new OpenCodeMessageRetry { Attempt = 2, At = retryDeadline.ToUnixTimeMilliseconds(),
            Error = new OpenCodeStructuredError { Message = "server retry reason" } });
        Check(view.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "OpenCodeCurrentRetry" && t.Text.StartsWith("重试中", StringComparison.Ordinal)),
            "wave7.live-retry-api-renders-actual-server-deadline-warning");
        var retryRow = view.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Single(t =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "OpenCodeCurrentRetry");
        OpenCodeWorkingClock.Shared.SetMotionEnabled(false);
        OpenCodeWorkingClock.Shared.TickSeconds(retryDeadline.AddSeconds(-3));
        Check(retryRow.Text == "重试中 · 第 2 次 · 3 秒后重试" && retryRow.Opacity == 1,
            "RT1.shared-clock-updates-visible-countdown-even-with-reduced-motion");
        view.RaiseUnloadedForTest();
        OpenCodeWorkingClock.Shared.TickSeconds(retryDeadline);
        Check(retryRow.Text.EndsWith("3 秒后重试", StringComparison.Ordinal) && !OpenCodeWorkingClock.Shared.SecondsRunning,
            "RT1.unload-leaves-shared-clock-without-background-countdown");
        view.RaiseLoadedForTest();
        OpenCodeWorkingClock.Shared.TickSeconds(retryDeadline);
        Check(retryRow.Text == "正在重试…", "RT1.loaded-countdown-resumes-and-expired-deadline-is-attempting");
        view.SetCurrentTurnActivity(false);
        Check(!view.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(t) == "OpenCodeCurrentRetry"), "wave7.authoritative-terminal-clears-live-retry-warning");
        Check(!OpenCodeWorkingClock.Shared.SecondsRunning, "RT1.terminal-clears-countdown-subscription");
        foreach (var unknown in new OpenCodeMessageRetry[] { new() { Attempt = 2 }, new() { At = retryDeadline.ToUnixTimeMilliseconds() }, new() { Attempt = 2, At = 123 } })
        {
            view.SetCurrentRetry(unknown);
            Check(!view.Children.Contains(retryRow) && !OpenCodeWorkingClock.Shared.SecondsRunning, "RT1.unknown-or-unverified-fields-never-invent-countdown");
        }
        OpenCodeWorkingClock.Shared.SetMotionEnabled(true);
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        view.ApplyMessages(new[] { M(new { id = "error", type = "assistant", error = new { type = "upstream_error", message = "first line\nfull detail" } }) });
        var errorRow = Walk(view).OfType<Microsoft.UI.Xaml.Controls.Grid>().Single(g => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(g) == "OpenCodeAssistantError");
        var errorText = errorRow.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Single();
        Check(errorRow.Children.Count == 2 && errorRow.Children.OfType<Microsoft.UI.Xaml.Controls.FontIcon>().Single().Glyph == "\uEB90"
            && errorText.MaxLines == 1 && errorText.IsTextSelectionEnabled && errorText.Text.Contains("first line\nfull detail", StringComparison.Ordinal)
            && !Walk(errorRow).OfType<Microsoft.UI.Xaml.Controls.Button>().Any(),
            "wave7.error-is-noninteractive-left-circular-x-plus-selectable-single-line-full-source-without-right-status");
        view.ClearMessages();

        ConversationViewTestProbe.Reset();
        var admitted = new OpenCodeInboxItem { Id = "msg_steer", SessionId = "ses_ui", Type = "user", Delivery = "steer",
            Payload = JsonSerializer.SerializeToElement(new { text = "  raw\nbody  ", files = new[] { new { name = "image.png", uri = "data:image/png;base64,AAAA" } } }) };
        var deferred = admitted with { Id = "msg_queue", Delivery = "queue" };
        var foreign = admitted with { Id = "msg_foreign", SessionId = "ses_elsewhere" };
        var originalPayload = admitted.Payload!.Value.GetRawText();
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted, deferred, foreign }, _ => true, _ => true);
        var steerRow = view.Children.OfType<Microsoft.UI.Xaml.Controls.ContentControl>().Single(p =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(p) == "OpenCodeAdmittedSteer.msg_steer");
        Check(view.Children.Count == 1 && !view.ContainsRenderedMessage("msg_steer") && Made<OpenCodeUserBubbleView>().Length == 1,
            "wave7.admitted-steer-is-normal-chat-bubble-projection-only-not-queue-foreign-session-or-fabricated-history");
        Check(!Made<OpenCodeUserBubbleView>().Single().Delivered,
            "wave8.real-durable-steer-admission-is-neutral-not-delivered-accent");
        var steerMenu = (Microsoft.UI.Xaml.Controls.MenuFlyout)steerRow.ContextFlyout!;
        Check(!Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t => t.Text == "插入当前回合")
            && !Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.Button>().Any()
            && steerRow.IsTabStop && steerMenu.Items.Select(i => i.Text).SequenceEqual(new[] { "改为排队", "撤回" }),
            "MQ3.owned-steer-has-native-keyboard-focusable-context-menu-with-both-actions-and-no-hover-buttons");
        Check(ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is "  raw\nbody  ")
            && Walk(steerRow).OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t => t.Text == "附件：image.png")
            && admitted.Payload!.Value.GetRawText() == originalPayload,
            "wave7.steer-bubble-preserves-raw-text-and-attachment-payload-and-displays-actual-attachment-name");
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted, deferred }, _ => true, _ => true);
        Check(ReferenceEquals(view.Children.Single(), steerRow) && Made<OpenCodeUserBubbleView>().Length == 1,
            "wave7.same-admitted-snapshot-reuses-bubble-identity");
        var withdrawals = 0; var changes = 0;
        view.PendingSteerWithdrawRequested += (_, id) => { if (id == "msg_steer") withdrawals++; };
        view.PendingSteerDeliveryChangeRequested += (_, request) => { if (request.InboxId == "msg_steer" && request.Delivery == "queue") changes++; };
        var withdrawButton = steerMenu.Items.Single(b => b.Text == "撤回");
        var deliveryButton = steerMenu.Items.Single(b => b.Text == "改为排队");
        view.SetPendingSteerBusy(true); withdrawButton.RaiseClickForTest();
        deliveryButton.RaiseClickForTest();
        Check(withdrawals == 0 && changes == 0 && !withdrawButton.IsEnabled && !deliveryButton.IsEnabled,
            "wave7.steer-actions-respect-existing-single-flight-busy-state");
        view.SetPendingSteerBusy(false); withdrawButton.RaiseClickForTest();
        deliveryButton.RaiseClickForTest();
        Check(withdrawals == 1 && changes == 1 && admitted.Delivery == "steer", "wave7.steer-actions-forward-real-id-without-optimistic-delivery-rewrite-or-send");
        var changeAllowed = false;
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted }, _ => true, _ => changeAllowed);
        steerMenu = (Microsoft.UI.Xaml.Controls.MenuFlyout)steerRow.ContextFlyout!;
        Check(steerMenu.Items.Count == 2 && !steerMenu.Items[0].IsEnabled && steerMenu.Items[1].IsEnabled,
            "MQ3.unavailable-action-remains-disabled-in-owned-menu");
        changeAllowed = true;
        steerMenu.OpenForTest();
        Check(steerMenu.Items.All(i => i.IsEnabled), "MQ3.opening-rechecks-current-action-guards");
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted }, _ => false, _ => false);
        Check(steerRow.ContextFlyout is null && !steerRow.IsTabStop,
            "MQ3.other-client-steer-remains-read-only-without-menu-or-extra-tab-stop");
        withdrawButton.RaiseClickForTest(); deliveryButton.RaiseClickForTest();
        Check(withdrawals == 1 && changes == 1, "MQ3.stale-menu-items-recheck-ownership-before-forwarding");
        view.ApplyAdmittedSteer("ses_ui", new[] { admitted }, _ => true, _ => true);
        steerMenu = (Microsoft.UI.Xaml.Controls.MenuFlyout)steerRow.ContextFlyout!;
        view.ApplyMessages(new[] { M(new { id = "msg_steer", type = "user", text = "  raw\nbody  " }) });
        Check(steerMenu.Items.All(i => !i.IsEnabled), "MQ3.history-delivery-disables-detached-menu-actions");
        withdrawButton.RaiseClickForTest();
        Check(view.ContainsRenderedMessage("msg_steer") && !view.Children.Contains(steerRow) && view.Children.Count == 1 && withdrawals == 1,
            "wave7.actual-user-history-id-replaces-projected-bubble-and-disables-detached-stale-actions");
        Check(Made<OpenCodeUserBubbleView>().Last().Delivered,
            "wave8.same-real-accepted-history-id-replaces-neutral-admission-with-accent-bubble");
        view.ApplyAdmittedSteer(null, null);
        Check(view.Children.Count == 1, "wave7.clearing-steer-binding-never-removes-real-history");
        view.ClearMessages();

        view.ApplyMessages(new[] { instruction });
        Check(ReferenceEquals(view.Children.Single(), Made<OpenCodeInstructionNoticeView>().Last().Element),
            "rp3.single-instruction-is-standalone-chip");
        view.ClearMessages();
        ConversationViewTestProbe.Reset();
        var modelSwitch = M(new { id = "model-switch", type = "model-switched", model = new { id = "opaque-model", providerID = "provider" } });
        view.ApplyMessages(new[] { instruction, modelSwitch, two });
        var modelChip = Made<OpenCodeTurnChipView>().Single();
        Check(ReferenceEquals(view.Children[0], Made<OpenCodeInstructionNoticeView>().Single().Element)
            && ReferenceEquals(view.Children[1], modelChip.Element)
            && Made<OpenCodeToolGroupView>().All(g => !g.ItemsHost.Children.Contains(modelChip.Element)),
            "rp3.instruction-and-model-chips-above-work-never-inside-processing-group");
        Check(OpenCodeToolRowPresentation.DisplayName("acp_status") == "上下文状态"
            && OpenCodeToolRowPresentation.DisplayName("compress") == "压缩上下文"
            && OpenCodeToolRowPresentation.DisplayName("third_party_acp_status") == "third_party_acp_status",
            "wave7.confirmed-context-tools-have-exact-display-only-aliases-without-mcp-suffix-renaming");
        var messageTool = JsonSerializer.Deserialize<OpenCodeContentPart>("{\"id\":\"message_tool\",\"type\":\"tool\",\"name\":\"task_message\",\"state\":{\"status\":\"running\",\"input\":{\"text\":\"hello\"}}}", OpenCodeJson.Options)!;
        var messageModel = OpenCodeToolRowPresentation.Build(messageTool);
        Check(messageModel.Name == "给子智能体发消息" && messageModel.GlyphOverride == OpenCodeStepRunPresentation.SubagentGlyph
            && messageModel.Kind == OpenCodeToolKind.Other && !messageModel.CanMoveToBackground && messageTool.Name == "task_message",
            "wave7.task-message-is-display-only-robot-alias-never-backgroundable-child-execution");
        view.ClearMessages();
        ConversationViewTestProbe.Reset();
        var restartText = OpenCodeNoticeDismissals.AutomaticRestartText;
        view.ApplyMessages(new[] { M(new { id = "restart", type = "synthetic", text = restartText }), M(new { id = "ordinary", type = "user", text = restartText }), M(new { id = "quoted", type = "user", text = "> " + restartText }) });
        Check(Walk(view).OfType<Microsoft.UI.Xaml.Controls.TextBlock>().Any(t => t.Text == "重启后继续")
            && ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is string raw && raw == restartText)
            && ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is string t && t == "> " + restartText),
            "wave7.only-entire-known-synthetic-notice-is-translated-not-user-or-quoted-body");
        view.ClearMessages();
        ConversationViewTestProbe.Reset();
        var manualCall = M(new { id = "manual_parent", type = "assistant", content = new[] { new { id = "manual_tool", type = "tool", name = "shell", time = new { ran = 1000 }, state = new { status = "completed", input = new { command = "inert manual test" } } } } });
        view.ApplyMessages(new[] { manualCall });
        Made<OpenCodeToolGroupView>().Single().OpenLeaf!();
        var manualController = Made<OpenCodeToolPartView>().Single();
        var beforeManual = manualController.UpdateCount;
        var manualNotice = M(new { id = "manual_notice", type = "synthetic", time = new { created = 2000 }, text = "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n- shell: inert manual test\n\nThe backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation." });
        view.ApplyMessages(new[] { manualCall, manualNotice });
        Check(manualController.UpdateCount > beforeManual && manualController.ManualBackground,
            "wave7.manual-index-updates-unchanged-real-tool-controller-on-accepted-raw-notice");
        var manualModel = OpenCodeToolRowPresentation.Build(manualCall.Content![0], null, manualController.ManualBackground);
        Check(manualModel.Name == "终端（后台）" && manualModel.Status == OpenCodeToolRowStatus.Running
            && manualModel.Summary == "后台运行中" && !manualModel.CanMoveToBackground,
            "wave7.manual-returned-invocation-renders-background-spinner-not-success-or-new-action");
        view.ClearMessages();
        view.ApplyMessages(new[] { manualCall });
        Made<OpenCodeToolGroupView>().Last().OpenLeaf!();
        Check(!Made<OpenCodeToolPartView>().Last().ManualBackground, "wave7.manual-binding-reset-clears-fallback-associations");
        view.ClearMessages();
        ConversationViewTestProbe.Reset();
        // Exact user-supplied machine payload is DATA ONLY. Never start a process or expand shell syntax.
        const string noticeCommand = "nixos-rebuild switch --flake \"path:.#$(hostname)\" --sudo";
        const string noticeBody = "User requested that active blocking work be moved to the background.\n\nBackgrounded work:\n- shell: " + noticeCommand
            + "\n\nThe backgrounded work is still unfinished. Move on to other work if you can. If there is nothing else useful to do, finish your response. Do not wait, sleep, poll, or report the backgrounded work as complete until a later completion notification is added to the conversation.";
        var suppliedNotice = M(new { id = "supplied_notice", type = "synthetic", time = new { created = 2000 }, text = noticeBody });
        var originalNoticeJson = JsonSerializer.Serialize(suppliedNotice);
        view.ApplyMessages(new[] { suppliedNotice });
        var noticeChip = Made<OpenCodeTimelineChipView>().Single();
        Check(noticeChip.Label == "终端（后台）" && noticeChip.Glyph == OpenCodeStepRunPresentation.TerminalGlyph
            && noticeChip.Content.Target == noticeCommand && noticeChip.Content.Summary == "后台运行中"
            && noticeChip.Content.Status == OpenCodeToolRowStatus.Running && noticeChip.LinkInvoked is null,
            "wave13.actual-known-notice-consumer-projects-original-command-compact-running-terminal-without-job-action-or-success");
        Check(!Made<OpenCodeUserBubbleView>().Any() && !Made<OpenCodeSubagentCompletionView>().Any()
            && !ConversationViewTestProbe.Records.Any(r => r.Operation == "Render" && r.Input is string s && s == noticeBody)
            && JsonSerializer.Serialize(suppliedNotice) == originalNoticeJson && view.ContainsRenderedMessage("supplied_notice"),
            "wave13.no-giant-english-user-or-synthetic-body-no-completion-wrapper-raw-dto-and-original-notice-id-retained");
        var chipCount = Made<OpenCodeTimelineChipView>().Length;
        view.ApplyMessages(new[] { suppliedNotice });
        Check(Made<OpenCodeTimelineChipView>().Length == chipCount,
            "wave13.unchanged-accepted-notice-reuses-entry-without-extra-operation");
        view.ClearMessages(); ConversationViewTestProbe.Reset();
        var partOriginNotice = M(new { id = "part_origin_notice", type = "user", content = new[] { new { id = "original_notice_part", type = "text", synthetic = true, text = noticeBody } } });
        Check(partOriginNotice.Content![0].Synthetic == true && OpenCodeManualBackgroundIndex.TryReadNotice(partOriginNotice, out var originItems)
            && originItems.Single().Title == noticeCommand, "wave13.actual-json-deserializer-retains-explicit-part-level-machine-origin-not-envelope-type-inference");
        view.ApplyMessages(new[] { partOriginNotice });
        Check(Made<OpenCodeTimelineChipView>().Single().Label == "终端（后台）" && !Made<OpenCodeUserBubbleView>().Any(),
            "wave13.user-envelope-explicit-synthetic-text-part-reaches-real-compact-consumer");
        foreach (var ordinary in new[] {
            M(new { id = "pasted", type = "user", text = noticeBody }),
            M(new { id = "false_origin", type = "user", content = new[] { new { type = "text", synthetic = false, text = noticeBody } } }),
            M(new { id = "mixed_origin", type = "user", text = "real user content", content = new[] { new { type = "text", synthetic = true, text = noticeBody }, new { type = "text", synthetic = false, text = "real user content" } } }),
            M(new { id = "partial", type = "synthetic", text = noticeBody[..^1] }),
            M(new { id = "quoted", type = "synthetic", text = "> " + noticeBody }) })
            Check(!OpenCodeManualBackgroundIndex.TryReadNotice(ordinary, out _),
                "wave13.no-user-paste-false-mixed-part-origin-partial-or-quoted-machine-inference:" + ordinary.Id);
        view.ClearMessages(); ConversationViewTestProbe.Reset();
        var suppliedCall = M(new { id = "supplied_parent", type = "assistant", content = new[] { new { id = "supplied_tool", type = "tool", name = "shell", time = new { ran = 1000 }, state = new { status = "completed", input = new { command = noticeCommand } } } } });
        view.ApplyMessages(new[] { suppliedCall, suppliedNotice });
        Made<OpenCodeToolGroupView>().First(g => g.OpenLeaf is not null).OpenLeaf!();
        Check(Made<OpenCodeToolPartView>().Single().ManualBackground && !Made<OpenCodeTimelineChipView>().Any()
            && Made<OpenCodeToolGroupView>().Count(g => g.Summary is not null && g.Summary.SuccessCount == 0) >= 1,
            "wave13.exact-unique-existing-call-owns-operation-no-duplicate-notice-chip-and-invocation-return-is-not-background-complete");
        var terminalCall = M(new { id = "supplied_parent", type = "assistant", content = new[] { new { id = "supplied_tool", type = "tool", name = "shell", time = new { ran = 1000 }, state = new { status = "completed", input = new { command = noticeCommand }, metadata = new { status = "completed", shellID = "sh_actual" } } } } });
        view.ApplyMessages(new[] { terminalCall, suppliedNotice });
        Check(!Made<OpenCodeToolPartView>().Last().ManualBackground && !Made<OpenCodeTimelineChipView>().Any(),
            "wave13.actual-keyed-terminal-metadata-wins-no-revived-unfinished-notice-or-fabricated-completion-record");
        view.ClearMessages(); ConversationViewTestProbe.Reset();
        var secondCall = suppliedCall with { Id = "second_parent", Content = new() { suppliedCall.Content![0] with { Id = "second_tool" } } };
        view.ApplyMessages(new[] { suppliedCall, secondCall, suppliedNotice });
        Made<OpenCodeToolGroupView>().First(g => g.HeaderVisible).ToggleForTest();
        Check(Made<OpenCodeToolPartView>().Length == 2 && Made<OpenCodeToolPartView>().All(p => !p.ManualBackground)
            && Made<OpenCodeTimelineChipView>().Single().Content.Status == OpenCodeToolRowStatus.Running,
            "wave13.ambiguous-history-keeps-notice-truthful-running-without-guessing-an-association");
        view.ClearMessages(); ConversationViewTestProbe.Reset();
        var multipleNotice = suppliedNotice with { Text = noticeBody.Replace("- shell: " + noticeCommand, "- shell: " + noticeCommand + "\n- subagent: inert child descriptor", StringComparison.Ordinal) };
        view.ApplyMessages(new[] { multipleNotice });
        Check(Made<OpenCodeTimelineChipView>().Length == 1 && Made<OpenCodeTimelineChipView>()[0].Glyph == OpenCodeStepRunPresentation.TerminalGlyph
            && Made<OpenCodeTimelineChipView>().All(p => p.Content.Status == OpenCodeToolRowStatus.Running),
            "v5.stale-subagent-background-snapshot-never-creates-another-running-row-terminal-notice-retained");
        view.ClearMessages();
        Console.WriteLine($"PASS wave6 timeline: {_checks} assertions; real renderer/policies with inert presenters, no UI.");
    }
}
