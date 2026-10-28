using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml.Controls;

internal static class NativeAuditFixture
{
    internal sealed record CdpCall(string Method, string? SessionId, bool Succeeded, string? Error);
    private sealed class RecordedCdp(IOpenCodeBrowserCdpConnection actual) : IOpenCodeBrowserCdpConnection
    {
        internal readonly ConcurrentQueue<CdpCall> Calls = new();
        internal readonly ConcurrentDictionary<string, int> Events = new(StringComparer.Ordinal);
        internal Action<string, string?>? BeforeSend;
        public async Task<JsonObject> CdpAsync(string method, JsonObject? parameters = null, string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                BeforeSend?.Invoke(method, sessionId);
                var response = await actual.CdpAsync(method, parameters, sessionId, cancellationToken).ConfigureAwait(false);
                Calls.Enqueue(new(method, sessionId, true, null)); return response;
            }
            catch (Exception ex) { Calls.Enqueue(new(method, sessionId, false, ex.ToString())); throw; }
        }
        public Task<IAsyncDisposable> SubscribeCdpAsync(string method, Action<OpenCodeBrowserCdpEvent> receive,
            CancellationToken cancellationToken = default) => actual.SubscribeCdpAsync(method, item =>
        {
            Events.AddOrUpdate(item.Method + (item.SessionId is null ? ":root" : ":renderer"), 1, (_, count) => count + 1);
            receive(item);
        }, cancellationToken);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<JsonObject> StateAsync(OpenCodeBrowserWebView2Driver driver)
    {
        var json = await driver.ExecuteScriptAsync("""
            (()=>({url:location.href,width:innerWidth,height:innerHeight,dpr:devicePixelRatio,
                scrollX,scrollY,history:history.length,local:localStorage.getItem('native-audit'),localCount:localStorage.length,
                session:sessionStorage.getItem('native-audit'),sessionCount:sessionStorage.length,
                cookie:document.cookie,marker:window.__nativeAuditMarker}))()
            """);
        return JsonNode.Parse(json)!.AsObject();
    }
    internal static async Task<bool> RunAsync(Grid host, string evidence, string runtimeDirectory)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        using var attachment = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        using var rpc = new AuditTunnelRpc();
        IOpenCodeBrowserRuntime? runtime = null; OpenCodeBrowserWebView2Driver? driver = null; RecordedCdp? cdp = null;
        JsonObject? before = null, after = null, afterCancellation = null, value = null;
        double? score = null; string? browserVersion = null, error = null, cancellation = null;
        string? cancelledCommand = null; var cancelledOwnedSession = 0; var proxyAlive = false; var passed = false;
        var navigationStarts = 0; var startsBefore = 0; var startsAfter = 0;
        var reports = new List<object>(); var watch = Stopwatch.StartNew();
        try
        {
            runtime = await new OpenCodeBrowserRuntimeFactory().CreateAsync(new(Guid.NewGuid().ToString("N"), runtimeDirectory), lifetime.Token);
            runtime.SetAttachment(new OpenCodeBrowserAttachmentLease("native-audit-fixture", Guid.NewGuid().ToString("N"), rpc, attachment.Token));
            driver = (OpenCodeBrowserWebView2Driver)new OpenCodeBrowserWebView2DriverFactory().Create(new(
                Guid.NewGuid().ToString("N"), Path.Combine(evidence, "profile"), runtime.Proxy, runtime.CdpEventNames));
            host.Children.Add(driver.View); await driver.EnsureReadyAsync(lifetime.Token);
            browserVersion = driver.Core.Environment.BrowserVersionString;
            driver.NavigationChanged += (_, change) => { if (change.Phase == OpenCodeBrowserNavigationPhase.Starting) navigationStarts++; };
            await driver.NavigateAsync(AuditTunnelRpc.Url, lifetime.Token);
            using (var loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                loading.CancelAfter(TimeSpan.FromSeconds(15));
                while (driver.State.Title != "native-lighthouse-fixture" || driver.State.Loading || driver.State.LoadError is not null)
                    await Task.Delay(30, loading.Token);
            }
            Check(rpc.Requests > 0 && rpc.Opened > 0, "Native page did not traverse the real sidecar tunnel callbacks.");
            await driver.ExecuteScriptAsync("localStorage.setItem('native-audit','preserve-local');sessionStorage.setItem('native-audit','preserve-session');document.cookie='nativeAudit=preserve-cookie;path=/';window.__nativeAuditMarker='preserve-document';window.scrollTo(0,100);");
            before = await StateAsync(driver); startsBefore = navigationStarts;
            cdp = new RecordedCdp(driver);
            var audit = await runtime.AuditAsync(AuditTunnelRpc.Url, cdp, lifetime.Token);
            value = audit.Value;
            foreach (var report in audit.Reports)
            {
                Check(report.Name is "lighthouse.json" or "lighthouse.html", "Unexpected native Lighthouse report name.");
                await File.WriteAllBytesAsync(Path.Combine(evidence, report.Name), report.Data, lifetime.Token);
                reports.Add(new { report.Name, report.Mime, Bytes = report.Data.Length });
            }
            var raw = JsonNode.Parse(audit.Reports.Single(report => report.Name == "lighthouse.json").Data)!.AsObject();
            score = raw["categories"]?["accessibility"]?["score"]?.GetValue<double>();
            Check(score is >= 0 and < 1, "Real Lighthouse did not detect the intentional accessibility failures.");
            Check(raw["audits"]?["button-name"]?["score"]?.GetValue<double>() == 0,
                "Real Lighthouse did not flag the controlled unnamed button.");
            Check(audit.Reports.Single(report => report.Name == "lighthouse.html").Data.Length > 1000, "Missing real HTML report.");
            after = await StateAsync(driver); startsAfter = navigationStarts;
            Check(JsonNode.DeepEquals(before, after) && startsBefore == startsAfter,
                "Production Lighthouse snapshot changed viewport, storage, URL/navigation or document sentinel.");
            var proxyAddress = runtime.Proxy.Address;
            using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                cdp.BeforeSend = (method, session) =>
                {
                    if (session is null || Interlocked.Exchange(ref cancelledOwnedSession, 1) != 0) return;
                    cancelledCommand = method; cancel.Cancel();
                };
                try
                {
                    await runtime.AuditAsync(AuditTunnelRpc.Url, cdp, cancel.Token);
                    cancellation = "BOUNDARY: second audit completed before owned-session cancellation.";
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                { cancellation = "PASS: in-flight production audit cancelled after owned session attachment."; }
                catch (Exception ex) { cancellation = "BOUNDARY: " + ex; }
                finally { cdp.BeforeSend = null; }
            }
            var probesBefore = rpc.ProbeRequests;
            var probe = await driver.CdpAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = "fetch('/after-cancel?nonce=" + Guid.NewGuid().ToString("N") + "',{cache:'no-store'}).then(r=>r.text())",
                ["awaitPromise"] = true, ["returnByValue"] = true,
            }, cancellationToken: lifetime.Token);
            proxyAlive = runtime.Proxy.Address == proxyAddress && rpc.ProbeRequests > probesBefore
                && probe["result"]?["value"]?.GetValue<string>() == "sidecar-proxy-still-alive";
            Check(proxyAlive, "Production sidecar proxy did not survive audit cancellation.");
            afterCancellation = await StateAsync(driver);
            Check(JsonNode.DeepEquals(before, afterCancellation) && navigationStarts == startsBefore,
                "Cancelled audit changed viewport, storage, URL/navigation or document sentinel.");
            Check(rpc.CredentialHeaders == 0, "Sidecar forwarded private authorization headers to the controlled origin.");
            passed = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            if (driver is not null) { try { await driver.DisposeAsync(); } catch (Exception ex) { error ??= ex.ToString(); passed = false; } }
            if (runtime is not null) { try { await runtime.DisposeAsync(); } catch (Exception ex) { error ??= ex.ToString(); passed = false; } }
            await File.WriteAllTextAsync(Path.Combine(evidence, "audit-results.json"), JsonSerializer.Serialize(new
            {
                Native = true, WebView2Sdk = "1.0.3179.45", BrowserVersion = browserVersion, Passed = passed,
                AccessibilityScore = score, Reports = reports, Result = value, Before = before, After = after,
                NavigationStartsBefore = startsBefore, NavigationStartsAfter = startsAfter,
                Cancellation = cancellation, CancelledOwnedSession = cancelledOwnedSession != 0, CancelledCommand = cancelledCommand,
                ProxyAliveAfterCancellation = proxyAlive, AfterCancellation = afterCancellation,
                TunnelOpens = rpc.Opened, OriginRequests = rpc.Requests, PostCancelProbeRequests = rpc.ProbeRequests,
                CredentialHeadersAtOrigin = rpc.CredentialHeaders, CdpCalls = cdp?.Calls.ToArray(), CdpEvents = cdp?.Events,
                ElapsedSeconds = watch.Elapsed.TotalSeconds, Error = error,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed;
    }
}
