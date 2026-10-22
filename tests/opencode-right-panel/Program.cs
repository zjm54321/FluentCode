using System;
using System.IO;
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

        var root = args[0];
        string Read(string path) => File.ReadAllText(Path.Combine(root, "src/AIFrontend", path));
        var pageSource = Read("Pages/OpenCodePage.xaml.cs");
        var integration = Read("Pages/OpenCodePage.RightPanelMetadata.cs");
        var view = Read("Pages/OpenCode/OpenCodeRightPanelView.cs");
        var boundary = Read("Pages/OpenCodePage.MessagePagination.cs");
        var flyout = Read("Pages/OpenCodePage.ConnectionFlyout.cs");
        var sidebars = Read("Pages/OpenCodePage.Sidebars.cs");
        Check(pageSource.Contains("InitializeRightPanelMetadata();") && pageSource.Contains("_ = ReadServerInfoAsync(); // B2 footer"), "slots initialized and version read once per installed source");
        Check(pageSource.Contains("private void UpdateEmptyState()\n    {\n        RefreshRightPanelMetadata();"), "session switch/missing/source reset synchronously reprojects metadata");
        Check(integration.Contains("!_homeActive && !_newSessionDraftActive && !_sessionMissing") && integration.Contains("SessionLayoutRoot.Visibility == Visibility.Visible"), "header requires actual session surface");
        Check(integration.Contains("_rightPanelMetadataOwner != owner") && integration.Contains("_rightPanelSessionState = null;"), "old source/session metadata discarded by full binding equality");
        Check(integration.Contains("_rightPanelDisplayedOwner != owner") && integration.Contains("ClearRightPanelCopyHint();"), "copy hint cleared on owner change/unload");
        Check(integration.Contains("IsCurrentInsightsBinding(owner)") && integration.Contains("data.SetText(owner.SessionId);") && integration.Contains("Clipboard.SetContent(data);"), "clipboard uses current fenced session ID");
        Check(integration.Contains("ShowCopyHint(\"已复制\")") && integration.Contains("timer.IsRepeating = false;"), "success hint is lightweight and expires without polling");
        Check(integration.Contains("IsSameMessageBinding(owner.SessionId, _messageBinding?.Directory)") && integration.Contains("known?.Location?.Directory") && !integration.Contains("Subpath"), "cwd uses binding or session Location, never subpath");
        var fence = boundary.IndexOf("if (!IsCurrentMessageHistoryRead(read)) return null;", StringComparison.Ordinal);
        var accepted = boundary.IndexOf("OnRightPanelSessionMetadata(state);", StringComparison.Ordinal);
        Check(fence >= 0 && accepted > fence && boundary.IndexOf("Directory = state.Directory", StringComparison.Ordinal) < accepted, "metadata accepted only after existing async fence and directory correction");
        Check(view.Contains("IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap") && !view.Contains("new TextBox"), "directory wraps fully and is selectable, not editable");
        Check(view.Contains("Assets/OpenCode/logo-dark.svg") && view.Contains("Assets/OpenCode/logo-light.svg"), "existing official assets used in both themes");
        Check(!view.Contains("已连接") && !integration.Contains("已连接"), "no connected wording beside footer dot");
        Check(integration.Contains("CurrentServerInfo()") && flyout.Contains("IsCurrentServerInfoOwner(owner)"), "footer reuses source/revision/generation fenced backend version");
        Check(integration.Contains("ConnectionFlyout.ShowAt(_rightPanelMetadata.Footer)") && flyout.Contains("RefreshRightPanelFooter();"), "existing real connection flyout reused and footer notified on results");
        Check(integration.Contains("RightConnectionStatusHost.Visibility = Visibility.Collapsed") && sidebars.Contains("SidebarHeaderActions.Children.Insert(1, ConnectionStatusButton)"), "only redundant resident host retired; narrow header restore retained");
        Check(sidebars.Contains("RefreshRightPanelMetadata();"), "layout/home/blank surface changes reproject header/footer");
        Console.WriteLine($"PASS {_checks} right-panel presentation/wiring checks; native clipboard/flyout visuals require Windows GUI acceptance.");
    }
}
