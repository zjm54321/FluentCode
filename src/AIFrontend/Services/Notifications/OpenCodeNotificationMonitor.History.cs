using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

internal sealed record OpenCodeMessageHistoryResult(List<OpenCodeMessage> Messages, bool Complete);

/// <summary>next is an opaque message anchor, not a short-page heuristic. No cursor+order requests.</summary>
internal static class OpenCodeMessageHistory
{
    internal static async Task<OpenCodeMessageHistoryResult> ReadAsync(
        Func<OpenCodeMessageQuery, CancellationToken, Task<OpenCodeSessionMessagesResponse>> read,
        int pageSize, int pageBudget, CancellationToken token)
    {
        var messages = new List<OpenCodeMessage>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        bool complete = false;
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
            if (page.Data.Count == 0 || cursor is null) { complete = true; break; }
            if (!cursors.Add(cursor!)) break;
        }
        messages.Reverse();
        return new(messages, complete);
    }

    private const int MaxCursorLength = 4096;
    private static bool IsValidCursor(string cursor)
        => cursor.Length > 0 && cursor.Length <= MaxCursorLength && !cursor.Any(char.IsControl);
}
