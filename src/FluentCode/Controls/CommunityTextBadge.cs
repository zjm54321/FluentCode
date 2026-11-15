// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using DevWinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace FluentCode.Controls;

/// <summary>应用语义色调：与视图层的语义（中性 / 需要注意 / 失败 / 成功 / 强调）一一对应。</summary>
internal enum CommunityTextBadgeTone
{
    /// <summary>中性事实（只读、未读取、不可用）。</summary>
    Neutral,
    /// <summary>需要用户注意（部分数据、可用但受限）。</summary>
    Caution,
    /// <summary>失败 / 错误。</summary>
    Critical,
    /// <summary>成功 / 已确认。</summary>
    Success,
    /// <summary>强调或信息性（FluentCode 本机操作、焦点状态）。</summary>
    Attention,
    /// <summary>分类用紫调（Native UI 分类徽章，不承载状态含义）。</summary>
    Purple,
    /// <summary>分类用粉调（Native UI 分类徽章，不承载状态含义）。</summary>
    Pink,
}

/// <summary>几何：紧凑标签（行内小标签）与药丸标签（20 DIP 高、圆角 10）。</summary>
internal enum CommunityTextBadgeShape
{
    /// <summary>行内紧凑标签：圆角 4、内边距 6,1,6,2、12px 常规字重。</summary>
    Compact,
    /// <summary>药丸标签：最小高度 20、圆角 10、内边距 8,2、12px 半粗字重。</summary>
    Pill,
}

/// <summary>
/// 文本徽标适配器（PLAN-v2 §3）：把应用级输入（文本 + 语义色调 + 几何 + 可选前置图标字形）交给 DevWinUI 的
/// <c>IssueLabel</c>，对外只返回普通 WinUI 元素，DevWinUI 类型不出现在调用方的可见面上。
/// </summary>
/// <remarks>
/// <para>
/// <b>只用于纯展示的只读文本标签。</b>带删除/点击行为的命令芯片、意图芯片、RichEdit 原子芯片、
/// 计数或状态圆点都不走这里（前者仍是 Button，后者用原生 InfoBadge）。
/// </para>
/// <para>
/// <b>为什么只传数据 + 模板，不传元素：</b>库模板内部有<b>两个</b>标题呈现器
/// （<c>SeverityTitle</c> 用于严重度配色、<c>LabelTitle</c> 用于自定义前景色，二者互斥显示且都绑定
/// 同一个 <c>Title</c>）。若把已有父级的 UIElement 赋给 <c>Title</c>，同一元素会被塞进两处呈现器，
/// 必然失败或显示错误。因此这里只赋值字符串，并由 <c>AppBadgeTitleTemplate</c> 提供文本外观。
/// </para>
/// <para>
/// <b>前置图标（C06）：</b>库模板没有图标位。带图标的徽标仍然只把字符串交给 <c>Title</c>，图标字形写进该徽标的
/// <c>TitleTemplate</c>（每个字形一份模板，见 <see cref="GlyphTemplate"/>），所以有无图标是同一个控件、同一套
/// 盒子与配色。字形和文字都从标题呈现器继承前景色，因此同样不需要设置 <c>Foreground</c>。
/// 来源：https://github.com/ghost1372/DevWinUI，提交 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e，
/// dev/DevWinUI/Themes/Styles/Controls/IssueLabel.xaml（SeverityTitle / LabelTitle 的 ContentTemplate 绑定）。
/// </para>
/// <para>
/// <b>不要设置 <c>Foreground</c>：</b>设置自定义前景会切换到第二个呈现器分支，从而绕过严重度配色。
/// 色调只通过 <see cref="CommunityTextBadgeTone"/> 表达。
/// </para>
/// </remarks>
internal static class CommunityTextBadge
{
    internal const string CompactStyleKey = "AppCompactTextBadgeStyle";
    internal const string PillStyleKey = "AppPillTextBadgeStyle";
    internal const string TitleTemplateKey = "AppBadgeTitleTemplate";

    private const string Namespaces = " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    // 每个字形一份模板。模板不是控件，也不持有控件；字形来自应用内固定的图标表，所以条目数有上限。
    private static readonly Dictionary<string, DataTemplate?> GlyphTemplates = new(StringComparer.Ordinal);

    /// <summary>构造一个文本徽标；返回普通 <see cref="FrameworkElement"/>（运行时为 IssueLabel）。</summary>
    /// <param name="text">显示文本（纯文本，不接受元素）。</param>
    /// <param name="tone">应用语义色调。</param>
    /// <param name="shape">几何（紧凑 / 药丸）。</param>
    /// <param name="accessibleName">
    /// 可访问名称。为 <c>null</c> 时标记 <see cref="AccessibilityView.Raw"/>（周围文本已读出同一信息）；
    /// 传入字符串时设置名称并保留在内容视图中。
    /// </param>
    /// <param name="glyph">前置图标字形（Segoe Fluent Icons 字符）；<c>null</c> 或空表示没有图标。</param>
    internal static FrameworkElement Create(
        string text,
        CommunityTextBadgeTone tone = CommunityTextBadgeTone.Neutral,
        CommunityTextBadgeShape shape = CommunityTextBadgeShape.Compact,
        string? accessibleName = null,
        string? glyph = null)
    {
        var label = new IssueLabel { Style = Style(shape == CommunityTextBadgeShape.Pill ? PillStyleKey : CompactStyleKey) };
        Apply(label, text, tone, accessibleName);
        SetGlyph(label, glyph);
        return label;
    }

    /// <summary>更新文本（色调、几何、图标与可访问名称保持不变）。没有文字也没有图标的徽标不显示。</summary>
    internal static void Update(FrameworkElement badge, string? text)
    {
        if (badge is IssueLabel label)
        {
            label.Title = text ?? string.Empty;
            label.Visibility = string.IsNullOrEmpty(text) && label.Tag is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>更新前置图标字形；<c>null</c> 或空表示去掉图标。字形未变时什么都不做。</summary>
    internal static void SetGlyph(FrameworkElement badge, string? glyph)
    {
        if (badge is not IssueLabel label)
        {
            return;
        }

        var next = string.IsNullOrEmpty(glyph) ? null : glyph;
        if (Equals(label.Tag, next))
        {
            return;
        }

        // 当前字形记在控件自己的 Tag 上：状态跟着控件走，适配器不为它保留任何引用。
        label.Tag = next;
        // 字形模板解析失败时退回纯文字模板：徽标少一个图标，但文字和配色不受影响。
        label.TitleTemplate = (next is null ? null : GlyphTemplate(next)) ?? TryResource(TitleTemplateKey) as DataTemplate;
        if (next is not null)
        {
            label.Visibility = Visibility.Visible;
        }
    }

    /// <summary>更新语义色调。</summary>
    internal static void SetTone(FrameworkElement badge, CommunityTextBadgeTone tone)
    {
        if (badge is IssueLabel label)
        {
            label.Severity = Severity(tone);
        }
    }

    /// <summary>更新可访问名称；<c>null</c> 表示回到“装饰性”（周围文本已表达同一信息）。</summary>
    internal static void SetAccessibleName(FrameworkElement badge, string? name)
    {
        if (badge is not IssueLabel label)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            label.ClearValue(AutomationProperties.NameProperty);
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        }
        else
        {
            AutomationProperties.SetName(label, name);
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Content);
        }
    }

    /// <summary>应用语义色调 → 库严重度的映射（见 CONTRACT-F.md）。私有：返回类型是库类型，不对外暴露。</summary>
    private static IssueLabelSeverity Severity(CommunityTextBadgeTone tone) => tone switch
    {
        CommunityTextBadgeTone.Neutral => IssueLabelSeverity.Gray,
        CommunityTextBadgeTone.Caution => IssueLabelSeverity.Warning,
        CommunityTextBadgeTone.Critical => IssueLabelSeverity.Error,
        CommunityTextBadgeTone.Success => IssueLabelSeverity.Success,
        CommunityTextBadgeTone.Attention => IssueLabelSeverity.Blue,
        CommunityTextBadgeTone.Purple => IssueLabelSeverity.Purple,
        CommunityTextBadgeTone.Pink => IssueLabelSeverity.Pink,
        _ => IssueLabelSeverity.Gray,
    };

    private static void Apply(IssueLabel label, string text, CommunityTextBadgeTone tone, string? accessibleName)
    {
        // 只传数据 + 模板：绝不赋值已入树的元素（库模板有两个标题呈现器）。
        label.Title = text ?? string.Empty;
        label.TitleTemplate = TryResource(TitleTemplateKey) as DataTemplate;
        label.Severity = Severity(tone);
        SetAccessibleName(label, accessibleName);
    }

    private static Style? Style(string key) => TryResource(key) as Style;

    /// <summary>
    /// 带前置图标的标题模板：12px 图标 + 4 DIP 间距 + 文字，与此前手绘图标徽标的排布一致。文字一侧与
    /// <c>AppBadgeTitleTemplate</c> 相同（不换行、可选中）。图标用图标字体的 TextBlock 而不是 FontIcon：
    /// TextBlock 的前景色一定从标题呈现器继承，色调因此不需要在模板里再写一遍。字形逐字符写成数字实体，
    /// 传入的内容不会被当作标记解析。
    /// </summary>
    private static DataTemplate? GlyphTemplate(string glyph)
    {
        if (GlyphTemplates.TryGetValue(glyph, out var cached))
        {
            return cached;
        }

        var entity = string.Concat(glyph.Select(character => "&#x" + ((int)character).ToString("X4") + ";"));
        DataTemplate? template;
        try
        {
            template = XamlReader.Load(
                "<DataTemplate" + Namespaces + "><Grid ColumnSpacing=\"4\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Center\">"
                + "<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions>"
                + "<TextBlock Text=\"" + entity + "\" FontFamily=\"{ThemeResource SymbolThemeFontFamily}\" FontSize=\"12\" FontWeight=\"Normal\""
                + " VerticalAlignment=\"Center\" AutomationProperties.AccessibilityView=\"Raw\" />"
                + "<TextBlock Grid.Column=\"1\" Text=\"{Binding}\" FontFamily=\"{ThemeResource ContentControlThemeFontFamily}\" TextWrapping=\"NoWrap\""
                + " IsTextSelectionEnabled=\"True\" VerticalAlignment=\"Center\" />"
                + "</Grid></DataTemplate>") as DataTemplate;
        }
        catch
        {
            template = null;
        }

        GlyphTemplates[glyph] = template;
        return template;
    }

    /// <summary>安全取应用资源：键缺失或资源系统不可用（关闭中）时返回 null，控件回退库默认外观。</summary>
    private static object? TryResource(string key)
    {
        try
        {
            return Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
