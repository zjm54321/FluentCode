// Only UI/native scheduling and unrelated App ownership are substituted. Actual App observation,
// expiry, gate, ACK and diagnostic dispatch methods compile from production partials.
using System;
using System.Collections.Generic;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.Taskbar;

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler<object>? Tick;
        public void Start() { }
        public void Stop() { }
        public void Fire() => Tick?.Invoke(this, new());
    }
    public sealed class DispatcherQueue
    {
        public DispatcherQueueTimer CreateTimer() => new();
    }
}

namespace AIFrontend
{
    public partial class App
    {
        private readonly object _integrationSync = new();
        private bool _closing = false;
        private OpenCodeNotificationMonitor? _openCodeMonitor;
        private CompletionNotificationService? _completionNotifications;
        internal void SetNotificationService(CompletionNotificationService service) => _completionNotifications = service;
        private OpenCodePageSource? _openCodePageSource;
        private long _openCodeSourceRevision;
        private string? _openCodeEndpointIdentity;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _appDispatcher = new();
        private readonly TaskbarStateAggregator _taskbarState = new();
        private readonly Queue<AppIntegrationDiagnostic> _integrationDiagnostics = new();
        private readonly Queue<Action> _queue = new();
        public event EventHandler<AppIntegrationDiagnosticEventArgs>? IntegrationDiagnosticChanged;
        public event EventHandler<NotificationInteractionEventArgs>? OpenCodeInteractionProcessed;
        public OpenCodePageSource? CurrentOpenCodePageSource => _openCodePageSource;
        internal TaskbarSnapshot Snapshot => _taskbarState.Snapshot;
        internal void SetSource(OpenCodePageSource source, OpenCodeNotificationMonitor monitor)
        {
            _openCodePageSource?.Revoke();
            ResetOpenCodeObservations();
            _openCodePageSource = source; _openCodeSourceRevision = source.Revision;
            _openCodeMonitor = monitor; _openCodeEndpointIdentity = source.Identity;
            BindSessionUiStore(source);
            TrackOpenCodeSession("ses_test");
        }
        public void TrackOpenCodeSession(string session)
            => TrackOpenCodeSessionCore(session, true);
        private void TrackOpenCodeSessionCore(string session, bool publishUi)
        {
            _openCodeMonitor!.TrackSession(session);
            SeedOpenCodeObservation(_openCodePageSource!, session);
            if (publishUi) PublishSessionUiChanges();
        }
        private static void RequireAppThread() { }
        private void PostToApp(Action action) { lock (_queue) _queue.Enqueue(action); }
        internal void DrainOne()
        {
            Action? action; lock (_queue) action = _queue.Count == 0 ? null : _queue.Dequeue();
            if (!_closing) action?.Invoke();
        }
        internal void Drain() { while (_queue.Count != 0) DrainOne(); }
        private TaskbarOperationResult ApplyTaskbarAggregate() => new(TaskbarOperationStatus.NotInitialized, "TestOnly");
        private void RaiseAppEvent<T>(EventHandler<T>? handlers, T args) where T : EventArgs => handlers?.Invoke(this, args);
        internal void Observe(OpenCodeNotificationMonitor monitor, OpenCodeSessionObservation observation)
            => OnOpenCodeSessionObserved(monitor, new(observation));
        internal void ObserveDiagnostic(OpenCodeNotificationMonitor monitor, OpenCodeNotificationObservation observation)
            => OnOpenCodeObserved(monitor, new(observation));
        internal void Tick() => _sessionObservationTimer!.Fire();
    }
}
