using System;

namespace FluentCode.Services.OpenCode;

/// <summary>Bounded image bytes and signature-verified raster media type, not a decoded bitmap.</summary>
public sealed record OpenCodeImageFile(byte[] Bytes, string MediaType);

/// <summary>Lexical validation only; never resolves a local Windows/WSL path or downloads a URL.</summary>
public static class OpenCodeImageReference
{
    /// <summary>
    /// Accepts a relative Markdown file reference (optionally starting ./), decodes URI escapes
    /// once, and rejects ambiguous/double escapes, schemes, roots, traversal and URL suffixes.
    /// The returned path is decoded; transport must escape each segment before sending it.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath)) throw Invalid();
        if (relativePath.StartsWith("./", StringComparison.Ordinal)) relativePath = relativePath[2..];
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            for (var j = 0; j < segment.Length; j++)
            {
                if (segment[j] != '%') continue;
                if (j + 2 >= segment.Length || !Uri.IsHexDigit(segment[j + 1]) || !Uri.IsHexDigit(segment[j + 2]))
                    throw Invalid();
                j += 2;
            }
            segment = Uri.UnescapeDataString(segment);
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..") throw Invalid();
            foreach (var ch in segment)
                if (char.IsControl(ch) || ch is '/' or '\\' or ':' or '?' or '#' or '%') throw Invalid();
            segments[i] = segment;
        }
        return string.Join('/', segments);
    }

    internal static string EscapeRelativePath(string relativePath)
    {
        var segments = NormalizeRelativePath(relativePath).Split('/');
        for (var i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
        return string.Join('/', segments);
    }

    private static ArgumentException Invalid() => new(
        "Image reference must be a relative file path without a scheme, root, traversal, controls, query, fragment or ambiguous escapes.",
        "relativePath");
}
