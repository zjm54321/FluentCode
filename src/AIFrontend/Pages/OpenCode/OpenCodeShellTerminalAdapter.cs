using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Read-only shell output route. No socket, local process, input, or resize endpoint.</summary>
internal sealed class OpenCodeShellTerminalAdapter(IOpenCodePersistentPtySurface surface, OpenCodePersistentPtyViewContext context)
{
    private string _rendered = string.Empty;
    private bool _initialized;

    internal async Task RenderAsync(string output, CancellationToken ct)
    {
        if (!_initialized)
        {
            if (!await surface.InitializeAsync(context, 120, 32, ct)) throw new InvalidOperationException("原生终端不可用。");
            _initialized = true;
        }
        // /shell output is a text-log route: LF needs CRLF for terminal rendering. Keep ANSI intact.
        output = output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
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
