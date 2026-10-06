using System;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

/// <summary>A trusted, immutable runtime binding. The caller owns the client; never put credentials in Identity.</summary>
public sealed class TrustedOpenCodeNotificationSource
{
    public TrustedOpenCodeNotificationSource(string identity, OpenCodeClient client)
    {
        if (!NotificationArgumentCodec.IsValidRequestId(identity))
            throw new ArgumentException("Use a stable opaque endpoint identity, not a URL or credential.", nameof(identity));
        ArgumentNullException.ThrowIfNull(client);
        Identity = identity;
        Client = client;
    }

    public string Identity { get; }
    internal OpenCodeClient Client { get; }
}

public enum NotificationInteractionStatus
{
    Prepared, Submitted, Succeeded, Navigate, InvalidInput, Duplicate, Expired,
    NeedsApp, Unavailable, Cancelled, Failed, OutcomeUnknown
}

/// <summary>Submitted means handed to Windows, not delivered or authorized. Only Succeeded means HTTP 204.</summary>
public sealed record NotificationInteractionResult(
    NotificationInteractionStatus Status,
    string Detail,
    string? EndpointIdentity = null,
    string? SessionId = null,
    string? RequestId = null,
    int? HttpStatusCode = null,
    NotificationNavigationKind? Kind = null)
{
    public NotificationNavigationTarget? NavigationTarget =>
        EndpointIdentity is not null && SessionId is not null && RequestId is not null && Kind is { } kind
            ? new(NotificationSource.OpenCode, EndpointIdentity, SessionId, RequestId, kind) : null;
}

public enum NotificationNavigationKind { Completion, Permission, Form }

/// <summary>Trusted runtime routing only; never infer Kind from an ID or treat navigation as authorization.</summary>
public sealed record NotificationNavigationTarget(NotificationSource Source, string EndpointIdentity,
    string SessionId, string RequestId, NotificationNavigationKind Kind);

public sealed class NotificationNavigationEventArgs(NotificationNavigationTarget target) : EventArgs
{
    public NotificationNavigationTarget Target { get; } = target;
}

/// <summary>Raised on a background thread, with no raw arguments, credentials, answer text or exception message.</summary>
public sealed class NotificationInteractionEventArgs(NotificationInteractionResult result) : EventArgs
{
    public NotificationInteractionResult Result { get; } = result;
}
