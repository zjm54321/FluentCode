using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>已知项目原始数组；不将 Git 项目身份当作已添加的目录，也不创建项目。</summary>
    public async Task<IReadOnlyList<OpenCodeProjectInfo>> GetProjectsAsync(
        bool? archived = null, CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/project";
        var query = new List<KeyValuePair<string, string>>();
        if (archived is { } includeArchived)
            query.Add(new("archived", includeArchived ? "true" : "false"));
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query, null, cancellationToken).ConfigureAwait(false);
        var projects = Deserialize<List<OpenCodeProjectInfo>>(document, "GET api/project");
        foreach (var project in projects)
        {
            if (project is null || string.IsNullOrWhiteSpace(project.Id) || string.IsNullOrWhiteSpace(project.Canonical)
                || project.Sandboxes is null || project.Time is null
                || project.Time.Created is not >= 0 || project.Time.Updated is not >= 0 || project.Time.Active is not >= 0
                || project.Time.Archived is < 0 || project.Sandboxes.Exists(path => path is null))
                throw new OpenCodeProtocolException("项目响应缺少必填字段或字段无效（GET api/project）。");
        }
        return projects;
    }

    /// <summary>
    /// 只读远程目录直接子项；path 可为绝对路径或相对于 location 的路径。
    /// 返回的 entry.Path 始终相对于响应 location；不在本机解析，不切换 location。
    /// </summary>
    public async Task<OpenCodeFileSystemResponse> GetFileSystemAsync(
        string? directory, string? path = null, CancellationToken cancellationToken = default)
    {
        const string endpoint = "api/fs/list";
        var query = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(directory)) query.Add(new("location[directory]", directory));
        if (path is not null) query.Add(new("path", path));
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, query, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeFileSystemResponse>(document, "GET api/fs/list");
        if (string.IsNullOrWhiteSpace(response.Location?.Directory) || response.Data is null)
            throw new OpenCodeProtocolException("文件系统响应缺少 location/data（GET api/fs/list）。");
        foreach (var entry in response.Data)
            if (entry is null || entry.Path is null || entry.Type is not ("file" or "directory"))
                throw new OpenCodeProtocolException("文件系统条目缺少 path/type 或类型无效（GET api/fs/list）。");
        return response;
    }

    /// <summary>
    /// 首次发送时一次性创建并设置 agent/model/variant；不改全局默认，不发创建后的切换请求。
    /// 调用时机、源/草稿所有权、一次发送保护由调用者负责；不自动重试。
    /// null 偏好省略，让服务器采用该目录的默认值。
    /// </summary>
    public async Task<OpenCodeSessionInfo> CreateSessionConfiguredAsync(
        string? directory, string? agent, OpenCodeModelRef? model, CancellationToken cancellationToken = default)
    {
        if (agent is not null) ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        if (model is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model.Id, nameof(model));
            ArgumentException.ThrowIfNullOrWhiteSpace(model.ProviderId, nameof(model));
        }
        var body = new ConfiguredSessionBody(
            string.IsNullOrEmpty(directory) ? null : new OpenCodeLocationRef { Directory = directory }, agent, model);
        using var content = CreateJsonContent(body);
        using var document = await SendForJsonAsync(HttpMethod.Post, "api/session", null, content, cancellationToken).ConfigureAwait(false);
        var session = RequireData<OpenCodeSessionInfo>(document, "POST api/session");
        if (string.IsNullOrWhiteSpace(session.Id))
            throw new OpenCodeProtocolException("响应缺少会话 id（POST api/session）。");
        return session;
    }
}

internal sealed record ConfiguredSessionBody(
    [property: JsonPropertyName("location")] OpenCodeLocationRef? Location,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("model")] OpenCodeModelRef? Model);
