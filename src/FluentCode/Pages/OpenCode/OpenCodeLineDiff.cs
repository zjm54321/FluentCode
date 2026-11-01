using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluentCode.Pages.OpenCode;

internal enum OpenCodeDiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    Meta,
}

/// <summary>统一 diff 的一行（旧/新行号可空）。</summary>
internal sealed record OpenCodeDiffLine(OpenCodeDiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>并排 diff 的一个单元格（左=旧，右=新）。</summary>
internal sealed record OpenCodeDiffCell(int? Number, string Text, OpenCodeDiffLineKind Kind);

/// <summary>并排 diff 的一行；Fold 行携带被折叠的未改动行，Hunk 行只有标题文本。</summary>
internal sealed class OpenCodeDiffRow
{
    public OpenCodeDiffCell? Left { get; init; }

    public OpenCodeDiffCell? Right { get; init; }

    public string? HunkText { get; init; }

    public IReadOnlyList<OpenCodeDiffRow>? Folded { get; init; }

    /// <summary>折叠行对应的统一视图行（展开时使用）。</summary>
    public IReadOnlyList<OpenCodeDiffLine>? FoldedUnified { get; init; }

    public bool IsFold => Folded is not null;

    public bool IsHunk => HunkText is not null;
}

/// <summary>
/// 纯本地的行级 diff：解析服务端统一 diff（metadata.files[].patch），或在无服务端数据时用 LCS 本地计算；
/// 并提供并排配对与长未改动段折叠。无 UI 依赖、无新包。
/// </summary>
internal static class OpenCodeLineDiff
{
    /// <summary>LCS 表大小上限（行数乘积）；超过时退化为“整段删除 + 整段新增”，不卡 UI 线程。</summary>
    internal const long MaxLcsCells = 4_000_000;

    internal static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }

    /// <summary>新增/删除行数（本地计算）。</summary>
    internal static (int Additions, int Deletions) Count(string before, string after)
    {
        var added = 0;
        var removed = 0;
        foreach (var line in Compute(before, after))
        {
            if (line.Kind == OpenCodeDiffLineKind.Added)
            {
                added++;
            }
            else if (line.Kind == OpenCodeDiffLineKind.Removed)
            {
                removed++;
            }
        }

        return (added, removed);
    }

    /// <summary>本地计算统一视图行（LCS；超大输入退化为整段替换）。</summary>
    internal static List<OpenCodeDiffLine> Compute(string? before, string? after)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);
        var result = new List<OpenCodeDiffLine>(a.Length + b.Length);

        // 去掉公共前后缀，缩小 LCS 规模。
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
               && string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        for (var i = 0; i < prefix; i++)
        {
            result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Context, i + 1, i + 1, a[i]));
        }

        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        if ((long)n * m > MaxLcsCells)
        {
            for (var i = 0; i < n; i++)
            {
                result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Removed, prefix + i + 1, null, a[prefix + i]));
            }

            for (var j = 0; j < m; j++)
            {
                result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Added, null, prefix + j + 1, b[prefix + j]));
            }
        }
        else if (n > 0 || m > 0)
        {
            var table = new int[(n + 1) * (m + 1)];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    table[i * (m + 1) + j] = string.Equals(a[prefix + i], b[prefix + j], StringComparison.Ordinal)
                        ? table[(i + 1) * (m + 1) + j + 1] + 1
                        : Math.Max(table[(i + 1) * (m + 1) + j], table[i * (m + 1) + j + 1]);
                }
            }

            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && string.Equals(a[prefix + x], b[prefix + y], StringComparison.Ordinal))
                {
                    result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Context, prefix + x + 1, prefix + y + 1, a[prefix + x]));
                    x++;
                    y++;
                }
                else if (y < m && (x >= n || table[x * (m + 1) + y + 1] >= table[(x + 1) * (m + 1) + y]))
                {
                    result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Added, null, prefix + y + 1, b[prefix + y]));
                    y++;
                }
                else
                {
                    result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Removed, prefix + x + 1, null, a[prefix + x]));
                    x++;
                }
            }
        }

        for (var k = 0; k < suffix; k++)
        {
            var oldIndex = a.Length - suffix + k;
            var newIndex = b.Length - suffix + k;
            result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Context, oldIndex + 1, newIndex + 1, a[oldIndex]));
        }

        return result;
    }

    /// <summary>
    /// 解析统一 diff 文本（“Index:/===/---/+++” 文件头 → Meta；“@@ -a,b +c,d @@” → Hunk；其余按首字符）。
    /// 不认识的行按上下文处理，不丢行。
    /// </summary>
    internal static List<OpenCodeDiffLine> ParseUnified(string? patch)
    {
        var result = new List<OpenCodeDiffLine>();
        var lines = SplitLines(patch);
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = TryParseHunk(line, out oldLine, out newLine);
                result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Hunk, null, null, line));
                continue;
            }

            if (!inHunk)
            {
                // 文件头（Index / ==== / --- / +++）与 hunk 之前的说明行。
                result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Meta, null, null, line));
                continue;
            }

            if (line.StartsWith('\\'))
            {
                // “\ No newline at end of file”。
                result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Meta, null, null, line));
                continue;
            }

            var marker = line.Length > 0 ? line[0] : ' ';
            var text = line.Length > 0 ? line[1..] : string.Empty;
            switch (marker)
            {
                case '+':
                    result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Added, null, newLine++, text));
                    break;
                case '-':
                    result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Removed, oldLine++, null, text));
                    break;
                default:
                    result.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Context, oldLine++, newLine++, marker == ' ' ? text : line));
                    break;
            }
        }

        return result;
    }

    private static bool TryParseHunk(string header, out int oldStart, out int newStart)
    {
        oldStart = 1;
        newStart = 1;
        var minus = header.IndexOf('-', 2);
        var plus = header.IndexOf('+', 2);
        if (minus < 0 || plus < 0)
        {
            return false;
        }

        oldStart = ReadNumber(header, minus + 1);
        newStart = ReadNumber(header, plus + 1);
        return true;
    }

    private static int ReadNumber(string text, int start)
    {
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > start && int.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 1;
    }

    /// <summary>统一视图行 → 并排行：连续“删除段 + 新增段”逐行配对，其余单侧；Meta 行省略（文件头在文件列表中呈现）。</summary>
    internal static List<OpenCodeDiffRow> ToSideBySide(IReadOnlyList<OpenCodeDiffLine> lines)
    {
        var rows = new List<OpenCodeDiffRow>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            switch (line.Kind)
            {
                case OpenCodeDiffLineKind.Meta:
                    i++;
                    break;
                case OpenCodeDiffLineKind.Hunk:
                    rows.Add(new OpenCodeDiffRow { HunkText = line.Text });
                    i++;
                    break;
                case OpenCodeDiffLineKind.Context:
                    rows.Add(new OpenCodeDiffRow
                    {
                        Left = new OpenCodeDiffCell(line.OldNumber, line.Text, OpenCodeDiffLineKind.Context),
                        Right = new OpenCodeDiffCell(line.NewNumber, line.Text, OpenCodeDiffLineKind.Context),
                    });
                    i++;
                    break;
                default:
                {
                    var removed = new List<OpenCodeDiffLine>();
                    var added = new List<OpenCodeDiffLine>();
                    while (i < lines.Count && lines[i].Kind == OpenCodeDiffLineKind.Removed)
                    {
                        removed.Add(lines[i++]);
                    }

                    while (i < lines.Count && lines[i].Kind == OpenCodeDiffLineKind.Added)
                    {
                        added.Add(lines[i++]);
                    }

                    var count = Math.Max(removed.Count, added.Count);
                    for (var k = 0; k < count; k++)
                    {
                        rows.Add(new OpenCodeDiffRow
                        {
                            Left = k < removed.Count ? new OpenCodeDiffCell(removed[k].OldNumber, removed[k].Text, OpenCodeDiffLineKind.Removed) : null,
                            Right = k < added.Count ? new OpenCodeDiffCell(added[k].NewNumber, added[k].Text, OpenCodeDiffLineKind.Added) : null,
                        });
                    }

                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// 折叠连续未改动段：长度 ≥ minRun 时首尾各保留 keep 行，中间替换为一个 Fold 行（可展开）。
    /// </summary>
    internal static List<OpenCodeDiffRow> Fold(IReadOnlyList<OpenCodeDiffRow> rows, int keep = 3, int minRun = 10)
    {
        var result = new List<OpenCodeDiffRow>(rows.Count);
        var i = 0;
        while (i < rows.Count)
        {
            if (!IsContext(rows[i]))
            {
                result.Add(rows[i++]);
                continue;
            }

            var start = i;
            while (i < rows.Count && IsContext(rows[i]))
            {
                i++;
            }

            var length = i - start;
            var leading = start == 0 ? 0 : keep;
            var trailing = i == rows.Count ? 0 : keep;
            if (length < minRun || length - leading - trailing < 2)
            {
                for (var k = start; k < i; k++)
                {
                    result.Add(rows[k]);
                }

                continue;
            }

            for (var k = start; k < start + leading; k++)
            {
                result.Add(rows[k]);
            }

            var folded = new List<OpenCodeDiffRow>();
            var unified = new List<OpenCodeDiffLine>();
            for (var k = start + leading; k < i - trailing; k++)
            {
                folded.Add(rows[k]);
                unified.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Context, rows[k].Left?.Number, rows[k].Right?.Number, rows[k].Left?.Text ?? string.Empty));
            }

            result.Add(new OpenCodeDiffRow { Folded = folded, FoldedUnified = unified });
            for (var k = i - trailing; k < i; k++)
            {
                result.Add(rows[k]);
            }
        }

        return result;
    }

    private static bool IsContext(OpenCodeDiffRow row)
        => !row.IsHunk && !row.IsFold && row.Left?.Kind == OpenCodeDiffLineKind.Context && row.Right?.Kind == OpenCodeDiffLineKind.Context;
}
