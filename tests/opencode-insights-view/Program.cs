// Source-linked regression for the session-insights view's persistent Billion controls.
// Subject under test: the REAL src/AIFrontend/Pages/OpenCode/OpenCodeSessionInsightsView.cs (whole file, unmodified,
// selected by run.ps1 -ViewSource) rendered through its real Render(...) with real snapshot records from
// OpenCodeSessionInsights.cs. Only WinUI and OpenCodeInteractionUi are synthetic (see WinUiOwnershipShim.cs).
// The eight ownership cases use no controller/transport. Origin cases (PageOriginTests.cs) additionally link the real
// controller, Billion client, OpenCodePage.SessionInsights.cs and extracted Page members (see run.ps1).
// Origin-era adaptation of these eight cases: layout now has the persistent origin panel at index 1 (confirm 2,
// button 3); events carry the rendered OpenCodeBillionContext, so event snapshots include a synthetic context.
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Billion;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static int Main()
    {
        // Hidden Parent (models the never-opened flyout hypothesis) — the runtime failure path.
        Run("hidden: construct-render then reset-render (runtime sequence)", false, view =>
        {
            Draw(view, Empty());
            Draw(view, Empty());
        });
        Run("hidden: idle -> loading -> loaded -> loaded (confirmed)", false, view =>
        {
            Draw(view, Empty());
            Draw(view, Billion(OpenCodeBillionPhase.Loading, confirmed: true));
            Draw(view, Billion(OpenCodeBillionPhase.Loaded, confirmed: true));
            Draw(view, Billion(OpenCodeBillionPhase.Loaded, confirmed: true));
        });
        Run("hidden: available -> unavailable -> available", false, view =>
        {
            Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: true));
            Draw(view, Unavailable());
            Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: true));
        });
        Run("hidden: partial construction (button Add fails) is recovered by next render", false, view =>
            PartialFault(view, Field<Button>(view, "_billionButton")));
        Run("hidden: partial construction (checkbox Add fails) is recovered by next render", false, view =>
            PartialFault(view, Field<CheckBox>(view, "_billionConfirm")));
        Run("hidden: renders raise no confirmation/refresh/read events; user toggle still raises one", false, Events);

        // Visible Parent — control: shows the dependency of the old Detach on Parent visibility.
        Run("visible: repeated renders incl. loading/loaded", true, view =>
        {
            Draw(view, Empty());
            Draw(view, Billion(OpenCodeBillionPhase.Loading, confirmed: true));
            Draw(view, Billion(OpenCodeBillionPhase.Loaded, confirmed: true));
        });
        Run("visible: renders raise no confirmation/refresh/read events; user toggle still raises one", true, Events);
        Run("sections: SelectorBar switch only toggles page visibility (no rebuild, Billion owner kept, no events)", true, Sections);
        Run("layout D: no duplicate Agents section or refresh toolbar on the right (subagents live in the left tree)", true, NoRightAgents);
        Run("to do: native checkbox states, TD1 strike semantics, hide empty/unknown", true, TodoList);
        Run("to do: real controller config, local derivation and stale-session/epoch fences", true, _ => TodoControllerTests.Run());
        Run("context ring: compact shared grid, thresholds, hide unknown, Billion in-place refresh", true, ContextRing);
        Run("context grid: Fluent icon order, selected child usage, tree totals and hidden unknowns", true, LayeredUsage);
        Run("connection flyout: concise rows, built-ins silently hidden, clickable MCP/plugin detail + MCP control actions", true, ConnectionFlyout);

        ShimTree.ParentVisible = true;
        foreach (var (name, body) in PageOriginTests.Cases())
        {
            ShimTree.FailNextAddOf = null;
            ShimTree.Focused = null;
            ShimTree.ForbiddenMutations = 0;
            Microsoft.UI.Dispatching.DispatcherQueue.Shared.Reset();
            try
            {
                body();
                _passed++;
                Console.WriteLine("PASS " + name + $" ({PageOriginTests.Assertions} origin assertions so far)");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var (name, body) in ContextPageTests.Cases())
        {
            Microsoft.UI.Dispatching.DispatcherQueue.Shared.Reset();
            ShimTree.FailNextAddOf = null;
            try { body(); _passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
        }

        foreach (var (name, body) in AutomaticInsightsTests.Cases())
        {
            Microsoft.UI.Dispatching.DispatcherQueue.Shared.Reset();
            try { body(); _passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
        }

        Console.WriteLine($"RESULT passed={_passed} failed={_failed} total={_passed + _failed}");
        return _failed;
    }

    private static void Run(string name, bool parentVisible, Action<OpenCodeSessionInsightsView> body)
    {
        ShimTree.ParentVisible = parentVisible;
        ShimTree.FailNextAddOf = null;
        try
        {
            body(new OpenCodeSessionInsightsView());
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            var hr = ex is System.Runtime.InteropServices.COMException com ? $" HResult=0x{com.HResult:X8}" : "";
            Console.WriteLine($"FAIL {name}: {ex.GetType().Name}{hr}: {ex.Message}");
        }
    }

    private static void PartialFault(OpenCodeSessionInsightsView view, UIElement victim)
    {
        Draw(view, Empty());
        ShimTree.FailNextAddOf = victim;
        var injected = false;
        try
        {
            view.Render(Billion(OpenCodeBillionPhase.Loaded, confirmed: true));
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("injected", StringComparison.Ordinal))
        {
            injected = true;
        }

        Check(injected, "fault was injected during BillionSection");
        Check(!(bool)typeof(OpenCodeSessionInsightsView).GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!,
            "_rendering guard reset after failure");
        Draw(view, Billion(OpenCodeBillionPhase.Loaded, confirmed: true));
        Draw(view, Unavailable());
        Draw(view, Empty());
    }

    private static void Events(OpenCodeSessionInsightsView view)
    {
        var confirmations = new List<bool>();
        var refresh = 0; // layout D: the view has no refresh entry; reads are automatic (page-owned)
        var reads = 0;
        var edits = 0;
        view.BillionConfirmationChanged += (_, request) =>
        {
            Check(ReferenceEquals(request.Context, SyntheticContext), "confirmation carries the rendered context");
            confirmations.Add(request.Confirmed);
        };
        view.BillionReadRequested += (_, context) =>
        {
            Check(ReferenceEquals(context, SyntheticContext), "read carries the rendered context");
            reads++;
        };
        view.BillionOriginEdited += (_, _) => edits++;

        Draw(view, Empty());
        Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: true));   // IsChecked false -> true inside Render
        Draw(view, Billion(OpenCodeBillionPhase.Loaded, confirmed: true));
        Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: false));  // true -> false inside Render
        Check(confirmations.Count == 0 && refresh == 0 && reads == 0, "Render raised no page events (no implicit read)");

        var confirm = Field<CheckBox>(view, "_billionConfirm");
        confirm.IsChecked = true;                                          // user toggle outside Render
        Check(confirmations.Count == 1 && confirmations[0], "user check raises exactly one confirmation(true)");
        Field<Button>(view, "_billionButton").SimulateClick();
        Check(reads == 1 && refresh == 0, "Billion read only on explicit click");
        Check(edits == 0, "programmatic origin text writes raised no edit event");
    }

    private static void Sections(OpenCodeSessionInsightsView view)
    {
        Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: true));
        var bar = Field<SelectorBar>(view, "_sections");
        var usage = Field<Border>(view, "_usageHost");
        var pages = Field<StackPanel[]>(view, "_pages");
        // Wave 3 F10c: 任务 and Agents are merged into 概况 (ring + 任务 + Agents); only Billion (consent workflow) stays separate.
        Check(AutomationProperties.GetAutomationId(bar) == "OpenCodeInsightsSections" && bar.Items.Count == 2
            && bar.Items.Select(item => item.Text).SequenceEqual(new[] { "概况", "Billion" })
            && bar.Items.Select(item => AutomationProperties.GetAutomationId(item)).SequenceEqual(new[]
                { "OpenCodeInsightsSectionItem.Overview", "OpenCodeInsightsSectionItem.Billion" }),
            "two native sections");
        Check(ReferenceEquals(bar.SelectedItem, bar.Items[0]) && pages[0].Visibility == Visibility.Visible
            && pages.Skip(1).All(page => page.Visibility == Visibility.Collapsed), "概况 selected by default");
        Check(bar.Visibility == Visibility.Collapsed && usage.Visibility == Visibility.Visible,
            "layout D: section bar hidden on the overview card (Billion setup is reached from the ring flyout)");
        var confirm = Field<CheckBox>(view, "_billionConfirm");
        var owner = confirm.Owner;
        var adds = ShimTree.AddCalls;
        bar.SelectedItem = bar.Items[1];
        Check(pages[1].Visibility == Visibility.Visible && pages[0].Visibility == Visibility.Collapsed,
            "Billion page shown");
        Check(bar.Visibility == Visibility.Visible && usage.Visibility == Visibility.Collapsed, "Billion setup shows the bar to return to 概况");
        Check(ShimTree.AddCalls == adds && ReferenceEquals(confirm.Owner, owner), "switching sections adds nothing and never reparents");
        bar.SelectedItem = null;
        Check(ReferenceEquals(bar.SelectedItem, bar.Items[0]) && pages[0].Visibility == Visibility.Visible, "no-selection falls back to 概况");
        Check(bar.Visibility == Visibility.Collapsed, "back on 概况 the bar hides again");
        view.ShowBillionSection();
        Check(pages[1].Visibility == Visibility.Visible && bar.Visibility == Visibility.Visible, "ring flyout 去设置 path still opens the Billion page");
    }

    private static void NoRightAgents(OpenCodeSessionInsightsView view)
    {
        Draw(view, WithChildren(
            ("ses_c1", "explorer", true, "Search codebase"),
            ("ses_c2", "explorer", false, "Read config")));
        var overview = Field<StackPanel[]>(view, "_pages")[0];
        Check(overview.Children.Count == 0,
            "overview holds only to do below the usage block");
        var texts = AllText((UIElement)view.Content!).ToList();
        Check(!texts.Contains("Search codebase") && !texts.Contains("Read config") && !texts.Contains("会话概况"),
            "no subagent rows and no old 会话概况 heading on the right");
        var type = typeof(OpenCodeSessionInsightsView);
        Check(type.GetEvent("RefreshRequested", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is null
            && type.GetEvent("ChildSessionRequested", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is null
            && type.GetField("_refreshButton", BindingFlags.Instance | BindingFlags.NonPublic) is null
            && type.GetField("_agents", BindingFlags.Instance | BindingFlags.NonPublic) is null,
            "refresh toolbar and duplicate Agents section removed (not hidden)");
    }

    private static OpenCodeTodoSnapshot TodoSnapshot(OpenCodeTodoState state, bool complete, string reason, params OpenCodeTodoItem[] items)
        => new(new OpenCodeCapabilityScope("ep-view", 1, "/work", new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)),
            "ses_synthetic", state, ImmutableArray.Create(items), items.Length > 0 ? "msg_todo" : null, complete,
            new OpenCodeCapabilityState(OpenCodeCapabilityAvailability.Available, "ok"), reason);

    private static StackPanel DrawTodo(OpenCodeSessionInsightsView view, OpenCodeInsightState state, OpenCodeTodoSnapshot? todo, string note = "note")
    {
        Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: false) with
        {
            Todo = new OpenCodeInsightSection<OpenCodeTodoSnapshot>(state, todo, note),
        });
        return (StackPanel)((StackPanel)Field<StackPanel[]>(view, "_pages")[0].Children[0]).Children[1];
    }

    private static StackPanel TodoContent(StackPanel panel) => (StackPanel)panel.Children[1];

    private static string BadgeText(StackPanel panel)
    {
        var heading = panel.Children[0] is Button button ? (Grid)button.Content! : (Grid)panel.Children[0];
        return heading.Children.OfType<Border>().SingleOrDefault()?.Child is TextBlock text ? text.Text : "";
    }

    // A10: official v2.1.1 sidebar look — "to do" header, [✓][•][ ][-] glyph equivalents, >2 collapsible with remaining count.
    private static void TodoList(OpenCodeSessionInsightsView view)
    {
        var four = TodoSnapshot(OpenCodeTodoState.Listed, true, "latest",
            new OpenCodeTodoItem("pending", "pending"), new OpenCodeTodoItem("completed", "completed"),
            new OpenCodeTodoItem("working", "in_progress"), new OpenCodeTodoItem("cancelled", "cancelled"));
        var panel = DrawTodo(view, OpenCodeInsightState.Loaded, four);
        Check(AllText(panel).Contains("1 / 4"), "header counts completed only");
        var list = (StackPanel)panel.Children[1];
        var boxes = list.Children.Cast<CheckBox>().ToArray();
        Check(boxes.Select(box => box.IsChecked).SequenceEqual(new bool?[] { false, true, null, false }), "native pending/completed/in-progress/cancelled checkbox states");
        Check(boxes.All(box => box.IsThreeState && !box.IsHitTestVisible && !box.IsTabStop), "todos are view-only native checkboxes");
        var texts = boxes.Select(box => (TextBlock)box.Content!).ToArray();
        Check(texts[1].TextDecorations == Windows.UI.Text.TextDecorations.None
            && texts[3].TextDecorations == Windows.UI.Text.TextDecorations.Strikethrough, "TD1 completed is not struck, cancelled alone is struck");
        Check(texts.Select(text => text.Text).SequenceEqual(new[] { "pending", "completed", "working", "cancelled" }), "server order preserved");
        foreach (var state in new[] { OpenCodeTodoState.Empty, OpenCodeTodoState.Unknown, OpenCodeTodoState.ReadFailure, OpenCodeTodoState.Disabled })
        {
            Draw(view, Empty() with { Todo = new(OpenCodeInsightState.Loaded, TodoSnapshot(state, true, "hidden"), "hidden") });
            Check(Field<StackPanel[]>(view, "_pages")[0].Children.Count == 0, "empty/unknown/unsupported to do hides whole section and divider");
        }
        Draw(view, Empty());
        Check(Field<StackPanel[]>(view, "_pages")[0].Children.Count == 0, "not-loaded todo hidden");
    }

    private static IEnumerable<string?> FlyoutGroupIds(OpenCodeContextRing ring)
    {
        var scroll = (ScrollViewer)((Flyout)ring.Button.Flyout!).Content!;
        var groups = (StackPanel)scroll.Content!;
        return groups.Children.Select(child => AutomationProperties.GetAutomationId(child)).Where(id => id is not null);
    }

    private static OpenCodeSessionInsightsSnapshot WithContext(double tokens, long? limit, OpenCodeInsightState state = OpenCodeInsightState.Loaded)
    {
        var usage = new OpenCodeAssistantUsageReference("msg_ctx", 1, null,
            new OpenCodeUsageTokens(tokens - 30, 10, 5, 10, 5));
        var estimate = new OpenCodeContextEstimateResult("ses_synthetic", null, usage, limit, ImmutableArray<OpenCodeContextStaleReason>.Empty);
        return Billion(OpenCodeBillionPhase.Idle, confirmed: true) with
        {
            HistoryComplete = true,
            Context = new OpenCodeInsightSection<OpenCodeContextEstimateResult>(state, estimate, estimate.Label),
        };
    }

    private static void ContextRing(OpenCodeSessionInsightsView view)
    {
        var ring = Field<OpenCodeContextRing>(view, "_contextRing");
        Check(OpenCodeSessionInsightsView.Compact(0) == "0"
            && OpenCodeSessionInsightsView.Compact(999) == "999"
            && OpenCodeSessionInsightsView.Compact(82_340) == "82.3K"
            && OpenCodeSessionInsightsView.Compact(999_960) == "1M"
            && OpenCodeSessionInsightsView.Compact(1_000_000) == "1M"
            && OpenCodeSessionInsightsView.Compact(10_200_000) == "10.2M", "H10: compact number formatting");
        Draw(view, WithContext(82_300, 1_000_000));
        Check(ring.Button.Width == 32 && ring.Button.Height == 32 && AllText(ring.Root).Count() == 0,
            "wide card: ring button is a 32x32 icon-only button");
        ring.ShowPercent = true;
        Check(double.IsNaN(ring.Button.Width) && AllText(ring.Root).SequenceEqual(new[] { "8%" }),
            "layout D narrow header: ring shows its context % beside it");
        ring.ShowPercent = false;
        Check(ring.Button.Width == 32 && AllText(ring.Root).Count() == 0 && ReferenceEquals(ring.Button.Content, ring.Ring),
            "back to wide: same ring instance, icon only");
        Check(!ring.Ring.IsIndeterminate && ring.Ring.IsActive && ring.Ring.Minimum == 0 && ring.Ring.Maximum == 100
            && Math.Abs(ring.Ring.Value - 8.23) < 1e-9, "determinate native ProgressRing at 8.23%");
        Check(AutomationProperties.GetAutomationId(ring.Ring) == "OpenCodeInsightsContextRing"
            && AutomationProperties.GetAutomationId(ring.Button) == "OpenCodeInsightsContextRingButton", "ring AutomationIds");
        Check(ring.ToneKey == "AccentFillColorDefaultBrush", "below 80%: accent");
        Check(ring.Summary.Contains("8%") && ring.Summary.Contains("82.3K / 1M tokens"),
            "G5/H10: tooltip and summary carry percent and compact used / limit");
        var detail = AllText((UIElement)((Flyout)ring.Button.Flyout!).Content!).ToList();
        Check(detail.Contains("上下文") && !detail.Contains("用量计费") && !detail.Contains("当前模型价目") && !detail.Contains("ACP / Billion 状态"), "flyout is the same compact context grid only");
        Check(detail.Contains("82.3K / 1M"), "flyout context uses compact format");
        Check(!detail.Contains("输入") && !detail.Contains("思考"), "metrics use icons, names are tooltips only");
        Check(!detail.Any(text => text.Contains("加载更早消息", StringComparison.Ordinal)), "no stale 'load earlier' button hint");

        Draw(view, WithContext(159_999, 200_000));
        Check(ring.ToneKey == "AccentFillColorDefaultBrush", "79.99%: still accent");
        Draw(view, WithContext(160_000, 200_000));
        Check(ring.ToneKey == "SystemFillColorCautionBrush", "80%: caution");
        Draw(view, WithContext(190_000, 200_000));
        Check(ring.ToneKey == "SystemFillColorCriticalBrush" && Math.Abs(ring.Ring.Value - 95) < 1e-9, "95%: critical");
        Draw(view, WithContext(400_000, 200_000));
        Check(ring.Ring.Value == 100 && ring.ToneKey == "SystemFillColorCriticalBrush", "over limit clamps to 100% critical");
        Draw(view, WithContext(1_000, null));
        Check(ring.Ring.Value == 0 && ring.Summary.Contains("—") && ring.ToneKey == "AccentFillColorDefaultBrush",
            "unknown limit: no fake percentage");

        var before = ring.Flyout.Content;
        var section = Field<StackPanel>(view, "_billionSection");
        var loaded = WithContext(1_000, null) with
        {
            Billion = new(OpenCodeBillionPhase.Loaded, "ses_synthetic", null,
                new BillionSessionStats { Blocks = 2, BlockDetails = ImmutableArray.Create(new BillionBlockInfo("b1", 1, 123, true), new BillionBlockInfo("b2", 2, 45, false)), TokensSaved = 42_000 }, "loaded"),
        };
        Draw(view, loaded);
        Check(!ReferenceEquals(before, ring.Flyout.Content) && AllText(ring.Flyout.Content!).Contains("42K")
            && AllText(ring.Flyout.Content!).Contains("1 / 2"), "Billion-only update refreshes compact flyout grid");
        Check(ReferenceEquals(section, Field<StackPanel>(view, "_billionSection")), "Billion inputs not reparented");
        Check(AllText(Field<Border>(view, "_usageHost")).Contains("42K"), "wide grid also refreshes in place");
        Check(OpenCodeSessionInsightsView.CompactionBlocksText(loaded.Billion.Stats! with { BlockDetailsTruncated = true }) == "2", "partial block details never invent active count");
        view.ShowContext = false;
        Check(Field<Border>(view, "_usageHost").Visibility == Visibility.Collapsed, "narrow overview excludes context");
        view.ShowContext = true;
        Check(Field<Border>(view, "_usageHost").Visibility == Visibility.Visible, "wide restores context");
    }

    private static void LayeredUsage(OpenCodeSessionInsightsView view)
    {
        var sample = WithContext(12_500, 200_000) with { Turns = 3 };
        OpenCodeSessionInfo Row(string id, string? parent, double cost) => new()
        {
            Id = id, ParentId = parent, Cost = cost,
            Tokens = new() { Input = 100, Output = 20, Reasoning = 5, Cache = new() { Read = 300, Write = 100 } },
        };
        var summary = OpenCodeUsageAggregator.Aggregate("ses_root",
            new[] { Row("ses_root", null, 1), Row("ses_child", "ses_root", 2), Row("ses_grandchild", "ses_child", 3), Row("ses_unrelated", null, 99) },
            Array.Empty<OpenCodeUsageIssue>());
        sample = sample with { Cost = new(OpenCodeInsightState.Loaded,
            new(summary, new("ses_root", true, 0), null, DateTimeOffset.UnixEpoch), "loaded") };
        Draw(view, sample);
        var host = Field<Border>(view, "_usageHost");
        var texts = AllText(host).Reverse().ToArray();
        Check(texts.Take(3).SequenceEqual(new[] { "上下文", "6.2%", "12.5K / 200K" }),
            "RC2 context + right-aligned percentage, then main-agent used / limit");
        var panel = (StackPanel)host.Child!;
        var heading = (Grid)panel.Children[0];
        var percent = heading.Children.OfType<TextBlock>().Last();
        Check(heading.ColumnDefinitions[0].Width.GridUnitType == GridUnitType.Star
            && heading.ColumnDefinitions[1].Width.GridUnitType == GridUnitType.Auto
            && percent.HorizontalAlignment == HorizontalAlignment.Right
            && (string)heading.Attached["ToolTip"] == "第 3 轮", "percentage at header trailing edge; turns available by tooltip");
        var current = (StackPanel)panel.Children[1];
        var progress = current.Children.OfType<ProgressBar>().Single();
        Check(panel.Spacing == 16 && current.Spacing == 8 && !progress.IsIndeterminate
            && progress.Minimum == 0 && progress.Maximum == 100 && Math.Abs(progress.Value - 6.25) < 1e-9
            && progress.Visibility == Visibility.Visible, "native determinate context progress under used/limit with section breathing room");
        Check(OpenCodeSessionInsightsView.UsageLabels.SequenceEqual(new[] { "输入", "输出", "思考", "缓存读", "缓存写", "命中率" }), "V4 row order");
        var usage = ((StackPanel)host.Child!).Children.OfType<Grid>().Single(grid => AutomationProperties.GetAutomationId(grid) == "OpenCodeInsightsUsageGrid");
        Check(usage.ColumnDefinitions.Count == 3 && usage.RowDefinitions.Count == 2
            && usage.ColumnDefinitions.All(column => column.Width.GridUnitType == GridUnitType.Star && column.Width.Value == 1)
            && usage.ColumnSpacing == 16 && usage.RowSpacing == 12, "three equal native columns, 16px columns / 12px rows");
        Check(usage.Children.Cast<Grid>().Select(cell => cell.Children.OfType<FontIcon>().Single().Glyph).SequenceEqual(OpenCodeSessionInsightsView.UsageGlyphs), "all metrics use specified Fluent glyphs including hit target");
        Check(usage.Children.Cast<Grid>().All(cell => cell.Children.OfType<FontIcon>().Single().FontSize == 16), "16px metric icons");
        var totals = panel.Children.OfType<Grid>().Single(grid => AutomationProperties.GetAutomationId(grid) == "OpenCodeInsightsTotals");
        Check(totals.ColumnSpacing == 16 && totals.RowSpacing == 12 && panel.Children[panel.Children.IndexOf(totals) - 1] is Border,
            "totals follow divider with the same spacing");
        Check(usage.Children.Cast<Grid>().All(cell => cell.Attached.ContainsKey("ToolTip")), "metrics expose names and values in tooltip");
        Check(texts.Contains("1.6K") && texts.Contains(OpenCodeSessionInsightsView.Money(6)), "tree totals still include descendants once");
        Check(!texts.Contains("输入") && !texts.Contains("汇总"), "icon-only grid has no visible labels");
        var child = sample with { SessionId = "ses_child", Context = sample.Context with { Value = sample.Context.Value! with { SessionId = "ses_child" } } };
        Draw(view, child);
        Check(OpenCodeSessionInsightsView.CurrentContextLine(child) == "12.5K / 200K · 6.2%", "SA4 child's own assistant usage and model limit, not tree tokens");
        Check(OpenCodeContextRing.Measure(child with { Context = sample.Context }).Tokens is null, "mismatched parent context cannot populate child ring");
        Draw(view, Empty());
        Check(((StackPanel)((StackPanel)host.Child!).Children[1]).Children.OfType<ProgressBar>().Single().Visibility == Visibility.Collapsed,
            "unknown context never shows a zero progress bar");
        Check(OpenCodeSessionInsightsView.TurnsText(null) == "" && !AllText(host).Contains("轮次不可用"), "unknown turns hidden");
        Check(!AllText(host).Contains("不可用") && ((StackPanel)host.Child!).Children.OfType<Grid>().SelectMany(grid => grid.Children).OfType<Grid>().All(cell => cell.Visibility == Visibility.Collapsed), "unknown numeric cells hidden, never zero placeholders");
        var stale = sample with { Context = sample.Context with { Value = sample.Context.Value! with { StaleReasons = ImmutableArray.Create(OpenCodeContextStaleReason.CompactionAfterUsage) } } };
        Check(OpenCodeSessionInsightsView.CurrentContextLine(stale) == "", "compaction awaits fresh session usage");
    }

    private static void ConnectionFlyout(OpenCodeSessionInsightsView _)
    {
        var at = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var scope = new OpenCodeCapabilityScope("ep-view", 1, "/work", at);
        OpenCodePluginInfo Plugin(string? id, OpenCodePluginSource source, string status, string? error = null) => new()
        {
            Id = id, Source = source, Features = new() { Server = true, Tui = true }, State = new() { Status = status, Error = error },
        };
        var plugins = new[]
        {
            Plugin("builtin-a", new() { Type = "builtin" }, "active"),
            Plugin(null, new() { Type = "builtin" }, "active"),
            Plugin("opencode-todolist", new() { Type = "package", Target = "opencode-todolist@0.4.1", Version = "0.4.1", Outdated = true }, "active"),
            Plugin(null, new() { Type = "local", Path = "/home/u/.config/opencode/plugin/my-hook.ts" }, "failed", "synthetic load error"),
        };
        var mcp = new[]
        {
            new OpenCodeMcpServer { Name = "files", Status = new() { Status = "connected" } },
            new OpenCodeMcpServer { Name = "remote", Status = new() { Status = "needs_auth", Error = "synthetic auth hint" } },
            new OpenCodeMcpServer { Name = "booting", Status = new() { Status = "pending" } },
        };
        var snapshot = OpenCodeCapabilityStatus.Create(scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.Success(mcp),
            OpenCodeCapabilityRead<OpenCodePluginInfo>.Success(plugins));
        var refreshed = 0;
        string? selected = null;
        var mcpCalls = new List<(string Server, OpenCodeMcpRequest Request)>();
        var details = new OpenCodeConnectionDetails("已连接", "最近一次请求已收到服务响应", "http://127.0.0.1:4096",
            new OpenCodeServerInfoView(OpenCodeInsightState.Loaded, "1.2.3", "ok"),
            new OpenCodeInsightSection<OpenCodeCapabilitySnapshot>(OpenCodeInsightState.Loaded, snapshot, "ok"), null)
        {
            ServerDotKey = OpenCodeInsightsTheme.Success,
        };
        ScrollViewer Rebuild() => (ScrollViewer)OpenCodeConnectionFlyout.Build(details, () => refreshed++,
            selected, key => selected = key, (server, req) => mcpCalls.Add((server, req)));

        // H6 list view: concise rows (name left, indicator right), built-ins silently hidden, no meta captions.
        var root = Rebuild();
        var texts = AllText(root).ToList();
        var groups = ((StackPanel)root.Content!).Children.Select(child => AutomationProperties.GetAutomationId(child)).Where(id => id is not null).ToList();
        Check(groups.SequenceEqual(new[] { "OpenCodeConnectionServer", "OpenCodeConnectionMcp", "OpenCodeConnectionPlugins", "OpenCodeConnectionLsp", "OpenCodeConnectionRefresh" }),
            "four groups + refresh button on list view");
        Check(texts.Contains("OpenCode") && texts.Contains("1.2.3") && texts.Contains("files") && texts.Contains("remote")
            && texts.Contains("booting") && texts.Contains("opencode-todolist") && texts.Contains("0.4.1") && texts.Contains("my-hook.ts"),
            "H6: list rows show server, MCP and non-built-in plugin names");
        Check(!texts.Contains("builtin-a") && !texts.Any(t => t.Contains("已隐藏", StringComparison.Ordinal))
            && !texts.Any(t => t.Contains("未观测", StringComparison.Ordinal)),
            "H6: built-in plugins silently hidden; no meta captions on list view");
        Check(OpenCodeConnectionFlyout.McpDotKey("connected") == OpenCodeInsightsTheme.Success
            && OpenCodeConnectionFlyout.McpDotKey("failed") == OpenCodeInsightsTheme.Critical
            && OpenCodeConnectionFlyout.McpIndicator("pending", false) is ProgressRing,
            "H6: MCP status indicators (green ok, red failed, ProgressRing connecting)");
        Check(OpenCodeConnectionFlyout.VisiblePlugins(snapshot).Count == 2, "projection keeps exactly the non-built-in plugins");
        Check(OpenCodeConnectionFlyout.IsMonospaceRow("路径") && OpenCodeConnectionFlyout.IsMonospaceRow("版本") && OpenCodeConnectionFlyout.IsMonospaceRow("包")
            && !OpenCodeConnectionFlyout.IsMonospaceRow("运行状态") && !OpenCodeConnectionFlyout.IsMonospaceRow("错误"),
            "only version/package/path values use the app monospace style");

        // Click server row -> server detail page, then back.
        FindButton(root, "OpenCodeConnectionServer.Row")!.SimulateClick();
        Check(selected == OpenCodeConnectionFlyout.ServerKey, "server row selects server detail");
        var serverDetail = AllText(Rebuild()).ToList();
        Check(serverDetail.Contains("http://127.0.0.1:4096") && serverDetail.Contains("1.2.3") && serverDetail.Contains("已连接"),
            "server detail shows URL, version and connection status");
        FindButton(Rebuild(), "OpenCodeConnectionDetailBack")!.SimulateClick();
        Check(selected is null, "back returns to list");

        // Click connected MCP row -> detail with Reconnect + Disconnect actions.
        FindButton(Rebuild(), "OpenCodeConnectionMcp.files")!.SimulateClick();
        var filesView = Rebuild();
        FindButton(filesView, "OpenCodeConnectionMcpReconnect")!.SimulateClick();
        FindButton(filesView, "OpenCodeConnectionMcpDisconnect")!.SimulateClick();
        Check(mcpCalls.SequenceEqual(new[] { ("files", OpenCodeMcpRequest.Reconnect), ("files", OpenCodeMcpRequest.Disconnect) }),
            "H6: connected MCP detail offers Reconnect and Disconnect");

        // Click non-connected MCP row -> detail with error text + Connect action.
        selected = null;
        FindButton(Rebuild(), "OpenCodeConnectionMcp.remote")!.SimulateClick();
        var remoteView = Rebuild();
        var remoteTexts = AllText(remoteView).ToList();
        Check(remoteTexts.Contains("需要授权") && remoteTexts.Contains("synthetic auth hint"), "MCP detail shows status and error");
        FindButton(remoteView, "OpenCodeConnectionMcpConnect")!.SimulateClick();
        Check(mcpCalls.Last() == ("remote", OpenCodeMcpRequest.Connect), "H6: non-connected MCP detail offers Connect");

        // Click package plugin row -> detail with target, version, outdated, features.
        selected = null;
        FindButton(Rebuild(), "OpenCodeConnectionPlugin.0")!.SimulateClick();
        var pkgTexts = AllText(Rebuild()).ToList();
        Check(pkgTexts.Contains("opencode-todolist@0.4.1") && pkgTexts.Contains("0.4.1") && pkgTexts.Contains("有新版本")
            && pkgTexts.Contains("服务器 / TUI"), "H6: package plugin detail shows target, version, outdated and features");

        // Click local failed plugin row -> detail with path, failed status, error text, source.
        selected = null;
        FindButton(Rebuild(), "OpenCodeConnectionPlugin.1")!.SimulateClick();
        var localTexts = AllText(Rebuild()).ToList();
        Check(localTexts.Contains("my-hook.ts") && localTexts.Contains("/home/u/.config/opencode/plugin/my-hook.ts")
            && localTexts.Contains("加载失败") && localTexts.Contains("synthetic load error") && localTexts.Contains("本地文件"),
            "H6: local plugin detail shows name, path, failed status, error text and source");

        // Click LSP row -> honest unavailable detail.
        selected = null;
        FindButton(Rebuild(), "OpenCodeConnectionLsp.Row")!.SimulateClick();
        Check(AllText(Rebuild()).Any(t => t.Contains("未公开LSP", StringComparison.Ordinal)), "LSP detail honestly unavailable");

        selected = null;
        var refresh = FindButton(Rebuild(), "OpenCodeConnectionRefresh");
        Check(refresh is { IsEnabled: true }, "refresh button present");
        refresh!.SimulateClick();
        Check(refreshed == 1, "refresh only on explicit click");

        var noSession = details with { CapabilitiesUnavailableReason = "选择一个会话后显示 MCP / 插件状态", ServerInfo = OpenCodeServerInfoView.Loading };
        var pending = (ScrollViewer)OpenCodeConnectionFlyout.Build(noSession, () => refreshed++);
        var pendingTexts = AllText(pending).ToList();
        Check(pendingTexts.Contains("选择一个会话后显示 MCP / 插件状态") && !pendingTexts.Contains("files"), "no session: capability lists not faked");
        Check(FindButton(pending, "OpenCodeConnectionRefresh") is { IsEnabled: false }, "refresh disabled while reading");
    }

    private static Button? FindButton(UIElement root, string automationId)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case Button button when AutomationProperties.GetAutomationId(button) == automationId: return button;
                case Panel panel: foreach (var child in panel.Children) stack.Push(child); break;
                case Border border when border.Child is not null: stack.Push(border.Child); break;
                case ContentControl content when content.Content is not null: stack.Push(content.Content); break;
            }
        }

        return null;
    }

    private static OpenCodeSessionInsightsSnapshot WithChildren(params (string Id, string Agent, bool? Active, string? Title)[] rows)
    {
        var at = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var scope = new OpenCodeCapabilityScope("ep-view", 1, null, at);
        var children = new OpenCodeSubagentStatusSnapshot(scope, "ses_synthetic",
            rows.Select(row => new OpenCodeSubagentSessionStatus(row.Id, "ses_synthetic", row.Agent, row.Active, row.Title)).ToImmutableArray(),
            true, new OpenCodeCapabilityState(OpenCodeCapabilityAvailability.Available, "ok"), "synthetic reason");
        return Billion(OpenCodeBillionPhase.Idle, confirmed: false) with
        {
            Children = new OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot>(OpenCodeInsightState.Loaded, children, "loaded"),
        };
    }

    private static bool HasText(UIElement root, string value) => AllText(root).Any(t => t.Contains(value, StringComparison.Ordinal));

    private static IEnumerable<string> AllText(UIElement root)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case TextBlock text: yield return text.Text; break;
                case Panel panel: foreach (var child in panel.Children) stack.Push(child); break;
                case Border border when border.Child is not null: stack.Push(border.Child); break;
                case ListView list: foreach (var item in list.Items) stack.Push(item); break;
                case ContentControl content when content.Content is not null: stack.Push(content.Content); break;
            }
        }
    }

    // ---------------------------------------------------------------- render + invariants

    private static readonly Dictionary<OpenCodeSessionInsightsView, (CheckBox, Button, StackPanel)> Originals = new();

    private static void Draw(OpenCodeSessionInsightsView view, OpenCodeSessionInsightsSnapshot snapshot)
    {
        var confirm = Field<CheckBox>(view, "_billionConfirm");
        var button = Field<Button>(view, "_billionButton");
        var origin = Field<StackPanel>(view, "_billionOriginPanel");
        if (!Originals.TryGetValue(view, out var original))
        {
            Originals[view] = original = (confirm, button, origin);
        }

        view.Render(snapshot);

        Check(ReferenceEquals(confirm, original.Item1) && ReferenceEquals(button, original.Item2)
            && ReferenceEquals(origin, original.Item3), "same Billion control instances");
        Check(AutomationProperties.GetAutomationId(confirm) == "OpenCodeInsightsBillionConfirm"
            && AutomationProperties.GetAutomationId(button) == "OpenCodeInsightsBillionReadButton", "AutomationIds preserved");
        var pages = Field<StackPanel[]>(view, "_pages");
        var body = Field<StackPanel>(view, "_billionPage");
        if (snapshot.UnavailableReason is not null)
        {
            Check(pages.All(page => page.Children.Count == 0), "unavailable: every section page empty");
            Check(confirm.Owner is null && button.Owner is null && origin.Owner is null, "unavailable: Billion controls released (no stale owner)");
            return;
        }

        // Wave 3 F10 + Wave 5 G5: SelectorBar pages — 概况 (任务 + Agents; context ring moved to header), Billion.
        // Usage/cost, pricing and ACP/Billion status live in the header ring's Flyout (three groups); MCP/plugins/LSP moved to the
        // header connection Flyout (see ConnectionFlyout case).
        var hasTodo = snapshot.Todo.Value is { State: OpenCodeTodoState.Listed, Items.Length: > 0 };
        Check(pages.Length == 2 && pages[0].Children.Count == (hasTodo ? 1 : 0)
            && ReferenceEquals(body, pages[1]) && body.Children.Count == 1, "empty todo omits its section");
        var ring = Field<OpenCodeContextRing>(view, "_contextRing");
        Check(!pages[0].Children.Contains(ring.Root) && ring.Button.Flyout is Flyout { Content: not null }, "ring remains in header");
        Check(FlyoutGroupIds(ring).SequenceEqual(new[] { "OpenCodeInsightsLayeredUsage" }), "flyout contains shared context grid only");
        var section = (StackPanel)body.Children[0];
        Check(AutomationProperties.GetAutomationId(section) == "OpenCodeInsightsBillion", "Billion section on its own page");
        var content = (StackPanel)section.Children[1];
        Check(content.Children.IndexOf(origin) == 1 && content.Children.IndexOf(confirm) == 2 && content.Children.IndexOf(button) == 3,
            "origin panel, checkbox, then button directly after the intro caption");
        Check(ReferenceEquals(confirm.Owner, content) && ReferenceEquals(button.Owner, content) && ReferenceEquals(origin.Owner, content),
            "single owner = current Billion content");
        Check(confirm.IsChecked == snapshot.BillionConfirmed, "checked state mirrors snapshot");
        Check(button.IsEnabled == (snapshot.BillionContext is not null && snapshot.BillionConfirmed
                && snapshot.Billion.Phase != OpenCodeBillionPhase.Loading),
            "read button enabled only with rendered context, confirmation, not loading");
    }

    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("invariant: " + what);
    }

    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    // ---------------------------------------------------------------- snapshots (real records)

    private static OpenCodeSessionInsightsSnapshot Empty() => OpenCodeSessionInsightsSnapshot.Empty(null, null);

    private static OpenCodeSessionInsightsSnapshot Unavailable() => OpenCodeSessionInsightsSnapshot.Empty(null, "OpenCode 连接尚未就绪。");

    private static OpenCodeSessionInsightsSnapshot Billion(OpenCodeBillionPhase phase, bool confirmed)
    {
        var at = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var stats = phase == OpenCodeBillionPhase.Loaded
            ? new BillionSessionStats { ContextTokens = 1200, ContextWindow = 200000, Requests = 3, CacheHitPct = 50, FetchedAt = at }
            : null;
        return OpenCodeSessionInsightsSnapshot.Empty("ses_synthetic", null) with
        {
            SampledAt = at,
            Billion = new OpenCodeBillionView(phase, "ses_synthetic", null, stats, "synthetic note"),
            BillionConfirmed = confirmed,
            BillionOrigin = "http://127.0.0.1:1",
            BillionOriginState = confirmed ? OpenCodeBillionOriginState.Confirmed : OpenCodeBillionOriginState.Unconfirmed,
            BillionContext = SyntheticContext,
        };
    }

    // Synthetic render context (no controller): a never-used offline client/source, only for reference identity.
    private static readonly OpenCodeBillionContext SyntheticContext = CreateSyntheticContext();

    private static OpenCodeBillionContext CreateSyntheticContext()
    {
        var client = new OpenCodeClient(new HttpClient(new NoNetwork()), new OpenCodeClientOptions
        {
            BaseUrl = "https://offline.invalid/prefix",
            AuthenticationMode = OpenCodeAuthenticationMode.None,
        }, true);
        var source = new OpenCodePageSource("ep-view", 1, () => client);
        return new OpenCodeBillionContext(new OpenCodeInsightsBinding(source, 1, 1, client, 1, 1, "ses_synthetic"), 1, 1);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("network is not allowed in the view harness");
    }
}
