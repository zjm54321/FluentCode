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
/// 悬停预览 = 小浮层（宽 ≤ <see cref="PreviewMaxWidth"/>、高 ≤ <see cref="PreviewMaxHeight"/>），紧贴来源行（间距 0）以便鼠标移入。
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

    /// <summary>悬停预览最大宽度 / 最小宽度 / 最大高度。</summary>
    internal const double PreviewMaxWidth = 520;

    internal const double PreviewMinWidth = 280;

    internal const double PreviewMaxHeight = 320;

    /// <summary>预览最多占窗口宽/高的比例（小窗口时仍保持“小”）。</summary>
    internal const double PreviewWindowFraction = 0.6;

    /// <summary>
    /// 鼠标离开行 / 预览后的过渡宽限（毫秒）：只用于跨过行与预览之间的指针事件次序（先 Exited 后 Entered），
    /// 低于感知阈值，用户看到的是“离开即隐藏”。
    /// </summary>
    internal const int HoverLeaveGraceMs = 80;

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

    /// <summary>预览宽度：不超过 <see cref="PreviewMaxWidth"/> 与窗口的 60%，不低于最小宽度（窗口足够时）。</summary>
    internal static double PreviewWidth(double rootWidth)
    {
        var available = Math.Max(0, rootWidth - (2 * WindowMargin));
        var width = Math.Min(PreviewMaxWidth, Math.Max(PreviewMinWidth, rootWidth * PreviewWindowFraction));
        return Math.Min(width, available);
    }

    /// <summary>预览最大高度：不超过 <see cref="PreviewMaxHeight"/> 与窗口的 60%。</summary>
    internal static double PreviewHeightLimit(double rootHeight)
    {
        var available = Math.Max(0, rootHeight - (2 * WindowMargin));
        return Math.Min(available, Math.Min(PreviewMaxHeight, Math.Max(120, rootHeight * PreviewWindowFraction)));
    }

    /// <summary>
    /// 预览位置：行右侧有空间时贴右侧、与行顶对齐；否则贴行下方（下方不足时贴上方）。
    /// 与行之间间距为 0（鼠标可直接移入预览），并夹在窗口边距内。
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
        double x;
        double y;
        var right = anchorX + anchorWidth;
        if (right + previewWidth <= rootWidth - WindowMargin)
        {
            x = right;
            y = anchorY;
        }
        else
        {
            x = anchorX;
            y = anchorY + anchorHeight;
            if (y + previewHeight > rootHeight - WindowMargin && anchorY - previewHeight >= WindowMargin)
            {
                y = anchorY - previewHeight;
            }
        }

        x = Math.Max(WindowMargin, Math.Min(x, rootWidth - previewWidth - WindowMargin));
        y = Math.Max(WindowMargin, Math.Min(y, rootHeight - previewHeight - WindowMargin));
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
