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

/// <summary>核心字段与常用输入元数据；约束保留 JSON 类型，由输入策略验证，不静默忽略错误类型。</summary>
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

    // 原始元数据仍在扩展数据中：保留指纹与其他消费者原有的 fail-closed 行为。
    // Undefined 表示未提供；显式 Null 保留，由输入策略报错，而不是静默丢弃。
    // 使用方法而非 JsonIgnore 属性，避免 Web/case-insensitive 反序列化吞掉同名扩展数据。
    public JsonElement GetPlaceholder() => Property("placeholder");

    public JsonElement GetDefault() => Property("default");

    public JsonElement GetMinLength() => Property("minLength");

    public JsonElement GetMaxLength() => Property("maxLength");

    public JsonElement GetMinimum() => Property("minimum");

    public JsonElement GetMaximum() => Property("maximum");

    public JsonElement GetMinItems() => Property("minItems");

    public JsonElement GetMaxItems() => Property("maxItems");

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    private JsonElement Property(string name)
        => AdditionalProperties is { } properties && properties.TryGetValue(name, out var value) ? value : default;
}

public sealed record OpenCodeFormOption
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    public JsonElement GetDescription()
        => AdditionalProperties is { } properties && properties.TryGetValue("description", out var value) ? value : default;

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
