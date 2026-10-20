using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 交互卡片（权限 / 表单）共享的原生样式工具：主题画刷/样式安全取用与常用文本构造。
/// </summary>
/// <remarks>
/// 与既有纯 C# 视图（OpenCodeConversationView / OpenCodeToolPartView）同一约定：
/// 键缺失时返回 null（不抛异常，控件回退默认外观）；不引入任何硬编码颜色。
/// </remarks>
internal static class OpenCodeInteractionUi
{
    /// <summary>安全取主题画刷：键不存在时返回 null。</summary>
    internal static Brush? TryBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources)
            {
                // 用索引器读取；键缺失抛 KeyNotFoundException，由外层 catch 兜底。
                return resources[key] as Brush;
            }
        }
        catch
        {
            // 资源字典在极端时机（关闭中）可能不可用；回退默认外观即可。
        }

        return null;
    }

    /// <summary>安全取主题样式（如 AccentButtonStyle）：键不存在时返回 null。</summary>
    internal static Style? TryStyle(string key)
    {
        try
        {
            if (Application.Current?.Resources is { } resources)
            {
                return resources[key] as Style;
            }
        }
        catch
        {
        }

        return null;
    }

    // ---- 重设计 v3 §1：内置文字样式与主题画刷的键（全部来自 WinUI 内置资源，不定义颜色） ----

    internal const string CaptionStyleKey = "CaptionTextBlockStyle";
    internal const string CaptionSubtleStyleKey = "CaptionSubtleTextBlockStyle";
    internal const string BodyStyleKey = "BodyTextBlockStyle";
    internal const string BodyStrongStyleKey = "BodyStrongTextBlockStyle";
    internal const string BodySubtleStyleKey = "BodySubtleTextBlockStyle";
    internal const string SubtitleStyleKey = "SubtitleTextBlockStyle";
    internal const string TitleStyleKey = "TitleTextBlockStyle";
    internal const string CodeStyleKey = "OpenCodeCodeTextBlockStyle";
    internal const string QuestionCautionStyleKey = "OpenCodeQuestionCautionTextStyle";
    internal const string QuestionErrorStyleKey = "OpenCodeQuestionErrorTextStyle";
    internal const string QuestionChromeStyleKey = "OpenCodeQuestionChromeStyle";

    internal const string TextPrimaryBrushKey = "TextFillColorPrimaryBrush";
    internal const string TextSecondaryBrushKey = "TextFillColorSecondaryBrush";
    internal const string TextTertiaryBrushKey = "TextFillColorTertiaryBrush";
    internal const string SuccessBrushKey = "SystemFillColorSuccessBrush";
    internal const string SuccessBackgroundBrushKey = "SystemFillColorSuccessBackgroundBrush";
    internal const string CriticalBrushKey = "SystemFillColorCriticalBrush";
    internal const string CriticalBackgroundBrushKey = "SystemFillColorCriticalBackgroundBrush";
    internal const string CautionBrushKey = "SystemFillColorCautionBrush";
    internal const string CautionBackgroundBrushKey = "SystemFillColorCautionBackgroundBrush";
    internal const string AttentionBrushKey = "SystemFillColorAttentionBrush";
    internal const string NeutralBrushKey = "SystemFillColorNeutralBrush";
    internal const string CardBackgroundBrushKey = "CardBackgroundFillColorDefaultBrush";
    internal const string CardStrokeBrushKey = "CardStrokeColorDefaultBrush";
    internal const string SubtleSecondaryBrushKey = "SubtleFillColorSecondaryBrush";

    /// <summary>状态圆点样式（AppTokens.xaml；填充色经 ThemeResource，主题切换自动更新）。</summary>
    internal const string AttentionDotStyleKey = "OpenCodeAttentionDotStyle";
    internal const string CautionDotStyleKey = "OpenCodeCautionDotStyle";

    /// <summary>
    /// 使用内置文字样式构造文本（如 <see cref="CaptionSubtleStyleKey"/>）。样式缺失时回退默认外观；
    /// 次要/三级文字色优先用 *Subtle 样式（样式内为 ThemeResource，随主题即时更新），不再用 Opacity 降低对比。
    /// </summary>
    internal static TextBlock StyledText(string text, string styleKey, bool selectable = false) => new()
    {
        Text = text,
        Style = TryStyle(styleKey),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = selectable,
    };

    /// <summary>次级说明文本（12px、可选中复制）。</summary>
    internal static TextBlock Caption(string text, double opacity = 0.65) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    /// <summary>正文文本（自适应换行、可选中复制）。</summary>
    internal static TextBlock Body(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };
}
