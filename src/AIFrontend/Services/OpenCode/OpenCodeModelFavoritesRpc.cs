using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

internal static class OpenCodeModelFavoritesRpc
{
    internal static JsonElement Output(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Object) throw new OpenCodeProtocolException("收藏 RPC 缺少 output 对象。");
        return output.Clone();
    }

    internal static string? ErrorCode(JsonElement error)
        => error.ValueKind == JsonValueKind.Object && error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() switch { "rpc.method_not_found" => "rpc.method_not_found", "rpc.invalid_input" => "rpc.invalid_input",
                "rpc.invalid_output" => "rpc.invalid_output", "rpc.internal" => "rpc.internal", "rpc.unavailable" => "rpc.unavailable", _ => null } : null;

    internal static object SetInput(IEnumerable<(string ProviderId, string ModelId)> favorites)
    {
        var values = favorites.Select(item => new { providerID = item.ProviderId, modelID = item.ModelId }).ToArray();
        if (values.Length > 200 || values.Any(item => !ValidId(item.providerID, 256) || !ValidId(item.modelID, 512)))
            throw new OpenCodeProtocolException("收藏数量或模型标识无效（最多 200 项）。");
        return new { favorites = values };
    }

    internal static (string ProviderId, string ModelId)[] Read(JsonElement output)
    {
        if (output.ValueKind != JsonValueKind.Object || output.EnumerateObject().Count() != 1
            || !output.TryGetProperty("favorites", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 200)
            throw new OpenCodeProtocolException("收藏 RPC 列表无效。");
        var result = new List<(string, string)>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2
                || !item.TryGetProperty("providerID", out var provider) || provider.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("modelID", out var model) || model.ValueKind != JsonValueKind.String
                || !ValidId(provider.GetString(), 256) || !ValidId(model.GetString(), 512))
                throw new OpenCodeProtocolException("收藏 RPC 模型标识无效。");
            result.Add((provider.GetString()!, model.GetString()!));
        }
        return result.ToArray();
    }

    private static bool ValidId(string? value, int max) => !string.IsNullOrEmpty(value) && value.Length <= max
        && !value.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\uFEFF');
}
