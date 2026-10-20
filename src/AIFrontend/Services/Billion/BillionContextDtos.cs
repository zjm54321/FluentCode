using System;
using System.Collections.Immutable;

namespace AIFrontend.Services.Billion;

public enum BillionSessionStatus { Available, NoRecord, Unavailable, Malformed }
public enum BillionContextTokensSource { Unknown, Usage, Estimate }
public enum BillionCapabilityStatus { Unavailable }

public static class BillionCapabilities
{
    public const BillionCapabilityStatus CompressionToggle = BillionCapabilityStatus.Unavailable;
    public const string CompressionToggleReason = "Billion 无会话级压缩开关；全局配置写入未授权";
}

public sealed record BillionHealth(bool Available, string? Version, string? InstanceId, string? Reason);

/// <summary>Only allowlisted telemetry. Null numeric values mean Unknown, never zero.</summary>
public sealed record BillionSessionStats
{
    public BillionSessionStatus Status { get; internal set; }
    public long? ContextTokens { get; internal set; }
    public BillionContextTokensSource ContextTokensSource { get; internal set; }
    public long? ContextBestTokens { get; internal set; }
    public string ContextBestKind { get; internal set; } = "Unknown";
    public long? ContextWindow { get; internal set; }
    public long? ContextUpperTokens { get; internal set; }
    public long? ContextMeasuredAt { get; internal set; }
    public long? SystemPromptTokens { get; internal set; }
    public long? LastInputTokens { get; internal set; }
    public long? InputTokens { get; internal set; }
    public long? CachedTokens { get; internal set; }
    public long? OutputTokens { get; internal set; }
    public double? CacheHitPct { get; internal set; }
    public long? Requests { get; internal set; }
    public long? Blocks { get; internal set; }
    public long? TokensSaved { get; internal set; }
    public double? NetSaved { get; internal set; }
    public bool? Live { get; internal set; }
    public DateTimeOffset? LastSeen { get; internal set; }
    public string? BiliVersion { get; internal set; }
    public DateTimeOffset FetchedAt { get; internal set; }

    /// <summary>
    /// 压缩块元数据列表（detail.blockDetails；与 OpenCode TUI 中 billion-context 的 /acp 面板 “Blocks” 段同源）。只投影
    /// blockId / tier / compressedTokens / active 四个白名单字段；块主题（topic）、摘要（summary）等正文从不解码。
    /// 最多 <see cref="MaximumBlockDetails"/> 条。不可变数组（值类型包装，不暴露可变集合）。
    /// </summary>
    public ImmutableArray<BillionBlockInfo> BlockDetails { get; internal set; } = ImmutableArray<BillionBlockInfo>.Empty;

    /// <summary>blockDetails 条目多于上限（其余未投影）。</summary>
    public bool BlockDetailsTruncated { get; internal set; }

    public const int MaximumBlockDetails = 200;
}

/// <summary>单个压缩块的白名单元数据（null = 未知）；不含主题或摘要正文。</summary>
public sealed record BillionBlockInfo(string? Id, long? Tier, long? CompressedTokens, bool? Active);
