using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 跟随主题切换的样式工厂：用 XamlReader 构造仅含 {ThemeResource …} Setter 的 Style（内置画刷键，不定义颜色），
/// 可选 BasedOn 内置/应用文本样式。按键缓存；不可用时返回 null（控件回退默认外观）。
/// </summary>
internal static class OpenCodeToolTheme
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Dictionary<string, Style?> Cache = new(StringComparer.Ordinal);

    internal const string Success = "SystemFillColorSuccessBrush";
    internal const string Critical = "SystemFillColorCriticalBrush";
    internal const string Caution = "SystemFillColorCautionBrush";
    internal const string Attention = "SystemFillColorAttentionBrush";
    internal const string SuccessBackground = "SystemFillColorSuccessBackgroundBrush";
    internal const string CriticalBackground = "SystemFillColorCriticalBackgroundBrush";
    internal const string CautionBackground = "SystemFillColorCautionBackgroundBrush";
    internal const string TextTertiary = "TextFillColorTertiaryBrush";
    internal const string TextSecondary = "TextFillColorSecondaryBrush";
    internal const string Accent = "AccentFillColorDefaultBrush";
    internal const string Divider = "DividerStrokeColorDefaultBrush";
    internal const string CardStroke = "CardStrokeColorDefaultBrush";
    internal const string SolidBase = "SolidBackgroundFillColorBaseBrush";
    internal const string SubtleSecondary = "SubtleFillColorSecondaryBrush";

    /// <summary>TextBlock 前景色样式（可基于已有文本样式，例如 OpenCodeCodeTextBlockStyle / CaptionTextBlockStyle）。</summary>
    internal static Style? Text(string brushKey, string? basedOnKey = null)
        => Get("TextBlock", basedOnKey, ("Foreground", brushKey));

    /// <summary>FontIcon 前景色样式。</summary>
    internal static Style? Icon(string brushKey) => Get("FontIcon", null, ("Foreground", brushKey));

    /// <summary>Border 背景（可选描边）样式。</summary>
    internal static Style? Border(string backgroundKey, string? borderBrushKey = null)
        => borderBrushKey is null
            ? Get("Border", null, ("Background", backgroundKey))
            : Get("Border", null, ("Background", backgroundKey), ("BorderBrush", borderBrushKey));

    /// <summary>Grid 背景样式。</summary>
    internal static Style? Grid(string backgroundKey) => Get("Grid", null, ("Background", backgroundKey));

    /// <summary>
    /// ProgressRing 前景色样式（只含 Foreground 的 ThemeResource Setter；模板仍来自控件默认样式）。
    /// 用于按状态着色的工作环（强调色 / 注意 / 严重），主题切换时由框架自动重取。
    /// </summary>
    internal static Style? Ring(string brushKey) => Get("ProgressRing", null, ("Foreground", brushKey));

    /// <summary>应用样式（null 时不改变控件，保持默认外观）。</summary>
    internal static void Apply(FrameworkElement element, Style? style)
    {
        if (style is not null)
        {
            element.Style = style;
        }
    }

    private static Style? Get(string targetType, string? basedOnKey, params (string Property, string Key)[] setters)
    {
        var cacheKey = targetType + "|" + (basedOnKey ?? string.Empty);
        foreach (var (property, key) in setters)
        {
            cacheKey += "|" + property + "=" + key;
        }

        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        Style? style = null;
        try
        {
            var xaml = new StringBuilder();
            xaml.Append("<Style xmlns=\"").Append(XamlNamespace).Append("\" TargetType=\"").Append(targetType).Append("\">");
            foreach (var (property, key) in setters)
            {
                xaml.Append("<Setter Property=\"").Append(property).Append("\" Value=\"{ThemeResource ").Append(key).Append("}\"/>");
            }

            xaml.Append("</Style>");
            style = XamlReader.Load(xaml.ToString()) as Style;
            if (style is not null && basedOnKey is not null && OpenCodeInteractionUi.TryStyle(basedOnKey) is { } basedOn)
            {
                style.BasedOn = basedOn;
            }
        }
        catch
        {
            style = null;
        }

        Cache[cacheKey] = style;
        return style;
    }
}

/// <summary>详情内容的显示选项（随弹出层生命周期保留）。</summary>
internal sealed class OpenCodeToolDetailOptions
{
    /// <summary>详情文字总是自动换行（G8：代码、diff、read、shell 输出、属性值）；不再提供“换行”开关。</summary>
    public bool Wrap => true;

    public bool Unified { get; set; }

    public bool ShowAll { get; set; }

    public int SelectedFile { get; set; }

    /// <summary>已展开的折叠段（按折叠行首行号标识）。</summary>
    public HashSet<string> ExpandedFolds { get; } = new(StringComparer.Ordinal);
}

/// <summary>构建结果：根元素 + 主滚动区（用于运行中刷新时保持/跟随滚动位置）。</summary>
internal sealed record OpenCodeToolDetailView(FrameworkElement Root, ScrollViewer? Scroller);

/// <summary>
/// 工具详情弹出层的内容构建（§6.3）：read 带行号全文；edit/patch/write/multiedit 并排 diff（可切统一视图），
/// 长未改动段折叠；shell 命令 + 完整输出；grep/glob/list 结果列表；webfetch URL + 内容；其他工具 输入/输出/错误。
/// 长内容经 ItemsRepeater 虚拟化，超过 <see cref="MaxRows"/> 行先显示前段并提供“显示全部”。
/// 全部使用原生控件 + 内置文本样式 + 主题画刷（经 <see cref="OpenCodeToolTheme"/>），不定义颜色、不自定义模板。
/// </summary>
internal static class OpenCodeToolDetailContent
{
    internal const int MaxRows = 20000;

    private sealed record CodeLine(int? Number, string Text);

    private sealed record PlainLine(string Text);

    private sealed record ActionLine(string Text, Action Invoke, string AutomationId);

    /// <summary>ItemsRepeater 的纯 C# 元素工厂（无 XAML 模板；按数据类型构建原生元素）。</summary>
    private sealed class LineFactory : IElementFactory
    {
        private readonly Func<object, UIElement> _build;

        public LineFactory(Func<object, UIElement> build) => _build = build;

        public UIElement GetElement(ElementFactoryGetArgs args) => _build(args.Data);

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
        }
    }

    internal static OpenCodeToolDetailView Build(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rebuild);

        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var banners = new StackPanel { Spacing = 4 };
        if (model.Status == OpenCodeToolRowStatus.Error)
        {
            banners.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Title = "工具出错",
                Message = model.ErrorText ?? "（服务端未提供错误详情）",
            });
        }

        if (model.Truncated)
        {
            banners.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                IsOpen = true,
                IsClosable = false,
                Message = "输出已被服务端截断",
            });
        }

        Grid.SetRow(banners, 0);
        root.Children.Add(banners);

        var top = new StackPanel { Spacing = 4 };
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        var rows = new List<object>();
        FrameworkElement? body = null;

        switch (model.Kind)
        {
            case OpenCodeToolKind.Read:
                BuildRead(model, rows, top);
                break;
            case OpenCodeToolKind.Edit:
            case OpenCodeToolKind.MultiEdit:
            case OpenCodeToolKind.Patch:
            case OpenCodeToolKind.Write:
                if (model.DiffFiles.Count > 0)
                {
                    BuildDiff(model, options, rebuild, rows, top);
                }
                else
                {
                    body = BuildGeneric(model);
                }

                break;
            case OpenCodeToolKind.Shell:
            {
                top.Children.Add(CodeBlock("$ " + (model.Command ?? model.Target)));
                var extra = new List<OpenCodeToolProperty>();
                var skippingCommand = false;
                foreach (var property in model.InputProperties)
                {
                    // 命令本身已在上方代码块中显示；其余输入（说明、超时、工作目录等）以属性列表补充。
                    if (property.Depth == 0)
                    {
                        skippingCommand = property.Key is "command" or "cmd";
                    }

                    if (!skippingCommand)
                    {
                        extra.Add(property);
                    }
                }

                if (extra.Count > 0)
                {
                    top.Children.Add(PropertyList(extra));
                }

                AddPlainLines(model.OutputText, rows);
                if (rows.Count == 0)
                {
                    top.Children.Add(Caption(model.IsActive ? "命令运行中，暂无输出…" : "（没有输出）"));
                }

                break;
            }

            case OpenCodeToolKind.Grep:
            case OpenCodeToolKind.Glob:
            case OpenCodeToolKind.List:
                AddPlainLines(model.OutputText, rows);
                if (rows.Count == 0)
                {
                    top.Children.Add(Caption(model.IsActive ? "正在查找…" : "（没有结果）"));
                }

                break;
            case OpenCodeToolKind.WebFetch:
            {
                if (!string.IsNullOrEmpty(model.Url))
                {
                    var url = OpenCodeInteractionUi.StyledText(model.Url!, OpenCodeInteractionUi.CodeStyleKey, true);
                    url.TextWrapping = TextWrapping.Wrap;
                    top.Children.Add(url);
                }

                AddPlainLines(model.OutputText, rows);
                break;
            }

            default:
                body = BuildGeneric(model);
                break;
        }

        foreach (var file in model.FileOutputs)
        {
            top.Children.Add(Caption(file));
        }

        if (model.RawState is { } rawState)
        {
            top.Children.Add(Caption("状态未识别，以下为服务端返回的状态内容："));
            top.Children.Add(model.RawStateProperties.Count > 0 ? PropertyList(model.RawStateProperties) : CodeBlock(rawState));
        }

        ScrollViewer? scroller = null;
        if (body is null)
        {
            if (!options.ShowAll && rows.Count > MaxRows)
            {
                var total = rows.Count;
                rows.RemoveRange(MaxRows, rows.Count - MaxRows);
                rows.Add(new ActionLine(
                    "已显示前 " + MaxRows.ToString(CultureInfo.InvariantCulture) + " 行（共 " + total.ToString(CultureInfo.InvariantCulture) + " 行）· 显示全部",
                    () =>
                    {
                        options.ShowAll = true;
                        rebuild();
                    },
                    "OpenCodeToolDetailShowAll"));
            }

            var unified = options.Unified;
            var repeater = new ItemsRepeater
            {
                ItemsSource = rows,
                ItemTemplate = new LineFactory(data => BuildRow(data, unified)),
            };
            body = repeater;
        }

        scroller = new ScrollViewer
        {
            Content = body,
            // G8：内容总是换行 → 无横向滚动，宽度受限于详情视图，长行在行内折行。
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetAutomationId(scroller, "OpenCodeToolDetailScroller");
        Grid.SetRow(scroller, 2);
        root.Children.Add(scroller);
        return new OpenCodeToolDetailView(root, scroller);
    }

    private static void BuildRead(OpenCodeToolRowModel model, List<object> rows, StackPanel top)
    {
        if (string.IsNullOrEmpty(model.OutputText))
        {
            if (model.FileOutputs.Count == 0)
            {
                top.Children.Add(Caption(model.IsActive ? "正在读取…" : "（没有内容）"));
            }

            return;
        }

        foreach (var line in model.OutputText.Split('\n'))
        {
            if (OpenCodeToolRowPresentation.TrySplitNumberedLine(line, out var number, out var content))
            {
                rows.Add(new CodeLine(number, content));
            }
            else if (rows.Count == 0 && line.Length > 0)
            {
                // 首行“Read file X, lines a-b”等说明作为标题说明。
                top.Children.Add(Caption(line.TrimEnd('\r')));
            }
            else if (line.Length > 0)
            {
                rows.Add(new CodeLine(null, line.TrimEnd('\r')));
            }
        }
    }

    private static void BuildDiff(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, List<object> rows, StackPanel top)
    {
        var files = model.DiffFiles;
        var index = Math.Clamp(options.SelectedFile, 0, files.Count - 1);
        if (files.Count > 1)
        {
            var list = new RadioButtons { MaxColumns = 1 };
            AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailFiles");
            AutomationProperties.SetName(list, "改动的文件");
            foreach (var file in files)
            {
                list.Items.Add(FileLabel(file));
            }

            list.SelectedIndex = index;
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex != options.SelectedFile)
                {
                    options.SelectedFile = list.SelectedIndex;
                    options.ExpandedFolds.Clear();
                    options.ShowAll = false;
                    rebuild();
                }
            };
            top.Children.Add(list);
        }
        else
        {
            var label = OpenCodeInteractionUi.StyledText(FileLabel(files[0]), OpenCodeInteractionUi.CodeStyleKey, true);
            label.TextWrapping = TextWrapping.Wrap;
            top.Children.Add(label);
        }

        var selected = files[index];
        List<OpenCodeDiffLine> unified;
        if (!string.IsNullOrEmpty(selected.Patch))
        {
            unified = OpenCodeLineDiff.ParseUnified(selected.Patch);
        }
        else if (selected.After is not null)
        {
            unified = OpenCodeLineDiff.Compute(selected.Before ?? string.Empty, selected.After);
            top.Children.Add(Caption(model.Kind == OpenCodeToolKind.Write ? "新写入的完整内容（本地计算）" : "根据工具输入本地计算的差异"));
        }
        else
        {
            top.Children.Add(Caption("服务端未提供该文件的差异内容。"));
            return;
        }

        var sideRows = OpenCodeLineDiff.Fold(OpenCodeLineDiff.ToSideBySide(unified));
        foreach (var row in sideRows)
        {
            if (row.IsFold)
            {
                var foldKey = (row.Folded![0].Left?.Number ?? 0).ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture);
                if (options.ExpandedFolds.Contains(foldKey))
                {
                    foreach (var inner in row.Folded)
                    {
                        rows.Add(options.Unified ? ToUnified(inner) : inner);
                    }
                }
                else
                {
                    rows.Add(new ActionLine(
                        "⋯ 展开 " + row.Folded.Count.ToString(CultureInfo.InvariantCulture) + " 行未改动 ⋯",
                        () =>
                        {
                            options.ExpandedFolds.Add(foldKey);
                            rebuild();
                        },
                        "OpenCodeToolDetailExpandFold"));
                }

                continue;
            }

            if (!options.Unified)
            {
                rows.Add(row);
            }
            else if (row.IsHunk)
            {
                rows.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Hunk, null, null, row.HunkText!));
            }
            else if (row.Left?.Kind == OpenCodeDiffLineKind.Context)
            {
                rows.Add(ToUnified(row));
            }
            else
            {
                if (row.Left is { } left)
                {
                    rows.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Removed, left.Number, null, left.Text));
                }

                if (row.Right is { } right)
                {
                    rows.Add(new OpenCodeDiffLine(OpenCodeDiffLineKind.Added, null, right.Number, right.Text));
                }
            }
        }

        if (options.Unified)
        {
            // 统一视图中删除段应集中在新增段之前（与统一 diff 一致）：按原始统一行重排相邻改动块。
            ReorderUnified(rows);
        }
    }

    private static void ReorderUnified(List<object> rows)
    {
        var i = 0;
        while (i < rows.Count)
        {
            if (rows[i] is not OpenCodeDiffLine { Kind: OpenCodeDiffLineKind.Removed or OpenCodeDiffLineKind.Added })
            {
                i++;
                continue;
            }

            var start = i;
            var removed = new List<object>();
            var added = new List<object>();
            while (i < rows.Count && rows[i] is OpenCodeDiffLine { Kind: OpenCodeDiffLineKind.Removed or OpenCodeDiffLineKind.Added } line)
            {
                (line.Kind == OpenCodeDiffLineKind.Removed ? removed : added).Add(line);
                i++;
            }

            rows.RemoveRange(start, i - start);
            rows.InsertRange(start, removed);
            rows.InsertRange(start + removed.Count, added);
        }
    }

    private static OpenCodeDiffLine ToUnified(OpenCodeDiffRow row)
        => new(OpenCodeDiffLineKind.Context, row.Left?.Number, row.Right?.Number, row.Left?.Text ?? row.Right?.Text ?? string.Empty);

    private static string FileLabel(OpenCodeToolDiffFile file)
    {
        var builder = new StringBuilder(file.Path);
        if (file.Additions is { } add || file.Deletions is not null)
        {
            builder.Append("  +").Append((file.Additions ?? 0).ToString(CultureInfo.InvariantCulture))
                .Append(" −").Append((file.Deletions ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        var status = file.Status switch
        {
            "added" => "新增文件",
            "deleted" => "已删除",
            "modified" => null,
            null => null,
            var other => other,
        };
        if (status is not null)
        {
            builder.Append("  （").Append(status).Append('）');
        }

        return builder.ToString();
    }

    private static void AddPlainLines(string? text, List<object> rows)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var line in text.Split('\n'))
        {
            rows.Add(new PlainLine(line.TrimEnd('\r')));
        }

        // 去掉末尾空行（输出常以换行结尾）。
        while (rows.Count > 0 && rows[^1] is PlainLine { Text.Length: 0 })
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }

    private static FrameworkElement BuildGeneric(OpenCodeToolRowModel model)
    {
        var panel = new StackPanel { Spacing = 16 };
        if (model.InputProperties.Count > 0)
        {
            panel.Children.Add(Section("输入", PropertyList(model.InputProperties)));
        }
        else if (model.InputText is { } input)
        {
            // 字符串输入（或流式生成中的原始文本）：按等宽块显示。
            panel.Children.Add(Section("输入", CodeBlock(input)));
        }

        if (model.OutputProperties.Count > 0)
        {
            panel.Children.Add(Section("输出", PropertyList(model.OutputProperties)));
        }
        else if (model.OutputText is { } output)
        {
            panel.Children.Add(Section("输出", CodeBlock(output)));
        }

        if (model.ErrorText is { } error && model.Status == OpenCodeToolRowStatus.Error)
        {
            var text = OpenCodeInteractionUi.StyledText(error, OpenCodeInteractionUi.BodyStyleKey, true);
            OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(OpenCodeToolTheme.Critical, OpenCodeInteractionUi.BodyStyleKey));
            text.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(Section("错误", text));
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(Caption(model.IsActive ? "工具运行中，暂无内容…" : "（没有可显示的内容）"));
        }

        return panel;
    }

    private static UIElement BuildRow(object data, bool unified)
    {
        switch (data)
        {
            case CodeLine code:
                return LineGrid(code.Number, null, false, null, code.Text, null, null);
            case PlainLine plain:
            {
                var text = Mono(plain.Text.Length == 0 ? " " : plain.Text);
                return text;
            }

            case ActionLine action:
            {
                var button = new HyperlinkButton
                {
                    Content = action.Text,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                AutomationProperties.SetAutomationId(button, action.AutomationId);
                button.Click += (_, _) => action.Invoke();
                return button;
            }

            case OpenCodeDiffLine line:
                return line.Kind switch
                {
                    OpenCodeDiffLineKind.Hunk or OpenCodeDiffLineKind.Meta => HunkRow(line.Text),
                    OpenCodeDiffLineKind.Added => LineGrid(line.OldNumber, line.NewNumber, true, "+", line.Text, OpenCodeToolTheme.SuccessBackground, OpenCodeToolTheme.Success),
                    OpenCodeDiffLineKind.Removed => LineGrid(line.OldNumber, line.NewNumber, true, "−", line.Text, OpenCodeToolTheme.CriticalBackground, OpenCodeToolTheme.Critical),
                    _ => LineGrid(line.OldNumber, line.NewNumber, true, " ", line.Text, null, null),
                };
            case OpenCodeDiffRow row:
            {
                if (row.IsHunk)
                {
                    return HunkRow(row.HunkText!);
                }

                var grid = new Grid { ColumnSpacing = 4 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var left = SideCell(row.Left);
                var divider = new Border { Width = 1 };
                OpenCodeToolTheme.Apply(divider, OpenCodeToolTheme.Border(OpenCodeToolTheme.Divider));
                var right = SideCell(row.Right);
                Grid.SetColumn(left, 0);
                Grid.SetColumn(divider, 1);
                Grid.SetColumn(right, 2);
                grid.Children.Add(left);
                grid.Children.Add(divider);
                grid.Children.Add(right);
                return grid;
            }

            default:
                return Caption(data?.ToString() ?? string.Empty);
        }
    }

    private static FrameworkElement SideCell(OpenCodeDiffCell? cell)
    {
        if (cell is null)
        {
            // 对侧无对应行：留空（新增文件左侧为空）。
            return new Border();
        }

        return cell.Kind switch
        {
            OpenCodeDiffLineKind.Added => LineGrid(cell.Number, null, false, "+", cell.Text, OpenCodeToolTheme.SuccessBackground, OpenCodeToolTheme.Success),
            OpenCodeDiffLineKind.Removed => LineGrid(cell.Number, null, false, "−", cell.Text, OpenCodeToolTheme.CriticalBackground, OpenCodeToolTheme.Critical),
            _ => LineGrid(cell.Number, null, false, " ", cell.Text, null, null),
        };
    }

    private static FrameworkElement HunkRow(string text)
    {
        var block = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.CodeStyleKey, true);
        OpenCodeToolTheme.Apply(block, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CodeStyleKey));
        block.Margin = new Thickness(0, 4, 0, 2);
        return block;
    }

    /// <summary>一行：[行号（三级文字）] [标记] [代码]；改动行整行语义背景、标记语义前景。</summary>
    private static FrameworkElement LineGrid(int? number, int? secondNumber, bool twoNumbers, string? marker, string text, string? backgroundKey, string? markerKey)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 0, 8, 0) };
        if (backgroundKey is not null)
        {
            OpenCodeToolTheme.Apply(grid, OpenCodeToolTheme.Grid(backgroundKey));
        }

        var column = 0;
        if (twoNumbers)
        {
            // 统一视图：旧行号 + 新行号两列。
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var oldNumber = NumberText(number);
            Grid.SetColumn(oldNumber, column++);
            grid.Children.Add(oldNumber);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var newNumber = NumberText(secondNumber);
            Grid.SetColumn(newNumber, column++);
            grid.Children.Add(newNumber);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            var numberText = NumberText(number);
            Grid.SetColumn(numberText, column++);
            grid.Children.Add(numberText);
        }

        if (marker is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            var markerText = OpenCodeInteractionUi.StyledText(marker, OpenCodeInteractionUi.CodeStyleKey);
            if (markerKey is not null)
            {
                OpenCodeToolTheme.Apply(markerText, OpenCodeToolTheme.Text(markerKey, OpenCodeInteractionUi.CodeStyleKey));
            }

            Grid.SetColumn(markerText, column++);
            grid.Children.Add(markerText);
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var code = Mono(text.Length == 0 ? " " : text);
        Grid.SetColumn(code, column);
        grid.Children.Add(code);
        return grid;
    }

    private static TextBlock NumberText(int? number)
    {
        var text = OpenCodeInteractionUi.StyledText(
            number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, OpenCodeInteractionUi.CodeStyleKey);
        OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextTertiary, OpenCodeInteractionUi.CodeStyleKey));
        text.IsTextSelectionEnabled = false;
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text.TextAlignment = TextAlignment.Right;
        return text;
    }

    /// <summary>
    /// 等宽块按此行数分段为多个 TextBlock（完整显示、不截断——大详情视图要求完整内容；分段只为避免单个超长文本布局过慢）。
    /// </summary>
    internal const int CodeBlockChunkLines = OpenCodeDetailTextPolicy.CodeBlockChunkLines;

    /// <summary>
    /// 可读属性列表（F2）：每行“键  值”，嵌套按层级缩进；长文本/命令/路径为等宽块。
    /// 仅原生 TextBlock + 内置文字样式 + 主题画刷；文本不可选择（弹出层不提供复制）。
    /// </summary>
    internal static FrameworkElement PropertyList(IReadOnlyList<OpenCodeToolProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var list = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailProperties");
        foreach (var property in properties)
        {
            list.Children.Add(PropertyRow(property));
        }

        return list;
    }

    /// <summary>等宽代码块（浅填充底 + 控件圆角），自动换行，完整显示（长内容分段，不截断），不可选择。</summary>
    internal static FrameworkElement CodeBlock(string text)
    {
        var panel = new StackPanel();
        foreach (var chunk in OpenCodeDetailTextPolicy.ChunkLines(text))
        {
            var block = OpenCodeInteractionUi.StyledText(chunk, OpenCodeInteractionUi.CodeStyleKey, false);
            block.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(block);
        }

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = ControlCornerRadius(),
        };
        OpenCodeToolTheme.Apply(border, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
        return border;
    }

    /// <summary>
    /// 通用入口（供思考文本、子会话预览等非工具来源使用）：把一段文本包装为可滚动的详情视图。
    /// monospace = true 时为等宽块，否则为正文样式。返回值可直接作为
    /// <see cref="IOpenCodeDetailContentSource.BuildDetailContent"/> 的结果。
    /// </summary>
    internal static OpenCodeToolDetailView TextView(string text, bool monospace = false)
    {
        FrameworkElement content;
        if (string.IsNullOrEmpty(text))
        {
            content = Caption("（没有内容）");
        }
        else if (monospace)
        {
            content = CodeBlock(text);
        }
        else
        {
            var body = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.BodyStyleKey, false);
            body.TextWrapping = TextWrapping.Wrap;
            content = body;
        }

        return Wrap(content);
    }

    /// <summary>通用入口：把任意元素放进与工具详情一致的纵向滚动区（保持/跟随滚动位置由弹出层处理）。</summary>
    internal static OpenCodeToolDetailView Wrap(FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0),
        };
        AutomationProperties.SetAutomationId(scroller, "OpenCodeToolDetailScroller");
        return new OpenCodeToolDetailView(scroller, scroller);
    }

    private static FrameworkElement PropertyRow(OpenCodeToolProperty property)
    {
        var indent = property.Depth * 16.0;
        switch (property.Kind)
        {
            case OpenCodeToolPropertyKind.Group:
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(indent, 2, 0, 0) };
                header.Children.Add(OpenCodeInteractionUi.StyledText(property.Key, OpenCodeInteractionUi.BodyStrongStyleKey, false));
                if (property.Value.Length > 0)
                {
                    var count = OpenCodeInteractionUi.StyledText(property.Value, OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
                    count.VerticalAlignment = VerticalAlignment.Center;
                    header.Children.Add(count);
                }

                return header;
            }

            case OpenCodeToolPropertyKind.Block:
            {
                var panel = new StackPanel { Spacing = 4, Margin = new Thickness(indent, 0, 0, 0) };
                if (property.Key.Length > 0)
                {
                    panel.Children.Add(KeyText(property.Key));
                }

                panel.Children.Add(CodeBlock(property.Value));
                return panel;
            }

            default:
            {
                var value = property.Kind switch
                {
                    OpenCodeToolPropertyKind.Code => OpenCodeInteractionUi.StyledText(property.Value, OpenCodeInteractionUi.CodeStyleKey, false),
                    OpenCodeToolPropertyKind.Empty => OpenCodeInteractionUi.StyledText(property.Value, OpenCodeInteractionUi.CaptionSubtleStyleKey, false),
                    _ => OpenCodeInteractionUi.StyledText(property.Value, OpenCodeInteractionUi.BodyStyleKey, false),
                };
                value.TextWrapping = TextWrapping.Wrap;
                value.VerticalAlignment = VerticalAlignment.Center;
                if (property.Key.Length == 0)
                {
                    value.Margin = new Thickness(indent, 0, 0, 0);
                    return value;
                }

                var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(indent, 0, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(64, 136 - indent)) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var key = KeyText(property.Key);
                key.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(value, 1);
                grid.Children.Add(key);
                grid.Children.Add(value);
                return grid;
            }
        }
    }

    private static TextBlock KeyText(string key)
    {
        // G8：键名同样换行显示（不再省略号截断）。
        var text = OpenCodeInteractionUi.StyledText(key, OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
        text.TextWrapping = TextWrapping.Wrap;
        return text;
    }

    private static CornerRadius ControlCornerRadius()
    {
        try
        {
            if (Application.Current?.Resources is { } resources && resources.TryGetValue("ControlCornerRadius", out var value) && value is CornerRadius radius)
            {
                return radius;
            }
        }
        catch (Exception)
        {
        }

        return new CornerRadius(4);
    }

    /// <summary>等宽文本行（总是换行，G8）。</summary>
    internal static TextBlock Mono(string text)
    {
        var block = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.CodeStyleKey, true);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    internal static TextBlock Caption(string text)
    {
        var block = OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.CaptionSubtleStyleKey, true);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    private static UIElement Section(string label, UIElement child)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(OpenCodeInteractionUi.StyledText(label, OpenCodeInteractionUi.BodyStrongStyleKey, false));
        panel.Children.Add(child);
        return panel;
    }
}
