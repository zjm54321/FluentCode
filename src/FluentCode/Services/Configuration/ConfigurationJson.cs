using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentCode.Services.Configuration;

/// <summary>
/// 配置文件共用的 JSON 序列化设置（内部使用）。
/// </summary>
/// <remarks>
/// 约定：camelCase 属性名；枚举以名称字符串读写（未知名称报错，视为损坏）；
/// null 属性不写出；读取时大小写不敏感；对非可空引用类型属性显式写 null 视为损坏
/// （<see cref="JsonSerializerOptions.RespectNullableAnnotations"/>）。
/// </remarks>
internal static class ConfigurationJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    internal static T Deserialize<T>(byte[] utf8Bytes)
    {
        if (JsonSerializer.Deserialize<T>(utf8Bytes, Options) is { } value)
        {
            return value;
        }

        throw new JsonException("JSON 内容为 null。");
    }

    internal static byte[] SerializeToUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
