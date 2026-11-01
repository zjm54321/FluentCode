using System;
using FluentCode.Services.Notifications;

namespace FluentCode.Services.OpenCode;

/// <summary>Capture before confirmation; canStart must recheck this exact source and view binding.</summary>
public sealed record PluginMutationOrigin(OpenCodePageSource Source, long Revision,
    string SessionId, string Directory, long OwnerGeneration);

public enum PluginMutationStatus { NotStarted, Rejected, Accepted, OutcomeUnknown }

/// <summary>OperationId is process-local correlation only, never an RPC input or idempotency key.</summary>
public sealed record PluginMutationResult(PluginMutationStatus Status, bool RequestStarted,
    bool ProtectionRetained, OpenCodeSessionPresetState? Preset, ClearEncryptedResult? Clear,
    string? FailureCode, string? SafeReason, Guid OperationId = default);

/// <summary>Local, queryable acknowledgement metadata. This is not remote status or proof of success.</summary>
public sealed record PluginMutationProtection(Guid OperationId, SessionControlKind Kind, PluginMutationResult Outcome);
