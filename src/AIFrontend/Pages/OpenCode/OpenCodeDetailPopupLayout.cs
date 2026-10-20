using System;
using System.Collections.Generic;
using System.Text;

namespace AIFrontend.Pages.OpenCode;

/// <summary>详情弹出层的两种形态（G7）。</summary>
internal enum OpenCodeDetailPopupMode
{
    /// <summary>未显示。</summary>
    None,

    /// <summary>悬停预览：小型浮层，无关闭按钮，鼠标离开行（且未进入预览）即隐藏。</summary>
    Preview,

    /// <summary>点击打开的大详情视图：约窗口 80%、居中、遮罩层，关闭按钮 / Esc / 点击外部关闭。</summary>
    Large,
}

/// <summary>纯数据矩形（与 WinUI 无关，便于无界面测试）。</summary>
internal readonly record struct OpenCodeDetailRect(double X, double Y, double Width, double Height);

/// <summary>
/// 详情弹出层的纯几何与悬停判定（G7，无 WinUI 依赖，headless 测试直接编译本文件）。
/// 大视图 = 应用窗口的 <see cref="LargeViewFraction"/>（居中，随窗口改变）；
/// 悬停预览 = 来源行 0.6–1.2 倍宽，优先最窄且无需纵向滚动的宽度，只在行上/下方。
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

    internal const double PreviewGap = 8;

    /// <summary>
    /// 鼠标离开行 / 预览后的过渡宽限（毫秒），允许安全跨越 8px 间距。
    /// </summary>
    internal const int HoverLeaveGraceMs = 240;

    /// <summary>大视图矩形：窗口的 80%，居中；窗口很小时不小于最小尺寸但绝不超出窗口（减去边距）。</summary>
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

    /// <summary>来源行宽度范围；视口边距优先于行比例。</summary>
    internal static (double Min, double Max) PreviewWidths(double anchorWidth, double rootWidth)
    {
        var available = Math.Max(0, rootWidth - (2 * WindowMargin));
        var row = Math.Max(0, anchorWidth);
        return (Math.Min(row * PreviewMinRowFraction, available), Math.Min(row * PreviewMaxRowFraction, available));
    }

    /// <summary>Measure the full wrapped height, not a ScrollViewer's constrained viewport. Search to one DIP.</summary>
    internal static (double Width, double FullHeight, bool Clipped) FitPreview(double anchorWidth, double rootWidth,
        double availableHeight, Func<double, double> measureFullHeight)
    {
        var (min, max) = PreviewWidths(anchorWidth, rootWidth);
        var height = measureFullHeight(min);
        if (height <= availableHeight) return (min, height, false);
        height = measureFullHeight(max);
        if (height > availableHeight) return (max, height, true);
        var low = min;
        var high = max;
        for (var i = 0; i < 16 && high - low > 1; i++)
        {
            var middle = (low + high) / 2;
            if (measureFullHeight(middle) <= availableHeight) high = middle;
            else low = middle;
        }
        return (high, measureFullHeight(high), false);
    }

    /// <summary>实际高度由来源行上/下可用空间进一步限制。</summary>
    internal static double PreviewHeightLimit(double rootHeight)
    {
        var available = Math.Max(0, rootHeight - (2 * WindowMargin));
        return available;
    }

    /// <summary>
    /// 预览只在行上方或下方，选择空间更大的一侧，保留 8px 间距并夹紧宽高。
    /// </summary>
    internal static OpenCodeDetailRect PreviewPlacement(
        double anchorX,
        double anchorY,
        double anchorWidth,
        double anchorHeight,
        double previewWidth,
        double previewHeight,
        double rootWidth,
        double rootHeight)
    {
        previewWidth = Math.Min(Math.Max(0, previewWidth), Math.Max(0, rootWidth - 2 * WindowMargin));
        var top = Math.Clamp(anchorY, WindowMargin, Math.Max(WindowMargin, rootHeight - WindowMargin));
        var bottom = Math.Clamp(anchorY + anchorHeight, WindowMargin, Math.Max(WindowMargin, rootHeight - WindowMargin));
        var above = Math.Max(0, top - WindowMargin - PreviewGap);
        var below = Math.Max(0, rootHeight - WindowMargin - bottom - PreviewGap);
        var useBelow = below >= above;
        previewHeight = Math.Min(Math.Max(0, previewHeight), useBelow ? below : above);
        var x = Math.Max(WindowMargin, Math.Min(anchorX + (anchorWidth - previewWidth) / 2, rootWidth - previewWidth - WindowMargin));
        var y = useBelow ? bottom + PreviewGap : top - PreviewGap - previewHeight;
        y = Math.Max(Math.Min(WindowMargin, rootHeight / 2), Math.Min(y, rootHeight - WindowMargin - previewHeight));
        return new OpenCodeDetailRect(x, y, previewWidth, previewHeight);
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
