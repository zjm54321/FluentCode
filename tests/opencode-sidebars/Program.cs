using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static void Main(string[] args)
    {
        var narrow = new OpenCodeSidebarLayout();
        Check(!narrow.IsWide && !narrow.LeftOpen && !narrow.RightOpen, "initial narrow drawers closed");
        // Layout D: 300 card + 24 outer + 8 inner gap per side, reading column 820 + 2×16 padding.
        Check(OpenCodeSidebarLayout.LeftWidth == 332 && OpenCodeSidebarLayout.RightWidth == 332
            && OpenCodeSidebarLayout.EnterWideWidth == 1516 && OpenCodeSidebarLayout.LeaveWideWidth == 1468, "D dock budget 332+852+332");
        Check(!narrow.Resize(1515).IsWide, "enter threshold exclusive below 1516");
        var wide = narrow.Resize(1516);
        Check(wide.IsWide && wide.LeftOpen && wide.RightOpen, "both resident at 1516 epx");
        Check(wide.ForSession(true) == new OpenCodeSidebarSurface(true, true, true, false), "session owns wide resident panels");
        Check(wide.ForSession(false) == default, "home and new-session have no residents or drawer entries at wide width");
        Check(narrow.Toggle(OpenCodeSidebarDrawer.Left).ForSession(false) == default, "leaving session hides an open narrow drawer and its entry");
        Check(narrow.ForSession(true).ShowDrawerButtons, "narrow session retains drawer entries");
        Check(wide.Resize(1468).IsWide, "leave boundary inclusive at 1468");
        Check(wide.Resize(1492).IsWide && !narrow.Resize(1492).IsWide, "hysteresis depends on previous mode");
        Check(!wide.Resize(1467.99).IsWide, "leave wide below 1468");
        foreach (var width in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Check(wide.Resize(width) == wide, "ignore unmeasured/invalid page width " + width);
        Check(narrow.ShowHeaderContext && !narrow.ShowHeaderConnection && narrow.ShowHeaderOverview,
            "narrow header keeps only context % and 概况 (connection lives in the composer row)");
        Check(!wide.ShowHeaderContext && !wide.ShowHeaderConnection && !wide.ShowHeaderOverview, "wide header shows no sidebar entries");
        var left = narrow.Toggle(OpenCodeSidebarDrawer.Left);
        Check(left.LeftOpen && !left.RightOpen, "left drawer toggle opens");
        Check(left.Toggle(OpenCodeSidebarDrawer.Left) == narrow, "left drawer toggle closes");
        var right = left.Toggle(OpenCodeSidebarDrawer.Right);
        Check(right.RightOpen && !right.LeftOpen, "opening right atomically replaces left");
        Check(right.Toggle(OpenCodeSidebarDrawer.Right) == narrow, "overview is a two-way toggle");
        Check(right.Dismiss(OpenCodeSidebarDrawer.Left) == right, "late left light-dismiss cannot close right");
        Check(right.Dismiss(OpenCodeSidebarDrawer.Right) == narrow, "native right light-dismiss clears drawer");
        Check(left.Dismiss(OpenCodeSidebarDrawer.Left) == narrow, "native left light-dismiss clears drawer");
        Check(right.Escape() == narrow && left.Escape() == narrow, "Esc closes either drawer");
        Check(narrow.Escape() == narrow, "Esc with no drawer is a no-op");
        Check(wide.Toggle(OpenCodeSidebarDrawer.Left) == wide && wide.Toggle(OpenCodeSidebarDrawer.Right) == wide,
            "resident panels cannot be toggled shut");
        Check(wide.Escape() == wide && wide.Dismiss(OpenCodeSidebarDrawer.Left) == wide, "Esc/light-dismiss cannot hide residents");
        Check(left.Resize(1516) == wide && wide.Resize(1200) == narrow, "mode transitions clear transient drawer state");
        Check(narrow.Toggle(OpenCodeSidebarDrawer.None) == narrow, "None is not a drawer");
        Check(left.AnimateDrawers(true) && !left.AnimateDrawers(false), "reduced motion disables drawer animation");
        Check(!wide.AnimateDrawers(true) && !wide.AnimateDrawers(false), "resident cards never slide as drawers");
        Check(OpenCodeSidebarLayout.AnimateModeSwitch(wide, narrow, true) && OpenCodeSidebarLayout.AnimateModeSwitch(narrow, wide, true),
            "wide⇄narrow folds in / pops out when system animations are on");
        Check(!OpenCodeSidebarLayout.AnimateModeSwitch(wide, narrow, false) && !OpenCodeSidebarLayout.AnimateModeSwitch(narrow, wide, false),
            "system animations off: mode switch is instant");
        Check(!OpenCodeSidebarLayout.AnimateModeSwitch(wide, wide, true) && !OpenCodeSidebarLayout.AnimateModeSwitch(narrow, left, true),
            "no mode change, no fold/pop");
        for (var i = 0; i < 100; i++)
        {
            wide = wide.Resize(i % 2 == 0 ? 1470 : 1510);
            narrow = narrow.Resize(i % 2 == 0 ? 1470 : 1510);
        }
        Check(wide.IsWide && !narrow.IsWide, "resize jitter inside dead band does not oscillate");

        var repo = args[0];
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var layoutCode = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.Sidebars.cs"));
        Check(layoutCode.Contains("wide ? OpenCodeSidebarLayout.LeftWidth") && layoutCode.Contains("wide ? OpenCodeSidebarLayout.RightWidth"),
            "runtime pane lengths override legacy initial XAML tokens with 300px card budgets");
        var page = XDocument.Load(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.xaml"));
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var pane = page.Descendants(ui + "SplitView").Single(e => (string?)e.Attribute(x + "Name") == "SessionPane");
        var sidebarContent = pane.Element(ui + "SplitView.Pane")!.Elements().Single();
        Check(sidebarContent.Elements().Select(e => (string?)e.Attribute(x + "Name")).SequenceEqual(new[] { "LeftAgentTreeSlot", "LeftBottomCardSlot" }),
            "left sidebar contains only agent tree and control card");
        Check(!page.Descendants().Any(e => new[] { "SessionList", "NewSessionButton", "RefreshSessionsButton", "SessionsHint", "LoadMoreSessionsButton" }
            .Contains((string?)e.Attribute(x + "Name"))), "native sidebar list/buttons/hints are deleted, not hidden");
        // Layout D cards: right session card order, bottom-right directory above version, calm WSL entry, no old heading/toolbar.
        XElement Named(string name) => page.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        var names = page.Descendants().Select(e => (string?)e.Attribute(x + "Name")).Where(n => n is not null).ToList();
        int At(string name) => names.IndexOf(name);
        var card = Named("RightSessionCard");
        var cardContent = Named("RightSessionContent");
        Check((string?)cardContent.Attribute("Padding") == "16" && (string?)cardContent.Attribute("RowSpacing") == "16",
            "RC2 right card uses 16px padding and section spacing");
        Check(cardContent.Name == ui + "Grid" && (string?)cardContent.Attribute("RowDefinitions") == "Auto,*"
            && !card.Descendants(ui + "ScrollViewer").Any() && (string?)Named("InsightsPaneHost").Attribute("Grid.Row") == "1",
            "RP9 top metadata fixed and finite remaining height reaches insights; no whole-card scroll viewer");
        Check((string?)Named("RightDock").Attribute("RowDefinitions") == "*,Auto"
            && (string?)Named("RightFooterCard").Attribute("Grid.Row") == "1"
            && layoutCode.Contains("RightDock.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star)")
            && layoutCode.Contains("PageRoot.ActualHeight - 96"), "RP9 footer pinned and narrow flyout has a finite shared height budget");
        Check(card.Descendants().Contains(Named("RightPanelHeaderSlot")) && card.Descendants().Contains(Named("InsightsPaneHost"))
            && At("RightPanelHeaderSlot") < At("RightSessionDiagnostics") && At("RightSessionDiagnostics") < At("InsightsPaneHost"),
            "right card: title/id first, then usage and to do");
        var footer = Named("RightFooterCard");
        Check(footer.Descendants().Contains(Named("RightPanelDirectorySlot")) && footer.Descendants().Contains(Named("RightPanelFooterSlot"))
            && At("RightPanelDirectorySlot") < At("RightPanelFooterSlot"), "bottom-right card: directory above logo/version");
        Check(card.Descendants().Contains(Named("WslQuickAccessSlot")) && card.Descendants().Contains(Named("WslQuickAccessNoteSlot"))
            && At("RightPanelHeaderSlot") < At("WslQuickAccessSlot") && At("WslQuickAccessSlot") < At("InsightsPaneHost"),
            "WSL quick access follows title/id and precedes context in top-right card");
        foreach (var dockCard in new[] { card, footer })
            Check((string?)dockCard.Attribute("Style") == "{StaticResource DockCard}"
                && dockCard.Attribute("Background") is null && (string?)dockCard.Attribute("Translation") == "0,0,16"
                && dockCard.Descendants(ui + "ThemeShadow").Any(), "shared solid card survives ClearValue with theme shadow: " + dockCard.Attribute(x + "Name")!.Value);
        var app = XDocument.Load(Path.Combine(repo, "src/AIFrontend/App.xaml"));
        var chrome = app.Descendants(ui + "Style").Single(e => (string?)e.Attribute(x + "Key") == "DockCard");
        string? Setter(string property) => (string?)chrome.Elements(ui + "Setter").Single(e => (string?)e.Attribute("Property") == property).Attribute("Value");
        Check(Setter("Background") == "{ThemeResource SolidBackgroundFillColorQuarternaryBrush}"
            && Setter("BorderBrush") == "{ThemeResource CardStrokeColorDefaultBrush}"
            && Setter("BorderThickness") == "1" && Setter("CornerRadius") == "8", "RC1 deterministic theme-solid shared card chrome");
        Check(app.Descendants(ui + "Style").Single(e => (string?)e.Attribute(x + "Key") == "DockCardFlyoutPresenterStyle")
            .Elements(ui + "Setter").Any(e => (string?)e.Attribute("Property") == "Background"
                && (string?)e.Attribute("Value") == Setter("Background"))
            && layoutCode.Contains("_overviewFlyout.FlyoutPresenterStyle = OpenCodeInteractionUi.TryStyle(\"DockCardFlyoutPresenterStyle\")"),
            "RC1 narrow overview uses the same opaque theme surface");
        Check(!page.Descendants().Any(e => (string?)e.Attribute("Text") == "会话概况")
            && !names.Contains("OverviewRefreshMessagesButton") && !names.Contains("InsightsPaneCloseButton"),
            "old 会话概况 heading, refresh toolbar and close button removed from the right");
        foreach (var split in new[] { "SessionPane", "InsightsPane" })
            Check((string?)Named(split).Attribute("PaneBackground") == "Transparent", split + " pane has no full-height chrome; cards float");
        Check(Named("RightSessionDiagnostics").Descendants().Contains(Named("AgentSelectorHost")), "switch diagnostics stay reachable in the right card");
        var sidebars = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCodePage.Sidebars.cs"));
        Check(sidebars.Contains("OpenCodeSidebarLayout.AnimateModeSwitch(previous, next, _sidebarSettings?.AnimationsEnabled ?? false)")
            && sidebars.Contains("PlayDockMotion(fold: true") && sidebars.Contains("PlayDockMotion(fold: false"),
            "page folds/pops cards only through the system-animation gate");
        Check(sidebars.Contains("MessageScroll.TransformToVisual(SessionPane)") && sidebars.Contains("ComposerCell.TransformToVisual(SessionPane)"),
            "card insets follow the first message row and the composer bottom");
        var treeView = File.ReadAllText(Path.Combine(repo, "src/AIFrontend/Pages/OpenCode/OpenCodeAgentTreeView.cs"));
        var treeMarkup = treeView.Split("\"\"\"", StringSplitOptions.None)[1];
        var treeCard = XDocument.Parse(treeMarkup);
        var trees = treeCard.Descendants(ui + "TreeView").ToArray();
        var terminalDivider = treeCard.Descendants(ui + "Border").Single(e => (string?)e.Attribute(x + "Name") == "TerminalDivider");
        Check(trees.Length == 2 && trees.All(e => (string?)e.Attribute("SelectionMode") == "None"),
            "AT3: both agent and terminal native trees disable selection indicators");
        Check(terminalDivider.Parent == trees[0].Parent && terminalDivider.Parent == trees[1].Parent
            && !terminalDivider.Elements().Any() && (string?)terminalDivider.Attribute("Height") == "1"
            && terminalDivider.Attribute("BorderThickness") is null,
            "AT4: divider is a plain sibling line, not a box around the terminal header");
        Console.WriteLine($"PASS {_checks} sidebar policy/token checks (headless; native visuals require user checks).");
    }
}
