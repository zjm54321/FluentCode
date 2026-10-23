using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    public async Task<OpenCodeSkillsResponse> ListSkillsAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/skill",
            new[] { new KeyValuePair<string, string>("location[directory]", directory) }, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeSkillsResponse>(document, "GET api/skill");
        if (string.IsNullOrWhiteSpace(response.Location?.Directory) || response.Data is null)
            throw new OpenCodeProtocolException("Skill 响应缺少 location/data（GET api/skill）。");
        foreach (var skill in response.Data)
            if (skill is null || string.IsNullOrWhiteSpace(skill.Id) || string.IsNullOrWhiteSpace(skill.Name)
                || string.IsNullOrWhiteSpace(skill.Path) || skill.Content is null)
                throw new OpenCodeProtocolException("Skill 响应缺少必填字段（GET api/skill）。");
        return response;
    }
}

public sealed record OpenCodeSkillInfo
{
    [JsonRequired, JsonPropertyName("id")] public string? Id { get; init; }
    [JsonRequired, JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonRequired, JsonPropertyName("path")] public string? Path { get; init; }
    [JsonRequired, JsonPropertyName("content")] public string? Content { get; init; }
    public override string ToString() => "OpenCodeSkillInfo { [redacted] }";
}

public sealed record OpenCodeSkillsResponse
{
    [JsonRequired, JsonPropertyName("location")] public OpenCodeLocationRef? Location { get; init; }
    [JsonRequired, JsonPropertyName("data")] public List<OpenCodeSkillInfo>? Data { get; init; }
    public override string ToString() => "OpenCodeSkillsResponse { [redacted] }";
}

// Composer-owned mention identity and UTF-16 span. Never inferred from arbitrary @text or a file path.
internal sealed record OpenCodePromptContext(string Id, bool IsSkill, int Start)
{
    internal int End => Start + Id.Length + 1;
    internal bool Matches(string text) => Start >= 0 && End <= text.Length
        && text.AsSpan(Start, End - Start).SequenceEqual(("@" + Id).AsSpan());
    public override string ToString() => "OpenCodePromptContext { [redacted] }";
}
