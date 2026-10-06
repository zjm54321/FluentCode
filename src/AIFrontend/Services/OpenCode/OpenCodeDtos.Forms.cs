using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFrontend.Services.OpenCode;

/// <summary>GET .../form 的 {data: Form.Info[]}；列表仅包含 pending forms。</summary>
public sealed record OpenCodeFormListResponse
{
    [JsonPropertyName("data")]
    public List<OpenCodeFormInfo>? Data { get; init; }
}

public record OpenCodeFormInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("sessionID")]
    public string? SessionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("fields")]
    public List<OpenCodeFormField>? Fields { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>GET .../form/{formID} 的 data（Form.Detail）。发送前应重新读取而非信任旧通知快照。</summary>
public sealed record OpenCodeFormDetail : OpenCodeFormInfo
{
    [JsonPropertyName("state")]
    public OpenCodeFormState? State { get; init; }
}

public sealed record OpenCodeFormState
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>仅 answered 时必填；值限 string/number/bool/string[]。</summary>
    [JsonPropertyName("answer")]
    public Dictionary<string, JsonElement>? Answer { get; init; }

    /// <summary>cancelled 时可选。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>已核实核心字段；其余字段（when/default/边界/url 等）保留 JSON，不猜测或执行。</summary>
public sealed record OpenCodeFormField
{
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("required")]
    public bool? Required { get; init; }

    [JsonPropertyName("hidden")]
    public bool? Hidden { get; init; }

    [JsonPropertyName("options")]
    public List<OpenCodeFormOption>? Options { get; init; }

    [JsonPropertyName("custom")]
    public bool? Custom { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record OpenCodeFormOption
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public static class OpenCodeFormStatuses
{
    public const string Pending = "pending";
    public const string Answered = "answered";
    public const string Cancelled = "cancelled";
}

public static class OpenCodeFormFieldTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string Multiselect = "multiselect";
    public const string External = "external";
}
