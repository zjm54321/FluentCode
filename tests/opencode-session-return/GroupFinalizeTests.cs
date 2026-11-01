using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    // A processed group finalizes ("已处理", frozen, collapsed) as soon as any visible message follows it,
    // even while the turn is still running. Only the trailing group of a running turn stays live.
    private static void GroupFinalizesWhenFollowed()
    {
        var before = _assertions;
        var user = new OpenCodeMessage { Id = "msg_fin_user", Type = "user", Text = "go", Time = new() { Created = 1, Completed = 1 } };

        // Same assistant message: [group A] text [group B, running] while the turn is busy.
        ConversationViewTestProbe.Reset();
        var view = new OpenCodeConversationView();
        var running = Assistant("msg_fin_a", completed: null,
            Tool("fa1", "read", "completed", 10, 20), Tool("fa2", "glob", "completed", 20, 30),
            Text("fa_text", "先修复清理插件…"),
            Tool("fb1", "read", "completed", 40, 50), Tool("fb2", "bash", "running", 50, null));
        view.ApplyMessages(new[] { user, running });
        view.SetCurrentTurnActivity(true);
        var groups = HeaderGroups();
        Check(groups.Length == 2, "finalize.text-splits-turn-into-two-groups");
        var first = groups[0];
        var last = groups[1];
        Check(first.Summary is { IsActive: false } && !first.IsExpanded, "finalize.group-followed-by-text-is-finalized-and-collapsed");
        Check(last.Summary is { IsActive: true } && last.IsExpanded, "finalize.trailing-group-of-busy-turn-stays-live-and-expanded");

        // User expands the finalized group: the choice survives further streaming.
        first.ToggleForTest();
        view.ApplyMessages(new[] { user, running with { Content = new(running.Content!) { Text("fb_tail", "继续") } } });
        groups = HeaderGroups();
        Check(groups[0].IsExpanded && groups[0].Summary is { IsActive: false }, "finalize.user-expanded-finalized-group-stays-expanded");
        Check(groups[1].Summary is { IsActive: false } && !groups[1].IsExpanded, "finalize.trailing-group-finalizes-once-text-streams-after-it");
        view.ClearMessages();

        // Split across assistant messages: completed step with [tools, text] then a new running step.
        ConversationViewTestProbe.Reset();
        var split = new OpenCodeConversationView();
        var step1 = Assistant("msg_fin_s1", completed: 35,
            Tool("s1a", "read", "completed", 10, 20), Tool("s1b", "glob", "completed", 20, 30), Text("s1_text", "说明"));
        var step2 = Assistant("msg_fin_s2", completed: null,
            Tool("s2a", "read", "completed", 40, 50), Tool("s2b", "grep", "running", 50, null));
        split.ApplyMessages(new[] { user, step1, step2 });
        split.SetCurrentTurnActivity(true);
        groups = HeaderGroups();
        Check(groups.Length == 2 && groups[0].Summary is { IsActive: false } && !groups[0].IsExpanded
            && groups[1].Summary is { IsActive: true } && groups[1].IsExpanded,
            "finalize.cross-message-earlier-group-finalized-latest-live");

        // A trailing group with nothing after it in a busy turn remains live across a new empty step.
        var step3 = Assistant("msg_fin_s3", completed: null);
        split.ApplyMessages(new[] { user, step1, step2 with { Time = new() { Created = 40, Completed = 60 } }, step3 });
        groups = HeaderGroups();
        Check(groups.Last().Summary is { IsActive: true } && groups.Last().IsExpanded, "finalize.trailing-group-live-across-empty-new-step");
        split.SetCurrentTurnActivity(false);
        Check(HeaderGroups().All(g => g.Summary is { IsActive: false } && !g.IsExpanded), "finalize.turn-end-finalizes-all");
        split.ClearMessages();
        Console.WriteLine($"PASS processed group finalization: {_assertions - before} assertions.");
    }

    private static OpenCodeToolGroupView[] HeaderGroups()
        => Constructed<OpenCodeToolGroupView>().Where(g => g.HeaderVisible && g.Summary is not null).ToArray();

    private static OpenCodeMessage Assistant(string id, double? completed, params OpenCodeContentPart[] parts) => new()
    {
        Id = id, Type = "assistant", Time = new() { Created = 5, Completed = completed }, Content = new List<OpenCodeContentPart>(parts),
    };

    private static OpenCodeContentPart Text(string id, string text) => new() { Id = id, Type = "text", Text = text };

    private static OpenCodeContentPart Tool(string id, string name, string status, double start, double? end) => new()
    {
        Id = id, Type = "tool", Name = name,
        State = JsonSerializer.SerializeToElement(new
        {
            status,
            input = name == "bash" ? (object)new { command = "git status" } : new { filePath = "/r/" + id, pattern = "*" },
            time = end is { } e ? (object)new { start, end = e } : new { start },
        }),
    };
}
