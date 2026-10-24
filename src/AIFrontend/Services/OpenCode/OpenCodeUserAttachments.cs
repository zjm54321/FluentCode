using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

public sealed record OpenCodeUserAttachment(string Key, string Name, OpenCodeToolImageSource? Image);

/// <summary>Pure projection: v2 user.files inline payloads and file content parts; never decodes or reads bytes.</summary>
public static class OpenCodeUserAttachments
{
    public static IReadOnlyList<OpenCodeUserAttachment> FromMessage(OpenCodeMessage message)
    {
        var result = new List<OpenCodeUserAttachment>();
        void Add(string key, string? mime, string? name, string? reference)
        {
            name = string.IsNullOrWhiteSpace(name) ? "附件" : name;
            var image = mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(reference)
                ? new OpenCodeToolImageSource(reference, mime, name) : null;
            result.Add(new((message.Id ?? "user") + "/" + key, name, image));
        }
        if (message.Files is { ValueKind: JsonValueKind.Array } files)
        {
            var index = 0;
            foreach (var file in files.EnumerateArray())
            {
                string? Field(string key) => file.ValueKind == JsonValueKind.Object && file.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var mime = Field("mime");
                var reference = Field("url");
                if (reference is null && Field("data") is { } data && mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                    reference = "data:" + mime + ";base64," + data;
                Add("file:" + index++, mime, Field("filename") ?? Field("name"), reference);
            }
        }
        if (message.Content is { } parts)
            for (var i = 0; i < parts.Count; i++)
                if (parts[i] is { Type: "file" } part)
                    Add(part.Id ?? "part:" + i, part.Mime, part.FileName ?? part.Name, part.Url);
        return result;
    }
}
