using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Interlocked.Increment(ref checks); }

    public static async Task Main(string[] args)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var tunnels = new OpenCodeBrowserTunnelTransport(lifetime.Token);
        var rpc = new ControlledRpc();
        var attachment = new OpenCodeBrowserAttachmentLease("server-session-secret", "server-connection-secret", rpc, lifetime.Token);
        var lease = tunnels.Register(attachment);
        await using var protocol = OpenCodeBrowserRuntimeProtocol.Start(Path.Combine(args[0], "artifacts", "browser-runtime"), tunnels.HandleAsync);
        var ready = await protocol.Ready.WaitAsync(lifetime.Token);
        Check(ready["protocol"]!.GetValue<int>() == 1, "Protocol version");
        var proxy = ready["proxy"]!.AsObject();
        await protocol.CallAsync("attachment.set", new JsonObject { ["leaseId"] = lease.Id }, lifetime.Token);
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(new Uri(proxy["address"]!.GetValue<string>()))
            { Credentials = new NetworkCredential(proxy["username"]!.GetValue<string>(), proxy["password"]!.GetValue<string>()) },
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        Check(await http.GetStringAsync("http://controlled.invalid/", lifetime.Token) == "hello", "C# callbacks did not supply HTTP tunnel bytes");
        rpc.OpenError = "connect ECONNREFUSED controlled.invalid:80 secret-server-credential";
        var errorPage = await http.GetAsync("http://controlled.invalid/failure", lifetime.Token);
        var errorText = await errorPage.Content.ReadAsStringAsync(lifetime.Token);
        Check((int)errorPage.StatusCode == 502 && errorText.Contains("ECONNREFUSED", StringComparison.Ordinal)
            && errorText.Contains("controlled.invalid:80", StringComparison.Ordinal), "tunnel.open failure page includes server-side diagnostic");
        Check(!errorText.Contains("secret-server-credential", StringComparison.Ordinal), "raw RPC details never cross into browser content");
        Check(OpenCodeBrowserTunnelTransport.Error(lease, "https://controlled.invalid:80/probe")?.Contains("ECONNREFUSED", StringComparison.Ordinal) == true, "CONNECT failures expose server diagnostics to the native error surface");
        Check(OpenCodeBrowserTunnelTransport.Error(lease, "https://other.invalid:80/") is null, "diagnostic is target-specific");
        rpc.OpenError = null;
        Check(await http.GetStringAsync("http://controlled.invalid/recovered", lifetime.Token) == "hello"
            && OpenCodeBrowserTunnelTransport.Error(lease, "http://controlled.invalid/") is null, "successful reconnect clears stale server error");
        tunnels.Retire(lease);
        await protocol.CallAsync("attachment.set", new JsonObject { ["leaseId"] = null }, lifetime.Token);
        Check((int)(await http.GetAsync("http://controlled.invalid/detached", lifetime.Token)).StatusCode == 503, "Detached proxy did not fail closed");

        var delayed = new ControlledRpc { DelayOpen = true };
        var old = tunnels.Register(new("original-session", "original-connection", delayed, lifetime.Token));
        var open = tunnels.HandleAsync("tunnel.open", new JsonObject
        { ["leaseId"] = old.Id, ["target"] = new JsonObject { ["host"] = "controlled.invalid", ["port"] = 80 } }, lifetime.Token);
        await delayed.OpenStarted.Task.WaitAsync(lifetime.Token);
        tunnels.Retire(old);
        delayed.OpenRelease.TrySetResult();
        try { await open; throw new InvalidOperationException("Late open was accepted after detach"); }
        catch (OperationCanceledException) { }
        Check(delayed.Closed > 0, "Late open was not closed on its original RPC");
        tunnels.RetireAll();
        Console.WriteLine("PASS C# JSON-lines sidecar callbacks, authenticated proxy, detach, and late-open original-lease cleanup");
        await CheckWorkspaceRuntimeAsync(args[0], lifetime.Token);
        Console.WriteLine($"PASS {checks} C# browser-runtime assertions");
    }

    private static HttpClient ProxyClient(OpenCodeBrowserProxyOptions proxy) => new(new HttpClientHandler
    {
        Proxy = new WebProxy(proxy.Address) { Credentials = new NetworkCredential(proxy.Username, proxy.Password) },
        UseProxy = true,
    }) { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task WaitForAttachmentAsync(HttpClient http, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("http://controlled.invalid/", ct);
                if (response.IsSuccessStatusCode)
                {
                    Check(await response.Content.ReadAsStringAsync(ct) == "hello", "Factory proxy returned incorrect tunnel bytes");
                    return;
                }
                Check((int)response.StatusCode == 503, "Unexpected proxy attachment failure");
            }
            catch (HttpRequestException) { /* Publication deliberately destroys sockets accepted on the previous lease. */ }
            await Task.Delay(20, ct);
        }
        throw new InvalidOperationException("Attachment publication did not reach the stable listener.");
    }

    private static async Task CheckWorkspaceRuntimeAsync(string root, CancellationToken ct)
    {
        await using var runtime = await new OpenCodeBrowserRuntimeFactory().CreateAsync(
            new("opaque-workspace", Path.Combine(root, "artifacts", "browser-runtime")), ct);
        Check(runtime.CdpEventNames.Count > 100 && runtime.CdpEventNames.Contains("Target.attachedToTarget"), "Complete CDP catalogue");
        Check(!runtime.Proxy.ToString().Contains(runtime.Proxy.Password, StringComparison.Ordinal), "Proxy credentials leaked through ToString");
        using var attached = new CancellationTokenSource();
        var first = new ControlledRpc();
        runtime.SetAttachment(new("private-session-A", "private-connection-A", first, attached.Token));
        using var http = ProxyClient(runtime.Proxy);
        await WaitForAttachmentAsync(http, ct);
        var address = runtime.Proxy.Address;
        attached.Cancel();
        var detached = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("http://controlled.invalid/detached", ct);
                if ((int)response.StatusCode == 503) { detached = true; break; }
            }
            catch (HttpRequestException) { /* Old sockets are aborted before detach publication is acknowledged. */ }
            await Task.Delay(20, ct);
        }
        Check(detached, "Cancelled attachment did not leave the listener fail-closed");
        var second = new ControlledRpc();
        runtime.SetAttachment(new("private-session-B", "private-connection-B", second, ct));
        await WaitForAttachmentAsync(http, ct);
        Check(runtime.Proxy.Address == address, "Attachment replacement changed the proxy listener");
        Console.WriteLine("PASS public factory, complete catalogue, nonblocking lease publication and stable authenticated proxy");
        if (!ChromeCdpHost.Available) { Console.WriteLine("SKIP C# real snapshot: controlled Chrome is unavailable"); return; }
        await using var chrome = await ChromeCdpHost.StartAsync(root, ct);
        var result = await runtime.AuditAsync(chrome.Url, chrome, ct);
        Check(result.Reports.Count == 2, "Expected raw JSON and HTML report bytes");
        foreach (var report in result.Reports) Check(report.Data.Length is > 0 and <= 5 * 1024 * 1024, "Report byte limit");
        var jsonReport = JsonNode.Parse(result.Reports.First(report => report.Mime == "application/json").Data)!.AsObject();
        Check(jsonReport["lighthouseVersion"]!.GetValue<string>() == "13.4.1", "Real Lighthouse version");
        Check(jsonReport["gatherMode"]!.GetValue<string>() == "snapshot", "Real snapshot mode");
        Check(result.Value["scores"]!.AsArray().Count == 3 && result.Value["failures"]!.AsArray().Count > 0, "Real scores and failures");
        Check(chrome.SubscriptionCount == 0 && chrome.OwnedCount == 0, "Snapshot sessions/subscription leaked");

        // Cancel after the worker has already started detaching the root session. Cleanup
        // must join that mutation, not treat an in-progress detach as an acknowledged one.
        chrome.HoldNextDetachment();
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var auditing = runtime.AuditAsync(chrome.Url, chrome, cancel.Token);
            await chrome.DetachmentStarted.Task.WaitAsync(ct);
            cancel.Cancel();
            var completedBeforeDetach = false;
            try
            {
                try { await auditing.WaitAsync(TimeSpan.FromMilliseconds(250)); completedBeforeDetach = true; }
                catch (OperationCanceledException) { completedBeforeDetach = true; }
                catch (TimeoutException) { }
            }
            finally { chrome.DetachmentRelease.TrySetResult(); }
            try { await auditing; throw new InvalidOperationException("Audit cancellation was ignored"); }
            catch (OperationCanceledException) { }
            Check(!completedBeforeDetach, "Audit cancellation returned before its in-flight CDP detach completed");
        }
        Check(chrome.SubscriptionCount == 0 && chrome.OwnedCount == 0, "In-flight detach left sessions/subscription");

        chrome.HoldNextEvaluation();
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var auditing = runtime.AuditAsync(chrome.Url, chrome, cancel.Token);
            await chrome.EvaluationStarted.Task.WaitAsync(ct);
            cancel.Cancel();
            try { await auditing; throw new InvalidOperationException("Audit cancellation was ignored"); }
            catch (OperationCanceledException) { }
        }
        Check(chrome.SubscriptionCount == 0 && chrome.OwnedCount == 0,
            $"Cancelled audit leaked sessions/subscription (subscriptions={chrome.SubscriptionCount}, sessions={chrome.OwnedCount}, attaches={chrome.Attachments}, detaches={chrome.Detachments})");
        await WaitForAttachmentAsync(http, ct);
        // Immediately run another snapshot: cancellation acknowledgement must leave the worker slot reusable.
        await runtime.AuditAsync(chrome.Url, chrome, ct);
        Check(chrome.SubscriptionCount == 0 && chrome.OwnedCount == 0, "Second snapshot leaked sessions/subscription");

        chrome.HoldNextAttachment();
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var auditing = runtime.AuditAsync(chrome.Url, chrome, cancel.Token);
            await chrome.AttachmentCreated.Task.WaitAsync(ct);
            cancel.Cancel();
            try { await auditing; throw new InvalidOperationException("Late attachment cancellation was ignored"); }
            catch (OperationCanceledException) { }
            chrome.AttachmentRelease.TrySetResult();
            await chrome.Detached.Task.WaitAsync(ct);
        }
        Check(chrome.SubscriptionCount == 0 && chrome.OwnedCount == 0, "Late CDP attachment escaped cleanup");
        await WaitForAttachmentAsync(http, ct);
        Console.WriteLine("PASS C# public AuditAsync: real Lighthouse reports, child CDP sessions, cancellation/reuse, late-attach cleanup, and live proxy after cancellation");
    }

    private sealed class ControlledRpc : IOpenCodeBrowserTunnelRpc
    {
        private int sequence;
        private readonly ConcurrentDictionary<string, bool> delivered = new();
        public bool DelayOpen;
        public string? OpenError;
        public int Closed;
        public readonly TaskCompletionSource OpenStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource OpenRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> OpenAsync(OpenCodeBrowserTunnelTarget target, CancellationToken cancellationToken = default)
        {
            Check(target.Host == "controlled.invalid" && target.Port == 80, "Tunnel target mismatch");
            if (OpenError is not null) throw new IOException(OpenError);
            OpenStarted.TrySetResult();
            if (DelayOpen) await OpenRelease.Task; // Deliberately complete after cancellation to test cleanup.
            var id = "original-" + Interlocked.Increment(ref sequence);
            delivered[id] = false;
            return id;
        }
        public Task<OpenCodeBrowserTunnelRead> ReadAsync(string tunnelId, CancellationToken cancellationToken = default)
        {
            var first = delivered.TryUpdate(tunnelId, true, false);
            return Task.FromResult(new OpenCodeBrowserTunnelRead(first
                ? Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello") : Array.Empty<byte>(), !first));
        }
        public Task WriteAsync(string tunnelId, byte[] data, bool? end = null, CancellationToken cancellationToken = default)
        { Check(data.Length <= 65536, "Chunk size"); return Task.CompletedTask; }
        public Task CloseAsync(string tunnelId, CancellationToken cancellationToken = default)
        { delivered.TryRemove(tunnelId, out _); Interlocked.Increment(ref Closed); return Task.CompletedTask; }
    }
}

// These two serialization-neutral records are defined beside the application RPC extensions.
// Mirror them here so the focused source-linked harness needs no OpenCode HTTP client.
namespace FluentCode.Services.OpenCode.Browser
{
    public sealed record OpenCodeBrowserTunnelTarget(string Host, int Port);
    public sealed record OpenCodeBrowserTunnelRead(byte[] Data, bool Eof);
}
