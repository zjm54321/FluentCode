using System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Segoe Fluent Icons glyphs for agents. oh-my-opencode-slim agents get their own glyph, matched by
/// agent name ignoring case (every councillor-* shares the councillor glyph); any other agent uses the
/// default agent glyph. Mapping follows the approved mockup legend (/tmp/opencode/aif-mockups/v4/icon-legend.png).
/// </summary>
internal static class OpenCodeAgentIcons
{
    internal const string DefaultGlyph = "\uE99A";

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
