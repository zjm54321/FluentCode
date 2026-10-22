using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _assertions;

    private static void Check(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
    }

    private static IReadOnlyList<OpenCodeToolProperty> Props(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OpenCodeToolRowPresentation.BuildProperties(document.RootElement.Clone());
    }

    private static OpenCodeToolProperty Row(IReadOnlyList<OpenCodeToolProperty> rows, string key, int depth = 0)
        => rows.Single(r => r.Key == key && r.Depth == depth);

    private static OpenCodeToolRowModel Model(string name, string state)
    {
        using var document = JsonDocument.Parse(state);
        return OpenCodeToolRowPresentation.Build(new OpenCodeContentPart
        {
            Id = "prt_1", Type = OpenCodePartTypes.Tool, Name = name, State = document.RootElement.Clone(),
        });
    }

    /// <summary>Wave 5 G7/G8: large view ≈80% centered, hover preview small + adjacent, hide-on-leave, wrap-by-default, no truncation.</summary>
    private static void DetailPopupContract(string[] args)
    {
        // Large view: 80% of the window, centered, follows window size, never exceeds the window.
        var large = OpenCodeDetailPopupLayout.LargeView(1600, 1000);
        Check(large.Width == 1280 && large.Height == 800 && large.X == 160 && large.Y == 100, "large view = 80% of window, centered");
        var resized = OpenCodeDetailPopupLayout.LargeView(1000, 700);
        Check(resized.Width == 800 && resized.Height == 560 && resized.X == 100 && resized.Y == 70, "large view recomputed for a resized window");
        var tiny = OpenCodeDetailPopupLayout.LargeView(300, 200);
        Check(tiny.Width <= 300 - 2 * OpenCodeDetailPopupLayout.WindowMargin && tiny.Height <= 200 - 2 * OpenCodeDetailPopupLayout.WindowMargin
            && tiny.X >= OpenCodeDetailPopupLayout.WindowMargin && tiny.Y >= OpenCodeDetailPopupLayout.WindowMargin, "large view stays inside a tiny window");
        Check(OpenCodeDetailPopupLayout.LargeViewFraction == 0.8, "large view fraction is 80%");

        // A8: horizontal bounds come from the message content column (not the window), inset ~10% per side.
        // Content column in a 1600-wide window: left 390, width 820 (OpenCodeTimelineMaxWidth), so band = [472, 1128].
        var band = OpenCodeDetailPopupLayout.PreviewSafeBand(390, 1210, 1600)!.Value;
        Check(OpenCodeDetailPopupLayout.ContentEdgeFraction == 0.1 && band.Left == 472 && band.Right == 1128 && band.Width == 656,
            "safe band = content column inset 10% per side, non-zero column origin, never the window");
        Check(OpenCodeDetailPopupLayout.PreviewSafeBand(double.NaN, 800, 1600) is null && OpenCodeDetailPopupLayout.PreviewSafeBand(500, 500, 1600) is null
            && OpenCodeDetailPopupLayout.PreviewSafeBand(600, 400, 1600) is null && OpenCodeDetailPopupLayout.PreviewSafeBand(0, double.PositiveInfinity, 1600) is null,
            "invalid / zero / inverted content bounds yield no band (caller must not fall back to window or (0,0))");
        var windowEdge = OpenCodeDetailPopupLayout.PreviewSafeBand(-100, 700, 600)!.Value;
        Check(windowEdge.Left == 8 && windowEdge.Right == 592, "band additionally respects the 8px window margin");
        var previewWidth = OpenCodeDetailPopupLayout.PreviewWidths(1000, band).Max;
        var previewHeight = OpenCodeDetailPopupLayout.PreviewHeightLimit(1000);
        Check(OpenCodeDetailPopupLayout.PreviewWidths(500, band) == (300, 600) && previewHeight == 984, "row bounds 0.6A/1.2A when the safe band is wider");
        Check(OpenCodeDetailPopupLayout.PreviewWidths(1000, band) == (600, 656), "row-based max width clamps to the content safe band");
        var narrowBand = OpenCodeDetailPopupLayout.PreviewSafeBand(100, 400, 1600)!.Value;
        Check(narrowBand.Left == 130 && narrowBand.Right == 370 && OpenCodeDetailPopupLayout.PreviewWidths(300, narrowBand) == (180, 240)
            && OpenCodeDetailPopupLayout.PreviewWidths(1000, narrowBand) == (240, 240), "narrow column: preview width itself shrinks to the safe band");
        var measurements = 0;
        var fitsNarrow = OpenCodeDetailPopupLayout.FitPreview(500, band, 500, width => { measurements++; return 96; });
        Check(fitsNarrow == (300, 96, false) && measurements == 1, "short wrapped content keeps minimum row width and natural height without probing wider widths");
        var adaptive = OpenCodeDetailPopupLayout.FitPreview(500, band, 500, width => 180000 / width);
        Check(adaptive.Width >= 360 && adaptive.Width < 361 && adaptive.FullHeight <= 500 && !adaptive.Clipped, "expand only to the narrowest fitting wrapped width within one DIP");
        var constrained = OpenCodeDetailPopupLayout.FitPreview(500, band, 500, width => 1000000 / width);
        Check(constrained.Width == 600 && constrained.Clipped, "only content still too tall at maximum row width requires vertical scrolling and full-detail hint");
        var squeezed = OpenCodeDetailPopupLayout.FitPreview(1000, narrowBand, 500, width => 1000000 / width);
        Check(squeezed.Width == 240 && squeezed.Clipped, "narrow column fit never exceeds the safe band width");

        // Pointer-centred X, clamped to the band (not the source-row centre).
        var centred = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, 400, 300, band, 1000);
        Check(centred.X == 600 && centred.X + centred.Width / 2 == 800 && centred.Y == 240, "preview horizontally centred on the pointer X, below with 8px gap");
        var nearLeft = OpenCodeDetailPopupLayout.PreviewPlacement(400, 200, 32, 400, 300, band, 1000);
        Check(nearLeft.X == 472, "pointer near the column's left edge: clamps to the 10% left margin");
        var nearRight = OpenCodeDetailPopupLayout.PreviewPlacement(1200, 200, 32, 400, 300, band, 1000);
        Check(nearRight.X + nearRight.Width == 1128, "pointer near the column's right edge: clamps to the 10% right margin");
        var outside = OpenCodeDetailPopupLayout.PreviewPlacement(1590, 200, 32, 400, 300, band, 1000);
        Check(outside.X + outside.Width <= 1128 && outside.X >= 472, "pointer near the window edge never escapes the content column");
        var tooWide = OpenCodeDetailPopupLayout.PreviewPlacement(250, 200, 32, 900, 300, narrowBand, 1000);
        Check(tooWide.Width == 240 && tooWide.X == 130 && tooWide.X + tooWide.Width == 370, "preview wider than a narrow band shrinks to it before clamping");
        Check(OpenCodeDetailPopupLayout.PreviewX(double.NaN, 200, band) == 700, "non-finite pointer falls back to the band centre, still inside");
        var lastX = double.NegativeInfinity;
        for (var px = 0d; px <= 1600; px += 20)
        {
            var x = OpenCodeDetailPopupLayout.PreviewX(px, 400, band);
            Check(x >= lastX && x >= band.Left && x + 400 <= band.Right, "pointer sweep: X is monotone and always inside the band");
            lastX = x;
        }

        // Vertical behaviour unchanged: above/below roomier side, 8px gap, natural/auto height.
        var below = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, previewWidth, 300, band, 1000);
        Check(below.Y == 240, "wide row: preview below with 8px gap");
        var above = OpenCodeDetailPopupLayout.PreviewPlacement(800, 900, 32, previewWidth, 300, band, 1000);
        Check(above.Y + above.Height == 892, "row near bottom: preview above with 8px gap");
        var clamped = OpenCodeDetailPopupLayout.PreviewPlacement(1400, 980, 16, previewWidth, 300, band, 1000);
        Check(clamped.X + clamped.Width <= 1128 && clamped.Y + clamped.Height <= 992, "preview clamped inside content band and window height");
        var shortPreview = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, 220, 96, band, 1000);
        Check(shortPreview.Width == 220 && shortPreview.Height == 96 && shortPreview.Y == 240, "short measured content keeps its natural size, not the maximum empty surface");
        var longPreview = OpenCodeDetailPopupLayout.PreviewPlacement(800, 200, 32, 600, 5000, band, 1000);
        Check(longPreview.Height == 752 && longPreview.Width == 600, "long measured content clamps to roomier side for scrolling");
        foreach (var (w, h) in new[] { (1600d, 1000d), (800d, 600d), (300d, 200d), (120d, 80d) })
        foreach (var y in new[] { 8d, h / 2, h - 40 })
        foreach (var x in new[] { 0d, w - 20 })
        {
            var b = OpenCodeDetailPopupLayout.PreviewSafeBand(w * 0.1, w * 0.9, w)!.Value;
            var r = OpenCodeDetailPopupLayout.PreviewPlacement(x, y, 24,
                OpenCodeDetailPopupLayout.PreviewWidths(w, b).Max, OpenCodeDetailPopupLayout.PreviewHeightLimit(h), b, h);
            Check(r.X >= b.Left && r.X + r.Width <= b.Right && r.X >= 8 && r.X + r.Width <= w - 8 && r.Y >= 8 && r.Y + r.Height <= h - 8,
                "edge/small viewport: width within content band AND height clamp");
            Check(r.Y >= y + 24 + 8 || r.Y + r.Height <= y - 8, "edge placement is exclusively above or below with gap");
            var room = Math.Max(y - 16, h - y - 40);
            Check(r.Height == Math.Max(0, room), "height fills roomier vertical side instead of a fixed cap");
        }

        // Hide on leave: leaving the row hides unless the pointer is in the preview; leaving both hides.
        var hover = new OpenCodeDetailHoverTracker();
        hover.EnterAnchor();
        Check(!hover.ShouldHide, "pointer on row keeps preview");
        hover.ExitAnchor();
        Check(hover.ShouldHide, "leaving the row (not into preview) hides");
        hover.EnterPreview();
        Check(!hover.ShouldHide, "moving into the preview keeps it");
        hover.ExitPreview();
        Check(hover.ShouldHide, "leaving both row and preview hides");
        hover.EnterAnchor();
        hover.Reset();
        Check(hover.ShouldHide && !hover.InAnchor && !hover.InPreview, "reset clears pointer state");
        Check(OpenCodeDetailPopupLayout.HoverLeaveGraceMs == 240, "bounded leave grace allows pointer to cross the 8px gap");

        // G8 / no truncation: chunks reassemble to the full text (no dropped lines), wrap is default-on.
        Check(OpenCodeDetailTextPolicy.WrapByDefault, "detail text wraps by default");
        var longText = string.Join("\n", Enumerable.Range(1, 5000).Select(i => "line " + i)) + "\r\n";
        var chunks = OpenCodeDetailTextPolicy.ChunkLines(longText);
        Check(chunks.Count == (5000 + OpenCodeDetailTextPolicy.CodeBlockChunkLines - 1) / OpenCodeDetailTextPolicy.CodeBlockChunkLines, "long block split into chunks");
        Check(string.Join("\n", chunks) == longText.TrimEnd('\r', '\n') && chunks[^1].EndsWith("line 5000", StringComparison.Ordinal), "chunks keep every line (no truncation)");
        Check(OpenCodeDetailTextPolicy.ChunkLines("a\r\nb").Single() == "a\nb" && OpenCodeDetailTextPolicy.ChunkLines("").Single() == string.Empty, "CR stripped, empty text yields one empty chunk");

        // Source contract (WinUI files cannot be compiled headless): popup + content view.
        if (args.Length == 0)
        {
            throw new InvalidOperationException("FAIL: repo path argument required for the popup source contract");
        }

        var dir = System.IO.Path.Combine(args[0], "src", "AIFrontend", "Pages", "OpenCode");
        var permissions = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodePermissionPresentation.cs"));
        Check(permissions.Contains("\"todowrite\" => \"更新 to do\"") && permissions.Contains("\"todoread\" => \"读取 to do\""),
            "A10 permission copy changes labels, never tool ids");
        var popup = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodeToolDetailPopup.cs"));
        var content = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodeToolDetailContent.cs"));
        Check(popup.Contains("internal const int HoverOpenDelayMs = 800;") && popup.Contains("internal const int HoverSwitchDelayMs = 0;"), "confirmed initial 800ms / open-preview row switch 0ms contract");
        Check(popup.Contains("_previewPresenter.Measure(new Size(width, double.PositiveInfinity))") && popup.Contains("FitPreview(anchor.ActualWidth")
            && popup.Contains("_previewPresenter.MaxHeight = double.PositiveInfinity") && !popup.Contains("PreviewWidth("), "native preview measures full wrapped content and every opening path uses the new row-based fitter, not the superseded workspace cap");
        Check(popup.Contains("_previewPresenter.MaxHeight = capacity.Height") && !popup.Contains("_previewPresenter.Height = rect.Height"), "preview retains natural automatic height as streaming text/images grow, bounded only by side capacity");
        Check(!popup.Contains("OpenCodeToolDetailWrap") && !popup.Contains("\"换行\"") && !popup.Contains("_wrapButton"), "no wrap toggle in the popup");
        Check(popup.Contains("\"OpenCodeToolDetailPreview\"") && popup.Contains("\"OpenCodeToolDetailPopup\"") && popup.Contains("\"OpenCodeToolDetailOverlay\""), "preview / large view / overlay AutomationIds");
        Check(popup.Contains("_dialogChrome.Actions.Children.Add(_closeButton)") && !popup.Contains("_previewChrome.Actions.Children.Add("), "close button only in the large view (preview has none)");
        Check(popup.Contains("SmokeFillColorDefaultBrush") && popup.Contains("_smoke.Tapped") && popup.Contains("VirtualKey.Escape"), "large view: smoke layer, outside click + Esc close");
        Check(popup.Contains("root.Changed += OnRootChanged") && popup.Contains("OpenCodeDetailPopupLayout.LargeView("), "large view follows window size");
        Check(popup.Contains("KeyboardNavigationMode.Cycle") && popup.Contains("restore.Focus(FocusState.Programmatic)"), "large view traps Tab and restores focus");
        Check(popup.Contains("_options.ShowAll = true;"), "large view shows full content (no row cap)");
        Check(popup.Contains("new FlyoutPresenter") && !popup.Contains("Color.FromArgb") && !popup.Contains("TryBrush"), "native FlyoutPresenter chrome, no hand-rolled colors");
        Check(popup.Contains("interface IOpenCodeDetailContentSource") && popup.Contains("OpenCodeToolDetailView BuildDetailContent();")
            && popup.Contains("OpenCodeDetailHeader DetailHeader { get; }") && popup.Contains("void OnDetailStateChanged(bool shown, bool pinned);"), "IOpenCodeDetailContentSource API stable");
        var toolPart = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodeToolPartView.cs"));
        var chipView = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodeTimelineChipView.cs"));
        var groupView = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodeToolGroupView.cs"));
        Check(!toolPart.Contains("ToolTipService.SetToolTip(_row") && !chipView.Contains("ToolTipService.SetToolTip(_chip"),
            "A8: duplicated native text tooltips on tool rows / timeline chips (终端完成 etc.) are removed");
        Check(toolPart.Contains("ToolTipService.SetToolTip(_backgroundButton") && groupView.Contains("ToolTipService.SetToolTip(_backgroundButton")
            && popup.Contains("ToolTipService.SetToolTip(_closeButton, \"关闭（Esc）\")") && content.Contains("ToolTipService.SetToolTip(copy"),
            "A8: unrelated action tooltips (background, close, copy) are kept");
        Check(toolPart.Contains("_row.PointerEntered += (_, e) => { OpenCodeToolDetailPopup.Shared.TrackPointer(_row, e); HoverEnterDetail(); };")
            && toolPart.Contains("_row.PointerMoved += (_, e) => OpenCodeToolDetailPopup.Shared.TrackPointer(_row, e);")
            && chipView.Contains("_chip.PointerEntered += (_, e) => { OpenCodeToolDetailPopup.Shared.TrackPointer(_chip, e); OpenCodeToolDetailPopup.Shared.HoverEnter(Source); };")
            && chipView.Contains("_chip.PointerMoved += (_, e) => OpenCodeToolDetailPopup.Shared.TrackPointer(_chip, e);")
            && groupView.Contains("if (_enterLeaf is not null) OpenCodeToolDetailPopup.Shared.TrackPointer(_header, e);")
            && groupView.Contains("_header.PointerMoved +="),
            "A8: row, chip and collapsed single-leaf header capture the entry point before hover and track moves");
        Check(popup.Contains("ReferenceEquals(element, _previewAnchor)") && popup.Contains("ReferenceEquals(element, anchor)")
            && popup.Contains("TryContentBand(anchor, root.Size.Width, out var band)") && popup.Contains("ContentColumnName = \"MessageHost\"")
            && popup.Contains("viewport.ViewportWidth") && !popup.Contains("origin = new Point(0, 0)") && popup.Contains("if (_previewPopup.IsOpen) Close();"),
            "A8: pointer applies only to the current anchor; bounds = MessageHost column ∩ visible scroll viewport; no window/(0,0) fallback");
        Check(popup.Contains("OpenCodeDetailPopupLayout.LargeView(") && popup.Contains("LayoutLarge(root.Size)"), "A8: large view branch unchanged");
        Check(!content.Contains("TextWrapping.NoWrap") && !content.Replace("preview.TextTrimming = TextTrimming.CharacterEllipsis;", string.Empty).Contains("TextTrimming.CharacterEllipsis") && !content.Contains("ScrollMode.Auto"), "full content always wraps without ellipsis/horizontal scroll; only explicitly collapsed code preview can elide");
        Check(!content.Contains("仅显示前") && content.Contains("OpenCodeDetailTextPolicy.ChunkLines"), "code blocks are not truncated");
        var child = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "OpenCodeChildChatWindow.cs"));
        Check(child.Contains("OpenCodeConversationView _timeline = new()") && child.Contains("_timeline.ApplyMessages(OpenCodeChildChatReadSession.OrderMessages(response.Data))")
            && !child.Contains("WebView") && !child.Contains("TextBox") && !child.Contains("SendPrompt"), "child window uses the actual timeline and has no composer/send surface");
        Check(child.Contains("new FlyoutPresenter") && child.Contains("new ThemeShadow()")
            && child.Contains("OpenCodeChildChatMaximize") && child.Contains("OpenCodeChildChatClose"), "child window has native Fluent chrome and accessible maximize/X actions");
        Check(child.Contains("SetSuppressDetails(_timeline, true)") && popup.Contains("DetailsSuppressed(AnchorOf(source))"),
            "nested child tool details cannot replace the global parent popup or recurse");
        Check(!child.Contains("960") && child.Contains("_root.Size.Width * 0.9") && child.Contains("HorizontalScrollMode = ScrollMode.Disabled"), "child uses wide rectangular viewport without artificial cap or sideways panning");
        Check(child.Contains("EnforceWrapping(_timeline, width)") && content.Contains("VisualTreeHelper.GetChild(root, i)") && content.Contains("rich.TextWrapping = TextWrapping.Wrap"), "actual nested timeline including rich text/code is wrapped, not just outer scrollbar hidden");
        Check(child.Contains("_timeline.Width = width") && child.Contains("_scroller.Width = Math.Max")
            && child.Contains("ScrollViewer.SetHorizontalScrollMode(_surface, ScrollMode.Disabled)") && content.Contains("element.MaxWidth = width"),
            "finite child body/timeline/descendant measure plus presenter-template scroll mode prevent sideways panning");
        Check(popup.Contains("_previewHint.Visibility = fit.Clipped || _previewTruncated ? Visibility.Visible : Visibility.Collapsed")
            && content.Contains("IsTruncated = false") && popup.Contains("OnPreviewContentLayoutUpdated"), "hint only for actual overflow/row truncation; extent changes remeasure asynchronous content");
        Check(popup.Contains("model.Kind == OpenCodeToolKind.Shell ? string.Empty : model.Target") && content.Contains("CodeBlock(\"$ \" + (model.Command ?? model.Target))"),
            "terminal command occurs once in body, never duplicated as popup header target; output path unchanged");
        Check(child.Contains("Action<OpenCodeConversationView>? initializeTimeline = null")
            && child.IndexOf("initializeTimeline?.Invoke(_timeline)", StringComparison.Ordinal) < child.IndexOf("body.Children.Add(_scroller)", StringComparison.Ordinal),
            "optional child-only context initialization precedes attachment and message rendering without parent fallback");
        Check(content.Contains("markdown.Render(source, OpenCodeRichTextStyle.Body)") && content.Contains("OpenCodeFileSourceToggle") && content.Contains("StyledText(text, OpenCodeInteractionUi.CodeStyleKey, true)"), "Markdown native safe renderer with selectable source access");
        var palette = System.IO.File.ReadAllText(System.IO.Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeCodePalette.cs"));
        var presenter = System.IO.File.ReadAllText(System.IO.Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeRichTextPresenter.cs"));
        Check(content.Contains("OpenCodeFileDetailPolicy.IsFullWrite") && content.Contains("OpenCodeCodePalette.BindText") && palette.Contains("OpenCodeFileDetailPolicy.Tokens") && palette.Contains("run.Text = text"), "file content routes to shared native syntax runs, real diff renderer retained");
        var codeFactory = presenter[presenter.IndexOf("private static UIElement CreateCodeBlock", StringComparison.Ordinal)..presenter.IndexOf("private static UIElement? CreateTable", StringComparison.Ordinal)];
        Check(!codeFactory.Contains("ScrollViewer") && !codeFactory.Contains("MaxHeight") && codeFactory.Contains("TextWrapping.Wrap"), "code block has no nested wheel/height trap and wraps");
        Check(codeFactory.Contains("OpenCodeCodePalette.BindText(code, OpenCodeDetailTextPolicy.CodeDisplayText(source), language)") && presenter.Contains("NormalizeLanguage(name[9..])") && presenter.Contains("originals[index].TextContent == pre.TextContent"), "only normalized matching Markdown code language reaches shared lexical palette without relaxing sanitizer");
        var tableFactory = presenter[presenter.IndexOf("private static UIElement? CreateTable", StringComparison.Ordinal)..presenter.IndexOf("private static UIElement CreateDefinitionList", StringComparison.Ordinal)];
        Check(!tableFactory.Contains("ScrollViewer") && !tableFactory.Contains("MaxHeight") && !tableFactory.Contains("wrap: false") && tableFactory.Contains("GridUnitType.Star"), "table columns wrap inside finite width with no inner scrolling or height cap");
        Check(presenter.Contains("_root.SizeChanged += (_, _) => ConstrainBlocks()") && presenter.Contains("EnforceWrapping(block, _root.ActualWidth)"), "all Markdown block descendants receive actual finite width on resizing");
        var frame = content[content.IndexOf("internal static FrameworkElement CodeBlockFrame", StringComparison.Ordinal)..content.IndexOf("internal static OpenCodeToolDetailView TextView", StringComparison.Ordinal)];
        Check(!frame.Contains("RowDefinitions") && !frame.Contains("Grid.SetRow") && frame.Contains("VerticalAlignment = VerticalAlignment.Top") && frame.Contains("layout.Children.Add(controls)"), "copy/toggle float in code cell without a dedicated header row");
        Check(frame.Contains("OpenCodeCodeBlockExpand") && frame.Contains("preview.MaxLines = OpenCodeDetailTextPolicy.CodePreviewLines") && frame.Contains("content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed"), "local native toggle keeps six-line wrapped preview and expands full content");
        Check(frame.Contains("copy.Tapped += (_, args) => args.Handled = true") && frame.Contains("toggle.Tapped += (_, args) => args.Handled = true"), "code controls consume taps rather than opening parent details");
        Check(content.Contains("OpenCodeCodeBlockCopy") && content.Contains("data.SetText(source)") && content.Contains("ToolTipService.SetToolTip(copy"), "native accessible per-block copy captures exact source");
        Check(palette.Contains("service.Changed -= Changed") && palette.Contains("element.Loaded +=") && palette.Contains("Restore(surface, Border.BackgroundProperty, background)"), "cached surfaces repaint on load, detach on unload and restore caller's default background");
        Check(content.Contains("OpenCodeFileCopySource") && content.Contains("data.SetText(source)") && content.Contains("Clipboard.SetContent(data)"), "copy preserves complete raw source including final newline in both preview/source modes");
        var write = Model("write", "{\"status\":\"completed\",\"input\":{\"filePath\":\"a.cs\",\"content\":\"class A {}\"}}");
        Check(write.DiffFiles.Count == 1 && OpenCodeFileDetailPolicy.IsFullWrite(write.Kind, write.DiffFiles[0]), "real write model is full file, not an all-green diff");
        Check(!OpenCodeFileDetailPolicy.IsFullWrite(OpenCodeToolKind.Patch, new OpenCodeToolDiffFile { Path = "a.cs", After = "x" }), "patch never becomes full-file just because it has a path/after");
        Check(!OpenCodeFileDetailPolicy.IsFullWrite(OpenCodeToolKind.Write, new OpenCodeToolDiffFile { Path = "a.cs", After = "x", Patch = "@@ -1 +1 @@\n-a\n+b" }), "explicit server patch retains semantic change marks even on write");
        Check(OpenCodeFileDetailPolicy.IsMarkdown("README.MD") && !OpenCodeFileDetailPolicy.IsMarkdown("a.cs"), "Markdown preview eligibility follows extension");
        var code = "public class A { // comment\n string s = \"safe\"; return 42; }";
        var tokens = OpenCodeFileDetailPolicy.Tokens(code, OpenCodeFileDetailPolicy.Language("a.cs")).ToList();
        Check(string.Concat(tokens.Select(t => t.Text)) == code, "syntax tokens preserve exact complete source for copy");
        Check(tokens.Any(t => t.Kind == "keyword") && tokens.Any(t => t.Kind == "comment") && tokens.Any(t => t.Kind == "string") && tokens.Any(t => t.Kind == "number"), "code highlighting distinguishes keyword/comment/string/number");
        Check(OpenCodeFileDetailPolicy.Tokens("<script>alert(1)</script>", "text").Single().Kind == "text", "unknown language source remains literal, never executable HTML");
        var categories = OpenCodeFileDetailPolicy.Tokens("class Demo { string Name = \"ok\"; int count = 42; // note\n Run(count + 1); }", "csharp").ToList();
        Check(new[] { "keyword", "type", "string", "number", "comment", "function", "operator" }.All(kind => categories.Any(token => token.Kind == kind)), "recognized code has differentiated lexical categories");
        const string exact = "let x = 1;\r\n\r\n";
        Check(string.Concat(OpenCodeFileDetailPolicy.Tokens(exact, "typescript").Select(t => t.Text)) == exact, "lexical coloring preserves CRLF and trailing blank lines");
        Check(OpenCodeDetailTextPolicy.CodeDisplayText(exact) == "let x = 1;" && exact.EndsWith("\r\n\r\n", StringComparison.Ordinal), "display removes trailing empty line without mutating copied source");
        Check(OpenCodeDetailTextPolicy.CodePreviewLines == 6 && !OpenCodeDetailTextPolicy.CanCollapseCode("a\nb\nc\nd\ne\nf\n"), "short six-line code remains completely visible without toggle");
        Check(OpenCodeDetailTextPolicy.CodePreview("a\r\nb\r\nc\r\nd\r\ne\r\nf\r\ng\r\n") == "a\r\nb\r\nc\r\nd\r\ne\r\nf", "collapsed preview retains exactly first six source lines and internal CRLF");
        Check(OpenCodeDetailTextPolicy.CanCollapseCode("a\nb\nc\nd\ne\nf\ng") && OpenCodeDetailTextPolicy.CanCollapseCode(new string('x', 241)), "multiline and very long single-line code offer expansion without an inner viewport");
    }

    private static int Main(string[] args)
    {
        try
        {
            // Scalars: short text, code-like keys, paths, numbers, booleans, empties.
            var rows = Props("{\"description\":\"列出文件\",\"command\":\"ls -la\",\"filePath\":\"src/a.cs\",\"other\":\"C:\\\\temp\\\\x\",\"timeout\":120,\"force\":true,\"note\":\"\",\"none\":null}");
            Check(Row(rows, "description").Kind == OpenCodeToolPropertyKind.Text && Row(rows, "description").Value == "列出文件", "plain text stays readable");
            Check(Row(rows, "command").Kind == OpenCodeToolPropertyKind.Code && Row(rows, "command").Value == "ls -la", "command is monospace");
            Check(Row(rows, "filePath").Kind == OpenCodeToolPropertyKind.Code, "path key is monospace");
            Check(Row(rows, "other").Kind == OpenCodeToolPropertyKind.Code && Row(rows, "other").Value == @"C:\temp\x", "path-like value is monospace and unescaped");
            Check(Row(rows, "timeout").Kind == OpenCodeToolPropertyKind.Code && Row(rows, "timeout").Value == "120", "number");
            Check(Row(rows, "force").Value == "是", "boolean is localized");
            Check(Row(rows, "note").Kind == OpenCodeToolPropertyKind.Empty && Row(rows, "none").Kind == OpenCodeToolPropertyKind.Empty, "empties");
            Check(rows.Select(r => r.Key).SequenceEqual(new[] { "description", "command", "filePath", "other", "timeout", "force", "note", "none" }), "server order kept");
            Check(rows.All(r => !r.Value.Contains('{') && !r.Value.Contains('"')), "no raw JSON syntax in values");

            // Long / multi-line strings become blocks with real newlines (not \n escapes).
            rows = Props("{\"content\":\"line1\\nline2\\n\",\"prompt\":\"" + new string('x', 150) + "\"}");
            Check(Row(rows, "content").Kind == OpenCodeToolPropertyKind.Block && Row(rows, "content").Value == "line1\nline2", "multi-line block, trailing newline trimmed");
            Check(Row(rows, "prompt").Kind == OpenCodeToolPropertyKind.Block, "long text is a block");

            // Nesting: groups + indented children; short scalar arrays inline; object arrays numbered.
            rows = Props("{\"options\":{\"depth\":2,\"inner\":{\"a\":\"b\"}},\"tags\":[\"x\",\"y\"],\"todos\":[{\"content\":\"写测试\",\"status\":\"done\"},{\"content\":\"发布\",\"status\":\"pending\"}],\"emptyList\":[],\"emptyObject\":{}}");
            Check(Row(rows, "options").Kind == OpenCodeToolPropertyKind.Group && Row(rows, "depth", 1).Value == "2"
                && Row(rows, "inner", 1).Kind == OpenCodeToolPropertyKind.Group && Row(rows, "a", 2).Value == "b", "nested objects indent");
            Check(Row(rows, "tags").Kind == OpenCodeToolPropertyKind.Text && Row(rows, "tags").Value == "x, y", "short scalar array inline");
            Check(Row(rows, "todos").Kind == OpenCodeToolPropertyKind.Group && Row(rows, "todos").Value == "2 项"
                && Row(rows, "1", 1).Kind == OpenCodeToolPropertyKind.Group && rows.Count(r => r.Key == "content" && r.Depth == 2) == 2, "object arrays numbered 1..N");
            Check(Row(rows, "emptyList").Kind == OpenCodeToolPropertyKind.Empty && Row(rows, "emptyObject").Kind == OpenCodeToolPropertyKind.Empty, "empty collections");

            // Defensive cap.
            var big = "{" + string.Join(",", Enumerable.Range(0, 2500).Select(i => "\"k" + i + "\":" + i)) + "}";
            rows = Props(big);
            Check(rows.Count == OpenCodeToolRowPresentation.MaxPropertyRows + 1 && rows[^1].Kind == OpenCodeToolPropertyKind.Empty, "row cap with note");

            // Structured output detection.
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput("  {\"ok\":true}\n").Single().Key == "ok", "JSON object output parsed");
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput("plain text {not json}").Count == 0, "plain output stays text");
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput("{broken").Count == 0, "broken JSON stays text");
            Check(OpenCodeToolRowPresentation.TryParseStructuredOutput(null).Count == 0, "null output");

            // Real model build: generic tool input/output become property lists; raw JSON text kept only as fallback.
            var model = Model("mcp_lookup", "{\"status\":\"completed\",\"input\":{\"query\":\"天气\",\"limit\":3},\"content\":[{\"type\":\"text\",\"text\":\"{\\\"city\\\":\\\"上海\\\",\\\"temp\\\":21}\"}]}");
            Check(model.InputProperties.Count == 2 && Row(model.InputProperties, "query").Value == "天气", "input properties built");
            Check(model.OutputProperties.Count == 2 && Row(model.OutputProperties, "city").Value == "上海", "JSON output properties built");
            var shell = Model("bash", "{\"status\":\"running\",\"input\":{\"command\":\"npm test\",\"description\":\"运行测试\"},\"metadata\":{}}");
            Check(shell.Command == "npm test" && Row(shell.InputProperties, "description").Value == "运行测试", "shell extra input available for the property list");
            var streaming = Model("bash", "{\"status\":\"streaming\",\"input\":\"{\\\"comm\"}");
            Check(streaming.InputProperties.Count == 0 && streaming.InputText == "{\"comm", "streaming partial input is not parsed");
            var unknown = Model("x", "{\"status\":\"weird\",\"detail\":{\"a\":1}}");
            Check(unknown.RawState is not null && unknown.RawStateProperties.Any(r => r.Key == "detail" && r.Kind == OpenCodeToolPropertyKind.Group), "unrecognized state rendered as properties");

            var todoWrite = Model("todowrite", "{\"status\":\"completed\",\"input\":{\"todos\":[{\"content\":\"待办内容原样保留\",\"status\":\"pending\"}]}}");
            var todoRead = Model("todoread", "{\"status\":\"completed\",\"input\":{}}");
            Check(todoWrite.Name == "更新 to do" && todoRead.Name == "查看 to do", "A10 app-owned to do tool-row copy");
            Check(OpenCodeToolRowPresentation.DisplayName("custom_todowrite") == "custom_todowrite"
                && Row(todoWrite.InputProperties, "content", 2).Value == "待办内容原样保留"
                && Row(todoWrite.InputProperties, "status", 2).Value == "pending", "A10 custom ids, protocol fields and tool input content are unchanged");

            DetailPopupContract(args);
            ChildChatLifecycle().GetAwaiter().GetResult();

            Console.WriteLine($"PASS: tool detail property list + detail popup (G7/G8), {_assertions} assertions; real OpenCodeToolRowPresentation + OpenCodeDetailPopupLayout, no UI.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task ChildChatLifecycle()
    {
        // Execute the exact production helper called by native ShowMessages, not the fake page window.
        // Deliberately non-monotonic timestamps ensure server sequence, not timestamp sorting, is authoritative.
        var latest = new OpenCodeMessage { Id = "msg_latest", Type = "user", Text = "latest", Time = new() { Created = 10 } };
        var middle = new OpenCodeMessage { Id = "msg_middle", Type = "user", Text = "middle", Time = new() { Created = 900 } };
        var oldest = new OpenCodeMessage { Id = "msg_oldest", Type = "user", Text = "oldest", Time = new() { Created = 80 } };
        var descending = new List<OpenCodeMessage> { latest, middle, oldest };
        var ordered = OpenCodeChildChatReadSession.OrderMessages(descending);
        Check(ordered.Select(m => m.Id).SequenceEqual(new[] { "msg_oldest", "msg_middle", "msg_latest" }),
            "native child ordering helper reverses server-desc sequence to oldest-first without timestamp sorting");
        Check(ReferenceEquals(ordered[^1], latest) && ReferenceEquals(ordered[0], oldest),
            "native child final rendered item is latest; message instances are preserved");
        Check(descending.SequenceEqual(new[] { latest, middle, oldest }) && !ReferenceEquals(descending, ordered),
            "native child ordering copies without mutating the server response list");
        Check(OpenCodeChildChatReadSession.OrderMessages(null).Count == 0
            && OpenCodeChildChatReadSession.OrderMessages(Array.Empty<OpenCodeMessage>()).Count == 0,
            "native child ordering handles null/empty response data");
        var payload = new OpenCodeSessionMessagesResponse { Data = new() { new OpenCodeMessage { Id = "msg_child", Type = "user", Text = "真实子消息" } } };
        var current = true;
        var reads = 0;
        var errors = 0;
        OpenCodeSessionMessagesResponse? published = null;
        Task Read(Func<CancellationToken, Task<bool>> verify, CancellationToken token = default,
            Func<CancellationToken, Task<OpenCodeSessionMessagesResponse>>? fetch = null)
            => OpenCodeChildChatReadSession.LoadAsync(() => current, verify,
                fetch ?? (_ => { reads++; return Task.FromResult(payload); }),
                value => published = value, _ => errors++, token);
        await Read(_ => Task.FromResult(true));
        Check(ReferenceEquals(published, payload) && reads == 1, "verified read publishes the actual response instance, no mock projection/fallback");
        published = null; reads = 0;
        await Read(_ => Task.FromResult(false));
        Check(reads == 0 && published is null && errors == 1, "unrelated child never fetches or displays messages");
        current = false;
        var verifies = 0;
        await Read(_ => { verifies++; return Task.FromResult(true); });
        Check(verifies == 0 && reads == 0, "invalid source binding starts NO reads");
        current = true;
        var ancestry = new TaskCompletionSource<bool>();
        var pending = Read(_ => ancestry.Task);
        current = false; ancestry.SetResult(true); await pending;
        Check(reads == 0 && published is null, "binding change during ancestry blocks child fetch");
        current = true;
        using var close = new CancellationTokenSource();
        var response = new TaskCompletionSource<OpenCodeSessionMessagesResponse>();
        pending = Read(_ => Task.FromResult(true), close.Token, _ => response.Task);
        close.Cancel(); response.SetResult(payload); await pending;
        Check(published is null, "close/navigation cancellation blocks late response publication even if transport ignores cancellation");
        response = new();
        pending = Read(_ => Task.FromResult(true), default, _ => response.Task);
        current = false; response.SetResult(payload); await pending;
        Check(published is null, "source/session change blocks stale message response");
        current = true;
        await Read(_ => Task.FromResult(true), default, _ => Task.FromException<OpenCodeSessionMessagesResponse>(new InvalidOperationException("read failed")));
        Check(errors == 2 && published is null, "fetch failure reports truthfully without fallback content");
        await Read(_ => Task.FromException<bool>(new InvalidOperationException("ancestry failed")));
        Check(errors == 3 && published is null, "ancestry exception fails closed");

        var parentSelection = "ses_parent";
        var closes = 0;
        var navigations = 0;
        OpenCodeChildChatActions Actions() => new(() => current, () => closes++, () =>
        {
            Check(closes > 0, "maximize closes the small window before full navigation");
            navigations++; parentSelection = "ses_child"; return Task.CompletedTask;
        });
        var actions = Actions();
        await actions.MaximizeAsync();
        Check(closes == 0 && navigations == 0, "loading/unverified window cannot maximize");
        actions.MarkLoaded(); actions.Close(); actions.Close();
        await actions.MaximizeAsync();
        Check(closes == 1 && navigations == 0 && parentSelection == "ses_parent", "X is idempotent, cancels/dismisses only and preserves parent selection");
        actions = Actions(); actions.MarkLoaded(); current = false;
        await actions.MaximizeAsync();
        Check(navigations == 0, "stale binding blocks maximize");
        current = true;
        await actions.MaximizeAsync(); await actions.MaximizeAsync();
        Check(closes == 2 && navigations == 1 && parentSelection == "ses_child", "only maximize navigates, once");
    }
}
