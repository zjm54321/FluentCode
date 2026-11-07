using System;
using System.Linq;
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
            var label = "@designer " + (kind == "progress" ? "汇报进度" : "上报问题");
            Check(row.Content.Name == label && row.Content.Target == "· 首行汇报" && row.Content.DetailText == body
                && !row.Content.AccessibleName.Contains(UpdateId, StringComparison.Ordinal), "update.existing-chip-label-excerpt-full-popup-and-no-uuid");
            Check(row.Content.AgentName == "designer" && row.Glyph == OpenCodeAgentIcons.Glyph("designer"), "update.reuses-agent-glyph");
            Check(row.Content.IsEscalation == (kind == "escalation") && row.Content.Status is null && !row.CompletionNotice
                && OpenCodeToolRowChrome.For(row.Content.Status, row.Content.IsEscalation).TextKey == (kind == "escalation" ? "SystemFillColorCautionBrush" : null),
                "update.escalation-is-caution-never-marked-complete");
            Check(ReferenceEquals(row.Element.Parent, view) && tool.AsyncCompletion is null
                && OpenCodeToolRowPresentation.Build(call.Content![0]).Status == OpenCodeToolRowStatus.Running,
                "update.standalone-initiator-does-not-complete-background-child");
            OpenCodeSubagentSessionRequestedEventArgs? navigation = null;
            view.SubagentSessionRequested += (_, args) => navigation = args;
            var click = row.LinkInvoked!;
            click();
            Check(navigation is { ParentMessageId: "msg_subagent_update", ChildSessionId: "ses_notice_child" }, "update.same-child-navigation-affordance");
            view.ApplyMessages(new[] { call, notice });
            Check(ReferenceEquals(row, Constructed<OpenCodeTimelineChipView>().Single()), "update.retained-snapshot-keeps-one-chip");
            view.ApplyMessages(new[] { call, UpdateMessage(UpdateText(kind, "修订全文\n第二行仍完整")) });
            Check(row.DetailText == "修订全文\n第二行仍完整" && row.Content.Target == "· 修订全文", "update.changed-text-refreshes-existing-chip");
            view.ApplyMessages(new[] { call, UpdateMessage(UpdateText(kind, body).Replace(UpdateId, "bad-id", StringComparison.Ordinal)) });
            navigation = null; click();
            Check(row.Label == OpenCodeStepRunPresentation.InternalInitiatorLabel && row.LinkInvoked is null && navigation is null,
                "update.malformed-revision-falls-back-and-revokes-old-link");
            view.ClearMessages(); navigation = null; click();
            Check(row.DisposeCount == 1 && navigation is null, "update.disposal-revokes-stored-child-navigation");
        }

        var valid = UpdateText("progress", "第一行\n第二行");
        Check(OpenCodeStepRunPresentation.TryParseSubagentUpdateText(valid.Replace("\n", "\r\n", StringComparison.Ordinal), out var crlf)
            && crlf.Message == "第一行\r\n第二行", "update.crlf-preserves-full-message");
        Check(OpenCodeStepRunPresentation.TryParseSubagentUpdateText(valid.Replace("id=\"" + UpdateId + "\" kind=\"progress\"",
            "kind=\"progress\" id=\"" + UpdateId + "\"", StringComparison.Ordinal), out _), "update.header-attribute-order-is-not-significant");
        Check(OpenCodeStepRunPresentation.TryParseSubagentUpdateText(UpdateText("progress", new string('x', 2000)), out var maximum)
            && maximum.Message.Length == 2000 && OpenCodeStepRunPresentation.DescribeSubagentUpdate(maximum).Target.Length == 83,
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
        Console.WriteLine($"PASS subagent update initiator chips: {_assertions - before} assertions; real parser/ConversationView, inert existing chip visuals.");
    }
}
