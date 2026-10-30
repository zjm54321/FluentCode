using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

internal static partial class OpenCodeToolDetailContent
{
    internal static string? PreviewFormat(OpenCodeToolRowModel model) => Field(StateObject(model, "input"), "format");
    internal static string? PreviewProvider(OpenCodeToolRowModel model) => Field(StateObject(model, "metadata"), "provider");
    private static FrameworkElement ResultRepeater(IEnumerable<object> rows, Func<object, UIElement> build)
        => new ItemsRepeater { ItemsSource = rows.ToArray(), ItemTemplate = new LineFactory(build) };

    private static FrameworkElement BuildTerminal(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, out int hidden)
    {
        var lines = (model.OutputText ?? string.Empty).TrimEnd('\r', '\n').Split('\n');
        var limit = options.Compact ? model.IsActive ? 6 : 8 : options.ShowAll ? lines.Length : MaxRows;
        hidden = Math.Max(0, lines.Length - limit);
        var shown = options.Compact ? lines.Skip(Math.Max(0, lines.Length - limit)) : lines.Take(limit);
        return TerminalBody(model.Command ?? model.Target,
            string.IsNullOrEmpty(model.OutputText) ? model.IsActive ? "命令运行中，暂无输出…" : "（没有输出）" : string.Join('\n', shown),
            !options.Compact, hidden > 0 && !options.Compact ? ShowAllButton(hidden, options, rebuild) : null);
    }

    // Single terminal presentation for tool details and the full-center sidebar terminal.
    // Hosts own paging/navigation; neither host duplicates fonts, command, divider or output styling.
    internal static FrameworkElement TerminalBody(string? command, string output, bool selectable = true, FrameworkElement? footer = null)
    {
        var panel = new StackPanel { Spacing = 12 };
        RichTextBlock TerminalText(string source)
        {
            var text = new RichTextBlock { IsTextSelectionEnabled = selectable, TextWrapping = TextWrapping.Wrap,
                FontSize = 13, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 204, 204)) };
            AppFonts.Apply(text, AppFontSlot.Monospace);
            foreach (var chunk in OpenCodeDetailTextPolicy.ChunkLines(source))
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.Add(new Run { Text = chunk });
                text.Blocks.Add(paragraph);
            }
            return text;
        }
        panel.Children.Add(TerminalText("$ " + command));
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 64, 64, 64)) });
        panel.Children.Add(TerminalText(output));
        if (footer is not null) panel.Children.Add(footer);
        return new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12)),
            Padding = new Thickness(16), CornerRadius = new CornerRadius(8), Child = panel };
    }

    private static Button ShowAllButton(int hidden, OpenCodeToolDetailOptions options, Action rebuild)
    {
        var button = new Button { Content = $"还有 {hidden} 行 · 显示全部" };
        button.Click += (_, _) => { options.ShowAll = true; rebuild(); };
        return button;
    }

    private static FrameworkElement BuildGrep(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, out int hidden)
    {
        var parsed = OpenCodeToolDetailResultData.Grep(model.OutputText);
        hidden = 0;
        var panel = new StackPanel { Spacing = 12 };
        if (parsed.Files.Count == 0)
        {
            panel.Children.Add(CodeBlock(model.OutputText ?? (model.IsActive ? "正在查找…" : "（没有结果）")));
            return panel;
        }
        var total = parsed.Files.Sum(file => file.Matches.Count);
        panel.Children.Add(Caption($"{total} 处匹配 · {parsed.Files.Count} 个文件"));
        var results = new StackPanel { Spacing = 20 };
        var pattern = Field(StateObject(model, "input"), "pattern");
        Regex? expression = null;
        if (!string.IsNullOrEmpty(pattern))
            try { expression = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { }
        UIElement MatchLine(OpenCodeGrepFile file, OpenCodeGrepMatch match)
        {
            var text = Mono(match.Line.ToString(CultureInfo.InvariantCulture) + "  " + match.Text);
            text.IsTextSelectionEnabled = true;
            if (expression is not null)
                try
                {
                    var highlighter = new TextHighlighter();
                    foreach (Match found in expression.Matches(match.Text))
                        if (found.Length > 0) highlighter.Ranges.Add(new TextRange(match.Line.ToString(CultureInfo.InvariantCulture).Length + 2 + found.Index, found.Length));
                    text.TextHighlighters.Add(highlighter);
                }
                catch (RegexMatchTimeoutException) { }
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 4),
                Style = OpenCodeInteractionUi.TryStyle("SubtleButtonStyle") };
            if (!options.Compact && options.OpenFile is { } open) button.Click += (_, _) => open(file.Path, match.Line);
            if (!options.Compact)
            {
                var reason = options.PathActionUnavailableReason?.Invoke(file.Path, false)
                    ?? (options.OpenFile is null ? "当前连接未提供已授权的路径打开方式" : null);
                button.IsEnabled = reason is null;
                if (reason is not null) ToolTipService.SetToolTip(button, reason);
            }
            return button;
        }
        var shownCount = 0;
        void Populate(string filter)
        {
            results.Children.Clear();
            var remaining = options.ShowAll ? int.MaxValue : MaxRows;
            foreach (var file in options.Compact ? parsed.Files.Take(2) : parsed.Files)
            {
                var matches = file.Matches.Where(match => string.IsNullOrEmpty(filter) || file.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || match.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) continue;
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                header.Children.Add(Mono(System.IO.Path.GetFileName(file.Path.Replace('\\', '/'))));
                header.Children.Add(Caption(file.Path.Contains('/') ? file.Path[..file.Path.LastIndexOf('/')] : string.Empty));
                header.Children.Add(new InfoBadge { Value = matches.Length });
                var group = new StackPanel { Spacing = 8 };
                group.Children.Add(header);
                var shown = matches.Take(options.Compact ? 2 : remaining).ToArray();
                shownCount += shown.Length;
                remaining -= shown.Length;
                var visible = options.Compact || shown.Length <= 50 ? shown : shown.Take(50).ToArray();
                group.Children.Add(ResultRepeater(visible.Cast<object>(), item => MatchLine(file, (OpenCodeGrepMatch)item)));
                if (!options.Compact && shown.Length > 50)
                    group.Children.Add(new Expander { Header = $"还有 {shown.Length - 50} 处匹配", HorizontalAlignment = HorizontalAlignment.Stretch,
                        Content = ResultRepeater(shown.Skip(50).Cast<object>(), item => MatchLine(file, (OpenCodeGrepMatch)item)) });
                results.Children.Add(group);
                if (remaining <= 0) break;
            }
        }
        if (!options.Compact)
        {
            var filter = new AutoSuggestBox { PlaceholderText = "筛选已返回的结果", QueryIcon = new SymbolIcon(Symbol.Find) };
            AutomationProperties.SetAutomationId(filter, "OpenCodeGrepLocalFilter");
            filter.TextChanged += (_, _) => Populate(filter.Text);
            panel.Children.Add(filter);
        }
        Populate(string.Empty);
        hidden = Math.Max(0, total - shownCount);
        if (options.Compact && hidden > 0)
            options.PreviewHint = $"还有 {hidden} 处（含 {parsed.Files.Skip(2).Count() + parsed.Files.Take(2).Count(file => file.Matches.Count > 2)} 个文件）";
        panel.Children.Add(results);
        if (!options.Compact && hidden > 0) panel.Children.Add(ShowAllButton(hidden, options, rebuild));
        if (!string.IsNullOrEmpty(parsed.Unparsed) && !options.Compact) panel.Children.Add(CodeBlock(parsed.Unparsed));
        return panel;
    }

    private static FrameworkElement BuildPaths(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, out int hidden)
    {
        var paths = (model.OutputText ?? string.Empty).Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var limit = options.Compact ? model.Kind == OpenCodeToolKind.List ? 6 : 8 : options.ShowAll ? paths.Length : MaxRows;
        hidden = Math.Max(0, paths.Length - limit);
        var visible = paths.Take(limit).ToArray();
        var panel = new StackPanel { Spacing = 12 };
        if (visible.Length == 0) { panel.Children.Add(Caption(model.IsActive ? "正在查找…" : "（没有结果）")); return panel; }
        if (options.Compact)
        {
            var directories = paths.Select(path => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty).Distinct().ToArray();
            var shared = directories.Length == 1 && directories[0].Length > 0 ? directories[0] : null;
            if (shared is not null) panel.Children.Add(Caption(shared));
            foreach (var path in visible) panel.Children.Add(Mono(shared is not null ? path[(shared.Length + 1)..] : path));
            return panel;
        }
        var host = new Grid();
        var selector = new SelectorBar();
        var treeChoice = new SelectorBarItem { Text = "树" };
        var listChoice = new SelectorBarItem { Text = "列表" };
        selector.Items.Add(treeChoice); selector.Items.Add(listChoice);
        void PathMenu(FrameworkElement element, string path)
        {
            if (options.PathActionUnavailableReason?.Invoke(path, false) is { } reason) ToolTipService.SetToolTip(element, reason);
            var copy = new MenuFlyoutItem { Text = "复制路径" };
            copy.Click += (_, _) =>
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(path);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            };
            var flyout = new MenuFlyout(); flyout.Items.Add(copy); element.ContextFlyout = flyout;
        }
        void Populate()
        {
            host.Children.Clear();
            if (selector.SelectedItem == listChoice)
            {
                var list = new ListView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };
                ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
                foreach (var path in visible) { var row = new ListViewItem { Content = Mono(path), Tag = path }; PathMenu(row, path); list.Items.Add(row); }
                list.ItemClick += (_, args) => { if (args.ClickedItem is ListViewItem { Tag: string path }) options.OpenFile?.Invoke(path, null); };
                host.Children.Add(list);
            }
            else
            {
                var tree = new TreeView { SelectionMode = TreeViewSelectionMode.Single };
                ScrollViewer.SetVerticalScrollMode(tree, ScrollMode.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
                var nodes = new Dictionary<string, TreeViewNode>(StringComparer.Ordinal);
                var files = new Dictionary<TreeViewNode, string>();
                foreach (var path in visible)
                {
                    TreeViewNode? parent = null; var key = path.StartsWith('/') ? "/" : string.Empty;
                    foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        key += segment + "/";
                        if (!nodes.TryGetValue(key, out var node))
                        {
                            var label = Mono(segment); PathMenu(label, key.TrimEnd('/'));
                            nodes[key] = node = new TreeViewNode { Content = label, IsExpanded = true };
                            if (parent is null) tree.RootNodes.Add(node); else parent.Children.Add(node);
                        }
                        parent = node;
                    }
                    if (parent is not null) files[parent] = path;
                }
                tree.ItemInvoked += (_, args) => { if (args.InvokedItem is TreeViewNode node && files.TryGetValue(node, out var path)) options.OpenFile?.Invoke(path, null); };
                host.Children.Add(tree);
            }
        }
        selector.SelectedItem = model.Kind == OpenCodeToolKind.List ? treeChoice : listChoice;
        selector.SelectionChanged += (_, _) => Populate();
        Populate(); panel.Children.Add(selector); panel.Children.Add(host);
        if (hidden > 0) panel.Children.Add(ShowAllButton(hidden, options, rebuild));
        return panel;
    }

    private static FrameworkElement BuildWebFetch(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, out int hidden)
    {
        var source = model.OutputText ?? string.Empty;
        var lines = source.Split('\n');
        var limit = options.Compact ? 6 : options.ShowAll ? lines.Length : MaxRows;
        hidden = Math.Max(0, lines.Length - limit);
        var shown = string.Join('\n', lines.Take(limit));
        var format = Field(StateObject(model, "input"), "format");
        var panel = new StackPanel { Spacing = 12 };
        if (!options.Compact)
        {
            var selector = new SelectorBar();
            var content = new SelectorBarItem { Text = "内容" }; var raw = new SelectorBarItem { Text = "原文" };
            selector.Items.Add(content); selector.Items.Add(raw); selector.SelectedItem = options.ShowSource ? raw : content;
            selector.SelectionChanged += (_, _) => { options.ShowSource = selector.SelectedItem == raw; rebuild(); };
            panel.Children.Add(selector);
        }
        if (format == "markdown" && !options.ShowSource)
        {
            var markdown = new OpenCodeRichTextPresenter(); markdown.Render(shown, OpenCodeRichTextStyle.Body); EnforceWrapping(markdown.Element); panel.Children.Add(markdown.Element);
        }
        else panel.Children.Add(CodeBlock(format == "html" && !options.ShowSource ? OpenCodeToolDetailResultData.HtmlText(shown) : shown));
        if (hidden > 0 && !options.Compact) panel.Children.Add(ShowAllButton(hidden, options, rebuild));
        return panel;
    }

    private static FrameworkElement BuildWebResults(OpenCodeToolRowModel model, OpenCodeToolDetailOptions options, Action rebuild, bool code, out int hidden)
    {
        var data = OpenCodeToolDetailBuiltinData.WebSearch(model);
        if (data.Results.Count == 0) { hidden = 0; return CodeBlock(model.OutputText ?? "（没有结果）"); }
        var limit = options.Compact ? 3 : options.ShowAll ? data.Results.Count : MaxRows;
        hidden = Math.Max(0, data.Results.Count - limit);
        if (options.Compact && hidden > 0) options.PreviewHint = $"还有 {hidden} 条结果";
        var panel = new StackPanel { Spacing = 12 };
        var firstHeading = Regex.Match(model.OutputText ?? string.Empty, @"(?m)^#{1,6}\s*\[");
        var partial = model.Truncated || data.Results.Any(result => result.Url is null)
            || firstHeading.Success && !string.IsNullOrWhiteSpace(model.OutputText![..firstHeading.Index]);
        panel.Children.Add(Caption($"{data.Results.Count}{(partial ? "+" : string.Empty)} 条"));
        panel.Children.Add(ResultRepeater(data.Results.Take(limit).Cast<object>(), item =>
        {
            var result = (OpenCodeWebSearchResult)item;
            var resultPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
            if (Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
            {
                var site = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                site.Children.Add(new PersonPicture { Initials = uri.Host[..1].ToUpperInvariant(), Width = 24, Height = 24 });
                site.Children.Add(Caption(uri.Host + uri.AbsolutePath)); resultPanel.Children.Add(site);
                var title = new HyperlinkButton { Content = result.Title, NavigateUri = uri, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
                ToolTipService.SetToolTip(title, result.Url); resultPanel.Children.Add(title);
            }
            else resultPanel.Children.Add(Caption(result.Title));
            var snippet = code ? Mono(result.Snippet) : OpenCodeInteractionUi.StyledText(result.Snippet, "BodyTextBlockStyle", true);
            snippet.TextWrapping = TextWrapping.Wrap; snippet.MaxLines = options.Compact ? 2 : 3; snippet.TextTrimming = TextTrimming.CharacterEllipsis;
            resultPanel.Children.Add(snippet);
            return resultPanel;
        }));
        if (hidden > 0 && !options.Compact) panel.Children.Add(ShowAllButton(hidden, options, rebuild));
        return panel;
    }
}
