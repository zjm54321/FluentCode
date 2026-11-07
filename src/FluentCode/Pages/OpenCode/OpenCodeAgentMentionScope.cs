using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

/// <summary>Presentation-only filtering of Agent.Info.permissions; the host still owns delegation authorization.</summary>
internal static class OpenCodeAgentMentionScope
{
    // OMO permission.task is projected to native V2 action=subagent, resource=agent ID, effect=allow/ask/deny.
    internal static bool AllowsMention(OpenCodeAgentInfo? primary, string candidateId)
    {
        if (primary?.Permissions is not { ValueKind: JsonValueKind.Array } rules) return true;
        string? effect = null;
        foreach (var rule in rules.EnumerateArray())
            if (String(rule, "action") is { } action && Matches("subagent", action)
                && String(rule, "resource") is { } resource && Matches(candidateId, resource)
                && String(rule, "effect") is { } matched && matched is "allow" or "ask" or "deny")
                effect = matched;
        return effect != "deny";
    }

    private static string? String(JsonElement rule, string key)
        => rule.ValueKind == JsonValueKind.Object && rule.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool Matches(string value, string pattern)
    {
        var input = 0;
        var token = 0;
        var star = -1;
        var retry = 0;
        while (input < value.Length)
        {
            if (token < pattern.Length && pattern[token] == '*') { star = token++; retry = input; }
            else if (token < pattern.Length && pattern[token] == value[input]) { input++; token++; }
            else if (star >= 0) { token = star + 1; input = ++retry; }
            else return false;
        }
        while (token < pattern.Length && pattern[token] == '*') token++;
        return token == pattern.Length;
    }
}
