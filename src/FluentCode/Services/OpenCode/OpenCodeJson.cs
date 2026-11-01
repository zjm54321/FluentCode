using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// OpenCode 客户端与 DTO 共用的 JSON 选项。
/// 采用 Web 默认值（大小写不敏感、camelCase 命名策略、数字允许从字符串读取）；
/// 写出时忽略 null，使 POST api/session 在无 location 时发送空对象 {}。
/// 所有 DTO 字段均显式标注 <see cref="JsonPropertyNameAttribute"/>，不依赖命名策略。
/// </summary>
internal static class OpenCodeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
