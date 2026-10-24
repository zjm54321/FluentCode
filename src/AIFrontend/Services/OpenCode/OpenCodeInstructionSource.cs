using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

internal enum OpenCodeInstructionRoute { Text, Http, WslMapping, Unavailable }
internal sealed record OpenCodeInstructionReadPlan(OpenCodeInstructionRoute Route, string Value, string? Reason = null);

/// <summary>Logical instruction keys are not filenames. Only an authorized WSL binding can read outside location.</summary>
internal static class OpenCodeInstructionSource
{
    internal const int MaximumBytes = 64 * 1024 * 1024;

    internal static string? BoundedBody(string? body)
        => !string.IsNullOrWhiteSpace(body) && Encoding.UTF8.GetByteCount(body) <= MaximumBytes ? body : null;

    internal static OpenCodeInstructionReadPlan Resolve(string? body, string directory, string source, bool wsl)
    {
        if (BoundedBody(body) is { } text) return new(OpenCodeInstructionRoute.Text, text);
        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaximumBytes)
            return new(OpenCodeInstructionRoute.Unavailable, source, "指令正文超过 64 MiB 查看上限。");
        if (source.StartsWith("core/", StringComparison.Ordinal))
            return new(OpenCodeInstructionRoute.Unavailable, source, "这是系统指令来源标识，不是文件路径；此通知未提供指令正文。");
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(source) || source.Contains("://", StringComparison.Ordinal))
            return new(OpenCodeInstructionRoute.Unavailable, source, "会话目录或指令文件路径无效。");
        try { return new(OpenCodeInstructionRoute.Http, OpenCodeClient.RelativeTextFilePath(directory, source)); }
        catch (ArgumentException)
        {
            // Only absolute Linux paths can cross the WSL local-byte boundary, never traversal or a host URL.
            if (source.StartsWith('/'))
            {
                foreach (var segment in source.Split('/'))
                    if (segment is "." or "..") return Invalid(source);
                foreach (var ch in source)
                    if (char.IsControl(ch) || ch is '\\' or ':') return Invalid(source);
                return wsl ? new(OpenCodeInstructionRoute.WslMapping, source)
                    : new(OpenCodeInstructionRoute.Unavailable, source, "此文件不在会话目录内，远程连接无法读取。");
            }
            return Invalid(source);
        }
    }

    private static OpenCodeInstructionReadPlan Invalid(string source)
        => new(OpenCodeInstructionRoute.Unavailable, source, "指令文件路径无效，无法读取。");

    internal static async Task<string> ReadLocalUtf8Async(string hostPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Same bounded local-byte rule as browser preview: length check plus a streaming growth bound.
        await using var input = new FileStream(hostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        if (input.Length > MaximumBytes) throw new IOException("指令文件超过 64 MiB 查看上限。");
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length > MaximumBytes - count) throw new IOException("指令文件超过 64 MiB 查看上限。");
            output.Write(buffer, 0, count);
        }
        var text = new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    internal static string ReadFailure(Exception error) => error switch
    {
        OpenCodeApiException { StatusCode: 404 } => "未找到此指令文件，或服务器不允许读取此路径。",
        OpenCodeApiException { StatusCode: 401 or 403 } => "当前连接没有读取此指令文件的权限。",
        FileNotFoundException or DirectoryNotFoundException => "此指令文件已不存在，无法读取。",
        UnauthorizedAccessException => "没有读取此指令文件的权限。",
        DecoderFallbackException => "此指令文件不是有效的 UTF-8 文本。",
        IOException => "无法读取指令文件：" + error.Message,
        OpenCodeTimeoutException => "读取指令文件超时。",
        OpenCodeTransportException => "连接中断，无法读取指令文件。",
        _ => "无法读取此指令文件。",
    };
}
