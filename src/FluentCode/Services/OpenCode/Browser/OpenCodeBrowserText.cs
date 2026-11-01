using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Human-readable CDP text, not the wire encoder or HTML/JavaScript source.</summary>
public static class OpenCodeBrowserText
{
    public static JsonSerializerOptions Options { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Quote(string value) => JsonSerializer.Serialize(value, Options);
    public static string Json(JsonNode value) => value.ToJsonString(Options);
}
