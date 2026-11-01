using System;
using FluentCode.Services.Notifications;
using FluentCode.Services.Taskbar;

namespace FluentCode;

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
    private bool IsCurrentOpenCodeCallback(object? monitor, OpenCodePageSource? source, long revision)
    {
        lock (_integrationSync)
            return !_closing && ReferenceEquals(monitor, _openCodeMonitor)
                && ReferenceEquals(source, _openCodePageSource) && revision == _openCodeSourceRevision
                && source?.IsRevoked != true;
    }

    private void OnOpenCodeObserved(object? sender, OpenCodeNotificationObservationEventArgs args)
    {
        OpenCodePageSource? source;
        long revision;
        lock (_integrationSync)
        {
            source = _openCodePageSource;
            revision = _openCodeSourceRevision;
            if (source is null || !IsCurrentOpenCodeCallback(sender, source, revision)) return;
        }
        PostToApp(() =>
        {
            if (!IsCurrentOpenCodeCallback(sender, source, revision)) return;
            if (args.Observation.EndpointIdentity != source.Identity) return;
            RecordDiagnostic("opencode-observation", observation: args.Observation);
            if (args.Observation.Interaction is not { } result) return;
            if (result.EndpointIdentity is { } endpoint && endpoint != source.Identity) return;
            RecordDiagnostic("opencode-interaction", interaction: result);
            RaiseAppEvent(OpenCodeInteractionProcessed, new NotificationInteractionEventArgs(result));
        });
    }

    private void RecordDiagnostic(string code, Exception? error = null, NotificationInteractionResult? interaction = null,
        OpenCodeNotificationObservation? observation = null, TaskbarOperationResult? taskbar = null)
    {
        AppIntegrationDiagnostic diagnostic;
        OpenCodeNotificationMonitor? monitor;
        OpenCodePageSource? source;
        long revision;
        lock (_integrationSync)
        {
            monitor = _openCodeMonitor;
            source = _openCodePageSource;
            revision = _openCodeSourceRevision;
            diagnostic = new(DateTimeOffset.UtcNow, code, observation?.EndpointIdentity ?? interaction?.EndpointIdentity ?? _openCodeEndpointIdentity,
                error?.GetType().FullName, error?.HResult, interaction, observation, taskbar);
            if (_integrationDiagnostics.Count == 128) _integrationDiagnostics.Dequeue();
            _integrationDiagnostics.Enqueue(diagnostic);
        }
        PostToApp(() =>
        {
            if (!IsCurrentOpenCodeCallback(monitor, source, revision)) return;
            RaiseAppEvent(IntegrationDiagnosticChanged, new AppIntegrationDiagnosticEventArgs(diagnostic));
        });
    }
}
