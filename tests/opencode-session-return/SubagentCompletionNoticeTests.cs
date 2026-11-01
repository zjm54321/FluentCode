using System;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;

internal static partial class Program
{
    // Mirrors TUI SessionNoticeMessageV2's synthetic metadata + description, not child status text.
    private static void SubagentCompletionNotices()
    {
        var before = _assertions;
        foreach (var state in new[] { "completed", "error", "cancelled", "interrupted" })
        {
            ConversationViewTestProbe.Reset();
            var view = new OpenCodeConversationView();
            var call = SubagentCall("Scrollbar spacing mirror navigator");
            view.ApplyMessages(new[] { call });
            var group = Constructed<OpenCodeToolGroupView>().Single();
            group.OpenLeaf!();
            var tool = Constructed<OpenCodeToolPartView>().Single();
            var notice = SubagentNotice(state, "Scrollbar spacing mirror navigator");
            view.ApplyMessages(new[] { call, notice });
            var row = Constructed<OpenCodeTimelineChipView>().Single(chip => chip.CompletionNotice);
            var heading = state switch { "error" => "Designer 失败", "cancelled" => "Designer 已取消", "interrupted" => "Designer 已中断", _ => "Designer 已完成" };
            Check(row.Content.Name == heading && row.Content.Target == "Scrollbar spacing mirror navigator"
                && row.Content.Summary.Length == 0, "notice.heading-agent-state-and-message-description");
            Check(row.Cancelled == (state is "cancelled" or "interrupted"), "notice.cancelled-warning-variant");
            Check(row.Content.Status == (state == "completed" ? OpenCodeToolRowStatus.Completed : OpenCodeToolRowStatus.Error), "notice.terminal-status");
            var groups = Constructed<OpenCodeToolGroupView>().ToArray();
            Check(!ReferenceEquals(row.Element.Parent, view) && groups.Any(g => ReferenceEquals(row.Element.Parent, g.ItemsHost))
                && row.Element.Visibility == Visibility.Visible,
                "notice.folded-into-processed-group-items-even-with-original-call");
            var completedNotice = state == "completed";
            // Launch row + notice are two counted operations; a failed child also turns its launch row red.
            Check(group.OpenLeaf is null && group.HeaderVisible && group.Summary is { } joined
                && joined.SuccessCount + joined.ErrorCount == 2 && (completedNotice ? joined.ErrorCount == 0 : joined.ErrorCount > 0),
                "notice.joins-turn-chain-header-and-is-counted-in-its-tally");
            Check(tool.AsyncCompletion?.State == state && tool.UpdateCount >= 2, "notice.still-updates-existing-background-tool-cache");
            OpenCodeSubagentSessionRequestedEventArgs? navigation = null;
            view.SubagentSessionRequested += (_, args) => navigation = args;
            var click = row.LinkInvoked!;
            click();
            Check(navigation is { } emitted && emitted.ParentMessageId == notice.Id && emitted.ChildSessionId == "ses_notice_child", "notice.click-forwards-real-parent-message-and-child");
            view.ApplyMessages(new[] { call, notice });
            Check(ReferenceEquals(row, Constructed<OpenCodeTimelineChipView>().Single(chip => chip.CompletionNotice)), "notice.unchanged-snapshot-retains-single-row");
            view.ClearMessages(); navigation = null; click();
            Check(row.DisposeCount == 1 && navigation is null, "notice.disposal-revokes-old-navigation-callback");
        }

        ConversationViewTestProbe.Reset();
        var orphanView = new OpenCodeConversationView();
        var orphan = SubagentNotice("completed", "Explicit description");
        orphanView.ApplyMessages(new[] { orphan });
        var retained = Constructed<OpenCodeTimelineChipView>().Single(chip => chip.CompletionNotice);
        var lone = Constructed<OpenCodeToolGroupView>().Single();
        Check(lone.OpenLeaf is null && lone.HeaderVisible && lone.Summary is { SuccessCount: 1, ErrorCount: 0 }
            && ReferenceEquals(retained.Element.Parent, lone.ItemsHost),
            "notice.lone-notice-still-gets-a-processed-header-not-an-orphan-row");
        orphanView.ApplyMessages(new[] { SubagentCall("Old launch title"), orphan });
        Check(ReferenceEquals(retained, Constructed<OpenCodeTimelineChipView>().Single(chip => chip.CompletionNotice))
            && retained.Element.Visibility == Visibility.Visible && retained.Content.Target == "Explicit description",
            "notice.loading-original-call-never-suppresses-notice-or-overrides-description");
        var fallback = orphan with { Description = null, Text = "<subagent id=\"ses_notice_child\" state=\"completed\">Result body</subagent>" };
        orphanView.ApplyMessages(new[] { SubagentCall("Fallback task"), fallback });
        Check(retained.Content.Target == "Fallback task", "notice.missing-description-uses-associated-launch-task");
        orphanView.ApplyMessages(new[] { SubagentCall("Revised task"), fallback });
        Check(retained.Content.Target == "Revised task", "notice.associated-title-change-invalidates-retained-row");
        orphanView.ClearMessages();

        ConversationViewTestProbe.Reset();
        var unsafeView = new OpenCodeConversationView();
        unsafeView.ApplyMessages(new[] { orphan with { Metadata = JsonSerializer.SerializeToElement(new {
            source = "subagent", childID = "https://untrusted.invalid", state = "completed" }) } });
        var unsafeRow = Constructed<OpenCodeTimelineChipView>().Single(chip => chip.CompletionNotice);
        Check(unsafeRow.Content.Name == "子智能体 已完成" && unsafeRow.LinkInvoked is null,
            "notice.missing-agent-has-label-but-invalid-child-never-navigates");
        unsafeView.ClearMessages();
        Console.WriteLine($"PASS parent subagent completion notices: {_assertions - before} assertions; real ConversationView/parser/cache, inert chip visuals.");
    }

    private static System.Collections.Generic.IEnumerable<T> Constructed<T>()
        => ConversationViewTestProbe.Records.Where(record => record.Operation == "Construct").Select(record => record.Subject).OfType<T>();

    private static OpenCodeMessage SubagentCall(string description) => new()
    {
        Id = "msg_notice_call", Type = "assistant", Time = new() { Created = 1, Completed = 2 },
        Content = new() { new() { Id = "part_notice_call", Type = "tool", Name = "subagent",
            State = JsonSerializer.SerializeToElement(new { status = "completed", input = new { agent = "designer", description, background = true },
                metadata = new { sessionID = "ses_notice_child", status = "running" } }) } },
    };

    private static OpenCodeMessage SubagentNotice(string state, string? description) => new()
    {
        Id = "msg_notice_done", Type = "synthetic", Description = description, Time = new() { Created = 3 },
        Text = "<subagent id=\"ses_notice_child\" description=\"Legacy wrapper title\">Full result stays out of the transcript row</subagent>",
        Metadata = JsonSerializer.SerializeToElement(new { source = "subagent", childID = "ses_notice_child", agent = "designer", state }),
    };
}
