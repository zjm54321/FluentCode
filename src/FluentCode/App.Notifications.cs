using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace FluentCode;

public partial class App
{
    private volatile CompletionNotificationService? _completionNotifications;
    private readonly Queue<NotificationNavigationTarget> _pendingNavigation = new();
    private readonly HashSet<NotificationNavigationTarget> _navigationSeen = new();
    private bool _navigationReady;
    private bool _allowWindowClose;

    public OpenCodeInteractionGate InteractionGate => OpenCodeInteractionGate.Shared;
    public event EventHandler<NotificationNavigationEventArgs>? OpenCodeNavigationRequested;
    public event EventHandler<NotificationInteractionEventArgs>? OpenCodeInteractionProcessed;

    /// <summary>UI thread: subscribe first, then mark ready. False during page/window navigation teardown.</summary>
    public void SetOpenCodeNavigationReady(bool ready)
    {
        RequireAppThread();
        if (_closing) return;
        _navigationReady = ready;
        FlushOpenCodeNavigation();
    }

    private void StartCompletionNotifications(MainWindow window)
    {
        if (_closing || _completionNotifications is not null) return;
        var service = new CompletionNotificationService();
        _completionNotifications = service;
        service.NavigationRequested += OnOpenCodeNavigation;
        service.InteractionProcessed += OnOpenCodeInteraction;
        // The service itself subscribes NotificationInvoked BEFORE Register. Do not defer registration
        // past AppInstance.GetActivatedEventArgs: that would misclassify cold notification activation.
        RecordDiagnostic(service.Initialize() ? "notifications-initialized" : "notifications-unavailable");
    }

    private void OnOpenCodeNavigation(object? sender, NotificationNavigationEventArgs args)
    {
        int generation = System.Threading.Volatile.Read(ref _configurationGeneration);
        PostToApp(() => { if (generation == _configurationGeneration) QueueOpenCodeNavigation(args.Target); });
    }

    private void QueueOpenCodeNavigation(NotificationNavigationTarget target)
    {
        if (_closing || target.Source != NotificationSource.OpenCode) return;
        if (OpenCodeEndpointIdentity is { } endpoint && endpoint != target.EndpointIdentity)
        { RecordDiagnostic("navigation-stale-endpoint"); return; }
        ShowMainWindow(); // A toast click wakes the UI even while navigation waits for source readiness.
        if (!_navigationSeen.Add(target)) return;
        if (_pendingNavigation.Count >= 64)
        {
            _navigationSeen.Remove(_pendingNavigation.Dequeue());
            RecordDiagnostic("navigation-queue-overflow");
        }
        _pendingNavigation.Enqueue(target);
        FlushOpenCodeNavigation();
    }

    private void FlushOpenCodeNavigation()
    {
        if (_closing || !_navigationReady || OpenCodeEndpointIdentity is null || OpenCodeNavigationRequested is null) return;
        while (_pendingNavigation.TryDequeue(out var target))
        {
            try
            {
                if (target.EndpointIdentity != OpenCodeEndpointIdentity) { RecordDiagnostic("navigation-stale-endpoint"); continue; }
                ShowMainWindow();
                RaiseAppEvent(OpenCodeNavigationRequested, new NotificationNavigationEventArgs(target));
            }
            finally { _navigationSeen.Remove(target); } // Queued/in-flight only; later clicks must navigate again.
        }
    }

    private void OnOpenCodeInteraction(object? sender, NotificationInteractionEventArgs args) => PostToApp(() =>
    {
        RecordDiagnostic("opencode-interaction", interaction: args.Result);
        if (args.Result.EndpointIdentity is { } endpoint && endpoint != OpenCodeEndpointIdentity) return;
        RaiseAppEvent(OpenCodeInteractionProcessed, args);
    });

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose) return;
        args.Cancel = true; // Keep the message loop alive while the bounded HTTP/monitor cancellation drains.
        if (_closing || _window is not MainWindow window) return;
        if (ShouldHideWindowOnClose())
        {
            window.HideToBackground();
            return;
        }
        await CloseWindowAfterShutdownAsync(window);
    }

    private async Task CloseWindowAfterShutdownAsync(MainWindow window)
    {
        if (_closing) return;
        BeginWindowShutdown(window);
        try { await ShutdownCompletion; }
        finally
        {
            _allowWindowClose = true;
            // Closing the already-owned window is the only permitted action after shutdown starts.
            // Never use PostToApp here: that helper intentionally rejects all post-close callbacks.
            if (!window.DispatcherQueue.TryEnqueue(() => window.Close()))
                RecordDiagnostic("window-close-dispatch-unavailable");
        }
    }

    private void OnNotificationWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not MainWindow window || !ReferenceEquals(_window, window)) return;
        window.Closed -= OnNotificationWindowClosed;
        window.AppWindow.Closing -= OnAppWindowClosing;
        if (!_closing) BeginWindowShutdown(window); // Fallback for a platform close without Closing.
        _window = null;
    }

    private void BeginWindowShutdown(MainWindow window)
    {
        if (!_backgroundLifetime.TryBeginShutdown()) return;
        _closing = true;
        ShutdownBackgroundMode();
        _navigationReady = false;
        _pendingNavigation.Clear();
        _navigationSeen.Clear();
        StopActivationOwnership();
        StopOpenCodeRequests();
        RecordDiagnostic("taskbar-shutdown", taskbar: ShutdownTaskbar());
        ShutdownCompletion = DrainApplicationAsync(); // Never block the HWND STA.
    }

    public Task ShutdownCompletion { get; private set; } = Task.CompletedTask;

    private async Task DrainApplicationAsync()
    {
        try
        {
            if (_activationPipe is { } pipe)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _activationPipe = null;
            }
            await DrainOpenCodeOwnershipAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { RecordDiagnostic("opencode-shutdown-failed", error: ex); }
        finally
        {
            var service = _completionNotifications;
            _completionNotifications = null;
            if (service is not null)
            {
                service.NavigationRequested -= OnOpenCodeNavigation;
                service.InteractionProcessed -= OnOpenCodeInteraction;
                await service.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
