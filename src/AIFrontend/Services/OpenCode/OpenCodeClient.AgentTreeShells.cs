using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

// Verified against the 2.1.1 Shell.Info contract. Session ownership is metadata.sessionID,
// not cwd (several sessions can share a location). No shell creation/control here.
public sealed record OpenCodeTreeShellInfo
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("command")] public string? Command { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("exit")] public double? Exit { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

public sealed record OpenCodeTreeShellsResponse
{
    [JsonPropertyName("data")] public List<OpenCodeTreeShellInfo>? Data { get; init; }
}

public sealed partial class OpenCodeClient
{
    public async Task<OpenCodeTreeShellsResponse> ListTreeShellsAsync(string directory, CancellationToken ct)
    {
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/shell", TreeShellLocation(directory), null, ct).ConfigureAwait(false);
        var response = Deserialize<OpenCodeTreeShellsResponse>(document, "GET api/shell");
        if (response.Data is null) throw new OpenCodeProtocolException("响应缺少 data 字段（GET api/shell）。");
        return response;
    }

    public async Task<OpenCodeTreeShellInfo> GetTreeShellAsync(string id, string directory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/shell/" + Uri.EscapeDataString(id), TreeShellLocation(directory), null, ct).ConfigureAwait(false);
        return RequireData<OpenCodeTreeShellInfo>(document, "GET api/shell/{id}");
    }

    public async Task<OpenCodeShellOutput> ReadTreeShellOutputAsync(string id, string directory, long cursor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var query = TreeShellLocation(directory);
        query.Add(new("cursor", cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        query.Add(new("limit", "65536"));
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/shell/" + Uri.EscapeDataString(id) + "/output", query, null, ct).ConfigureAwait(false);
        return RequireData<OpenCodeShellOutput>(document, "GET api/shell/{id}/output");
    }

    private static List<KeyValuePair<string, string>> TreeShellLocation(string directory)
        => new() { new("location[directory]", directory) };
}
