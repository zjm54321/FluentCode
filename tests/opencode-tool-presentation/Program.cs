using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(name);
    }

    private static void Main(string[] args)
    {
        ToolNamingAndDetails();
        HeaderAffordance(args[0]);
        PreviewThemeAndPlacement(args[0]);
        Console.WriteLine($"PASS {_checks} focused tool naming, glyph, generic-detail, working-activity, step-summary, header-affordance and preview theme/placement checks.");
    }

    private static void PreviewThemeAndPlacement(string repo)
    {
        // The hover preview leaves the whole neighbouring row (tallest = running 36px + 2px margins) uncovered.
        const double margin = OpenCodeDetailPopupLayout.WindowMargin, gap = OpenCodeDetailPopupLayout.PreviewGap;
        var running = OpenCodeToolRowStatus.Running;
        var tallestRow = OpenCodeProcessedRowMetrics.Height(running) + 2 * OpenCodeProcessedRowMetrics.VerticalGap(running);
        Check(OpenCodeDetailPopupLayout.PreviewNeighbourRowClearance >= tallestRow, "clearance covers the tallest timeline row slot");
        Check(gap > OpenCodeDetailPopupLayout.PreviewNeighbourRowClearance, "visual separation beyond the neighbouring row");
        Check(OpenCodeDetailPopupLayout.HoverLeaveGraceMs >= 300, "grace still bridges the larger gap into the preview");
        foreach (var rowHeight in new[] { 30d, 36d })
        {
            // Plenty of room below: the preview starts after the next row, never on it.
            var below = OpenCodeProcessedPreviewPlacement.Vertical(100, rowHeight, 200, 900, margin, gap);
            Check(below.Y >= 100 + rowHeight + tallestRow, "below preview skips the next row " + rowHeight);
            // Row near the bottom: above, ending before the previous row.
            var above = OpenCodeProcessedPreviewPlacement.Vertical(800, rowHeight, 200, 900, margin, gap);
            Check(above.Y + above.Height <= 800 - tallestRow && above.Y >= margin, "above preview skips the previous row " + rowHeight);
            var band = new OpenCodeDetailBand(100, 700);
            var capacity = OpenCodeDetailPopupLayout.PreviewPlacement(400, 100, rowHeight, 400, 200, band, 900);
            Check(capacity.Y >= 100 + rowHeight + tallestRow && capacity.Y + capacity.Height <= 900 - margin, "capacity probe uses the same clearance " + rowHeight);
        }

        string Read(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var popup = Read("OpenCodeToolDetailPopup.cs");
        Check(popup.Contains("FollowTheme(anchor);", StringComparison.Ordinal) && popup.Contains("FollowTheme(null);", StringComparison.Ordinal),
            "popup follows the source theme on open and releases it on close");
        Check(popup.Contains("anchor.ActualThemeChanged += OnAnchorThemeChanged", StringComparison.Ordinal)
            && popup.Contains("old.ActualThemeChanged -= OnAnchorThemeChanged", StringComparison.Ordinal), "live theme changes reach an open popup");
        Check(popup.Contains("_previewPresenter.RequestedTheme = theme", StringComparison.Ordinal)
            && popup.Contains("_overlay.RequestedTheme = theme", StringComparison.Ordinal), "preview and large view (incl. smoke) both themed");
        var chip = Read("OpenCodeTimelineChipView.cs");
        Check(!chip.Contains("\"↳\"", StringComparison.Ordinal) && !chip.Contains("new FontFamily(\"Segoe UI\")", StringComparison.Ordinal),
            "completion notice no longer draws text glyphs");
        Check(chip.Contains("CompletionNoticeDoneGlyph = \"\\uE930\"", StringComparison.Ordinal)
            && chip.Contains("CompletionNoticeFailedGlyph = \"\\uE783\"", StringComparison.Ordinal), "completion notice uses Fluent completed/error glyphs");
        Check(Read("OpenCodeConversationView.cs").Contains("\"\\uE930\", completionNotice: subagentNotice", StringComparison.Ordinal),
            "done glyph matches the sibling async-completion rows");
    }

    private static void ToolNamingAndDetails()
    {
        var names = new Dictionary<string, string>
        {
            ["session_read"] = "读取会话", ["session_list"] = "会话列表", ["session_diff"] = "查看会话差异",
            ["pty_read"] = "读取终端输出", ["pty_write"] = "写入终端输入", ["pty_spawn"] = "启动终端",
            ["pty_kill"] = "结束终端", ["pty_list"] = "列出终端",
        };
        var completed = new List<OpenCodeToolRowModel>();
        foreach (var (id, name) in names)
        {
            var catalog = OpenCodeExecuteInnerCatalog.Entries[id];
            var glyph = id.StartsWith("session_", StringComparison.Ordinal) ? "\uE8F2" : OpenCodeStepRunPresentation.TerminalGlyph;
            Check(catalog.Name == name && catalog.Glyph == glyph, id + " catalog name/glyph");
            Check(OpenCodeToolRowPresentation.Classify(" " + id.ToUpperInvariant() + " ") == OpenCodeToolKind.Other, id + " normalized exact classification");
            Check(OpenCodeToolRowPresentation.DisplayName(" " + id.ToUpperInvariant() + " ") == catalog.Name, id + " normalized catalog display name");
            foreach (var status in new[] { "running", "completed", "error" })
            {
                var model = OpenCodeToolRowPresentation.Build(Part(id, status));
                Check(model.Kind == OpenCodeToolKind.Other && !OpenCodeToolRowPresentation.IsDiffKind(model.Kind), id + " non-file/non-diff kind");
                Check(model.Name == (status == "error" ? "失败 · " : "") + name, id + " top-level display name");
                Check(model.GlyphOverride == glyph && model.RawToolLabel == id, id + " glyph override and unchanged raw badge");
                Check(model.DiffFiles.Count == 0 && model.Additions is null && model.Deletions is null, id + " no file diff data");
                Check(OpenCodeToolDetailBuiltinData.Resolve(model) == OpenCodeBuiltinToolView.None, id + " generic detail routing");
                var detail = GenericProbe.Build(model);
                Check(detail.Children.Select(child => child.Title).Contains("输入")
                    && detail.Children.Select(child => child.Title).Contains("输出"), id + " actual generic builder retains input/output sections");
                var input = (FrameworkElement)detail.Children.First(child => child.Title == "输入").Value!;
                var output = (FrameworkElement)detail.Children.First(child => child.Title == "输出").Value!;
                Check(ReferenceEquals(input.Value, model.InputProperties) && Equals(output.Value, model.OutputText), id + " generic detail retains original payloads");
                Check(!model.Summary.Contains(" 行", StringComparison.Ordinal), id + " no file-read/write line summary");
                var activity = id.StartsWith("pty_", StringComparison.Ordinal) ? OpenCodeWorkingActivity.Shell
                    : id is "session_read" or "session_list" ? OpenCodeWorkingActivity.Read : OpenCodeWorkingActivity.Tool;
                Check(OpenCodeWorkingPhrases.Activity(model.ToolName) == activity, id + " working activity unchanged");
                if (status == "completed") completed.Add(model);
            }
        }

        var summary = OpenCodeStepRunPresentation.Build(completed, 0, 1_700_000_000_000, 1_700_000_008_000, false, 1_700_000_009_000);
        Check(summary.SuccessCount == 8 && summary.ErrorCount == 0 && summary.ActiveCount == 0 && !summary.IsActive, "step summary retains successful counts");
        Check(names.Values.All(name => summary.DetailText.Contains(name, StringComparison.Ordinal)), "step summary uses catalog names");
        Check(summary.HeaderText == "已处理 8 秒", "step summary duration unchanged");
        foreach (var (id, kind) in new[] { ("read", OpenCodeToolKind.Read), ("mcp_read", OpenCodeToolKind.Read),
            ("list", OpenCodeToolKind.List), ("mcp_list", OpenCodeToolKind.List), ("write", OpenCodeToolKind.Write),
            ("mcp_write", OpenCodeToolKind.Write), ("apply_patch", OpenCodeToolKind.Patch), ("ast_grep_search", OpenCodeToolKind.Other) })
            Check(OpenCodeToolRowPresentation.Classify(id) == kind, id + " existing exact/suffix classification preserved");
        Check(OpenCodeToolRowPresentation.Classify("unknown_session_read") == OpenCodeToolKind.Read, "only known exact ids override suffix fallback");
    }

    private static OpenCodeContentPart Part(string id, string status) => new()
    {
        Id = id + status, Type = "tool", Name = id,
        State = JsonSerializer.SerializeToElement(new
        {
            status,
            input = new { sessionID = "ses_test", id = "pty_test", data = "hello", path = "/not-a-file", content = "not a file write" },
            metadata = new { count = 2, files = new[] { new { file = "/not-a-file", patch = "@@ -1 +1 @@\n-old\n+new", additions = 1, deletions = 1 } } },
            content = new[] { new { type = "text", text = "1: transcript line\n2: terminal output" } },
            error = new { message = "test error" },
        }),
    };

    private static void HeaderAffordance(string repo)
    {
        foreach (var active in new[] { false, true })
        for (var flags = 0; flags < 16; flags++)
        {
            var probe = new HoverProbe
            {
                _summary = new("", "", 0, 0, active, null, false, ""),
                _pointerOver = (flags & 1) != 0, _focusVisible = (flags & 2) != 0,
                _headerPointerOver = (flags & 4) != 0, _headerFocusVisible = (flags & 8) != 0,
            };
            probe.Update();
            var highlighted = (flags & 12) != 0;
            Check(probe._headerSurface.BackgroundKey == (highlighted ? "SubtleFillColorSecondaryBrush" : "SubtleFillColorTransparentBrush"), "running/settled header-only fill " + flags);
            Check(probe._outline.BackgroundKey is null, "running/settled outline remains unfilled " + flags);
            Check(probe._chevron.Opacity == (highlighted ? 1 : 0), "running/settled chevron follows header only " + flags);
        }
        string Read(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var group = Read("OpenCodeToolGroupView.cs");
        Check(!group.Contains("Apply(_outline", StringComparison.Ordinal) && !group.Contains("_outline.BackgroundTransition", StringComparison.Ordinal), "no outline fill or transition remains");
        Check(group.Contains("_headerSurface.PointerEntered +=", StringComparison.Ordinal)
            && group.Contains("_headerSurface.PointerExited +=", StringComparison.Ordinal)
            && group.Contains("_headerSurface.PointerCanceled +=", StringComparison.Ordinal), "all pointer affordance events owned by header");
        Check(!group.Contains("_root.PointerEntered +=", StringComparison.Ordinal)
            && group.Contains("&& !IsItemsSource(e.OriginalSource as DependencyObject)", StringComparison.Ordinal), "child hover/focus never highlights group header");
        Check(group.Contains("_headerSurface.BackgroundTransition = enabled ?", StringComparison.Ordinal), "header hover transition retained");
        var detail = Read("OpenCodeToolDetailContent.cs");
        Check(detail.Contains("OpenCodeToolDetailBuiltins.Build(model, options) ?? BuildGeneric(model)", StringComparison.Ordinal), "default popup path uses generic builder when builtin resolver returns None");
        Check(Read("OpenCodeToolPartView.cs").Contains("model.GlyphOverride ?? KindGlyph(model.Kind)", StringComparison.Ordinal), "row consumes existing glyph override contract");
        Check(Read("OpenCodeHomeView.cs").Contains("OpenCodeHomeTone.Unread => \"\\uE8F2\"", StringComparison.Ordinal), "session glyph reused from home session list");
    }
}
