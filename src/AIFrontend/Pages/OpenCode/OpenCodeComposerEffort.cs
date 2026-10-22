using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal sealed record OpenCodeEffortTick(string Id, double Angle, bool Selected);

internal static class OpenCodeComposerEffort
{
    // Catalog order and raw IDs are authoritative. Default/null is not an invented extra level.
    internal static IReadOnlyList<OpenCodeEffortTick> Ticks(OpenCodeModelInfo? model, string? current)
    {
        var ids = model?.Enabled == false ? Array.Empty<string>() : model?.Variants?
            .Select(v => v.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        return ids.Select((id, i) => new OpenCodeEffortTick(id,
            ids.Length == 1 ? -90 : -160 + 140d * i / (ids.Length - 1), id == current)).ToArray();
    }
}
