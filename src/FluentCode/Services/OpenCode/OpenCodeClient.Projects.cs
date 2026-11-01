using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>Read UTF-8 text from the remote session location using fs.read, bounded to 64 MiB.</summary>
    public async Task<string> ReadTextFileAsync(string directory, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var relative = RelativeTextFilePath(directory, path);
        var segments = relative.Split('/');
        for (var i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
        var endpoint = "api/fs/read/" + string.Join('/', segments);
        var query = new[] { new KeyValuePair<string, string>("location[directory]", directory) };
        const string label = "GET api/fs/read/{path}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, endpoint, query, null, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenCodeApiException((int)response.StatusCode, null, null,
                    $"无法读取：OpenCode 服务返回 HTTP {(int)response.StatusCode}（{label}）。");
            var bytes = await ReadResponseBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var text = new UTF8Encoding(false, true).GetString(bytes.Span);
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("OpenCode 文件读取已取消。", cancellationToken); }
        catch (OperationCanceledException)
        { throw new OpenCodeTimeoutException($"无法读取：OpenCode 请求超时（{label}）。"); }
        catch (DecoderFallbackException)
        { throw new OpenCodeProtocolException("无法读取：文件不是有效的 UTF-8 文本。"); }
        catch (HttpRequestException)
        { throw new OpenCodeTransportException($"无法读取：OpenCode 请求或响应读取失败（{label}）。"); }
        catch (IOException)
        { throw new OpenCodeTransportException($"无法读取：OpenCode 响应读取失败（{label}）。"); }
    }

    // Remote lexical paths only; never resolve a server path through the local Windows filesystem.
    internal static string RelativeTextFilePath(string directory, string path)
    {
        var root = directory.Replace('\\', '/').TrimEnd('/');
        var relative = path.Replace('\\', '/');
        var windows = root.Length > 1 && root[1] == ':';
        if (relative.StartsWith('/') || relative.Length > 1 && relative[1] == ':')
        {
            var prefix = root + "/";
            if (!relative.StartsWith(prefix, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("无法读取：指令文件位于会话目录之外。", nameof(path));
            relative = relative[prefix.Length..];
        }
        if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") throw InvalidTextPath();
            foreach (var ch in segment)
                if (char.IsControl(ch) || ch == ':') throw InvalidTextPath();
        }
        return relative;

        static ArgumentException InvalidTextPath() => new("无法读取：指令文件路径无效。", "path");
    }

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
    /// 服务器文件索引递归排序搜索（GET api/fs/find，operationId fs.find）；返回的 entry.Path 相对于响应 location。
    /// type 省略时文件与目录都返回；limit 按线格式以字符串发送。只读，不在本机解析路径。
    /// </summary>
    public async Task<OpenCodeFileSystemResponse> FindFileSystemAsync(
        string? directory, string query, int? limit = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        const string endpoint = "api/fs/find";
        var parameters = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(directory)) parameters.Add(new("location[directory]", directory));
        parameters.Add(new("query", query));
        if (limit is { } max) parameters.Add(new("limit", max.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var document = await SendForJsonAsync(HttpMethod.Get, endpoint, parameters, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeFileSystemResponse>(document, "GET api/fs/find");
        if (string.IsNullOrWhiteSpace(response.Location?.Directory) || response.Data is null)
            throw new OpenCodeProtocolException("文件搜索响应缺少 location/data（GET api/fs/find）。");
        foreach (var entry in response.Data)
            if (entry is null || entry.Path is null || entry.Type is not ("file" or "directory"))
                throw new OpenCodeProtocolException("文件搜索条目缺少 path/type 或类型无效（GET api/fs/find）。");
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
