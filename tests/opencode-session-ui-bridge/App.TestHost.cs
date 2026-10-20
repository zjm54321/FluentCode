// Only configuration assignment, dispatcher/native facades, and observation inputs are substituted.
// Mapping, gates, owner fences, publication, expiry and ACK compile from production partials.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly Queue<Action> queue = new();
        public bool HasThreadAccess => true;
        public DispatcherQueueTimer CreateTimer() => new();
        public bool TryEnqueue(Action action) { lock (queue) queue.Enqueue(action); return true; }
        public bool DrainOne()
        {
            Action action;
            lock (queue) { if (queue.Count == 0) return false; action = queue.Dequeue(); }
            action(); return true;
        }
    }
}

namespace AIFrontend
{
    public partial class App
    {
        private readonly object _integrationSync = new();
        private bool _closing = false;
        private int _configurationGeneration;
        private OpenCodeNotificationMonitor? _openCodeMonitor;
        private AIFrontend.Services.OpenCode.OpenCodeClient? _openCodeClient = null;
        private OpenCodePageSource? _openCodePageSource;
        private long _openCodeSourceRevision;
        private string? _openCodeEndpointIdentity;
        private readonly CompletionNotificationService? _completionNotifications = null;
        private readonly HashSet<string> _trackedOpenCodeSessions = new(StringComparer.Ordinal) { "ses_test" };
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _appDispatcher = new();
        private readonly TaskbarStateAggregator _taskbarState = new();
        private readonly Queue<AppIntegrationDiagnostic> _integrationDiagnostics = new();
        public event EventHandler<AppIntegrationDiagnosticEventArgs>? IntegrationDiagnosticChanged;
        public event EventHandler<NotificationInteractionEventArgs>? OpenCodeInteractionProcessed;
        public event EventHandler? OpenCodeSourceChanged;
        internal bool LockHeld => Monitor.IsEntered(_integrationSync);
        internal TaskbarSnapshot Taskbar => _taskbarState.Snapshot;
        internal int CallbackErrors => _integrationDiagnostics.Count(x => x.Code == "app-callback-failed");

        internal void Install(OpenCodePageSource source, OpenCodeNotificationMonitor monitor)
        {
            // Configuration/network construction is excluded; execute the verbatim production
            // dispatcher binding body after assigning an injected, credential-free source.
            lock (_integrationSync)
            {
                _openCodePageSource = source;
                _openCodeSourceRevision = source.Revision;
                _openCodeMonitor = monitor;
                _openCodeEndpointIdentity = source.Identity;
                _configurationGeneration++;
                foreach (var session in _trackedOpenCodeSessions) monitor.TrackSession(session);
            }
            monitor.SessionObserved += OnOpenCodeSessionObserved;
            BridgePublishBinding(_configurationGeneration, monitor);
        }
        internal Task Release() => ReleaseOpenCodeSourceAsync();
        internal void Reset() => ResetOpenCodeObservations();
        internal void CloseForTest() => _closing = true;
        internal bool DrainOne() => _appDispatcher.DrainOne();
        internal void Drain() { while (DrainOne()) { } }
        internal void Observe(OpenCodeNotificationMonitor monitor, OpenCodeSessionObservation observation)
            => OnOpenCodeSessionObserved(monitor, new(observation));
        internal void Tick() => _sessionObservationTimer!.Fire();
        private void FlushOpenCodeNavigation() { }
        private TaskbarOperationResult ApplyTaskbarAggregate() => new(TaskbarOperationStatus.NotInitialized, "OfflineHost");
    }
}
