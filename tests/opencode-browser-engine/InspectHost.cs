// Source-linked command tests only. No native controls, files, browser runtime or network are created by this host.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        public static DispatcherQueue GetForCurrentThread() => new();
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
namespace AIFrontend.Services.OpenCode.Browser
{
    public sealed record OpenCodeBrowserCdpEvent(string Method, JsonObject Parameters, string? SessionId);
    public interface IOpenCodeBrowserCdpConnection
    {
        Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default);
        Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive, CancellationToken cancellationToken = default);
    }
    public enum OpenCodeBrowserDownloadState { InProgress, Completed, Interrupted }
    public interface IOpenCodeBrowserDownload
    {
        string Url { get; } // Required provenance seam, requested from foundation owner.
        string Name { get; } string Mime { get; } long? TotalBytes { get; } long BytesReceived { get; }
        OpenCodeBrowserDownloadState State { get; } event EventHandler? Changed; void Cancel();
    }
    public sealed class OpenCodeBrowserDownloadStartingEventArgs(IOpenCodeBrowserDownload download) : EventArgs
    {
        public IOpenCodeBrowserDownload Download { get; } = download;
        public string ResultFilePath { get; set; } = "";
        public bool Cancel { get; set; } public bool Handled { get; set; }
    }
    public sealed record OpenCodeBrowserAuditReport(string Name, string Mime, byte[] Data);
    public sealed record OpenCodeBrowserAuditResult(JsonObject Value, IReadOnlyList<OpenCodeBrowserAuditReport> Reports);
    public interface IOpenCodeBrowserRuntime { Task<OpenCodeBrowserAuditResult> AuditAsync(string url, IOpenCodeBrowserCdpConnection cdp, CancellationToken cancellationToken = default); }
    public static class OpenCodeBrowserPreviewPolicy { public static bool SamePath(string? a, string? b) => a == b; }
    public sealed class OpenCodeBrowserPeer { }
    public sealed class OpenCodeBrowserWorkspace
    {
        public string SessionId => "session";
        public List<OpenCodeBrowserPage> Pages { get; } = new();
        public IOpenCodeBrowserRuntime? Runtime { get; set; }
        public int Mutations;
        public OpenCodeBrowserPeer? BoundPeer;
        public void BindPeer(OpenCodeBrowserPeer peer) => BoundPeer = peer;
        public event EventHandler? PagesChanged;
        public OpenCodeBrowserState GetState() => new(Pages.Select(p => p.ToTab()).ToArray(), Pages.FirstOrDefault()?.Id);
        public OpenCodeBrowserPage? Find(string id) => Pages.FirstOrDefault(p => p.Id == id);
        public OpenCodeBrowserPage CreatePage(string url, bool agent, string? path = null, bool startNavigation = true)
        {
            Mutations++; var page = new OpenCodeBrowserPage { Url = url, PreviewPath = path }; Pages.Add(page); PagesChanged?.Invoke(this, EventArgs.Empty); return page;
        }
        public void ActivateByAgent(OpenCodeBrowserPage page, bool switchShown = true) => Mutations++;
        public void Show(OpenCodeBrowserPage page) => Mutations++;
    }
    public sealed class OpenCodeBrowserPage : IOpenCodeBrowserCdpConnection
    {
        private sealed class Subscription(Action dispose) : IAsyncDisposable { public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; } }
        private readonly Dictionary<string, List<Action<OpenCodeBrowserCdpEvent>>> _events = new();
        public string Id { get; } = "tab_" + Guid.NewGuid().ToString("D");
        public string Url { get; set; } = "https://root.test/";
        public string? PreviewPath { get; set; }
        public OpenCodeBrowserNavigation Navigation { get; } = new();
        public long Generation => Navigation.Generation;
        public bool UserTakeover => false;
        public bool Closed { get; private set; }
        public bool IsReady => true;
        public bool CanGoBack => true;
        public bool CanGoForward => true;
        public bool Loading => false;
        public CancellationToken Lifetime => CancellationToken.None;
        public SemaphoreSlim ActionGate { get; } = new(1);
        public Func<Task>? PrepareNavigationAsync;
        public event EventHandler? Committed, Navigating, Ready, Closing;
        public event EventHandler<OpenCodeBrowserDownloadStartingEventArgs>? DownloadStarting;
        public int Mutations, Activity, ChildVersion = 1;
        public bool ChildAttached, NestedAttached;
        public string ChildUrl = "https://child.test/";
        public OpenCodeBrowserPage() { Navigation.Start(1); Navigation.Commit(1); }
        public OpenCodeBrowserTab ToTab() => new(Id, Url, "Fake", false, null, false, false, Generation);
        public Task EnsureReadyAsync() => Task.CompletedTask;
        public void MarkReady() => Ready?.Invoke(this, EventArgs.Empty);
        public void BeginAgentActivity(string value) => Activity++;
        public Task EndAgentActivityAsync() { Activity++; return Task.CompletedTask; }
        public void ReportPointer(double x, double y, bool pressed) { }
        public void GoBack() => Mutations++; public void GoForward() => Mutations++; public void Reload() => Mutations++; public void Stop() => Mutations++;
        public Task NavigateAsync(string url, CancellationToken ct, bool waitForCommit = false) { ct.ThrowIfCancellationRequested(); Mutations++; Url = url; return Task.CompletedTask; }
        public Task NavigateOperationAsync(Action action, CancellationToken ct) { action(); return Task.CompletedTask; }
        public void Close() { Mutations++; Closed = true; Closing?.Invoke(this, EventArgs.Empty); }
        public void NavigationStarted() => Navigating?.Invoke(this, EventArgs.Empty);
        public void CommitRoot() { Navigation.Start(2); Navigation.Commit(2); Committed?.Invoke(this, EventArgs.Empty); }
        public void Download(IOpenCodeBrowserDownload download) => DownloadStarting?.Invoke(this, new(download));
        public void Emit(string method, JsonObject parameters, string? session = null)
        {
            foreach (var receive in _events.GetValueOrDefault(method)?.ToArray() ?? Array.Empty<Action<OpenCodeBrowserCdpEvent>>()) receive(new(method, parameters, session));
        }
        public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive, CancellationToken ct = default)
        {
            if (!_events.TryGetValue(method, out var list)) _events[method] = list = new(); list.Add(receive);
            return Task.FromResult<IAsyncDisposable>(new Subscription(() => list.Remove(receive)));
        }
        private JsonObject Frame(string id, string? parent = null) => new() { ["id"] = id, ["parentId"] = parent, ["url"] = id == "root" ? Url : id == "child" ? ChildUrl : "https://leaf.test/", ["loaderId"] = id == "child" ? "child-" + ChildVersion : id + "-1" };
        public Task<JsonObject> CdpAsync(string method, JsonObject? p = null, string? session = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            JsonObject result = new();
            if (method == "Runtime.enable")
            {
                var frame = session is null ? "root" : session == "oop" ? "child" : "leaf";
                Emit("Runtime.executionContextCreated", new() { ["context"] = new JsonObject { ["id"] = 1, ["auxData"] = new JsonObject { ["frameId"] = frame, ["isDefault"] = true } } }, session);
            }
            else if (method == "Target.setAutoAttach" && session is null && !ChildAttached)
            {
                ChildAttached = true; Emit("Target.attachedToTarget", new() { ["sessionId"] = "oop", ["targetInfo"] = new JsonObject { ["targetId"] = "child", ["type"] = "iframe" } });
            }
            else if (method == "Target.setAutoAttach" && session == "oop" && !NestedAttached)
            {
                NestedAttached = true; Emit("Target.attachedToTarget", new() { ["sessionId"] = "nested", ["targetInfo"] = new JsonObject { ["targetId"] = "leaf", ["type"] = "iframe" } }, "oop");
            }
            else if (method == "Page.getFrameTree")
            {
                result["frameTree"] = session is null ? new JsonObject { ["frame"] = Frame("root"), ["childFrames"] = new JsonArray(new JsonObject { ["frame"] = Frame("child", "root") }) }
                    : session == "oop" ? new JsonObject { ["frame"] = Frame("child", "root"), ["childFrames"] = new JsonArray(new JsonObject { ["frame"] = Frame("leaf", "child") }) }
                    : new JsonObject { ["frame"] = Frame("leaf", "child") };
            }
            else if (method == "Accessibility.getFullAXTree") result["nodes"] = JsonNode.Parse("""[{"nodeId":"1","backendDOMNodeId":7,"role":{"value":"button"},"name":{"value":"Pick"}}]""");
            else if (method == "Page.captureScreenshot") { Mutations++; result["data"] = Convert.ToBase64String(new byte[] { 137, 80, 78, 71 }); }
            else if (method == "Runtime.evaluate") { Mutations++; result["result"] = new JsonObject { ["type"] = "number", ["value"] = 42 }; }
            else if (method.StartsWith("Input.", StringComparison.Ordinal) || method is "DOM.setFileInputFiles" or "Profiler.start" or "HeapProfiler.takeHeapSnapshot" or "Tracing.start" or "Page.handleJavaScriptDialog") Mutations++;
            return Task.FromResult(result);
        }
    }
}
