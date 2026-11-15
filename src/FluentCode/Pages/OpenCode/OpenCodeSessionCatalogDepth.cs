using System;
using System.Collections.Generic;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// How many list pages a refresh of the session catalog reads. The catalog normally holds what the list pages
/// returned, so its size says how deep the user has loaded. A row that is there only because a title-bar tab asked
/// for its session by id is not loaded depth: counting it would make every refresh read one page more until the
/// list reached that session. Such rows are remembered here and left out of the count; a refresh still re-reads
/// them by id. Once a list page contains the session it is an ordinary row again. UI thread only.
/// </summary>
public sealed class OpenCodeSessionCatalogDepth
{
    private readonly HashSet<string> _askedById = new(StringComparer.Ordinal);

    /// <summary>The session entered the catalog through a request for its id, not through a list page.</summary>
    public void AskedById(string sessionId) => _askedById.Add(sessionId);

    /// <summary>A list page returned the session.</summary>
    public void Listed(string sessionId) => _askedById.Remove(sessionId);

    public bool IsAskedById(string sessionId) => _askedById.Contains(sessionId);

    /// <summary>The catalog was emptied (another connection, or the page let go).</summary>
    public void Clear() => _askedById.Clear();

    /// <summary>Pages to read for a catalog that currently holds <paramref name="catalogIds"/>; never fewer than one.</summary>
    public int PagesToRead(IEnumerable<string> catalogIds, int pageSize)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var listed = 0;
        foreach (var id in catalogIds)
            if (!_askedById.Contains(id)) listed++;
        return Math.Max(1, (listed + pageSize - 1) / pageSize);
    }
}
