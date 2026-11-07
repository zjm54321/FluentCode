using System;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private const string UpdateId = "fe062fc7-a0db-463f-89e8-1d19ae6ed98a";

    private static string UpdateText(string kind, string message)
        => "<subagent-update id=\"" + UpdateId + "\" kind=\"" + kind + "\">\n"
            + "From @designer (session ses_notice_child):\n" + message + "\n</subagent-update>\n<!-- SLIM_INTERNAL_INITIATOR -->";

    private static OpenCodeMessage UpdateMessage(string text) => new()
    {
        Id = "msg_subagent_update", Type = "user",
        Content = new() { new() { Type = "text", Synthetic = true, Text = text } },
    };

    private static void SubagentUpdates()
    {
        var before = _assertions;
        foreach (var kind in new[] { "progress", "escalation" })
        {
            const string body = "首行汇报\n完整消息的第二行";
            var notice = UpdateMessage(UpdateText(kind, body));
            Check(OpenCodeStepRunPresentation.IsInternalInitiator(notice)
                && OpenCodeStepRunPresentation.TryReadSubagentUpdate(notice, out var parsed)
                && parsed.Id == UpdateId && parsed.Kind == kind && parsed.Agent == "designer"
                && parsed.ChildSessionId == "ses_notice_child" && parsed.Message == body, "update.parses-native-synthetic-text-part");
            Check(!OpenCodeStepRunPresentation.TryReadAsyncCompletion(notice, out _), "update.never-parses-as-terminal-notice");
            ConversationViewTestProbe.Reset();
            var view = new OpenCodeConversationView();
            var call = SubagentCall("正在执行的子任务");
            view.ApplyMessages(new[] { call });
            Constructed<OpenCodeToolGroupView>().Single().OpenLeaf!();
            var tool = Constructed<OpenCodeToolPartView>().Single();
            view.ApplyMessages(new[] { call, notice });
            var row = Constructed<OpenCodeTimelineChipView>().Single();
            var label = kind == "progress" ? "汇报进度" : "上报问题";
            Check(row.Content.Name == label && row.Content.Target == "Designer · 首行汇报" && row.Content.DetailText == body
                && !row.Content.AccessibleName.Contains(UpdateId, StringComparison.Ordinal), "update.existing-chip-label-excerpt-full-popup-and-no-uuid");
            Check(row.Content.AgentName == "designer" && row.Glyph == OpenCodeAgentIcons.Glyph("designer"), "update.reuses-agent-glyph");
            Check(row.Content.IsEscalation == (kind == "escalation") && row.Content.Status is null && !row.CompletionNotice
                && OpenCodeToolRowChrome.For(row.Content.Status, row.Content.IsEscalation).TextKey == (kind == "escalation" ? "SystemFillColorCautionBrush" : null),
                "update.escalation-is-caution-never-marked-complete");
            Check(!ReferenceEquals(row.Element.Parent, view)
                && Constructed<OpenCodeToolGroupView>().Any(group => ReferenceEquals(row.Element.Parent, group.ItemsHost)) && tool.AsyncCompletion is null
                && OpenCodeToolRowPresentation.Build(call.Content![0]).Status == OpenCodeToolRowStatus.Running,
                "update.folded-initiator-does-not-complete-background-child");
            OpenCodeSubagentSessionRequestedEventArgs? navigation = null;
            view.SubagentSessionRequested += (_, args) => navigation = args;
            var click = row.LinkInvoked!;
            click();
            Check(navigation is { ParentMessageId: "msg_subagent_update", ChildSessionId: "ses_notice_child" }, "update.same-child-navigation-affordance");
            view.ApplyMessages(new[] { call, notice });
            Check(ReferenceEquals(row, Constructed<OpenCodeTimelineChipView>().Single()), "update.retained-snapshot-keeps-one-chip");
            view.ApplyMessages(new[] { call, UpdateMessage(UpdateText(kind, "修订全文\n第二行仍完整")) });
            Check(row.DetailText == "修订全文\n第二行仍完整" && row.Content.Target == "Designer · 修订全文", "update.changed-text-refreshes-existing-chip");
            view.ApplyMessages(new[] { call, UpdateMessage(UpdateText(kind, body).Replace(UpdateId, "bad-id", StringComparison.Ordinal)) });
            navigation = null; click();
            Check(row.Label == OpenCodeStepRunPresentation.InternalInitiatorLabel && row.LinkInvoked is null && navigation is null,
                "update.malformed-revision-falls-back-and-revokes-old-link");
            view.ClearMessages(); navigation = null; click();
            Check(row.DisposeCount == 1 && navigation is null, "update.disposal-revokes-stored-child-navigation");
        }

        var valid = UpdateText("progress", "第一行\n第二行");
        Check(OpenCodeStepRunPresentation.IsInternalInitiator(UpdateMessage(valid.Replace("<!-- SLIM_INTERNAL_INITIATOR -->", "", StringComparison.Ordinal)))
            && OpenCodeStepRunPresentation.TryReadSubagentUpdate(UpdateMessage(valid.Replace("<!-- SLIM_INTERNAL_INITIATOR -->", "", StringComparison.Ordinal)), out _),
            "update.native-envelope-without-comment-is-still-internal");
        Check(OpenCodeStepRunPresentation.TryParseSubagentUpdateText(valid.Replace("\n", "\r\n", StringComparison.Ordinal), out var crlf)
            && crlf.Message == "第一行\r\n第二行", "update.crlf-preserves-full-message");
        Check(OpenCodeStepRunPresentation.TryParseSubagentUpdateText(valid.Replace("id=\"" + UpdateId + "\" kind=\"progress\"",
            "kind=\"progress\" id=\"" + UpdateId + "\"", StringComparison.Ordinal), out _), "update.header-attribute-order-is-not-significant");
        Check(OpenCodeStepRunPresentation.TryParseSubagentUpdateText(UpdateText("progress", new string('x', 2000)), out var maximum)
            && maximum.Message.Length == 2000 && OpenCodeStepRunPresentation.DescribeSubagentUpdate(maximum).Target == "Designer · " + new string('x', 80) + "…",
            "update.maximum-full-body-kept-with-bounded-excerpt");
        Check(!OpenCodeStepRunPresentation.TryReadSubagentUpdate(UpdateMessage(valid) with { Type = "assistant" }, out _), "update.assistant-text-is-not-an-initiator");
        foreach (var malformed in new[]
        {
            valid.Replace(" id=\"" + UpdateId + "\"", "", StringComparison.Ordinal),
            valid.Replace(UpdateId, "not-a-uuid", StringComparison.Ordinal),
            valid.Replace("kind=\"progress\"", "kind=\"other\"", StringComparison.Ordinal),
            valid.Replace("kind=\"progress\"", "id=\"" + UpdateId + "\"", StringComparison.Ordinal),
            valid.Replace("From @designer", "From designer", StringComparison.Ordinal),
            valid.Replace("ses_notice_child", "https://untrusted.invalid", StringComparison.Ordinal),
            valid.Replace("</subagent-update>", "", StringComparison.Ordinal),
            UpdateText("progress", "   "),
            UpdateText("escalation", new string('x', 2001)),
        })
        {
            var notice = UpdateMessage(malformed);
            Check(OpenCodeStepRunPresentation.IsInternalInitiator(notice)
                && !OpenCodeStepRunPresentation.TryReadSubagentUpdate(notice, out _), "update.malformed-envelope-rejected");
            ConversationViewTestProbe.Reset();
            var view = new OpenCodeConversationView();
            view.ApplyMessages(new[] { notice });
            var fallback = Constructed<OpenCodeTimelineChipView>().Single();
            Check(fallback.Label == OpenCodeStepRunPresentation.InternalInitiatorLabel && fallback.DetailText == malformed
                && fallback.LinkInvoked is null && fallback.Content.Status is null && !fallback.CompletionNotice, "update.malformed-keeps-generic-initiator-details");
            view.ClearMessages();
        }
        FoldedUpdateGroups();
        ViewportContentBottom();
        Console.WriteLine($"PASS subagent update initiator chips: {_assertions - before} assertions; real parser/ConversationView, inert existing chip visuals.");
    }

    private static void FoldedUpdateGroups()
    {
        var user = new OpenCodeMessage { Id = "msg_fold_user", Type = "user", Text = "go", Time = new() { Created = 1, Completed = 1 } };
        var first = Assistant("msg_fold_first", completed: 35,
            Tool("fold_read", "read", "completed", 10, 20), Tool("fold_glob", "glob", "completed", 20, 30));
        var running = Assistant("msg_fold_running", completed: null,
            Tool("fold_last_read", "read", "completed", 40, 50), Tool("fold_shell", "bash", "running", 50, null));
        var progress = UpdateMessage(UpdateText("progress", "Headless failure reproduced…")) with { Id = "msg_fold_progress" };
        var escalation = UpdateMessage(UpdateText("escalation", "需要主会话处理的问题")) with { Id = "msg_fold_escalation" };

        // Updates are rows in the already-running chain; they neither finish it nor become its current step.
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        view.ApplyMessages(new[] { user, first, running });
        var leader = HeaderGroups().Single();
        var baseline = leader.Summary!;
        var baselineRows = Constructed<OpenCodeToolGroupView>().Sum(group => group.ItemsHost.Children.Count);
        var currentStep = leader.CurrentStep;
        view.ApplyMessages(new[] { user, first, progress, running, escalation });
        Check(HeaderGroups().Length == 1 && ReferenceEquals(HeaderGroups()[0], leader)
            && leader.Summary is { IsActive: true } && leader.IsExpanded, "fold.updates-between-steps-and-latest-do-not-split-running-group");
        Check(Constructed<OpenCodeToolGroupView>().Sum(group => group.ItemsHost.Children.Count) == baselineRows + 2
            && leader.Summary == baseline, "fold.updates-count-as-rows-not-completions-or-failures");
        Check(leader.CurrentStep == currentStep && leader.WorkingTool, "fold.latest-escalation-does-not-replace-the-real-running-step");
        var chips = Constructed<OpenCodeTimelineChipView>().ToArray();
        Check(chips.Length == 2 && chips.All(chip => !ReferenceEquals(chip.Element.Parent, view)
            && Constructed<OpenCodeToolGroupView>().Any(group => ReferenceEquals(chip.Element.Parent, group.ItemsHost))), "fold.update-visuals-live-in-expanded-group-item-hosts");
        view.BindIncrementalSnapshot(view, 1);
        var changed = running with { Content = new() { Tool("fold_last_read", "read", "completed", 40, 55), running.Content![1] } };
        Check(view.TryApplyMessageChanges(new[] { changed }, view, 1, 2), "fold.latest-update-does-not-move-the-real-assistant-out-of-the-latest-turn");
        view.ClearMessages();

        // Existing prose is still a real group boundary; inserting updates adds no third group or state transition.
        ConversationViewTestProbe.Reset();
        var separated = new OpenCodeConversationView();
        var withText = first with { Content = new(first.Content!) { Text("fold_explanation", "阶段说明") } };
        separated.ApplyMessages(new[] { user, withText, running });
        var groups = HeaderGroups();
        var firstHeader = groups[0].Summary!;
        var lastHeader = groups[1].Summary!;
        separated.ApplyMessages(new[] { user, withText, progress, running, escalation });
        groups = HeaderGroups();
        Check(groups.Length == 2 && groups[0].Summary is { IsActive: false } && groups[1].Summary is { IsActive: true },
            "fold.updates-between-existing-groups-preserve-only-the-prose-boundary");
        Check(groups[0].Summary!.SuccessCount == firstHeader.SuccessCount && groups[0].Summary!.ErrorCount == firstHeader.ErrorCount
            && groups[1].Summary!.SuccessCount == lastHeader.SuccessCount && groups[1].Summary!.ErrorCount == lastHeader.ErrorCount,
            "fold.both-existing-group-tallies-stay-unchanged");
        Check(groups[0].Summary!.HeaderText == firstHeader.HeaderText && !groups[0].IsExpanded && groups[1].IsExpanded,
            "fold.completed-group-duration-and-expansion-are-not-changed-by-updates");
        separated.ClearMessages();

        // A latest update after an idle signal stays in the previous processed group, even if activity is later authoritative.
        ConversationViewTestProbe.Reset();
        var finished = new OpenCodeConversationView();
        var idle = new OpenCodeMessage { Id = "msg_fold_idle", Type = "idle", Outcome = "succeeded", Time = new() { Created = 100 } };
        finished.ApplyMessages(new[] { user, first, idle });
        var processed = HeaderGroups().Single();
        var processedSummary = processed.Summary!;
        finished.ApplyMessages(new[] { user, first, idle, escalation });
        finished.SetCurrentTurnActivity(true);
        Check(HeaderGroups().Length == 1 && ReferenceEquals(HeaderGroups()[0], processed) && processed.Summary is { IsActive: false }
            && processed.Summary.SuccessCount == processedSummary.SuccessCount && processed.Summary.ErrorCount == processedSummary.ErrorCount,
            "fold.latest-update-never-starts-a-turn-or-reopens-a-processed-group");
        finished.ClearMessages();

        // Initiators before the first step attach to that step; generic fallback uses exactly the same neutral path.
        ConversationViewTestProbe.Reset();
        var leading = new OpenCodeConversationView();
        var generic = UpdateMessage("内部消息\n<!-- SLIM_INTERNAL_INITIATOR -->") with { Id = "msg_fold_generic" };
        leading.ApplyMessages(new[] { user, progress, running, generic });
        Check(HeaderGroups().Length == 1 && HeaderGroups()[0].Summary is { IsActive: true, SuccessCount: 1, ErrorCount: 0 }
            && Constructed<OpenCodeToolGroupView>().Sum(group => group.ItemsHost.Children.Count) == 4,
            "fold.leading-update-and-generic-initiator-use-one-neutral-running-chain");
        Check(Constructed<OpenCodeTimelineChipView>().All(chip => !ReferenceEquals(chip.Element.Parent, leading)),
            "fold.generic-initiators-also-remain-folded");
        leading.ClearMessages();

        ConversationViewTestProbe.Reset();
        var noticesOnly = new OpenCodeConversationView();
        noticesOnly.ApplyMessages(new[] { progress, escalation });
        Check(HeaderGroups().Length == 1 && HeaderGroups()[0].Summary is { IsActive: false, SuccessCount: 0, ErrorCount: 0 }
            && Constructed<OpenCodeTimelineChipView>().Count() == 2 && !HeaderGroups()[0].IsExpanded,
            "fold.notices-without-work-share-one-neutral-processed-header");
        noticesOnly.ClearMessages();

        var fixer = OpenCodeStepRunPresentation.DescribeSubagentUpdate(new(UpdateId, "progress", "fixer", "ses_notice_child", "Headless failure reproduced…"));
        var subagent = OpenCodeToolRowPresentation.Build(new OpenCodeContentPart { Type = "tool", Name = "subagent",
            State = JsonSerializer.SerializeToElement(new { status = "completed", input = new { agent = "fixer", description = "Headless failure reproduced…" } }) });
        Check(fixer.Name == "汇报进度" && fixer.Target == subagent.Target && fixer.Target == "Fixer · Headless failure reproduced…"
            && !fixer.AccessibleName.Contains('@') && fixer.Properties.Single(property => property.Key == "智能体").Value == "Fixer",
            "fold.naming-matches-the-subagent-row-helper-without-at-prefix");
    }

    private static void ViewportContentBottom()
    {
        var view = new OpenCodeConversationView();
        view.ApplyMessages(new OpenCodeMessage[] {
            new() { Id = "msg_view_user", Type = "user", Text = "question", Time = new() { Created = 1 } },
            new() { Id = "msg_view_answer", Type = "assistant", Time = new() { Created = 2 }, Content = new() { new() { Type = "text", Text = "answer" } } },
            new() { Id = "msg_view_idle", Type = "idle", Outcome = "succeeded", Time = new() { Created = 100 } },
        });
        var viewport = new Microsoft.UI.Xaml.Controls.ScrollView();
        var presenter = new Microsoft.UI.Xaml.Controls.ScrollContentPresenter { Content = view };
        viewport.Content = presenter;
        var root = new Microsoft.UI.Xaml.Controls.Border { Child = viewport };
        var xamlRoot = new Microsoft.UI.Xaml.XamlRoot();
        void Geometry(Microsoft.UI.Xaml.UIElement node, double top = 0)
        {
            if (node is Microsoft.UI.Xaml.FrameworkElement element)
            {
                element.IsLoaded = true; element.XamlRoot = xamlRoot; element.ActualWidth = element.ActualHeight = 500;
                element.TestTransform = _ => new Microsoft.UI.Xaml.Media.GeneralTransform(top);
            }
            if (node is Microsoft.UI.Xaml.Controls.Panel panel) foreach (var child in panel.Children) Geometry(child, top);
            if (node is Microsoft.UI.Xaml.Controls.Border { Child: { } borderChild }) Geometry(borderChild, top);
            if (node is Microsoft.UI.Xaml.Controls.ContentControl { Content: Microsoft.UI.Xaml.UIElement content }) Geometry(content, top);
        }
        Geometry(root);
        var idle = view.Children[^1];
        Geometry(idle, 600); // End separator is entirely below the viewport.
        var answer = (Microsoft.UI.Xaml.FrameworkElement)view.Children[^2];
        answer.ActualHeight = 800; answer.TestTransform = _ => new Microsoft.UI.Xaml.Media.GeneralTransform(-300);
        Check(view.CaptureIdleViewport(viewport).Candidate is null, "viewport.content-bottom-before-layout-does-not-ack");
        view.RaiseLayoutUpdatedForTest();
        Check(view.CaptureIdleViewport(viewport).Candidate?.Watermark == 100,
            "viewport.latest-answer-bottom-visible-with-offscreen-idle-separator");
        answer.TestTransform = _ => new Microsoft.UI.Xaml.Media.GeneralTransform(-299);
        Check(view.CaptureIdleViewport(viewport).Candidate is null, "viewport.answer-bottom-below-viewport-does-not-ack");
        answer.TestTransform = _ => new Microsoft.UI.Xaml.Media.GeneralTransform(-300);
        answer.Opacity = 0;
        Check(view.CaptureIdleViewport(viewport).Candidate is null, "viewport.hidden-answer-never-proves-view");
        answer.Opacity = 1;
        viewport.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        Check(view.CaptureIdleViewport(viewport).Candidate is null, "viewport.hidden-ancestor-never-proves-view");
        view.ClearMessages();
    }
}
