using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>GET api/command；保留旧调用的服务端默认位置，不合并本地 UI 快捷命令。</summary>
    public Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(CancellationToken cancellationToken = default)
        => GetCommandsCoreAsync(null, cancellationToken);

    /// <summary>GET api/command；用公开的 location[directory] 读取指定目录，非法目录项忽略，不回退到默认位置。</summary>
    // Both arguments are required so legacy GetCommandsAsync(default) remains unambiguous.
    public Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return GetCommandsCoreAsync(directory, cancellationToken);
    }

    private async Task<IReadOnlyList<OpenCodeCommandInfo>> GetCommandsCoreAsync(string? directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = await SendForJsonAsync(HttpMethod.Get, "api/command",
            CapabilityLocationQuery(directory), null, cancellationToken).ConfigureAwait(false);
        var data = RequireData<List<JsonElement>>(document, "GET api/command");
        var commands = new List<OpenCodeCommandInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data)
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !IsValidCommandName(name.GetString())) continue;
            string? description = null;
            if (item.TryGetProperty("description", out var value))
            {
                if (value.ValueKind != JsonValueKind.String) continue;
                description = value.GetString();
            }
            if (names.Add(name.GetString()!)) commands.Add(new(name.GetString()!, description, ReadCommandHints(item)));
        }
        return commands.AsReadOnly();
    }

    internal const int MaxCommandHints = 16;
    internal const int MaxCommandHintLength = 32;

    /// <summary>
    /// Optional argument metadata. Absent / null / malformed (non-array, non-string or invalid entries, too many) → null
    /// ("unknown", never "no arguments"): a bad hints value never drops the command and never proves argument-free.
    /// Only a well-formed array — possibly empty — is returned.
    /// </summary>
    private static IReadOnlyList<string>? ReadCommandHints(JsonElement item)
    {
        if (!item.TryGetProperty("hints", out var hints) || hints.ValueKind != JsonValueKind.Array
            || hints.GetArrayLength() > MaxCommandHints) return null;
        var list = new List<string>(hints.GetArrayLength());
        foreach (var hint in hints.EnumerateArray())
        {
            if (hint.ValueKind != JsonValueKind.String || hint.GetString() is not { Length: > 0 and <= MaxCommandHintLength } text
                || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return null;
            list.Add(text);
        }
        return list.AsReadOnly();
    }

    /// <summary>
    /// POST api/session/{sessionID}/command；204 只确认服务器 callback 执行，不证明模型完成。
    /// text 省略时发送空字符串以兼容必填 text 的 V2 快照。无自动重试。
    /// 与 prompt 相同地保留 API/transport/timeout/protocol 异常；超时、断连、取消或畸形回执
    /// 均不能证明 mutation 失败，上层必须保留未知结果保护，不能自动重新提交。
    /// </summary>
    public async Task ExecuteCommandAsync(string sessionId, string name, string? text = null,
        IReadOnlyList<PromptFileAttachment>? files = null, CancellationToken cancellationToken = default)
        => await ExecuteCommandGuardedAsync(sessionId, name, text, files, action => { action(); return true; }, cancellationToken).ConfigureAwait(false);

    // Validation/serialization precede the synchronous source-owned transport starter.
    internal async Task ExecuteCommandGuardedAsync(string sessionId, string name, string? text,
        IReadOnlyList<PromptFileAttachment>? files, Func<Action, bool> starter, CancellationToken cancellationToken)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        if (!IsValidCommandName(name)) throw new ArgumentException("命令名必须为不含空白、控制字符或反斜杠的名称（最多 256 字符）；正斜杠分隔的各段不得为空、. 或 ..。", nameof(name));
        var body = new OpenCodeCommandBody(name, text ?? string.Empty, OpenCodeAttachment.ValidateFiles(files));
        var bytes = OpenCodeAttachment.SerializeRequest(body);
        cancellationToken.ThrowIfCancellationRequested();
        using var content = CreateJsonContent(bytes);
        // Existing no-content transport provides the same bounded timeout and sanitized error classes.
        Task? pending = null;
        if (!starter(() => pending = SendForNoContentAsync(HttpMethod.Post,
            "api/session/" + Uri.EscapeDataString(sessionId) + "/command", null, content, cancellationToken)))
            throw new OperationCanceledException(cancellationToken);
        await (pending ?? throw new InvalidOperationException("Transport starter did not execute.")).ConfigureAwait(false);
    }

    private static bool IsValidCommandName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) return false;
        foreach (var ch in name)
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\') return false;
        // Upstream config/plugin/command.ts normalizes relative command-file names to e.g. nested/docs.
        // These are literal catalog keys carried in JSON, never URL route segments or local paths.
        foreach (var segment in name.Split('/'))
            if (segment is "" or "." or "..") return false;
        return true;
    }
}
