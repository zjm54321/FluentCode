using System;
using System.Collections.Generic;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeSubagentTaskTitles
{
    internal static IReadOnlyDictionary<string, string> Build(IReadOnlyList<OpenCodeMessage> messages)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (messages is null)
        {
            return result;
        }

        var latest = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (message is null)
            {
                continue;
            }

            if ((message.Type == OpenCodeMessageTypes.Assistant ||
                 message.Type == OpenCodeMessageTypes.Synthetic ||
                 message.Type == OpenCodeMessageTypes.System) &&
                !string.IsNullOrEmpty(message.Id) &&
                OpenCodeSubagentPresentation.TryReadCompletionMetadata(message.Metadata, out var completion) &&
                latest.TryGetValue(completion.ChildSessionId!, out var description))
            {
                result[message.Id!] = description;
            }

            if (message.Type != OpenCodeMessageTypes.Assistant || message.Content is null)
            {
                continue;
            }

            foreach (var part in message.Content)
            {
                if (part is null || part.Type != OpenCodePartTypes.Tool)
                {
                    continue;
                }

                if (!OpenCodeSubagentPresentation.TryReadToolPart(part, out var tool) ||
                    string.IsNullOrEmpty(tool.ChildSessionId))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(tool.Description))
                {
                    latest.Remove(tool.ChildSessionId!);
                }
                else
                {
                    latest[tool.ChildSessionId!] = tool.Description!;
                }
            }
        }

        return result;
    }
}
