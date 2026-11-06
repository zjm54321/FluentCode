using System;
using System.Collections.Generic;
using System.Text;

namespace FluentCode.Pages.OpenCode;

/// <summary>详情弹出层的两种形态（G7）。</summary>
internal enum OpenCodeDetailPopupMode
{
    /// <summary>未显示。</summary>
    None,

    /// <summary>悬停预览：小型浮层，无关闭按钮，鼠标离开行（且未进入预览）即隐藏。</summary>
    Preview,

    /// <summary>点击打开的大详情视图：按内容收缩（最大约窗口 80%）、居中、遮罩层，关闭按钮 / Esc / 点击外部关闭。</summary>
    Large,
}

/// <summary>纯数据矩形（与 WinUI 无关，便于无界面测试）。</summary>
internal readonly record struct OpenCodeDetailRect(double X, double Y, double Width, double Height);

/// <summary>Native content measurement, including overflow hidden by an inner scroll/fixed viewport.</summary>
internal readonly record struct OpenCodeDetailPreviewMeasure(double FullHeight, bool Clipped = false);

/// <summary>A8：预览横向安全区（根坐标，DIP）：消息内容列内、两侧留白后的 [Left, Right]。</summary>
internal readonly record struct OpenCodeDetailBand(double Left, double Right)
{
    internal double Width => Math.Max(0, Right - Left);
}

/// <summary>
/// 详情弹出层的纯几何与悬停判定（G7，无 WinUI 依赖，headless 测试直接编译本文件）。
/// 大视图 = 按内容收缩、最大为应用窗口的 <see cref="LargeViewFraction"/>（居中，随窗口改变）；
/// 悬停预览 = 自然宽度（最多安全区的 80%），仍被裁切时逐步扩至安全区全宽，只在行上/下方，
/// 横向以指针为中心并夹紧在内容列两侧约 10% 留白之内（A8）。
/// </summary>
internal static class OpenCodeDetailPopupLayout
{
    /// <summary>大视图占窗口的比例。</summary>
    internal const double LargeViewFraction = 0.8;

    /// <summary>大视图最小尺寸（窗口更小时以窗口减去边距为准）。</summary>
    internal const double LargeMinWidth = 360;

    internal const double LargeMinHeight = 240;

    /// <summary>浮层与窗口边缘的最小距离。</summary>
    internal const double WindowMargin = 8;

    internal const double PreviewMinRowFraction = 0.6;
    internal const double PreviewMaxRowFraction = 1.2;

    internal const double PreviewWrapFraction = 0.8;
    internal const double PreviewGrowthFraction = 0.05;

    /// <summary>
    /// 时间线中最高的一整行占位：进行中行 36px + 上下各 2px 间距（OpenCodeTimelineRowMetrics / OpenCodeProcessedRowMetrics）。
    /// </summary>
    internal const double PreviewNeighbourRowClearance = 40;

    /// <summary>
    /// 预览与来源行之间的距离：先让出相邻的一整行，再留 4px 视觉间隔。
    /// 鼠标从当前行移到上一行 / 下一行时落在那一行上，而不是落在当前行的预览上。
    /// </summary>
    internal const double PreviewGap = PreviewNeighbourRowClearance + 4;

    /// <summary>
    /// 鼠标离开行 / 预览后的过渡宽限（毫秒），允许从来源行越过相邻行（<see cref="PreviewGap"/>）移入预览。
    /// </summary>
    internal const int HoverLeaveGraceMs = 360;

    /// <summary>
    /// 大视图的上限矩形：窗口的 80%，居中；窗口很小时不小于最小尺寸但绝不超出窗口（减去边距）。
    /// 实际大小按内容收缩（<see cref="LargeViewWidthRange"/> / <see cref="LargeViewHeightRange"/>），只有并排 diff、
    /// 浏览器与图片等需要宽度的视图才占满这一宽度。
    /// </summary>
    internal static OpenCodeDetailRect LargeView(double rootWidth, double rootHeight)
    {
        rootWidth = Math.Max(0, rootWidth);
        rootHeight = Math.Max(0, rootHeight);
        var maxWidth = Math.Max(0, rootWidth - (2 * WindowMargin));
        var maxHeight = Math.Max(0, rootHeight - (2 * WindowMargin));
        var width = Math.Min(maxWidth, Math.Max(LargeMinWidth, Math.Round(rootWidth * LargeViewFraction)));
        var height = Math.Min(maxHeight, Math.Max(LargeMinHeight, Math.Round(rootHeight * LargeViewFraction)));
        return new OpenCodeDetailRect((rootWidth - width) / 2, (rootHeight - height) / 2, width, height);
    }

    /// <summary>按内容收缩的大视图最小宽度：短内容也是一张完整的卡片，而不是一条窄条。</summary>
    internal const double LargeFitMinWidth = 560;

    /// <summary>按内容收缩的大视图最小高度（标题栏 56 + 约三行正文）。</summary>
    internal const double LargeFitMinHeight = 160;

    /// <summary>
    /// 大视图宽度范围：默认取内容的自然宽度，限制在 [<see cref="LargeFitMinWidth"/>, 上限宽度]；
    /// <paramref name="fillWidth"/>（并排 diff / 浏览器 / 图片）固定为上限宽度。上限比最小值还小时以上限为准。
    /// </summary>
    internal static (double Min, double Max) LargeViewWidthRange(OpenCodeDetailRect cap, bool fillWidth)
    {
        var max = Math.Max(0, cap.Width);
        return (fillWidth ? max : Math.Min(LargeFitMinWidth, max), max);
    }

    /// <summary>大视图高度范围：内容高度，限制在 [<see cref="LargeFitMinHeight"/>, 上限高度]；更长的内容在视图内滚动。</summary>
    internal static (double Min, double Max) LargeViewHeightRange(OpenCodeDetailRect cap)
    {
        var max = Math.Max(0, cap.Height);
        return (Math.Min(LargeFitMinHeight, max), max);
    }

    /// <summary>把内容测得的自然宽度夹到 <see cref="LargeViewWidthRange"/> 内。</summary>
    internal static double LargeFitWidth(double contentWidth, OpenCodeDetailRect cap, bool fillWidth)
    {
        var (min, max) = LargeViewWidthRange(cap, fillWidth);
        return double.IsFinite(contentWidth) ? Math.Clamp(contentWidth, min, max) : max;
    }

    /// <summary>
    /// 哪些大视图需要占满上限宽度：差异视图（edit / multiedit / patch，以及 write 的差异模式）的并排两栏、
    /// 浏览器页面，以及带图片结果的工具。write 的整文件预览 / 源码与其他所有视图按内容收缩。
    /// </summary>
    internal static bool LargeViewFillsWidth(OpenCodeToolKind kind, bool hasImages, bool fileView)
        => hasImages || kind == OpenCodeToolKind.Browser
            || ((kind is OpenCodeToolKind.Edit or OpenCodeToolKind.MultiEdit or OpenCodeToolKind.Patch or OpenCodeToolKind.Write) && !fileView);

    /// <summary>
    /// A8：悬停预览左右各留内容列宽度的这一比例（约 10%）。预览只能落在内容列内这段安全区里，
    /// 不以整个窗口为边界。
    /// </summary>
    internal const double ContentEdgeFraction = 0.1;

    /// <summary>
    /// A8：由消息内容列（已与可见滚动视口相交、同一根坐标）得到预览的横向安全区：
    /// 两侧各收进 <see cref="ContentEdgeFraction"/> 列宽，并且仍不越过窗口 <see cref="WindowMargin"/>。
    /// 坐标非法 / 列宽为 0 / 安全区为空时返回 null（调用方不得退回窗口或 (0,0)）。
    /// </summary>
    internal static OpenCodeDetailBand? PreviewSafeBand(double contentLeft, double contentRight, double rootWidth)
    {
        if (!double.IsFinite(contentLeft) || !double.IsFinite(contentRight) || !double.IsFinite(rootWidth)) return null;
        var width = contentRight - contentLeft;
        if (width <= 0 || rootWidth <= 0) return null;
        var inset = width * ContentEdgeFraction;
        var left = Math.Max(contentLeft + inset, WindowMargin);
        var right = Math.Min(contentRight - inset, rootWidth - WindowMargin);
        return right - left >= 1 ? new OpenCodeDetailBand(left, right) : null;
    }

    /// <summary>
    /// A8：以指针 X 为中心，夹紧在安全区内（宽度先被限制到安全区宽度，绝不溢出）。
    /// </summary>
    internal static double PreviewX(double pointerX, double previewWidth, OpenCodeDetailBand band)
    {
        previewWidth = Math.Min(Math.Max(0, previewWidth), band.Width);
        if (!double.IsFinite(pointerX)) pointerX = band.Left + band.Width / 2;
        // Max() guards floating-point rounding when the preview exactly fills the band.
        return Math.Clamp(pointerX - previewWidth / 2, band.Left, Math.Max(band.Left, band.Right - previewWidth));
    }

    /// <summary>保留原来的最小宽度，但不超过 80% 换行阈值；最大宽度为整个安全区。</summary>
    internal static (double Min, double Max) PreviewWidths(double anchorWidth, OpenCodeDetailBand band)
    {
        var available = Math.Max(0, band.Width);
        var row = Math.Max(0, anchorWidth);
        return (Math.Min(row * PreviewMinRowFraction, available * PreviewWrapFraction), available);
    }

    /// <summary>HP1: natural width up to 80%; wrap there, then grow in bounded 5% steps only while clipped.</summary>
    internal static (double Width, double FullHeight, bool Clipped) FitPreview(double anchorWidth, OpenCodeDetailBand band,
        double naturalWidth, double availableHeight, Func<double, OpenCodeDetailPreviewMeasure> measureContent)
    {
        var (min, max) = PreviewWidths(anchorWidth, band);
        var wrap = max * PreviewWrapFraction;
        var width = Math.Clamp(naturalWidth, min, wrap);
        var measured = measureContent(width);
        bool Clipped() => measured.Clipped || measured.FullHeight > availableHeight;
        if (!Clipped()) return (width, measured.FullHeight, false);
        // A narrow but tall block may first use the normal wrapping limit.
        if (width < wrap)
        {
            width = wrap;
            measured = measureContent(width);
        }
        for (var step = 1; Clipped() && width < max && step <= 4; step++)
        {
            width = step == 4 ? max : Math.Min(max, max * (PreviewWrapFraction + step * PreviewGrowthFraction));
            measured = measureContent(width);
        }
        return (width, measured.FullHeight, Clipped());
    }

    /// <summary>实际高度由来源行上/下可用空间进一步限制。</summary>
    internal static double PreviewHeightLimit(double rootHeight)
    {
        var available = Math.Max(0, rootHeight - (2 * WindowMargin));
        return available;
    }

    /// <summary>
    /// 预览只在行上方或下方，选择空间更大的一侧，保留 <see cref="PreviewGap"/>（让出相邻一行）并夹紧宽高。
    /// 横向（A8）：以指针 X 为中心，夹紧在内容列安全区 <paramref name="band"/> 内，而不是来源行中心 / 整个窗口。
    /// </summary>
    internal static OpenCodeDetailRect PreviewPlacement(
        double pointerX,
        double anchorY,
        double anchorHeight,
        double previewWidth,
        double previewHeight,
        OpenCodeDetailBand band,
        double rootHeight)
    {
        previewWidth = Math.Min(Math.Max(0, previewWidth), band.Width);
        var top = Math.Clamp(anchorY, WindowMargin, Math.Max(WindowMargin, rootHeight - WindowMargin));
        var bottom = Math.Clamp(anchorY + anchorHeight, WindowMargin, Math.Max(WindowMargin, rootHeight - WindowMargin));
        var above = Math.Max(0, top - WindowMargin - PreviewGap);
        var below = Math.Max(0, rootHeight - WindowMargin - bottom - PreviewGap);
        var useBelow = below >= above;
        previewHeight = Math.Min(Math.Max(0, previewHeight), useBelow ? below : above);
        var x = PreviewX(pointerX, previewWidth, band);
        var y = useBelow ? bottom + PreviewGap : top - PreviewGap - previewHeight;
        y = Math.Max(Math.Min(WindowMargin, rootHeight / 2), Math.Min(y, rootHeight - WindowMargin - previewHeight));
        return new OpenCodeDetailRect(x, y, previewWidth, previewHeight);
    }
}

/// <summary>File presentation decisions are independent of the visual tree and never infer a diff from a path.</summary>
internal static class OpenCodeFileDetailPolicy
{
    internal static bool IsFullWrite(OpenCodeToolKind kind, OpenCodeToolDiffFile file)
        => kind == OpenCodeToolKind.Write && file.After is not null && string.IsNullOrEmpty(file.Patch);

    internal static bool IsMarkdown(string path)
        => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mdown", StringComparison.OrdinalIgnoreCase);

    internal static string Language(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".js" or ".jsx" or ".ts" or ".tsx" => "javascript",
        ".py" => "python", ".json" => "json", ".sh" or ".bash" => "shell",
        ".go" => "go", ".rs" => "rust", ".java" => "java", ".cpp" or ".h" or ".c" => "c",
        ".css" => "css", ".yaml" or ".yml" => "yaml", _ => "text",
    };

    // Bounded lexical styling only: all tokens preserve the exact source and never execute it.
    internal static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "cs" or "c#" or "csharp" => "csharp", "js" or "jsx" or "ts" or "tsx" or "javascript" or "typescript" => "javascript",
        "py" or "python" => "python", "json" => "json", "sh" or "bash" or "shell" => "shell",
        "go" or "golang" => "go", "rs" or "rust" => "rust", "java" => "java",
        "c" or "cpp" or "c++" => "c", "css" => "css", "yaml" or "yml" => "yaml", _ => "text",
    };

    internal static IEnumerable<(string Text, string Kind)> Tokens(string source, string language)
    {
        language = NormalizeLanguage(language);
        if (language == "text") { yield return (source, "text"); yield break; }
        var hashComment = language is "python" or "shell" or "yaml" ? "#[^\\r\\n]*|" : string.Empty;
        var pattern = "(//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|" + hashComment + "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|\\b\\d+(?:\\.\\d+)?\\b|\\b[A-Za-z_][A-Za-z_0-9]*\\b|[+*/%=!<>|&?:-]+)";
        var offset = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(source, pattern))
        {
            if (match.Index > offset) yield return (source[offset..match.Index], "text");
            var value = match.Value;
            var next = match.Index + match.Length;
            while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
            var kind = value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("/*", StringComparison.Ordinal)
                || (value.StartsWith('#') && language is "python" or "shell" or "yaml") ? "comment"
                : value[0] is '\"' or '\'' ? "string"
                : char.IsDigit(value[0]) ? "number"
                : (" class public private internal static sealed void return if else for foreach while using namespace new var const let function async await import from def in is not None True False true false null export default interface type extends implements try catch throw switch case break continue package func fn struct impl pub use mut match self this "
                     .Contains(" " + value + " ", StringComparison.Ordinal)) ? "keyword"
                : "+*/%=!<>|&?:-".IndexOf(value[0]) >= 0 ? "operator"
                : next < source.Length && source[next] == '(' ? "function"
                : char.IsUpper(value[0]) || " int string bool double float long byte char decimal object short ".Contains(" " + value + " ", StringComparison.Ordinal) ? "type" : "text";
            yield return (value, kind);
            offset = match.Index + match.Length;
        }
        if (offset < source.Length) yield return (source[offset..], "text");
    }
}

/// <summary>
/// 详情文字策略（G7/G8，纯函数）：详情文字总是换行；等宽块完整显示，只按 <see cref="CodeBlockChunkLines"/> 行分段
/// 为多个文本元素（不截断、不丢行）。
/// </summary>
internal static class OpenCodeDetailTextPolicy
{
    /// <summary>所有详情文字默认换行（无开关）。</summary>
    internal const bool WrapByDefault = true;

    /// <summary>等宽块每段行数。</summary>
    internal const int CodeBlockChunkLines = 400;

    internal const int CodePreviewLines = 6;
    internal const int CodePreviewCharacters = 240;

    // Display only: remove terminal line endings which otherwise render an empty bottom line.
    // Copy always retains the original source, never this display projection.
    internal static string CodeDisplayText(string source) => source.TrimEnd('\r', '\n');

    internal static bool CanCollapseCode(string source)
    {
        var display = CodeDisplayText(source);
        return display.Length > CodePreviewCharacters || CodePreview(source).Length < display.Length;
    }

    internal static string CodePreview(string source)
    {
        var display = CodeDisplayText(source);
        var offset = 0;
        for (var line = 0; line < CodePreviewLines; line++)
        {
            var end = display.IndexOf('\n', offset);
            if (end < 0) return display;
            if (line == CodePreviewLines - 1) return display[..end].TrimEnd('\r');
            offset = end + 1;
        }
        return display;
    }

    /// <summary>把文本按行分段（去掉末尾换行与每行的 \r）；空文本返回一个空段。拼接各段（以 \n）即得完整原文。</summary>
    internal static IReadOnlyList<string> ChunkLines(string? text, int chunkLines = CodeBlockChunkLines)
    {
        if (chunkLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkLines));
        }

        var trimmed = (text ?? string.Empty).TrimEnd('\r', '\n');
        var lines = trimmed.Split('\n');
        var chunks = new List<string>((lines.Length / chunkLines) + 1);
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i % chunkLines != 0)
            {
                builder.Append('\n');
            }

            builder.Append(lines[i].TrimEnd('\r'));
            if ((i + 1) % chunkLines == 0 || i == lines.Length - 1)
            {
                chunks.Add(builder.ToString());
                builder.Clear();
            }
        }

        return chunks;
    }
}

/// <summary>
/// 悬停预览的指针跟踪（G7）：指针在来源行或预览内时保持；两者都离开即应隐藏。
/// 弹出层在 <see cref="ShouldHide"/> 为真时经 <see cref="OpenCodeDetailPopupLayout.HoverLeaveGraceMs"/> 宽限后再次复核并隐藏。
/// </summary>
internal sealed class OpenCodeDetailHoverTracker
{
    internal bool InAnchor { get; private set; }

    internal bool InPreview { get; private set; }

    internal bool ShouldHide => !InAnchor && !InPreview;

    internal void EnterAnchor() => InAnchor = true;

    internal void ExitAnchor() => InAnchor = false;

    internal void EnterPreview() => InPreview = true;

    internal void ExitPreview() => InPreview = false;

    internal void Reset()
    {
        InAnchor = false;
        InPreview = false;
    }
}
