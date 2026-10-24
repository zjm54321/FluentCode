using System;
using System.Collections.Generic;
using System.Linq;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeModelPickerFavorites
{
    internal static OpenCodeModelPickerFavorites Shared { get; } = new();
    private readonly HashSet<(string ProviderId, string ModelId)> _keys = new();

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
