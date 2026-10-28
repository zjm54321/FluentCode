using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

/// <summary>One process/profile (managed) or private browser context (attached), shared by owned tabs only.</summary>
internal sealed class OpenCodeBrowserChromeWorkspace : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly OpenCodeBrowserChromeOptions _configuration;
    private readonly Func<OpenCodeBrowserChromeWorkspace, Task> _release;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, OpenCodeBrowserChromeDriver> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeBrowserChromeDriver> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenCodeBrowserChromeDriver> _downloads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _authAttempts = new(StringComparer.Ordinal);
    private readonly OpenCodeBrowserCdpWebSocket _cdp = new();
    private Task? _initialization;
    private Process? _process;
    private FileStream? _profileLock;
    private string? _contextId;
    private int _references;
    private int _disposed;

    internal OpenCodeBrowserChromeWorkspace(OpenCodeBrowserChromeOptions configuration,
        OpenCodeBrowserPageDriverOptions options, Func<OpenCodeBrowserChromeWorkspace, Task> release)
    {
        _configuration = configuration;
        Options = options;
        _release = release;
        _cdp.Received += Receive;
        _cdp.Disconnected += Disconnected;
    }

    internal OpenCodeBrowserPageDriverOptions Options { get; }
    internal string DownloadDirectory => Path.Combine(Options.ProfileDirectory, "ChromeDownloads");
    internal bool Matches(OpenCodeBrowserPageDriverOptions options)
        => Path.GetFullPath(options.ProfileDirectory).Equals(Path.GetFullPath(Options.ProfileDirectory), StringComparison.OrdinalIgnoreCase)
           && options.Proxy == Options.Proxy;
    internal void AddReference() => _references++;
    internal bool RemoveReference() => --_references == 0;
    internal Task ReleaseAsync() => _release(this);

    private Task EnsureReadyAsync()
    {
        lock (_gate) return _initialization ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var proxy = Options.Proxy;
        if (!proxy.Address.IsAbsoluteUri || proxy.Address.Scheme != "http"
            || !OpenCodeBrowserChromeConnector.IsLoopback(proxy.Address.Host)
            || !string.IsNullOrEmpty(proxy.Address.UserInfo) || proxy.Address.AbsolutePath != "/"
            || !string.IsNullOrEmpty(proxy.Address.Query) || !string.IsNullOrEmpty(proxy.Address.Fragment)
            || proxy.BypassList != "<-loopback>")
            throw new ArgumentException("Chrome requires the workspace's authenticated loopback HTTP proxy with <-loopback> bypass policy.");
        OpenCodeBrowserChromeConnector.ValidateProfile(Options.ProfileDirectory);
        try
        {
            Directory.CreateDirectory(Options.ProfileDirectory);
            // Also prevents two factory instances/workspaces from accidentally sharing an on-disk profile.
            _profileLock = new FileStream(Path.Combine(Options.ProfileDirectory, ".opencode-chrome.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Directory.CreateDirectory(DownloadDirectory);
            Uri endpoint;
            if (_configuration.Attach)
            {
                endpoint = await OpenCodeBrowserChromeConnector.ResolveEndpointAsync(_configuration.Endpoint
                    ?? throw new ArgumentException("Chrome attach requires a loopback debugging endpoint."), token).ConfigureAwait(false);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(_configuration.Endpoint))
                    throw new ArgumentException("A Chrome endpoint is only used when Attach is enabled.");
                var executable = OpenCodeBrowserChromeConnector.FindExecutable(_configuration.ExecutablePath);
                var portFile = Path.Combine(Options.ProfileDirectory, "DevToolsActivePort");
                if (File.Exists(portFile)) File.Delete(portFile);
                _process = OpenCodeBrowserChromeConnector.Launch(executable, Options.ProfileDirectory, proxy);
                endpoint = await OpenCodeBrowserChromeConnector.WaitForEndpointAsync(_process, Options.ProfileDirectory, token).ConfigureAwait(false);
            }
            await _cdp.ConnectAsync(endpoint, token).ConfigureAwait(false);
            if (_configuration.Attach)
            {
                // These policies cannot be installed per incognito context by CDP. Refuse an unsafe host instead
                // of silently allowing WebRTC's direct UDP path in the attached process.
                var commandLine = await _cdp.SendAsync("Browser.getBrowserCommandLine", cancellationToken: token).ConfigureAwait(false);
                var arguments = (commandLine["arguments"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToArray() ?? [];
                if (!arguments.Contains("--disable-quic", StringComparer.Ordinal)
                    || !arguments.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", StringComparer.Ordinal))
                    throw new InvalidOperationException("Attach Chrome must be started with --enable-automation --disable-quic --force-webrtc-ip-handling-policy=disable_non_proxied_udp to enforce workspace network isolation.");
                var context = await _cdp.SendAsync("Target.createBrowserContext", new JsonObject
                {
                    ["disposeOnDetach"] = true,
                    ["proxyServer"] = proxy.Address.GetLeftPart(UriPartial.Authority),
                    ["proxyBypassList"] = "<-loopback>"
                }, cancellationToken: token).ConfigureAwait(false);
                _contextId = context["browserContextId"]!.GetValue<string>();
            }
            var downloads = new JsonObject
            {
                ["behavior"] = "allowAndName", ["downloadPath"] = DownloadDirectory, ["eventsEnabled"] = true
            };
            if (_contextId != null) downloads["browserContextId"] = _contextId;
            await _cdp.SendAsync("Browser.setDownloadBehavior", downloads, cancellationToken: token).ConfigureAwait(false);
            await _cdp.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<(string Target, string Session)> CreatePageAsync(OpenCodeBrowserChromeDriver driver)
    {
        await EnsureReadyAsync().ConfigureAwait(false);
        _lifetime.Token.ThrowIfCancellationRequested();
        var parameters = new JsonObject { ["url"] = "about:blank", ["background"] = true };
        if (_contextId != null) parameters["browserContextId"] = _contextId;
        var target = (await _cdp.SendAsync("Target.createTarget", parameters, cancellationToken: _lifetime.Token).ConfigureAwait(false))["targetId"]!.GetValue<string>();
        lock (_gate) _targets[target] = driver;
        try
        {
            var attached = await _cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true }, cancellationToken: _lifetime.Token).ConfigureAwait(false);
            var session = attached["sessionId"]!.GetValue<string>();
            lock (_gate) _sessions[session] = driver;
            driver.SetTransport(target, session);
            await ConfigureSessionAsync(session, true).ConfigureAwait(false);
            return (target, session);
        }
        catch
        {
            await ClosePageAsync(driver, target).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConfigureSessionAsync(string session, bool document)
    {
        // Chrome exposes Fetch on page/OOPIF targets, not dedicated workers. The owning document's
        // URLLoader interceptor also covers its worker requests; register it before resuming workers.
        if (document)
            await _cdp.SendAsync("Fetch.enable", new JsonObject
            {
                ["handleAuthRequests"] = true,
                ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*", ["requestStage"] = "Request" })
            }, session, _lifetime.Token).ConfigureAwait(false);
        await _cdp.SendAsync("Network.enable", sessionId: session, cancellationToken: _lifetime.Token).ConfigureAwait(false);
        if (document)
            await _cdp.SendAsync("Page.enable", sessionId: session, cancellationToken: _lifetime.Token).ConfigureAwait(false);
        await _cdp.SendAsync("Target.setAutoAttach", new JsonObject
        {
            ["autoAttach"] = true, ["waitForDebuggerOnStart"] = true, ["flatten"] = true
        }, session, _lifetime.Token).ConfigureAwait(false);
    }

    internal Task<JsonObject> SendAsync(string method, JsonObject? parameters, string? session, CancellationToken token)
        => _cdp.SendAsync(method, parameters, session, token);

    internal bool OwnsSession(OpenCodeBrowserChromeDriver driver, string session)
    {
        lock (_gate) return _sessions.TryGetValue(session, out var owner) && ReferenceEquals(driver, owner);
    }

    private void Receive(OpenCodeBrowserCdpEvent message)
    {
        OpenCodeBrowserChromeDriver? driver = null;
        lock (_gate)
        {
            if (message.SessionId != null) _sessions.TryGetValue(message.SessionId, out driver);
            else if (message.Method == "Target.detachedFromTarget" && message.Parameters["sessionId"]?.GetValue<string>() is string detached)
                _sessions.TryGetValue(detached, out driver);
        }
        // Frame ownership must be current when a following browser-level download arrives, not when
        // the UI queue eventually handles the preceding Page event. Only owned renderer sessions enter here.
        if (driver != null && message.SessionId != null) driver.ObserveFrames(message);
        if (message.Method == "Target.targetCreated" && message.Parameters["targetInfo"] is JsonObject target)
        {
            var opener = target["openerId"]?.GetValue<string>();
            if (opener != null)
            {
                lock (_gate) _targets.TryGetValue(opener, out driver);
                if (driver != null)
                {
                    // Page.windowOpen supplies the app-tab request. Never adopt Chrome's popup target.
                    Observe(_cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = target["targetId"]!.GetValue<string>() }));
                }
            }
            return;
        }
        if (message.Method == "Target.attachedToTarget" && driver != null)
        {
            var child = message.Parameters["sessionId"]!.GetValue<string>();
            var info = message.Parameters["targetInfo"] as JsonObject;
            var type = info?["type"]?.GetValue<string>();
            var childTarget = info?["targetId"]?.GetValue<string>();
            if (type == "page" && childTarget != driver.TargetId)
            {
                if (childTarget != null) Observe(_cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = childTarget }));
                return;
            }
            lock (_gate)
            {
                _sessions[child] = driver;
                if (childTarget != null) _targets[childTarget] = driver;
            }
            Observe(ResumeChildAsync(child, type is "iframe" or "page"));
        }
        if (message.Method is "Fetch.authRequired" or "Fetch.requestPaused")
        {
            if (driver != null && message.SessionId != null) Observe(HandleFetchAsync(message));
        }
        if (message.Method == "Browser.downloadWillBegin")
        {
            var frame = message.Parameters["frameId"]?.GetValue<string>();
            lock (_gate)
            {
                driver = _targets.Values.FirstOrDefault(d => frame != null && d.OwnsFrame(frame));
                if (driver != null) _downloads[message.Parameters["guid"]!.GetValue<string>()] = driver;
            }
        }
        else if (message.Method == "Browser.downloadProgress")
        {
            lock (_gate) _downloads.TryGetValue(message.Parameters["guid"]!.GetValue<string>(), out driver);
        }
        else if (message.Method is "Target.targetDestroyed" or "Target.targetInfoChanged")
        {
            var id = message.Method == "Target.targetDestroyed" ? message.Parameters["targetId"]?.GetValue<string>()
                : message.Parameters["targetInfo"]?["targetId"]?.GetValue<string>();
            lock (_gate) { if (id != null) _targets.TryGetValue(id, out driver); }
            if (message.Method == "Target.targetDestroyed")
            {
                if (id == driver?.TargetId) driver?.ConnectionFailed(new IOException("The owned Chrome tab was closed."));
                else if (id != null) { lock (_gate) _targets.Remove(id); }
            }
        }
        if (driver != null) driver.Receive(message);
        else if (message.SessionId == null && !message.Method.StartsWith("Target.", StringComparison.Ordinal)
                 && !message.Method.StartsWith("Browser.download", StringComparison.Ordinal))
        {
            OpenCodeBrowserChromeDriver[] owners;
            lock (_gate) owners = _targets.Values.Distinct().ToArray();
            foreach (var owner in owners) owner.Receive(message);
        }
        if (message.Method == "Target.detachedFromTarget")
        {
            var detached = message.Parameters["sessionId"]?.GetValue<string>();
            if (detached != null)
            {
                lock (_gate) _sessions.Remove(detached);
                if (detached == driver?.RootSessionId) driver.ConnectionFailed(new IOException("Chrome detached the owned page session."));
            }
        }
    }

    private async Task ResumeChildAsync(string session, bool document)
    {
        await ConfigureSessionAsync(session, document).ConfigureAwait(false);
        await _cdp.SendAsync("Runtime.runIfWaitingForDebugger", sessionId: session, cancellationToken: _lifetime.Token).ConfigureAwait(false);
    }

    private async Task HandleFetchAsync(OpenCodeBrowserCdpEvent message)
    {
        var requestId = message.Parameters["requestId"]!.GetValue<string>();
        if (message.Method == "Fetch.requestPaused")
        {
            await _cdp.SendAsync("Fetch.continueRequest", new JsonObject { ["requestId"] = requestId }, message.SessionId, _lifetime.Token).ConfigureAwait(false);
            return;
        }
        var challenge = message.Parameters["authChallenge"] as JsonObject;
        var proxy = Options.Proxy;
        var origin = challenge?["origin"]?.GetValue<string>();
        var matches = challenge?["source"]?.GetValue<string>() == "Proxy"
            && string.Equals(challenge?["scheme"]?.GetValue<string>(), "basic", StringComparison.OrdinalIgnoreCase)
            && challenge?["realm"]?.GetValue<string>() == proxy.Realm
            && Uri.TryCreate(origin, UriKind.Absolute, out var address)
            && address.Scheme == proxy.Address.Scheme
            && string.Equals(address.Host, proxy.Address.Host, StringComparison.OrdinalIgnoreCase)
            && address.Port == proxy.Address.Port;
        lock (_gate) matches &= _authAttempts.Add(message.SessionId + ":" + requestId);
        var response = new JsonObject { ["response"] = matches ? "ProvideCredentials" : "CancelAuth" };
        if (matches) { response["username"] = proxy.Username; response["password"] = proxy.Password; }
        await _cdp.SendAsync("Fetch.continueWithAuth", new JsonObject
        {
            ["requestId"] = requestId, ["authChallengeResponse"] = response
        }, message.SessionId, _lifetime.Token).ConfigureAwait(false);
    }

    internal async Task CancelDownloadAsync(string id)
    {
        var parameters = new JsonObject { ["guid"] = id };
        if (_contextId != null) parameters["browserContextId"] = _contextId;
        await _cdp.SendAsync("Browser.cancelDownload", parameters, cancellationToken: _lifetime.Token).ConfigureAwait(false);
    }

    internal async Task ClosePageAsync(OpenCodeBrowserChromeDriver driver, string? target)
    {
        lock (_gate)
        {
            foreach (var key in _sessions.Where(pair => ReferenceEquals(pair.Value, driver)).Select(pair => pair.Key).ToArray()) _sessions.Remove(key);
            foreach (var key in _downloads.Where(pair => ReferenceEquals(pair.Value, driver)).Select(pair => pair.Key).ToArray()) _downloads.Remove(key);
            foreach (var key in _targets.Where(pair => ReferenceEquals(pair.Value, driver)).Select(pair => pair.Key).ToArray()) _targets.Remove(key);
        }
        if (target != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _cdp.SendAsync("Target.closeTarget", new JsonObject { ["targetId"] = target }, cancellationToken: timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or OpenCodeBrowserChromeProtocolException) { }
        }
    }

    private void Observe(Task task) => _ = ObserveAsync(task);
    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OpenCodeBrowserChromeProtocolException error) when (error.Message.Contains("Invalid InterceptionId", StringComparison.Ordinal)
            || error.Message.Contains("Session with given id not found", StringComparison.Ordinal)
            || error.Message.Contains("Target closed", StringComparison.Ordinal)) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested) Disconnected(error); }
    }

    private void Disconnected(Exception error)
    {
        OpenCodeBrowserChromeDriver[] owners;
        lock (_gate) owners = _targets.Values.Distinct().ToArray();
        foreach (var owner in owners) owner.ConnectionFailed(error);
    }

    private async Task CleanupAsync()
    {
        // Attached Chrome is never sent Browser.close and its process is never killed.
        if (_contextId != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _cdp.SendAsync("Target.disposeBrowserContext", new JsonObject { ["browserContextId"] = _contextId }, cancellationToken: timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or OpenCodeBrowserChromeProtocolException) { }
            _contextId = null;
        }
        await _cdp.DisposeAsync().ConfigureAwait(false);
        if (_process != null)
        {
            try { if (!_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
            _process.Dispose();
            _process = null;
        }
        _profileLock?.Dispose();
        _profileLock = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_initialization != null)
        {
            try { await _initialization.ConfigureAwait(false); }
            catch (Exception) { /* Initialization's original exception remains on each page's readiness task. */ }
        }
        await CleanupAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
