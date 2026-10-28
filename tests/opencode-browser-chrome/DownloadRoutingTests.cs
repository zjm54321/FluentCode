using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;

internal static class DownloadRoutingTests
{
    internal static async Task RunAsync(string directory, string evidence, FixtureProxy proxy)
    {
        var profile = Path.Combine(directory, "r");
        var options = new OpenCodeBrowserPageDriverOptions("download-routing", profile, proxy.Options, []);
        var factory = new OpenCodeBrowserChromeDriverFactory();
        await using var page = factory.Create(options);
        var driver = (OpenCodeBrowserChromeDriver)page;
        var sessions = new Dictionary<string, string?>();
        var accepted = new List<IOpenCodeBrowserDownload>();
        var publicDownloadGuids = new HashSet<string>(StringComparer.Ordinal);
        var unexpected = new List<string>();
        var expected = new Dictionary<string, (string Path, TaskCompletionSource<IOpenCodeBrowserDownload> Done)>();
        using var arrivedWhileUiBusy = new ManualResetEventSlim();
        var foreignBegin = new TaskCompletionSource<OpenCodeBrowserCdpEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var foreignComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? foreignGuid = null;
        using var trace = Trace(page, Path.Combine(evidence, "download-routing.jsonl"), e =>
        {
            if (e.Method == "Browser.downloadWillBegin")
            {
                var url = e.Parameters["url"]!.GetValue<string>();
                if (url.EndsWith("?case=queued", StringComparison.Ordinal)) arrivedWhileUiBusy.Set();
                if (url.EndsWith("?case=unowned", StringComparison.Ordinal))
                {
                    foreignGuid = e.Parameters["guid"]!.GetValue<string>();
                    foreignBegin.TrySetResult(e);
                }
            }
            else if (e.Method == "Browser.downloadProgress" && e.Parameters["guid"]?.GetValue<string>() == foreignGuid
                && e.Parameters["state"]?.GetValue<string>() == "completed") foreignComplete.TrySetResult();
        });
        await using var attached = await page.SubscribeCdpAsync("Target.attachedToTarget", e =>
        {
            if (e.Parameters["targetInfo"]?["type"]?.GetValue<string>() == "iframe")
                sessions[e.Parameters["sessionId"]!.GetValue<string>()] = e.SessionId;
        });
        await using var publicEvents = await page.SubscribeCdpAsync("*", e =>
        {
            if (e.Method is "Browser.downloadWillBegin" or "Browser.downloadProgress")
                publicDownloadGuids.Add(e.Parameters["guid"]!.GetValue<string>());
        });
        page.DownloadStarting += (_, e) =>
        {
            accepted.Add(e.Download);
            if (!expected.TryGetValue(e.Download.Url, out var entry))
            {
                unexpected.Add(e.Download.Url);
                e.Cancel = true;
                return;
            }
            e.Handled = true;
            e.ResultFilePath = entry.Path;
            e.Download.Changed += (_, _) =>
            {
                if (e.Download.State != OpenCodeBrowserDownloadState.InProgress) entry.Done.TrySetResult(e.Download);
            };
        };
        await page.EnsureReadyAsync();
        await Program.NavigateAsync(page, "http://fixture.test/download-frames");
        await Program.WaitAsync(() => sessions.Count == 2, "two levels of OOPIF targets attached");
        var tree = await page.CdpAsync("Page.getFrameTree");
        var sameFrame = FindFrame((JsonObject)tree["frameTree"]!, "http://fixture.test/same-frame");
        Program.Check(sameFrame != null && driver.OwnsFrame(sameFrame), "same-process iframe belongs to the owned logical frame tree");
        string? outerSession = null, innerSession = null, outerFrame = null, innerFrame = null;
        foreach (var session in sessions.Keys.ToArray())
        {
            var childTree = await page.CdpAsync("Page.getFrameTree", sessionId: session);
            var frame = childTree["frameTree"]!["frame"]!;
            if (frame["url"]!.GetValue<string>() == "http://outer-fixture.test/outer-frame")
            { outerSession = session; outerFrame = frame["id"]!.GetValue<string>(); }
            if (frame["url"]!.GetValue<string>() == "http://inner-fixture.test/inner-frame")
            { innerSession = session; innerFrame = frame["id"]!.GetValue<string>(); }
        }
        Program.Check(outerSession != null && innerSession != null && sessions[innerSession] == outerSession
            && driver.OwnsFrame(outerFrame!) && driver.OwnsFrame(innerFrame!), "nested cross-origin renderers retain owned frame ancestry across swaps");

        var same = await DownloadAsync("http://fixture.test/download?case=same", () => page.ExecuteScriptAsync(
            "document.getElementById('same').contentWindow.location.href='http://fixture.test/download?case=same';void 0"));
        var nested = await DownloadAsync("http://inner-fixture.test/download?case=nested", () => page.CdpAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = "location.href='http://inner-fixture.test/download?case=nested';void 0", ["userGesture"] = true
        }, innerSession));

        var queued = await DownloadAsync("http://fixture.test/download?case=queued", async () =>
        {
            var issue = Task.Run(() => page.CdpAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = "var f=document.createElement('iframe');f.src='http://fixture.test/download?case=queued';document.body.appendChild(f);void 0",
                ["userGesture"] = true
            }));
            // Hold the UI queue while the browser creates a frame and emits its download signal.
            Program.Check(arrivedWhileUiBusy.Wait(TimeSpan.FromSeconds(10)), "new iframe download reaches production receive loop while UI callbacks are queued");
            await issue;
        });

        await page.ExecuteScriptAsync("document.getElementById('outer').remove();void 0");
        await Program.WaitAsync(() => !driver.OwnsFrame(outerFrame!) && !driver.OwnsFrame(innerFrame!), "detached OOPIF subtree ownership removed");
        Program.Check(true, "real frame removal retires the whole nested subtree rather than retaining stale ownership");
        await Program.NavigateAsync(page, "http://fixture.test/after-download");
        Program.Check(same.Url == "http://fixture.test/download?case=same" && nested.Url == "http://inner-fixture.test/download?case=nested"
            && queued.Url == "http://fixture.test/download?case=queued", "same-process and nested child source URLs remain immutable after page navigation");

        var mainCount = accepted.Count;
        await using (var sibling = factory.Create(options))
        {
            var siblingDone = new TaskCompletionSource<IOpenCodeBrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var destination = Path.Combine(evidence, "sibling.bin");
            sibling.DownloadStarting += (_, e) =>
            {
                e.Handled = true; e.ResultFilePath = destination;
                e.Download.Changed += (_, _) =>
                {
                    if (e.Download.State != OpenCodeBrowserDownloadState.InProgress) siblingDone.TrySetResult(e.Download);
                };
            };
            await sibling.NavigateAsync("http://fixture.test/download?case=sibling-root");
            var download = await siblingDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Program.Check(download.State == OpenCodeBrowserDownloadState.Completed && download.Url == "http://fixture.test/download?case=sibling-root"
                && File.ReadAllText(destination) == "hello bytes" && accepted.Count == mainCount, "root download routes only to its owning sibling app tab within the shared profile");
            await Program.NavigateAsync(sibling, "http://fixture.test/sibling-after");
            Program.Check(download.Url == "http://fixture.test/download?case=sibling-root", "sibling root source URL remains original after navigation");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var portFile = await File.ReadAllLinesAsync(Path.Combine(profile, "DevToolsActivePort"));
        var endpoint = await OpenCodeBrowserChromeConnector.ResolveEndpointAsync("ws://127.0.0.1:" + portFile[0] + portFile[1], timeout.Token);
        await using var observer = new OpenCodeBrowserCdpWebSocket();
        await observer.ConnectAsync(endpoint, timeout.Token);
        var target = (await observer.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank", ["background"] = true }))["targetId"]!.GetValue<string>();
        try
        {
            var session = (await observer.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }))["sessionId"]!.GetValue<string>();
            await observer.SendAsync("Page.navigate", new JsonObject { ["url"] = "http://fixture.test/download?case=unowned" }, session);
            var browserEvent = await foreignBegin.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await foreignComplete.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await page.CdpAsync("Runtime.evaluate", new JsonObject { ["expression"] = "0" });
            Program.Check(!driver.OwnsFrame(browserEvent.Parameters["frameId"]!.GetValue<string>()) && accepted.Count == mainCount
                && unexpected.Count == 0 && !publicDownloadGuids.Contains(foreignGuid!)
                && File.ReadAllText(Path.Combine(profile, "ChromeDownloads", foreignGuid!)) == "hello bytes",
                "real browser-level download from an unowned user-fixture tab is observed but never accepted by the app driver");
        }
        finally { await observer.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = target }); }
        Program.Check(unexpected.Count == 0, "download routing never falls back to a shared profile or arbitrary active tab");
        Program.Check(publicDownloadGuids.SetEquals(accepted.Select(download => download.Id)), "public CDP download signals contain only this page's owned download GUIDs");
        Console.WriteLine("PASS focused real-Chrome logical-frame download ownership regressions");

        async Task<IOpenCodeBrowserDownload> DownloadAsync(string url, Func<Task> issue)
        {
            var destination = Path.Combine(evidence, "owned-" + expected.Count + ".bin");
            var done = new TaskCompletionSource<IOpenCodeBrowserDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
            expected.Add(url, (destination, done));
            await issue();
            var download = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Program.Check(download.Url == url && download.State == OpenCodeBrowserDownloadState.Completed && File.ReadAllText(destination) == "hello bytes",
                "production driver retains destination bytes and request URL: " + url);
            return download;
        }
    }

    private static string? FindFrame(JsonObject tree, string url)
    {
        if (tree["frame"]?["url"]?.GetValue<string>() == url) return tree["frame"]!["id"]!.GetValue<string>();
        if (tree["childFrames"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>())
                if (FindFrame(child, url) is string id) return id;
        return null;
    }

    // Observe the actual production receive stream without changing download behavior or event ownership.
    internal static IDisposable Trace(IOpenCodeBrowserPageDriver page, string path, Action<OpenCodeBrowserCdpEvent>? observe = null)
    {
        var driver = (OpenCodeBrowserChromeDriver)page;
        var workspace = typeof(OpenCodeBrowserChromeDriver).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(driver)!;
        var transport = (OpenCodeBrowserCdpWebSocket)workspace.GetType().GetField("_cdp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
        void Receive(OpenCodeBrowserCdpEvent e)
        {
            if (!e.Method.StartsWith("Page.frame", StringComparison.Ordinal)
                && !e.Method.StartsWith("Target.attached", StringComparison.Ordinal)
                && !e.Method.StartsWith("Target.detached", StringComparison.Ordinal)
                && !e.Method.StartsWith("Browser.download", StringComparison.Ordinal)) return;
            var frame = e.Parameters["frameId"]?.GetValue<string>();
            File.AppendAllText(path, new JsonObject
            {
                ["method"] = e.Method, ["sessionId"] = e.SessionId,
                ["ownedAtReceive"] = frame != null && driver.OwnsFrame(frame), ["params"] = e.Parameters.DeepClone()
            }.ToJsonString() + Environment.NewLine);
            observe?.Invoke(e);
        }
        transport.Received += Receive;
        return new TraceSubscription(() => transport.Received -= Receive);
    }

    private sealed class TraceSubscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
