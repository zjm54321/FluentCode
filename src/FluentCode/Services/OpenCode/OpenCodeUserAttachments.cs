using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FluentCode.Services.OpenCode;

public sealed record OpenCodeUserAttachment(string Key, string Name, OpenCodeToolImageSource? Image);

/// <summary>Pure projection: v2 user.files inline payloads and file content parts; never decodes or reads bytes.</summary>
public static class OpenCodeUserAttachments
{
    public static IReadOnlyList<OpenCodeUserAttachment> FromMessage(OpenCodeMessage message)
    {
        var result = new List<OpenCodeUserAttachment>();
        var owner = message.Id ?? "user";
        AddFiles(result, owner, message.Files);
        if (message.Content is { } parts)
            for (var i = 0; i < parts.Count; i++)
                if (parts[i] is { Type: "file" } part)
                    Add(result, owner, part.Id ?? "part:" + i, part.Mime, part.FileName ?? part.Name, part.Url);
        return result;
    }

    /// <summary>The same file projection for a pending user message, without inventing a delivered history message.</summary>
    public static IReadOnlyList<OpenCodeUserAttachment> FromInbox(OpenCodeInboxItem item)
    {
        var result = new List<OpenCodeUserAttachment>();
        if (item.Type == OpenCodeInboxItemTypes.User && item.Payload is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("files", out var files)) AddFiles(result, item.Id ?? "pending-user", files);
        return result;
    }

    /// <summary>Attachment-token eligibility shared by delivered and pending text presenters.</summary>
    public static (int Images, int Pdfs) Counts(IReadOnlyList<OpenCodeUserAttachment> attachments)
    {
        var images = 0;
        var pdfs = 0;
        foreach (var attachment in attachments)
        {
            if (attachment.Image is not null) images++;
            if (attachment.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) pdfs++;
        }
        return (images, pdfs);
    }

    private static void AddFiles(List<OpenCodeUserAttachment> result, string owner, JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Array } files)
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
                Add(result, owner, "file:" + index++, mime, Field("filename") ?? Field("name"), reference);
            }
        }
    }

    private static void Add(List<OpenCodeUserAttachment> result, string owner, string key, string? mime, string? name, string? reference)
    {
        var isImage = mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        if (string.IsNullOrWhiteSpace(name) || isImage && string.Equals(name, "clipboard", StringComparison.OrdinalIgnoreCase))
            name = isImage ? "图片" : "附件";
        var image = isImage && !string.IsNullOrWhiteSpace(reference) ? new OpenCodeToolImageSource(reference, mime, name) : null;
        result.Add(new(owner + "/" + key, name, image));
    }
}
