using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal static partial class OpenCodeToolDetailBuiltins
{
    private static FrameworkElement Generic(OpenCodeToolRowModel model, bool compact)
    {
        var panel = new StackPanel { Spacing = 12 };
        // Native error InfoBar is owned by the enclosing detail surface. Preserve invalid-call prose here as well.
        if (model.ToolName == "invalid" && model.Status != OpenCodeToolRowStatus.Error)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "无效的工具调用", Message = model.ErrorText ?? model.OutputText ?? "服务端未提供错误详情" });
        if (OpenCodeToolDetailBuiltinData.Input(model) is { } input)
            panel.Children.Add(Section("输入", JsonProperties(input, compact)));
        else if (model.InputText is { } rawInput) panel.Children.Add(Section("输入", RawExcerpt(rawInput, compact ? 8 : int.MaxValue)));
        if (model.OutputText is { } output)
        {
            JsonElement? parsed = null;
            try { using var document = JsonDocument.Parse(output); parsed = document.RootElement.Clone(); }
            catch (JsonException) { }
            if (parsed is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json)
                panel.Children.Add(Section("输出", compact ? JsonProperties(json, true) : ContentSwitch("属性", "JSON", () => JsonProperties(json, false), () => RawExcerpt(output, int.MaxValue))));
            else panel.Children.Add(Section("输出", RawExcerpt(output, compact ? 8 : int.MaxValue)));
        }
        else if (model.OutputProperties.Count > 0) panel.Children.Add(Section("输出", OpenCodeToolDetailContent.PropertyList(model.OutputProperties)));
        if (panel.Children.Count == 0) panel.Children.Add(OpenCodeToolDetailContent.Caption(model.IsActive ? "工具运行中，暂无内容…" : "（没有可显示的内容）"));
        return panel;
    }

    private static FrameworkElement ContentSwitch(string first, string second, Func<FrameworkElement> primary, Func<FrameworkElement> raw)
    {
        var panel = new StackPanel { Spacing = 12 };
        var selector = new SelectorBar();
        var primaryItem = new SelectorBarItem { Text = first };
        var rawItem = new SelectorBarItem { Text = second };
        selector.Items.Add(primaryItem);
        selector.Items.Add(rawItem);
        selector.SelectedItem = primaryItem;
        var content = new ContentControl { Content = primary(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        selector.SelectionChanged += (_, _) => content.Content = selector.SelectedItem == rawItem ? raw() : primary();
        SwallowTap(selector);
        panel.Children.Add(selector);
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>Native, expandable JSON. Limit counts actual properties, not just top-level keys; “show all” is recoverable.</summary>
    private static FrameworkElement JsonProperties(JsonElement value, bool compact)
    {
        var host = new StackPanel { Spacing = 12 };
        var limit = compact ? 8 : 2000;
        void Rebuild(int maximum)
        {
            host.Children.Clear();
            int shown = 0, total = 0;
            void Count(JsonElement element, int depth)
            {
                if (depth >= 8) return;
                if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) { total++; Count(property.Value, depth + 1); }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { total++; Count(item, depth + 1); }
            }
            Count(value, 0);
            FrameworkElement Build(JsonElement element, int depth)
            {
                if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return RawExcerpt(element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(), compact ? 2 : int.MaxValue);
                var list = new StackPanel { Spacing = 12 };
                if ((element.ValueKind == JsonValueKind.Object && !element.EnumerateObject().Any()) || (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0))
                { list.Children.Add(OpenCodeToolDetailContent.Caption("（空）")); return list; }
                if (depth >= 8) return RawExcerpt(element.GetRawText(), compact ? 2 : int.MaxValue);
                void Add(string key, JsonElement child)
                {
                    if (shown >= maximum) return;
                    shown++;
                    var nested = child.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
                    list.Children.Add(nested ? Disclosure(key, Build(child, depth + 1), false) : Section(key, Build(child, depth + 1)));
                }
                if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) Add(property.Name, property.Value);
                else { var index = 0; foreach (var item in element.EnumerateArray()) Add((index++).ToString(CultureInfo.InvariantCulture), item); }
                return list;
            }
            host.Children.Add(Build(value, 0));
            if (shown < total)
            {
                if (compact) AddMore(host, total - shown, "项");
                else
                {
                    var more = new Button { Content = "已显示 " + shown + " / " + total + " 项 · 显示全部" };
                    more.Click += (_, _) => Rebuild(int.MaxValue);
                    SwallowTap(more);
                    host.Children.Add(more);
                }
            }
        }
        Rebuild(limit);
        return host;
    }

    private static FrameworkElement TodoDetailRow(string content, string status, int index)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var check = new CheckBox { IsThreeState = true, IsChecked = status == "in_progress" ? null : status == "completed", IsHitTestVisible = false, IsTabStop = false, MinWidth = 0, MinHeight = 0, Width = 20, Height = 20, Padding = new Thickness(0) };
        var box = new Viewbox { Width = 16, Height = 16, Child = check, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        row.Children.Add(box);
        var text = OpenCodeInteractionUi.StyledText(content, status is "completed" or "cancelled" ? OpenCodeInteractionUi.BodySubtleStyleKey : OpenCodeInteractionUi.BodyStyleKey);
        text.FontSize = 14;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextDecorations = status is "completed" or "cancelled" ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        AutomationProperties.SetAutomationId(row, "OpenCodeToolDetailTodoItem." + index);
        AutomationProperties.SetName(check, (status switch { "completed" => "已完成", "in_progress" => "进行中", "cancelled" => "已取消", _ => "待处理" }) + "：" + content);
        return row;
    }
}
