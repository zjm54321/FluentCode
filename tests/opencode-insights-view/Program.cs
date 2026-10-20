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
        Run("agents: grouped list, item click opens in-panel detail, open raises child id, refresh keeps/clears detail", true, Agents);
        Run("context ring: determinate value, caution/critical thresholds, unknown limit, Billion in-place refreshes flyout", true, ContextRing);
        Run("connection flyout: built-in plugins hidden by source.type, every known plugin/MCP field shown", true, ConnectionFlyout);

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
        var refresh = 0;
        var reads = 0;
        var edits = 0;
        view.BillionConfirmationChanged += (_, request) =>
        {
            Check(ReferenceEquals(request.Context, SyntheticContext), "confirmation carries the rendered context");
            confirmations.Add(request.Confirmed);
        };
        view.RefreshRequested += (_, _) => refresh++;
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
        var refresh = 0;
        view.RefreshRequested += (_, _) => refresh++;
        Draw(view, Billion(OpenCodeBillionPhase.Idle, confirmed: true));
        var bar = Field<SelectorBar>(view, "_sections");
        var pages = Field<StackPanel[]>(view, "_pages");
        // Wave 3 F10c: 任务 and Agents are merged into 概况 (ring + 任务 + Agents); only Billion (consent workflow) stays separate.
        Check(AutomationProperties.GetAutomationId(bar) == "OpenCodeInsightsSections" && bar.Items.Count == 2
            && bar.Items.Select(item => item.Text).SequenceEqual(new[] { "概况", "Billion" })
            && bar.Items.Select(item => AutomationProperties.GetAutomationId(item)).SequenceEqual(new[]
                { "OpenCodeInsightsSectionItem.Overview", "OpenCodeInsightsSectionItem.Billion" }),
            "two native sections");
        Check(ReferenceEquals(bar.SelectedItem, bar.Items[0]) && pages[0].Visibility == Visibility.Visible
            && pages.Skip(1).All(page => page.Visibility == Visibility.Collapsed), "概况 selected by default");
        var confirm = Field<CheckBox>(view, "_billionConfirm");
        var owner = confirm.Owner;
        var adds = ShimTree.AddCalls;
        bar.SelectedItem = bar.Items[1];
        Check(pages[1].Visibility == Visibility.Visible && pages[0].Visibility == Visibility.Collapsed,
            "Billion page shown");
        Check(ShimTree.AddCalls == adds && ReferenceEquals(confirm.Owner, owner), "switching sections adds nothing and never reparents");
        bar.SelectedItem = null;
        Check(ReferenceEquals(bar.SelectedItem, bar.Items[0]) && pages[0].Visibility == Visibility.Visible, "no-selection falls back to 概况");
        Check(refresh == 0, "section switching triggers no read");
    }

    private static void Agents(OpenCodeSessionInsightsView view)
    {
        var requested = new List<string>();
        view.ChildSessionRequested += (_, id) => requested.Add(id);
        view.PrimaryAgent = "build";
        Draw(view, WithChildren(("ses_c1", "explorer", true), ("ses_c2", "explorer", false), ("ses_c3", "fixer", null)));
        var agents = (StackPanel)Field<StackPanel[]>(view, "_pages")[0].Children[2];
        Check(AutomationProperties.GetAutomationId(agents) == "OpenCodeInsightsAgents", "Agents root merged into 概况 after the ring and 任务");
        var section = Field<OpenCodeInsightsAgentsSection>(view, "_agents");
        var listPage = Field<StackPanel>(section, "_listPage");
        var detail = Field<StackPanel>(section, "_detailPage");
        var children = (StackPanel)((StackPanel)listPage.Children[1]).Children[1];
        var groups = children.Children.OfType<StackPanel>()
            .Where(panel => AutomationProperties.GetAutomationId(panel)?.StartsWith("OpenCodeInsightsAgentGroup.", StringComparison.Ordinal) == true)
            .ToList();
        Check(groups.Count == 2 && AutomationProperties.GetAutomationId(groups[0]) == "OpenCodeInsightsAgentGroup.explorer"
            && AutomationProperties.GetAutomationId(groups[1]) == "OpenCodeInsightsAgentGroup.fixer", "grouped by agent, running/larger group first");
        var list = (ListView)groups[0].Children[1];
        Check(list.Items.Count == 2 && list.IsItemClickEnabled && list.SelectionMode == ListViewSelectionMode.None, "native click list");
        var second = (ListViewItem)list.Items[1];
        Check(AutomationProperties.GetAutomationId(second) == "OpenCodeInsightsAgent.ses_c2", "item AutomationId");
        Check(AllText(listPage).Any(text => text == "build"), "primary agent shown");

        list.SimulateItemClick(second);
        Check(detail.Visibility == Visibility.Visible && listPage.Visibility == Visibility.Collapsed, "detail page opened in panel");
        Check(AllText(detail).Any(text => text == "ses_c2") && AllText(detail).Any(text => text.Contains("不代表已完成", StringComparison.Ordinal)),
            "detail shows id and the not-completed distinction");
        var back = (Button)detail.Children[0];
        var open = detail.Children.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "OpenCodeInsightsAgentOpenSession");
        Check(AutomationProperties.GetAutomationId(back) == "OpenCodeInsightsAgentDetailBack", "back AutomationId");
        Check(requested.Count == 0, "opening details does not navigate");
        open.SimulateClick();
        Check(requested.SequenceEqual(new[] { "ses_c2" }), "open raises exactly the shown child id");

        Draw(view, WithChildren(("ses_c1", "explorer", false), ("ses_c2", "explorer", true)));
        detail = Field<StackPanel>(section, "_detailPage");
        Check(detail.Visibility == Visibility.Visible && AllText(detail).Any(text => text == "前台运行中"), "refresh keeps detail with new state");
        Draw(view, WithChildren(("ses_c1", "explorer", false)));
        Check(detail.Visibility == Visibility.Collapsed && listPage.Visibility == Visibility.Visible, "child gone: back to list");
        var backAgain = Field<StackPanel>(section, "_listPage");
        Check(ReferenceEquals(backAgain, listPage), "list page instance stable");
        Check(requested.Count == 1, "renders raise no navigation");
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
        Draw(view, WithContext(50_000, 200_000));
        Check(!ring.Ring.IsIndeterminate && ring.Ring.IsActive && ring.Ring.Minimum == 0 && ring.Ring.Maximum == 100
            && Math.Abs(ring.Ring.Value - 25) < 1e-9, "determinate native ProgressRing at 25%");
        Check(AutomationProperties.GetAutomationId(ring.Ring) == "OpenCodeInsightsContextRing"
            && AutomationProperties.GetAutomationId(ring.Button) == "OpenCodeInsightsContextRingButton", "ring AutomationIds");
        Check(ring.ToneKey == "AccentFillColorDefaultBrush", "below 80%: accent");
        Check(AllText(ring.Root).Contains("25%") && AllText(ring.Root).Any(text => text.StartsWith("50k / 200k", StringComparison.Ordinal)),
            "percent inside ring, used / limit next to it");
        var detail = AllText((UIElement)((Flyout)ring.Button.Flyout!).Content!).ToList();
        Check(detail.Contains("当前上下文") && detail.Contains("用量计费") && detail.Contains("ACP / Billion 状态"), "three group titles");
        Check(detail.Contains("推理") && detail.Contains("缓存读取") && detail.Contains("缓存写入"), "context breakdown rows");
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
        Check(ring.Ring.Value == 0 && AllText(ring.Root).Contains("—") && ring.ToneKey == "AccentFillColorDefaultBrush",
            "unknown limit: no fake percentage");

        // Billion-only change takes the in-place path (no reparenting) and still refreshes the flyout ACP group.
        var before = ((Flyout)ring.Button.Flyout!).Content;
        var billionSection = Field<StackPanel>(view, "_billionSection");
        var loaded = WithContext(1_000, null) with
        {
            Billion = new OpenCodeBillionView(OpenCodeBillionPhase.Loaded, "ses_synthetic", null,
                new BillionSessionStats { ContextTokens = 1200, Blocks = 7, TokensSaved = 4321, CacheHitPct = 50, FetchedAt = DateTimeOffset.UnixEpoch }, "synthetic note"),
        };
        Draw(view, loaded);
        var after = ((Flyout)ring.Button.Flyout!).Content!;
        Check(!ReferenceEquals(before, after) && AllText((UIElement)after).Contains("4,321") && AllText((UIElement)after).Contains("7"),
            "in-place Billion update rebuilt the flyout ACP group with compression stats");
        Check(ReferenceEquals(Field<StackPanel>(view, "_billionSection"), billionSection), "Billion-only change took the in-place path");
        // Font change flips the theme twice (AppFonts refresh): panel re-renders from the latest snapshot, never refetches.
        var refreshes = 0;
        view.RefreshRequested += (_, _) => refreshes++;
        view.SimulateThemeChanged();
        view.SimulateThemeChanged();
        Check(refreshes == 0 && Field<StackPanel[]>(view, "_pages")[0].Children.Count == 3
            && AllText((UIElement)((Flyout)ring.Button.Flyout!).Content!).Contains("4,321"), "double theme flip: no read, content intact");
        Check(Field<StackPanel[]>(view, "_pages")[0].Children.Count == 3, "overview page intact");
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
        };
        var snapshot = OpenCodeCapabilityStatus.Create(scope, OpenCodeCapabilityRead<OpenCodeMcpServer>.Success(mcp),
            OpenCodeCapabilityRead<OpenCodePluginInfo>.Success(plugins));
        var refreshed = 0;
        var details = new OpenCodeConnectionDetails("已连接", "最近一次请求已收到服务响应", "http://127.0.0.1:4096",
            new OpenCodeServerInfoView(OpenCodeInsightState.Loaded, "1.2.3", "ok"),
            new OpenCodeInsightSection<OpenCodeCapabilitySnapshot>(OpenCodeInsightState.Loaded, snapshot, "ok"), null);
        var root = (ScrollViewer)OpenCodeConnectionFlyout.Build(details, () => refreshed++);
        var texts = AllText(root).ToList();
        var groups = ((StackPanel)root.Content!).Children.Select(child => AutomationProperties.GetAutomationId(child)).Where(id => id is not null).ToList();
        Check(groups.SequenceEqual(new[] { "OpenCodeConnectionServer", "OpenCodeConnectionMcp", "OpenCodeConnectionPlugins", "OpenCodeConnectionLsp" }),
            "four groups: 服务状态 / MCP / 插件 / LSP");
        Check(texts.Contains("http://127.0.0.1:4096") && texts.Contains("1.2.3") && texts.Contains("已连接"), "server URL, version, connection");
        Check(texts.Contains("files") && texts.Contains("remote") && texts.Contains("需要授权") && texts.Contains("synthetic auth hint"),
            "MCP names, status and server error text");
        Check(!texts.Contains("builtin-a") && texts.Count(text => text == "OpenCode 内置") == 0, "built-in plugins hidden");
        Check(texts.Any(text => text.Contains("已隐藏 2 个 OpenCode 内置插件", StringComparison.Ordinal)), "hidden built-in count reported");
        Check(texts.Contains("opencode-todolist") && texts.Contains("opencode-todolist@0.4.1") && texts.Contains("0.4.1") && texts.Contains("有新版本"),
            "package plugin: id, target, version, outdated");
        Check(texts.Contains("my-hook.ts") && texts.Contains("/home/u/.config/opencode/plugin/my-hook.ts") && texts.Contains("加载失败")
            && texts.Contains("synthetic load error") && texts.Contains("本地文件"), "local plugin: name, path, failed status, error text, source");
        Check(texts.Contains("服务器 / TUI"), "declared features shown");
        Check(texts.Any(text => text.Contains("未公开LSP", StringComparison.Ordinal)), "LSP honestly unavailable");
        Check(OpenCodeConnectionFlyout.VisiblePlugins(snapshot).Count == 2, "projection keeps exactly the non-built-in plugins");
        Check(OpenCodeConnectionFlyout.IsMonospaceRow("路径") && OpenCodeConnectionFlyout.IsMonospaceRow("版本") && OpenCodeConnectionFlyout.IsMonospaceRow("包")
            && !OpenCodeConnectionFlyout.IsMonospaceRow("运行状态") && !OpenCodeConnectionFlyout.IsMonospaceRow("错误"),
            "only version/package/path values use the app monospace style");

        var refresh = FindButton(root, "OpenCodeConnectionRefresh");
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

    private static OpenCodeSessionInsightsSnapshot WithChildren(params (string Id, string Agent, bool? Active)[] rows)
    {
        var at = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var scope = new OpenCodeCapabilityScope("ep-view", 1, null, at);
        var children = new OpenCodeSubagentStatusSnapshot(scope, "ses_synthetic",
            rows.Select(row => new OpenCodeSubagentSessionStatus(row.Id, "ses_synthetic", row.Agent, row.Active)).ToImmutableArray(),
            true, new OpenCodeCapabilityState(OpenCodeCapabilityAvailability.Available, "ok"), "synthetic reason");
        return Billion(OpenCodeBillionPhase.Idle, confirmed: false) with
        {
            Children = new OpenCodeInsightSection<OpenCodeSubagentStatusSnapshot>(OpenCodeInsightState.Loaded, children, "loaded"),
        };
    }

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

        // Wave 3 F10: SelectorBar pages — 概况 (context ring + 任务 + Agents), Billion.
        // Usage/cost, pricing and ACP/Billion status live in the ring's Flyout (three groups); MCP/plugins/LSP moved to the
        // header connection Flyout (see ConnectionFlyout case).
        Check(pages.Length == 2 && pages[0].Children.Count == 3
            && ReferenceEquals(body, pages[1]) && body.Children.Count == 1, "two section pages with their sections");
        Check(new[] { "OpenCodeInsightsContext", "OpenCodeInsightsTodo", "OpenCodeInsightsAgents" }
                .SequenceEqual(pages[0].Children.Select(child => AutomationProperties.GetAutomationId(child))),
            "概况 shows ring, 任务 and Agents together (section AutomationIds kept)");
        var ring = Field<OpenCodeContextRing>(view, "_contextRing");
        Check(ReferenceEquals(pages[0].Children[0], ring.Root) && ring.Button.Flyout is Flyout { Content: not null }, "ring button owns a flyout with content");
        Check(FlyoutGroupIds(ring).SequenceEqual(new[] { "OpenCodeInsightsContextDetail", "OpenCodeInsightsCost", "OpenCodeInsightsAcpStatus" }),
            "flyout has exactly three groups: 当前上下文 / 用量计费 / ACP·Billion");
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
