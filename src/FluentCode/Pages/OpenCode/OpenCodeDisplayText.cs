using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Display-only cleanup for model-authored question and to-do text (pure, no WinUI). Never applied to values sent back
/// to the server: option values, answer matching and stored to-do content keep the exact original string.
/// </summary>
/// <remarks>
/// Models sometimes emit a bare <c>\r</c> at token boundaries (<c>"1\r.2611\r.14\r 已发布安装\r（PID\r 38448\r）"</c>).
/// A TextBlock renders every CR as a line break, so such an item became a column of fragments. A bare CR (not part of
/// CRLF) is never a real line break in this protocol, and in real payloads it always sits next to a space or
/// punctuation — dropping it restores the intended text. CRLF / LF / NEL / U+2028 / U+2029 / VT / FF are real breaks.
/// Other C0/C1 controls and invisible format characters (ZWSP, BOM, soft hyphen, bidi embeddings/overrides/isolates)
/// are removed; ZWJ / ZWNJ stay because emoji sequences and some scripts need them.
/// </remarks>
internal static class OpenCodeDisplayText
{
    private static readonly Regex RecommendedSuffix = new(@"(?:\s*[(（]\s*(?:recommended|推荐)\s*[)）])+\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>One logical line (to-do item, header, option label, title): every break or tab becomes one space,
    /// runs of spaces collapse, the result is trimmed.</summary>
    internal static string Line(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        for (var index = 0; index < text.Length; index++)
        {
            switch (Classify(text, index))
            {
                case Kind.Drop:
                    continue;
                case Kind.Break:
                case Kind.Space:
                    pendingSpace = builder.Length > 0;
                    continue;
                default:
                    if (pendingSpace) builder.Append(' ');
                    pendingSpace = false;
                    builder.Append(text[index]);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Paragraph text (question body, option description): keeps real line breaks as <c>\n</c>, drops stray
    /// CR / controls / invisible format characters, trims each line's end, allows at most one blank line in a row and
    /// trims leading/trailing blank lines. Indentation inside a line is kept.</summary>
    internal static string Block(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        var line = new StringBuilder();
        var blankRun = 0;
        void EndLine()
        {
            var content = line.ToString().TrimEnd();
            line.Clear();
            if (content.Length == 0)
            {
                if (builder.Length > 0) blankRun++;
                return;
            }

            if (builder.Length > 0) builder.Append(blankRun > 0 ? "\n\n" : "\n");
            builder.Append(content);
            blankRun = 0;
        }

        for (var index = 0; index < text.Length; index++)
        {
            switch (Classify(text, index))
            {
                case Kind.Drop:
                    continue;
                case Kind.Break:
                    EndLine();
                    continue;
                case Kind.Space:
                    line.Append(' ');
                    continue;
                default:
                    line.Append(text[index]);
                    break;
            }
        }

        EndLine();
        return builder.ToString();
    }

    /// <summary>
    /// Option label for display: <see cref="Line"/> plus the model's trailing “(Recommended)” / “（推荐）” suffix(es) removed
    /// (shown as a tag instead). A label that is only the suffix stays as is and is not marked recommended.
    /// </summary>
    internal static string OptionLabel(string? label, out bool recommended)
    {
        var line = Line(label);
        var plain = RecommendedSuffix.Replace(line, string.Empty).TrimEnd();
        recommended = plain.Length > 0 && plain.Length != line.Length;
        return recommended ? plain : line;
    }

    private enum Kind { Keep, Drop, Break, Space }

    private static Kind Classify(string text, int index)
    {
        var c = text[index];
        switch (c)
        {
            case '\r':
                // CRLF counts once (as the LF); a bare CR is a stray tokenizer artifact, not a line break.
                return Kind.Drop;
            case '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029':
                return Kind.Break;
            case '\t' or ' ':
                return Kind.Space;
            case '\u200C' or '\u200D':
                return Kind.Keep;
        }

        if (char.IsControl(c)) return Kind.Drop;
        return char.GetUnicodeCategory(c) == UnicodeCategory.Format ? Kind.Drop : Kind.Keep;
    }
}
