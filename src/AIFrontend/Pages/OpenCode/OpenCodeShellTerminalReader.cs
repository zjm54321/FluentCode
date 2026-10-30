using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeShellTerminalReader
{
    internal static async Task ReadAsync(Func<Task<OpenCodeTreeShellInfo>> readInfo, Func<long, Task<OpenCodeShellOutput>> readOutput,
        OpenCodeShellTerminalState state, Func<bool> current, Func<Task> render, CancellationToken ct)
    {
        // /shell/{id} may already be collected while its output endpoint still retains bytes.
        // Metadata 404 is therefore not evidence that command output has disappeared.
        try
        {
            var info = await readInfo();
            if (!current()) throw new OperationCanceledException(ct);
            state.ApplyInfo(info);
        }
        catch (OpenCodeApiException error) when (error.StatusCode == 404) { }
        do
        {
            ct.ThrowIfCancellationRequested();
            if (!current()) throw new OperationCanceledException(ct);
            var output = await readOutput(state.Cursor);
            if (!current()) throw new OperationCanceledException(ct);
            state.ApplyOutput(output);
            await render();
        } while (state.HasMore);
    }
}
