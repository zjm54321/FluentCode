using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.OpenCode
{
    // The real DTO lives beside OpenCodeClient, which this headless runner does not compile.
    public sealed record OpenCodeTreeShellInfo
    {
        public string? Id { get; init; }
        public string? Status { get; init; }
        public string? Command { get; init; }
        public string? Cwd { get; init; }
        public double? Exit { get; init; }
        public JsonElement? Metadata { get; init; }
    }
}

internal static partial class Program
{
    private static readonly string[][] AgentTeams =
    {
        new[] { "orchestrator", "explorer", "oracle", "designer", "fixer", "council", "councillor", "documenter", "validator" },
        new[] { "brainstorm", "ideator", "scout", "red-team" },
        new[] { "scribe", "doc-researcher", "drafter", "fact-checker", "typesetter" },
    };

    private static readonly string[] InternalPrimaries = { "compaction", "title", "summary" };

    private static void AgentIdentityTable()
    {
        var existing = new Dictionary<string, string>
        {
            ["orchestrator"] = "\uEC26", ["explorer"] = "\uE721", ["oracle"] = "\uE82F", ["council"] = "\uE716",
            ["councillor"] = "\uE77B", ["librarian"] = "\uE8F1", ["designer"] = "\uE790", ["fixer"] = "\uE90F",
            ["observer"] = "\uE7B3", ["validator"] = "\uE9D5",
        };
        foreach (var (agent, glyph) in existing) Check(OpenCodeAgentIcons.Glyph(agent) == glyph, "identity.existing-glyph-kept." + agent);
        var agents = AgentTeams.SelectMany(team => team).Concat(InternalPrimaries).Concat(new[] { "librarian", "observer" }).ToArray();
        Check(agents.Select(OpenCodeAgentIcons.Glyph).Distinct(StringComparer.Ordinal).Count() == agents.Length, "identity.every-agent-has-a-distinct-glyph");
        foreach (var agent in agents)
        {
            var glyph = OpenCodeAgentIcons.Glyph(agent);
            Check(glyph != OpenCodeAgentIcons.DefaultGlyph && glyph.Length == 1 && glyph[0] is >= '\uE700' and <= '\uF8FF', "identity.real-glyph." + agent);
            Check(OpenCodeAgentIcons.Glyph(OpenCodeAgentIcons.DisplayName(agent)) == glyph, "identity.display-name-resolves-same-glyph." + agent);
            var light = OpenCodeAgentIcons.Color(agent, null, false);
            var dark = OpenCodeAgentIcons.Color(agent, null, true);
            Check(light != dark && light.Length == 7 && dark.Length == 7, "identity.theme-pair." + agent);
            Check(OpenCodeAgentIcons.Color(agent, "#123456", true) == "#123456", "identity.backend-color-wins." + agent);
            // Non-text UI needs 3:1 on the app's lightest/darkest cards.
            Check(Contrast(light, "#FFFFFF") >= 3 && Contrast(light, "#F3F3F3") >= 3, "identity.light-contrast." + agent);
            Check(Contrast(dark, "#202020") >= 3 && Contrast(dark, "#2C2C2C") >= 3, "identity.dark-contrast." + agent);
        }
        foreach (var team in AgentTeams.Skip(1).Append(new[] { "documenter" }).Append(InternalPrimaries))
            foreach (var agent in team)
                Check(Contrast(OpenCodeAgentIcons.Color(agent, null, false), "#F3F3F3") >= 4.5
                    && Contrast(OpenCodeAgentIcons.Color(agent, null, true), "#2C2C2C") >= 4.5, "identity.new-pair-meets-text-contrast." + agent);
        foreach (var team in AgentTeams.Append(InternalPrimaries))
            foreach (var dark in new[] { false, true })
                Check(team.Select(agent => OpenCodeAgentIcons.Color(agent, null, dark)).Distinct(StringComparer.Ordinal).Count() == team.Length, "identity.team-colors-distinct");
        Check(OpenCodeAgentIcons.Glyph("Doc-Researcher") == "\uE736" && OpenCodeAgentIcons.Color("FACT-CHECKER", null, false) == "#B52A6E", "identity.case-insensitive");
        Check(OpenCodeAgentIcons.Glyph("councillor-beta") == "\uE77B", "identity.councillors-share-one-glyph");
        Check(OpenCodeAgentIcons.Glyph("custom-agent") == OpenCodeAgentIcons.DefaultGlyph, "identity.unknown-agent-keeps-default");
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string color)
        {
            static double Channel(string hex)
            {
                var value = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color[1..3]) + 0.7152 * Channel(color[3..5]) + 0.0722 * Channel(color[5..7]);
        }
        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void SidebarModelNames()
    {
        var models = new[]
        {
            new OpenCodeModelInfo { ProviderId = "gateway", Id = "gpt-6-luna", Name = "Gateway Luna" },
            new OpenCodeModelInfo { ProviderId = "openai", Id = "gpt-6-luna", Name = "GPT-6 Luna" },
            new OpenCodeModelInfo { ProviderId = "anthropic", Id = "claude-opus-5-5", Name = "Claude Opus 5.5", Enabled = false },
            new OpenCodeModelInfo { ProviderId = "custom", Id = "unnamed", Name = " " },
        };
        var agents = new[] { new OpenCodeAgentInfo { Id = "worker", Name = "Worker", Mode = OpenCodeAgentModes.Subagent } };
        var session = new OpenCodeSessionInfo { Id = "ses_child", ParentId = "ses_parent", Agent = "worker" };
        var live = new Dictionary<string, OpenCodeTreeStatus>();
        string Label(OpenCodeModelRef? model, IReadOnlyList<OpenCodeModelInfo>? catalog)
            => OpenCodeAgentTreeProjection.Agents(agents, new[] { session with { Model = model } }, "ses_parent", live,
                models: catalog).Single().Children.Single().Model;

        var luna = new OpenCodeModelRef { ProviderId = "openai", Id = "gpt-6-luna", Variant = "high" };
        Check(Label(luna, models) == "GPT-6 Luna", "sidebar.model-name-matches-provider-and-model-not-first-id");
        Check(Label(new() { ProviderId = "anthropic", Id = "claude-opus-5-5" }, models) == "Claude Opus 5.5",
            "sidebar.historical-model-keeps-catalog-display-name-even-if-disabled");
        Check(Label(new() { Id = "claude-opus-5-5" }, models) == "Claude Opus 5.5", "sidebar.model-name-can-match-without-provider");
        Check(Label(new() { ProviderId = "", Id = "claude-opus-5-5" }, models) == "Claude Opus 5.5", "sidebar.empty-provider-matches-by-model");
        Check(Label(luna with { ProviderId = "missing" }, models) == "gpt-6-luna", "sidebar.unknown-provider-never-borrows-another-name");
        Check(Label(new() { ProviderId = "custom", Id = "unlisted-model" }, models) == "unlisted-model", "sidebar.unknown-model-keeps-raw-id");
        Check(Label(new() { ProviderId = "custom", Id = "unnamed" }, models) == "unnamed", "sidebar.empty-catalog-name-keeps-raw-id");
        Check(Label(luna, null) == "gpt-6-luna" && Label(luna, Array.Empty<OpenCodeModelInfo>()) == "gpt-6-luna",
            "sidebar.unavailable-catalog-keeps-raw-id");
        Check(Label(null, models) == "模型不可用", "sidebar.missing-execution-model-stays-unavailable");
    }

    private static void SidebarPrimaryScope()
    {
        OpenCodeAgentInfo Agent(string id, string mode, bool hidden = false) => new() { Id = id, Name = id, Mode = mode, Hidden = hidden };
        // Shape of the live GET api/agent rules: deny everything, then allow this primary's team; last match wins.
        OpenCodeAgentInfo Primary(string id, params string[] team) => Agent(id, OpenCodeAgentModes.Primary) with
        {
            Permissions = JsonSerializer.SerializeToElement(new[] { new { action = "*", resource = "*", effect = "allow" },
                new { action = "subagent", resource = "*", effect = "deny" } }
                .Concat(team.Select(member => new { action = "subagent", resource = member, effect = "allow" })))
        };
        var orchestrator = Primary("orchestrator", "explorer", "council*");
        var brainstorm = Primary("brainstorm", "ideator", "scout", "red-team");
        var agents = new[]
        {
            orchestrator, brainstorm,
            Agent("explorer", OpenCodeAgentModes.Subagent), Agent("council", OpenCodeAgentModes.Subagent), Agent("councillor-alpha", OpenCodeAgentModes.Subagent),
            Agent("ideator", OpenCodeAgentModes.Subagent), Agent("scout", OpenCodeAgentModes.Subagent), Agent("red-team", OpenCodeAgentModes.Subagent),
            Agent("drafter", OpenCodeAgentModes.Subagent), Agent("hidden-helper", OpenCodeAgentModes.Subagent, hidden: true),
        };
        var noRuns = Array.Empty<OpenCodeSessionInfo>();
        var noLive = new Dictionary<string, OpenCodeTreeStatus>();
        string[] Listed(OpenCodeAgentInfo? primary, IEnumerable<OpenCodeSessionInfo> runs, IReadOnlyDictionary<string, OpenCodeTreeStatus> live)
            => OpenCodeAgentTreeProjection.Agents(agents, runs, "ses_parent", live,
                primary is null ? null : id => OpenCodeAgentMentionScope.AllowsMention(primary, id)).Select(a => a.Id).ToArray();

        Check(Listed(brainstorm, noRuns, noLive).SequenceEqual(new[] { "ideator", "scout", "red-team" }), "sidebar.lists-only-the-current-primary-team");
        Check(Listed(orchestrator, noRuns, noLive).SequenceEqual(new[] { "explorer", "council", "councillor-alpha" }), "sidebar.switching-primary-switches-team-with-wildcards");

        var everything = new[] { "explorer", "council", "councillor-alpha", "ideator", "scout", "red-team", "drafter" };
        Check(Listed(null, noRuns, noLive).SequenceEqual(everything), "sidebar.no-primary-shows-everything");
        Check(Listed(Agent("build", OpenCodeAgentModes.Primary), noRuns, noLive).SequenceEqual(everything), "sidebar.primary-without-rules-shows-everything");
        var unrelated = Agent("plan", OpenCodeAgentModes.Primary) with { Permissions = JsonSerializer.SerializeToElement(new[] { new { action = "read", resource = "*", effect = "deny" } }) };
        Check(Listed(unrelated, noRuns, noLive).SequenceEqual(everything), "sidebar.no-subagent-rule-shows-everything");

        var running = new OpenCodeSessionInfo { Id = "ses_running", ParentId = "ses_parent", Agent = "drafter" };
        var finished = new OpenCodeSessionInfo { Id = "ses_finished", ParentId = "ses_parent", Agent = "explorer", Outcome = "succeeded" };
        var hiddenRun = new OpenCodeSessionInfo { Id = "ses_hidden", ParentId = "ses_parent", Agent = "hidden-helper" };
        var otherParent = new OpenCodeSessionInfo { Id = "ses_other", ParentId = "ses_elsewhere", Agent = "explorer" };
        var live = new Dictionary<string, OpenCodeTreeStatus> { ["ses_running"] = OpenCodeTreeStatus.Running };
        Check(Listed(brainstorm, new[] { running }, live).SequenceEqual(new[] { "ideator", "scout", "red-team", "drafter" }), "sidebar.active-out-of-scope-run-stays-visible");
        Check(Listed(brainstorm, new[] { finished }, noLive).SequenceEqual(new[] { "explorer", "ideator", "scout", "red-team" }), "sidebar.historical-out-of-scope-run-stays-visible");
        Check(Listed(brainstorm, new[] { hiddenRun }, noLive).Contains("hidden-helper"), "sidebar.hidden-agent-with-a-run-still-listed");
        Check(!Listed(brainstorm, new[] { otherParent }, noLive).Contains("explorer"), "sidebar.another-session-run-does-not-retain");
        Check(!Listed(null, noRuns, noLive).Contains("hidden-helper"), "sidebar.hidden-agent-without-runs-stays-hidden");
        Check(OpenCodeAgentTreeProjection.Agents(agents, new[] { running }, "ses_parent", live, id => id == "ideator")
            .Sum(a => a.Children.Count(c => c.Status == OpenCodeTreeStatus.Running)) == 1, "sidebar.running-count-includes-retained-run");
        Check(OpenCodeAgentTreeProjection.Agents(agents, noRuns, "ses_parent", noLive).Count == everything.Length, "sidebar.default-call-is-unscoped");
    }
}
