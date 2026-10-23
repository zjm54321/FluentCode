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

    private static void Main()
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

        Console.WriteLine($"PASS {_checks} right-panel presentation checks; native clipboard/flyout visuals require Windows GUI acceptance.");
    }
}
