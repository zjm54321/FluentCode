using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace FluentCode.Services.OpenCode.Browser;

public enum OpenCodeBrowserPreviewKind { Source, Markdown, Mermaid, Html, Image, Svg, Csv, Tsv, Pdf, Audio, Video, Font }
public enum OpenCodeBrowserPreviewRoute { Http, Unavailable }
public sealed record OpenCodeBrowserPreviewReadPlan(OpenCodeBrowserPreviewRoute Route, string Directory, string Path, string? Reason = null);
public sealed record OpenCodeBrowserPreviewContent(byte[] Bytes, string? Mime = null);
public sealed record OpenCodeBrowserPreviewTable(IReadOnlyList<string[]> Rows, int RemainingRows, bool CellsTruncated);

/// <summary>Pure preview policy: never interpret a server path as a desktop path.</summary>
public static class OpenCodeBrowserPreviewPolicy
{
    public const int TextLimit = 64 * 1024 * 1024;
    public const int MediaLimit = 5 * 1024 * 1024;
    public static string Extension(string path) => Path.GetExtension(path.Replace('\\', '/')).ToLowerInvariant();
    public static string Badge(string path) => "预览 · " + (Extension(path) is { Length: > 0 } ext ? ext : "文件");
    public static bool SamePath(string? first, string second) => string.Equals(first, second, StringComparison.Ordinal);
    public static bool IsText(OpenCodeBrowserPreviewKind kind) => kind is OpenCodeBrowserPreviewKind.Source or OpenCodeBrowserPreviewKind.Markdown
        or OpenCodeBrowserPreviewKind.Mermaid or OpenCodeBrowserPreviewKind.Html or OpenCodeBrowserPreviewKind.Svg or OpenCodeBrowserPreviewKind.Csv or OpenCodeBrowserPreviewKind.Tsv;
    public static int Limit(OpenCodeBrowserPreviewKind kind) => IsText(kind) && kind != OpenCodeBrowserPreviewKind.Svg ? TextLimit : MediaLimit;
    public static OpenCodeBrowserPreviewKind Detect(string path, string? mime = null)
    {
        mime = mime?.Split(';')[0].Trim().ToLowerInvariant();
        if (mime == "image/svg+xml") return OpenCodeBrowserPreviewKind.Svg;
        if (mime?.StartsWith("image/", StringComparison.Ordinal) == true) return OpenCodeBrowserPreviewKind.Image;
        if (mime?.StartsWith("audio/", StringComparison.Ordinal) == true) return OpenCodeBrowserPreviewKind.Audio;
        if (mime?.StartsWith("video/", StringComparison.Ordinal) == true) return OpenCodeBrowserPreviewKind.Video;
        if (mime?.StartsWith("font/", StringComparison.Ordinal) == true) return OpenCodeBrowserPreviewKind.Font;
        return mime switch
        {
            "text/markdown" => OpenCodeBrowserPreviewKind.Markdown, "text/html" => OpenCodeBrowserPreviewKind.Html,
            "text/csv" => OpenCodeBrowserPreviewKind.Csv, "text/tab-separated-values" => OpenCodeBrowserPreviewKind.Tsv,
            "application/pdf" => OpenCodeBrowserPreviewKind.Pdf,
            _ => Extension(path) switch
            {
                ".md" or ".markdown" or ".mdown" => OpenCodeBrowserPreviewKind.Markdown,
                ".mmd" or ".mermaid" => OpenCodeBrowserPreviewKind.Mermaid,
                ".html" or ".htm" => OpenCodeBrowserPreviewKind.Html,
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".avif" => OpenCodeBrowserPreviewKind.Image,
                ".svg" => OpenCodeBrowserPreviewKind.Svg, ".csv" => OpenCodeBrowserPreviewKind.Csv, ".tsv" => OpenCodeBrowserPreviewKind.Tsv,
                ".pdf" => OpenCodeBrowserPreviewKind.Pdf,
                ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" or ".aac" => OpenCodeBrowserPreviewKind.Audio,
                ".mp4" or ".webm" or ".mov" or ".m4v" or ".avi" => OpenCodeBrowserPreviewKind.Video,
                ".ttf" or ".otf" or ".woff" or ".woff2" => OpenCodeBrowserPreviewKind.Font,
                _ => OpenCodeBrowserPreviewKind.Source,
            }
        };
    }
    public static string Decode(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }
    /// <summary>Only an explicit preview request selects an fs.read location, never an asset URL.</summary>
    public static OpenCodeBrowserPreviewReadPlan Plan(string directory, string path)
    {
        if (!ValidPath(directory) || !ValidPath(path) || !Absolute(directory.Replace('\\', '/')) || path.EndsWith('/') || path.EndsWith('\\'))
            return new(OpenCodeBrowserPreviewRoute.Unavailable, directory, path, "服务器文件路径无效；不能使用网络地址、控制字符或 .. 路径");
        var resolved = Resolve(directory, path);
        var slash = resolved.LastIndexOf('/');
        if (slash < 0 || resolved[(slash + 1)..] is "" or ".")
            return new(OpenCodeBrowserPreviewRoute.Unavailable, directory, path, "预览路径必须指向文件");
        var parent = slash == 0 ? "/" : resolved[..slash];
        if (parent.Length == 2 && parent[1] == ':') parent += "/";
        return new(OpenCodeBrowserPreviewRoute.Http, parent, resolved[(slash + 1)..]);
    }
    public static OpenCodeBrowserPreviewReadPlan ResourcePlan(string directory, string previewPath, string resourcePath)
    {
        var preview = Plan(directory, previewPath);
        if (preview.Route == OpenCodeBrowserPreviewRoute.Unavailable) return preview;
        if (!ValidPath(resourcePath)) return new(OpenCodeBrowserPreviewRoute.Unavailable, preview.Directory, resourcePath, "资源路径无效或包含 ..");
        var resolved = Resolve(directory, resourcePath);
        var prefix = preview.Directory.TrimEnd('/') + "/";
        var comparison = preview.Directory.Length > 1 && preview.Directory[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(prefix, comparison) || resolved.Length <= prefix.Length)
            return new(OpenCodeBrowserPreviewRoute.Unavailable, preview.Directory, resourcePath, "资源路径超出明确预览文件的目录");
        // Keep the original location for nested assets: the server's realpath check then
        // also rejects symlinks that leave this directory. Never re-root at an asset's parent.
        return new(OpenCodeBrowserPreviewRoute.Http, preview.Directory, resolved[prefix.Length..]);
    }
    private static bool Absolute(string path) => path.StartsWith('/') || path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/';
    private static bool ValidPath(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains("://", StringComparison.Ordinal)
        && !path.Any(char.IsControl) && !path.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal);
    private static string Resolve(string directory, string path) => Normalize(Absolute(path.Replace('\\', '/')) ? path : directory.TrimEnd('/', '\\') + "/" + path);
    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return (path.StartsWith('/') ? "/" : "") + string.Join('/', parts);
    }

    public static string LocalSvg(string source)
    {
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "svg") throw new FormatException("文件不是 SVG 文档");
        foreach (var element in document.Descendants().ToArray())
        {
            if (element.Name.LocalName is "script" or "style" or "foreignObject") { element.Remove(); continue; }
            foreach (var attribute in element.Attributes().ToArray())
            {
                var value = attribute.Value.Trim();
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.LocalName is "href" or "src" && !value.StartsWith('#')
                    || value.Contains("url(", StringComparison.OrdinalIgnoreCase) && (!value.StartsWith("url(#", StringComparison.OrdinalIgnoreCase)
                        || value.IndexOf("url(", 4, StringComparison.OrdinalIgnoreCase) >= 0)) attribute.Remove();
            }
        }
        return document.ToString();
    }

    /// <summary>RFC-style quoted fields, escaped quotes and embedded newlines; bounded retained rows/cells.</summary>
    public static OpenCodeBrowserPreviewTable ParseTable(string text, char delimiter, int maxRows = 500, int maxColumns = 64, int maxCellChars = 16384)
    {
        if (maxRows < 1 || maxColumns < 1 || maxCellChars < 1) throw new ArgumentOutOfRangeException(nameof(maxRows));
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder();
        var quoted = false; var afterQuote = false; var fieldStart = true; var rowStarted = false; var truncated = false; var total = 0; var column = 0;
        void Character(char ch)
        {
            if (total < maxRows && column < maxColumns && cell.Length < maxCellChars) cell.Append(ch);
            else if (total < maxRows) truncated = true;
        }
        void Field()
        {
            if (total < maxRows && column < maxColumns) row.Add(cell.ToString());
            else if (total < maxRows) truncated = true;
            cell.Clear(); column++; fieldStart = true; afterQuote = false;
        }
        void Row() { Field(); if (total < maxRows) rows.Add(row.ToArray()); total++; row.Clear(); column = 0; rowStarted = false; }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { Character('"'); i++; }
                    else { quoted = false; afterQuote = true; }
                }
                else Character(ch);
                continue;
            }
            if (afterQuote && ch != delimiter && ch is not ('\r' or '\n')) throw new FormatException("引号字段后出现无效字符");
            if (ch == '"') { if (!fieldStart) throw new FormatException("字段中的引号必须转义"); quoted = true; fieldStart = false; rowStarted = true; }
            else if (ch == delimiter) { Field(); rowStarted = true; }
            else if (ch is '\r' or '\n') { if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; Row(); }
            else { Character(ch); fieldStart = false; rowStarted = true; }
        }
        if (quoted) throw new FormatException("引号字段尚未结束");
        if (rowStarted) Row();
        return new(rows, Math.Max(0, total - rows.Count), truncated);
    }
}
