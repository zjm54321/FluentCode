using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
{
    private static void Omo307ReportsAndControls()
    {
        const string id = "fe062fc7-a0db-463f-89e8-1d19ae6ed98a";
        const string message = "第一行\r\n完整的第二行";
        foreach (var kind in new string?[] { "progress", "escalation", "other", null })
            foreach (var withoutWaking in new[] { false, true })
            {
                var input = new Dictionary<string, string> { ["message"] = "  " + message + "  " };
                if (kind is not null) input["kind"] = kind;
                var result = "Update " + id + " queued for the parent" + (withoutWaking ? " without waking it" : "")
                    + ". The host accepted the update; processing is not confirmed.";
                var row = OpenCodeToolRowPresentation.Build(Tool("task_report", "completed", input, result, new { }));
                var expectedName = kind switch { "progress" => "汇报进度", "escalation" => "上报问题", _ => "汇报" };
                Check(row.Name == expectedName && row.Kind == OpenCodeToolKind.Report && row.RawToolLabel.Length == 0, "report.name-kind-and-no-raw-badge");
                Check(row.Summary == "第一行" && row.Target.Length == 0 && !row.AccessibleName.Contains(id, StringComparison.Ordinal), "report.literal-first-line-and-no-update-uuid");
                Check(row.InputProperties.Single(p => p.Key == "消息").Value == message
                    && row.InputProperties.Any(p => p.Key == "类型"), "report.full-message-and-kind-in-existing-popup-properties");
                var delivery = withoutWaking ? "已排队（未唤醒主会话）。主会话尚未确认处理。" : "已排队。主会话尚未确认处理。";
                Check(row.OutputText == delivery && row.OutputProperties.Single().Value == delivery, "report.both-delivery-variants-localized");
                Check(row.Status == OpenCodeToolRowStatus.Unknown && !row.IsBackground && !row.CanMoveToBackground
                    && OpenCodeToolRowPresentation.BuildGroupSummary(new[] { row }).SuccessCount == 0, "report.queue-acceptance-never-counted-as-completion");
                Check(row.IsEscalation == (kind == "escalation") && row.GlyphOverride == (kind == "escalation" ? "\uE7BA" : "\uE8F2"), "report.escalation-glyph-and-semantic-treatment");
                var chrome = OpenCodeToolRowChrome.For(row.Status, row.IsEscalation);
                Check(!chrome.IsFailure && chrome.TextKey == (kind == "escalation" ? "SystemFillColorCautionBrush" : null), "report.reuses-existing-caution-not-completion-chrome");
                var detail = GenericProbe.Build(row);
                Check(detail.Children.Select(p => p.Title).SequenceEqual(new[] { "输入", "输出" })
                    && detail.Children[0].Value is FrameworkElement { Value: IReadOnlyList<OpenCodeToolProperty> properties }
                    && properties.Any(p => p.Value == message), "report.actual-generic-popup-keeps-full-message");
                Check(OpenCodeExecuteInnerCatalog.Summary(new("task_report", "completed", JsonSerializer.Serialize(input))) == "第一行", "report.execute-catalog-first-line");
            }
        Check(OpenCodeExecuteInnerCatalog.Find("task_report") is { Name: "汇报", Family: "agent", Glyph: "\uE8F2" }
            && OpenCodeToolRowPresentation.DisplayName("task_report") == "汇报", "report.catalog-and-display-name");
        var unknownResult = OpenCodeToolRowPresentation.Build(Tool("task_report", "completed", new { message, kind = "progress" }, "A new host result", new { }));
        Check(unknownResult.OutputText == "A new host result" && unknownResult.Status == OpenCodeToolRowStatus.Unknown, "report.unrecognized-delivery-retained-without-inventing-acceptance");
        var running = OpenCodeToolRowPresentation.Build(Tool("task_report", "running", new { message, kind = "progress" }, null, new { }));
        Check(running.Status == OpenCodeToolRowStatus.Running && running.Summary == "第一行", "report.running-call-retains-real-tool-state");

        const string child = "ses_native_child";
        foreach (var tool in new[] { "task_status", "task_cancel" })
        {
            var part = Tool(tool, "completed", new { sessionID = child }, "Session " + child + "\nagent: scout\nstate: running", new { });
            string? lookedUp = null;
            var row = OpenCodeToolRowPresentation.Build(part, findChild: session => { lookedUp = session; return ("scout", "已知子会话标题"); });
            Check(lookedUp == child && row.AgentName == "scout" && row.Target == "Scout · 已知子会话标题", tool + ".sessionID-resolves-known-agent-and-title");
            Check(OpenCodeToolDetailBuiltinData.Companion(row).SessionId == child, tool + ".sessionID-keeps-child-session-linkage");
            Check(OpenCodeToolRowPresentation.Build(part).Target == "Scout", tool + ".native-reported-agent-fallback");
            var legacy = OpenCodeToolRowPresentation.Build(Tool(tool, "completed", new { task_id = child }, "state: running", new { }));
            Check(OpenCodeToolDetailBuiltinData.Companion(legacy).SessionId == child && legacy.Target == child[..12], tool + ".legacy-task_id-still-supported");
            var both = OpenCodeToolRowPresentation.Build(Tool(tool, "completed", new { sessionID = child, task_id = "ses_old_child" }, "state: running", new { }));
            Check(OpenCodeToolDetailBuiltinData.Companion(both).SessionId == child, tool + ".v2-id-precedes-v1-id");
            foreach (var output in new[] { "state: completed (unconfirmed)", "state: unknown", "state: unexpected", "Session " + child,
                "state: completed\nstatus_uncertain: true", "state: completed\nAn additional (unconfirmed) notice" })
            {
                var uncertain = OpenCodeToolRowPresentation.Build(Tool(tool, "completed", new { sessionID = child }, output, new { }));
                Check(OpenCodeToolDetailBuiltinData.Companion(uncertain).Uncertain && uncertain.Status == OpenCodeToolRowStatus.Unknown
                    && OpenCodeToolRowPresentation.BuildGroupSummary(new[] { uncertain }).SuccessCount == 0, tool + ".unknown-or-unconfirmed-never-terminal-success");
            }
        }
        Check(OpenCodeExecuteInnerCatalog.Find("task_status")!.Keys.Contains("sessionID", StringComparison.Ordinal)
            && OpenCodeExecuteInnerCatalog.Find("task_cancel")!.Keys.Contains("sessionID", StringComparison.Ordinal), "companion.catalog-reads-v2-sessionID");
    }

    private static void AgentMentionScopesAndValidator()
    {
        Check(OpenCodeAgentIcons.Glyph("validator") == "\uE9D5" && OpenCodeAgentIcons.DisplayName("VALIDATOR") == "Validator", "validator.checklist-glyph-and-built-in-display-name");
        Check(OpenCodeAgentIcons.Color("validator", null, false) == "#006B5F" && OpenCodeAgentIcons.Color("VALIDATOR", null, true) == "#5ED8C5", "validator.theme-pair");
        Check(OpenCodeAgentIcons.Color("validator", "#123456", true) == "#123456", "validator.backend-color-still-wins");
        OpenCodeAgentInfo Primary(string id, string rules) => new() { Id = id, Mode = OpenCodeAgentModes.Primary, Permissions = JsonSerializer.Deserialize<JsonElement>(rules) };
        var brainstorm = Primary("brainstorm", """
            [{"action":"*","resource":"*","effect":"allow"},
             {"action":"subagent","resource":"*","effect":"deny"},
             {"action":"subagent","resource":"ideator","effect":"allow"},
             {"action":"subagent","resource":"sc*","effect":"ask"},
             {"action":"subagent","resource":"scout","effect":"deny"},
             {"action":"read","resource":"ideator","effect":"deny"}]
            """);
        var scribe = Primary("scribe", """
            [{"action":"*","resource":"*","effect":"deny"},
             {"action":"sub*","resource":"d*a*r","effect":"allow"},
             {"action":"subagent","resource":"fact-checker","effect":"ask"},
             {"action":"subagent","resource":"typesetter","effect":"allow"}]
            """);
        Check(OpenCodeAgentMentionScope.AllowsMention(brainstorm, "ideator") && !OpenCodeAgentMentionScope.AllowsMention(scribe, "ideator"), "mentions.current-primary-allowlists-are-distinct");
        Check(!OpenCodeAgentMentionScope.AllowsMention(brainstorm, "scout") && OpenCodeAgentMentionScope.AllowsMention(brainstorm, "scout-extra"), "mentions.last-matching-rule-wins-and-ask-stays-visible");
        Check(OpenCodeAgentMentionScope.AllowsMention(scribe, "drafter") && OpenCodeAgentMentionScope.AllowsMention(scribe, "fact-checker")
            && OpenCodeAgentMentionScope.AllowsMention(scribe, "typesetter"), "mentions.action-and-resource-wildcards");
        Check(!OpenCodeAgentMentionScope.AllowsMention(scribe, "red-team") && !OpenCodeAgentMentionScope.AllowsMention(brainstorm, "Ideator"), "mentions.deny-hides-and-agent-ids-match-ordinally");
        Check(OpenCodeAgentMentionScope.AllowsMention(null, "scout") && OpenCodeAgentMentionScope.AllowsMention(Primary("old", "[]"), "scout"), "mentions.no-primary-or-no-matching-rule-preserves-existing-candidates");
    }
}
