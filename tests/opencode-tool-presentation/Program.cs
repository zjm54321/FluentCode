using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;

internal static partial class Program
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
        Omo307ReportsAndControls();
        AgentMentionScopesAndValidator();
        AgentIdentityTable();
        SidebarPrimaryScope();
        SidebarModelNames();
        SessionToolViews();
        QuestionAndDuration();
        RedesignSourceContracts(args[0]);
        HeaderAffordance(args[0]);
        HomeSettingsLayouts(args[0]);
        PreviewThemeAndPlacement(args[0]);
        LargeViewFitsContent(args[0]);
        UnifiedRunningIndicator(args[0]);
        IndicatorCaptureSourceContracts(args[0]);
        Console.WriteLine($"PASS {_checks} focused tool naming, glyph, OMO reports/controls/mention scopes, generic-detail, session-tool parsing, question/duration formatting, working-activity, step-summary, home-settings layout, header-affordance, preview theme/placement and unified running-indicator checks.");
    }

    private const string ListOutput = "[\n  {\n    \"id\": \"ses_a1\",\n    \"title\": \"Fix login\",\n    \"directory\": \"C:\\\\src\\\\app\",\n    \"updated\": 1700000000000\n  },\n"
        + "  {\n    \"id\": \"ses_b2\",\n    \"title\": \"Explore auth\",\n    \"directory\": \"C:\\\\src\\\\app\",\n    \"parentID\": \"ses_a1\",\n    \"updated\": 1699990000000\n  }\n]\n\nPagination: {\"next\":\"cur_2\"}";

    private const string ReadOutput = "# Fix login\n\nSession: ses_a1\nDirectory: C:\\src\\app\n\nShowing the last 6 of 40 active-context messages.\n\n"
        + "## Turn 3\n\n### User\n\nWhy does **login** fail?\n\n### Assistant\n\nLooking.\n\n[Tool: read (completed)]\n\n[Tool: bash (error)] exit 1\n\nIt was the token.\n… [truncated]\n\n---\n\n"
        + "## Turn 4\n\n### Compaction summary\n\nEarlier work summarized.\n\n### Assistant\n\n[Tool: session_read (completed)]\n\n[Assistant error] rate limited\n\n### Shell\n\n[Tool: shell (completed)] Exit code: 2.\n\n"
        + "Pagination: {\"previous\":\"cur_1\"}";

    private static void SessionToolViews()
    {
        var list = OpenCodeSessionToolData.ParseList(ListOutput, "login");
        Check(list is { Sessions.Count: 2, HasMore: true, Notice: null, Search: "login" }, "session_list parses items and next-page cursor");
        Check(list!.Sessions[0] == new OpenCodeSessionListItem("ses_a1", "Fix login", "C:\\src\\app", false, 1700000000000)
            && list.Sessions[1].IsChild && list.SharedDirectory == "C:\\src\\app", "session_list item fields, child marker and shared directory");
        Check(OpenCodeSessionToolData.ParseList(ListOutput.Replace("{\"next\":\"cur_2\"}", "{}", StringComparison.Ordinal))?.HasMore == false, "no next cursor = complete list");
        var unavailable = OpenCodeSessionToolData.ParseList("[]\n\nSession list is unavailable: neither the background service nor in-process sessions could be read.");
        Check(unavailable is { Sessions.Count: 0, Notice: "暂时无法读取会话列表" }, "unavailable list becomes a localized notice");
        Check(OpenCodeSessionToolData.ParseList("not json") is null && OpenCodeSessionToolData.ParseList("{\"id\":1}") is null
            && OpenCodeSessionToolData.ParseList("[{\"title\":\"no id\"}]") is null && OpenCodeSessionToolData.ParseList(null) is null, "unknown list output falls back to generic");

        var read = OpenCodeSessionToolData.ParseRead(ReadOutput, "recent");
        Check(read is { Title: "Fix login", SessionId: "ses_a1", Directory: "C:\\src\\app", Scope: "最近 6 / 40 条消息", HasMore: false, Notice: null }, "session_read header, scope and pagination");
        Check(read!.Turns.Count == 2 && read.Turns[0].Number == 3 && read.Turns[1].Number == 4, "turn numbers kept");
        var first = read.Turns[0].Messages;
        Check(first.Count == 2 && first[0].Speaker == OpenCodeSessionSpeaker.User && first[0].Segments.Single().Text == "Why does **login** fail?", "user prompt kept as Markdown");
        var assistant = first[1].Segments;
        Check(assistant.Count == 4 && assistant[0].Text == "Looking." && assistant[1].Tool == new OpenCodeSessionToolCall("read", "completed", null)
            && assistant[2].Tool is { Failed: true, Detail: "exit 1", Name: "bash" }, "assistant text interleaved with tool badges and failure detail");
        Check(assistant[3].Text == "It was the token.\n\n*…（内容过长，已截断）*", "plugin truncation marker localized without altering text");
        var second = read.Turns[1].Messages;
        Check(second.Select(m => m.Speaker).SequenceEqual(new[] { OpenCodeSessionSpeaker.Compaction, OpenCodeSessionSpeaker.Assistant, OpenCodeSessionSpeaker.Shell }),
            "compaction, assistant and shell blocks recognised");
        Check(second[1].Segments[1].Error == "rate limited" && second[2].Segments.Single().Tool is { Name: "shell", Detail: "Exit code: 2." }, "assistant error and shell exit code");
        Check(OpenCodeSessionToolData.ParseRead(ReadOutput.Replace("{\"previous\":\"cur_1\"}", "{\"next\":\"cur_3\"}", StringComparison.Ordinal))!.HasMore, "next cursor = earlier messages exist");
        Check(OpenCodeSessionToolData.ParseRead("Session not found: ses_x") is { SessionId: "ses_x", Notice: "未找到这个会话", Turns.Count: 0 }, "not-found notice");
        Check(OpenCodeSessionToolData.ParseRead("# T\n\nSession: ses_e\nDirectory: unknown\n\nNo conversation messages in this session.") is { Title: "T", Directory: null, Notice: "这个会话还没有对话消息" },
            "empty session notice and unknown directory hidden");
        Check(OpenCodeSessionToolData.ParseRead("# T\n\nSession: ses_e\nDirectory: d\n\nSession messages are unavailable: neither x.")?.Notice == "暂时无法读取这个会话的消息", "unavailable messages notice");
        Check(OpenCodeSessionToolData.ParseRead("# T\n\nSession: s\nDirectory: d\n\nSomething new\n\n## Turn 1\n\n### User\n\nhi") is null
            && OpenCodeSessionToolData.ParseRead(ReadOutput, "diff") is null && OpenCodeSessionToolData.ParseRead("plain text") is null, "unknown read output and diff mode fall back to generic");
        Check(OpenCodeSessionToolData.ModeLabel("summary") == "概要" && OpenCodeSessionToolData.ModeLabel("diff") is null, "mode labels");

        var readRow = OpenCodeToolRowPresentation.Build(Tool("session_read", "completed", new { sessionID = "ses_a1", mode = "recent" }, ReadOutput, new { title = "Fix login (meta)" }));
        Check(readRow.Target == "Fix login (meta)" && readRow.Summary == "2 轮对话" && readRow.RawToolLabel.Length == 0, "session_read row: title target, turn summary, no raw badge");
        Check(OpenCodeToolDetailBuiltinData.Resolve(readRow) == OpenCodeBuiltinToolView.SessionRead, "session_read dedicated view");
        var titled = OpenCodeToolRowPresentation.Build(Tool("session_read", "completed", new { sessionID = "ses_a1" }, ReadOutput, new { }));
        Check(titled.Target == "Fix login", "session_read title from output when metadata lacks it");
        var running = OpenCodeToolRowPresentation.Build(Tool("session_read", "running", new { sessionID = "ses_a1" }, null, new { }));
        Check(running.Target == "ses_a1" && running.Summary.Length == 0, "running session_read shows the requested id only");
        var listRow = OpenCodeToolRowPresentation.Build(Tool("session_list", "completed", new { search = " login ", limit = 20 }, ListOutput, new { count = 2 }));
        Check(listRow.Target == "“login”" && listRow.Summary == "2 个会话（还有更多）" && listRow.RawToolLabel.Length == 0, "session_list row: search target and count summary");
        Check(OpenCodeToolRowPresentation.Build(Tool("session_list", "completed", new { }, "[]\n\nPagination: {}", new { })).Summary == "没有找到会话", "empty list summary");

        // Consistency: every catalogued non-file tool drops the raw id pill; unknown snake_case tools keep it.
        foreach (var id in new[] { "session_diff", "opencode.session_move", "opencode.session_rename" })
            Check(new OpenCodeToolRowModel { ToolName = id }.RawToolLabel.Length == 0, id + " has no raw-id badge");
        Check(new OpenCodeToolRowModel { ToolName = "my_custom_tool" }.RawToolLabel == "my_custom_tool", "uncatalogued snake_case tool keeps its raw id pill");

        var now = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        double At(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
        Check(OpenCodeSessionToolData.DescribeUpdated(At(now.AddSeconds(-20)), now) == "刚刚"
            && OpenCodeSessionToolData.DescribeUpdated(At(now.AddMinutes(-5)), now) == "5 分钟前"
            && OpenCodeSessionToolData.DescribeUpdated(At(now.AddHours(-3)), now) == "3 小时前"
            && OpenCodeSessionToolData.DescribeUpdated(At(new DateTimeOffset(2026, 11, 5, 14, 20, 0, TimeSpan.Zero)), now) == "昨天 14:20"
            && OpenCodeSessionToolData.DescribeUpdated(At(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero)), now) == "3月2日"
            && OpenCodeSessionToolData.DescribeUpdated(At(new DateTimeOffset(2025, 3, 2, 9, 0, 0, TimeSpan.Zero)), now) == "2025年3月2日"
            && OpenCodeSessionToolData.DescribeUpdated(5, now) is null && OpenCodeSessionToolData.DescribeUpdated(null, now) is null, "relative update times");
    }

    private static void QuestionAndDuration()
    {
        var recommended = new OpenCodeQuestionOption("Postgres (Recommended)", "Durable");
        Check(recommended.Recommended && recommended.DisplayLabel == "Postgres", "(Recommended) suffix becomes a tag");
        Check(new OpenCodeQuestionOption("SQLite（推荐）", null) is { Recommended: true, DisplayLabel: "SQLite" }, "full-width 推荐 suffix");
        Check(new OpenCodeQuestionOption("Plain", null) is { Recommended: false, DisplayLabel: "Plain" }
            && new OpenCodeQuestionOption("(Recommended)", null) is { Recommended: false, DisplayLabel: "(Recommended)" }, "plain and suffix-only labels unchanged");
        var detail = new OpenCodeQuestionDetail(null, "Which DB?", new[] { recommended, new OpenCodeQuestionOption("MySQL", null) }, false, new[] { "Postgres (Recommended)", "Something else" });
        Check(detail.IsSelected(recommended) && detail.CustomAnswers().SequenceEqual(new[] { "Something else" }), "selection still matches the raw offered label");

        var question = new
        {
            questions = new[] { new { header = "Storage", question = "Which DB?", options = new[] { new { label = "Postgres (Recommended)", description = "Durable" }, new { label = "MySQL", description = "Fine" } } } },
        };
        var answered = OpenCodeToolRowPresentation.Build(Tool("question", "completed", question, null, new { answers = new[] { new[] { "Postgres (Recommended)" } } },
            new OpenCodeToolTime { Created = 1_700_000_000_000, Completed = 1_700_000_000_000 + 36_118_000 }));
        Check(answered.Summary == "已回答：Postgres", "row summary drops the recommended suffix");
        Check(answered.DurationText == "10 小时 1 分", "long waits format as hours/minutes, not raw seconds");
        Check(OpenCodeToolDetailBuiltinData.Resolve(answered) == OpenCodeBuiltinToolView.Question, "question dedicated view");
        string Duration(double ms) => OpenCodeToolRowPresentation.Build(Tool("read", "completed", new { filePath = "a" }, "x", new { },
            new OpenCodeToolTime { Created = 1_700_000_000_000, Completed = 1_700_000_000_000 + ms })).DurationText;
        Check(Duration(450) == "450 毫秒" && Duration(2_500) == "2.5 秒" && Duration(12_300) == "12 秒" && Duration(125_000) == "2 分 5 秒", "short durations unchanged; minutes formatted");
    }

    private static void RedesignSourceContracts(string repo)
    {
        string Read(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var builtins = Read("OpenCodeToolDetailBuiltins.cs");
        Check(builtins.Contains("OpenCodeBuiltinToolView.SessionRead => SessionRead(model, options.Compact)", StringComparison.Ordinal)
            && builtins.Contains("OpenCodeBuiltinToolView.SessionList => SessionList(model, options.Compact)", StringComparison.Ordinal), "session views routed");
        Check(!builtins.Contains("new RadioButtons", StringComparison.Ordinal) && !builtins.Contains("Choice(", StringComparison.Ordinal)
            && builtins.Contains("option.DisplayLabel", StringComparison.Ordinal), "question options use read-only rows with display labels");
        var sessions = Read("OpenCodeToolDetailSessions.cs");
        Check(sessions.Contains("is not { } transcript) return null;", StringComparison.Ordinal)
            && sessions.Contains("is not { } list) return null;", StringComparison.Ordinal), "unparsed session output returns null → generic view");
        var reasoning = Read("OpenCodeReasoningRowView.cs");
        Check(reasoning.Contains("compact: !OpenCodeToolDetailContent.IsFullDetailBuild", StringComparison.Ordinal), "pinned reasoning view shows the full text");
    }

    private static OpenCodeContentPart Tool(string name, string status, object input, string? output, object metadata, OpenCodeToolTime? time = null) => new()
    {
        Id = name + status + Guid.NewGuid().ToString("N"), Type = "tool", Name = name, Time = time,
        State = JsonSerializer.SerializeToElement(output is null
            ? (object)new { status, input, metadata }
            : new { status, input, metadata, content = new[] { new { type = "text", text = output } } }),
    };

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

    private static void UnifiedRunningIndicator(string repo)
    {
        // Look: the system default ProgressRing — the sidebar subagent list's 14px ring — centred in the 16px status column.
        Check(OpenCodeWorkingMotion.RingSize == 14 && OpenCodeWorkingMotion.StatusCellSize == 16, "ring = default 14px ring (as in the sidebar), centred in the 16px status column");
        // Rhythm: one absolute ring phase with the default ring's own 2 s cycle; rings joining at any time read the same value.
        Check(OpenCodeWorkingClock.RingPeriod == TimeSpan.FromSeconds(2), "ring cycle = the default indeterminate ProgressRing's 2 s");
        var ticks = 500L;
        var clock = new OpenCodeWorkingClock(() => ticks, 1000, OpenCodeWorkingClock.DefaultPeriod);
        var early = clock.RingPhase;
        ticks = 2500;
        Check(early == 0.25 && clock.RingPhase == 0.25 && clock.RingPhaseAt(4000) == 0 && clock.RingPhaseAt(-500) == 0.75,
            "ring phase is absolute: one cycle later it is the same, never restarted from zero");
        Check(clock.PhaseAt(900) == 0.5, "the 1.8 s shimmer/gear phase is unchanged");
        Check(OpenCodeWorkingMotion.RingProgressExpression == "clock.RingPhase", "every ring's playback progress reads the one shared phase");
        double still = OpenCodeWorkingMotion.RingStaticProgress;
        Check(still > 0.25 && still < 0.5, "reduced motion stops on a long-arc frame, never the empty first frame");
        Check(OpenCodeWorkingMotion.RingAccessibleName == "进行中", "rings carry an accessible name");

        string Read(string name) => File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode", name));
        var sync = Read("OpenCodeProgressRingSync.cs");
        Check(sync.Contains("new ProgressRing { IsActive = true, Width = size, Height = size, MinWidth = 0, MinHeight = 0 }", StringComparison.Ordinal)
            && !sync.Contains("ControlTemplate", StringComparison.Ordinal) && !sync.Contains("Template =", StringComparison.Ordinal)
            && !sync.Contains("IndeterminateSource", StringComparison.Ordinal),
            "the native default ring: no custom template or animation source");
        Check(sync.Contains("AnimatedVisualPlayer.IsPlayingProperty", StringComparison.Ordinal) && sync.Contains("player.Pause();", StringComparison.Ordinal)
            && sync.Contains("player.ProgressObject.TryGetAnimationController(ProgressProperty)", StringComparison.Ordinal)
            && sync.Contains("controller.StartAnimation(ProgressProperty, progress);", StringComparison.Ordinal)
            && sync.Contains("OpenCodeWorkingClockComposition.PhaseSet(compositor)", StringComparison.Ordinal),
            "each native play is paused and its progress bound to the shared phase");
        Check(sync.Contains("controller.Progress = (float)OpenCodeWorkingMotion.RingStaticProgress;", StringComparison.Ordinal)
            && sync.Contains("clock.Join(this)", StringComparison.Ordinal) && sync.Contains("clock.Leave(this);", StringComparison.Ordinal)
            && sync.Contains("_loaded && _ring.IsActive && _ring.IsIndeterminate", StringComparison.Ordinal),
            "joins the clock only while loaded and active; reduced motion leaves a still arc");
        Check(sync.Contains("AutomationProperties.SetName(ring, accessibleName);", StringComparison.Ordinal), "every synced ring is named");
        var composition = Read("OpenCodeWorkingClockComposition.cs");
        Check(composition.Contains("Loop(compositor, set, RingPhaseProperty, OpenCodeWorkingClock.RingPeriod, Clock.RingPhase);", StringComparison.Ordinal)
            && composition.Contains("set.StopAnimation(RingPhaseProperty);", StringComparison.Ordinal),
            "one RingPhase per compositor, aligned to the absolute clock and stopped with it");

        var cell = Read("OpenCodeTimelineStatusCell.cs");
        Check(cell.Contains("_ring = OpenCodeProgressRingSync.Create();", StringComparison.Ordinal) && cell.Contains("_cell.Children.Remove(ring);", StringComparison.Ordinal)
            && !cell.Contains("ArcSegment", StringComparison.Ordinal) && !cell.Contains("RotationAngleInDegrees", StringComparison.Ordinal),
            "status cell hosts the shared default ring only while running (no custom arc)");
        Check(cell.Contains("internal static readonly Thickness CellMargin = new(0, 0, OpenCodeTimelineRowMetrics.Padding, 0);", StringComparison.Ordinal)
            && cell.Contains("var size = OpenCodeWorkingMotion.StatusCellSize;", StringComparison.Ordinal) && cell.Contains("HorizontalAlignment = HorizontalAlignment.Right", StringComparison.Ordinal),
            "one fixed 16px cell, 8px from the row's right edge");
        Check(cell.Contains("OpenCodeToolTheme.Ring(\"SystemColorHighlightTextColorBrush\")", StringComparison.Ordinal)
            && cell.Contains("ring.ClearValue(FrameworkElement.StyleProperty);", StringComparison.Ordinal)
            && !cell.Contains("OpenCodeToolTheme.Caution", StringComparison.Ordinal) && !cell.Contains("IsBackground", StringComparison.Ordinal),
            "default accent for foreground and background work; high-contrast highlight text on the highlighted running row");
        var tree = Read("OpenCodeAgentTreeView.cs");
        Check(tree.Contains("OpenCodeProgressRingSync.Create(accessibleName: statusText)", StringComparison.Ordinal) && !tree.Contains("new ProgressRing", StringComparison.Ordinal),
            "the sidebar subagent ring is the same synced ring");
        var builtins = Read("OpenCodeToolDetailBuiltins.cs");
        Check(!builtins.Contains("new ProgressRing", StringComparison.Ordinal) && builtins.Split("OpenCodeProgressRingSync.Create(12)").Length - 1 == 3
            && Read("OpenCodeToolDetailSurface.cs").Contains("OpenCodeProgressRingSync.Create(16)", StringComparison.Ordinal),
            "detail rings for the same running work share the phase");
        foreach (var name in new[] { "OpenCodeToolPartView.cs", "OpenCodeReasoningRowView.cs", "OpenCodeTimelineChipView.cs" })
        {
            var row = Read(name);
            Check(!row.Contains("ProgressRing", StringComparison.Ordinal) && !row.Contains("_progress", StringComparison.Ordinal), name + " has no private ring");
            Check(row.Contains("Grid.SetColumn(_status.Element, 3);", StringComparison.Ordinal) && row.Contains(".SetRunning(", StringComparison.Ordinal)
                && row.Contains("_status.Dispose();", StringComparison.Ordinal), name + " hosts the shared status cell in root column 3");
            var root = row[row.IndexOf("_root = new Grid", StringComparison.Ordinal)..];
            Check(root.Contains("ColumnSpacing = 2", StringComparison.Ordinal)
                && root.Split("_root.ColumnDefinitions.Add(", StringSplitOptions.None).Length - 1 == 4, name + " uses the same 4-column root (bar | row | actions | status)");
        }

        // Background jobs: the call's own time is the launch, not the job; never show it beside 后台运行中 / 后台已完成.
        var pending = OpenCodeToolRowPresentation.Build(Tool("bash", "completed", new { command = "sleep 60", background = true }, "started", new { status = "running" },
            new OpenCodeToolTime { Created = 1_700_000_000_000, Completed = 1_700_000_000_009 }));
        Check(pending is { IsBackground: true, Status: OpenCodeToolRowStatus.Running, DurationText: "" } && pending.Summary == "后台运行中", "running background row has no launch duration");
        var foreground = OpenCodeToolRowPresentation.Build(Tool("bash", "completed", new { command = "ls" }, "a", new { },
            new OpenCodeToolTime { Created = 1_700_000_000_000, Completed = 1_700_000_000_009 }));
        Check(foreground is { IsBackground: false, DurationText: "9 毫秒" }, "foreground rows keep their duration");
    }

    private static void IndicatorCaptureSourceContracts(string repo)
    {
        string Read(string name) => File.ReadAllText(Path.Combine(repo, name));
        var page = Read("src/FluentCode/Pages/OpenCodePage.IndicatorCapture.cs");
        Check(page.Contains("Environment.GetEnvironmentVariable(\"FC_INDICATOR_CAPTURE_TEST\") == \"1\"", StringComparison.Ordinal)
            && page.Contains("Environment.GetEnvironmentVariable(\"FC_SHELL_CAPTURE_TEST\") == \"1\"", StringComparison.Ordinal)
            && page.Contains("FC_STARTUP_SMOKE_ROOT", StringComparison.Ordinal), "indicator fixture requires explicit indicator/shell/disposable-profile gates");
        Check(page.Contains("if (_source is not null || _client is not null)", StringComparison.Ordinal)
            && page.Contains("CreateSessionTab(id);", StringComparison.Ordinal)
            && page.Contains("ApplySessionTabState(entry);", StringComparison.Ordinal)
            && Read("src/FluentCode/Pages/OpenCodePage.xaml.cs").Contains("Composer.Visibility = ComposerCell.Visibility = Visibility.Collapsed;", StringComparison.Ordinal),
            "offline fixture uses real tab/status paths, rejects a backend and hides the unrelated composer before first measure");
        Check(new[] { "working", "background", "retry", "failed", "interrupted", "waiting", "unread", "idle", "long" }
            .All(state => page.Contains("ses_capture_" + state, StringComparison.Ordinal)), "fixture covers all eight statuses plus a long working title");
        var shell = Read("src/FluentCode/MainWindow.ShellCapture.cs");
        var window = Read("src/FluentCode/MainWindow.IndicatorCapture.cs");
        Check(shell.Contains("if (Pages.OpenCodePage.IndicatorCaptureEnabled)", StringComparison.Ordinal)
            && window.Contains("new[] { 1920, 1024 }", StringComparison.Ordinal)
            && window.Contains("new[] { AppTheme.Light, AppTheme.Dark }", StringComparison.Ordinal)
            && window.Contains("await Screenshot(RootElement", StringComparison.Ordinal)
            && Read("src/FluentCode/App.xaml.cs").Contains("Environment.GetEnvironmentVariable(\"FC_INDICATOR_CAPTURE_TEST\") != \"1\"", StringComparison.Ordinal),
            "capture renders both native widths/themes with the existing RTB helper and skips backend ownership");
        var runner = Read("tests/opencode-indicator-capture/capture.ps1");
        Check(runner.Contains("Get-Process -Name RichEditPrototype", StringComparison.Ordinal) && runner.Contains("quser", StringComparison.Ordinal)
            && runner.Contains("$process.Kill();", StringComparison.Ordinal) && runner.Contains("PID=$($process.Id)", StringComparison.Ordinal)
            && !runner.Contains("SendInput", StringComparison.Ordinal) && !runner.Contains("SendKeys", StringComparison.Ordinal)
            && !runner.Contains("BitBlt", StringComparison.Ordinal), "capture preflights the active desktop, verifies its PID and never sends desktop input or grabs the screen");
        var locked = Read("tests/opencode-indicator-capture/capture.sh");
        Check(locked.Contains("mkdir \"$lock\"", StringComparison.Ordinal) && locked.Contains("SECONDS + 1200", StringComparison.Ordinal)
            && locked.Contains("sleep 15", StringComparison.Ordinal) && locked.Contains("$lock/owner", StringComparison.Ordinal)
            && locked.Contains("rmdir -- \"$lock\"", StringComparison.Ordinal), "shared capture lock has bounded 15s retries, owner and exact cleanup");
    }

    private static void LargeViewFitsContent(string repo)
    {
        // The 80% rectangle is only the cap; short content (reasoning, to do, text) shrinks to fit.
        var cap = OpenCodeDetailPopupLayout.LargeView(1600, 1000);
        Check(cap.Width == 1280 && cap.Height == 800, "large view cap stays 80% of the window");
        Check(OpenCodeDetailPopupLayout.LargeViewWidthRange(cap, fillWidth: false) == (560, 1280), "content-fit width range is 560..cap");
        Check(OpenCodeDetailPopupLayout.LargeViewHeightRange(cap) == (160, 800), "content-fit height range is 160..cap");
        Check(OpenCodeDetailPopupLayout.LargeFitWidth(808, cap, false) == 808, "reasoning column (760 + 48 inset) keeps its own width");
        Check(OpenCodeDetailPopupLayout.LargeFitWidth(120, cap, false) == 560, "very short content still reads as a card");
        Check(OpenCodeDetailPopupLayout.LargeFitWidth(5000, cap, false) == 1280 && OpenCodeDetailPopupLayout.LargeFitWidth(double.PositiveInfinity, cap, false) == 1280,
            "wide content never exceeds the cap");
        Check(OpenCodeDetailPopupLayout.LargeFitWidth(120, cap, true) == 1280 && OpenCodeDetailPopupLayout.LargeViewWidthRange(cap, true) == (1280, 1280),
            "fill-width views keep the full cap");
        var small = OpenCodeDetailPopupLayout.LargeView(420, 300);
        Check(OpenCodeDetailPopupLayout.LargeViewWidthRange(small, false).Min == small.Width
            && OpenCodeDetailPopupLayout.LargeViewHeightRange(small).Min == 160, "tiny windows clamp the minimum to the cap");
        foreach (var kind in new[] { OpenCodeToolKind.Edit, OpenCodeToolKind.MultiEdit, OpenCodeToolKind.Patch, OpenCodeToolKind.Write, OpenCodeToolKind.Browser })
            Check(OpenCodeDetailPopupLayout.LargeViewFillsWidth(kind, hasImages: false, fileView: false), "wide view fills the cap: " + kind);
        Check(!OpenCodeDetailPopupLayout.LargeViewFillsWidth(OpenCodeToolKind.Write, false, fileView: true), "write file preview fits its content");
        foreach (var kind in new[] { OpenCodeToolKind.Todo, OpenCodeToolKind.Read, OpenCodeToolKind.Shell, OpenCodeToolKind.Grep, OpenCodeToolKind.WebFetch, OpenCodeToolKind.Other })
            Check(!OpenCodeDetailPopupLayout.LargeViewFillsWidth(kind, false, false), "content-fit view: " + kind);
        Check(OpenCodeDetailPopupLayout.LargeViewFillsWidth(OpenCodeToolKind.Other, hasImages: true, false), "image results fill the cap");

        var popup = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeToolDetailPopup.cs"));
        Check(popup.Contains("_dialogPresenter.Width = fill ? maxWidth : double.NaN;", StringComparison.Ordinal)
            && popup.Contains("_dialogPresenter.LayoutUpdated += (_, _) => FreezeLargeWidth();", StringComparison.Ordinal)
            && popup.Contains("FitLarge(root.Size, growOnly: true);", StringComparison.Ordinal)
            && !popup.Contains("MinHeight = Math.Min(320", StringComparison.Ordinal),
            "large view sizes to native content once, freezes, and only grows on refresh");
    }

    private static void ToolNamingAndDetails()
    {
        var names = new Dictionary<string, string>
        {
            ["session_read"] = "读取会话", ["session_list"] = "会话列表", ["session_diff"] = "查看会话差异",
            ["pty_read"] = "读取终端输出", ["pty_write"] = "写入终端输入", ["pty_spawn"] = "启动终端",
            ["pty_kill"] = "结束终端", ["pty_list"] = "列出终端", ["pty_resize"] = "调整终端大小",
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
                Check(model.GlyphOverride == glyph && model.RawToolLabel.Length == 0, id + " glyph override and no raw-id badge");
                Check(model.DiffFiles.Count == 0 && model.Additions is null && model.Deletions is null, id + " no file diff data");
                Check(OpenCodeToolDetailBuiltinData.Resolve(model) == id switch
                {
                    "session_read" => OpenCodeBuiltinToolView.SessionRead,
                    "session_list" => OpenCodeBuiltinToolView.SessionList,
                    _ => OpenCodeBuiltinToolView.None,
                }, id + " detail routing");
                if (id == "session_read") Check(model.Target == "ses_test", id + " unparsed output keeps the requested session id as target");
                if (id == "session_list") Check(model.Target.Length == 0, id + " list without search has no target");
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
        Check(summary.SuccessCount == 9 && summary.ErrorCount == 0 && summary.ActiveCount == 0 && !summary.IsActive, "step summary retains successful counts");
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
        var home = Read("OpenCodeHomeView.cs");
        Check(home.Contains("indicator.Visibility = row.ShowStatusIndicator", StringComparison.Ordinal)
            && home.Contains("unread.Visibility = row.ShowUnreadIndicator", StringComparison.Ordinal), "home consumes mutually exclusive indicator policy");
    }

    private static void HomeSettingsLayouts(string repo)
    {
        var home = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeHomeView.cs"));
        var page = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Home.cs"));
        var settings = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.SettingsTab.cs"));
        Check(home.Contains("Grid.SetColumn(_settingsHost, 2)", StringComparison.Ordinal)
            && home.Contains("searchRow.Children.Add(_settingsHost)", StringComparison.Ordinal)
            && home.Contains("SetSettingsButtons(Button narrowButton, Button wideButton)", StringComparison.Ordinal), "narrow settings stays beside New in the search row");
        Check(home.Contains("ContentControl _settingsHost = new() { IsTabStop = false", StringComparison.Ordinal)
            && home.IndexOf("searchRow.Children.Add(create)", StringComparison.Ordinal) < home.IndexOf("searchRow.Children.Add(_settingsHost)", StringComparison.Ordinal),
            "narrow settings follows New in keyboard order without a focusable host");
        Check(home.Contains("var searchRow = new Grid();", StringComparison.Ordinal)
            && home.Contains("Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0)", StringComparison.Ordinal)
            && home.Contains("MinHeight = 36, Margin = new Thickness(10, 0, 0, 0)", StringComparison.Ordinal), "action gaps stay 10px without an empty-column gap in wide mode");
        Check(page.Contains("HomePageHost.Children.Add(wideSettingsButton)", StringComparison.Ordinal)
            && page.Contains("_homeView.SetSettingsButtons(CreateHomeSettingsButton(), wideSettingsButton)", StringComparison.Ordinal), "wide settings overlay and narrow icon registered together");
        Check(home.Contains("SizeChanged += (_, _) => ApplyLayout()", StringComparison.Ordinal)
            && home.Contains("Loaded += (_, _) => { ApplyLayout();", StringComparison.Ordinal), "home settings layout follows load and live resize");
        var narrowStart = settings.IndexOf("private Button CreateHomeSettingsButton()", StringComparison.Ordinal);
        var wideStart = settings.IndexOf("private Button CreateWideHomeSettingsButton()", StringComparison.Ordinal);
        Check(narrowStart >= 0 && wideStart > narrowStart, "separate narrow and wide settings factories");
        var narrowButton = settings[narrowStart..wideStart];
        var wideButton = settings[wideStart..];
        Check(narrowButton.Contains("MinWidth = 36", StringComparison.Ordinal) && narrowButton.Contains("MinHeight = 36", StringComparison.Ordinal)
            && narrowButton.Contains("Content = new FontIcon { Glyph = \"\\uE713\", FontSize = 16 }", StringComparison.Ordinal)
            && !narrowButton.Contains("new TextBlock", StringComparison.Ordinal), "narrow settings remains icon-only with 36px touch target");
        Check(wideButton.Contains("HorizontalAlignment = HorizontalAlignment.Right", StringComparison.Ordinal)
            && wideButton.Contains("VerticalAlignment = VerticalAlignment.Bottom", StringComparison.Ordinal)
            && wideButton.Contains("Margin = new Thickness(0, 0, 24, 24)", StringComparison.Ordinal)
            && wideButton.Contains("Padding = new Thickness(12, 6, 12, 7)", StringComparison.Ordinal), "wide settings restores original bottom-right alignment, margins and padding");
        Check(wideButton.Contains("Orientation = Orientation.Horizontal", StringComparison.Ordinal) && wideButton.Contains("Spacing = 8", StringComparison.Ordinal)
            && wideButton.Contains("new FontIcon { Glyph = \"\\uE713\", FontSize = 14, VerticalAlignment = VerticalAlignment.Center }", StringComparison.Ordinal)
            && wideButton.Contains("new TextBlock { Text = \"设置\", VerticalAlignment = VerticalAlignment.Center }", StringComparison.Ordinal), "wide settings restores original 14px icon and text");
        Check(narrowButton.Contains("SetAutomationId(button, \"FluentCodeHomeSettingsButton\")", StringComparison.Ordinal)
            && wideButton.Contains("SetAutomationId(button, \"FluentCodeHomeWideSettingsButton\")", StringComparison.Ordinal), "settings buttons have distinct automation identities");
        Check(narrowButton.Contains("SetName(button, \"设置\")", StringComparison.Ordinal) && wideButton.Contains("SetName(button, \"设置\")", StringComparison.Ordinal)
            && narrowButton.Contains("SetToolTip(button, \"设置\")", StringComparison.Ordinal)
            && wideButton.Contains("SetToolTip(button, \"设置 (Ctrl+,)\")", StringComparison.Ordinal), "both settings buttons retain Narrator names and mode-specific tooltips");
        Check(narrowButton.Contains("button.Click += HomeSettingsButton_Click", StringComparison.Ordinal)
            && wideButton.Contains("button.Click += HomeSettingsButton_Click", StringComparison.Ordinal), "both settings buttons open the existing singleton settings tab");

        var probe = new HomeLayoutProbe();
        foreach (var width in new[] { 0d, HomeLayoutProbe.WideThreshold - 1, HomeLayoutProbe.WideThreshold, HomeLayoutProbe.WideThreshold + 1, 600d, 1600d })
        {
            probe.Resize(width);
            var narrow = width > 0 && width < HomeLayoutProbe.WideThreshold;
            Check(probe._narrow == narrow && probe.NarrowSettingsButton.Visibility == (narrow ? Visibility.Visible : Visibility.Collapsed)
                && probe._wideSettingsButton.Visibility == (narrow ? Visibility.Collapsed : Visibility.Visible), "settings follows actual home threshold at width " + width);
            Check((probe.NarrowSettingsButton.Visibility == Visibility.Visible ? 1 : 0)
                + (probe._wideSettingsButton.Visibility == Visibility.Visible ? 1 : 0) == 1, "exactly one settings button visible at width " + width);
            Check(probe._settingsHost.Visibility == (narrow ? Visibility.Visible : Visibility.Collapsed)
                && probe.NarrowSettingsButton.IsTabStop == narrow && probe._wideSettingsButton.IsTabStop == !narrow, "hidden settings and host leave keyboard/layout order at width " + width);
        }
        probe._wideSettingsButton.Focus(FocusState.Keyboard);
        probe.Resize(HomeLayoutProbe.WideThreshold - 1);
        Check(probe.NarrowSettingsButton.FocusState == FocusState.Keyboard && probe._wideSettingsButton.FocusState == FocusState.Unfocused,
            "wide-to-narrow resize preserves settings keyboard focus");
        var narrowFocusCalls = probe.NarrowSettingsButton.FocusCalls;
        probe.Resize(600);
        Check(probe.NarrowSettingsButton.FocusCalls == narrowFocusCalls, "same-mode resize does not re-focus settings");
        probe.Resize(HomeLayoutProbe.WideThreshold);
        Check(probe._wideSettingsButton.FocusState == FocusState.Keyboard && probe.NarrowSettingsButton.FocusState == FocusState.Unfocused,
            "narrow-to-wide resize preserves settings keyboard focus");
        probe._wideSettingsButton.FocusState = FocusState.Unfocused;
        probe.Resize(HomeLayoutProbe.WideThreshold - 1);
        Check(probe.NarrowSettingsButton.FocusCalls == narrowFocusCalls && probe.NarrowSettingsButton.FocusState == FocusState.Unfocused,
            "resizing does not steal focus from another home control");
    }
}
