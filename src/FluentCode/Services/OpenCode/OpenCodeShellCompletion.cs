using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace FluentCode.Services.OpenCode;

/// <summary>Machine-origin shell output retained after the live shell registry is collected.</summary>
internal sealed record OpenCodeShellCompletion(string Id, string? Command, string Output, string State, double? Exit, bool Finished)
{
    internal static OpenCodeShellCompletion? Read(OpenCodeMessage message)
    {
        if (message.Type is not ("synthetic" or "system") || Text(message.Metadata, "source") != "shell") return null;
        var text = message.Text ?? string.Join("\n", (message.Content ?? new()).Where(part => part.Type == "text").Select(part => part.Text));
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("<shell", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 6
            && (char.IsWhiteSpace(trimmed[6]) || trimmed[6] == '>'))
        {
            var quote = '\0'; var end = -1;
            for (var i = 6; i < trimmed.Length; i++)
            {
                var c = trimmed[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '\'' or '"') quote = c;
                else if (c == '>') { end = i; break; }
            }
            if (end >= 0)
            {
                var head = trimmed[..end]; var position = 0;
                while (position < head.Length)
                {
                    var equals = head.IndexOf("=\"", position, StringComparison.Ordinal);
                    if (equals < 0) break;
                    var start = equals - 1;
                    while (start >= 0 && (char.IsLetterOrDigit(head[start]) || head[start] is '_' or '-')) start--;
                    var close = head.IndexOf('"', equals + 2);
                    if (close < 0) break;
                    attributes[head[(start + 1)..equals]] = WebUtility.HtmlDecode(head[(equals + 2)..close]);
                    position = close + 1;
                }
                var closeTag = trimmed.LastIndexOf("</shell>", StringComparison.OrdinalIgnoreCase);
                body = closeTag > end ? trimmed[(end + 1)..closeTag].Trim('\r', '\n') : trimmed[(end + 1)..];
            }
        }
        var id = Text(message.Metadata, "shellID") ?? Text(message.Metadata, "jobID") ?? attributes.GetValueOrDefault("id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        double? exit = message.Metadata is { ValueKind: JsonValueKind.Object } metadata && metadata.TryGetProperty("exit", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var code) ? code : null;
        var timeout = message.Metadata is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty("timeout", out var timedOut) && timedOut.ValueKind == JsonValueKind.True;
        var state = timeout ? "timeout" : Text(message.Metadata, "state") ?? Text(message.Metadata, "status") ?? attributes.GetValueOrDefault("state") ?? (exit.HasValue ? "completed" : string.Empty);
        var finished = exit.HasValue || state is "completed" or "exited" or "succeeded" or "success" or "done" or "failed" or "error"
            or "killed" or "interrupted" or "timeout" or "aborted" or "cancelled" or "canceled";
        return new(id, attributes.GetValueOrDefault("command") ?? message.Description,
            message.Output?.Output ?? Text(message.Metadata, "output") ?? body, state, exit, finished);
    }

    private static string? Text(JsonElement? value, string name)
        => value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
