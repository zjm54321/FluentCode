// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;

namespace FluentCode.Services.NativeUi;

internal sealed record NativeUiSnapshot(IReadOnlyDictionary<string, object?> State, IReadOnlyDictionary<string, object?> Fields);

// App-run state only. Owns plain values, never views, timers, event handlers or host callbacks.
// Part IDs are globally unique; source equality prevents state from an edited tool input leaking forward.
internal static class NativeUiStateStore
{
    private sealed record Entry(string Source, NativeUiSnapshot Snapshot);
    private static readonly Dictionary<string, Entry> Cards = new(System.StringComparer.Ordinal);
    internal static void Save(string id, string source, NativeUiSnapshot snapshot) => Cards[id] = new(source, snapshot);
    internal static NativeUiSnapshot? Read(string id, string source) => Cards.TryGetValue(id, out var entry) && entry.Source == source ? entry.Snapshot : null;
}
