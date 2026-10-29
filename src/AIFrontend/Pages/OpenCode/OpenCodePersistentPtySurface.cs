using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Pages.OpenCode;

internal interface IOpenCodePersistentPtySurface : IDisposable
{
    Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct);
    Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct);
    Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct);
}
