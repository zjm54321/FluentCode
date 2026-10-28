using System;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

public enum OpenCodeSessionPresetStatus
{
    Bound,
    Unbound,
}

/// <summary>
/// A session's persisted OMO preset binding, not the global preset or its native model.
/// A bound snapshot can have no name. There are no RPC events: read again on binding/explicit refresh.
/// </summary>
public sealed class OpenCodeSessionPresetState
{
    private readonly object _owner;

    internal OpenCodeSessionPresetState(object owner, string sessionId, string? directory,
        OpenCodeSessionPresetStatus status, string? presetName)
    {
        _owner = owner;
        SessionId = sessionId;
        Directory = directory;
        Status = status;
        PresetName = presetName;
    }

    public string SessionId { get; }
    public string? Directory { get; }
    public OpenCodeSessionPresetStatus Status { get; }
    public string? PresetName { get; }
    public string Affects => "future-children-only";

    /// <summary>
    /// Matches the exact requesting client/session/location, including across identical server URLs.
    /// Does not assert freshness: the caller must still check its captured source/binding revision.
    /// The opaque owner retains neither the client nor its credentials.
    /// </summary>
    public bool IsFor(OpenCodeClient? client, string sessionId, string? directory = null)
        => client is not null && ReferenceEquals(_owner, client.SessionPresetOwner)
            && string.Equals(SessionId, sessionId, StringComparison.Ordinal)
            && string.Equals(Directory, directory, StringComparison.Ordinal);

    internal static OpenCodeSessionPresetState Parse(JsonElement envelope, object owner,
        string sessionId, string? directory)
    {
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("sessionID", out var id) || id.ValueKind != JsonValueKind.String
            || !string.Equals(id.GetString(), sessionId, StringComparison.Ordinal)
            || !output.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
            || status.GetString() is not ("bound" or "unbound")
            || !output.TryGetProperty("presetName", out var name) || name.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || !output.TryGetProperty("affects", out var affects) || affects.ValueKind != JsonValueKind.String
            || affects.GetString() != "future-children-only")
            throw new OpenCodeProtocolException("Session preset RPC 响应缺少必填字段、状态无效或所属会话不匹配。");

        return new(owner, sessionId, directory,
            status.GetString() == "bound" ? OpenCodeSessionPresetStatus.Bound : OpenCodeSessionPresetStatus.Unbound,
            name.ValueKind == JsonValueKind.Null ? null : name.GetString());
    }
}

/// <summary>RPC failure with only HTTP status and a recognized code; never retains server messages/data.</summary>
public sealed class OpenCodeSessionPresetRpcException(int statusCode, string? code)
    : OpenCodeException($"Session preset RPC 失败（HTTP {statusCode}）。")
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsUnsupported => StatusCode == 404 || Code == "rpc.method_not_found";
    public bool IsUnavailable => IsUnsupported || Code == "rpc.unavailable";
}
