using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace AIFrontend.Services.Protocols;

internal sealed record SseFrame(string Event, string Data);

internal static class SseReader
{
    internal static async IAsyncEnumerable<SseFrame> ReadAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var data = new StringBuilder();
        var eventName = "message";
        var hasData = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 1024 * 1024) throw new InvalidDataException("SSE line limit exceeded.");
            if (line.Length == 0)
            {
                if (hasData) yield return new SseFrame(eventName, data.ToString());
                data.Clear();
                eventName = "message";
                hasData = false;
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") eventName = value;
            if (field != "data") continue;
            if (hasData) data.Append('\n');
            data.Append(value);
            hasData = true;
            if (data.Length > 4 * 1024 * 1024) throw new InvalidDataException("SSE event limit exceeded.");
        }
        // An unfinished event at EOF is not dispatched. No reconnection or POST replay.
    }
}
