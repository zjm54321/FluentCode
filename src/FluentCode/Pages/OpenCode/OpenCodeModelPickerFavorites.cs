using System;
using System.Collections.Generic;
using System.Linq;

namespace FluentCode.Pages.OpenCode;

internal sealed class OpenCodeModelPickerFavorites
{
    private readonly HashSet<(string ProviderId, string ModelId)> _keys = new();

    internal IEnumerable<(string ProviderId, string ModelId)> Keys => _keys;

    internal void Replace(IEnumerable<(string ProviderId, string ModelId)> keys)
    {
        _keys.Clear();
        foreach (var key in keys) _keys.Add(key);
    }

    internal bool Contains(OpenCodeModelPickerRow row) => _keys.Contains((row.ProviderId, row.ModelId));

    internal bool Toggle(OpenCodeModelPickerRow row)
    {
        var key = (row.ProviderId, row.ModelId);
        if (_keys.Add(key)) return true;
        _keys.Remove(key);
        return false;
    }

    internal static bool Matches(OpenCodeModelPickerRow row, string filter) => filter.Length == 0
        || row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || row.ModelId.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (row.ProviderName ?? row.ProviderId).Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    internal (OpenCodeModelPickerRow[] Favorites, OpenCodeModelPickerRow[] All) Filter(
        IEnumerable<OpenCodeModelPickerRow> rows, string filter)
    {
        var all = rows.Where(row => Matches(row, filter)).ToArray();
        return (all.Where(Contains).ToArray(), all);
    }
}
