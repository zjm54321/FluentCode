using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

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
    internal static Style? ScrollSurface(string backgroundKey) => Get("ScrollViewer", null, ("Background", backgroundKey));

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
    public bool? AutoUnified { get; set; }

    public bool ShowAll { get; set; }
    public bool RawData { get; set; }

    public int SelectedFile { get; set; }
    public string? SelectedFileKey { get; set; }
    public bool ShowSource { get; set; }
    public bool ShowDifference { get; set; }
    public Action<string, int?>? OpenFile { get; set; }
    public Action<string>? RevealFile { get; set; }
    public Action? OpenTerminal { get; set; }
    public Action<string>? ShowBrowserPage { get; set; }
    internal OpenCodeMarkdownImageScope? BrowserImageScope { get; set; }
    internal OpenCodeBrowserCaptureImage? BrowserCaptureImage { get; set; }
    public Action<string>? OpenDirectory { get; set; }
    public Func<string, bool, string?>? PathActionUnavailableReason { get; set; }
    internal string? PreviewHint { get; set; }
    public Dictionary<string, int> FoldVisibleLines { get; } = new(StringComparer.Ordinal);

    /// <summary>TL1: true while building the hover preview (built-in views cap their lists; the large view shows everything).</summary>
    public bool Compact { get; set; }

    /// <summary>TL1: opens a child session through the page's existing subagent navigation (null = no link).</summary>
    public Action<string>? OpenChildSession { get; set; }

    /// <summary>已展开的折叠段（按折叠行首行号标识）。</summary>
    public HashSet<string> ExpandedFolds { get; } = new(StringComparer.Ordinal);
}

/// <summary>构建结果：根元素 + 主滚动区（用于运行中刷新时保持/跟随滚动位置）。</summary>
internal sealed record OpenCodeToolDetailView(FrameworkElement Root, ScrollViewer? Scroller, bool IsTruncated = false, int HiddenLineCount = 0, string? PreviewHint = null);

/// <summary>
/// 工具详情弹出层的内容构建（§6.3）：read 带行号全文；edit/patch/write/multiedit 并排 diff（可切统一视图），
/// 长未改动段折叠；shell 命令 + 完整输出；grep/glob/list 结果列表；webfetch URL + 内容；其他工具 输入/输出/错误。
/// 长内容经 ItemsRepeater 虚拟化，超过 <see cref="MaxRows"/> 行先显示前段并提供“显示全部”。
/// 全部使用原生控件 + 内置文本样式 + 主题画刷（经 <see cref="OpenCodeToolTheme"/>），不定义颜色、不自定义模板。
/// </summary>
internal static partial class OpenCodeToolDetailContent
{
    internal const int MaxRows = 20000;

    private sealed record CodeLine(int? Number, string Text, string Language = "text");

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

    internal static OpenCodeToolDetailView Build(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild,
        int? selectedFileIndex = null, string? selectedFileKey = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rebuild);
        options.PreviewHint = null;
        if (selectedFileIndex is { } requestedIndex) options.SelectedFile = requestedIndex;
        if (selectedFileKey is not null) options.SelectedFileKey = selectedFileKey;
        if (options.SelectedFileKey is { } key)
            for (var index = 0; index < model.DiffFiles.Count; index++)
                if (string.Equals(model.DiffFiles[index].Path, key, StringComparison.Ordinal)) { options.SelectedFile = index; break; }

        using var detailScope = DetailBuildScope(!options.Compact);
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var banners = new StackPanel { Spacing = 12 };
        if (model.Status == OpenCodeToolRowStatus.Error)
        {
            banners.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Title = ErrorTitle(model.ErrorText),
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

        var top = new StackPanel { Spacing = 12 };

        var rows = new List<object>();
        FrameworkElement? body = null;
        var toolHidden = 0;

        switch (model.Kind)
        {
            case OpenCodeToolKind.Browser:
                body = BuildBrowser(model, options);
                break;
            case OpenCodeToolKind.Read:
                BuildRead(model, rows, top);
                if (rows.Count == 0 && ReadImage(model) is { } imageBody) body = imageBody;
                if (rows.Count > 0 && OpenCodeFileDetailPolicy.IsMarkdown(model.Target))
                {
                    var source = string.Join("\n", rows.ConvertAll(row => row is CodeLine line ? line.Text : string.Empty));
                    body = BuildFileContent(model.Target, source, options, rebuild, top);
                    rows.Clear();
                }
                break;
            case OpenCodeToolKind.Edit:
            case OpenCodeToolKind.MultiEdit:
            case OpenCodeToolKind.Patch:
            case OpenCodeToolKind.Write:
                if (model.DiffFiles.Count > 0)
                {
                    var file = model.DiffFiles[Math.Clamp(options.SelectedFile, 0, model.DiffFiles.Count - 1)];
                    if (model.Kind == OpenCodeToolKind.Write && file.After is not null && !options.ShowDifference)
                        body = BuildFileContent(file.Path, file.After, options, rebuild, top, file.Before is not null);
                    else
                        BuildDiff(model, options, rebuild, rows, top);
                }
                else
                {
                    body = BuildGeneric(model);
                }

                break;
            case OpenCodeToolKind.Shell:
                body = BuildTerminal(model, options, rebuild, out toolHidden);
                break;
            case OpenCodeToolKind.Grep:
                body = BuildGrep(model, options, rebuild, out toolHidden);
                break;
            case OpenCodeToolKind.Glob:
            case OpenCodeToolKind.List:
                body = BuildPaths(model, options, rebuild, out toolHidden);
                break;
            case OpenCodeToolKind.WebFetch:
                body = BuildWebFetch(model, options, rebuild, out toolHidden);
                break;
            case OpenCodeToolKind.WebSearch:
                body = BuildWebResults(model, options, rebuild, false, out toolHidden);
                break;

            default:
                // TL1: built-in tools (todowrite, subagent, question, skill, websearch, execute) get their own view.
                body = model.ToolName == "codesearch" || model.ToolName.EndsWith("_codesearch", StringComparison.Ordinal)
                    ? BuildWebResults(model, options, rebuild, true, out toolHidden)
                    : OpenCodeToolDetailBuiltins.Build(model, options) ?? BuildGeneric(model);
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
        var isTruncated = toolHidden > 0;
        var hiddenLines = toolHidden;
        if (body is null)
        {
            var rowLimit = options.Compact ? CompactRowLimit(model.Kind) : MaxRows;
            if ((!options.ShowAll || options.Compact) && rows.Count > rowLimit)
            {
                isTruncated = true;
                var total = rows.Count;
                hiddenLines = total - rowLimit;
                rows.RemoveRange(rowLimit, rows.Count - rowLimit);
                if (!options.Compact) rows.Add(new ActionLine(
                    "已显示前 " + MaxRows.ToString(CultureInfo.InvariantCulture) + " 行（共 " + total.ToString(CultureInfo.InvariantCulture) + " 行）· 显示全部",
                    () =>
                    {
                        options.ShowAll = true;
                        rebuild();
                    },
                    "OpenCodeToolDetailShowAll"));
            }

            var repeater = new ItemsRepeater
            {
                ItemsSource = rows,
                ItemTemplate = new LineFactory(BuildRow),
            };
            body = repeater;
            if (model.Kind == OpenCodeToolKind.Read)
            {
                var surface = new Border { Child = body };
                OpenCodeCodePalette.BindSurface(surface);
                body = surface;
            }
        }

        if (options.RawData && !options.Compact) body = RawProperties(model);
        var allContent = new StackPanel { Spacing = 20 };
        if (banners.Children.Count > 0) allContent.Children.Add(banners);
        if (top.Children.Count > 0 && !options.RawData) allContent.Children.Add(top);
        allContent.Children.Add(body);
        if (model.Kind == OpenCodeToolKind.Read && rows.Count > 0 && !options.Compact && rows[^1] is CodeLine { Number: { } last })
            allContent.Children.Add(Caption(int.TryParse(ReadTotal(model.OutputText), out var totalLines) && last < totalLines
                ? $"第 {last} 行之后未被读取；未从磁盘补全。" : "仅显示本次读取返回的内容；未从磁盘补全。"));
        scroller = new ScrollViewer
        {
            Content = allContent,
            // G8：内容总是换行 → 无横向滚动，宽度受限于详情视图，长行在行内折行。
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = options.Compact ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto,
            VerticalScrollMode = options.Compact ? ScrollMode.Disabled : ScrollMode.Auto,
            MaxHeight = options.Compact ? 260 : double.PositiveInfinity,
            Padding = options.Compact ? new Thickness(0) : new Thickness(24, 16, 24, 16),
        };
        AutomationProperties.SetAutomationId(scroller, "OpenCodeToolDetailScroller");
        root.RowDefinitions[0].Height = GridLength.Auto;
        root.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        root.RowDefinitions[2].Height = GridLength.Auto;
        if (!options.Compact) root.Children.Add(FactsBar(model, options));
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        if (!options.Compact && OpenCodeToolRowPresentation.IsDiffKind(model.Kind)) AddDiffNavigation(root, scroller);
        if (!options.Compact)
        {
            var footer = DetailFooter(model, options, rebuild);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
        }
        return new OpenCodeToolDetailView(root, scroller, isTruncated, hiddenLines, options.PreviewHint);
    }

    private static void BuildRead(OpenCodeToolRowModel model, List<object> rows, StackPanel top)
    {
        if (string.IsNullOrEmpty(model.OutputText))
        {
            if (model.FileOutputs.Count == 0)
            {
                if (model.Status == OpenCodeToolRowStatus.Error) top.Children.Add(PropertyList(model.InputProperties));
                else if (model.IsActive) top.Children.Add(Running("正在读取…"));
                else top.Children.Add(Caption("（文件为空）"));
            }

            return;
        }

        var offset = int.TryParse(Field(StateObject(model, "input"), "offset"), out var requestedOffset) ? requestedOffset : (int?)null;
        foreach (var line in model.OutputText.Split('\n'))
        {
            if (OpenCodeToolRowPresentation.TrySplitNumberedLine(line, out var number, out var content))
            {
                rows.Add(new CodeLine(number, content, OpenCodeFileDetailPolicy.Language(model.Target)));
            }
            else if (rows.Count == 0 && line.Length > 0)
            {
                // The file target is already in shared chrome; do not repeat the protocol's read header.
                if (!line.StartsWith("Read file ", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("<", StringComparison.Ordinal))
                    rows.Add(new CodeLine(offset is { } start ? start + rows.Count : null, line.TrimEnd('\r'), OpenCodeFileDetailPolicy.Language(model.Target)));
            }
            else if (line.Length > 0)
            {
                if (!line.StartsWith("</", StringComparison.Ordinal)) rows.Add(new CodeLine(offset is { } start ? start + rows.Count : null, line.TrimEnd('\r'), OpenCodeFileDetailPolicy.Language(model.Target)));
            }
        }
    }

    private static FrameworkElement BuildFileContent(string path, string source, OpenCodeToolDetailOptions options, Action rebuild, StackPanel top, bool canDiff = false)
    {
        var lines = source.Split('\n');
        var limit = options.Compact ? 8 : options.ShowAll ? lines.Length : MaxRows;
        var hidden = lines.Length > limit;
        var shown = hidden ? string.Join("\n", lines, 0, limit) : source;
        if (!options.Compact)
        {
            var toggle = new SelectorBar();
            var preview = new SelectorBarItem { Text = "预览" };
            var raw = new SelectorBarItem { Text = "源码" };
            var diff = new SelectorBarItem { Text = "差异" };
            toggle.Items.Add(preview);
            toggle.Items.Add(raw);
            if (canDiff) toggle.Items.Add(diff);
            toggle.SelectedItem = options.ShowDifference ? diff : options.ShowSource ? raw : preview;
            AutomationProperties.SetAutomationId(toggle, "OpenCodeFileSourceToggle");
            toggle.SelectionChanged += (_, _) => { options.ShowSource = toggle.SelectedItem == raw; options.ShowDifference = toggle.SelectedItem == diff; rebuild(); };
            top.Children.Add(toggle);
        }

        var panel = new StackPanel();
        AutomationProperties.SetAutomationId(panel, "OpenCodeFileSource");
        if (OpenCodeFileDetailPolicy.IsMarkdown(path) && !options.ShowSource)
        {
            var markdown = new OpenCodeRichTextPresenter();
            markdown.Render(shown, OpenCodeRichTextStyle.Body);
            EnforceWrapping(markdown.Element);
            panel.Children.Add(markdown.Element);
        }
        else
        {
        var language = OpenCodeFileDetailPolicy.Language(path);
        foreach (var chunk in OpenCodeDetailTextPolicy.ChunkLines(shown))
        {
            var block = Mono(string.Empty);
            FillSyntax(block, chunk, language);
            panel.Children.Add(block);
        }
        }
        if (hidden)
        {
            var more = new HyperlinkButton { Content = options.Compact ? $"还有 {lines.Length - limit} 行 · 点击行打开完整内容" : $"已显示前 {limit} 行（共 {lines.Length} 行）· 显示全部" };
            more.Click += (_, _) => { options.ShowAll = true; rebuild(); };
            panel.Children.Add(more);
        }
        var surface = new Border { Child = panel };
        OpenCodeCodePalette.BindSurface(surface);
        return surface;
    }

    private static void FillSyntax(TextBlock block, string source, string language)
    {
        OpenCodeCodePalette.BindText(block, source, language);
    }

    /// <summary>Scope wrapping to the embedded file/child tree, including native Markdown code blocks and tables.</summary>
    private sealed class WidthLimit
    {
        internal double Original;
        internal double Applied;
        internal WidthLimit(double original) { Original = original; Applied = original; }
    }
    // Native DP storage survives recreated managed projections of the same WinUI element.
    private static readonly DependencyProperty WidthLimitProperty = DependencyProperty.RegisterAttached(
        "DetailWidthLimit", typeof(object), typeof(OpenCodeToolDetailContent), new PropertyMetadata(null));

    internal static void EnforceWrapping(DependencyObject root, double availableWidth = double.PositiveInfinity)
    {
        if (root is FrameworkElement element && double.IsFinite(availableWidth))
        {
            if (element.GetValue(WidthLimitProperty) is not WidthLimit limit)
            {
                limit = new WidthLimit(element.MaxWidth);
                element.SetValue(WidthLimitProperty, limit);
            }
            if (element.MaxWidth != limit.Applied) limit.Original = element.MaxWidth;
            var width = Math.Max(0, Math.Min(limit.Original, availableWidth));
            if (element.MaxWidth != width) element.MaxWidth = width;
            limit.Applied = width;
        }
        if (root is TextBlock text && text.TextWrapping != TextWrapping.Wrap) text.TextWrapping = TextWrapping.Wrap;
        if (root is RichTextBlock rich && rich.TextWrapping != TextWrapping.Wrap) rich.TextWrapping = TextWrapping.Wrap;
        if (root is ScrollViewer scroll)
        {
            if (scroll.HorizontalScrollMode != ScrollMode.Disabled) scroll.HorizontalScrollMode = ScrollMode.Disabled;
            if (scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            if (scroll.HorizontalOffset > 0) scroll.ChangeView(0, null, null, true);
        }
        if (root is Grid grid && grid.ColumnDefinitions.Count > 1)
        {
            var table = true;
            foreach (var column in grid.ColumnDefinitions) table &= column.Width.IsAuto;
            if (table)
                foreach (var column in grid.ColumnDefinitions) column.Width = new GridLength(1, GridUnitType.Star);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var childWidth = availableWidth;
            if (root is Border border) childWidth -= border.Padding.Left + border.Padding.Right;
            if (root is Grid columns && child is FrameworkElement gridChild && columns.ColumnDefinitions.Count > 0)
            {
                var first = Grid.GetColumn(gridChild);
                var last = Math.Min(columns.ColumnDefinitions.Count, first + Grid.GetColumnSpan(gridChild));
                var measured = 0d;
                for (var column = first; column < last; column++) measured += columns.ColumnDefinitions[column].ActualWidth;
                if (measured > 0) childWidth = Math.Min(childWidth, measured);
            }
            if (root is StackPanel { Orientation: Orientation.Horizontal } horizontal && child is FrameworkElement item)
            {
                var fixedWidth = Math.Max(0, horizontal.Children.Count - 1) * horizontal.Spacing;
                var flexible = 0;
                foreach (var sibling in horizontal.Children)
                    if (sibling is FrameworkElement other)
                    {
                        if (other is FontIcon || double.IsFinite(other.Width)) fixedWidth += double.IsFinite(other.Width) ? other.Width : other.DesiredSize.Width;
                        else flexible++;
                    }
                if (item is not FontIcon && !double.IsFinite(item.Width) && flexible > 0)
                    childWidth = Math.Max(0, availableWidth - fixedWidth) / flexible;
            }
            if (child is FrameworkElement margins) childWidth -= margins.Margin.Left + margins.Margin.Right;
            EnforceWrapping(child, Math.Max(0, childWidth));
        }
    }

    private static void BuildDiff(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, List<object> rows, StackPanel top)
    {
        var files = model.DiffFiles;
        var index = Math.Clamp(options.SelectedFile, 0, files.Count - 1);
        var selected = files[index];
        List<OpenCodeDiffLine> unified;
        if (!string.IsNullOrEmpty(selected.Patch))
        {
            unified = OpenCodeLineDiff.ParseUnified(selected.Patch);
            unified.RemoveAll(line => line.Kind == OpenCodeDiffLineKind.Meta);
        }
        else if (selected.After is not null)
        {
            unified = OpenCodeLineDiff.Compute(selected.Before ?? string.Empty, selected.After);
            // Input fragments have no real file positions. Never present LCS-relative positions as file line numbers.
            for (var line = 0; line < unified.Count; line++) unified[line] = unified[line] with { OldNumber = null, NewNumber = null };
        }
        else
        {
            top.Children.Add(Caption("服务端未提供该文件的差异内容。"));
            return;
        }

        if (model.Kind is OpenCodeToolKind.Edit or OpenCodeToolKind.MultiEdit && !options.Compact)
        {
            top.Children.Add(new Expander { Header = "编辑参数", IsExpanded = false, Content = PropertyList(model.InputProperties) });
        }
        if (model.Kind == OpenCodeToolKind.Write && selected.Before is not null && !options.Compact)
        {
            var back = new HyperlinkButton { Content = "返回预览 / 源码" };
            back.Click += (_, _) => { options.ShowDifference = false; rebuild(); };
            top.Children.Add(back);
        }
        if (options.Compact)
        {
            var snippets = 0;
            for (var i = 0; i < unified.Count && snippets < (model.Kind == OpenCodeToolKind.MultiEdit ? 3 : 1); i++)
            {
                if (unified[i].Kind is not OpenCodeDiffLineKind.Added and not OpenCodeDiffLineKind.Removed) continue;
                if (i > 0) rows.Add(unified[i - 1]);
                do { rows.Add(unified[i++]); }
                while (i < unified.Count && unified[i].Kind is OpenCodeDiffLineKind.Added or OpenCodeDiffLineKind.Removed);
                if (i < unified.Count) rows.Add(unified[i]);
                snippets++;
            }
            if (rows.Count == 0) rows.AddRange(unified);
            return;
        }

        var sideRows = OpenCodeLineDiff.Fold(OpenCodeLineDiff.ToSideBySide(unified));
        var foldOrdinal = 0;
        foreach (var row in sideRows)
        {
            if (row.IsFold)
            {
                var folded = row.Folded!;
                var foldKey = index.ToString(CultureInfo.InvariantCulture) + ":" + (foldOrdinal++).ToString(CultureInfo.InvariantCulture);
                var shown = options.ExpandedFolds.Contains(foldKey) ? folded.Count : options.FoldVisibleLines.GetValueOrDefault(foldKey);
                for (var innerIndex = 0; innerIndex < Math.Min(shown, folded.Count); innerIndex++)
                {
                    var inner = folded[innerIndex];
                    rows.Add(options.Unified ? ToUnified(inner) : inner);
                }
                if (shown < folded.Count)
                {
                    rows.Add(new ActionLine(
                        "⋯ 展开 " + (folded.Count - shown).ToString(CultureInfo.InvariantCulture) + " 行未改动 ⋯",
                        () =>
                        {
                            if (ShiftPressed()) options.ExpandedFolds.Add(foldKey);
                            else options.FoldVisibleLines[foldKey] = shown + 20;
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
        var panel = new StackPanel { Spacing = 20 };
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

    private static UIElement BuildRow(object data)
    {
        switch (data)
        {
            case CodeLine code:
            {
                var line = (Grid)LineGrid(code.Number, null, false, null, code.Text, null, null);
                if (line.Children[^1] is TextBlock block) FillSyntax(block, code.Text, code.Language);
                return line;
            }
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
            var placeholder = Mono("╱    ╱    ╱    ╱    ╱    ╱");
            placeholder.Opacity = 0.2;
            return placeholder;
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
        block.Tag = "OpenCodeDiffHunk";
        return block;
    }

    /// <summary>一行：[行号（三级文字）] [标记] [代码]；改动行整行语义背景、标记语义前景。</summary>
    private static FrameworkElement LineGrid(int? number, int? secondNumber, bool twoNumbers, string? marker, string text, string? backgroundKey, string? markerKey)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 0, 8, 0) };
        if (backgroundKey is not null)
        {
            BindDiffSurface(grid, backgroundKey == OpenCodeToolTheme.SuccessBackground);
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
        var list = new StackPanel { Spacing = 12 };
        AutomationProperties.SetAutomationId(list, "OpenCodeToolDetailProperties");
        void Populate(bool all)
        {
            list.Children.Clear();
            var count = all ? properties.Count : Math.Min(2000, properties.Count);
            for (var index = 0; index < count; index++) list.Children.Add(PropertyRow(properties[index]));
            if (count < properties.Count)
            {
                var expand = new HyperlinkButton { Content = $"已显示 2 000 / {properties.Count} 项 · 显示全部" };
                expand.Click += (_, _) => Populate(true);
                list.Children.Add(expand);
            }
        }
        Populate(false);

        return list;
    }

    /// <summary>Wrapped code with a short local preview; expanding displays all chunks without an inner scroller.</summary>
    internal static FrameworkElement CodeBlock(string text)
    {
        var panel = new StackPanel();
        void Populate(bool all)
        {
            panel.Children.Clear();
            var lines = text.Split('\n');
            var count = all ? lines.Length : Math.Min(MaxRows, lines.Length);
            var shown = count == lines.Length ? text : string.Join("\n", lines, 0, count);
            foreach (var chunk in OpenCodeDetailTextPolicy.ChunkLines(shown))
            {
                var block = OpenCodeInteractionUi.StyledText(chunk, OpenCodeInteractionUi.CodeStyleKey, true);
                block.TextWrapping = TextWrapping.Wrap;
                OpenCodeCodePalette.BindText(block, chunk, "text");
                panel.Children.Add(block);
            }
            if (count < lines.Length)
            {
                var expand = new HyperlinkButton { Content = $"已显示前 {count} 行（共 {lines.Length} 行）· 显示全部" };
                expand.Click += (_, _) => Populate(true);
                panel.Children.Add(expand);
            }
        }
        Populate(_fullDetailBuild == 0);

        return CodeBlockFrame(panel, text);
    }

    /// <summary>One wrapped, uncapped block; the containing conversation/detail owns scrolling.
    /// Copy captures the original full string, never reconstructed chunks, rendered line numbers or highlights.</summary>
    internal static FrameworkElement CodeBlockFrame(FrameworkElement content, string source, string? language = null)
    {
        var excerpt = OpenCodeDetailTextPolicy.CodePreview(source);
        var canCollapse = _fullDetailBuild == 0 && OpenCodeDetailTextPolicy.CanCollapseCode(source);
        var preview = Mono(excerpt);
        preview.MaxLines = OpenCodeDetailTextPolicy.CodePreviewLines;
        preview.TextTrimming = TextTrimming.CharacterEllipsis;
        OpenCodeCodePalette.BindText(preview, excerpt, language);
        preview.Visibility = canCollapse ? Visibility.Visible : Visibility.Collapsed;
        content.Visibility = canCollapse ? Visibility.Collapsed : Visibility.Visible;
        // A narrow right gutter keeps the floating controls off selectable code. No header row.
        content.Margin = new Thickness(0, 0, canCollapse ? 68 : 34, 0);
        preview.Margin = new Thickness(0, 0, 68, 0);
        var copy = new Button
        {
            Content = new FontIcon { Glyph = "\uE8C8", FontSize = 12 },
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 28, MinHeight = 28, Padding = new Thickness(5),
        };
        AutomationProperties.SetAutomationId(copy, "OpenCodeCodeBlockCopy");
        AutomationProperties.SetName(copy, "复制完整代码");
        ToolTipService.SetToolTip(copy, "复制完整代码");
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(source);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                ToolTipService.SetToolTip(copy, "已复制完整代码");
            }
            catch (Exception)
            {
                ToolTipService.SetToolTip(copy, "复制失败，请选择代码复制");
                AutomationProperties.SetHelpText(copy, "复制失败，请选择代码复制");
            }
        };
        copy.Tapped += (_, args) => args.Handled = true;
        copy.DoubleTapped += (_, args) => args.Handled = true;
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        if (canCollapse)
        {
            var glyph = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
            var toggle = new ToggleButton { Content = glyph, MinWidth = 28, MinHeight = 28, Padding = new Thickness(5), IsChecked = false };
            AutomationProperties.SetAutomationId(toggle, "OpenCodeCodeBlockExpand");
            void UpdateExpansion()
            {
                var expanded = toggle.IsChecked == true;
                content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                preview.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
                glyph.Glyph = expanded ? "\uE70E" : "\uE70D";
                var label = expanded ? "收起代码（保留前 6 行）" : "展开全部代码";
                ToolTipService.SetToolTip(toggle, label);
                AutomationProperties.SetName(toggle, label);
            }
            toggle.Click += (_, _) => UpdateExpansion();
            toggle.Tapped += (_, args) => args.Handled = true;
            toggle.DoubleTapped += (_, args) => args.Handled = true;
            UpdateExpansion();
            controls.Children.Add(toggle);
        }
        controls.Children.Add(copy);
        var layout = new Grid();
        layout.Children.Add(content);
        layout.Children.Add(preview);
        // Last in the same Grid cell: the native controls float over the top-right surface.
        layout.Children.Add(controls);
        var border = new Border
        {
            Child = layout,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = ControlCornerRadius(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        OpenCodeToolTheme.Apply(border, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
        OpenCodeCodePalette.BindSurface(border);
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
                value.IsTextSelectionEnabled = true;
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
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(OpenCodeInteractionUi.StyledText(label, OpenCodeInteractionUi.BodyStrongStyleKey, false));
        panel.Children.Add(child);
        return panel;
    }
}
