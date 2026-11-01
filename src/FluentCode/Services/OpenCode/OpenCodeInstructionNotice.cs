using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace FluentCode.Services.OpenCode;

/// <summary>
/// 指令更新通知的安全投影：只保留客户端可见的最小字段集。
/// <see cref="MessageId"/>、<see cref="Created"/> 可为空；<see cref="Summary"/> 已按显示上限截断。
/// </summary>
public record OpenCodeInstructionNotice(
    string? MessageId,
    string Summary,
    ImmutableArray<string> SourceKeys,
    double? Created)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Body { get; init; }
}

/// <summary>
/// 将 <see cref="OpenCodeMessageTypes.System"/> 且 metadata.notice == "instructions" 的消息
/// 收敛为安全通知，并剥离所有其余字段。无日志、网络、UI 或额外依赖。
/// </summary>
public static class OpenCodeInstructionNoticeProjection
{
    private const string NoticeKind = "instructions";
    private const int MaxSourceKeys = 32;
    private const int MaxKeyLength = 256;
    private const int MaxSummaryLength = 1024;
    private const string FallbackSummary = "指令已更新，来源未提供";

    /// <summary>
    /// 命中时返回仅含 Id、Type、Description、安全 metadata（notice/instructionSources）
    /// 与只含 Created 的新 Time 的消息；其余字段（Text/Content/Snapshot/provider/Error/Retry/
    /// subagent 等）一律丢弃；有界正文仅存入不序列化的显式查看字段。未命中返回原消息不变。
    /// </summary>
    public static OpenCodeMessage SanitizeForClient(OpenCodeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!TryGetInstructionsNotice(message, out var notice))
        {
            return message;
        }

        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["notice"] = NoticeKind,
            ["instructionSources"] = notice.SourceKeys,
        });

        return new OpenCodeMessage
        {
            Id = message.Id,
            Type = message.Type,
            Description = notice.Summary,
            InstructionBody = notice.Body,
            Metadata = metadata,
            Time = notice.Created is { } created
                ? new OpenCodeMessageTime { Created = created }
                : null,
        };
    }

    /// <summary>
    /// 严格命中判定：Type 为 System（Ordinal），且 metadata 为对象且其中 notice 字符串
    /// Ordinal 等于 "instructions"。不读取 Text 作为 fallback。
    /// </summary>
    public static bool TryGetInstructionsNotice(OpenCodeMessage message, out OpenCodeInstructionNotice notice)
    {
        notice = null!;

        if (message is null)
        {
            return false;
        }

        if (!string.Equals(message.Type, OpenCodeMessageTypes.System, StringComparison.Ordinal))
        {
            return false;
        }

        if (message.Metadata is not { } metadata || metadata.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!metadata.TryGetProperty("notice", out var noticeElement)
            || noticeElement.ValueKind != JsonValueKind.String
            || !string.Equals(noticeElement.GetString(), NoticeKind, StringComparison.Ordinal))
        {
            return false;
        }

        var sourceKeys = ReadSourceKeys(metadata);
        var summary = BuildSummary(sourceKeys, message.Description);
        var created = ReadCreated(message.Time);

        notice = new OpenCodeInstructionNotice(message.Id, summary, sourceKeys, created)
        {
            Body = OpenCodeInstructionSource.BoundedBody(message.InstructionBody ?? message.Text),
        };
        return true;
    }

    private static ImmutableArray<string> ReadSourceKeys(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("instructionSources", out var sources)
            || sources.ValueKind != JsonValueKind.Array)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in sources.EnumerateArray())
        {
            if (builder.Count >= MaxSourceKeys)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = item.GetString();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            builder.Add(Truncate(key, MaxKeyLength));
        }

        return builder.ToImmutable();
    }

    private static string BuildSummary(ImmutableArray<string> sourceKeys, string? description)
    {
        if (!sourceKeys.IsDefaultOrEmpty)
        {
            return Truncate("指令已更新，来源：" + string.Join("、", sourceKeys), MaxSummaryLength);
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            return Truncate(description, MaxSummaryLength);
        }

        return FallbackSummary;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    private static double? ReadCreated(OpenCodeMessageTime? time)
    {
        if (time is not { } value)
        {
            return null;
        }

        var created = value.Created;
        return double.IsFinite(created) && created >= 0 ? created : null;
    }
}
