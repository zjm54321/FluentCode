using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); _checks++; }
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static async Task Main(string[] args)
    {
        AgentColorTests.Run();
        var view = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeAgentTreeView.cs"));
        Check(view.Contains("<TreeView x:Name=\"Tree\"") && view.Contains("_tree.RootNodes.Add(group)") && !view.Contains("ItemsRepeater"), "NV2: native TreeView owns the hierarchy");
        Check(view.Contains("Text=\"子智能体\"") && view.Contains("_card.Style = OpenCodeInsightsTheme.DockCard()") && view.Contains("<ThemeShadow />")
            && view.Contains("Translation=\"0,0,16\"") && !view.Contains("MaxHeight=\"280\""), "D: floating 子智能体 card; tree fills the card height");
        Check(view.Contains("Style = OpenCodeInsightsTheme.TreeLabel(dimmed)") && view.Contains("agent.Children.Count == 0"), "idle configured groups use theme styling");
        Check(view.Contains("<ToggleButton x:Name=\"All\"") && view.Contains("Text=\"全部\"") && view.Contains("&#xE71C;")
            && !view.Contains("Action? RefreshRequested") && !view.Contains("x:Name=\"Refresh\""), "L1/L2: filter toggle replaces refresh");
        Check(view.Contains("private static bool _showAll;") && view.Contains("_all.IsChecked = _showAll"), "working-only default; filter retained in app memory");
        Check(view.Contains("_all.Checked +=") && view.Contains("_all.Unchecked +="), "filter responds to native check state changes including automation");
        Check(view.Contains("OpenCodeAgentIcons.DisplayName(agent.Name)") && view.Contains("OpenCodeAgentIcons.Glyph(agent.Name)"), "L4/L5/AS2/V6: shared agent names and glyphs");
        Check(view.Contains("Group(\"shell\", \"终端\", \"\\uE756\"") && view.Contains("VisibleShells(_shells, _showAll)"), "terminal group shares the filter and terminal glyph");
        Check(view.Contains("x:Name=\"TerminalDivider\" Height=\"1\"") && !view.Contains("divider.Child = content")
            && view.Contains("_terminalTree.ItemContainerStyle = _tree.ItemContainerStyle")
            && view.Contains("_terminalDivider.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed"),
            "AT4: standalone divider separates native trees; terminal header shares agent row chrome, not the divider");
        Check(view.Contains("_tree.ItemInvoked += OnTreeItemInvoked") && view.Contains("_terminalTree.ItemInvoked += OnTreeItemInvoked"),
            "AT1/AT4: both native trees use the same group and leaf invoke path");
        Check(view.Contains("Grid.SetColumn(model, 3)") && view.Contains("Grid.SetColumn(indicator, 0)")
            && view.Contains("model.HorizontalAlignment = HorizontalAlignment.Right") && !view.Contains("text.Children.Add(model)"),
            "instance status leads; small gray model occupies the trailing column");
        var group = view[view.IndexOf("private TreeViewNode Group", StringComparison.Ordinal)..view.IndexOf("private static TextBlock Caption", StringComparison.Ordinal)];
        Check(group.Contains("TextWrapping.Wrap") && group.Contains("TextTrimming.None") && group.Contains("MaxLines = 0")
            && !group.Contains("CharacterEllipsis"), "complete agent group names wrap without ellipsis or line cap");
        Check(view.Contains("x:Name=\"ChevronHover\" Grid.Column=\"2\" Opacity=\"0\"")
            && view.Contains("Target=\"ChevronHover.Opacity\" Value=\"1\"") && view.Contains("x:Name=\"PointerOver\"")
            && view.Contains("ColumnDefinitions=\"Auto,*,Auto\" HorizontalAlignment=\"Left\""), "AT1/L6: chevron follows the measured name/count, shown only on hover");
        Check(view.Contains("x:Name=\"ExpandCollapseChevron\"") && view.Contains("TreeViewItemTemplateSettings.CollapsedGlyphVisibility")
            && view.Contains("TreeViewItemTemplateSettings.ExpandedGlyphVisibility") && !view.Contains("UpdateExpansion")
            && !view.Contains("header.Click") && !view.Contains("InstanceButton"), "NV2: native TreeViewItem keeps expansion automation, keyboard and glyph states");
        Check(view.Contains("_groups.Values.Contains(node)) node.IsExpanded = !node.IsExpanded")
            && view.Contains("x:Name=\"ExpandCollapseChevron\" Width=\"20\" IsHitTestVisible=\"False\""),
            "AT1: whole group row toggles once; chevron has no competing pointer handler");
        Check(view.Contains("AutomationProperties.SetName(item, name)") && view.Contains("NameTreeContent(row,"), "native node peers announce actual group and instance names");
        Check(view.Contains("SessionRequested?.Invoke(instance.Id)") && view.Contains("ShellRequested?.Invoke(shell.Id)"), "real session and shell navigation retained");
        Check(view.Contains("SelectionMode=\"None\"") && !view.Contains("SelectionIndicator")
            && view.Contains("Property=\"UseSystemFocusVisuals\" Value=\"True\"")
            && view.Contains("TreeViewItemBackgroundPointerOver"), "AT3: agent and terminal rows invoke without a selection pill; focus and hover remain native");
        Check(view.Contains("Content=\"返回主智能体\"") && view.Contains("_card.Visibility = Visibility.Collapsed;")
            && view.Contains("_parentReturn.Visibility = Visibility.Visible;"), "SA1: return button replaces the whole child-session tree card");
        var page = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCodePage.AgentTree.cs"));
        Check(page.Contains("_treeParentTarget = CreateVerifiedParentTarget(session, binding.SessionId)")
            && page.IndexOf("if (_treeParentTarget is not null)", StringComparison.Ordinal) < page.IndexOf("binding.Client.ListAgentsAsync", StringComparison.Ordinal),
            "SA1: verified GET/session parent decides child mode before loading any child agent tree");
        var returnHandler = page[page.IndexOf("private async Task ReturnFromAgentTreeAsync", StringComparison.Ordinal)..page.IndexOf("partial void OnAgentTreeActivityChanged", StringComparison.Ordinal)];
        Check(returnHandler.Contains("InstallSessionReturnContext(parent)") && returnHandler.Contains("ReturnToSessionAsync()")
            && returnHandler.Contains("IsTreeCurrent(binding)") && returnHandler.Contains("_treeParentReturnBusy")
            && !returnHandler.Contains("SwitchToSessionAsync") && !returnHandler.Contains("GetSessionInfoAsync"),
            "SA1: return delegates to existing guarded SessionReturn navigation without reimplementing it");
        var agents = new[]
        {
            new OpenCodeAgentInfo { Id = "custom-main", Name = "主配置", Mode = "primary" },
             new OpenCodeAgentInfo { Id = "custom-worker", Name = "任意配置", Mode = "subagent", Color = "#A1B2C3" },
            new OpenCodeAgentInfo { Id = "idle-worker", Name = "idle", Mode = "subagent" },
            new OpenCodeAgentInfo { Id = "custom-hidden", Name = "隐藏配置", Mode = "all", Hidden = true },
            new OpenCodeAgentInfo { Id = "compaction", Name = "Compaction", Hidden = true },
            new OpenCodeAgentInfo { Id = "title", Name = "Title", Hidden = true },
            new OpenCodeAgentInfo { Id = "summary", Name = "Summary", Hidden = true }
        };
        var sessions = new[]
        {
            new OpenCodeSessionInfo { Id = "ses_child", ParentId = "ses_parent", Agent = "custom-worker", Title = "真实标题", Model = new() { Id = "used-model" }, Outcome = "succeeded" },
            new OpenCodeSessionInfo { Id = "ses_foreign", ParentId = "ses_other", Agent = "custom-worker" },
            new OpenCodeSessionInfo { Id = "ses_unconfigured", ParentId = "ses_parent", Agent = "not-configured" },
            new OpenCodeSessionInfo { Id = "ses_hidden", ParentId = "ses_parent", Agent = "custom-hidden" },
            new OpenCodeSessionInfo { Id = "ses_main", ParentId = "ses_parent", Agent = "custom-main", Outcome = "failed" }
        };
        var live = new Dictionary<string, OpenCodeTreeStatus>();
        var groups = OpenCodeAgentTreeProjection.Agents(agents, sessions, "ses_parent", live);
        Check(groups.Count == 3 && groups.All(g => g.Id != "custom-main"), "L3: arbitrary primary excluded even with a real child instance");
        Check(groups[1].Children.Count == 0 && groups[0].Children.Single().Id == "ses_child", "group by child's actual agent, not dispatching parent");
        Check(groups[2].Hidden && groups[2].Children.Single().Model == "模型不可用", "hidden agent with real work retained; absent model honest");
        Check(groups.All(g => g.Id is not ("compaction" or "title" or "summary")), "idle hidden built-in agents excluded");
        Check(OpenCodeAgentTreeProjection.Agents(agents, Array.Empty<OpenCodeSessionInfo>(), "ses_parent", live).Count == 2,
            "hidden groups disappear only when they have no real child instances");
        Check(groups.SelectMany(g => g.Children).All(c => c.Id != "ses_foreign" && c.Id != "ses_unconfigured"), "exact parent and configured identity only");
        Check(groups[0].Children.Single().Model == "used-model" && groups[0].Children.Single().Status == OpenCodeTreeStatus.Succeeded, "real model and outcome");
        Check(groups[0].Color == "#A1B2C3" && groups[1].Color is null, "AT5: projection carries real agent color, preserving unknown as null");
        Check(view.Contains("OpenCodeAgentIconTheme.Create(agentName, color)")
            && view.Contains("OpenCodeAgentIconTheme.Create(agent.Name, agent.Color)")
            && view.Contains("instance.Status, instance, agent)") && !view.Contains("icon.Opacity = dimmed"),
            "AC1: group and instance glyphs share agent identity colors at full contrast, including idle agents");
        var iconTheme = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeAgentIconTheme.cs"));
        Check(iconTheme.Contains("icon.ActualThemeChanged +=") && iconTheme.Contains("icon.Loaded +=")
            && iconTheme.Contains("icon.ActualTheme == ElementTheme.Dark"), "AC1: icon palette follows inherited and live theme changes");
        Check(view.Contains("SystemFillColorCriticalBrush") && view.Contains("new ProgressRing"), "AT5: failure and running status indicators retain their own semantics");
        Check(OpenCodeAgentTreeProjection.VisibleAgents(groups, false).Count == 0, "default hides completed, unknown and idle agents");
        Check(OpenCodeAgentTreeProjection.VisibleAgents(groups, true).Count == 3, "all includes configured idle and hidden agents with real instances");
        var librarian = OpenCodeAgentTreeProjection.Agents(new[]
        {
            new OpenCodeAgentInfo { Id = "librarian", Name = "librarian", Mode = "subagent" }
        }, Array.Empty<OpenCodeSessionInfo>(), "ses_parent", live);
        Check(OpenCodeAgentTreeProjection.VisibleAgents(librarian, true).Single().Id == "librarian"
            && OpenCodeAgentTreeProjection.VisibleAgents(librarian, false).Count == 0,
            "AT2: configured librarian follows the same all/working-only rule as every other subagent");
        Check(groups.All(g => g.Id != "librarian"), "AT2: absent runtime agent never produces a fabricated librarian row");
        var workingCount = OpenCodeAgentTreeProjection.VisibleAgents(groups, false).Count;
        Check(OpenCodeAgentTreeProjection.EmptyHint(workingCount, false) == "暂无运行中的子智能体或终端；点击“全部”查看历史会话。",
            "completed-only session explains the empty working filter and how to reveal history");
        Check(OpenCodeAgentTreeProjection.EmptyHint(OpenCodeAgentTreeProjection.VisibleAgents(groups, true).Count, true) is null
            && OpenCodeAgentTreeProjection.EmptyHint(0, true) == "暂无子智能体或终端",
            "all filter clears working-only hint when rows exist and reports genuine emptiness");
        Check(view.Contains("_readHint ?? OpenCodeAgentTreeProjection.EmptyHint(_groups.Count, _showAll)")
            && view.Contains("_readHint = hint;") && view.Contains("_hint.Visibility = string.IsNullOrEmpty(hint)"),
            "filter changes recompute empty-state visibility while retaining read/loading errors");
        live["ses_child"] = OpenCodeTreeStatus.Running;
        groups = OpenCodeAgentTreeProjection.Agents(agents, sessions, "ses_parent", live);
        Check(groups[0].Children.Single().Status == OpenCodeTreeStatus.Running, "new runtime status beats persisted outcome");
        Check(OpenCodeAgentTreeProjection.VisibleAgents(groups, false).Single().Id == "custom-worker", "default includes currently working agents only");
        var mixed = new[] { groups[0].Children.Single(), new OpenCodeTreeInstance("done", "Done", "model", OpenCodeTreeStatus.Succeeded),
            new OpenCodeTreeInstance("failed", "Failed", "model", OpenCodeTreeStatus.Failed), new OpenCodeTreeInstance("unknown", "Unknown", "model", OpenCodeTreeStatus.Unknown) };
        Check(OpenCodeAgentTreeProjection.VisibleInstances(mixed, false).Single().Id == "ses_child"
            && OpenCodeAgentTreeProjection.VisibleInstances(mixed, true).Count == 4, "instance filter shows only working by default and all statuses under all");
        Check(OpenCodeAgentTreeProjection.CountLabel(2, 4, false) == "2" && OpenCodeAgentTreeProjection.CountLabel(1, 2, true) == "1 / 2"
            && OpenCodeAgentTreeProjection.CountLabel(0, 0, true) == "0 / 0", "counts are working or working / total, including configured idle agents");
        Check(OpenCodeAgentTreeProjection.Outcome(null) == OpenCodeTreeStatus.Unknown && OpenCodeAgentTreeProjection.Outcome("future") == OpenCodeTreeStatus.Unknown, "unknown is not failure or success");
        var job = new OpenCodeTreeShellInfo { Id = "sh_real", Cwd = "/same/path", Status = "running", Command = "a real command", Metadata = Json("{\"sessionID\":\"ses_parent\",\"background\":true}") };
        var shell = OpenCodeAgentTreeProjection.Shell(job, "ses_parent");
        Check(shell?.Kind == OpenCodeTreeShellKind.Background && shell.Status == OpenCodeTreeStatus.Running, "metadata session ownership and background identity");
        var mixedShells = new[] { shell!, shell! with { Id = "sh_done", Status = OpenCodeTreeStatus.Succeeded },
            shell! with { Id = "sh_unknown", Status = OpenCodeTreeStatus.Unknown } };
        Check(OpenCodeAgentTreeProjection.VisibleShells(mixedShells, false).Single().Id == "sh_real"
            && OpenCodeAgentTreeProjection.VisibleShells(mixedShells, true).Count == 3, "terminals use the same working/all status filter");
        Check(OpenCodeAgentTreeProjection.Shell(job, "ses_other") is null && OpenCodeAgentTreeProjection.Shell(job with { Metadata = Json("{}") }, "ses_parent") is null, "cwd never grants session ownership");
        Check(OpenCodeAgentTreeProjection.Shell(job with { Metadata = Json("{\"sessionID\":\"ses_parent\"}") }, "ses_parent")?.Kind == OpenCodeTreeShellKind.Unknown, "missing background flag not guessed");
        Check(OpenCodeAgentTreeProjection.ShellStatus("exited", null) == OpenCodeTreeStatus.Unknown, "exited without exit code not success");
        Check(OpenCodeAgentTreeProjection.ShellStatus("exited", 0) == OpenCodeTreeStatus.Succeeded && OpenCodeAgentTreeProjection.ShellStatus("exited", 5) == OpenCodeTreeStatus.Failed, "real exit code drives success and failure");
        Check(OpenCodeAgentTreeProjection.ShellStatus("killed", null) == OpenCodeTreeStatus.Interrupted && OpenCodeAgentTreeProjection.ShellStatus("timeout", null) == OpenCodeTreeStatus.Failed, "real killed and timeout statuses");
        Check(OpenCodeAgentTreeProjection.Shell(new OpenCodeMessage { Type = "synthetic", Text = "command succeeded", Metadata = Json("{\"shellID\":\"sh_fake\"}") }) is null, "no parsing completion from prose");
        var foreground = OpenCodeAgentTreeProjection.Shell(new OpenCodeMessage { Type = "shell", ShellId = "sh_fg", Command = "pwd", Status = "exited", Exit = Json("0"), Output = new() { Output = "real output" } });
        Check(foreground is { Kind: OpenCodeTreeShellKind.Foreground, Status: OpenCodeTreeStatus.Succeeded, Output: "real output" }, "native foreground shell message and retained output");
        Check(OpenCodeAgentTreeProjection.Shell(new OpenCodeMessage { Type = "shell", ShellId = "sh_bg", Metadata = Json("{\"background\":true}") })?.Kind == OpenCodeTreeShellKind.Background, "background native shell message remains background");
        var detachedTool = new OpenCodeContentPart { Type = "tool", State = Json("{\"status\":\"completed\",\"input\":{\"command\":\"real command\"},\"metadata\":{\"shellID\":\"sh_bg\",\"status\":\"running\"}}") };
        Check(OpenCodeAgentTreeProjection.Shell(detachedTool) is { Kind: OpenCodeTreeShellKind.Background, Status: OpenCodeTreeStatus.Running }, "completed dispatch tool does not complete real detached shell");
        var retained = new OpenCodeMessage { Type = "synthetic", Description = "retained command", Metadata = Json("{\"source\":\"shell\",\"shellID\":\"sh_bg\",\"state\":\"completed\"}") };
        Check(OpenCodeAgentTreeProjection.Shell(retained)?.Status == OpenCodeTreeStatus.Unknown, "retained notice without real exit is unknown");
        Check(OpenCodeAgentTreeProjection.Shell(retained with { Metadata = Json("{\"source\":\"shell\",\"shellID\":\"sh_bg\",\"exit\":3}") })?.Status == OpenCodeTreeStatus.Failed, "retained structured exit is authoritative without reading prose");

        var handler = new Handler();
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new OpenCodeClientOptions { BaseUrl = "http://localhost:12345" }, transportSafetyConfirmed: true);
        var result = await client.ListTreeShellsAsync("/directory with space", CancellationToken.None);
        Check(result.Data?.Single().Id == "sh_real" && handler.Last!.Contains("location%5Bdirectory%5D=%2Fdirectory%20with%20space", StringComparison.OrdinalIgnoreCase), "real list endpoint and escaped location query");
        var info = await client.GetTreeShellAsync("sh_real", "/work", CancellationToken.None);
        Check(info.Status == "exited" && info.Exit == 0, "get retains actual exit");
        var output = await client.ReadTreeShellOutputAsync("sh_real", "/work", 12, CancellationToken.None);
        Check(output.Output == "bytes" && output.Cursor == 17 && handler.Last!.Contains("cursor=12") && handler.Last.Contains("limit=65536"), "output endpoint byte cursor and bounded page");

        Console.WriteLine($"PASS {_checks} focused agent-tree checks");
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal string? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request.RequestUri!.AbsoluteUri;
            var path = request.RequestUri.AbsolutePath;
            var body = path.EndsWith("/output", StringComparison.Ordinal) ? "{\"data\":{\"output\":\"bytes\",\"cursor\":17,\"size\":17,\"truncated\":false}}"
                : path.EndsWith("/sh_real", StringComparison.Ordinal) ? "{\"data\":{\"id\":\"sh_real\",\"status\":\"exited\",\"exit\":0}}"
                : "{\"data\":[{\"id\":\"sh_real\",\"status\":\"running\"}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
