using System;
using System.IO;
using System.Linq;
using FluentCode.Pages;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Tone = FluentCode.Pages.OpenCodePage.SessionIndicatorTone;

internal static class TabStatusTests
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(name);
    }
    private static OpenCodeSessionUiState Idle() => new("ses_tab", false, false, false, null, null, OpenCodeStateUnknownReason.None);
    private static OpenCodeSessionUiState Unread(string outcome = "succeeded") => Idle() with { Completion = new(outcome, 100) };
    private static readonly OpenCodeSessionRetryMark Retry = new(3, 1700000001000, "上游限流");

    private static void Main(string[] args)
    {
        ClassificationAndStatus();
        GlyphsAndCollapse();
        StableNameAndStatusEvents();
        HeaderGeometryAndSourceContracts(args[0]);
        Console.WriteLine($"PASS {_checks} v3 tab classification, glyph/collapse, ItemStatus/change-event and finite-header geometry/source checks; inert WinUI only, no native rendering or Narrator audio.");
    }

    private static void ClassificationAndStatus()
    {
        var cases = new (OpenCodeSessionUiState? State, Tone Tone, string Status)[]
        {
            (null, Tone.None, "状态未知"),
            (Idle(), Tone.None, "空闲"),
            (Idle() with { Working = null }, Tone.None, "状态未知"),
            (Idle() with { Working = true }, Tone.Working, "工作中"),
            (Idle() with { Working = true, Retry = Retry }, Tone.Retrying, "重试中，第 3 次"),
            (Unread("failed") with { Working = true, PendingAction = true, Retry = Retry, BackgroundWorking = true }, Tone.WorkingNeedsAction, "工作中，需要你处理"),
            (Unread("interrupted") with { PendingAction = true, BackgroundWorking = true, Retry = Retry }, Tone.NeedsAction, "需要你处理"),
            (Unread("failed") with { BackgroundWorking = true }, Tone.Failed, "执行失败"),
            (Unread("interrupted") with { BackgroundWorking = true }, Tone.Interrupted, "已中断"),
            (Idle() with { BackgroundWorking = true, BackgroundChildCount = 2 }, Tone.BackgroundWorking, "子智能体工作中（2 个）"),
            (Idle() with { Working = null, BackgroundWorking = true }, Tone.BackgroundWorking, "子智能体工作中"),
            (Unread() with { BackgroundWorking = true, BackgroundChildCount = 2 }, Tone.BackgroundWorking, "有新的回复，子智能体工作中（2 个）"),
            (Unread(), Tone.Unread, "有新的回复"),
            (Idle() with { Retry = Retry }, Tone.None, "空闲"),
            (Idle() with { Working = null, Retry = Retry }, Tone.None, "状态未知"),
            (Unread("failed") with { ViewedWatermark = 100 }, Tone.None, "空闲"),
            (Unread("interrupted") with { ViewedWatermark = 100, BackgroundWorking = true }, Tone.BackgroundWorking, "子智能体工作中"),
            (Unread("failed") with { Working = true, BackgroundWorking = true }, Tone.Working, "工作中"),
            (Unread() with { Working = true, Retry = Retry }, Tone.Retrying, "重试中，第 3 次"),
        };
        var page = new OpenCodePage();
        for (var i = 0; i < cases.Length; i++)
        {
            var test = cases[i];
            var id = "ses_case" + i;
            const string title = "需要完整保留的长标题，不再被二百像素最大宽度截断";
            var tab = page.Tab(id, title, test.State);
            Check(OpenCodePage.ClassifySessionIndicator(test.State) == test.Tone, "classification/priority " + i);
            Check(AutomationProperties.GetItemStatus(tab) == test.Status && tab.HelpText == test.Status, "Chinese ItemStatus/HelpText " + i);
            Check(tab.AutomationName == title + "（" + id + "）", "stable tab Name without status " + i);
            Check(tab.AutomationId == "OpenCodeSessionTab." + id, "stable tab identity " + i);
            Check(page.Slot(id).Visibility == (test.Tone == Tone.None ? Visibility.Collapsed : Visibility.Visible), "idle/unknown slot collapses " + i);
            var detail = test.Tone == Tone.Retrying ? test.Status + "：上游限流" : test.Status;
            Check(Equals(tab.ToolTip, title + Environment.NewLine + detail + Environment.NewLine + id), "three-line tooltip with retry detail " + i);
        }
    }

    private static void GlyphsAndCollapse()
    {
        var slot = new OpenCodeTabStatusSlot();
        Check(slot.Width == 16 && slot.Height == 16 && slot.Margin == new Thickness(0, 0, 8, 0)
            && !slot.IsHitTestVisible && slot.Visibility == Visibility.Collapsed, "16px slot, collapsible 8px margin, no hit target");
        slot.Show(OpenCodeTabStatusGlyph.Working, unreadOverlay: true);
        var ring = (ProgressRing)slot.Children.Single();
        Check(ring is { Width: 14, Height: 14, IsActive: true, IsIndeterminate: true }
            && ring.Style?.Key == "AccentFillColorDefaultBrush" && ring.AutomationName == "进行中", "working uses the named default 14px synchronized-ring factory without unread overlay");
        slot.Show(OpenCodeTabStatusGlyph.Background);
        Check(ReferenceEquals(slot.Children.Single(), ring) && ring.Style?.Key == "TextFillColorSecondaryBrush" && ring.IsActive,
            "subagent-only is just the same grey ring, no corner badge");
        slot.Show(OpenCodeTabStatusGlyph.Background, unreadOverlay: true);
        Check(slot.Children.Count == 2 && slot.Children[1] is Ellipse { Width: 6, Height: 6, HorizontalAlignment: HorizontalAlignment.Center, VerticalAlignment: VerticalAlignment.Center },
            "background + unread adds only a centred 6px dot");
        Check(slot.Children[1].Style?.Key == "OpenCodeAttentionDotStyle", "unread overlay retains the attention-dot theme style");
        RawTree(slot);
        slot.Show(OpenCodeTabStatusGlyph.Retrying);
        Check(!ring.IsActive && slot.Children.Single() is FontIcon { Glyph: "\uE72C", FontSize: 14, Style.Key: "SystemFillColorCautionBrush" },
            "retry is a static caution refresh glyph and stops the ring");
        slot.Show(OpenCodeTabStatusGlyph.NeedsYou);
        Check(slot.Children.Single() is InfoBadge { Width: 16, Height: 16, Style.Key: "CautionIconInfoBadgeStyle" }, "waiting uses the solid native caution exclamation badge style");
        RawTree(slot);
        slot.Show(OpenCodeTabStatusGlyph.Failed);
        Outcome(slot, "\uF13D", "SystemFillColorCriticalBrush");
        slot.Show(OpenCodeTabStatusGlyph.Interrupted);
        Outcome(slot, "\uE738", "TextFillColorSecondaryBrush");
        slot.Show(OpenCodeTabStatusGlyph.Unread);
        Check(slot.Children.Single() is Ellipse { Width: 8, Height: 8, Style.Key: "OpenCodeAttentionDotStyle" }, "done-unread retains the 8px dot");
        RawTree(slot);
        slot.Show(OpenCodeTabStatusGlyph.Working);
        slot.Clear();
        Check(!ring.IsActive && slot.Children.Count == 0 && slot.Visibility == Visibility.Collapsed, "clear/close deactivates the cached ring, removes children and collapses the entire slot");
        slot.Clear();
        Check(slot.Children.Count == 0 && !ring.IsActive, "repeated clear stays inactive");
    }

    private static void Outcome(OpenCodeTabStatusSlot slot, string mark, string key)
    {
        var grid = (Grid)slot.Children.Single();
        Check(grid.Width == 14 && grid.Height == 14 && grid.Children.OfType<FontIcon>().Select(icon => icon.Glyph).SequenceEqual(new[] { "\uF138", mark }),
            "outlined outcome ring with distinct inner mark " + mark);
        Check(grid.Children.All(icon => icon.Style?.Key == key), "both outcome strokes follow the semantic theme brush " + key);
        RawTree(slot);
    }

    private static void RawTree(FrameworkElement element)
    {
        Check(element.AccessibilityView == AccessibilityView.Raw, "status slot and every nested graphic are UIA Raw");
        if (element is Grid grid) foreach (var child in grid.Children) RawTree(child);
    }

    private static void StableNameAndStatusEvents()
    {
        var page = new OpenCodePage();
        var tab = page.Tab("ses_events", "固定名称", Idle() with { Working = true });
        var peer = tab.Peer = new FrameworkElementAutomationPeer();
        var name = tab.AutomationName;
        page.Tab("ses_events", "固定名称", Idle() with { PendingAction = true });
        Check(peer.Changes.Count == 1 && peer.Changes[0] == (AutomationElementIdentifiers.ItemStatusProperty, "工作中", "需要你处理"), "status change raises ItemStatusProperty with exact old/new strings");
        page.Tab("ses_events", "固定名称", Idle() with { PendingAction = true });
        Check(peer.Changes.Count == 1, "identical status does not raise a duplicate event");
        page.Tab("ses_events", "固定名称", Idle() with { Working = true, Retry = Retry });
        page.Tab("ses_events", "固定名称", Idle() with { Working = true, Retry = Retry with { Message = "另一个错误" } });
        Check(peer.Changes.Count == 2 && tab.ItemStatus == "重试中，第 3 次", "retry error detail does not churn ItemStatus");
        page.Tab("ses_events", "固定名称", Idle() with { Working = true, Retry = Retry with { Attempt = 4 } });
        Check(peer.Changes.Count == 3 && tab.ItemStatus == "重试中，第 4 次" && tab.AutomationName == name, "retry attempt changes status once, never the tab Name");
        page.Tab("ses_events", "固定名称", Idle());
        Check(tab.ItemStatus == "空闲" && page.Slot("ses_events").Visibility == Visibility.Collapsed, "idle update removes the slot and reports idle");
    }

    private static void HeaderGeometryAndSourceContracts(string repo)
    {
        var page = new OpenCodePage();
        var tab = page.Tab("ses_geometry", new string('长', 300), Idle() with { Working = true });
        var grid = (Grid)tab.Header!;
        var title = page.Title("ses_geometry");
        Check(grid.ColumnDefinitions.Count == 2 && grid.ColumnDefinitions[0].Width == GridLength.Auto
            && grid.ColumnDefinitions[1].Width == new GridLength(1, GridUnitType.Star), "Auto status column precedes the finite remaining-width star title column");
        Check(ReferenceEquals(grid.Children[0], page.Slot("ses_geometry")) && ReferenceEquals(grid.Children[1], title) && title.Column == 1,
            "status receives width before the title");
        Check(double.IsPositiveInfinity(title.MaxWidth) && title.Text.Length == 300 && title.TextWrapping == TextWrapping.NoWrap
            && title.TextTrimming == TextTrimming.CharacterEllipsis && tab.HorizontalContentAlignment == HorizontalAlignment.Stretch,
            "long title has no 200px cap, trims inside stretched finite header instead of clipping the status");
        var tabs = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.Tabs.cs"));
        var slot = File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeTabStatusSlot.cs"));
        Check(!tabs.Contains("MaxWidth = 200", StringComparison.Ordinal) && !tabs.Contains("new StackPanel", StringComparison.Ordinal)
            && !tabs.Contains("ColumnSpacing", StringComparison.Ordinal) && !tabs.Contains("ControlTemplate", StringComparison.Ordinal), "header has no infinite-measure StackPanel, title cap, idle gap or template replacement");
        Check(slot.Contains("OpenCodeProgressRingSync.Create(", StringComparison.Ordinal) && slot.Contains("AccessibilityView.Raw", StringComparison.Ordinal)
            && !slot.Contains("ControlTemplate", StringComparison.Ordinal), "slot uses existing synchronization and hides its graphics without a custom template");
        Check(!File.Exists(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeWorkingShimmer.cs"))
            && File.Exists(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/OpenCodeWorkingClockComposition.cs")), "shimmer removed and shared clock retained in its own file");
        Check(tabs.Contains("entry.Status.Clear();", StringComparison.Ordinal) && tabs.Contains("retired.Status.Clear();", StringComparison.Ordinal)
            && File.ReadAllText(Path.Combine(repo, "src/FluentCode/Pages/OpenCodePage.DraftSlots.cs")).Contains("entry.Status.Clear();", StringComparison.Ordinal), "close and source retirement clear the status slot");
    }
}
