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
        var view = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeAgentTreeView.cs"));
        Check(view.Contains("<ItemsRepeater x:Name=\"Tree\"") && view.Contains("_tree.ItemsSource = groups"), "native repeater presents expandable groups without a left chevron");
        Check(view.Contains("Text=\"子智能体\"") && view.Contains("AcrylicInAppFillColorDefaultBrush") && view.Contains("<ThemeShadow />")
            && view.Contains("Translation=\"0,0,16\"") && !view.Contains("MaxHeight=\"280\""), "D: floating 子智能体 card; tree fills the card height");
        Check(view.Contains("Style = OpenCodeInsightsTheme.TreeLabel(dimmed)") && view.Contains("agent.Children.Count == 0"), "idle configured groups use theme styling");
        Check(view.Contains("<ToggleButton x:Name=\"All\"") && view.Contains("Text=\"全部\"") && view.Contains("&#xE71C;")
            && !view.Contains("Action? RefreshRequested") && !view.Contains("x:Name=\"Refresh\""), "L1/L2: filter toggle replaces refresh");
        Check(view.Contains("private static bool _showAll;") && view.Contains("_all.IsChecked = _showAll"), "working-only default; filter retained in app memory");
        Check(view.Contains("_all.Checked +=") && view.Contains("_all.Unchecked +="), "filter responds to native check state changes including automation");
        Check(view.Contains("OpenCodeAgentIcons.DisplayName(agent.Name)") && view.Contains("OpenCodeAgentIcons.Glyph(agent.Name)"), "L4/L5/AS2/V6: shared agent names and glyphs");
        Check(view.Contains("Group(\"shell\", \"终端\", \"\\uE756\"") && view.Contains("VisibleShells(_shells, _showAll)"), "terminal group shares the filter and terminal glyph");
        Check(view.Contains("Grid.SetColumn(model, 2)") && view.Contains("Grid.SetColumn(indicator, 0)")
            && view.Contains("model.HorizontalAlignment = HorizontalAlignment.Right") && !view.Contains("text.Children.Add(model)"),
            "instance status leads; small gray model occupies the trailing column");
        var group = view[view.IndexOf("private FrameworkElement Group", StringComparison.Ordinal)..view.IndexOf("private Button InstanceButton", StringComparison.Ordinal)];
        Check(group.Contains("TextWrapping.Wrap") && group.Contains("TextTrimming.None") && group.Contains("MaxLines = 0")
            && !group.Contains("CharacterEllipsis"), "complete agent group names wrap without ellipsis or line cap");
        Check(group.Contains("Grid.SetColumn(chevron, 3)") && group.Contains("Opacity = 0") && group.Contains("header.PointerEntered")
            && group.Contains("header.PointerExited") && group.Contains("header.GotFocus"), "L6: right chevron shown on hover or keyboard focus only");
        Check(view.Contains("SessionRequested?.Invoke(id)") && view.Contains("ShellRequested?.Invoke(shell.Id)"), "real session and shell navigation retained");
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
             new OpenCodeAgentInfo { Id = "custom-worker", Name = "任意配置", Mode = "subagent" },
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
        Check(OpenCodeAgentTreeProjection.VisibleAgents(groups, false).Count == 0, "default hides completed, unknown and idle agents");
        Check(OpenCodeAgentTreeProjection.VisibleAgents(groups, true).Count == 3, "all includes configured idle and hidden agents with real instances");
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
