using System;
using System.Globalization;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Segoe Fluent Icons glyphs for agents. oh-my-opencode-slim agents get their own glyph, matched by
/// agent name ignoring case (every councillor-* shares the councillor glyph); any other agent uses the
/// default agent glyph. Mapping follows the approved mockup legend (/tmp/opencode/fc-mockups/v4/icon-legend.png).
/// </summary>
internal static class OpenCodeAgentIcons
{
    internal const string DefaultGlyph = "\uE99A";

    // Fluent colors, light/dark respectively. Never use the user's accent as an agent identity.
    private static readonly (string Light, string Dark)[] ExtraColors =
    {
        ("#A4262C", "#FF99A4"), ("#4F6BED", "#A3B3FF"),
        ("#007A5A", "#54D6AF"), ("#744DA9", "#D0B0FF"),
        ("#9D5D00", "#FFC83D"), ("#7A4968", "#EAA4CE"),
    };

    /// <summary>Backend color wins; otherwise a stable, case-insensitive agent identity in either theme.</summary>
    internal static string Color(string? agentName, string? runtimeColor, bool dark)
    {
        if (runtimeColor is { Length: 7 } && runtimeColor[0] == '#'
            && uint.TryParse(runtimeColor.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            return runtimeColor;

        var name = agentName?.Trim().ToLowerInvariant() ?? string.Empty;
        var colors = name.StartsWith("councillor-", StringComparison.Ordinal) ? ("#107C10", "#6CCB5F") : name switch
        {
            "orchestrator" => ("#0078D4", "#4CC2FF"),
            "explorer" => ("#038387", "#00B7C3"),
            "oracle" => ("#8764B8", "#B4A0FF"),
            "designer" => ("#BF0077", "#FF6FCF"),
            "fixer" => ("#CA5010", "#FF9349"),
            "council" => ("#986F0B", "#FCE100"),
            "councillor" => ("#107C10", "#6CCB5F"),
            "secretary" => ("#005B70", "#60CDFF"),
            "librarian" => ("#8E562E", "#E0A060"),
            "observer" => ("#486860", "#9AB8B0"),
            _ => ExtraColors[StableHash(name) % (uint)ExtraColors.Length],
        };
        return dark ? colors.Item2 : colors.Item1;
    }

    private static uint StableHash(string name)
    {
        var hash = 2166136261u;
        foreach (var character in name) hash = unchecked((hash ^ character) * 16777619u);
        return hash;
    }

    internal static string Glyph(string? agentName)
    {
        var name = agentName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) return DefaultGlyph;
        if (name.StartsWith("councillor-", StringComparison.Ordinal)) return "\uE77B";
        return name switch
        {
            "orchestrator" => "\uEC26",
            "explorer" => "\uE721",
            "oracle" => "\uE82F",
            "council" => "\uE716",
            "councillor" => "\uE77B",
            "librarian" => "\uE8F1",
            "designer" => "\uE790",
            "fixer" => "\uE90F",
            "observer" => "\uE7B3",
            _ => DefaultGlyph,
        };
    }

    /// <summary>Display form required by the user: first letter of every '-'-separated part uppercase.</summary>
    internal static string DisplayName(string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName)) return agentName ?? "";
        var parts = agentName.Trim().Split('-');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join('-', parts);
    }
}
