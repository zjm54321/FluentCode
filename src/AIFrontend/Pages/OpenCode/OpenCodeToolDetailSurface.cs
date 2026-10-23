using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIFrontend.Pages.OpenCode;

internal static partial class OpenCodeToolDetailContent
{
    [ThreadStatic] private static int _fullDetailBuild;

    private sealed class BuildScope : IDisposable
    {
        private readonly bool _full;
        internal BuildScope(bool full) { _full = full; if (full) _fullDetailBuild++; }
        public void Dispose() { if (_full) _fullDetailBuild--; }
    }

    // Scoped at construction, not a global setting: conversation code blocks keep their six-line collapse.
    internal static IDisposable DetailBuildScope(bool full) => new BuildScope(full);

    private static int CompactRowLimit(OpenCodeToolKind kind) => kind switch
    {
        OpenCodeToolKind.Edit or OpenCodeToolKind.MultiEdit => 10,
        OpenCodeToolKind.List => 6,
        _ => 8,
    };

    private static string ErrorTitle(string? error)
    {
        if (error?.Contains("File not found", StringComparison.OrdinalIgnoreCase) == true) return "文件不存在";
        if (error?.Contains("not a directory", StringComparison.OrdinalIgnoreCase) == true) return "不是目录";
        if (error?.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) == true) return "没有访问权限";
        if (error?.Contains("404", StringComparison.Ordinal) == true) return "HTTP 404";
        return "工具出错";
    }

    private static JsonElement? StateObject(OpenCodeToolRowModel model, string key)
        => model.State is { ValueKind: JsonValueKind.Object } state
            && state.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? Field(JsonElement? source, params string[] keys)
    {
        if (source is not { ValueKind: JsonValueKind.Object } root) return null;
        foreach (var key in keys)
            if (root.TryGetProperty(key, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return null;
    }

    private sealed record Fact(string Key, string Value, bool Link = false);

    private static FrameworkElement FactsBar(OpenCodeToolRowModel model)
    {
        var facts = new List<Fact>();
        var input = StateObject(model, "input");
        var metadata = StateObject(model, "metadata");
        void Add(string key, string? value, bool link = false)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new Fact(key, value, link));
        }
        switch (model.Kind)
        {
            case OpenCodeToolKind.Read:
                Add("范围", model.Summary.Length > 0 ? model.Summary : null);
                Add("文件总行数", ReadTotal(model.OutputText));
                Add("offset", Field(input, "offset"));
                Add("limit", Field(input, "limit"));
                break;
            case OpenCodeToolKind.Edit:
            case OpenCodeToolKind.MultiEdit:
            case OpenCodeToolKind.Patch:
                Add("差异来源", model.DiffFiles.Any(file => !string.IsNullOrEmpty(file.Patch))
                    ? "metadata.files[].patch" : model.DiffFiles.Any(file => file.After is not null) ? "本地对比 oldString/newString" : null);
                break;
            case OpenCodeToolKind.Shell:
                Add("工作目录", Field(input, "workdir", "cwd"));
                Add("超时", Field(input, "timeout", "timeout_ms"));
                if (model.OutputText is { } output) Add("输出行数", output.TrimEnd('\r', '\n').Split('\n').Length.ToString(CultureInfo.InvariantCulture));
                Add("说明", Field(input, "description"));
                break;
            case OpenCodeToolKind.WebFetch:
                Add("URL", model.Url, true);
                Add("格式", Field(input, "format"));
                break;
            case OpenCodeToolKind.Grep:
            case OpenCodeToolKind.Glob:
            case OpenCodeToolKind.List:
                Add("结果", OpenCodeToolDetailBuiltinData.CountHeader(model));
                Add("忽略规则", Field(input, "ignore", "ignores") ?? "未提供");
                break;
        }
        if (model.Truncated) Add("截断", "是");
        var repeater = new ItemsRepeater
        {
            ItemsSource = facts,
            Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 28 },
            ItemTemplate = new LineFactory(item =>
            {
                var fact = (Fact)item;
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                panel.Children.Add(Caption(fact.Key));
                if (fact.Link && Uri.TryCreate(fact.Value, UriKind.Absolute, out var uri))
                    panel.Children.Add(new HyperlinkButton { Content = fact.Value, NavigateUri = uri, Padding = new Thickness(0), FontSize = 12 });
                else panel.Children.Add(Caption(fact.Value));
                return panel;
            }),
            Margin = new Thickness(24, 12, 24, 0),
        };
        AutomationProperties.SetAutomationId(repeater, "OpenCodeToolDetailFacts");
        return repeater;
    }

    private static string? ReadTotal(string? output)
    {
        if (output is null) return null;
        var match = System.Text.RegularExpressions.Regex.Match(output, @"(?:of|共|Total lines:)\s*(\d+)\s*(?:total|lines|行)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static FrameworkElement RawProperties(OpenCodeToolRowModel model)
    {
        var panel = new StackPanel { Spacing = 20 };
        panel.Children.Add(PropertyList(new[]
        {
            new OpenCodeToolProperty(0, "tool", model.ToolName, OpenCodeToolPropertyKind.Code),
            new OpenCodeToolProperty(0, "partID", model.PartId, OpenCodeToolPropertyKind.Code),
        }));
        panel.Children.Add(Section("输入", model.InputProperties.Count > 0 ? PropertyList(model.InputProperties) : CodeBlock(model.InputText ?? "未提供")));
        panel.Children.Add(Section("输出", model.OutputProperties.Count > 0 ? PropertyList(model.OutputProperties) : CodeBlock(model.OutputText ?? "未提供")));
        return panel;
    }

    private static FrameworkElement DetailFooter(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild)
    {
        var grid = new Grid { Height = 52, Padding = new Thickness(24, 0, 24, 0), ColumnSpacing = 12 };
        OpenCodeToolTheme.Apply(grid, OpenCodeToolTheme.Grid(OpenCodeToolTheme.SolidBase));
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toggle = new ToggleButton { Content = "原始数据", IsChecked = options.RawData, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(toggle, "OpenCodeToolDetailRawData");
        toggle.Click += (_, _) => { options.RawData = toggle.IsChecked == true; rebuild(); };
        grid.Children.Add(toggle);
        var source = Caption(model.Truncated ? "输出已被服务端截断" : "来自本次工具调用；未从磁盘补全");
        source.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(source, 1);
        grid.Children.Add(source);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        AddToolActions(actions, model, options);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private static void AddToolActions(StackPanel actions, OpenCodeToolRowModel model, OpenCodeToolDetailOptions options)
    {
        if (model.Kind == OpenCodeToolKind.Shell)
        {
            actions.Children.Add(CopyButton("复制命令", model.Command ?? model.Target));
            if (model.OutputText is { } output) actions.Children.Add(CopyButton("复制输出", output));
        }
        else if (model.Kind == OpenCodeToolKind.Patch)
        {
            if (Field(StateObject(model, "input"), "patchText", "patch") is { } patch) actions.Children.Add(CopyButton("复制补丁原文", patch));
        }
        else if (model.Kind == OpenCodeToolKind.Write)
        {
            var source = model.DiffFiles.Count > 0 ? model.DiffFiles[Math.Clamp(options.SelectedFile, 0, model.DiffFiles.Count - 1)].After : Field(StateObject(model, "input"), "content");
            if (source is not null) actions.Children.Add(CopyButton("复制源码", source));
        }
        else if (model.Kind == OpenCodeToolKind.WebFetch)
        {
            if (model.OutputText is { } content) actions.Children.Add(CopyButton("复制内容", content));
            if (Uri.TryCreate(model.Url, UriKind.Absolute, out var uri)) actions.Children.Add(new HyperlinkButton { Content = "在浏览器中打开", NavigateUri = uri });
        }
    }

    private static Button CopyButton(string label, string source)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) =>
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(source);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                ToolTipService.SetToolTip(button, "已复制");
            }
            catch (Exception) { ToolTipService.SetToolTip(button, "复制失败，请选择文本复制"); }
        };
        return button;
    }
}
