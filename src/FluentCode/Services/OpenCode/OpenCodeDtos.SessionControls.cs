using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentCode.Services.Notifications;

namespace FluentCode.Services.OpenCode;

public sealed record SessionControlModelRef(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("providerID")] string ProviderId,
    [property: JsonPropertyName("variant"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Variant = null);
public sealed record SessionControlModel(string Id, string ModelId, string ProviderId, string Name, bool Enabled,
    ImmutableArray<string> Variants, string Fingerprint)
{
    public bool Offers(SessionControlModelRef model) => Enabled && Id == model.Id && ProviderId == model.ProviderId
        // The server canonicalizes an omitted/base selection to "default" in session state;
        // catalogs need not list that sentinel among their named variants.
        && (model.Variant is null or "default" || Variants.Contains(model.Variant, StringComparer.Ordinal));
}
public sealed record SessionControlModels(string Directory, ImmutableArray<SessionControlModel> Models);
public sealed record SessionControlDefaultModel(string Directory, SessionControlModel? Model);
public sealed record SessionControlRevert(string MessageId, string? PartId, bool HasSnapshot, int FileCount, string Fingerprint)
{
    public bool MayHaveFileEffects => HasSnapshot || FileCount != 0;
}
/// <summary>Model == null means ABSENT/inherited, never an explicit wire null.</summary>
public sealed record SessionControlState(string SessionId, string Directory, SessionControlModelRef? Model,
    SessionControlRevert? Revert, string Fingerprint)
{
    public string? Title { get; init; }
    public string? ParentId { get; init; }
}
public sealed record SessionControlHistoryMessage(string Id, string Type, string? Text, bool HasFiles, bool HasAgents,
    bool HasSkills, bool ReferencesComplete, OpenCodeExecutionMessageEvidence Execution, string Fingerprint)
{
    public bool HasNonTextReferences => HasFiles || HasAgents || HasSkills;
}
public sealed record SessionControlHistoryPage(ImmutableArray<SessionControlHistoryMessage> Messages, string? Next);
// SessionNotFound preserves the public tag; it can reflect session/location lookup, not a bad model.
public enum SessionControlErrorKind { Unclassified, InvalidRequest, Unauthorized, SessionNotFound, MessageNotFound, SessionBusy, LocationNotFound }
/// <summary>Allowlisted error identity only, never server message/body. Unknown post-start failures retain protection.</summary>
public sealed class OpenCodeSessionControlException : OpenCodeException
{
    internal OpenCodeSessionControlException(int status, SessionControlErrorKind kind, bool verifiedPreEffect)
        : base($"Session control HTTP {status} ({kind}).")
    { StatusCode = status; Kind = kind; VerifiedPreEffect = verifiedPreEffect; }
    public int StatusCode { get; }
    public SessionControlErrorKind Kind { get; }
    public bool VerifiedPreEffect { get; }
}

/// <summary>Strict bounded-control projections; no model headers/settings or attachment URI material is retained.</summary>
internal static class SessionControlJson
{
    // Dedicated to history. Preserve unrelated shared serializer behavior; never let case aliases
    // change the discriminant/identity or nested mapped execution fields read with exact JSON names.
    private static readonly JsonSerializerOptions HistoryOptions = new(OpenCodeJson.Options)
    {
        PropertyNameCaseInsensitive = false,
    };
    internal static OpenCodeProtocolException Invalid() => new("Malformed session-control response.");
    internal static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject()) if (!names.Add(item.Name)) throw Invalid();
        return value;
    }
    internal static JsonElement Required(JsonElement value, string name)
        => Object(value).TryGetProperty(name, out var property) ? property : throw Invalid();
    internal static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
    internal static string Nonempty(JsonElement value)
        => Text(value) is { } text && !string.IsNullOrWhiteSpace(text) ? text : throw Invalid();
    internal static string? OptionalText(JsonElement value, string name)
        => Object(value).TryGetProperty(name, out var item) ? Nonempty(item) : null;
    internal static JsonElement Array(JsonElement value)
        => value.ValueKind == JsonValueKind.Array ? value : throw Invalid();
    internal static bool Boolean(JsonElement value)
        => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid();
    internal static double Number(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Invalid();
    internal static long Integer(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw Invalid();
    internal static void Id(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !NotificationArgumentCodec.IsValidRequestId(value)) throw Invalid();
    }
    internal static string Directory(JsonElement root) => Nonempty(Required(Required(root, "location"), "directory"));

    internal static SessionControlModelRef ModelRef(JsonElement value)
    {
        Object(value);
        return new(Nonempty(Required(value, "id")), Nonempty(Required(value, "providerID")), OptionalText(value, "variant"));
    }
    internal static SessionControlModel Model(JsonElement value)
    {
        var reference = ModelRef(value);
        var modelId = Nonempty(Required(value, "modelID")); // Provider-facing ID is not the catalog alias.
        var name = Nonempty(Required(value, "name"));
        var capabilities = Required(value, "capabilities");
        Boolean(Required(capabilities, "tools"));
        foreach (var direction in new[] { "input", "output" })
            foreach (var modality in Array(Required(capabilities, direction)).EnumerateArray()) Nonempty(modality);
        var variants = ImmutableArray.CreateBuilder<string>();
        foreach (var variant in Array(Required(value, "variants")).EnumerateArray())
        {
            var id = Nonempty(Required(variant, "id"));
            ModelMetadata(variant);
            if (variants.Contains(id)) throw Invalid();
            variants.Add(id);
        }
        Number(Required(Required(value, "time"), "released"));
        foreach (var cost in Array(Required(value, "cost")).EnumerateArray())
        {
            Number(Required(cost, "input")); Number(Required(cost, "output"));
            Number(Required(Required(cost, "cache"), "read")); Number(Required(Required(cost, "cache"), "write"));
            if (cost.TryGetProperty("tier", out var tier))
            { if (Text(Required(tier, "type")) != "context") throw Invalid(); Integer(Required(tier, "size")); }
        }
        if (Nonempty(Required(value, "status")) is not ("alpha" or "beta" or "deprecated" or "active")) throw Invalid();
        var enabled = Boolean(Required(value, "enabled"));
        var limit = Required(value, "limit");
        foreach (var field in new[] { "context", "output" })
            Integer(Required(limit, field));
        if (limit.TryGetProperty("input", out var input)) Integer(input);
        foreach (var optional in new[] { "canonical", "family", "package" })
            if (value.TryGetProperty(optional, out var item)) Text(item);
        if (value.TryGetProperty("compatibility", out var compatibility)) Object(compatibility);
        ModelMetadata(value);
        return new(reference.Id, modelId, reference.ProviderId, name, enabled, variants.ToImmutable(), Fingerprint(value));
    }
    private static void ModelMetadata(JsonElement value)
    {
        foreach (var optional in new[] { "settings", "body" })
            if (value.TryGetProperty(optional, out var item)) Object(item);
        if (value.TryGetProperty("headers", out var headers))
            foreach (var header in Object(headers).EnumerateObject()) Text(header.Value);
    }
    internal static SessionControlRevert Revert(JsonElement value)
    {
        var id = Nonempty(Required(value, "messageID")); Id(id, "msg_");
        var part = OptionalText(value, "partID");
        bool snapshot = value.TryGetProperty("snapshot", out var snapshotValue);
        if (snapshot) Text(snapshotValue);
        int files = 0;
        if (value.TryGetProperty("files", out var array))
            foreach (var file in Array(array).EnumerateArray())
            {
                Text(Required(file, "file")); Text(Required(file, "patch"));
                foreach (var count in new[] { "additions", "deletions" })
                    if (Integer(Required(file, count)) < 0) throw Invalid();
                if (Nonempty(Required(file, "status")) is not ("added" or "deleted" or "modified")) throw Invalid();
                files++;
            }
        return new(id, part, snapshot, files, Fingerprint(value));
    }
    internal static SessionControlState State(JsonElement root, string sessionId)
    {
        var value = Required(root, "data");
        if (Nonempty(Required(value, "id")) != sessionId) throw Invalid();
        Nonempty(Required(value, "projectID")); Number(Required(value, "cost"));
        Object(Required(value, "tokens"));
        Number(Required(Required(value, "time"), "created")); Number(Required(Required(value, "time"), "updated"));
        var model = value.TryGetProperty("model", out var m) ? ModelRef(m) : null;
        var revert = value.TryGetProperty("revert", out var r) ? Revert(r) : null;
        var directory = Directory(value);
        return new(sessionId, directory, model, revert, Hash(new { sessionId, directory, model, stage = revert?.Fingerprint }))
        {
            Title = value.TryGetProperty("title", out var title) && title.ValueKind != JsonValueKind.Null ? Text(title) : null,
            ParentId = OptionalText(value, "parentID"),
        };
    }

    internal static SessionControlHistoryPage History(JsonElement root, string sessionId, int limit)
    {
        var rows = Array(Required(root, "data"));
        if (rows.GetArrayLength() > limit) throw Invalid();
        var cursor = Object(Required(root, "cursor"));
        if (cursor.TryGetProperty("previous", out var previous) && previous.ValueKind != JsonValueKind.Null) Nonempty(previous);
        string? next = cursor.TryGetProperty("next", out var nextValue) && nextValue.ValueKind != JsonValueKind.Null ? Nonempty(nextValue) : null;
        var result = ImmutableArray.CreateBuilder<SessionControlHistoryMessage>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Nonempty(Required(row, "id")); Id(id, "msg_");
            var type = Nonempty(Required(row, "type"));
            var text = type == "user" ? Text(Required(row, "text")) : null;
            var created = Number(Required(Required(row, "time"), "created"));
            RejectHistoryCaseAliases(row, typeof(OpenCodeMessage));
            var message = row.Deserialize<OpenCodeMessage>(HistoryOptions) ?? throw Invalid();
            if (message.Id != id || message.Type != type || message.Time?.Created != created) throw Invalid();
            var execution = OpenCodeExecutionProjection.CreateWindow(sessionId, new[] { message }, true, true, true);
            if (execution.Messages.Length != 1 || execution.Messages[0].Id != id
                || execution.Messages[0].Type != type || execution.Messages[0].Created != created) throw Invalid();
            bool complete = execution.IsComplete;
            bool files = References(row, "files", "mime", ref complete);
            bool agents = References(row, "agents", "name", ref complete);
            bool skills = References(row, "skills", "id", ref complete);
            if (type == "user")
                foreach (var property in row.EnumerateObject())
                    if (property.Name is not ("id" or "type" or "time" or "metadata" or "text" or "files" or "agents" or "skills"))
                        complete = false; // A future/unknown input carrier cannot be called reference-free.
            // Unknown message variants cannot establish a complete delivered-history projection.
            if (type is not ("user" or "assistant" or "idle" or "synthetic" or "system" or "skill" or "shell"
                or "compaction" or "agent-switched" or "model-switched" or "location-switched")) complete = false;
            result.Add(new(id, type, text, files, agents, skills, complete, execution.Messages[0], Fingerprint(row)));
        }
        return new(result.ToImmutable(), next);
    }
    private static void RejectHistoryCaseAliases(JsonElement value, Type mappedType)
    {
        mappedType = Nullable.GetUnderlyingType(mappedType) ?? mappedType;
        if (mappedType == typeof(JsonElement)) return; // Opaque metadata/state/provider payloads keep exact-key semantics.
        if (value.ValueKind == JsonValueKind.Array && mappedType.IsGenericType
            && mappedType.GetGenericTypeDefinition() == typeof(List<>))
        {
            foreach (var item in value.EnumerateArray()) RejectHistoryCaseAliases(item, mappedType.GetGenericArguments()[0]);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var property in value.EnumerateObject())
            foreach (var mapped in mappedType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = mapped.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                if (name is null || !string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Name != name) throw Invalid();
                RejectHistoryCaseAliases(property.Value, mapped.PropertyType);
                break;
            }
    }
    private static bool References(JsonElement row, string name, string key, ref bool complete)
    {
        if (!row.TryGetProperty(name, out var value)) return false;
        if (value.ValueKind != JsonValueKind.Array) { complete = false; return true; }
        foreach (var reference in value.EnumerateArray())
        {
            if (reference.ValueKind != JsonValueKind.Object || !reference.TryGetProperty(key, out var item)
                || item.ValueKind != JsonValueKind.String) { complete = false; continue; }
            if (name == "files")
            {
                if (!reference.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String
                    || !reference.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object
                    || !source.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                { complete = false; continue; }
                if (type.GetString() != "inline" && (type.GetString() != "uri" || !source.TryGetProperty("uri", out var uri)
                    || uri.ValueKind != JsonValueKind.String)) complete = false;
            }
            if (name == "skills" && (!reference.TryGetProperty("name", out var skillName) || skillName.ValueKind != JsonValueKind.String)) complete = false;
        }
        return value.GetArrayLength() != 0;
    }

    internal static string Hash<T>(T value) => Fingerprint(JsonSerializer.SerializeToElement(value));
    internal static string Fingerprint(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Canonical(writer, value);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            Object(value);
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); Canonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}
