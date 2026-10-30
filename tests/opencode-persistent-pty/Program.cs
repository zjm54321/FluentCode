using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    { if (!value) throw new Exception(name); _checks++; Console.WriteLine("PASS " + name); }
    private static void Main(string[] args)
    {
        var stream = new OpenCodePersistentPtyVtStream();
        var decoded = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes("\u001b[31m中文 😀\0suffix\u001b[0m")) decoded.Append(stream.Decode(new byte[] { value }));
        Check(decoded.ToString() == "\u001b[31m中文 😀suffix\u001b[0m", "byte-fragmented UTF-8 and VT retained; NUL does not truncate subsequent output");
        stream = new(); decoded.Clear();
        foreach (var value in Encoding.UTF8.GetBytes("before\u001b]52;c;c2VjcmV0\u001b\\after\u001b]0;title\aend")) decoded.Append(stream.Decode(new byte[] { value }));
        Check(decoded.ToString() == "beforeafter\u001b]0;title\aend", "fragmented OSC52 cannot overwrite the clipboard; other OSC is preserved");
        var context = new OpenCodePersistentPtyViewContext(Guid.NewGuid(), 4, "session", "pty-opaque");
        foreach (var phase in Enum.GetValues<OpenCodePersistentPtyPhase>())
        foreach (var role in Enum.GetValues<OpenCodePersistentPtyRole>())
        foreach (var authorized in new[] { false, true })
        {
            var state = new OpenCodePersistentPtyPresentation(context, "terminal", "/work", phase, role, "智能体", true, authorized);
            Check(state.Writable == (phase == OpenCodePersistentPtyPhase.Live && role == OpenCodePersistentPtyRole.Controller && authorized),
                $"input role/phase/authorization gate {phase}/{role}/{authorized}");
            Check(state.NeedsInputConsent == (phase == OpenCodePersistentPtyPhase.Live && (role == OpenCodePersistentPtyRole.Observer || !authorized)),
                $"explicit consent remains available without automatic promotion {phase}/{role}/{authorized}");
        }
        var promoted = new OpenCodePersistentPtyPresentation(context, "terminal", "/work", OpenCodePersistentPtyPhase.Live,
            OpenCodePersistentPtyRole.Controller, "当前连接", true, false);
        Check(!promoted.Writable && promoted.NeedsInputConsent && promoted.Status == "控制权已分配，输入未启用"
            && promoted.Role == OpenCodePersistentPtyRole.Controller, "automatic server promotion preserves actual role without claiming manual control");
        Check(promoted.ConsentAction == "启用输入" && !promoted.ConsentExplanation.Contains("转为只读")
            && !promoted.ConsentExplanation.Contains("当前控制者"), "existing-controller confirmation does not falsely promise another controller will lose control");
        Check((promoted with { Role = OpenCodePersistentPtyRole.Observer }).ConsentAction == "接管终端"
            && !(promoted with { CanTakeControl = false }).NeedsInputConsent, "takeover and enable-input respect explicit host capability separately");
        Check(context != context with { Generation = 5 } && context != context with { PtyId = "other" }
            && context != context with { SessionId = "other" }, "view events retain generation, PTY and session identity");
        Check(OpenCodePersistentPtyRendererPolicy.ValidGeometry(80, 24) && !OpenCodePersistentPtyRendererPolicy.ValidGeometry(0, 24)
            && !OpenCodePersistentPtyRendererPolicy.ValidGeometry(80, 1001), "renderer bounds resource geometry");
        var rendererSource = File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/Pages/OpenCode/OpenCodePersistentPtyRenderer.cs"));
        Check(rendererSource.Contains("TerminalHostMode.ChildWindow") && !rendererSource.Contains("WebView2") && !rendererSource.Contains("CoreWebView")
            && rendererSource.Contains("TerminalTextReader.ReadSelectionAsync"), "persistent renderer uses the native child HWND and non-destructive selection, not WebView");
        var pages = Path.Combine(args[0], "src/AIFrontend/Pages");
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(pages, "OpenCodePage.xaml"));
        System.Xml.Linq.XElement Named(string name) => xaml.Descendants().Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        var button = Named("PersistentPtyButtonSlot"); var strip = Named("PersistentPtyStripSlot");
        Check(button.Parent == Named("BrowserButtonSlot").Parent && (string?)button.Attribute("Grid.Column") == "2"
            && strip.ElementsBeforeSelf().Any(e => e == Named("BrowserStripSlot")) && strip.Ancestors().Contains(Named("RightSessionCard")),
            "terminal entry sits beside the browser button with its strip below the browser strip in the right session card");
        var host = Named("PersistentPtyOverlayHost");
        Check(host.Parent == Named("PageRoot") && host.ElementsBeforeSelf().Contains(Named("BrowserOverlayHost"))
            && (string?)host.Attribute("Grid.Row") == "1" && (string?)host.Attribute("Visibility") == "Collapsed",
            "terminal floating card is a page-level sibling of the browser card, hidden by default");
        var tree = File.ReadAllText(Path.Combine(pages, "OpenCodePage.AgentTree.cs"));
        Check(tree.Contains("LeftAgentTreeSlot.Children.Add(_agentTree);") && !tree.Contains("_persistentPtyInventoryView"),
            "left pane holds only the agent tree");
        var lifecycle = File.ReadAllText(Path.Combine(pages, "OpenCodePage.PersistentPty.cs"));
        Check(!lifecycle.Contains("TerminalViewHost") && !lifecycle.Contains("ConversationSurface")
            && lifecycle.Contains("ShowPersistentPtyOverlay(view, ptyId);") && lifecycle.Contains("HidePersistentPtyOverlay(owner.View); owner.View.Renderer.Dispose();"),
             "persistent terminal uses the shared floating card without replacing conversation");
        var glue = File.ReadAllText(Path.Combine(pages, "OpenCodePage.PersistentPtyOverlay.cs"));
        Check(glue.Contains("OpenCodeBrowserOverlayLayout.Place(") && glue.Contains("workspace.HideOverlay();")
            && glue.Contains("BrowserOverlayHost.Visibility == Visibility.Visible) CloseTerminalOverlay(restoreFocus: false)")
            && glue.Contains("VirtualKey.Escape") && glue.Contains("owner.View.Renderer.HasTerminalFocus") && glue.Contains("IsWithin(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, owner.View.Renderer)"),
            "browser geometry, one floating card at a time, Esc closes except while the terminal program has focus");
        foreach (var file in new[] { "OpenCode/OpenCodePersistentPtyView.cs", "OpenCode/OpenCodePersistentPtyInventoryView.cs", "OpenCode/OpenCodePersistentPtyOverlay.cs" })
        {
            var text = File.ReadAllText(Path.Combine(pages, file));
            Check(!text.Contains("点击只读查看") && !text.Contains("与命令输出终端分开") && !text.Contains("断开查看但不结束终端") && !text.Contains("不会向终端发送")
                && !text.Contains("明确请求") && !text.Contains("也不保证"), "no explanatory sentences in " + file);
        }
        Console.WriteLine($"PASS {_checks} pure presentation/security/vendor checks; no backend or native execution.");
    }
}
