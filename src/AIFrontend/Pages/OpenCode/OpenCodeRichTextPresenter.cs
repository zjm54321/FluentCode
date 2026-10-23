using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using AIFrontend.Services.Rendering;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using Windows.Storage.Streams;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace AIFrontend.Pages.OpenCode;

/// <summary>OpenCode 消息富文本的呈现样式。</summary>
public enum OpenCodeRichTextStyle
{
    /// <summary>正文（助手 text 部件 / 用户消息）：默认字号、不透明。</summary>
    Body = 0,

    /// <summary>思考过程（reasoning 部件）：12px、75% 不透明。</summary>
    Reasoning = 1,
}

/// <summary>
/// OpenCode 消息富文本呈现器：把一段消息源文本（Markdown，或作为 Markdown 处理的 HTML 片段）
/// 转成<strong>原生控件子树</strong>（<see cref="RichTextBlock"/> / <see cref="Paragraph"/> /
/// <see cref="Run"/> / <see cref="Grid"/> / <see cref="Border"/> 等）。
/// </summary>
/// <remarks>
/// <para>
/// 普通 sealed 业务类（<strong>不是</strong>控件子类）：构造时创建同一实例的根
/// <see cref="StackPanel"/> 并经 <see cref="Element"/> 暴露；不派生控件、不自定义模板、不重写测量、不自绘、
/// 不创建 WebView，因此可安全用于最近 100 条消息（无“每条消息一个浏览器”）。
/// </para>
/// <para>
/// 安全边界（<strong>唯一</strong>复用共享 <see cref="SafeContentRenderer"/>，未削弱其策略）：
/// <list type="number">
/// <item>源文本先经 Markdig（本地管线，HTML 保留）转为 HTML——这是共享
/// <c>SafeContentRenderer.RenderMarkdown</c> 的 <c>DisableHtml</c> 语义之外的<strong>本 helper 局部管线</strong>，
/// 用于保留安全 inline HTML；<strong>未修改</strong>共享渲染器的 <c>DisableHtml</c> 语义。</item>
/// <item>随后<strong>必定</strong>经共享 <see cref="SafeContentRenderer.SanitizeHtml"/> 清洗：移除全部属性
/// （含 href/src/style/on*）、资源元素（HTML img 等）、script/style/form/iframe/object 等；因此模型输出
/// 不能执行脚本、加载任意外部资源或产生可导航链接。</item>
/// <item>真正 Markdown AST 图片另经显式会话作用域与受限栅格文件传输，HTML img 永不进入图片加载路径。</item>
/// <item>只解析<strong>已清洗</strong>后的 fragment（AngleSharp 仅作为解析器，不做净化）；遍历 DOM 时
/// 只读取标签名与文本，忽略一切属性，不注入任何 URL 或可执行内容。</item>
/// <item>Markdig/清洗/解析任一步抛异常（如超过 256K 输入）→ 降级为<strong>可选择的明示纯文本</strong>，
/// 展示<strong>完整</strong>原文，绝不截断、绝不注入未渲染原文、绝不静默消失。</item>
/// </list>
/// </para>
/// <para>
/// 支持的常用富文本：标题（h1–h6）、段落、粗体/斜体/下划线、有序/无序列表（含嵌套）、引用、
/// 围栏/缩进代码块（等宽、长行水平滚动）、行内代码、表格（水平滚动、限高）、分隔线、定义列表。
/// 有意<strong>不</strong>支持（降级为纯文本，见合同）：删除线（s/del）、上/下标（sub/sup）、
/// 链接跳转（<c>a</c> 仅保留文字、不保留 href、不可点击）、HTML 图片、外部资源、任意网页完整 HTML 布局。
/// </para>
/// </remarks>
public sealed class OpenCodeRichTextPresenter
{
    /// <summary>嵌套深度上限：超过即降级为纯文本，避免恶意深层嵌套导致栈溢出。</summary>
    private const int MaximumDepth = 64;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    // 无状态：方法内每次新建 HtmlSanitizer 实例；共享只读管线。见 SafeContentRenderer 文档。
    private static readonly SafeContentRenderer Renderer = new();

    // 用户可选等宽字体（F11）：构建时取当前值；整块文本元素另经 AppFonts.Apply 随设置实时更新。
    private static FontFamily MonospaceFont => AppFonts.Monospace;

    // 标题字号：Body 与 Reasoning 两套（Reasoning 更小）。
    private static readonly double[] BodyHeadingSizes = { 20, 18, 16, 15, 14, 14 };
    private static readonly double[] ReasoningHeadingSizes = { 14, 13, 12, 12, 12, 12 };

    private readonly StackPanel _root = new()
    {
        Spacing = 6,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly OpenCodeImageRenderLifetime _renderLifetime = new();
    private readonly OpenCodeRichTextRenderGate _renderGate = new();
    // Keep the originating surface policy across image-scope rebinds and Unloaded/Loaded rerenders.
    private readonly bool _expandCodeBlocks = OpenCodeToolDetailContent.IsFullDetailBuild;
    private readonly List<Func<Task>> _pendingImageLoads = new();
    private string? _lastSource;
    private OpenCodeRichTextStyle _lastStyle;

    /// <summary>承载原生控件子树的根元素（构造时创建，同一实例；由调用方入树）。</summary>
    public FrameworkElement Element => _root;

    /// <summary>最近一次 <see cref="Render"/> 是否因转换异常处于明示纯文本降级态（供诊断/静态验证）。</summary>
    public bool IsPlainFallback { get; private set; }

    /// <summary>创建空的呈现器；通过 <see cref="Render"/> 填充内容。</summary>
    public OpenCodeRichTextPresenter()
    {
        _root.SizeChanged += (_, _) => ConstrainBlocks();
        // Reparenting can deliver the old Unloaded after the new Loaded.
        _root.Unloaded += (_, _) => { if (!_root.IsLoaded) _renderLifetime.Cancel(); };
        _root.Loaded += async (_, _) =>
        {
            ConstrainBlocks();
            if (_renderLifetime.IsCanceled) Render(_lastSource, _lastStyle);
            // Child Loaded can precede the presenter's own Loaded. Start those images
            // only now, with the original generation/token and all scope fences intact.
            var pending = _pendingImageLoads.ToArray();
            _pendingImageLoads.Clear();
            foreach (var load in pending) await load();
        };
    }

    /// <summary>
    /// 就地渲染一段源文本到 <see cref="Element"/> 的子树（保留根元素身份，便于增量更新与滚动保持）。
    /// </summary>
    /// <param name="source">Markdown 源文本或 HTML 片段；null/空白 → 折叠（不渲染）。</param>
    /// <param name="style">正文或思考样式。</param>
    public void Render(string? source, OpenCodeRichTextStyle style)
    {
        using var detailScope = OpenCodeToolDetailContent.DetailBuildScope(_expandCodeBlocks);
        var imageScope = OpenCodeImageContext.Find(_root);
        if (!_renderGate.ShouldRender(source, style, imageScope, _renderLifetime.IsCanceled)) return;

        _lastSource = source;
        _lastStyle = style;
        var (generation, renderToken) = _renderLifetime.Begin();
        _pendingImageLoads.Clear();
        _root.Opacity = style == OpenCodeRichTextStyle.Reasoning ? 0.75 : 1.0;

        if (string.IsNullOrWhiteSpace(source))
        {
            _root.Children.Clear();
            _root.Visibility = Visibility.Collapsed;
            IsPlainFallback = false;
            return;
        }

        _root.Visibility = Visibility.Visible;

        List<UIElement> blocks;
        try
        {
            blocks = new List<UIElement>();
            foreach (var segment in OpenCodeMarkdownImagePlan.Build(source))
            {
                if (segment.IsImage)
                    blocks.Add(CreateMarkdownImage(segment.Text, segment.Alt, segment.Title, generation, renderToken));
                else
                {
                    var html = Markdown.ToHtml(segment.Text, Pipeline);
                    var safe = Renderer.SanitizeHtml(html);
                    blocks.AddRange(BuildBlocks(safe, style, html));
                }
            }

            // 成功解析但清洗后无任何可渲染内容（如整段仅含被移除的 img/script）：
            // 仍以明示纯文本展示完整原文，避免消息“凭空消失”。
            if (blocks.Count == 0)
            {
                blocks = new List<UIElement> { BuildPlainFallback(source) };
                IsPlainFallback = true;
            }
            else
            {
                IsPlainFallback = false;
            }
        }
        catch (Exception)
        {
            // 绝不截断/丢失原文：完整原文以可选择纯文本展示，并显式标注未渲染。
            blocks = new List<UIElement> { BuildPlainFallback(source) };
            IsPlainFallback = true;
        }

        _root.Children.Clear();
        foreach (var block in blocks)
        {
            _root.Children.Add(block);
        }
        ConstrainBlocks();
    }

    private void ConstrainBlocks()
    {
        // A finite viewport reaches tables, inline code and nested quotes/lists, not just the outer scrollbar.
        // Do not cap the root to its old width: it must grow again when the conversation is resized.
        if (_root.ActualWidth <= 0) return;
        foreach (var block in _root.Children)
            OpenCodeToolDetailContent.EnforceWrapping(block, _root.ActualWidth);
    }

    private FrameworkElement CreateMarkdownImage(string reference, string alt, string? title, int generation, CancellationToken renderToken)
    {
        var label = string.IsNullOrWhiteSpace(alt) ? "图片" : alt;
        var status = new TextBlock { Text = label + " · 正在加载…", TextWrapping = TextWrapping.Wrap };
        AppFonts.Apply(status, AppFontSlot.Body);
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(image, label);
        var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(image);
        panel.Children.Add(status);
        var surface = new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(surface, "OpenCodeMarkdownImage");
        AutomationProperties.SetName(surface, label);
        ToolTipService.SetToolTip(surface, string.IsNullOrWhiteSpace(title) ? label : title);
        // Image is loaded only after entering the explicitly bound subtree, never from a selected/global source.
        surface.Loaded += async (_, _) =>
        {
            Task Load() => LoadMarkdownImageAsync(image, status, reference, label, generation, renderToken);
            if (!_root.IsLoaded)
            {
                _pendingImageLoads.Add(Load);
                return;
            }
            await Load();
        };
        return surface;
    }

    private async Task LoadMarkdownImageAsync(Image image, TextBlock status, string reference, string alt,
        int generation, CancellationToken renderToken)
    {
        var scope = OpenCodeImageContext.Find(_root);
        bool Current() => _renderLifetime.IsCurrent(generation, renderToken) && _root.IsLoaded
            && scope is { IsCurrent: true } && ReferenceEquals(scope, OpenCodeImageContext.Find(_root));
        if (!Current())
        {
            status.Text = alt + " · 图片上下文不可用";
            status.Visibility = Visibility.Visible;
            return;
        }
        status.Text = alt + " · 正在加载…";
        status.Visibility = Visibility.Visible;
        try
        {
            var file = await scope!.ReadAsync(reference, renderToken);
            if (!Current()) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(file.Bytes);
                await writer.StoreAsync();
            }
            if (!Current()) return;
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 1920 };
            await bitmap.SetSourceAsync(stream);
            if (!Current()) return;
            image.Source = bitmap;
            image.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (Current())
            {
                status.Text = alt + " · 图片无法加载（仅支持会话目录内的栅格图片）";
                status.Visibility = Visibility.Visible;
            }
        }
    }

    // ------------------------------------------------------------------ 解析（仅解析已清洗 fragment）

    private sealed record CodeLanguage(string Name);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IElement, CodeLanguage> CodeLanguages = new();

    private static List<UIElement> BuildBlocks(string safeHtml, OpenCodeRichTextStyle style, string generatedHtml)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument("<body></body>");
        var context = document.Body ?? document.DocumentElement
            ?? throw new InvalidOperationException("无法建立 HTML 片段解析上下文。");
        var nodes = parser.ParseFragment(safeHtml, context);

        // The sanitizer still removes ALL attributes. Recover only a normalized lexer hint
        // from matching generated code text; no original DOM node or attribute is rendered.
        var generated = parser.ParseDocument(generatedHtml);
        var originals = generated.QuerySelectorAll("pre");
        var index = 0;
        void BindCodeLanguage(INode node, int depth)
        {
            if (depth > MaximumDepth) return;
            if (node is IElement pre && pre.LocalName == "pre")
            {
                if (index < originals.Length && originals[index].TextContent == pre.TextContent)
                {
                    var originalCode = originals[index].QuerySelector("code");
                    foreach (var name in (originalCode?.GetAttribute("class") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (name.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
                        {
                            CodeLanguages.Add(pre, new CodeLanguage(OpenCodeFileDetailPolicy.NormalizeLanguage(name[9..])));
                            break;
                        }
                }
                index++;
            }
            foreach (var child in node.ChildNodes) BindCodeLanguage(child, depth + 1);
        }
        foreach (var node in nodes) BindCodeLanguage(node, 0);

        var blocks = new List<UIElement>();
        AppendBlocks(blocks, nodes, style, 0);
        return blocks;
    }

    private static void AppendBlocks(
        List<UIElement> blocks, IEnumerable<INode> nodes, OpenCodeRichTextStyle style, int depth)
    {
        foreach (var node in nodes)
        {
            switch (node.NodeType)
            {
                case NodeType.Text:
                    if (!string.IsNullOrWhiteSpace(node.TextContent))
                    {
                        blocks.Add(CreateParagraph(node, style));
                    }

                    break;

                case NodeType.Element:
                    if (node is IElement element)
                    {
                        var block = CreateBlock(element, style, depth);
                        if (block is not null)
                        {
                            blocks.Add(block);
                        }
                    }

                    break;
            }
        }
    }

    private static UIElement? CreateBlock(IElement element, OpenCodeRichTextStyle style, int depth)
    {
        if (depth > MaximumDepth)
        {
            return CreatePlainTextBlock(element.TextContent);
        }

        switch (element.LocalName)
        {
            case "p":
                return CreateParagraph(element, style);
            case "h1":
            case "h2":
            case "h3":
            case "h4":
            case "h5":
            case "h6":
                return CreateHeading(element, style);
            case "ul":
                return CreateList(element, ordered: false, style, depth);
            case "ol":
                return CreateList(element, ordered: true, style, depth);
            case "blockquote":
                return CreateQuote(element, style, depth);
            case "pre":
                return CreateCodeBlock(element);
            case "table":
                return CreateTable(element, style, depth);
            case "hr":
                return CreateSeparator();
            case "dl":
                return CreateDefinitionList(element, style, depth);
            case "div":
            case "section":
            case "article":
            case "main":
            case "body":
            case "header":
            case "footer":
            case "nav":
            case "aside":
            {
                var panel = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
                var childBlocks = new List<UIElement>();
                AppendBlocks(childBlocks, element.ChildNodes, style, depth + 1);
                foreach (var child in childBlocks)
                {
                    panel.Children.Add(child);
                }

                return panel.Children.Count == 0 ? null : panel;
            }

            default:
                // 未知/内联标签出现在块级位置：按段落处理（保留其安全后代文本与格式）。
                return CreateParagraph(element, style);
        }
    }

    // ------------------------------------------------------------------ 块级构造

    private static RichTextBlock CreateRichTextBlock(OpenCodeRichTextStyle style, bool wrap = true)
    {
        var block = new RichTextBlock
        {
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (style == OpenCodeRichTextStyle.Reasoning)
        {
            block.FontSize = 12;
        }

        return block;
    }

    private static UIElement CreateParagraph(INode node, OpenCodeRichTextStyle style)
    {
        var block = CreateRichTextBlock(style);
        var paragraph = new Paragraph();
        AppendInlines(paragraph.Inlines, node.ChildNodes, 0);
        if (paragraph.Inlines.Count == 0)
        {
            return CreatePlainTextBlock(node.TextContent);
        }

        block.Blocks.Add(paragraph);
        return block;
    }

    private static UIElement CreateHeading(IElement element, OpenCodeRichTextStyle style)
    {
        var level = element.LocalName.Length == 2 && char.IsDigit(element.LocalName[1])
            ? element.LocalName[1] - '0'
            : 6;
        level = Math.Clamp(level, 1, 6);
        var sizes = style == OpenCodeRichTextStyle.Reasoning ? ReasoningHeadingSizes : BodyHeadingSizes;

        var block = CreateRichTextBlock(style);
        block.FontSize = sizes[level - 1];
        block.FontWeight = FontWeights.SemiBold;
        block.Margin = new Thickness(0, level <= 2 ? 6 : 4, 0, 2);

        var paragraph = new Paragraph();
        AppendInlines(paragraph.Inlines, element.ChildNodes, 0);
        if (paragraph.Inlines.Count == 0)
        {
            return CreatePlainTextBlock(element.TextContent);
        }

        block.Blocks.Add(paragraph);
        return block;
    }

    private static UIElement CreateList(IElement list, bool ordered, OpenCodeRichTextStyle style, int depth)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        var index = 1;

        foreach (var child in list.ChildNodes)
        {
            if (child is not IElement item || item.LocalName != "li")
            {
                continue;
            }

            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = ordered ? index.ToString(CultureInfo.InvariantCulture) + "." : "•",
                FontSize = style == OpenCodeRichTextStyle.Reasoning ? 12 : 14,
                VerticalAlignment = VerticalAlignment.Top,
                IsTextSelectionEnabled = true,
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            // 内容列用 StackPanel + 星列 Grid，保证正文获得有限宽度可换行（不会无限测量）。
            var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
            var paragraph = new Paragraph();
            foreach (var node in item.ChildNodes)
            {
                if (node is IElement nested && nested.LocalName is "ul" or "ol")
                {
                    continue;
                }

                AppendInlines(paragraph.Inlines, new[] { node }, 0);
            }

            if (paragraph.Inlines.Count > 0)
            {
                var block = CreateRichTextBlock(style);
                block.Blocks.Add(paragraph);
                content.Children.Add(block);
            }

            foreach (var node in item.ChildNodes)
            {
                if (node is IElement nested && nested.LocalName is "ul" or "ol")
                {
                    var nestedBlock = CreateList(nested, nested.LocalName == "ol", style, depth + 1);
                    content.Children.Add(nestedBlock);
                }
            }

            if (content.Children.Count == 0)
            {
                content.Children.Add(CreatePlainTextBlock(item.TextContent));
            }

            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);

            index++;
        }

        if (panel.Children.Count == 0)
        {
            return CreatePlainTextBlock(list.TextContent);
        }

        return panel;
    }

    private static UIElement CreateQuote(IElement quote, OpenCodeRichTextStyle style, int depth)
    {
        var inner = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        var innerBlocks = new List<UIElement>();
        AppendBlocks(innerBlocks, quote.ChildNodes, style, depth + 1);
        foreach (var child in innerBlocks)
        {
            inner.Children.Add(child);
        }

        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Style = TryStyle("OpenCodeDividerBorderStyle"),
            Padding = new Thickness(10, 2, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = inner,
        };

        return border;
    }

    private static UIElement CreateCodeBlock(IElement pre)
    {
        var source = pre.TextContent ?? string.Empty;
        var code = new TextBlock
        {
            Text = source,
            FontFamily = MonospaceFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AppFonts.Apply(code, AppFontSlot.Monospace);

        var language = CodeLanguages.TryGetValue(pre, out var hint) ? hint.Name : "text";
        OpenCodeCodePalette.BindText(code, OpenCodeDetailTextPolicy.CodeDisplayText(source), language);

        // No inner viewport: wheel gestures and vertical extent belong to the conversation/popup body.
        return OpenCodeToolDetailContent.CodeBlockFrame(code, source, language);
    }

    private static UIElement? CreateTable(IElement table, OpenCodeRichTextStyle style, int depth)
    {
        var rows = new List<IElement>();
        CollectTableRows(table, rows);
        if (rows.Count == 0)
        {
            return null;
        }

        var cellRows = new List<List<IElement>>(rows.Count);
        var columns = 0;
        foreach (var row in rows)
        {
            var cells = new List<IElement>();
            foreach (var child in row.ChildNodes)
            {
                if (child is IElement cell && cell.LocalName is "th" or "td")
                {
                    cells.Add(cell);
                }
            }

            cellRows.Add(cells);
            columns = Math.Max(columns, cells.Count);
        }

        if (columns == 0)
        {
            return null;
        }

        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < cellRows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = cellRows[r];
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                var border = CreateTableCell(cell, cell.LocalName == "th", style, depth);
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        return new Border
        {
            Style = TryStyle("OpenCodeSubtleSurfaceStyle"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        };
    }

    private static Border CreateTableCell(IElement cell, bool header, OpenCodeRichTextStyle style, int depth)
    {
        var block = CreateRichTextBlock(style);
        if (header)
        {
            block.FontWeight = FontWeights.SemiBold;
        }

        var paragraph = new Paragraph();
        AppendInlines(paragraph.Inlines, cell.ChildNodes, depth + 1);
        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run { Text = NormalizeInlineText(cell.TextContent ?? string.Empty) });
        }

        block.Blocks.Add(paragraph);

        return new Border
        {
            BorderThickness = new Thickness(1),
            Style = TryStyle("OpenCodeCardOutlineStyle"),
            Padding = new Thickness(6, 3, 6, 3),
            Child = block,
        };
    }

    private static UIElement CreateDefinitionList(IElement list, OpenCodeRichTextStyle style, int depth)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };

        foreach (var child in list.ChildNodes)
        {
            if (child is not IElement entry)
            {
                continue;
            }

            if (entry.LocalName == "dt")
            {
                var term = CreateRichTextBlock(style);
                term.FontWeight = FontWeights.SemiBold;
                var paragraph = new Paragraph();
                AppendInlines(paragraph.Inlines, entry.ChildNodes, depth + 1);
                term.Blocks.Add(paragraph.Inlines.Count > 0
                    ? paragraph
                    : new Paragraph { Inlines = { new Run { Text = NormalizeInlineText(entry.TextContent ?? string.Empty) } } });
                panel.Children.Add(term);
            }
            else if (entry.LocalName == "dd")
            {
                var definition = new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(14, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                var definitionBlocks = new List<UIElement>();
                AppendBlocks(definitionBlocks, entry.ChildNodes, style, depth + 1);
                foreach (var definitionBlock in definitionBlocks)
                {
                    definition.Children.Add(definitionBlock);
                }

                if (definition.Children.Count == 0)
                {
                    definition.Children.Add(CreatePlainTextBlock(entry.TextContent));
                }

                panel.Children.Add(definition);
            }
        }

        if (panel.Children.Count == 0)
        {
            return CreatePlainTextBlock(list.TextContent);
        }

        return panel;
    }

    private static UIElement CreateSeparator() => new Border
    {
        Height = 1,
        Style = TryStyle("OpenCodeDividerSurfaceStyle"),
        Margin = new Thickness(0, 4, 0, 4),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    // ------------------------------------------------------------------ 行内构造

    private static void AppendInlines(InlineCollection target, IEnumerable<INode> nodes, int depth)
    {
        foreach (var node in nodes)
        {
            switch (node.NodeType)
            {
                case NodeType.Text:
                {
                    var text = NormalizeInlineText(node.TextContent ?? string.Empty);
                    if (text.Length > 0)
                    {
                        target.Add(new Run { Text = text });
                    }

                    break;
                }

                case NodeType.Element:
                    if (node is IElement element)
                    {
                        AppendInlineElement(target, element, depth);
                    }

                    break;
            }
        }
    }

    private static void AppendInlineElement(InlineCollection target, IElement element, int depth)
    {
        if (depth > MaximumDepth)
        {
            var text = NormalizeInlineText(element.TextContent ?? string.Empty);
            if (text.Length > 0)
            {
                target.Add(new Run { Text = text });
            }

            return;
        }

        switch (element.LocalName)
        {
            case "br":
                target.Add(new LineBreak());
                break;
            case "strong":
            case "b":
                AddSpan(target, new Bold(), element, depth);
                break;
            case "em":
            case "i":
                AddSpan(target, new Italic(), element, depth);
                break;
            case "u":
                AddSpan(target, new Underline(), element, depth);
                break;
            case "code":
            {
                var span = new Span { FontFamily = MonospaceFont, FontSize = 12 };
                AppendInlines(span.Inlines, element.ChildNodes, depth + 1);
                if (span.Inlines.Count > 0)
                {
                    target.Add(span);
                }

                break;
            }

            default:
                // a（href 已被移除，仅保留文字、不可点击）、s/del、sub/sup、span 及其他内联：
                // 直接透传其安全后代（不做删除线/上下标/链接跳转）。
                AppendInlines(target, element.ChildNodes, depth + 1);
                break;
        }
    }

    private static void AddSpan(InlineCollection target, Span span, IElement element, int depth)
    {
        AppendInlines(span.Inlines, element.ChildNodes, depth + 1);
        if (span.Inlines.Count > 0)
        {
            target.Add(span);
        }
    }

    // ------------------------------------------------------------------ 纯文本降级 / 工具

    private static UIElement CreatePlainTextBlock(string? text) => new TextBlock
    {
        Text = text ?? string.Empty,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static UIElement BuildPlainFallback(string source)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock
        {
            Text = "［内容无法安全渲染，已按纯文本显示］",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            // 完整原文，不截断；等宽便于阅读原始标记。
            Text = source,
            FontFamily = MonospaceFont,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return panel;
    }

    /// <summary>把连续的空白（含换行）折叠为单个空格，保留单个前导/尾随空格（贴近 HTML 渲染）。</summary>
    private static string NormalizeInlineText(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                previousWasSpace = false;
            }
        }

        return sb.ToString();
    }

    private static void CollectTableRows(IElement table, List<IElement> rows)
    {
        foreach (var child in table.ChildNodes)
        {
            if (child is not IElement element)
            {
                continue;
            }

            switch (element.LocalName)
            {
                case "tr":
                    rows.Add(element);
                    break;
                case "thead":
                case "tbody":
                case "tfoot":
                    foreach (var inner in element.ChildNodes)
                    {
                        if (inner is IElement row && row.LocalName == "tr")
                        {
                            rows.Add(row);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>样式中的 ThemeResource Setter 随主题更新；键缺失时保持原生默认外观。</summary>
    private static Style? TryStyle(string key)
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
            // 资源字典在极端时机（关闭中）可能不可用；回退默认外观即可。
        }

        return null;
    }
}
