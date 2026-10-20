using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

// Temporary opt-in origin diagnostics. Call sites supply fixed phase names and scalar state only.
// No exception handling or completion behavior changes; writes are flushed before the call returns.
internal static class OpenCodeCompletionCrashTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("AIF_COMPLETION_TRACE") == "1";
    private static readonly object Gate = new();

    internal static void Mark(string phase, string? state = null)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "logs");
                Directory.CreateDirectory(directory);
                byte[] record = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    utc = DateTimeOffset.UtcNow,
                    pid = Environment.ProcessId,
                    thread = Environment.CurrentManagedThreadId,
                    phase,
                    state,
                }) + "\n");
                using var stream = new FileStream(Path.Combine(directory, $"completion-phases-{Environment.ProcessId}.jsonl"),
                    FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                stream.Write(record);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) { } // Diagnostic file failures must not replace the originating exception.
            catch (UnauthorizedAccessException) { }
        }
    }
}
