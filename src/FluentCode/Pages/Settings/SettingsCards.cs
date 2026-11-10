using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using FluentCode.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

/// <summary>
/// Windows 11 设置页卡片：CommunityToolkit <see cref="SettingsCard"/> / <see cref="SettingsExpander"/>
/// （图标 · 标题 · 可选的一行说明 · 右对齐控件；窄宽度时控件自动换到标题下方）。组标题 BodyStrong；行间距 4，组间距 28。
/// 画刷、悬停 / 禁用态与高对比度均来自工具包模板的 ThemeResource，运行时切换主题会重新着色。
/// </summary>
internal static class SettingsCards
{
    internal const double RowSpacing = 4;
    internal const double GroupSpacing = 28;
    internal const double ControlWidth = 280;

    /// <summary>卡片外附加内容的左缩进：与卡片标题文字对齐（边距 16 + 图标 20 + 间距 22，同工具包 SettingsExpanderItemPadding 左 58）。</summary>
    private const double TextIndent = 58;

    internal static T Styled<T>(T element, string styleKey) where T : FrameworkElement
    {
        if (Application.Current?.Resources is { } resources && resources.TryGetValue(styleKey, out object? value)
            && value is Style style && style.TargetType is { } target && target.IsAssignableFrom(element.GetType()))
            element.Style = style;
        return element;
    }

    /// <summary>页面内的一组卡片（组标题可省略）。</summary>
    internal static StackPanel Group(string? title, params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = RowSpacing };
        if (title is not null)
            panel.Children.Add(Styled(new TextBlock { Text = title, Margin = new Thickness(1, 0, 0, 4) }, "BodyStrongTextBlockStyle"));
        foreach (var row in rows) panel.Children.Add(row);
        return panel;
    }

    /// <summary>一张设置卡片：图标 · 标题 / 说明 · 右侧控件。</summary>
    internal static SettingsCard Card(string glyph, string header, string? description, FrameworkElement? control, string? automationId = null)
    {
        var card = new SettingsCard { Header = header, HeaderIcon = Icon(glyph), Content = control };
        if (!string.IsNullOrEmpty(description)) card.Description = description;
        AutomationProperties.SetName(card, header);
        if (automationId is not null) AutomationProperties.SetAutomationId(card, automationId);
        return card;
    }

    /// <summary>任意内容的卡片外框（如整块表单）。</summary>
    internal static Border Panel(Border content) => Styled(content, "AppSettingsCardStyle");

    /// <summary>说明文字随状态变化的卡片：<paramref name="note"/> 为空时折叠。</summary>
    internal static SettingsCard Card(string glyph, string header, out TextBlock note, FrameworkElement? control)
    {
        var card = Card(glyph, header, null, control);
        card.Description = note = Note();
        return card;
    }

    internal static void SetNote(TextBlock note, string? text)
    {
        note.Text = text ?? string.Empty;
        note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>可展开卡片：标题行与卡片一致，子项为卡片内以分隔线隔开的行（由工具包模板绘制）。</summary>
    internal static SettingsExpander Expander(string glyph, string header, string? description, FrameworkElement? headerControl, params UIElement[] rows)
    {
        var expander = new SettingsExpander { Header = header, HeaderIcon = Icon(glyph) };
        if (!string.IsNullOrEmpty(description)) expander.Description = description;
        if (headerControl is not null) expander.Content = headerControl;
        foreach (var row in rows) expander.Items.Add(row);
        return expander;
    }

    /// <summary>标题行说明随状态变化的可展开卡片（<paramref name="note"/> 为空时折叠，展开前也能看到）。</summary>
    internal static SettingsExpander Expander(string glyph, string header, out TextBlock note, FrameworkElement? headerControl, params UIElement[] rows)
    {
        var expander = Expander(glyph, header, null, headerControl, rows);
        expander.Description = note = Note();
        return expander;
    }

    /// <summary>可展开卡片内的整行内容（如预览、多行说明），左缘与标题文字对齐、横向铺满。</summary>
    internal static SettingsCard SubContent(UIElement content) => new()
    {
        ContentAlignment = ContentAlignment.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = content,
    };

    /// <summary>卡片下方、不在卡片内的附加内容（如密钥状态与清除选项），与上方卡片的标题文字对齐。</summary>
    internal static Border Indented(UIElement content) => new() { Padding = new Thickness(TextIndent, 8, 0, 8), Child = content };

    internal static T Sized<T>(T control, double width = ControlWidth) where T : FrameworkElement
    {
        control.Width = width;
        return control;
    }

    private static FontIcon Icon(string glyph) => new() { Glyph = glyph };

    /// <summary>说明行占位：作为 Description 常驻（模板只按 null 折叠说明区），文字为空时自身折叠、不占高度。</summary>
    private static TextBlock Note() => new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
}

/// <summary>
/// 可输入也可选择的字体框：可编辑 ComboBox 列出本机字体（<see cref="SystemFontCatalog"/>），也接受手动输入的族名。
/// 提交（回车 / 失焦 / 选择）后校验：非法族名拒绝并还原；本机未找到的族名照常保存但提示将使用回退字体。
/// 第一项为“默认”（返回 null）。
/// </summary>
internal sealed class SettingsFontBox
{
    internal const string DefaultText = "默认";

    private readonly ComboBox _combo;
    private readonly bool _monospaceOnly;
    private IReadOnlyList<SystemFontFamily> _catalog = Array.Empty<SystemFontFamily>();
    private string? _value;
    private bool _applying;

    internal SettingsFontBox(string automationId, string name, bool monospaceOnly)
    {
        _monospaceOnly = monospaceOnly;
        _combo = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Width = SettingsCards.ControlWidth, PlaceholderText = DefaultText };
        AutomationProperties.SetAutomationId(_combo, automationId);
        AutomationProperties.SetName(_combo, name);
        _combo.Items.Add(DefaultText);
        _combo.TextSubmitted += (_, args) => { args.Handled = true; Commit(args.Text); };
        _combo.SelectionChanged += (_, _) =>
        {
            if (_applying || _combo.SelectedItem is not string chosen) return;
            Commit(chosen);
        };
    }

    internal ComboBox Control => _combo;

    /// <summary>用户提交了新的族名（null = 默认）。</summary>
    internal event Action<string?>? Submitted;

    /// <summary>提交被拒绝或族名不在本机字体中时的说明（null = 清除）。</summary>
    internal event Action<string?>? Notice;

    internal bool IsEnabled { get => _combo.IsEnabled; set => _combo.IsEnabled = value; }

    internal void SetCatalog(IReadOnlyList<SystemFontFamily> catalog)
    {
        _catalog = catalog;
        IEnumerable<SystemFontFamily> families = catalog.Where(f => !f.IsSymbol);
        if (_monospaceOnly && families.Any(f => f.IsMonospaced)) families = families.Where(f => f.IsMonospaced);
        _applying = true;
        try
        {
            _combo.Items.Clear();
            _combo.Items.Add(DefaultText);
            foreach (var name in families.Select(f => f.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase)) _combo.Items.Add(name);
        }
        finally { _applying = false; }
        Show(_value);
    }

    /// <summary>显示已保存的值（不触发提交）。</summary>
    internal void Show(string? family)
    {
        _value = family;
        _applying = true;
        try
        {
            var item = family is null ? DefaultText
                : _combo.Items.OfType<string>().FirstOrDefault(i => string.Equals(i, family, StringComparison.OrdinalIgnoreCase));
            if (item is not null) _combo.SelectedItem = item;
            else { _combo.SelectedItem = null; _combo.Text = family!; }
            if (family is null) _combo.Text = DefaultText;
        }
        finally { _applying = false; }
        Notice?.Invoke(family is not null && _catalog.Count > 0 && Find(family) is null
            ? $"本机未找到“{family}”，将使用回退字体显示。" : null);
    }

    private SystemFontFamily? Find(string text) => _catalog.FirstOrDefault(f =>
        string.Equals(f.FamilyName, text, StringComparison.OrdinalIgnoreCase)
        || string.Equals(f.DisplayName, text, StringComparison.OrdinalIgnoreCase));

    private void Commit(string? text)
    {
        if (_applying) return;
        text = text?.Trim();
        string? family;
        if (string.IsNullOrEmpty(text) || text == DefaultText) family = null;
        else if (Find(text) is { } known) family = known.FamilyName; // 本地化名（如“微软雅黑”）映射到族名。
        else if (AppFontDefaults.IsValidFamilyName(text)) family = text;
        else
        {
            Notice?.Invoke($"“{text}”不是有效的字体名称（不能包含逗号或 #）。");
            Show(_value);
            return;
        }

        if (string.Equals(family, _value, StringComparison.OrdinalIgnoreCase)) { Show(_value); return; }
        Submitted?.Invoke(family);
    }
}
