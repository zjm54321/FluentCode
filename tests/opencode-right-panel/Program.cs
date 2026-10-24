using System;
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
        foreach (var missing in new string?[] { null, "", "  " })
        {
            Check(OpenCodeRightPanelPresentation.Create(true, missing, "title", "/real") is null, "no ID means no session header");
            var unknown = OpenCodeRightPanelPresentation.Create(true, "session", missing, missing)!;
            Check(unknown.Title == "未命名会话" && unknown.Directory == "不可用", "missing metadata stays honest");
            Check(OpenCodeRightPanelPresentation.Version(missing) == "不可用", "missing backend version unavailable");
        }
        Check(OpenCodeRightPanelPresentation.Create(false, "old-session", "old title", "/old") is null, "home/blank never projects previous session");
        var directory = "/home/用户/project with spaces/" + new string('a', 400);
        var session = OpenCodeRightPanelPresentation.Create(true, "ses_123", "真实会话", directory)!;
        Check(session.SessionId == "ses_123" && session.Title == "真实会话" && session.Directory == directory, "exact ID/title/full directory preserved");
        Check(OpenCodeRightPanelPresentation.Version("2.1.1") == "2.1.1", "server version preserved, not client version");
        foreach (var page in new[] { false, true })
        foreach (var source in new[] { false, true })
        foreach (var connected in new[] { false, true })
            Check(OpenCodeRightPanelPresentation.ShowConnectedDot(page, source, connected) == (page && source && connected), "dot requires active page, live source and connected state");

        // Layout D bottom-right card: read-only directory above logo · version · green dot (no "已连接"), clickable for details.
        var view = System.IO.File.ReadAllText(System.IO.Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeRightPanelView.cs"));
        var metadata = System.IO.File.ReadAllText(System.IO.Path.Combine(args[0], "src/AIFrontend/Pages/OpenCodePage.RightPanelMetadata.cs"));
        Check(view.Contains("public FrameworkElement Directory { get; }") && view.Contains("Children = { _logo, _version, _dot }")
            && !view.Contains("已连接") && view.Contains("IsTextSelectionEnabled = true"), "D: read-only directory row separate from the logo/version/dot row");
        Check(metadata.IndexOf("RightPanelDirectorySlot.Children.Add", System.StringComparison.Ordinal) >= 0
            && metadata.Contains("ConnectionFlyout.ShowAt(_rightPanelMetadata.Footer)"), "D: directory docks in the bottom-right card; version row opens version/MCP/LSP details");
        Check(view.Contains("Children = { _title, CopyButton, _hint } }"), "D: right card opens with title then copyable session id");
        Check(OpenCodeContextHeader.TitleRow == 0 && OpenCodeContextHeader.ContextRow == 1, "CX1 title first, context label/percentage second, no leading empty row");
        foreach (var turns in new[] { 1, 2, 3, 100 })
        {
            Check(OpenCodeContextHeader.TurnNumber(turns) == turns.ToString(System.Globalization.CultureInfo.InvariantCulture), "CX1 turn indicator is the number only");
            Check(OpenCodeContextHeader.TurnTooltip(turns) == "第 " + turns + " 轮", "CX1 turn tooltip names the selected turn");
        }
        Check(OpenCodeContextHeader.TurnNumber(null) == "" && OpenCodeContextHeader.TurnTooltip(null) == "", "CX1 unknown turns hidden, not invented");
        var insights = System.IO.File.ReadAllText(System.IO.Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodeSessionInsightsView.cs"));
        Check(insights.Contains("Grid.SetRow(_usageHost, 0)") && insights.Contains("Grid.SetRow(title, OpenCodeContextHeader.TitleRow)")
            && insights.Contains("Grid.SetRow(heading, OpenCodeContextHeader.ContextRow)"), "CX1 actual view wires both header rows before status/selector placeholders");
        Console.WriteLine($"PASS {_checks} right-panel presentation checks; native clipboard/flyout visuals require Windows GUI acceptance.");
    }
}
