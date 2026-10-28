using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static class Program
{
    private static void Main() => TestUi.Run(async () =>
    {
        ProxyPolicy();
        await Facade();
        await Workspace();
        await RuntimeFailure();
        await ClosingDuringStartup();
        Console.WriteLine("PASS 5/5 browser foundation contract groups");
    });
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void ProxyPolicy()
    {
        var proxy = new OpenCodeBrowserProxyOptions(new("http://127.0.0.1:12345"), "local-user", "local-secret");
        var args = OpenCodeBrowserDriverPolicy.BrowserArguments(proxy);
        Check(args.Contains("--disable-quic") && args.Contains("<-loopback>") && args.Contains("disable_non_proxied_udp"), "Proxy leak policy");
        Check(!args.Contains(proxy.Username) && !args.Contains(proxy.Password) && !proxy.ToString().Contains(proxy.Password), "Credential disclosure");
        const string challenge = "Basic realm=\"OpenCode Browser Proxy\"";
        Check(OpenCodeBrowserDriverPolicy.IsProxyChallenge(proxy, proxy.Address.ToString(), challenge), "Expected private proxy auth rejected");
        foreach (var uri in new[] { "http://127.0.0.1:12346", "https://127.0.0.1:12345", "http://example.test:12345", "http://127.0.0.1:12345/site" })
            Check(!OpenCodeBrowserDriverPolicy.IsProxyChallenge(proxy, uri, challenge), "Unrelated auth accepted");
        Check(!OpenCodeBrowserDriverPolicy.IsProxyChallenge(proxy, proxy.Address.ToString(), "Basic realm=\"Other\""), "Wrong realm accepted");
        try { OpenCodeBrowserDriverPolicy.BrowserArguments(proxy with { BypassList = "localhost" }); throw new Exception("Direct localhost bypass accepted"); }
        catch (OpenCodeBrowserException) { }
        Console.WriteLine("PASS private proxy arguments and exact credential challenge");
    }
    private static async Task Facade()
    {
        var driver = new Driver();
        await using var page = new OpenCodeBrowserPage(_ => { }, _ => { }, createDriver: _ => Task.FromResult<IOpenCodeBrowserPageDriver>(driver));
        await page.EnsureReadyAsync();
        page.UserTakeover = true; page.BeginAgentActivity("working");
        var navigation = page.NavigateAsync("https://example.test", page.Lifetime);
        Check(page.Loading && page.Generation == 0 && !navigation.IsCompleted, "Navigation issue/commit conflated");
        driver.Commit();
        Check(page.Generation == 1 && !navigation.IsCompleted, "Commit did not advance generation exactly once");
        driver.Commit();
        Check(page.Generation == 1, "Duplicate commit advanced generation");
        driver.Complete(0); Check(!navigation.IsCompleted, "Stale completion released navigation wait");
        driver.Complete(); await navigation;
        Check(!page.Loading && page.UserTakeover && page.AgentActivity == "working", "Facade state lost across navigation");
        var result = await Task.Run(() => page.CdpAsync("Runtime.evaluate", new(), "renderer-7"));
        Check(result["session"]!.GetValue<string>() == "renderer-7" && driver.LastThread == TestUi.CurrentUi.ThreadId, "CDP session/thread lost");
        using (var cancelled = new ManualResetEventSlim())
        {
            var waiting = Task.Run(async () =>
            {
                using var stop = new CancellationTokenSource();
                var queued = page.CdpAsync("Runtime.evaluate", cancellationToken: stop.Token); stop.Cancel();
                try { await queued; throw new Exception("Queued CDP ignored cancellation"); }
                catch (OperationCanceledException) { cancelled.Set(); }
            });
            Check(cancelled.Wait(TimeSpan.FromSeconds(2)), "Cancellation waited for the blocked UI queue");
            await waiting;
        }
        OpenCodeBrowserCdpEvent? received = null;
        var subscription = await Task.Run(() => page.SubscribeCdpAsync("*", value => received = value));
        driver.Emit(new("Runtime.executionContextCreated", new(), "renderer-7"));
        Check(received?.SessionId == "renderer-7", "Renderer event identity lost");
        await Task.Run(() => subscription.DisposeAsync().AsTask());
        Check(driver.Observer is null, "Subscription not removed on owning queue");
        page.SetInputEnabled(true); Check(driver.FocusCount == 0, "Implicit focus activation");
        await page.FocusAsync(); Check(driver.FocusCount == 1, "Explicit focus missing");
        var cancellationBeforeClosing = false;
        page.Closing += (_, _) => cancellationBeforeClosing = page.Lifetime.IsCancellationRequested;
        await page.DisposeAsync(); Check(cancellationBeforeClosing && driver.Disposed, "Close cancellation/disposal ordering");
        Console.WriteLine("PASS page generation/waits, takeover/activity, marshalled CDP session/event/focus and close");
    }
    private static async Task Workspace()
    {
        var log = new List<string>(); var runtime = new Runtime(log); var runtimeFactory = new RuntimeFactory(runtime);
        var factory = new DriverFactory(log);
        await using var workspace = new OpenCodeBrowserWorkspace("session", new Grid(), new Grid(), new(
            WorkspaceId: "source/session", RuntimeFactory: runtimeFactory, DriverFactory: factory));
        var first = workspace.CreatePage("about:blank", startNavigation: false);
        var ready = first.EnsureReadyAsync();
        Check(runtimeFactory.Creates == 1 && factory.Creates == 0, "Native page created before runtime ready");
        runtimeFactory.Ready.SetResult(); await ready;
        var second = workspace.CreatePage("about:blank", startNavigation: false); await second.EnsureReadyAsync();
        Check(runtimeFactory.Creates == 1 && factory.Creates == 2, "Runtime was not workspace-owned");
        Check(factory.Options.All(option => ReferenceEquals(option.Proxy, runtime.Proxy)) && factory.Options.Select(option => option.ProfileDirectory).Distinct().Count() == 1,
            "Workspace page profile/proxy not stable");
        Check(runtimeFactory.Options!.RuntimeDirectory.EndsWith("BrowserRuntime"), "Deployed runtime bundle default missing");
        var peer = new OpenCodeBrowserPeer(); workspace.BindPeer(peer);
        using var lifetime = new CancellationTokenSource();
        var lease = new OpenCodeBrowserAttachmentLease("session", "connection", new TunnelRpc(), lifetime.Token);
        peer.Set(lease); Check(ReferenceEquals(runtime.Attachment, lease), "Lease not wired to stable runtime");
        lifetime.Cancel(); peer.Set(null); Check(runtime.Attachment is null && ReferenceEquals(workspace.Runtime, runtime), "Detach recreated/disposed runtime");
        await workspace.DisposeAsync();
        Check(log.Count(item => item == "driver-dispose") == 2 && log.Last() == "runtime-dispose", "Runtime disposed before pages");
        Console.WriteLine("PASS runtime-before-driver, stable proxy/profile/attachment and ordered workspace disposal");
    }
    private static async Task RuntimeFailure()
    {
        var factory = new DriverFactory(new()); var runtimeFactory = new RuntimeFactory(new(new()));
        await using var workspace = new OpenCodeBrowserWorkspace("session", new Grid(), new Grid(), new(RuntimeFactory: runtimeFactory, DriverFactory: factory));
        var page = workspace.CreatePage("about:blank", startNavigation: false);
        var ready = page.EnsureReadyAsync(); runtimeFactory.Ready.SetException(new InvalidOperationException("missing bundle"));
        try { await ready; throw new Exception("Missing runtime was ignored"); } catch (InvalidOperationException ex) when (ex.Message == "missing bundle") { }
        Check(factory.Creates == 0 && workspace.Runtime is null && page.LoadError == "missing bundle", "Missing runtime selected fallback or hid error");
        Console.WriteLine("PASS runtime initialization failure leaves no native engine/direct fallback");
    }
    private static async Task ClosingDuringStartup()
    {
        var runtimeFactory = new RuntimeFactory(new(new())); var factory = new DriverFactory(new());
        await using var workspace = new OpenCodeBrowserWorkspace("session", new Grid(), new Grid(), new(RuntimeFactory: runtimeFactory, DriverFactory: factory));
        var page = workspace.CreatePage("about:blank", startNavigation: false);
        var pending = page.EnsureReadyAsync();
        await page.DisposeAsync();
        try { await pending; throw new Exception("Closed page startup completed"); } catch (OperationCanceledException) { }
        Check(factory.Creates == 0 && page.Lifetime.IsCancellationRequested, "Closed page created a native engine or lost lifetime");
        runtimeFactory.Ready.SetResult();
        var second = workspace.CreatePage("about:blank", startNavigation: false); await second.EnsureReadyAsync();
        Check(runtimeFactory.Creates == 1 && factory.Creates == 1, "Closing a page cancelled/replaced the workspace runtime");
        var blockedRuntime = new RuntimeFactory(new(new())); var blockedFactory = new DriverFactory(new());
        await using var closing = new OpenCodeBrowserWorkspace("session", new Grid(), new Grid(), new(RuntimeFactory: blockedRuntime, DriverFactory: blockedFactory));
        var blocked = closing.CreatePage("about:blank", startNavigation: false).EnsureReadyAsync();
        await closing.DisposeAsync();
        try { await blocked; throw new Exception("Disposed workspace startup completed"); } catch (OperationCanceledException) { }
        Check(blockedFactory.Creates == 0, "Disposed workspace created a native engine");
        Console.WriteLine("PASS closing during initialization cancels page/workspace waits without late native creation");
    }
    private sealed class Driver : IOpenCodeBrowserPageDriver
    {
        private readonly OpenCodeBrowserUiContext _ui = new();
        private readonly List<string>? _log;
        private ulong _id;
        public Driver(List<string>? log = null) { _log = log; }
        public OpenCodeBrowserPresentation Presentation => OpenCodeBrowserPresentation.Embedded;
        public FrameworkElement EmbeddedView { get; } = new();
        public bool IsReady { get; private set; }
        public bool Disposed; public int LastThread, FocusCount;
        public Action<OpenCodeBrowserCdpEvent>? Observer;
        public OpenCodeBrowserDriverState State { get; private set; } = new("about:blank", "test", false, null, false, false);
        public event EventHandler? StateChanged;
        public event EventHandler<OpenCodeBrowserDriverNavigation>? NavigationChanged;
        public event EventHandler<string>? NewWindowRequested { add { } remove { } }
        public event EventHandler<OpenCodeBrowserDownloadStartingEventArgs>? DownloadStarting { add { } remove { } }
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) { IsReady = true; return Task.CompletedTask; }
        public Task NavigateAsync(string url, CancellationToken cancellationToken = default)
        { _id++; State = State with { Url = url, Loading = true }; StateChanged?.Invoke(this, EventArgs.Empty); NavigationChanged?.Invoke(this, new(_id, OpenCodeBrowserNavigationPhase.Starting, url)); return Task.CompletedTask; }
        public void Commit() => NavigationChanged?.Invoke(this, new(_id, OpenCodeBrowserNavigationPhase.Committed, State.Url));
        public void Complete(ulong? id = null)
        { if (id is null) State = State with { Loading = false }; NavigationChanged?.Invoke(this, new(id ?? _id, OpenCodeBrowserNavigationPhase.Completed, State.Url)); }
        public Task GoBackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task GoForwardAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default) => Task.FromResult("null");
        public Task<byte[]> CapturePreviewAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SetInputEnabled(bool enabled) { }
        public Task FocusAsync(CancellationToken cancellationToken = default) { FocusCount++; return Task.CompletedTask; }
        public Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default)
        { LastThread = Environment.CurrentManagedThreadId; return Task.FromResult(new JsonObject { ["session"] = sessionId }); }
        public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive, CancellationToken cancellationToken = default)
        { Observer = receive; return Task.FromResult<IAsyncDisposable>(new Subscription(this)); }
        public void Emit(OpenCodeBrowserCdpEvent value) => Observer?.Invoke(value);
        private sealed class Subscription(Driver owner) : IAsyncDisposable
        { public ValueTask DisposeAsync() => new(owner._ui.InvokeAsync(() => { owner.Observer = null; return Task.CompletedTask; })); }
        public ValueTask DisposeAsync() { Disposed = true; _log?.Add("driver-dispose"); return ValueTask.CompletedTask; }
    }
    private sealed class DriverFactory(List<string> log) : IOpenCodeBrowserPageDriverFactory
    {
        public int Creates;
        public List<OpenCodeBrowserPageDriverOptions> Options { get; } = new();
        public IOpenCodeBrowserPageDriver Create(OpenCodeBrowserPageDriverOptions options)
        { Creates++; Options.Add(options); return new Driver(log); }
    }
    private sealed class RuntimeFactory(Runtime runtime) : IOpenCodeBrowserRuntimeFactory
    {
        public int Creates; public OpenCodeBrowserRuntimeOptions? Options;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IOpenCodeBrowserRuntime> CreateAsync(OpenCodeBrowserRuntimeOptions options, CancellationToken cancellationToken = default)
        { Creates++; Options = options; await Ready.Task.WaitAsync(cancellationToken); return runtime; }
    }
    private sealed class Runtime(List<string> log) : IOpenCodeBrowserRuntime
    {
        public OpenCodeBrowserProxyOptions Proxy { get; } = new(new("http://127.0.0.1:12345"), "local-user", "local-secret");
        public IReadOnlyList<string> CdpEventNames => new[] { "Runtime.executionContextCreated" };
        public OpenCodeBrowserAttachmentLease? Attachment;
        public void SetAttachment(OpenCodeBrowserAttachmentLease? attachment) { Attachment = attachment; }
        public Task<OpenCodeBrowserAuditResult> AuditAsync(string url, IOpenCodeBrowserCdpConnection cdp, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { log.Add("runtime-dispose"); return ValueTask.CompletedTask; }
    }
    private sealed class TunnelRpc : IOpenCodeBrowserTunnelRpc
    {
        public Task<string> OpenAsync(OpenCodeBrowserTunnelTarget target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<OpenCodeBrowserTunnelRead> ReadAsync(string tunnelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WriteAsync(string tunnelId, byte[] data, bool? end = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAsync(string tunnelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
