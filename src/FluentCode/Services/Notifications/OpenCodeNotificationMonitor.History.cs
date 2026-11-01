using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;

namespace FluentCode.Services.Notifications;

internal sealed record OpenCodeMessageHistoryResult(List<OpenCodeMessage> Messages, bool Complete, bool AnchorFound = false);

/// <summary>next is an opaque message anchor, not a short-page heuristic. No cursor+order requests.</summary>
internal static class OpenCodeMessageHistory
{
    internal static async Task<OpenCodeMessageHistoryResult> ReadAsync(
        Func<OpenCodeMessageQuery, CancellationToken, Task<OpenCodeSessionMessagesResponse>> read,
        int pageSize, int pageBudget, CancellationToken token, string? anchor = null)
    {
        var messages = new List<OpenCodeMessage>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        bool complete = false;
        bool anchorFound = false;
        for (int pageIndex = 0; pageIndex < pageBudget; pageIndex++)
        {
            var page = await read(new() { Limit = pageSize, Cursor = cursor,
                Order = cursor is null ? OpenCodeSessionOrders.Desc : null }, token).ConfigureAwait(false);
            if (page.Data is null || page.Data.Count > pageSize) break;
            bool valid = true;
            foreach (var message in page.Data)
            {
                if (message is null || !NotificationArgumentCodec.IsValidRequestId(message.Id) || !ids.Add(message.Id!))
                { valid = false; break; }
                messages.Add(message);
            }
            if (!valid) break;
            cursor = page.Cursor?.Next;
            if (cursor is not null && !IsValidCursor(cursor)) break;
            anchorFound = anchor is not null && ids.Contains(anchor);
            if (page.Data.Count == 0 || cursor is null) { complete = true; break; }
            if (anchorFound) break; // Newest pages cover the entire delta; older history is already retained.
            if (!cursors.Add(cursor!)) break;
        }
        messages.Reverse();
        return new(messages, complete, anchorFound);
    }

    private const int MaxCursorLength = 4096;
    private static bool IsValidCursor(string cursor)
        => cursor.Length > 0 && cursor.Length <= MaxCursorLength && !cursor.Any(char.IsControl);
}
