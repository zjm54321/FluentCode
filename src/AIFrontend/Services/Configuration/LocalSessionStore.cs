using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Services.Configuration;

/// <summary>
/// 本地会话持久化：每个会话一个 <c>sessions\{sessionId}.json</c> 文件。
/// </summary>
/// <remarks>
/// 约定（重要）：
/// <list type="bullet">
/// <item><description>保存为原子替换；保存时强制刷新 <see cref="LocalSession.UpdatedAtUtc"/>（UTC now），
/// 返回实际写入的快照；<see cref="LocalSession.CreatedAtUtc"/> 为默认值时一并补为当前时间。</description></item>
/// <item><description><see cref="ListAsync"/> 按 UpdatedAtUtc 降序（再按 Id 升序）返回全部会话；
/// 任一文件损坏即抛 <see cref="LocalSessionStoreException"/>（不静默跳过、不改动原文件）。</description></item>
/// <item><description>会话文件内 <see cref="LocalSession.Id"/> 必须与文件名一致，否则视为损坏。</description></item>
/// </list>
/// 并发：同一数据目录内的所有服务实例共享信号量；跨进程并发不在保障范围。
/// </remarks>
public sealed class LocalSessionStore
{
    private readonly SemaphoreSlim _gate;
    private readonly string _dataDirectory;

    /// <summary>创建存储。</summary>
    /// <param name="dataDirectory">数据根目录；null 使用默认（<c>%LOCALAPPDATA%\AIFrontend</c>）。</param>
    public LocalSessionStore(string? dataDirectory = null)
    {
        _dataDirectory = AppDataPaths.GetRootDirectory(dataDirectory);
        _gate = AtomicFile.GateForDirectory(_dataDirectory);
    }

    /// <summary>数据根目录（绝对路径）。</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>列出全部会话（UpdatedAtUtc 降序，其次 Id 升序）。目录不存在时返回空列表。</summary>
    public async Task<IReadOnlyList<LocalSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        string directory = AppDataPaths.GetSessionsDirectory(_dataDirectory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<LocalSession>();
            }

            var sessions = new List<LocalSession>();
            foreach (string filePath in Directory.EnumerateFiles(directory, "*.json"))
            {
                sessions.Add(await ReadSessionFileAsync(filePath, cancellationToken).ConfigureAwait(false));
            }

            sessions.Sort(static (a, b) =>
            {
                int byTime = b.UpdatedAtUtc.CompareTo(a.UpdatedAtUtc);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
            });

            return sessions;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>加载单个会话；不存在返回 null；内容损坏抛 <see cref="LocalSessionStoreException"/>。</summary>
    /// <param name="sessionId">会话 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'）。</param>
    public async Task<LocalSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        AppDataPaths.ValidateStorageId(sessionId, nameof(sessionId));
        string filePath = AppDataPaths.GetSessionFilePath(_dataDirectory, sessionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? bytes = await AtomicFile.ReadAllBytesOrNullAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            return ParseSession(bytes, filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>保存会话（原子写入）。返回实际写入的快照（UpdatedAtUtc 已刷新为当前 UTC）。</summary>
    /// <param name="session">会话快照；<see cref="LocalSession.Id"/> 必须已设置且合法。</param>
    public async Task<LocalSession> SaveAsync(LocalSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        AppDataPaths.ValidateStorageId(session.Id, nameof(session));
        ValidateSessionContent(session, filePath: null);

        // 分离调用方集合，避免等待锁期间继续读取 UI 的列表。
        LocalSession snapshot = ConfigurationJson.Deserialize<LocalSession>(ConfigurationJson.SerializeToUtf8Bytes(session));
        string filePath = AppDataPaths.GetSessionFilePath(_dataDirectory, snapshot.Id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? existing = await AtomicFile.ReadAllBytesOrNullAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                _ = ParseSession(existing, filePath);
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            LocalSession persisted = snapshot with
            {
                CreatedAtUtc = snapshot.CreatedAtUtc == default ? now : snapshot.CreatedAtUtc,
                UpdatedAtUtc = now,
            };
            byte[] bytes = ConfigurationJson.SerializeToUtf8Bytes(persisted);
            await AtomicFile.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
            return persisted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除会话文件；返回是否实际删除（文件不存在返回 false，不算错误）。</summary>
    /// <param name="sessionId">会话 ID（1-64 字符，仅字母/数字/'.'/'_'/'-'）。</param>
    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        AppDataPaths.ValidateStorageId(sessionId, nameof(sessionId));
        string filePath = AppDataPaths.GetSessionFilePath(_dataDirectory, sessionId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? existing = await AtomicFile.ReadAllBytesOrNullAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return false;
            }

            _ = ParseSession(existing, filePath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(filePath);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<LocalSession> ReadSessionFileAsync(string filePath, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return ParseSession(bytes, filePath);
    }

    private static LocalSession ParseSession(byte[] utf8Bytes, string filePath)
    {
        LocalSession session;
        try
        {
            session = ConfigurationJson.Deserialize<LocalSession>(utf8Bytes);
        }
        catch (JsonException ex)
        {
            throw new LocalSessionStoreException($"会话文件不是有效的 JSON 或结构不匹配：{filePath}", filePath, ex);
        }

        string expectedId = Path.GetFileNameWithoutExtension(filePath);
        if (!string.Equals(session.Id, expectedId, StringComparison.Ordinal))
        {
            throw new LocalSessionStoreException(
                $"会话文件内的 Id（'{session.Id}'）与文件名（'{expectedId}'）不一致：{filePath}", filePath);
        }

        ValidateSessionContent(session, filePath);
        return session;
    }

    private static void ValidateSessionContent(LocalSession session, string? filePath)
    {
        if (!AppDataPaths.IsValidStorageId(session.Id))
        {
            throw SessionError($"会话 Id 非法：'{session.Id}'", filePath);
        }

        if (session.Messages is null)
        {
            throw SessionError($"会话消息列表不能为 null：{session.Id}", filePath);
        }

        foreach (ChatMessage? message in session.Messages)
        {
            if (message is null)
            {
                throw SessionError($"会话含 null 消息：{session.Id}", filePath);
            }

            if (!Enum.IsDefined(message.Role))
            {
                throw SessionError($"消息 role 取值非法：{(int)message.Role}（会话 {session.Id}）", filePath);
            }

            if (message.Content is null)
            {
                throw SessionError($"消息 content 不能为 null（会话 {session.Id}）", filePath);
            }

            if (message.Role == ChatRole.Tool && string.IsNullOrEmpty(message.ToolCallId))
            {
                throw SessionError($"工具消息缺少 toolCallId（会话 {session.Id}）", filePath);
            }

            if (message.ToolCalls is not null)
            {
                foreach (ToolCall? toolCall in message.ToolCalls)
                {
                    if (toolCall is null ||
                        string.IsNullOrEmpty(toolCall.CallId) ||
                        string.IsNullOrEmpty(toolCall.Name) ||
                        !Enum.IsDefined(toolCall.Status))
                    {
                        throw SessionError($"工具调用缺少 callId/name 或 status 非法（会话 {session.Id}）", filePath);
                    }
                }
            }
        }

        if (session.Model is { } model &&
            (string.IsNullOrWhiteSpace(model.ProviderId) || string.IsNullOrWhiteSpace(model.ModelId)))
        {
            throw SessionError($"会话模型引用非法（会话 {session.Id}）", filePath);
        }
    }

    private static LocalSessionStoreException SessionError(string message, string? filePath) =>
        new(message, filePath);
}
