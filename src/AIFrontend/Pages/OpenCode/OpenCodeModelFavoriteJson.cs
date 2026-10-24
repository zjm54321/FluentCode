using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AIFrontend.Pages.OpenCode;

// OpenCode TUI model.json: modify only favorite, retaining all other JSON values verbatim.
internal static class OpenCodeModelFavoriteJson
{
    internal static IReadOnlyList<(string ProviderId, string ModelId)> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Favorites(document.RootElement).Select(Key).ToArray();
    }

    private static IEnumerable<JsonElement> Favorites(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("模型状态不是对象。");
        if (!root.TryGetProperty("favorite", out var favorites)) return Array.Empty<JsonElement>();
        if (favorites.ValueKind != JsonValueKind.Array) throw new JsonException("模型收藏不是数组。");
        return favorites.EnumerateArray().ToArray();
    }

    private static (string ProviderId, string ModelId) Key(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("providerID", out var provider)
            || !item.TryGetProperty("modelID", out var model) || provider.ValueKind != JsonValueKind.String
            || model.ValueKind != JsonValueKind.String) throw new JsonException("模型收藏缺少标识。");
        return (provider.GetString()!, model.GetString()!);
    }

    internal static string Set(string json, string providerId, string modelId, bool favorite)
    {
        using var document = JsonDocument.Parse(json);
        var entries = Favorites(document.RootElement).ToArray();
        // Validate the existing array before writing anything; malformed state is never replaced.
        foreach (var entry in entries) _ = Key(entry);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("favorite")) continue;
                writer.WritePropertyName(property.Name);
                writer.WriteRawValue(property.Value.GetRawText());
            }
            writer.WriteStartArray("favorite");
            foreach (var entry in entries)
                if (Key(entry) != (providerId, modelId)) writer.WriteRawValue(entry.GetRawText());
            if (favorite)
            {
                writer.WriteStartObject();
                writer.WriteString("providerID", providerId);
                writer.WriteString("modelID", modelId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
