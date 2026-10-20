using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AIFrontend.Services.OpenCode;

/// <summary>用户选定的字节；本类型不读取磁盘，调用方负责绑定 source/session 和异步读取代际。</summary>
public sealed record OpenCodeAttachmentDraft(string Name, string? Mime, byte[] Bytes);

/// <summary>纯附件验证与编码；任一项失败则整批失败，绝不读路径、下载 URL 或部分发送。</summary>
public static class OpenCodeAttachment
{
    public const int MaxFiles = 10;
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxTotalBytes = 20 * 1024 * 1024;
    public const int MaxRequestBytes = 28 * 1024 * 1024;

    /// <summary>先校验所有草稿再编码。总请求上限包含 JSON 转义及 Base64；发送时另计实际文本。</summary>
    public static IReadOnlyList<PromptFileAttachment> Encode(IReadOnlyList<OpenCodeAttachmentDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        ValidateCount(drafts.Count);
        var validated = new OpenCodeAttachmentDraft[drafts.Count];
        long total = 0;
        for (var i = 0; i < validated.Length; i++)
        {
            var draft = drafts[i];
            if (draft is null || draft.Bytes is null) throw Invalid("附件缺少文件字节。");
            ValidateBytes(draft.Bytes.Length, ref total);
            var name = FileName(draft.Name);
            if (name is null) throw Invalid("附件缺少文件名。");
            validated[i] = new(name, ResolveMime(draft.Mime, name), draft.Bytes);
        }

        var files = new PromptFileAttachment[validated.Length];
        for (var i = 0; i < files.Length; i++)
        {
            var draft = validated[i];
            files[i] = new("data:" + draft.Mime + ";base64," + Convert.ToBase64String(draft.Bytes), draft.Name);
        }
        ValidateRequestSize(new OpenCodeAttachedPromptBody(string.Empty, files));
        return Array.AsReadOnly(files);
    }

    // Revalidate public DTOs and snapshot the list at the final transport boundary, so callers
    // cannot bypass draft checks by constructing a file:, Windows-path, or http(s): attachment.
    internal static IReadOnlyList<PromptFileAttachment>? ValidateFiles(IReadOnlyList<PromptFileAttachment>? files)
    {
        if (files is null) return null;
        ValidateCount(files.Count);
        var snapshot = new PromptFileAttachment[files.Count];
        long total = 0;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var file = files[i];
            if (file is null || file.Uri is null || !file.Uri.StartsWith("data:", StringComparison.Ordinal))
                throw Invalid("附件仅支持内联 Base64 data URL；不支持本地路径、file: 或 HTTP(S)。");
            var separator = file.Uri.IndexOf(";base64,", StringComparison.Ordinal);
            if (separator < 5 || separator > 64) throw Invalid("附件 data URL 格式无效。");
            var mime = file.Uri[5..separator];
            if (!AllowedMime(mime)) throw Invalid("附件 MIME 类型不受支持。");
            var encoded = file.Uri.AsSpan(separator + 8);
            if (encoded.Length > 4 * ((MaxFileBytes + 2) / 3)) throw Invalid("单个附件不得超过 10 MiB。");
            // Do not accept whitespace, URL-escaped data, parameters, or non-canonical Base64.
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded.ToString()); }
            catch (FormatException) { throw Invalid("附件 Base64 数据无效。"); }
            ValidateBytes(bytes.Length, ref total);
            if (!encoded.SequenceEqual(Convert.ToBase64String(bytes).AsSpan())) throw Invalid("附件 Base64 数据必须为规范编码。");
            snapshot[i] = new(file.Uri, FileName(file.Name), file.Description);
        }
        return Array.AsReadOnly(snapshot);
    }

    internal static void ValidateRequestSize<T>(T body)
    {
        // Count the exact production serialization without allocating an unbounded JSON string.
        // The default encoder can expand '+' and non-ASCII text, not just the usual Base64 4/3.
        using var counter = new RequestSizeCounter();
        JsonSerializer.Serialize(counter, body, OpenCodeJson.Options);
    }

    private static void ValidateCount(int count)
    {
        if (count > MaxFiles) throw Invalid("每次最多发送 10 个附件。");
    }

    private static void ValidateBytes(int size, ref long total)
    {
        if (size > MaxFileBytes) throw Invalid("单个附件不得超过 10 MiB。");
        total += size;
        if (total > MaxTotalBytes) throw Invalid("附件原始字节总量不得超过 20 MiB。");
    }

    private static string? FileName(string? name)
    {
        if (name is null) return null;
        // Path.GetFileName alone is platform dependent; strip both Windows and Unix separators.
        name = name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1)..];
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':') name = name[2..];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains(':'))
            throw Invalid("附件文件名无效。");
        foreach (var ch in name)
            if (char.IsControl(ch)) throw Invalid("附件文件名不得包含控制字符。");
        return name;
    }

    private static string ResolveMime(string? mime, string name)
    {
        if (!string.IsNullOrWhiteSpace(mime))
        {
            mime = mime.Trim().ToLowerInvariant();
            if (AllowedMime(mime)) return mime;
            // Generic picker MIME is not evidence of a format; infer from an allowed extension.
            if (mime != "application/octet-stream") throw Invalid("附件 MIME 类型不受支持。");
        }
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".txt" => "text/plain",
            ".md" or ".markdown" => "text/markdown",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            _ => throw Invalid("无法从扩展名推断受支持的附件 MIME 类型。"),
        };
    }

    private static bool AllowedMime(string mime) => mime is
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" or
        "text/plain" or "text/markdown" or "application/pdf" or "application/json";

    private static ArgumentException Invalid(string reason) => new(reason, "files");

    private sealed class RequestSizeCounter : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _length += buffer.Length;
            if (_length > MaxRequestBytes) throw Invalid("含 Base64、文本及 JSON 转义的请求不得超过 28 MiB。");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
