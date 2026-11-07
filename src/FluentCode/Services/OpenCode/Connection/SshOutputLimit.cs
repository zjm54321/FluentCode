using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Connection;

internal sealed class SshOutputLimit(Action overflow)
{
    internal const int CommandBytes = 64 * 1024;
    internal const int DiagnosticTailBytes = 16 * 1024;
    private int _total;
    internal void Add(int count)
    {
        if (Interlocked.Add(ref _total, count) <= CommandBytes) return;
        overflow();
        throw new SshOpenCodeException(SshFailureKind.OutputLimit);
    }
    internal static async Task<string> ReadAsync(Stream stream, SshOutputLimit limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            limit.Add(count);
            output.Write(buffer, 0, count);
        }
    }
    internal static async Task<string> ReadTailAsync(Stream stream, Action overflow, CancellationToken ct = default)
    {
        byte[] tail = new byte[DiagnosticTailBytes], buffer = new byte[4096];
        int length = 0;
        // A sliding window retains at most 16 KiB; more than 64 KiB per read window is an output flood.
        int windowBytes = 0;
        long window = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            int count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) return Encoding.UTF8.GetString(tail, 0, length);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(window) >= TimeSpan.FromSeconds(1)) { window = System.Diagnostics.Stopwatch.GetTimestamp(); windowBytes = 0; }
            windowBytes += count;
            if (windowBytes > CommandBytes) { overflow(); throw new SshOpenCodeException(SshFailureKind.OutputLimit); }
            int keep = Math.Min(length, tail.Length - count);
            if (keep > 0) Buffer.BlockCopy(tail, length - keep, tail, 0, keep);
            Buffer.BlockCopy(buffer, 0, tail, keep, count);
            length = keep + count;
        }
    }
}
