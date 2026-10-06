using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.Configuration;

/// <summary>
/// 同目录临时文件 + 原子替换的写文件助手（内部使用）。
/// </summary>
/// <remarks>
/// 写入流程：创建同目录唯一临时文件 → 写入并 Flush 到磁盘 → <see cref="File.Move(string, string, bool)"/>
/// 原子替换目标文件。任一步失败都会清理临时文件并保留原文件。目标目录不存在时自动创建。
/// 残留的临时文件（进程被强杀等极端情况）命名形如 <c>*.tmp-{guid}</c>，不会被
/// 会话列表的 <c>*.json</c> 枚举匹配。
/// </remarks>
internal static class AtomicFile
{
    // 同一数据目录的所有服务/实例共享锁；不宣称跨进程或路径别名锁定。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    internal static SemaphoreSlim GateForDirectory(string directory) =>
        Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), static _ => new(1, 1));

    internal static async Task WriteAllBytesAsync(string filePath, byte[] contents, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>读取全部字节；文件（或所在目录）不存在返回 null，其余 IO 错误照常抛出。</summary>
    internal static async Task<byte[]?> ReadAllBytesOrNullAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // 清理失败不掩盖原始异常。
        }
    }
}
