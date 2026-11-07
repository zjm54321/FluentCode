using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Dispatching;

namespace FluentCode;

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
        => TrackOpenCodeSessionCore(sessionId, publishUi: true);

    private void TrackOpenCodeSessionCore(string sessionId, bool publishUi)
    {
        RequireAppThread();
        if (_closing) return;
        lock (_integrationSync)
        {
            if (_openCodeMonitor is null) { RecordDiagnostic("opencode-track-not-ready"); return; }
            if (_openCodeMonitor.TrackSession(sessionId)) _trackedOpenCodeSessions.Add(sessionId);
            if (_openCodePageSource is { } source) SeedOpenCodeObservation(source, sessionId);
        }
        if (publishUi) PublishSessionUiChanges();
    }

    /// <summary>UI thread. Closing a tab retires observation, never an unknown mutation's protection.</summary>
    public void UntrackOpenCodeSession(OpenCodePageSource source, string sessionId)
    {
        RequireAppThread();
        if (_closing) return;
        lock (_integrationSync)
        {
            if (!ReferenceEquals(source, _openCodePageSource) || source.IsRevoked) return;
            _trackedOpenCodeSessions.Remove(sessionId);
            _openCodeMonitor?.UntrackSession(sessionId);
        }
    }

    private void StartOpenCodeOwnership()
    {
        _settingsService = FluentCode.Services.Configuration.AppServices.Settings;
        _secretStore = FluentCode.Services.Configuration.AppServices.Secrets;
        _settingsService.Changed += OnAppSettingsChanged;
        RequestOpenCodeConfiguration(_settingsService.Current);
    }

    private void OnAppSettingsChanged(object? sender, AppSettingsChangedEventArgs args)
    {
        if (_settingsService?.Current is { } saved) _ = RemoveStoredSshSecretAsync(saved);
        PostToApp(() => RequestOpenCodeConfiguration(_settingsService?.Current));
    }

    private async Task RemoveStoredSshSecretAsync(AppSettings saved)
    {
        try
        {
            if (_settingsService is null || _secretStore is null) return;
            await OpenCodeConnectionCommitter.RemoveStoredSshSecretAsync(saved,
                (transform, ct) => _settingsService.UpdateAsync(transform, ct), () => _settingsService.Current,
                (id, ct) => _secretStore.RemoveAsync(id, ct), _appLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RecordDiagnostic("opencode-ssh-old-credential-cleanup-failed", error: ex); }
    }

    private void RequestOpenCodeConfiguration(AppSettings? settings, OpenCodeClientOptions? recoveredOptions = null,
        SshOpenCodeConnection? recoveredSsh = null)
    {
        if (_closing) { recoveredSsh?.Dispose(); return; }
        // WK1: kind/distribution now own recovery authority, not just presentation metadata.
        // Changing them must retire the old WSL recovery worker as well as its source capability.
        OpenCodeSettings? relevant = settings?.OpenCode;
        try
        {
            if (relevant is not null && !string.IsNullOrWhiteSpace(relevant.BaseUrl))
                relevant = relevant with { BaseUrl = OpenCodeClient.NormalizeBaseUri(relevant.BaseUrl).AbsoluteUri };
        }
        catch (Exception ex) { RecordDiagnostic("opencode-configuration-invalid", error: ex); }
        if (recoveredOptions is null && recoveredSsh is null && _configurationRequested && Equals(relevant, _requestedOpenCodeConfiguration)) return;
        StopWslRecovery();
        StopSshRecovery();
        _configurationRequested = true;
        _requestedOpenCodeConfiguration = relevant;
        ResetOpenCodeObservations();
        // Fence native/redirected activation synchronously, even while an older configuration holds the semaphore.
        int generation;
        lock (_integrationSync)
        {
            generation = Interlocked.Increment(ref _configurationGeneration);
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            _ = _completionNotifications?.SuspendInteractionsAsync(_openCodeEndpointIdentity);
            _openCodeMonitor?.Dispose(); // Cancel its app-owned SSE immediately; release below awaits drainage.
        }
        PostToApp(() => RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty));
        _pendingNavigation.Clear();
        _navigationSeen.Clear();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token);
        var previous = Interlocked.Exchange(ref _configurationCancellation, cancellation);
        try { previous?.Cancel(); } catch (ObjectDisposedException) { /* Previous configuration already drained. */ }
        _ = ConfigureOpenCodeAsync(settings, generation, cancellation, recoveredOptions, recoveredSsh);
    }

    private async Task ConfigureOpenCodeAsync(AppSettings? settings, int generation, CancellationTokenSource cancellation,
        OpenCodeClientOptions? recoveredOptions = null, SshOpenCodeConnection? recoveredSsh = null)
    {
        bool held = false;
        SshOpenCodeConnection? stagedSsh = recoveredSsh;
        SshRecoveryOwner? sshOwner = null;
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
            if (options.Kind != OpenCodeConnectionKind.Ssh && recoveredOptions is null && options.AuthenticationMode != OpenCodeAuthenticationMode.None)
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
            var clientOptions = recoveredOptions ?? new OpenCodeClientOptions
            {
                // Apply the IPv4 forwarding route to existing saved WSL profiles too; retain
                // the saved endpoint identity so unknown-result tombstones stay in scope.
                BaseUrl = options.Kind == OpenCodeConnectionKind.Wsl
                    && Services.OpenCode.Connection.WslOpenCodeServiceLocator.TryMapUrl(options.BaseUrl, out var wslUrl)
                        ? wslUrl : options.BaseUrl,
                AuthenticationMode = options.AuthenticationMode,
                Username = options.Username, Secret = secret
            };
            if (options.Kind == OpenCodeConnectionKind.Ssh)
            {
                stagedSsh ??= await CreateSshRecovery().RecoverAsync(options,
                    status => PostToApp(() => { if (generation == _configurationGeneration) SetOpenCodeRecoveryStatus(status); }), token).ConfigureAwait(false);
                if (stagedSsh is null) { RecordDiagnostic("opencode-ssh-connect-failed"); return; }
                clientOptions = stagedSsh.ToClientOptions();
                sshOwner = new(settings, generation, CreateSshRecovery(), CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.Token));
                var owner = sshOwner;
                clientOptions = clientOptions with { ConnectionFailure = _ => PostToApp(() => BeginSshRecovery(owner)) };
            }
            var recoveryOwner = CreateWslRecoveryOwner(settings, generation);
            if (recoveryOwner is not null)
                clientOptions = clientOptions with { ConnectionFailure = unreachable =>
                    PostToApp(() => BeginWslRecovery(recoveryOwner, unreachable)) };
            var client = new OpenCodeClient(clientOptions); // Validates options before publishing a page factory.
            // Identity is endpoint-only so credential changes do NOT bypass unknown-result tombstones.
            string endpoint = CreateOpenCodeEndpointIdentity(options);
            var monitor = new OpenCodeNotificationMonitor(_completionNotifications, new(endpoint, client));
            monitor.Observed += OnOpenCodeObserved;
            monitor.SessionObserved += OnOpenCodeSessionObserved;
            lock (_integrationSync)
            {
                if (_closing || token.IsCancellationRequested || generation != _configurationGeneration)
                { recoveryOwner?.Cancellation.Dispose(); monitor.Dispose(); client.Dispose(); return; }
                _openCodeClient = client;
                _wslRecoveryOwner = recoveryOwner;
                _sshRecoveryOwner = sshOwner;
                _sshConnection = stagedSsh;
                stagedSsh = null;
                _openCodeMonitor = monitor;
                _openCodeEndpointIdentity = endpoint;
                _openCodePageSource = new OpenCodePageSource(endpoint, checked(++_openCodeSourceRevision),
                    () => new OpenCodeClient(clientOptions)); // Same immutable options; no second secret read.
                if (_trackedEndpointIdentity != endpoint) _trackedOpenCodeSessions.Clear();
                _trackedEndpointIdentity = endpoint;
                foreach (string session in _trackedOpenCodeSessions) monitor.TrackSession(session);
                _completionNotifications?.ResumeInteractions();
            }
            if (sshOwner is { } liveSshOwner && _sshConnection is { } liveSsh)
            {
                liveSsh.Tunnel.Exited += (_, _) => PostToApp(() => BeginSshRecovery(liveSshOwner));
                // The process may have exited between verification and event subscription.
                if (!liveSsh.Tunnel.IsAlive) PostToApp(() => BeginSshRecovery(liveSshOwner));
                PostToApp(() => { if (OwnsSshRecovery(liveSshOwner)) SetOpenCodeRecoveryStatus(liveSsh.Service.Warning); });
                sshOwner = null; // Published owner now owns its cancellation source.
            }
            bool started = monitor.Start(); // State observation is independent of native notification registration.
            RecordDiagnostic(started ? "opencode-monitor-ready" : "opencode-monitor-notifications-unavailable");
            PostToApp(() =>
            {
                lock (_integrationSync)
                {
                    if (_closing || generation != _configurationGeneration || _openCodePageSource is not { } source
                        || source.IsRevoked || !ReferenceEquals(monitor, _openCodeMonitor)
                        || source.Revision != _openCodeSourceRevision) return;
                    BindSessionUiStore(source);
                    // Seed every retained session before its individual poll; absence is not an Idle result.
                    foreach (var session in _trackedOpenCodeSessions) SeedOpenCodeObservation(source, session);
                }
                PublishSessionUiChanges();
                RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty);
                FlushOpenCodeNavigation();
            });
        }
        catch (OperationCanceledException) { /* Superseded configuration, not a credential or connectivity success. */ }
        catch (Exception ex) { RecordDiagnostic("opencode-configuration-failed", error: ex); }
        finally
        {
            stagedSsh?.Dispose();
            sshOwner?.Cancellation.Dispose();
            if (held) _configurationGate.Release();
            Interlocked.CompareExchange(ref _configurationCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static string CreateOpenCodeEndpointIdentity(OpenCodeSettings settings)
    {
        // SSH's local port changes after recovery/relaunch. Keep unknown-result tombstones on the logical remote
        // profile instead of letting a new ephemeral port bypass them; passwords/remote service ports are excluded.
        string identity = settings.Kind == OpenCodeConnectionKind.Ssh
            ? "ssh\n" + SshConnectionTarget.Parse(settings.SshHost!).DisplayName + "\n" + (settings.SshCommand ?? "opencode")
            : OpenCodeClient.NormalizeBaseUri(settings.BaseUrl!).AbsoluteUri;
        return "ep-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private void StopOpenCodeRequests()
    {
        StopWslRecovery();
        StopSshRecovery();
        _sshConnection?.Dispose();
        OpenCodePageSource? stoppedSource;
        lock (_integrationSync)
        {
            stoppedSource = _openCodePageSource;
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            Interlocked.Increment(ref _configurationGeneration);
            _ = _completionNotifications?.SuspendInteractionsAsync(_openCodeEndpointIdentity);
            _openCodeMonitor?.Dispose();
        }
        PostToApp(() => InvalidateSessionUiStore(stoppedSource));
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
        OpenCodePageSource? releasedSource;
        SshOpenCodeConnection? ssh;
        lock (_integrationSync)
        {
            monitor = _openCodeMonitor; client = _openCodeClient; endpoint = _openCodeEndpointIdentity;
            releasedSource = _openCodePageSource;
            ssh = _sshConnection; _sshConnection = null;
            // Revoke before exposing an empty owner and before any monitor await.
            _openCodePageSource?.Revoke();
            _openCodePageSource = null;
            _ = _completionNotifications?.SuspendInteractionsAsync(endpoint);
            _openCodeMonitor = null; _openCodeClient = null; _openCodeEndpointIdentity = null;
        }
        ssh?.Dispose();
        PostToApp(() =>
        {
            InvalidateSessionUiStore(releasedSource);
            RaiseAppEvent(OpenCodeSourceChanged, EventArgs.Empty);
        });
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
                    monitor.SessionObserved -= OnOpenCodeSessionObserved;
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
