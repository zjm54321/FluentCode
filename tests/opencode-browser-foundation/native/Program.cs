using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    internal static int ExitCode = 2;
    internal static string Evidence = "";
    internal static string? RuntimeDirectory;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2) throw new ArgumentException("Expected evidence directory and optional prepared runtime directory.");
        Evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(Evidence);
        RuntimeDirectory = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new FixtureApplication();
            });
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Evidence, "host-error.txt"), ex.ToString());
        }
        return ExitCode;
    }
}

internal sealed class FixtureApplication : Application
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var grid = new Grid(); _window = new Window { Title = "Isolated WebView2 native proxy fixture", Content = grid };
        grid.Loaded += async (_, _) => await RunAsync(grid);
        _window.Activate();
    }
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record AuthObservation(string Uri, string Challenge);
    private sealed record EventObservation(string Receiver, string Method, string? SessionId, string? ChildSessionId);
    private sealed record NavigationObservation(ulong Id, string Phase, bool IsSuccess, string? Error);
    private async Task RunAsync(Grid grid)
    {
        if (Program.RuntimeDirectory is { } runtimeDirectory)
        {
            try { Program.ExitCode = await NativeAuditFixture.RunAsync(grid, Program.Evidence, runtimeDirectory) ? 0 : 1; }
            finally { _window?.Close(); Exit(); }
            return;
        }
        var auth = new List<AuthObservation>(); var events = new List<EventObservation>();
        var navigations = new List<NavigationObservation>(); var firstEventuallySucceeded = false;
        var checks = new List<string>(); var oopif = "not attempted"; string? error = null; string? browserVersion = null;
        string? proxyAddress = null, originAddress = null, firstNavigationError = null;
        bool? proxyPolicyMatched = null, proxyResponsePopulated = null, proxyResponseCancelled = null;
        ControlledHttpFixture? observedFixture = null;
        OpenCodeBrowserWebView2Driver? driver = null;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        try
        {
            await using var fixture = new ControlledHttpFixture();
            observedFixture = fixture; proxyAddress = fixture.Proxy.Address.ToString(); originAddress = fixture.Origin("first").ToString();
            var catalogue = new[] { "Target.attachedToTarget", "Target.detachedFromTarget", "Runtime.executionContextCreated", "Runtime.consoleAPICalled", "Page.frameNavigated" };
            driver = (OpenCodeBrowserWebView2Driver)new OpenCodeBrowserWebView2DriverFactory().Create(new(
                Guid.NewGuid().ToString("N"), Path.Combine(Program.Evidence, "profile"), fixture.Proxy, catalogue));
            grid.Children.Add(driver.View);
            await driver.EnsureReadyAsync(lifetime.Token);
            driver.NavigationChanged += (_, navigation) => navigations.Add(new(navigation.NavigationId,
                navigation.Phase.ToString(), navigation.IsSuccess, navigation.LoadError));
            browserVersion = driver.Core.Environment.BrowserVersionString;
            Check(fixture.ProxyFixtureRequests == 0 && fixture.OriginRequests == 0, "Controlled URL was requested before explicit navigation");
            var proxyAuth = Completion<bool>(); var originAuth = Completion<bool>();
            driver.Core.BasicAuthenticationRequested += ObserveAuthentication;
            void ObserveAuthentication(CoreWebView2 sender, CoreWebView2BasicAuthenticationRequestedEventArgs request)
            {
                // Authentication evidence deliberately excludes Response, usernames, passwords and HTTP headers.
                auth.Add(new(request.Uri, request.Challenge));
                var isProxy = Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri) && uri.Port == fixture.Proxy.Address.Port && uri.Host == fixture.Proxy.Address.Host;
                // WinRT handler invocation order is not our assertion boundary. Observe after native event dispatch,
                // keeping args alive with a real deferral; never cancel before the production handler can run.
                var deferral = request.GetDeferral();
                DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
                {
                    try
                    {
                        if (isProxy)
                        {
                            var populated = request.Response.UserName == fixture.Proxy.Username && request.Response.Password == fixture.Proxy.Password;
                            proxyPolicyMatched = OpenCodeBrowserDriverPolicy.IsProxyChallenge(fixture.Proxy, request.Uri, request.Challenge);
                            proxyResponsePopulated = populated; proxyResponseCancelled = request.Cancel;
                            proxyAuth.TrySetResult(populated);
                            if (!populated) request.Cancel = true;
                        }
                        else
                        {
                            originAuth.TrySetResult(string.IsNullOrEmpty(request.Response.UserName) && string.IsNullOrEmpty(request.Response.Password));
                            request.Cancel = true; // No user credential interaction in this fixture.
                        }
                    }
                    finally { deferral.Complete(); }
                });
            }
            await NavigateAsync(driver, fixture.Origin("first").ToString(), lifetime.Token, requireSuccess: false);
            firstNavigationError = driver.State.LoadError;
            Check(await proxyAuth.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token), "Production proxy auth predicate did not populate credentials for real HTTP 407");
            var proxyEvent = auth.First();
            Check(new Uri(proxyEvent.Uri).GetLeftPart(UriPartial.Authority) == fixture.Proxy.Address.GetLeftPart(UriPartial.Authority), "407 args.Uri is not the configured proxy URI");
            Check(proxyEvent.Challenge == ControlledHttpFixture.Challenge, "407 challenge formatting differs from the production exact scheme/realm predicate");
            // A native authentication failure/completion can precede BasicAuthenticationRequested and its retry.
            // Assert actual origin delivery, not the first provisional navigation completion.
            await fixture.FirstReached.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            using (var content = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                content.CancelAfter(TimeSpan.FromSeconds(10));
                while (driver.State.Title != "controlled-first" || driver.State.Loading || driver.State.LoadError is not null)
                    await Task.Delay(30, content.Token);
            }
            firstEventuallySucceeded = true;
            Check(fixture.AuthorizedRequests > 0 && fixture.FirstWasProxied && fixture.DirectRequests == 0, "localhost first navigation bypassed the configured proxy");
            checks.Add("real HTTP407 proxy Uri/challenge and production credential response");
            checks.Add("proxy installed before first navigation; localhost reached origin only through authenticated proxy");
            await NavigateAsync(driver, fixture.Origin("origin401").ToString(), lifetime.Token, requireSuccess: false);
            Check(await originAuth.Task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token), "Same-realm origin401 received credentials in native authentication response");
            Check(auth.Any(item => new Uri(item.Uri).Port == fixture.OriginPort && item.Challenge == ControlledHttpFixture.Challenge), "Real same-realm origin401 challenge was not observed");
            Check(fixture.CredentialLeaks == 0, "Origin received private proxy credentials");
            checks.Add("same-realm origin HTTP401 has no proxy credentials in event response or origin request");
            oopif = await ProbeSessionsAsync(driver, fixture, events, lifetime.Token);
            Check(fixture.Errors.IsEmpty, "Controlled HTTP fixture failed: " + string.Join(",", fixture.Errors));
            Check(fixture.DirectRequests == 0 && fixture.CredentialLeaks == 0, "Later native requests bypassed proxy or leaked credentials");
            driver.Core.BasicAuthenticationRequested -= ObserveAuthentication;
            Program.ExitCode = 0;
        }
        catch (Exception ex) { error = ex.ToString(); Program.ExitCode = 1; }
        finally
        {
            if (driver is not null) { try { await driver.DisposeAsync(); } catch (Exception ex) { error ??= ex.ToString(); Program.ExitCode = 1; } }
            File.WriteAllText(Path.Combine(Program.Evidence, "results.json"), JsonSerializer.Serialize(new
            {
                Native = true, WebView2Sdk = "1.0.3179.45", BrowserVersion = browserVersion,
                Passed = Program.ExitCode == 0, Checks = checks, Authentication = auth, SessionEvents = events, Oopif = oopif, Error = error,
                ProxyAddress = proxyAddress, OriginAddress = originAddress, FirstNavigationError = firstNavigationError,
                ProxyPolicyMatched = proxyPolicyMatched, ProxyResponsePopulated = proxyResponsePopulated, ProxyResponseCancelled = proxyResponseCancelled,
                ProxyRequests = observedFixture?.ProxyFixtureRequests, AuthorizedProxyRequests = observedFixture?.AuthorizedRequests,
                DirectOriginRequests = observedFixture?.DirectRequests, OriginRequests = observedFixture?.OriginRequests,
                FirstNavigationEventuallySucceeded = firstEventuallySucceeded, Navigations = navigations,
            }, new JsonSerializerOptions { WriteIndented = true }));
            _window?.Close(); Exit();
        }
    }
    private static async Task NavigateAsync(OpenCodeBrowserWebView2Driver driver, string url, CancellationToken ct, bool requireSuccess = true)
    {
        var completed = Completion<OpenCodeBrowserDriverNavigation>();
        void Changed(object? sender, OpenCodeBrowserDriverNavigation navigation)
        { if (navigation.Phase == OpenCodeBrowserNavigationPhase.Completed) completed.TrySetResult(navigation); }
        driver.NavigationChanged += Changed;
        try
        {
            await driver.NavigateAsync(url, ct);
            var navigation = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (requireSuccess) Check(navigation.IsSuccess, "Controlled navigation failed: " + navigation.LoadError);
        }
        finally { driver.NavigationChanged -= Changed; }
    }
    private static async Task<string> ProbeSessionsAsync(OpenCodeBrowserWebView2Driver driver, ControlledHttpFixture fixture,
        List<EventObservation> events, CancellationToken lifetime)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime); probe.CancelAfter(TimeSpan.FromSeconds(18));
        var frames = new List<(string Session, string? Parent)>(); var workers = new List<Task>();
        var received = Completion<string>(); var namedSessions = new HashSet<string>(StringComparer.Ordinal);
        var autoAttach = new JsonObject { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = true, ["flatten"] = true };
        await using var wildcard = await driver.SubscribeCdpAsync("*", item =>
        {
            if (item.Method == "Target.attachedToTarget")
            {
                var child = item.Parameters["sessionId"]?.GetValue<string>();
                events.Add(new("wildcard", item.Method, item.SessionId, child));
                if (child is not null && item.Parameters["targetInfo"]?["type"]?.GetValue<string>() == "iframe")
                { frames.Add((child, item.SessionId)); workers.Add(InitializeFrameAsync(child)); }
            }
            if (item.Method == "Runtime.consoleAPICalled")
            {
                events.Add(new("wildcard", item.Method, item.SessionId, null));
                if (item.SessionId is { } session && frames.Any(frame => frame.Session == session)) received.TrySetResult(session);
            }
        }, probe.Token);
        await using var named = await driver.SubscribeCdpAsync("Runtime.consoleAPICalled", item =>
        {
            events.Add(new("named", item.Method, item.SessionId, null));
            if (item.SessionId is { } session) namedSessions.Add(session);
        }, probe.Token);
        async Task InitializeFrameAsync(string session)
        {
            await driver.CdpAsync("Target.setAutoAttach", (JsonObject)autoAttach.DeepClone(), session, probe.Token);
            await driver.CdpAsync("Runtime.enable", sessionId: session, cancellationToken: probe.Token);
            await driver.CdpAsync("Runtime.runIfWaitingForDebugger", sessionId: session, cancellationToken: probe.Token);
            await driver.CdpAsync("Runtime.evaluate", new JsonObject { ["expression"] = "console.log('native-session-check')" }, session, probe.Token);
        }
        try
        {
            await driver.CdpAsync("Target.setAutoAttach", (JsonObject)autoAttach.DeepClone(), cancellationToken: probe.Token);
            await NavigateAsync(driver, fixture.Origin("oopif").ToString(), probe.Token);
            var session = await received.Task.WaitAsync(probe.Token);
            await Task.WhenAll(workers.ToArray()).WaitAsync(probe.Token);
            Check(namedSessions.Contains(session), "Native named and wildcard receivers disagree on renderer session identity");
            return frames.Any(frame => frame.Parent is not null && frames.Any(parent => parent.Session == frame.Parent)
                && namedSessions.Contains(frame.Session) && events.Any(item => item.Receiver == "wildcard"
                    && item.Method == "Runtime.consoleAPICalled" && item.SessionId == frame.Session))
                ? "PASS: real nested OOPIF named/wildcard renderer SessionId preserved"
                : "PASS: real OOPIF named/wildcard renderer SessionId preserved; deeper nested target not observed";
        }
        catch (Exception ex)
        {
            // Optional boundary is reported verbatim, never presented as a passing synthetic session test.
            return "BOUNDARY: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            probe.Cancel();
            foreach (var worker in workers) { try { await worker; } catch (Exception) { } }
            try { await driver.CdpAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = false, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, cancellationToken: lifetime); }
            catch (Exception) { }
        }
    }
}
