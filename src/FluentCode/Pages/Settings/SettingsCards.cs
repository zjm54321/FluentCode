using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.Settings;

/// <summary>
/// Windows 11 设置页式的轻量卡片（与 CommunityToolkit SettingsCard / SettingsExpander 同一版式，未引入额外依赖）：
/// 图标 · 标题 · 可选的一行说明 · 右对齐控件；组标题 BodyStrong；行间距 4，组间距 28。
/// 画刷全部经样式里的 ThemeResource 设置，运行时切换主题会重新着色。
/// </summary>
internal static class SettingsCards
{
    internal const double RowSpacing = 4;
    internal const double GroupSpacing = 28;
    internal const double ControlWidth = 280;
    internal const double RowMinHeight = 68;

    private static Style? s_cardStyle, s_dividerStyle;

    private static Style CardStyle => s_cardStyle ??= (Style)XamlReader.Load(
        "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Border\">" +
        "<Setter Property=\"Background\" Value=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\"/>" +
        "<Setter Property=\"BorderBrush\" Value=\"{ThemeResource CardStrokeColorDefaultBrush}\"/>" +
        "<Setter Property=\"BorderThickness\" Value=\"1\"/>" +
        "<Setter Property=\"CornerRadius\" Value=\"{ThemeResource ControlCornerRadius}\"/></Style>");

    private static Style DividerStyle => s_dividerStyle ??= (Style)XamlReader.Load(
        "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Border\">" +
        "<Setter Property=\"Background\" Value=\"{ThemeResource CardStrokeColorDefaultBrush}\"/>" +
        "<Setter Property=\"Height\" Value=\"1\"/></Style>");

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
    internal static Border Card(string glyph, string header, string? description, FrameworkElement? control, string? automationId = null)
    {
        var card = new Border { Style = CardStyle, Child = Row(glyph, header, description, control, new Thickness(16, 10, 16, 10)) };
        if (automationId is not null) AutomationProperties.SetAutomationId(card, automationId);
        return card;
    }

    /// <summary>任意内容的卡片外框（如整块表单）。</summary>
    internal static Border Panel(Border content)
    {
        content.Style = CardStyle;
        return content;
    }

    /// <summary>说明文字随状态变化的卡片：<paramref name="note"/> 为空时折叠。</summary>
    internal static Border Card(string glyph, string header, out TextBlock note, FrameworkElement? control)
    {
        var card = Card(glyph, header, " ", control);
        note = (TextBlock)((StackPanel)((Grid)card.Child).Children[1]).Children[1];
        note.Text = string.Empty;
        note.Visibility = Visibility.Collapsed;
        return card;
    }

    internal static void SetNote(TextBlock note, string? text)
    {
        note.Text = text ?? string.Empty;
        note.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>卡片内容：供 <see cref="Card"/> 与 <see cref="Expander"/> 的标题共用。</summary>
    internal static Grid Row(string? glyph, string header, string? description, FrameworkElement? control, Thickness padding)
    {
        var grid = new Grid { MinHeight = glyph is null ? 48 : RowMinHeight - 2, Padding = padding, ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph is not null)
            grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(Styled(new TextBlock { Text = header, TextWrapping = TextWrapping.Wrap }, "BodyTextBlockStyle"));
        if (!string.IsNullOrEmpty(description))
            text.Children.Add(Styled(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap }, "CaptionSubtleTextBlockStyle"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        AutomationProperties.SetName(grid, header);
        return grid;
    }

    /// <summary>可展开卡片（SettingsExpander 式）：标题行与卡片一致，子选项为卡片内以分隔线隔开的行。</summary>
    internal static Expander Expander(string glyph, string header, string? description, FrameworkElement? headerControl, params UIElement[] rows)
    {
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = Row(glyph, header, description, headerControl, new Thickness(0, 8, 0, 8)),
        };
        var body = new StackPanel();
        bool first = true;
        foreach (var row in rows)
        {
            if (!first) body.Children.Add(new Border { Style = DividerStyle, Margin = new Thickness(-16, 0, -16, 0) });
            first = false;
            body.Children.Add(row);
        }
        expander.Content = body;
        return expander;
    }

    /// <summary>标题行说明随状态变化的可展开卡片（<paramref name="note"/> 为空时折叠，展开前也能看到）。</summary>
    internal static Expander Expander(string glyph, string header, out TextBlock note, FrameworkElement? headerControl, params UIElement[] rows)
    {
        var expander = Expander(glyph, header, " ", headerControl, rows);
        note = (TextBlock)((StackPanel)((Grid)expander.Header).Children[1]).Children[1];
        note.Text = string.Empty;
        note.Visibility = Visibility.Collapsed;
        return expander;
    }

    /// <summary>可展开卡片内的一行（无图标，左侧缩进与标题文字对齐）。</summary>
    internal static Grid SubRow(string header, string? description, FrameworkElement? control)
        => Row(null, header, description, control, new Thickness(42, 8, 0, 8));

    /// <summary>可展开卡片内的整行内容（如预览、多行说明）。</summary>
    internal static Border SubContent(UIElement content) => new() { Padding = new Thickness(42, 12, 0, 12), Child = content };

    internal static T Sized<T>(T control, double width = ControlWidth) where T : FrameworkElement
    {
        control.Width = width;
        return control;
    }

    internal static TextBlock Subtle(string text) => Styled(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, "CaptionSubtleTextBlockStyle");
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
