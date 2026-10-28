using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

internal static class Program
{
    private static int Main(string[] args)
    {
        using var context = new PumpContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var task = RunAsync(args[0], args.Length > 1 ? args[1] : null);
        task.ContinueWith(_ => context.Complete(), TaskScheduler.Default);
        context.Run();
        try { task.GetAwaiter().GetResult();return 0; }
        catch (Exception error) { Console.Error.WriteLine(error);return 1; }
    }

    internal static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }

    internal static async Task WaitAsync(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { while (!condition()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception("Timed out: " + message); }
    }

    internal static async Task NavigateAsync(IOpenCodeBrowserPageDriver page, string url)
    {
        var done = new TaskCompletionSource<OpenCodeBrowserDriverNavigation>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, OpenCodeBrowserDriverNavigation e) { if (e.Phase == OpenCodeBrowserNavigationPhase.Completed) done.TrySetResult(e); }
        page.NavigationChanged += Handler;
        try
        {
            await page.NavigateAsync(url);
            var completed = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!completed.IsSuccess) throw new Exception("Navigation failed: " + completed.LoadError);
        }
        finally { page.NavigationChanged -= Handler; }
    }

    private static async Task RunAsync(string evidence, string? focusedCheck)
    {
        var directory = Path.Combine(evidence, "profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await using var proxy = new FixtureProxy();
        var executable = OpenCodeBrowserChromeConnector.FindExecutable(null);
        if (focusedCheck == "download-routing")
        {
            await DownloadRoutingTests.RunAsync(directory, evidence, proxy);
            return;
        }
        if (focusedCheck is "download-provenance" or "download-provenance-child")
        {
            await TestDownloadProvenanceAsync(directory, evidence, proxy, focusedCheck == "download-provenance-child");
            return;
        }
        foreach (var bad in new[] { "http://example.com:9222", "ws://192.168.1.2/devtools/browser/a", "http://127.0.0.1.evil.test", "http://u:p@127.0.0.1", "file:///x" })
        {
            try { OpenCodeBrowserChromeConnector.ValidateEndpoint(bad);throw new Exception("Accepted unsafe endpoint: " + bad); }
            catch (ArgumentException) { }
        }
        Check(true, "reject nonloopback and credential-bearing debugging endpoints");
        await ConnectorTests.RunAsync();
        var factory = new OpenCodeBrowserChromeDriverFactory();
        var options = new OpenCodeBrowserPageDriverOptions("workspace-one", Path.Combine(directory, "one"), proxy.Options, []);
        await using var first = factory.Create(options);
        await using var second = factory.Create(options);
        await first.EnsureReadyAsync();await second.EnsureReadyAsync();
        Check(first.Presentation == OpenCodeBrowserPresentation.ExternalWindow && first.EmbeddedView == null, "honest external presentation");
        var arguments = string.Join("\n", ((JsonArray)(await first.CdpAsync("Browser.getBrowserCommandLine"))["arguments"]!).Select(n => n!.GetValue<string>()));
        Check(arguments.Contains("--proxy-bypass-list=<-loopback>", StringComparison.Ordinal) && arguments.Contains("--disable-quic", StringComparison.Ordinal)
            && !arguments.Contains(proxy.Options.Username, StringComparison.Ordinal) && !arguments.Contains(proxy.Options.Password, StringComparison.Ordinal), "managed command line enforces proxy/leak policies without credentials");
        var thread = Environment.CurrentManagedThreadId;
        var events = new List<OpenCodeBrowserCdpEvent>();
        await using var all = await first.SubscribeCdpAsync("*", e =>
        {
            if (Environment.CurrentManagedThreadId != thread) throw new Exception("Wrong callback thread.");events.Add(e);
            if (e.Method == "Page.frameNavigated" || e.Method == "Network.loadingFailed" || e.Method == "Fetch.authRequired")
                File.AppendAllText(Path.Combine(evidence, "events.jsonl"), new JsonObject { ["method"] = e.Method, ["session"] = e.SessionId, ["params"] = e.Parameters.DeepClone() }.ToJsonString() + Environment.NewLine);
        });
        var navigations = new List<OpenCodeBrowserDriverNavigation>();first.NavigationChanged += (_, n) => navigations.Add(n);
        await NavigateAsync(first, "http://fixture.test/redirect");
        Check(navigations.Count == 3 && navigations.Select(n => n.NavigationId).Distinct().Count() == 1
            && navigations[1].Phase == OpenCodeBrowserNavigationPhase.Committed && first.State.Url.EndsWith("/final", StringComparison.Ordinal), "redirect keeps one Starting/Committed/Completed identity");
        Check(events.Any(e => e.Method == "Page.frameNavigated" && e.SessionId == null), "owned root event session normalized to null");
        Check(await first.ExecuteScriptAsync("({answer:42})") == "{\"answer\":42}" && await first.ExecuteScriptAsync("undefined") == "null", "WebView-compatible JSON script results");
        var png = await first.CapturePreviewAsync();File.WriteAllBytes(Path.Combine(evidence, "viewport.png"), png);
        Check(png.Length > 100 && png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "real viewport PNG capture");
        var before = navigations.Count;
        await first.ExecuteScriptAsync("history.pushState({},'', '/same-document')");
        await WaitAsync(() => first.State.Url.EndsWith("/same-document", StringComparison.Ordinal), "same document state");
        Check(before == navigations.Count, "same-document updates do not invent document commits");
        await first.GoBackAsync();
        await WaitAsync(() => first.State.Url.EndsWith("/final", StringComparison.Ordinal) && first.State.CanGoForward, "same-document back history");
        await first.GoForwardAsync();
        await WaitAsync(() => first.State.Url.EndsWith("/same-document", StringComparison.Ordinal) && !first.State.CanGoForward, "same-document forward history");
        Check(before == navigations.Count && first.State.CanGoBack, "history state and same-document back/forward preserve document identity");
        var reload = new TaskCompletionSource<OpenCodeBrowserDriverNavigation>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Reloaded(object? sender, OpenCodeBrowserDriverNavigation e) { if (e.Phase == OpenCodeBrowserNavigationPhase.Completed) reload.TrySetResult(e); }
        first.NavigationChanged += Reloaded;
        await first.ReloadAsync();
        var reloaded = await reload.Task.WaitAsync(TimeSpan.FromSeconds(20));first.NavigationChanged -= Reloaded;
        Check(reloaded.IsSuccess && navigations.Count == before + 3, "reload emits one new document navigation");
        await NavigateAsync(first, "http://127.0.0.1:45678/loopback");
        Check(proxy.LoopbackRequests > 0 && proxy.HttpAuthenticated > 0, "HTTP proxy authentication and forced loopback proxy routing");
        await first.CdpAsync("Security.setIgnoreCertificateErrors", new JsonObject { ["ignore"] = true });
        await NavigateAsync(first, "https://secure.fixture.test/secure");
        Check(proxy.ConnectAuthenticated > 0, "HTTPS CONNECT authenticates through the controlled proxy");
        var originBefore = navigations.Count;
        await first.NavigateAsync("http://fixture.test/origin-auth");
        await WaitAsync(() => events.Any(e => e.Method == "Fetch.authRequired" && e.Parameters["authChallenge"]?["source"]?.GetValue<string>() == "Server"), "origin auth challenge");
        await WaitAsync(() => !first.State.Loading && first.State.Url.EndsWith("/origin-auth", StringComparison.Ordinal), "origin challenge refusal");
        Check(proxy.OriginCredentialLeaks == 0, "origin challenges never receive proxy credentials");
        Check(navigations.Skip(originBefore).Count(n => n.Phase == OpenCodeBrowserNavigationPhase.Starting) == 1
            && navigations.Skip(originBefore).Count(n => n.Phase == OpenCodeBrowserNavigationPhase.Completed) == 1, "failed navigation is not duplicated by Page.navigate response or Chrome error document");
        await NavigateAsync(first, "http://fixture.test/frames");
        await WaitAsync(() => events.Any(e => e.Method == "Target.attachedToTarget"), "child target autoattach");
        await WaitAsync(() => events.Any(e => e.SessionId != null && e.Method.StartsWith("Network.", StringComparison.Ordinal)), "child network events");
        Check(true, "child renderer events retain their session IDs");
        var child = events.First(e => e.Method == "Target.attachedToTarget" && e.Parameters["targetInfo"]?["type"]?.GetValue<string>() == "iframe").Parameters["sessionId"]!.GetValue<string>();
        var childValue = await first.CdpAsync("Runtime.evaluate", new JsonObject { ["expression"] = "location.hostname", ["returnByValue"] = true }, child);
        Check(childValue["result"]?["value"]?.GetValue<string>() == "other-fixture.test", "explicit child-session CDP routes to its owned renderer");
        await WaitAsync(() => events.Any(e => e.Method == "Target.attachedToTarget" && e.Parameters["targetInfo"]?["type"]?.GetValue<string>() == "worker"), "worker autoattach");
        await WaitAsync(() => events.Any(e => e.Method == "Network.requestWillBeSent" && e.Parameters["request"]?["url"]?.GetValue<string>() == "http://worker-fixture.test/worker-data"), "worker proxy request");
        await WaitAsync(() => events.Any(e => e.Method == "Fetch.requestPaused" && e.Parameters["request"]?["url"]?.GetValue<string>() == "http://worker-fixture.test/worker-data"), "parent Fetch interceptor covers worker network");
        Check(first.IsReady, "owned worker initializes proxy interception before resuming");
        var console = new List<string>();
        await using (var named = await first.SubscribeCdpAsync("Runtime.consoleAPICalled", e => console.Add(e.Parameters["args"]![0]!["value"]!.GetValue<string>())))
        {
            await Task.Run(() => first.CdpAsync("Runtime.evaluate", new JsonObject { ["expression"] = "console.log('first');console.log('second')" }));
            await WaitAsync(() => console.Count == 2, "ordered named callbacks");
        }
        await first.ExecuteScriptAsync("console.log('after-dispose')");
        await WaitAsync(() => events.Any(e => e.Method == "Runtime.consoleAPICalled" && e.Parameters["args"]?[0]?["value"]?.GetValue<string>() == "after-dispose"), "wildcard callback after named subscription disposal");
        Check(console.SequenceEqual(new[] { "first", "second" }), "named and wildcard event order plus asynchronous unsubscription");
        using (var cancel = new CancellationTokenSource(100))
        {
            try { await Task.Run(() => first.CdpAsync("Runtime.evaluate", new JsonObject { ["expression"] = "new Promise(r=>setTimeout(()=>r(1),1000))", ["awaitPromise"] = true }, cancellationToken: cancel.Token));throw new Exception("Request was not canceled."); }
            catch (OperationCanceledException) { }
        }
        Check(await first.ExecuteScriptAsync("6*7") == "42", "canceling one CDP request leaves the connection usable");
        var downloadDone = new TaskCompletionSource<IOpenCodeBrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = Path.Combine(evidence, "retained-download.bin");
        void Download(object? sender, OpenCodeBrowserDownloadStartingEventArgs e)
        {
            e.Handled = true;e.ResultFilePath = destination;
            e.Download.Changed += (_, _) => { if (e.Download.State != OpenCodeBrowserDownloadState.InProgress) downloadDone.TrySetResult(e.Download); };
        }
        first.DownloadStarting += Download;
        await first.NavigateAsync("http://fixture.test/download");
        var finished = await downloadDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(finished.State == OpenCodeBrowserDownloadState.Completed && File.ReadAllText(destination) == "hello bytes", "download Completed follows destination byte retention");
        Check(finished.Url == "http://fixture.test/download", "completed download exposes its original request URL");
        first.DownloadStarting -= Download;
        var cancelled = new TaskCompletionSource<IOpenCodeBrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DownloadStarting += (_, e) => { e.Cancel = true;e.Download.Changed += (_, _) => cancelled.TrySetResult(e.Download); };
        await first.NavigateAsync("http://fixture.test/large");
        var canceledDownload = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await WaitAsync(() => canceledDownload.State == OpenCodeBrowserDownloadState.Interrupted, "download cancel");
        Check(true, "download synchronous cancellation");
        var slow = first.NavigateAsync("http://fixture.test/slow");
        await WaitAsync(() => first.State.Loading, "slow navigation start");
        await first.StopAsync();
        await slow;
        Check(!first.State.Loading && first.State.LoadError != null, "stop terminates the current navigation with a failure state");
        Check(finished.Url == "http://fixture.test/download", "retained download URL is unchanged after subsequent navigation");
        await first.DisposeAsync();
        await NavigateAsync(second, "http://fixture.test/still-alive");
        Check(await second.ExecuteScriptAsync("document.title") == "\"Fixture /still-alive\"", "closing one page preserves other owned pages");
        var independent = new OpenCodeBrowserPageDriverOptions("workspace-two", Path.Combine(directory, "two"), proxy.Options, []);
        await using (var other = factory.Create(independent))
        {
            await other.EnsureReadyAsync();
            await other.CdpAsync("Security.setIgnoreCertificateErrors", new JsonObject { ["ignore"] = true });
            await NavigateAsync(other, "https://secure.fixture.test/https-first");
            Check(proxy.ConnectAuthenticated > 0, "fresh managed workspace authenticates an HTTPS-first CONNECT challenge");
            await NavigateAsync(other, "http://fixture.test/other-workspace");
            await second.ExecuteScriptAsync("localStorage.setItem('isolation','one');document.cookie='isolation=one'");
            Check(await other.ExecuteScriptAsync("localStorage.getItem('isolation')") == "null"
                && await other.ExecuteScriptAsync("document.cookie") == "\"\"", "two managed workspaces isolate profile storage");
        }
        await TestAttachAsync(executable, directory, proxy, evidence);
        await DownloadRoutingTests.RunAsync(directory, evidence, proxy);
        Console.WriteLine("PASS focused real-Chrome connector/driver checks complete");
    }

    private static async Task TestDownloadProvenanceAsync(string directory, string evidence, FixtureProxy proxy, bool childFrame)
    {
        var factory = new OpenCodeBrowserChromeDriverFactory();
        await using var page = factory.Create(new OpenCodeBrowserPageDriverOptions(
            "download-provenance", Path.Combine(directory, "p"), proxy.Options, []));
        using var trace = DownloadRoutingTests.Trace(page, Path.Combine(evidence, "download-routing.jsonl"));
        string? childSession = null;
        await using var childAttached = await page.SubscribeCdpAsync("Target.attachedToTarget", e =>
        {
            if (e.Parameters["targetInfo"]?["type"]?.GetValue<string>() == "iframe")
                childSession = e.Parameters["sessionId"]!.GetValue<string>();
        });
        await page.EnsureReadyAsync();
        await NavigateAsync(page, childFrame ? "http://fixture.test/frames" : "http://fixture.test/before-download");
        if (childFrame) await WaitAsync(() => childSession != null, "download child renderer attached");
        var origin = childFrame ? "http://other-fixture.test/download" : "http://fixture.test/download";
        var destination = Path.Combine(evidence, "provenance-download.bin");
        var complete = new TaskCompletionSource<IOpenCodeBrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.DownloadStarting += (_, e) =>
        {
            Check(e.Download.Url == origin && (!childFrame || e.Download.Url != page.State.Url), "download captures its own request URL at creation");
            e.Handled = true;
            e.ResultFilePath = destination;
            e.Download.Changed += (_, _) =>
            {
                if (e.Download.State != OpenCodeBrowserDownloadState.InProgress) complete.TrySetResult(e.Download);
            };
        };
        if (childFrame) await page.CdpAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = "location.href='" + origin + "';void 0", ["userGesture"] = true
        }, childSession);
        else await page.NavigateAsync(origin);
        var download = await complete.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Check(download.State == OpenCodeBrowserDownloadState.Completed && File.ReadAllText(destination) == "hello bytes", "download completes with destination bytes retained");
        await NavigateAsync(page, "http://fixture.test/after-download");
        Check(page.State.Url == "http://fixture.test/after-download" && download.Url == origin, "download origin remains original after page navigation");
        Console.WriteLine("PASS focused real-Chrome immutable download provenance regression");
    }

    private static async Task TestAttachAsync(string executable, string directory, FixtureProxy proxy, string evidence)
    {
        var profile = Path.Combine(directory, "user-fixture");Directory.CreateDirectory(profile);
        using var process = OpenCodeBrowserChromeConnector.Launch(executable, profile, proxy.Options);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var endpoint = await OpenCodeBrowserChromeConnector.WaitForEndpointAsync(process, profile, timeout.Token);
            await using var observer = new OpenCodeBrowserCdpWebSocket();await observer.ConnectAsync(endpoint, timeout.Token);
            var unrelated = (await observer.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }))["targetId"]!.GetValue<string>();
            var factory = new OpenCodeBrowserChromeDriverFactory(new OpenCodeBrowserChromeOptions(Attach: true, Endpoint: "http://127.0.0.1:" + endpoint.Port));
            var options = new OpenCodeBrowserPageDriverOptions("attached-one", Path.Combine(directory, "attached-one"), proxy.Options, []);
            await using (var page = factory.Create(options))
            {
                await page.EnsureReadyAsync();await NavigateAsync(page, "http://fixture.test/attached");
                await page.ExecuteScriptAsync("localStorage.setItem('attached','one')");
                var secondOptions = options with { WorkspaceId = "attached-two", ProfileDirectory = Path.Combine(directory, "attached-two") };
                var direct = new OpenCodeBrowserChromeDriverFactory(new OpenCodeBrowserChromeOptions(Attach: true, Endpoint: endpoint.AbsoluteUri));
                await using var second = direct.Create(secondOptions);await second.EnsureReadyAsync();await NavigateAsync(second, "http://fixture.test/attached-two");
                Check(await second.ExecuteScriptAsync("localStorage.getItem('attached')") == "null", "attached workspaces use isolated private storage contexts");
                var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                page.NewWindowRequested += (_, url) => requested.TrySetResult(url);
                await page.ExecuteScriptAsync("window.open('http://fixture.test/popup')");
                Check(await requested.Task.WaitAsync(TimeSpan.FromSeconds(10)) == "http://fixture.test/popup", "new window becomes app-tab request");
            }
            var targets = (await observer.SendAsync("Target.getTargets"))["targetInfos"] as JsonArray;
            Check(!process.HasExited && targets!.Any(t => t?["targetId"]?.GetValue<string>() == unrelated), "attach disposal leaves unrelated tabs and external Chrome alive");
            var contexts = (await observer.SendAsync("Target.getBrowserContexts"))["browserContextIds"] as JsonArray;
            Check(contexts!.Count == 0, "attached owned contexts are disposed");
            var session = (await observer.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = unrelated, ["flatten"] = true }))["sessionId"]!.GetValue<string>();
            var pending = observer.SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = "new Promise(()=>{})", ["awaitPromise"] = true }, session);
            await Task.Delay(100);await observer.DisposeAsync();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("Pending CDP request survived disconnect."); }
            catch (ObjectDisposedException) { }
            Check(!process.HasExited, "disconnect resolves pending CDP commands without closing external Chrome");
        }
        finally { if (!process.HasExited) { process.Kill(true);await process.WaitForExitAsync(); } }
    }

    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _work = new();
        public override void Post(SendOrPostCallback callback, object? state) => _work.Add((callback, state));
        internal void Complete() => _work.CompleteAdding();
        internal void Run() { foreach (var (callback, state) in _work.GetConsumingEnumerable()) callback(state); }
        public void Dispose() => _work.Dispose();
    }
}
