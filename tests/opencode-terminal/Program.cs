using System;
using System.IO;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); _checks++; }
    private static void Main(string[] args)
    {
        var state = new OpenCodeTerminalState();
        var shell = new OpenCodeTreeShell("sh_real", "printf real", "/work", OpenCodeTreeShellKind.Background, OpenCodeTreeStatus.Running, null, null);
        state.Open(shell);
        Check(state.ShellId == "sh_real" && state.Command == "printf real" && state.Running && state.StatusText == "运行中", "open uses actual shell ID, command and running state");
        state.ApplyInfo(new() { Status = "exited", Exit = 7, Time = new() { Started = 1700000000000, Completed = 1700000001000 } });
        Check(!state.Running && state.StatusText == "已退出 · 退出码 7" && state.TimeText.Contains("开始：") && state.TimeText.Contains("结束："), "exited status, nonzero exit and verified timestamps");
        state.ApplyInfo(new() { Status = "exited" });
        Check(state.StatusText == "已退出 · 退出码不可用", "missing exit is never zero or success");
        state.ApplyInfo(new() { Status = "future" });
        Check(state.StatusText == "状态不可用" && !state.Running, "unknown state remains unknown");
        state.ApplyOutput(new() { Output = "真实\n", Cursor = 7, Size = 12, Truncated = false });
        Check(state.Output == "真实\n" && state.Cursor == 7 && state.HasMore, "output uses server byte cursor rather than UTF-16 string length");
        state.ApplyOutput(new() { Output = "tail", Cursor = 12, Size = 12, Truncated = false });
        Check(state.Output == "真实\ntail" && !state.HasMore, "pages append verbatim and drain after exit");
        state.ApplyOutput(new() { Output = string.Empty, Cursor = 12, Size = 12, Truncated = false });
        Check(state.Output == "真实\ntail", "empty poll does not erase output");
        var rejected = false;
        try { state.ApplyOutput(new() { Output = "bad", Cursor = 11, Size = 12, Truncated = false }); } catch (OpenCodeProtocolException) { rejected = true; }
        Check(rejected, "regressing cursor rejected");
        rejected = false;
        try { state.ApplyOutput(new() { Output = "bad", Cursor = 12, Size = 20, Truncated = false }); } catch (OpenCodeProtocolException) { rejected = true; }
        Check(rejected, "non-progress page cannot loop forever");
        Check(OpenCodeTerminalState.ShouldFollow(true, 10, 100) && !OpenCodeTerminalState.ShouldFollow(false, 10, 100)
            && OpenCodeTerminalState.ShouldFollow(false, 99, 100), "follow output until user scrolls up; reaching bottom resumes follow");
        state.Close();
        Check(state.ShellId is null && !state.Running && !state.HasMore, "back closes selection and ends following");
        state.Open(shell with { Id = "sh_other", Output = "retained", Status = OpenCodeTreeStatus.Succeeded, Exit = "0" });
        Check(state.Cursor == 0 && state.Output == "retained" && state.ShellId == "sh_other", "switch shell resets cursor and preserves actual historical output until REST read");
        state.ApplyOutput(new() { Output = "authoritative", Cursor = 13, Size = 13, Truncated = true });
        Check(state.Output == "authoritative" && state.Notice == "后端标记输出已截断", "first REST page replaces cached output rather than duplicating it");

        var root = args[0];
        string Read(string path) => File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages", path));
        var tree = Read("OpenCode/OpenCodeAgentTreeView.cs");
        var page = Read("OpenCodePage.TerminalView.cs");
        var view = Read("OpenCode/OpenCodeTerminalView.cs");
        var xaml = Read("OpenCodePage.xaml");
        var treePage = Read("OpenCodePage.AgentTree.cs");
        Check(tree.Contains("ShellRequested?.Invoke(shell.Id)") && !tree.Contains("TerminalInvoked")
            && treePage.Contains("_agentTree.ShellRequested += OpenTerminalView") && !tree.Contains("x:Name=\"Details\"")
            && !treePage.Contains("ReadAgentTreeOutputAsync"), "shared tree hook opens full-center view; inline details and old reader removed");
        Check(page.Contains("ConversationSurface.Visibility = Visibility.Collapsed") && page.Contains("TerminalViewHost.Visibility = Visibility.Visible")
            && page.Contains("ConversationSurface.Visibility = Visibility.Visible") && page.Contains("TerminalViewHost.Visibility = Visibility.Collapsed"), "open replaces conversation; back restores its existing controls");
        Check(xaml.IndexOf("x:Name=\"TerminalViewHost\"", StringComparison.Ordinal) > xaml.IndexOf("x:Name=\"HomePageHost\"", StringComparison.Ordinal)
            && xaml.Contains("Loaded=\"TerminalViewHost_Loaded\""), "terminal host is center-only sibling, not a dock child");
        Check(view.Contains("<Button x:Name=\"Back\"") && view.Contains("&#xE72B;") && page.Contains("VirtualKey.Escape")
            && page.Contains("CloseTerminalView()"), "native back button plus page-wide Escape return navigation");
        Check(view.Contains("<RichTextBlock x:Name=\"Output\" IsTextSelectionEnabled=\"True\"") && view.Contains("<ScrollViewer x:Name=\"Scroll\"")
            && view.Contains("ShellTerminalBackground") && view.Contains("ShellTerminalForeground") && view.Contains("ShellTerminalPrompt")
            && view.Contains("Cascadia Mono, Consolas"), "native selectable read-only output, scroll viewer and existing Campbell/high contrast palette");
        Check(view.Contains("_scroll.ViewChanging +=") && view.Contains("if (_follow) _scroll.ChangeView") && view.Contains("if (_text == state.Output) return"), "user scroll-up suspends auto-follow; unchanged polls preserve selection");
        Check(page.Contains("IsTreeCurrent(binding)") && page.Contains("version == _terminalVersion") && page.Contains("CreateLinkedTokenSource")
            && page.Contains("Unloaded +=") && page.Contains("_terminalCts?.Cancel()"), "session/source/unload/close invalidate pending reads");
        Check(page.Contains("while (_terminalState.HasMore && Current())") && page.Contains("ReadTreeShellOutputAsync")
            && !page.Contains("CreateShell") && !page.Contains("/pty"), "verified output paging only; no terminal/session mutations or unrelated PTY API");
        Console.WriteLine($"PASS {_checks} focused terminal checks");
    }
}
