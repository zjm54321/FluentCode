using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeComposerAttachmentLoader
{
    internal static async Task<IReadOnlyList<OpenCodeAttachmentDraft>> ReadAsync(
        IReadOnlyList<OpenCodeComposerFileSource> sources, int existingCount, long existingBytes,
        Func<bool> ownsTicket, CancellationToken ct)
    {
        if (sources.Count + existingCount > OpenCodeAttachment.MaxFiles) throw new ArgumentException("Attachment limit.");
        var staged = new List<OpenCodeAttachmentDraft>();
        long total = existingBytes;
        foreach (var source in sources)
        {
            Check();
            if (source is null) throw new ArgumentException("Invalid file source.");
            // No path, length hint or data URL is trusted. The opener is invoked only while owned.
            await using var stream = await source.OpenReadAsync(ct).ConfigureAwait(false);
            Check();
            using var bytes = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                Check();
                // Read at most the remaining budget plus one sentinel byte.
                var remaining = Math.Min(OpenCodeAttachment.MaxFileBytes - bytes.Length,
                    OpenCodeAttachment.MaxTotalBytes - total);
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining + 1)), ct).ConfigureAwait(false);
                Check();
                if (count == 0) break;
                if (count > remaining) throw new ArgumentException("Attachment limit.");
                bytes.Write(buffer, 0, count);
                total += count;
            }
            staged.Add(new(source.DisplayName, source.Mime, bytes.ToArray()));
        }
        Check();
        return staged.AsReadOnly();

        void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (!ownsTicket()) throw new OperationCanceledException(ct);
        }
    }
}
