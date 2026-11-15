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
/// <c>TitleTemplate</c>（每个字形一份模板，见 <see cref="TitleTemplate"/>），所以有无图标是同一个控件、同一套
/// 盒子与配色。字形和文字都从标题呈现器继承前景色，因此同样不需要设置 <c>Foreground</c>。
/// 来源：https://github.com/ghost1372/DevWinUI，提交 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e，
/// dev/DevWinUI/Themes/Styles/Controls/IssueLabel.xaml（SeverityTitle / LabelTitle 的 ContentTemplate 绑定）。
/// </para>
/// <para>
/// <b>不要设置 <c>Foreground</c>：</b>设置自定义前景会切换到第二个呈现器分支，从而绕过严重度配色。
/// 色调只通过 <see cref="CommunityTextBadgeTone"/> 表达。状态色调的文字颜色写在标题模板里（D-13），
/// 那是模板内部的颜色，控件自身的 <c>Foreground</c> 仍然不设置。
/// </para>
/// </remarks>
internal static class CommunityTextBadge
{
    internal const string CompactStyleKey = "AppCompactTextBadgeStyle";
    internal const string PillStyleKey = "AppPillTextBadgeStyle";
    internal const string TitleTemplateKey = "AppBadgeTitleTemplate";

    private const string Namespaces = " xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    private const string XNamespace = " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    // 色调覆盖：库的分类色是写死的色值，不跟随应用。这里按“轻量样式”的做法，在每个徽标自己的资源里用同名键
    // 覆盖库模板读取的画刷，控件模板和它的状态切换都不用改；深色与浅色各一份，高对比度留空，沿用库自己的
    // 高对比度取值（窗口底色 + 窗口文字色）。
    // 来源：https://github.com/ghost1372/DevWinUI，提交 6c6c70d55cb7487d3a2aa506d0055ffa0e52ef6e，
    // dev/DevWinUI/Themes/Styles/Controls/IssueLabel.xaml（Severity* 状态用 {ThemeResource IssueLabel*Brush} 取色）。
    //
    // D-10 强调色调（Attention）跟随系统强调色，与卡片上其他“信息”元素一致：底色为强调色 12%，
    // 描边为强调色 40%，文字为强调文字色（深色 SystemAccentColorLight3，浅色 SystemAccentColorDark2，
    // 即 AccentTextFillColorPrimaryBrush 的取值）。
    //
    // D-13 状态色调（成功 / 注意 / 失败）的描边：库模板让描边和文字读同一把画刷（SystemFillColor*Brush），
    // 描边因此是满强度的状态色，比分类色调的描边重。这里把这把画刷在徽标内覆盖为同色 40%，描边随之变淡；
    // 文字不能跟着变淡，所以状态色调的标题模板自带一把满强度的文字画刷（见 TitleTemplate）。
    // 深色与浅色写法相同：颜色键本身随主题取值。高对比度不覆盖：系统在高对比度下把这三把画刷定为窗口文字色，
    // 而同名的颜色键在高对比度下只是占位值，不能使用。
    // 来源：https://github.com/microsoft/microsoft-ui-xaml，main 分支，
    // src/controls/dev/CommonStyles/Common_themeresources_any.xaml（SystemFillColor* 在三个主题字典中的取值）。
    private const string StatusRings =
        "<SolidColorBrush x:Key=\"SystemFillColorSuccessBrush\" Color=\"{ThemeResource SystemFillColorSuccess}\" Opacity=\"0.4\" />"
        + "<SolidColorBrush x:Key=\"SystemFillColorCautionBrush\" Color=\"{ThemeResource SystemFillColorCaution}\" Opacity=\"0.4\" />"
        + "<SolidColorBrush x:Key=\"SystemFillColorCriticalBrush\" Color=\"{ThemeResource SystemFillColorCritical}\" Opacity=\"0.4\" />";
    private const string DarkTones =
        "<SolidColorBrush x:Key=\"IssueLabelBlueBackgroundBrush\" Color=\"{ThemeResource SystemAccentColorLight2}\" Opacity=\"0.12\" />"
        + "<SolidColorBrush x:Key=\"IssueLabelBlueBorderBrush\" Color=\"{ThemeResource SystemAccentColorLight2}\" Opacity=\"0.4\" />"
        + "<SolidColorBrush x:Key=\"IssueLabelBlueForegroundBrush\" Color=\"{ThemeResource SystemAccentColorLight3}\" />"
        + StatusRings;
    private const string LightTones =
        "<SolidColorBrush x:Key=\"IssueLabelBlueBackgroundBrush\" Color=\"{ThemeResource SystemAccentColorDark1}\" Opacity=\"0.12\" />"
        + "<SolidColorBrush x:Key=\"IssueLabelBlueBorderBrush\" Color=\"{ThemeResource SystemAccentColorDark1}\" Opacity=\"0.4\" />"
        + "<SolidColorBrush x:Key=\"IssueLabelBlueForegroundBrush\" Color=\"{ThemeResource SystemAccentColorDark2}\" />"
        + StatusRings;
    private const string ToneResources =
        "<ResourceDictionary" + Namespaces + XNamespace + "><ResourceDictionary.ThemeDictionaries>"
        + "<ResourceDictionary x:Key=\"Default\">" + DarkTones + "</ResourceDictionary>"
        + "<ResourceDictionary x:Key=\"Light\">" + LightTones + "</ResourceDictionary>"
        + "<ResourceDictionary x:Key=\"HighContrast\" />"
        + "</ResourceDictionary.ThemeDictionaries></ResourceDictionary>";
    private const string InkBrushKey = "CommunityTextBadgeInkBrush";
    // 每个“字形 + 文字颜色”组合一份模板。模板不是控件，也不持有控件；字形来自应用内固定的图标表，
    // 文字颜色只有三种状态色，所以条目数有上限。
    private static readonly Dictionary<string, DataTemplate?> TitleTemplates = new(StringComparer.Ordinal);

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
        // 色调覆盖要在控件模板套用之前就位，所以在构造时设置；每个徽标一份字典，不共享实例。
        // 解析失败时不设置：徽标退回库自带的配色，文字和形状不受影响。
        try
        {
            label.Resources = (ResourceDictionary)XamlReader.Load(ToneResources);
        }
        catch
        {
        }

        Apply(label, text, tone, accessibleName);
        label.Tag = string.IsNullOrEmpty(glyph) ? null : glyph;
        Retitle(label);
        if (label.Tag is not null)
        {
            label.Visibility = Visibility.Visible;
        }

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
        Retitle(label);
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
            Retitle(label);
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

    /// <summary>状态色调的文字颜色键；其余色调返回 <c>null</c>，文字从标题呈现器继承库给的颜色。</summary>
    private static string? Ink(IssueLabelSeverity severity) => severity switch
    {
        IssueLabelSeverity.Success => "SystemFillColorSuccess",
        IssueLabelSeverity.Warning => "SystemFillColorCaution",
        IssueLabelSeverity.Error => "SystemFillColorCritical",
        _ => null,
    };

    /// <summary>按徽标当前的字形和色调选标题模板：两者都不需要时用应用的纯文字模板。</summary>
    private static void Retitle(IssueLabel label)
    {
        var glyph = label.Tag as string;
        var ink = Ink(label.Severity);
        var template = glyph is null && ink is null ? null : TitleTemplate(glyph, ink);
        if (ink is not null && template is null)
        {
            // 描边变淡靠的是覆盖描边和文字共用的那把画刷，文字要靠模板单独上色。模板建不出来时撤掉这个徽标的
            // 全部覆盖，整个徽标退回库自带的配色，文字不会跟着变淡。
            label.Resources = new ResourceDictionary();
            template = glyph is null ? null : TitleTemplate(glyph, null);
        }

        // 模板解析失败时退回纯文字模板：徽标少一个图标，但文字照常显示。
        var next = template ?? TryResource(TitleTemplateKey) as DataTemplate;
        if (!ReferenceEquals(label.TitleTemplate, next))
        {
            label.TitleTemplate = next;
        }
    }

    /// <summary>
    /// 适配器自建的标题模板：可选的 12px 前置图标 + 4 DIP 间距 + 文字，与此前手绘图标徽标的排布一致；文字一侧与
    /// <c>AppBadgeTitleTemplate</c> 相同（不换行、可选中）。图标用图标字体的 TextBlock 而不是 FontIcon：
    /// TextBlock 的前景色一定从标题呈现器继承，分类色调因此不需要在模板里写颜色。字形逐字符写成数字实体，
    /// 传入的内容不会被当作标记解析。
    /// <para>
    /// <paramref name="ink"/> 不为空时（状态色调），图标和文字改用模板根上的一把画刷：深色与浅色取该颜色键，
    /// 高对比度取窗口文字色。画刷定义在模板自己的资源里，离文字最近，所以不受徽标上“描边变淡”覆盖的影响。
    /// 这是模板内的颜色，不是控件的 <c>Foreground</c>，库模板仍然走严重度呈现器。
    /// </para>
    /// </summary>
    private static DataTemplate? TitleTemplate(string? glyph, string? ink)
    {
        var key = (ink ?? string.Empty) + "\n" + (glyph ?? string.Empty);
        if (TitleTemplates.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var entity = glyph is null ? string.Empty : string.Concat(glyph.Select(character => "&#x" + ((int)character).ToString("X4") + ";"));
        var color = ink is null ? string.Empty : " Foreground=\"{ThemeResource " + InkBrushKey + "}\"";
        string InkBrush(string colorKey) => "<SolidColorBrush x:Key=\"" + InkBrushKey + "\" Color=\"{ThemeResource " + colorKey + "}\" />";
        var resources = ink is null
            ? string.Empty
            : "<Grid.Resources><ResourceDictionary><ResourceDictionary.ThemeDictionaries>"
                + "<ResourceDictionary x:Key=\"Default\">" + InkBrush(ink) + "</ResourceDictionary>"
                + "<ResourceDictionary x:Key=\"Light\">" + InkBrush(ink) + "</ResourceDictionary>"
                + "<ResourceDictionary x:Key=\"HighContrast\">" + InkBrush("SystemColorWindowTextColor") + "</ResourceDictionary>"
                + "</ResourceDictionary.ThemeDictionaries></ResourceDictionary></Grid.Resources>";
        var icon = glyph is null
            ? string.Empty
            : "<Grid.ColumnDefinitions><ColumnDefinition Width=\"Auto\" /><ColumnDefinition Width=\"*\" /></Grid.ColumnDefinitions>"
                + "<TextBlock Text=\"" + entity + "\" FontFamily=\"{ThemeResource SymbolThemeFontFamily}\" FontSize=\"12\" FontWeight=\"Normal\""
                + color + " VerticalAlignment=\"Center\" AutomationProperties.AccessibilityView=\"Raw\" />";
        // 没有图标的模板与应用的纯文字模板一致，放不下时以省略号结尾。
        var text = "<TextBlock" + (glyph is null ? " TextTrimming=\"CharacterEllipsis\"" : " Grid.Column=\"1\"")
            + " Text=\"{Binding}\" FontFamily=\"{ThemeResource ContentControlThemeFontFamily}\" TextWrapping=\"NoWrap\""
            + color + " IsTextSelectionEnabled=\"True\" VerticalAlignment=\"Center\" />";
        DataTemplate? template;
        try
        {
            template = XamlReader.Load(
                "<DataTemplate" + Namespaces + XNamespace + "><Grid ColumnSpacing=\"4\" VerticalAlignment=\"Center\" HorizontalAlignment=\"Center\">"
                + resources + icon + text + "</Grid></DataTemplate>") as DataTemplate;
        }
        catch
        {
            template = null;
        }

        TitleTemplates[key] = template;
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
