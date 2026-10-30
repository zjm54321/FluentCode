using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.OpenCode.Browser;

internal static class ChildPeerTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var feed = new OpenCodeBrowserEventFeed(); feed.SetConnected(true);
        var client = new OpenCodeClient(feed);
        var backends = new Dictionary<string, Backend>();
        Backend Factory(string id) => backends.TryGetValue(id, out var backend) ? backend : backends[id] = new(id);
        await using var manager = new OpenCodeBrowserPeerManager(client, feed, Factory);
        manager.OpenSession("root", "/root");
        manager.SelectSession("child-open");
        manager.OpenSession("child-open", "/child", isChild: true);
        await Ready(manager, "child-open", ct);
        check(manager.GetPeer("root")?.SessionId == "root" && manager.GetPeer("child-open")?.Attachment?.SessionId == "child-open", "opened child has its own exact-session attachment, never parent's");
        check(client.Directories["child-open"] == "/child" && client.Connections["root"] != client.Connections["child-open"], "child location and connection are independent");
        check(backends.Values.All(backend => backend.PageCreations == 0), "attach alone creates no browser page");
        manager.SelectSession("root");
        manager.SyncRunningChildren(new[] { new OpenCodeBrowserChildSession("child-bg", "/background") });
        await Ready(manager, "child-bg", ct);
        check(manager.GetPeer("child-bg")?.Attachment?.SessionId == "child-bg" && !manager.OpenSessionIds.Contains("child-bg"), "running unopened child proactively attaches");
        check(backends["child-bg"].PageCreations == 0, "background attachment remains lazy");
        var request = "first-open";
        client.Commands["child-bg:" + request] = new(new JsonObject { ["type"] = "tabs.open" }, null, Array.Empty<OpenCodeBrowserFile>(), false, null);
        client.Publish("/background", client.Connections["child-bg"], "command", request);
        await Until(() => client.Results.ContainsKey("child-bg:" + request), ct);
        check(backends["child-bg"].PageCreations == 1 && backends["root"].PageCreations == 0, "first child command creates only the child's page");
        var old = manager.GetPeer("child-bg")!.Attachment!;
        manager.SyncRunningChildren(Array.Empty<OpenCodeBrowserChildSession>());
        await Until(() => old.Lifetime.IsCancellationRequested, ct);
        check(manager.GetPeer("child-bg") is null && manager.GetPeer("root") is not null, "finish detaches background child without touching root");
        check(manager.GetPeer("child-open") is null, "completed child in nonselected tab does not keep a background attachment");
        manager.SelectSession("child-open");
        await Ready(manager, "child-open", ct);
        check(manager.GetPeer("child-open") is not null, "opening a completed child restores its own attachment");
        manager.SelectSession("root");
        var children = Enumerable.Range(0, 12).Select(i => new OpenCodeBrowserChildSession("child-" + i, "/child/" + i)).ToArray();
        manager.SyncRunningChildren(children);
        check(manager.AttachedSessionIds.Count(id => id != "root") == 8 && children.Take(8).All(child => manager.GetPeer(child.SessionId) is not null), "hard cap admits eight child attachments in stable order");
        check(!backends.ContainsKey("child-8"), "over-cap children do not even construct a backend");
        var survivor = manager.GetPeer("child-1");
        manager.SyncRunningChildren(children);
        check(ReferenceEquals(survivor, manager.GetPeer("child-1")), "unchanged tree does not reconnect children");
        manager.SelectSession("child-11"); manager.OpenSession("child-11", "/child/11", isChild: true);
        check(manager.GetPeer("child-11") is not null && manager.AttachedSessionIds.Count(id => id != "root") == 8, "selected child preempts a background slot at the cap");
        manager.SelectSession("root"); manager.CloseSession("child-11");
        manager.SyncRunningChildren(children.Skip(1));
        check(manager.GetPeer("child-0") is null && manager.GetPeer("child-8") is not null, "completion frees capacity for next running child");
        manager.SyncRunningChildren(Array.Empty<OpenCodeBrowserChildSession>());
        check(manager.AttachedSessionIds.SequenceEqual(new[] { "root" }), "untracking root detaches every unopened child");

        var page = new OpenCodePage { BrowserBackendFactory = id => new Backend(id) };
        page.OpenChild("direct-child", "/own-directory");
        await Ready(page.BrowserPeerManager!, "direct-child", ct);
        check(page.BrowserPeerManager?.GetPeer("direct-child")?.Attachment?.SessionId == "direct-child", "production routed child attaches without any root-list item or session tab");
        page.TrackRoot("other-root", "tracked-child", "/tracked-child");
        await Ready(page.BrowserPeerManager!, "tracked-child", ct);
        check(page.BrowserPeerManager?.GetPeer("tracked-child") is not null, "production tree hook proactively attaches running child");
        page.FinishChild("tracked-child");
        check(page.BrowserPeerManager?.GetPeer("tracked-child") is null, "production terminal status detaches child");
        page.TrackRoot("other-root", "untracked-child", "/untracked-child");
        page.UntrackRoot();
        check(page.BrowserPeerManager?.GetPeer("untracked-child") is null, "production scope reset detaches child");
        await page.BrowserPeerManager!.DisposeAsync();

        string Read(string path) => File.ReadAllText(Path.Combine(root, "src/AIFrontend", path));
        var panel = Read("Pages/OpenCodePage.BrowserPanel.cs");
        var strip = Read("Pages/OpenCode/OpenCodeBrowserStrip.cs");
        var workspace = Read("Services/OpenCode/Browser/OpenCodeBrowserWorkspace.cs");
        var composition = Read("Pages/OpenCodePage.Browser.cs");
        check(panel.Contains("IsChildComposerSession && _pageActive && SourceReady"), "STATIC compact browser entry explicitly includes child sessions");
        check(panel.Contains("_pageActive && SourceReady && !_homeActive && _selectedSessionId is { } session ? GetBrowserWorkspace(session)") && !panel.Contains("ParentId: null"), "STATIC browser visibility has no parent/root-only gate");
        check(panel.Contains("child.ParentId != selected.SessionId") && panel.Contains("agent + \" · \"") && strip.Contains("BindGroups") && strip.Contains("PageRequested"), "STATIC parent strip groups and labels child-owned pages with explicit overlay route");
        check(Read("Pages/OpenCodePage.xaml").Contains("OpenCodeChildBrowser") && panel.Contains("menu.ShowAt(ChildBrowserButton)") && panel.Contains("!_sidebarLayout.IsWide"), "STATIC narrow child footer keeps browser pages and new-page entry reachable without composer");
        var constructor = workspace.Split("public OpenCodeBrowserWorkspace(")[1].Split("public string SessionId")[0];
        check(!constructor.Contains("CreatePage(") && !constructor.Contains("EnsureReadyAsync(") && !constructor.Contains("CreateAsync("), "STATIC workspace constructor never creates page/runtime/WebView");
        check(composition.Contains("IsBrowserPanelWorkspace(workspace)") && composition.Contains("other.HideOverlay()") && composition.Contains("_browserOverlayWorkspace?.HideOverlay()"), "STATIC child overlay remains source/parent fenced and Escape closes its owner");
        check(Read("Pages/OpenCodePage.PersistentPtyOverlay.cs").Contains("foreach (var workspace in _browserWorkspaces.Values) workspace.HideOverlay();")
            && Read("Pages/OpenCodePage.PersistentPtyOverlay.cs").Contains("CloseTerminalOverlay(restoreFocus: false)"), "STATIC browser/terminal overlay exclusion includes all child workspaces");
        check(Read("Pages/OpenCodePage.EventSync.cs").Contains(".Concat(_liveSessionStatuses.Keys)")
            && Read("Pages/OpenCodePage.AgentTree.cs").Contains("_treeStatuses[id] = OpenCodeAgentTreeProjection.Outcome(child.Outcome)"), "STATIC active-session snapshot repairs missed child completion rather than retaining stale running status");
    }

    private static async Task Ready(OpenCodeBrowserPeerManager manager, string id, CancellationToken ct)
    {
        try { await Until(() => manager.GetPeer(id)?.Availability == OpenCodeBrowserAvailability.Available, ct); }
        catch (OperationCanceledException ex) { throw new InvalidOperationException("Browser peer did not become available: " + id + " / " + manager.GetAvailability(id), ex); }
    }
    private static async Task Until(Func<bool> condition, CancellationToken ct)
    { while (!condition()) await Task.Delay(1, ct); }

    private sealed class Backend(string sessionId) : IOpenCodeBrowserSessionBackend
    {
        public string SessionId => sessionId;
        public int PageCreations { get; private set; }
        public OpenCodeBrowserState State => OpenCodeBrowserState.Empty;
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<OpenCodeBrowserOutcome> ExecuteAsync(OpenCodeBrowserCommand command, CancellationToken cancellationToken)
        {
            if (command.ActionType == "tabs.open") PageCreations++;
            return Task.FromResult<OpenCodeBrowserOutcome>(new OpenCodeBrowserOutcome.Success(null, Array.Empty<OpenCodeBrowserFile>()));
        }
    }
}
