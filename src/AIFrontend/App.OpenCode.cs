using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Taskbar;
using Microsoft.UI.Dispatching;

namespace AIFrontend;

public sealed record AppIntegrationDiagnostic(DateTimeOffset AtUtc, string Code,
    string? EndpointIdentity = null, string? ErrorType = null, int? HResult = null,
    NotificationInteractionResult? Interaction = null,
    OpenCodeNotificationObservation? Observation = null, TaskbarOperationResult? Taskbar = null);

public sealed class AppIntegrationDiagnosticEventArgs(AppIntegrationDiagnostic diagnostic) : EventArgs
{
    public AppIntegrationDiagnostic Diagnostic { get; } = diagnostic;
}

public partial class App
{
    private readonly object _integrationSync = new();
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly CancellationTokenSource _appLifetime = new();
    private CancellationTokenSource? _configurationCancellation;
    private DispatcherQueue? _appDispatcher;
    private volatile bool _closing;
    private int _configurationGeneration;
    private OpenCodeSettings? _requestedOpenCodeConfiguration;
    private bool _configurationRequested;
    private SettingsService? _settingsService;
    private DpapiSecretStore? _secretStore;
    private OpenCodeClient? _openCodeClient;
    private OpenCodeNotificationMonitor? _openCodeMonitor;
    private OpenCodePageSource? _openCodePageSource;
    private long _openCodeSourceRevision;
    private string? _openCodeEndpointIdentity;
    private string? _trackedEndpointIdentity;
    private readonly HashSet<string> _trackedOpenCodeSessions = new(StringComparer.Ordinal);
    private readonly Queue<AppIntegrationDiagnostic> _integrationDiagnostics = new();

    public string? OpenCodeEndpointIdentity { get { lock (_integrationSync) return _openCodeEndpointIdentity; } }
    public OpenCodePageSource? CurrentOpenCodePageSource { get { lock (_integrationSync) return _openCodePageSource; } }
    public IReadOnlyList<AppIntegrationDiagnostic> IntegrationDiagnostics
    { get { lock (_integrationSync) return _integrationDiagnostics.ToArray(); } }
    public event EventHandler<AppIntegrationDiagnosticEventArgs>? IntegrationDiagnosticChanged;
    public event EventHandler? OpenCodeSourceChanged;

    /// <summary>UI thread. Call only for a session explicitly selected/created by this app on the current endpoint.</summary>
    public void TrackOpenCodeSession(string sessionId)
    {
        RequireAppThread();
        if (_closing) return;
        lock (_integrationSync)
        {
            if (_openCodeMonitor is null) { RecordDiagnostic("opencode-track-not-ready"); return; }
            if (_openCodeMonitor.TrackSession(sessionId)) _trackedOpenCodeSessions.Add(sessionId);
        }
    }

    private void StartOpenCodeOwnership()
    {
        _settingsService = Pages.NormalPage.SharedSettingsService;
        _secretStore = Pages.NormalPage.SharedSecretStore;
        _settingsService.Changed += OnAppSettingsChanged;
        RequestOpenCodeConfiguration(_settingsService.Current);
    }

    private void OnAppSettingsChanged(object? sender, AppSettingsChangedEventArgs args) =>
        PostToApp(() => RequestOpenCodeConfiguration(_settingsService?.Current));

    /// <summary>UI thread; explicitly re-read credentials even when the reference is unchanged. Does not clear protection.</summary>
    public void RefreshOpenCodeCredentials()
    {
        RequireAppThread();
        RequestOpenCodeConfiguration(_settingsService?.Current, force: true);
    }

    private void RequestOpenCodeConfiguration(AppSettings? settings, bool force = false)
    {
        if (_closing) return;
        OpenCodeSettings? relevant = settings?.OpenCode;
        try
        {
            if (relevant is not null && !string.IsNullOrWhiteSpace(relevant.BaseUrl))
                relevant = relevant with { BaseUrl = OpenCodeClient.NormalizeBaseUri(relevant.BaseUrl).AbsoluteUri };
        }
        catch (Exception ex) { RecordDiagnostic("opencode-configuration-invalid", error: ex); }
        if (!force && _configurationRequested && Equals(relevant, _requestedOpenCodeConfiguration)) return;
        _configurationRequested = true;
        _requestedOpenCodeConfiguration = relevant;
        // Fence native/redirected activation synchronously, even while an older configuration holds the semaphore.
        int generation;
        lock (_integrationSync)
        {
            generation = Interlocked.Increment(ref _configurationGeneration);
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            _ = _completionNotifications?.SuspendInteractionsAsync(_openCodeEndpointIdentity);
        }
        PostToApp(() => RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty));
        _pendingNavigation.Clear();
        _navigationSeen.Clear();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token);
        var previous = Interlocked.Exchange(ref _configurationCancellation, cancellation);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { /* Previous configuration already drained. */ }
        _ = ConfigureOpenCodeAsync(settings, generation, cancellation);
    }

    private async Task ConfigureOpenCodeAsync(AppSettings? settings, int generation, CancellationTokenSource cancellation)
    {
        bool held = false;
        try
        {
            var token = cancellation.Token;
            await _configurationGate.WaitAsync(token).ConfigureAwait(false);
            held = true;
            token.ThrowIfCancellationRequested();
            await ReleaseOpenCodeSourceAsync().ConfigureAwait(false);
            settings ??= await _settingsService!.LoadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var options = settings.OpenCode;
            if (string.IsNullOrWhiteSpace(options.BaseUrl)) { RecordDiagnostic("opencode-not-configured"); return; }
            string? secret = null;
            if (options.AuthenticationMode != OpenCodeAuthenticationMode.None)
            {
                if (!string.IsNullOrWhiteSpace(options.CredentialId))
                {
                    try { secret = await _secretStore!.GetAsync(options.CredentialId, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { RecordDiagnostic("opencode-credential-read-failed", error: ex); return; }
                    if (string.IsNullOrWhiteSpace(secret)) { RecordDiagnostic("opencode-credential-missing"); return; }
                }
                else if (options.AuthenticationMode == OpenCodeAuthenticationMode.Basic)
                { RecordDiagnostic("opencode-basic-credential-required"); return; }
            }
            token.ThrowIfCancellationRequested();
            var clientOptions = new OpenCodeClientOptions
            {
                BaseUrl = options.BaseUrl, AuthenticationMode = options.AuthenticationMode,
                Username = options.Username, Secret = secret
            };
            var client = new OpenCodeClient(clientOptions); // Validates options before publishing a page factory.
            // Identity is endpoint-only so credential changes do NOT bypass unknown-result tombstones.
            string endpoint = CreateOpenCodeEndpointIdentity(options.BaseUrl);
            var monitor = new OpenCodeNotificationMonitor(_completionNotifications!, new(endpoint, client));
            monitor.Observed += OnOpenCodeObserved;
            lock (_integrationSync)
            {
                if (_closing || token.IsCancellationRequested || generation != _configurationGeneration)
                { monitor.Dispose(); client.Dispose(); return; }
                _openCodeClient = client;
                _openCodeMonitor = monitor;
                _openCodeEndpointIdentity = endpoint;
                _openCodePageSource = new OpenCodePageSource(endpoint, checked(++_openCodeSourceRevision),
                    () => new OpenCodeClient(clientOptions)); // Same immutable options; no second secret read.
                if (_trackedEndpointIdentity != endpoint) _trackedOpenCodeSessions.Clear();
                _trackedEndpointIdentity = endpoint;
                foreach (string session in _trackedOpenCodeSessions) monitor.TrackSession(session);
                _completionNotifications!.ResumeInteractions();
            }
            bool started = monitor.Start(); // Returns false unless the ONE notification service initialized successfully.
            RecordDiagnostic(started ? "opencode-monitor-ready" : "opencode-monitor-notifications-unavailable");
            PostToApp(() => { RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty); FlushOpenCodeNavigation(); });
        }
        catch (OperationCanceledException) { /* Superseded configuration, not a credential or connectivity success. */ }
        catch (Exception ex) { RecordDiagnostic("opencode-configuration-failed", error: ex); }
        finally
        {
            if (held) _configurationGate.Release();
            Interlocked.CompareExchange(ref _configurationCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static string CreateOpenCodeEndpointIdentity(string baseUrl)
    {
        var uri = OpenCodeClient.NormalizeBaseUri(baseUrl);
        return "ep-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    }

    private void OnOpenCodeObserved(object? sender, OpenCodeNotificationObservationEventArgs args)
    {
        lock (_integrationSync) if (_closing || !ReferenceEquals(sender, _openCodeMonitor)) return;
        RecordDiagnostic("opencode-observation", observation: args.Observation);
        if (args.Observation.Interaction is { } result)
            PostToApp(() =>
            {
                RecordDiagnostic("opencode-interaction", interaction: result);
                if (result.EndpointIdentity is { } endpoint && endpoint != OpenCodeEndpointIdentity) return;
                // Preparation NeedsApp is a visible diagnostic, NOT a user click: do not steal focus.
                RaiseAppEvent(OpenCodeInteractionProcessed, new NotificationInteractionEventArgs(result));
            });
        // A session projection is not an execution ID. Do not fabricate taskbar progress/completion here.
    }

    private void StopOpenCodeRequests()
    {
        lock (_integrationSync)
        {
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            Interlocked.Increment(ref _configurationGeneration);
            _ = _completionNotifications?.SuspendInteractionsAsync(_openCodeEndpointIdentity);
        }
        if (_settingsService is not null) _settingsService.Changed -= OnAppSettingsChanged;
        _appLifetime.Cancel();
    }

    private async Task DrainOpenCodeOwnershipAsync()
    {
        await _configurationGate.WaitAsync().ConfigureAwait(false);
        try { await ReleaseOpenCodeSourceAsync().ConfigureAwait(false); }
        finally { _configurationGate.Release(); }
    }

    private async Task ReleaseOpenCodeSourceAsync()
    {
        OpenCodeNotificationMonitor? monitor;
        OpenCodeClient? client;
        string? endpoint;
        lock (_integrationSync)
        {
            monitor = _openCodeMonitor; client = _openCodeClient; endpoint = _openCodeEndpointIdentity;
            // Revoke before exposing an empty owner and before any monitor await.
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            _ = _completionNotifications?.SuspendInteractionsAsync(endpoint);
            _openCodeMonitor = null; _openCodeClient = null; _openCodeEndpointIdentity = null;
        }
        PostToApp(() => RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty));
        try
        {
            if (monitor is not null) await monitor.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_completionNotifications is { } service)
                await service.SuspendInteractionsAsync(endpoint).ConfigureAwait(false);
            try
            {
                if (monitor is not null)
                {
                    monitor.Observed -= OnOpenCodeObserved;
                    await monitor.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { client?.Dispose(); }
        }
    }

    private void RequireAppThread()
    {
        if (_appDispatcher?.HasThreadAccess != true) throw new InvalidOperationException("Call on the App UI thread after launch.");
    }

    private void PostToApp(Action action)
    {
        if (_closing) return;
        try
        {
            _appDispatcher?.TryEnqueue(() =>
            {
                if (_closing) return;
                try { action(); } catch (Exception ex) { RecordDiagnostic("app-callback-failed", error: ex); }
            });
        }
        catch { /* Dispatcher teardown cannot resurrect a closed window. */ }
    }

    private void RecordDiagnostic(string code, Exception? error = null, NotificationInteractionResult? interaction = null,
        OpenCodeNotificationObservation? observation = null, TaskbarOperationResult? taskbar = null)
    {
        AppIntegrationDiagnostic diagnostic;
        lock (_integrationSync)
        {
            diagnostic = new(DateTimeOffset.UtcNow, code, observation?.EndpointIdentity ?? interaction?.EndpointIdentity ?? _openCodeEndpointIdentity,
                error?.GetType().FullName, error?.HResult, interaction, observation, taskbar);
            if (_integrationDiagnostics.Count == 128) _integrationDiagnostics.Dequeue();
            _integrationDiagnostics.Enqueue(diagnostic);
        }
        PostToApp(() => RaiseAppEvent(IntegrationDiagnosticChanged, new AppIntegrationDiagnosticEventArgs(diagnostic)));
    }

    private void RaiseAppEvent<T>(EventHandler<T>? handlers, T args) where T : EventArgs
    {
        if (handlers is null || _closing) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
            try { handler(this, args); } catch { /* Subscribers do not break owner lifecycle. */ }
    }

    private void RaiseAppEvent(EventHandler? handlers, EventArgs args)
    {
        if (handlers is null || _closing) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, args); } catch { }
    }
}
