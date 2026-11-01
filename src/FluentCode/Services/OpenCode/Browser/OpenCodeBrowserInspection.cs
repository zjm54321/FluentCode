using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Permission metadata identifies the resource, never upload bytes, paths or action arguments.</summary>
public static class OpenCodeBrowserInspection
{
    public static JsonObject Target(IEnumerable<string> resources, object identity) => new()
    {
        ["resources"] = new JsonArray(resources.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["key"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))))
    };
    public static void Check(JsonObject? expected, JsonObject actual)
    {
        if (expected is not null && !JsonNode.DeepEquals(expected, actual))
            throw new OpenCodeBrowserException("target_changed", "等待权限期间浏览器目标已改变；操作未执行，请重新读取页面或文件列表后重试");
    }
}
