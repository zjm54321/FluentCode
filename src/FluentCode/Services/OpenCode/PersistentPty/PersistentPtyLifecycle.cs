using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Notifications;

namespace FluentCode.Services.OpenCode;

public sealed record PersistentPtyOrigin(OpenCodePageSource Source, long Revision,
    string SessionId, string Directory, long OwnerGeneration);

public readonly record struct PersistentPtyEpoch(long Value);
public enum PersistentPtyPhase { Connecting, Replaying, Ready, Disconnected, Unsupported, Unavailable, Closed }
public sealed record PersistentPtyState(PersistentPtyEpoch Epoch, PersistentPtyPhase Phase,
    PersistentPtyRole Role, bool InputArmed, bool InputOutcomeUncertain, PersistentPtyInfo? Info, string? FailureCode);

/// <summary>Complete only after the emulator consumes the bytes; adapters own UI dispatch.</summary>
public interface IPersistentPtyRenderer
{
    ValueTask ResetAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken cancellationToken);
    ValueTask WriteAsync(PersistentPtyEpoch epoch, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask ResizeAsync(PersistentPtyEpoch epoch, PersistentPtySize size, ReadOnlyMemory<byte> checkpoint, CancellationToken cancellationToken);
}
