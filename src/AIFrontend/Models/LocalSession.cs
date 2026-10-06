using System;
using System.Collections.Generic;

namespace AIFrontend.Models;

/// <summary>
/// 本地会话（不可变快照）：左侧会话列表与聊天记录的最小载体。
/// </summary>
/// <remarks>
/// <see cref="Id"/> 为稳定的文件级标识（由 <see cref="CreateNew"/> 生成 32 位小写十六进制 GUID），
/// 保存后不应更改。会话级 <see cref="Model"/> 为 null 时按文档中的“模型选择规则”
/// 回退到 <see cref="AppSettings.SelectedModel"/>。
/// </remarks>
public sealed record LocalSession
{
    /// <summary>稳定 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'）。</summary>
    public required string Id { get; init; }

    /// <summary>标题；null 表示未命名（UI 可自行生成展示名）。</summary>
    public string? Title { get; init; }

    /// <summary>会话级模型覆盖；null 表示跟随全局选中模型。</summary>
    public ModelRef? Model { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>最后更新时间（UTC；由存储层在保存时刷新）。</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>消息列表（按时间顺序）。</summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = Array.Empty<ChatMessage>();

    /// <summary>创建新会话（生成 ID 与 UTC 时间戳）。</summary>
    /// <param name="title">标题（可空）。</param>
    /// <param name="model">会话级模型覆盖（可空）。</param>
    public static LocalSession CreateNew(string? title = null, ModelRef? model = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = title,
        Model = model,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}
