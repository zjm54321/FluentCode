using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Pages.OpenCode;

/// <summary>Read-only shell output route. No socket, local process, input, or resize endpoint.</summary>
internal sealed class OpenCodeShellTerminalAdapter(IOpenCodePersistentPtySurface surface, OpenCodePersistentPtyViewContext context)
{
    private string _rendered = string.Empty;
    private bool _initialized;
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    internal async Task RenderAsync(string output, CancellationToken ct, string? command = null)
    {
        await _renderGate.WaitAsync(ct);
        try { await RenderCoreAsync(output, ct, command); }
        finally { _renderGate.Release(); }
    }

    private async Task RenderCoreAsync(string output, CancellationToken ct, string? command)
    {
        if (!_initialized)
        {
            if (!await surface.InitializeAsync(context, 120, 32, ct)) throw new InvalidOperationException("原生终端不可用。");
            _initialized = true;
        }
        // /shell output is a text-log route: LF needs CRLF for terminal rendering. Keep ANSI intact.
        output = OpenCodeShellTerminalText.HideCursor
            + (string.IsNullOrEmpty(command) ? string.Empty : "\x1b[36m$ " + OpenCodeShellTerminalText.PlainText(command).ReplaceLineEndings(" ") + "\x1b[0m\r\n")
            + OpenCodeShellTerminalText.ReadOnlyVt(OpenCodeShellTerminalText.VtLog(output));
        var appended = output.StartsWith(_rendered, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(appended ? output[_rendered.Length..] : output);
        if (!appended) await surface.ResetAsync(context, ReadOnlyMemory<byte>.Empty, 120, 32, ct);
        for (var offset = 0; offset < bytes.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(1048576, bytes.Length - offset);
            await surface.WriteAsync(context, bytes.AsMemory(offset, length), ct);
            offset += length;
        }
        _rendered = output;
    }
}
