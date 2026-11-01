using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

internal sealed record OpenCodeEffortTick(string Id, double Angle, bool Selected);

internal static class OpenCodeComposerEffort
{
    // Fallback for historical turn chips without a current model catalog, never used to invent picker options.
    internal static IReadOnlyList<OpenCodeEffortTick> SemanticTicks(string? current)
    {
        var key = current?.ToLowerInvariant() switch
        {
            "none" => "minimal",
            "extra high" or "extra_high" or "extra-high" => "xhigh",
            var value => value,
        };
        var ids = new[] { "minimal", "low", "medium", "high", "xhigh", "max" };
        if (key is not null && !ids.Contains(key)) return new[] { new OpenCodeEffortTick(current!, -90, true) };
        return ids.Select((id, i) => new OpenCodeEffortTick(id, -160 + 140d * i / (ids.Length - 1), id == key)).ToArray();
    }
    // Catalog order and raw IDs are authoritative. Default/null is not an invented extra level.
    internal static IReadOnlyList<OpenCodeEffortTick> Ticks(OpenCodeModelInfo? model, string? current)
    {
        var ids = model?.Enabled == false ? Array.Empty<string>() : model?.Variants?
            .Select(v => v.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
        return ids.Select((id, i) => new OpenCodeEffortTick(id,
            ids.Length == 1 ? -90 : -160 + 140d * i / (ids.Length - 1), id == current)).ToArray();
    }
}
