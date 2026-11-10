using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#if !QUESTION_INPUT_TESTS
using Windows.Foundation;
#endif

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 选项标签 + “推荐”小标签的排布：小标签紧跟在标签文字之后（间隔 <see cref="Gap"/>），
/// 垂直居中于标签首行；长标签在扣除小标签宽度后的剩余宽度内换行，两者不重叠。
/// </summary>
internal static class OpenCodeOptionLabelLayout
{
    internal const double Gap = 6;

    /// <summary>标签可用宽度：总宽度扣除小标签和间隔；无限宽时不限制。</summary>
    internal static double LabelWidth(double available, double tagWidth) =>
        double.IsInfinity(available) ? double.PositiveInfinity : Math.Max(0, available - tagWidth - Gap);

    /// <summary>
    /// 小标签位置与整体尺寸。<paramref name="labelWidth"/> 是标签实际占用的宽度（换行后为最宽一行），
    /// 所以短标签的小标签紧贴文字末尾，长标签的小标签落在首行右侧。
    /// </summary>
    internal static (double TagLeft, double TagTop, double LabelTop, double Width, double Height) Place(
        double labelWidth, double labelHeight, double firstLineHeight, double tagWidth, double tagHeight)
    {
        var line = Math.Min(firstLineHeight, labelHeight);
        var tagTop = (line - tagHeight) / 2;
        // 小标签比首行高时把文字下移，而不是让小标签越过上边缘。
        var labelTop = tagTop < 0 ? -tagTop : 0;
        tagTop = Math.Max(0, tagTop);
        var tagLeft = labelWidth + Gap;
        return (tagLeft, tagTop, labelTop, tagLeft + tagWidth, Math.Max(labelTop + labelHeight, tagTop + tagHeight));
    }

    /// <summary>首行高度：样式给出 LineHeight 时用它，否则按字号估算（14px Body ≈ 20px）。</summary>
    internal static double FirstLineHeight(double lineHeight, double fontSize) =>
        lineHeight > 0 ? lineHeight : Math.Ceiling(fontSize * 1.4);
}

/// <summary>选项内容的首行：标签文字（可换行）+ 紧随其后的“推荐”小标签。</summary>
internal sealed partial class OpenCodeOptionLabelPanel : Panel
{
    private readonly TextBlock _label;
    private readonly FrameworkElement _tag;
#if !QUESTION_INPUT_TESTS
    private (double TagLeft, double TagTop, double LabelTop, double Width, double Height) _place;
#endif

    internal OpenCodeOptionLabelPanel(TextBlock label, FrameworkElement tag)
    {
        _label = label;
        _tag = tag;
        Children.Add(label);
        Children.Add(tag);
    }

#if !QUESTION_INPUT_TESTS
    protected override Size MeasureOverride(Size availableSize)
    {
        _tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tag = _tag.DesiredSize;
        _label.Measure(new Size(OpenCodeOptionLabelLayout.LabelWidth(availableSize.Width, tag.Width), double.PositiveInfinity));
        var label = _label.DesiredSize;
        var line = OpenCodeOptionLabelLayout.FirstLineHeight(_label.LineHeight, _label.FontSize);
        _place = OpenCodeOptionLabelLayout.Place(label.Width, label.Height, line, tag.Width, tag.Height);
        return new Size(_place.Width, _place.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var label = _label.DesiredSize;
        var tag = _tag.DesiredSize;
        _label.Arrange(new Rect(0, _place.LabelTop, label.Width, label.Height));
        _tag.Arrange(new Rect(_place.TagLeft, _place.TagTop, tag.Width, tag.Height));
        return finalSize;
    }
#endif
}
