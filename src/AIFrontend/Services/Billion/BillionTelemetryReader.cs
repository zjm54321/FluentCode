using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Billion;

/// <summary>
/// Incremental UTF-8 projection, not a document/DOM. Ignored values are never decoded to strings.
/// Utf8JsonReader needs one complete token, so a giant string may temporarily occupy the bounded
/// byte buffer; ignored containers are traversed token by token, not buffered as entire subtrees.
/// All pooled buffers are cleared on return, including on malformed input/cancellation.
/// </summary>
internal sealed class BillionTelemetryReader
{
    private readonly bool _health;
    private JsonReaderState _state = new(new JsonReaderOptions { MaxDepth = 128 });
    private Field _pending;
    private bool _rootStarted;
    private bool _rootFinished;
    private bool _inContextBest;
    // detail.blockDetails（数组，深度 1）→ 每个元素对象（深度 2）→ 白名单属性（深度 3）。
    private bool _inBlocks;
    private bool _blockOpen;
    private string? _blockId;
    private long? _blockTier;
    private long? _blockTokens;
    private bool? _blockActive;
    private readonly List<BillionBlockInfo> _blocks = new();
    private bool _identitySeen;
    private string? _sessionId;
    public bool? HealthOk { get; private set; }
    public string? Version { get; private set; }
    public string? InstanceId { get; private set; }
    public BillionSessionStats Stats { get; } = new();

    private BillionTelemetryReader(bool health) => _health = health;

    // The requested ID has already passed the client's syntax/length validation. Exact equality
    // therefore also rejects invalid response IDs, without exposing the response identity.
    internal bool MatchesSessionId(string requestedId) =>
        _identitySeen && string.Equals(_sessionId, requestedId, StringComparison.Ordinal);

    public static async Task<BillionTelemetryReader> ReadAsync(Stream stream, bool health, int limit, CancellationToken ct)
    {
        var projection = new BillionTelemetryReader(health);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        int buffered = 0;
        int total = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (buffered == buffer.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, limit + 1));
                    buffer.AsSpan(0, buffered).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    buffer = larger;
                }
                int read = await stream.ReadAsync(buffer.AsMemory(buffered,
                    Math.Min(buffer.Length - buffered, limit - total + 1)), ct).ConfigureAwait(false);
                total += read;
                if (total > limit)
                    throw new JsonException("Response exceeds byte limit.");
                buffered += read;
                int consumed = projection.Consume(buffer.AsSpan(0, buffered), read == 0, ct);
                buffered -= consumed;
                buffer.AsSpan(consumed, buffered).CopyTo(buffer);
                if (read == 0)
                {
                    if (!projection._rootFinished || buffered != 0)
                        throw new JsonException("Expected a complete JSON object.");
                    return projection;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private int Consume(ReadOnlySpan<byte> bytes, bool final, CancellationToken ct)
    {
        var reader = new Utf8JsonReader(bytes, final, _state);
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (!_rootStarted)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException("Expected a JSON object.");
                _rootStarted = true;
                continue;
            }
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                _pending = reader.CurrentDepth == 1 ? SelectRootField(ref reader) :
                    !_health && _inContextBest && reader.CurrentDepth == 2 ?
                        reader.ValueTextEquals("tokens"u8) ? Field.BestTokens :
                        reader.ValueTextEquals("kind"u8) ? Field.BestKind : Field.None :
                    !_health && _inBlocks && _blockOpen && reader.CurrentDepth == 3 ? SelectBlockField(ref reader) : Field.None;
                continue;
            }
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (reader.CurrentDepth == 0) _rootFinished = true;
                if (reader.CurrentDepth == 1) _inContextBest = false;
                if (reader.CurrentDepth == 2 && _inBlocks && _blockOpen) CommitBlock();
            }
            else if (reader.TokenType == JsonTokenType.StartObject && _inBlocks && reader.CurrentDepth == 2)
            {
                _blockOpen = true;
                _blockId = null;
                _blockTier = null;
                _blockTokens = null;
                _blockActive = null;
            }
            else if (reader.TokenType == JsonTokenType.EndArray && _inBlocks && reader.CurrentDepth == 1)
            {
                _inBlocks = false;
                Stats.BlockDetails = _blocks.ToImmutableArray();
            }
            Project(ref reader, _pending);
            _pending = Field.None;
        }
        _state = reader.CurrentState;
        return checked((int)reader.BytesConsumed);
    }

    private Field SelectRootField(ref Utf8JsonReader r)
    {
        if (_health)
        {
            if (r.ValueTextEquals("ok"u8)) return Field.Ok;
            if (r.ValueTextEquals("version"u8)) return Field.Version;
            if (r.ValueTextEquals("instanceId"u8)) return Field.InstanceId;
            return Field.None;
        }
        if (r.ValueTextEquals("id"u8))
        {
            if (_identitySeen) throw new JsonException("Duplicate session identity.");
            _identitySeen = true;
            return Field.Id;
        }
        if (r.ValueTextEquals("contextTokens"u8)) return Field.ContextTokens;
        if (r.ValueTextEquals("contextTokensSource"u8)) return Field.ContextTokensSource;
        if (r.ValueTextEquals("contextBest"u8)) return Field.ContextBest;
        if (r.ValueTextEquals("contextWindow"u8)) return Field.ContextWindow;
        if (r.ValueTextEquals("contextUpperTokens"u8)) return Field.ContextUpperTokens;
        if (r.ValueTextEquals("contextMeasuredAt"u8)) return Field.ContextMeasuredAt;
        if (r.ValueTextEquals("systemPromptTokens"u8)) return Field.SystemPromptTokens;
        if (r.ValueTextEquals("lastInputTokens"u8)) return Field.LastInputTokens;
        if (r.ValueTextEquals("inputTokens"u8)) return Field.InputTokens;
        if (r.ValueTextEquals("cachedTokens"u8)) return Field.CachedTokens;
        if (r.ValueTextEquals("outputTokens"u8)) return Field.OutputTokens;
        if (r.ValueTextEquals("cacheHitPct"u8)) return Field.CacheHitPct;
        if (r.ValueTextEquals("requests"u8)) return Field.Requests;
        if (r.ValueTextEquals("blocks"u8)) return Field.Blocks;
        if (r.ValueTextEquals("tokensSaved"u8)) return Field.TokensSaved;
        if (r.ValueTextEquals("netSaved"u8)) return Field.NetSaved;
        if (r.ValueTextEquals("live"u8)) return Field.Live;
        if (r.ValueTextEquals("lastSeen"u8)) return Field.LastSeen;
        if (r.ValueTextEquals("biliVersion"u8)) return Field.BiliVersion;
        if (r.ValueTextEquals("blockDetails"u8)) return Field.BlockDetails;
        return Field.None;
    }

    private static Field SelectBlockField(ref Utf8JsonReader r)
    {
        if (r.ValueTextEquals("blockId"u8)) return Field.BlockId;
        if (r.ValueTextEquals("tier"u8)) return Field.BlockTier;
        if (r.ValueTextEquals("compressedTokens"u8)) return Field.BlockTokens;
        if (r.ValueTextEquals("active"u8)) return Field.BlockActive;
        return Field.None; // topic / summary / createdAt / refs 等正文：从不解码（Billion 标题/摘要不进入本应用）。
    }

    private void CommitBlock()
    {
        _blockOpen = false;
        if (_blocks.Count >= BillionSessionStats.MaximumBlockDetails)
        {
            Stats.BlockDetailsTruncated = true;
            return;
        }

        _blocks.Add(new BillionBlockInfo(_blockId, _blockTier, _blockTokens, _blockActive));
    }

    private void Project(ref Utf8JsonReader r, Field field)
    {
        switch (field)
        {
            case Field.Id:
                // A valid 256-character ASCII ID can use six JSON bytes per escaped character.
                // Do not use the 128-byte display-metadata helper or decode unbounded strings.
                if (r.TokenType != JsonTokenType.String || r.ValueSpan.Length > 256 * 6)
                    throw new JsonException("Invalid session identity.");
                string? id = r.GetString();
                if (id is null || id.Length > 256)
                    throw new JsonException("Invalid session identity.");
                _sessionId = id;
                break;
            case Field.Ok: HealthOk = Boolean(ref r); break;
            case Field.Version: Version = ShortString(ref r); break;
            case Field.InstanceId: InstanceId = ShortString(ref r); break;
            case Field.ContextTokens: Stats.ContextTokens = Count(ref r); break;
            case Field.ContextTokensSource:
                Stats.ContextTokensSource = r.TokenType != JsonTokenType.String ? BillionContextTokensSource.Unknown :
                    r.ValueTextEquals("usage"u8) ? BillionContextTokensSource.Usage :
                    r.ValueTextEquals("estimate"u8) ? BillionContextTokensSource.Estimate : BillionContextTokensSource.Unknown;
                break;
            case Field.ContextBest:
                Stats.ContextBestTokens = null;
                Stats.ContextBestKind = "Unknown";
                _inContextBest = r.TokenType == JsonTokenType.StartObject;
                break;
            case Field.BestTokens: Stats.ContextBestTokens = Count(ref r); break;
            case Field.BestKind: Stats.ContextBestKind = ShortString(ref r) ?? "Unknown"; break;
            case Field.ContextWindow: Stats.ContextWindow = Count(ref r); break;
            case Field.ContextUpperTokens: Stats.ContextUpperTokens = Count(ref r); break;
            case Field.ContextMeasuredAt: Stats.ContextMeasuredAt = Count(ref r); break;
            case Field.SystemPromptTokens: Stats.SystemPromptTokens = Count(ref r); break;
            case Field.LastInputTokens: Stats.LastInputTokens = Count(ref r); break;
            case Field.InputTokens: Stats.InputTokens = Count(ref r); break;
            case Field.CachedTokens: Stats.CachedTokens = Count(ref r); break;
            case Field.OutputTokens: Stats.OutputTokens = Count(ref r); break;
            case Field.CacheHitPct:
                double? pct = Number(ref r);
                Stats.CacheHitPct = pct is >= 0 and <= 100 ? pct : null;
                break;
            case Field.Requests: Stats.Requests = Count(ref r); break;
            case Field.Blocks: Stats.Blocks = Count(ref r); break;
            case Field.TokensSaved: Stats.TokensSaved = Count(ref r); break;
            case Field.NetSaved: Stats.NetSaved = Number(ref r); break;
            case Field.Live: Stats.Live = Boolean(ref r); break;
            case Field.LastSeen:
                Stats.LastSeen = r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= 128 &&
                    r.TryGetDateTimeOffset(out var date) ? date : null;
                break;
            case Field.BiliVersion: Stats.BiliVersion = ShortString(ref r); break;
            case Field.BlockDetails:
                // 重复键：以最后一次为准。非数组 → 视为未提供（空列表）。
                _blocks.Clear();
                Stats.BlockDetails = ImmutableArray<BillionBlockInfo>.Empty;
                Stats.BlockDetailsTruncated = false;
                _inBlocks = r.TokenType == JsonTokenType.StartArray;
                _blockOpen = false;
                break;
            case Field.BlockId: _blockId = ShortString(ref r); break;
            case Field.BlockTier: _blockTier = Count(ref r); break;
            case Field.BlockTokens: _blockTokens = Count(ref r); break;
            case Field.BlockActive: _blockActive = Boolean(ref r); break;
        }
    }

    private static long? Count(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.Number &&
        r.TryGetInt64(out long value) && value >= 0 ? value : null;
    private static double? Number(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.Number &&
        r.TryGetDouble(out double value) && double.IsFinite(value) ? value : null;
    private static bool? Boolean(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.True ? true :
        r.TokenType == JsonTokenType.False ? false : null;
    private static string? ShortString(ref Utf8JsonReader r) =>
        r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= 128 ? r.GetString() : null;

    private enum Field
    {
        None, Id, Ok, Version, InstanceId, ContextTokens, ContextTokensSource, ContextBest, BestTokens, BestKind,
        ContextWindow, ContextUpperTokens, ContextMeasuredAt, SystemPromptTokens, LastInputTokens,
        InputTokens, CachedTokens, OutputTokens, CacheHitPct, Requests, Blocks, TokensSaved,
        NetSaved, Live, LastSeen, BiliVersion,
        BlockDetails, BlockId, BlockTier, BlockTokens, BlockActive
    }
}
