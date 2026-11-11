using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using FluentCode.Pages.OpenCode;

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
        var recording = File.ReadAllBytes(Path.Combine(args[0], "tests/opencode-persistent-pty/Fixtures/nushell-session.vt"));
        stream = new(); decoded.Clear();
        foreach (var value in recording) decoded.Append(stream.Decode(new byte[] { value }));
        var replayed = decoded.ToString();
        Check(replayed == Encoding.UTF8.GetString(recording).Replace("\0", string.Empty, StringComparison.Ordinal)
            && replayed.Contains("\x1b[6n") && replayed.Contains("\x1b]133;") && replayed.Contains("\x1b]7;") && replayed.Contains("\x1b[?2004h"),
            "recorded nushell VT decodes byte-fragmented without PTY LF conversion, stale query rewriting, OSC 133/7 or bracketed-paste loss");
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
        Check(!promoted.Writable && promoted.NeedsInputConsent && promoted.CanActivate && promoted.Status == "运行中"
            && promoted.TerminalState == OpenCodeTerminalState.Running && promoted.Role == OpenCodePersistentPtyRole.Controller,
            "automatic server promotion preserves actual role, stays read-only and only offers the double-click activation");
        Check((promoted with { ControlConflict = true }).TerminalState == OpenCodeTerminalState.Elsewhere
            && (promoted with { ControlConflict = true }).Status == "其他客户端正在输入"
            && (promoted with { Phase = OpenCodePersistentPtyPhase.Exited, Failed = true }).TerminalState == OpenCodeTerminalState.Failed
            && !(promoted with { Phase = OpenCodePersistentPtyPhase.Exited }).CanActivate && !(promoted with { CanTakeControl = false }).CanActivate,
            "one status component: running / elsewhere ring / failed; recorded or exited output can never be activated");
        Check(OpenCodeTerminalNames.Title("explicit", "nu", "/user name") == "/user name"
            && OpenCodeTerminalNames.Title("explicit", "nu", " ") == "explicit"
            && OpenCodeTerminalNames.Title("", "nu", null) == "nu", "userTitle > explicit title > shell, with user paths preserved and empty overrides ignored");
        Check(OpenCodeTerminalNames.Title("终端", "/etc/profiles/per-user/ming/bin/nu") == "nu" && OpenCodeTerminalNames.Title("dev server", "nu") == "dev server"
            && OpenCodeTerminalNames.Title("/mnt/c/repo", "nu") == "nu" && OpenCodeTerminalNames.Title(@"C:\repo", @"C:\Program Files\PowerShell\7\pwsh.exe") == "pwsh"
            && OpenCodeTerminalNames.Title("", @"C:\x\pwsh.exe") == "pwsh"
            && OpenCodeTerminalNames.ExitText(0) == "已完成 · 退出码 0" && OpenCodeTerminalNames.ExitText(101) == "失败 · 退出码 101",
            "terminal titles prefer an explicit title, never a shell-reported path or flickering prompt helper, else the shell name; exit text follows the status component");
        Check(promoted.ConsentAction == "启用输入" && !promoted.ConsentExplanation.Contains("转为只读")
            && !promoted.ConsentExplanation.Contains("当前控制者"), "existing-controller confirmation does not falsely promise another controller will lose control");
        Check((promoted with { Role = OpenCodePersistentPtyRole.Observer }).ConsentAction == "接管终端"
            && !(promoted with { CanTakeControl = false }).NeedsInputConsent, "takeover and enable-input respect explicit host capability separately");
        Check(context != context with { Generation = 5 } && context != context with { PtyId = "other" }
            && context != context with { SessionId = "other" }, "view events retain generation, PTY and session identity");
        Check(OpenCodePersistentPtyRendererPolicy.ValidGeometry(80, 24) && !OpenCodePersistentPtyRendererPolicy.ValidGeometry(0, 24)
            && !OpenCodePersistentPtyRendererPolicy.ValidGeometry(80, 1001), "renderer bounds resource geometry");
        var rendererSource = File.ReadAllText(Path.Combine(args[0], "src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyRenderer.cs"));
        Check(rendererSource.Contains("TerminalHostMode.ChildWindow") && !rendererSource.Contains("WebView2") && !rendererSource.Contains("CoreWebView")
            && rendererSource.Contains("TerminalTextReader.ReadSelectionAsync"), "persistent renderer uses the native child HWND and non-destructive selection, not WebView");
        var nativeSource = File.ReadAllText(Path.Combine(args[0], "src/ThirdParty/WindowsTerminalControl/src/WindowsTerminalControl.WinUI/TerminalControl.cs"));
        var wheel = nativeSource[nativeSource.IndexOf("private bool ScrollViewportWithMouseWheel", StringComparison.Ordinal)..nativeSource.IndexOf("private void OnGotFocus", StringComparison.Ordinal)];
        Check(nativeSource.Contains("case NativeMethods.WmMouseWheel:") && nativeSource.Contains("ValueChanged += OnScrollBarValueChanged")
            && wheel.Contains("_terminalScrollBar.Value = top") && wheel.Contains("_wheelDelta %= 120")
            && !wheel.Contains("Resize(") && !wheel.Contains("WriteInput"), "child-HWND wheel scrolls clamped local history with high-resolution delta accumulation, never PTY input/resize");
        var viewSource = File.ReadAllText(Path.Combine(args[0], "src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyView.cs"));
        Check(!viewSource.Contains("适应窗口") && !viewSource.Contains("ConfirmTakeControlAsync") && !viewSource.Contains("接管终端\"")
            && viewSource.Contains("TerminateRequested") && viewSource.Contains("internal void RequestEnd()") && viewSource.Contains("_confirm.Visibility = Visibility.Visible; _header.Visibility = Visibility.Collapsed;"),
            "no Fit/takeover button/modal: ending a running terminal is confirmed in place of the header; close only detaches");
        Check(viewSource.Contains("Renderer.SuppressAirspace(snapshot: true)") && viewSource.Contains("Grid.SetColumn(_scrollBar, 2)") && viewSource.Contains("Grid.SetColumn(Renderer, 1)")
            && viewSource.Contains("ListExpanded ?? count >= 2"), "B2 card: menus freeze the terminal frame, scrollbar sits in its own gutter beside the HWND, list defaults collapsed for one terminal");
        var scrollBar = viewSource[viewSource.IndexOf("internal sealed class OpenCodeTerminalScrollBar", StringComparison.Ordinal)..viewSource.IndexOf("internal static class OpenCodePersistentPtyViewText", StringComparison.Ordinal)];
        Check(scrollBar.Contains("private readonly ScrollBar _bar = new()") && scrollBar.Contains("Children.Add(_bar)") && scrollBar.Contains("Orientation = Orientation.Vertical")
            && scrollBar.Contains("IndicatorMode = ScrollingIndicatorMode.MouseIndicator") && scrollBar.Contains("SmallChange = 1")
            && !scrollBar.Contains("new Border") && !scrollBar.Contains("CapturePointer") && !scrollBar.Contains("FromArgb")
            && !scrollBar.Contains("PointerPressed +=") && !scrollBar.Contains("PointerMoved +="),
            "terminal scrollbar uses the stock WinUI control/template, not painted track/thumb or custom pointer dragging");
        Check(scrollBar.Contains("Minimum = 0;") && scrollBar.Contains("Maximum = state.MaximumTop;")
            && scrollBar.Contains("ViewportSize = state.ViewHeight;") && scrollBar.Contains("Value = state.ViewTop;")
            && scrollBar.Contains("LargeChange = Math.Max(1, state.ViewHeight - 1);"),
            "native scrollbar maps terminal rows, maximum top and viewport directly, with the existing one-row-overlap page step");
        Check(scrollBar.Contains("_updating = true;") && scrollBar.Contains("finally { _updating = false; }")
            && scrollBar.Contains("ValueChanged +=") && scrollBar.Contains("if (!_updating) _renderer.ScrollTo((int)Math.Round(args.NewValue))")
            && scrollBar.Contains("_state.ViewTop - Math.Sign(delta) * 3 * Math.Max(1, Math.Abs(delta) / 120)"),
            "renderer refreshes cannot echo scrolling; native/UIA value changes and the existing gutter wheel step only scroll local history");
        Check(scrollBar.Contains("GutterWidth = 40;") && scrollBar.Contains("state.MaximumTop > 0 && state.BufferSize > 0 ? Visibility.Visible : Visibility.Collapsed")
            && !scrollBar.Contains("ActualHeight") && viewSource.Contains("Width = new GridLength(OpenCodeTerminalScrollBar.GutterWidth)")
            && viewSource.Contains("_scrollBar.Margin = new Thickness(0, 12, 0, 12)") && viewSource.Contains("Renderer.Margin = new Thickness(0, 12, 0, 12)"),
            "empty/history transitions retain the fixed 40-DIP gutter and terminal inset without depending on collapsed geometry");
        Check(viewSource.Split("TryStyle(\"OpenCodeDividerSurfaceStyle\")").Length == 3
            && viewSource.Contains("_well.Style = OpenCodeInteractionUi.TryStyle(\"OpenCodeCardOutlineStyle\")")
            && viewSource.Contains("_statusPill.Style = OpenCodeInteractionUi.TryStyle(\"OpenCodeSubtleSurfaceStyle\")"),
            "terminal card and both dividers reuse same-value named resources without changing geometry or scheme background");
        var previewSource = File.ReadAllText(Path.Combine(args[0], "src/FluentCode/Pages/OpenCode/OpenCodeShellTerminalPreview.cs"));
        var appResources = System.Xml.Linq.XDocument.Load(Path.Combine(args[0], "src/FluentCode/App.xaml"));
        var codeStyle = appResources.Descendants().Single(e => e.Name.LocalName == "Style" && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "OpenCodeCodeTextBlockStyle"));
        Check(previewSource.Contains("_command.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey)")
            && previewSource.Contains("_output.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey)")
            && previewSource.Contains("_command.IsTextSelectionEnabled = false") && !previewSource.Contains("new FontFamily")
            && codeStyle.Elements().Any(e => (string?)e.Attribute("Property") == "FontFamily" && (string?)e.Attribute("Value") == "{ThemeResource AppMonospaceFontFamily}"),
            "shell summary follows the shared user monospace resource while retaining output-only text selection");
        var renderer = File.ReadAllText(Path.Combine(args[0], "src/FluentCode/Pages/OpenCode/OpenCodePersistentPtyRenderer.cs"));
        Check(!renderer.Contains("InputIntentRequested") && renderer.Contains("else ReadOnlyInteraction?.Invoke();")
            && renderer.Contains("if (click.ClickCount == 2 && _activatable)") && renderer.Contains("click.Handled = true;")
            && renderer.Contains("_activatable = activatable && !_shellViewport"),
            "two-click takeover: read-only keystrokes are dropped, a single click only focuses/selects, the second press of a double-click (not a drag) requests input and is not passed on as word selection");
        var nativeClicks = nativeSource[nativeSource.IndexOf("private bool NativeLeftButton", StringComparison.Ordinal)..nativeSource.IndexOf("private bool Accelerator", StringComparison.Ordinal)];
        Check(nativeClicks.Contains("GetDoubleClickTime()") && nativeClicks.Contains("_nativeUpNearDown") && nativeClicks.Contains("_swallowLeftUp = true")
            && nativeSource.Contains("ScrollStateChanged?.Invoke(this, ScrollState);") && nativeSource.Contains("public bool ShowScrollBar"),
            "vendored control reports double-click only within system time/distance after a non-drag release, and publishes scroll state for the host scrollbar");
        var toolRowSource = File.ReadAllText(Path.Combine(args[0], "src/FluentCode/Pages/OpenCode/OpenCodeToolPartView.cs"));
        var shellBranch = toolRowSource[toolRowSource.IndexOf("internal void TogglePinnedDetail()", StringComparison.Ordinal)..];
        Check(shellBranch.Contains("RequestTerminal();") && !toolRowSource.Contains("_inlineRegion")
            && !toolRowSource.Contains("new OpenCodeShellTerminalPreview"),
            "shell transcript keeps only its clickable task line, with no command/output preview");
        var pages = Path.Combine(args[0], "src/FluentCode/Pages");
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
        Check(File.ReadAllText(Path.Combine(pages, "OpenCodePage.EventSync.cs")).Contains("or \"persistent-pty.updated\""),
            "updated SSE is forwarded before message-workflow filtering");
        var inventoryView = File.ReadAllText(Path.Combine(pages, "OpenCode/OpenCodePersistentPtyInventoryView.cs"));
        Check(!inventoryView.Contains("Segoe Fluent Icons") && !inventoryView.Contains("FontFamily =")
            && inventoryView.Contains("_isSelected ? \"OpenCodeSubtleSurfaceStyle\" : \"OpenCodeTransparentSurfaceStyle\""),
            "terminal chrome uses the native icon font fallback and existing selected/unselected surface resources");
        Check(inventoryView.Contains("rows.Select(OpenCodeTerminalUi.Entry).Concat(_shellEntries") && inventoryView.Contains("if (!changed) return;")
            && inventoryView.Contains("Reconcile in place") && inventoryView.Contains("class OpenCodeTerminalSwitcher"),
            "one-row strip lists PTYs and agent commands once, shared with the overlay list, and reconciles chips in place during refresh");
        Check(lifecycle.Contains("ActivationRequested += request => _ = ActivatePersistentInputAsync(owner, request)") && !lifecycle.Contains("AcquirePersistentInputAsync")
            && lifecycle.Contains("if (owner.ControlConflict) await owner.Controller.TakeControlAsync(") && lifecycle.Contains("TryTakeControlAsync(owner.Lifetime.Token)"),
            "activation acquires without force; only a known conflict turns the same deliberate gesture into the explicit takeover");
        Check(lifecycle.Contains("if (request.IsTerminalResponse)") && !lifecycle.Contains("SendTerminalResponseAsync"),
            "persistent daemon owns DSR/CPR replies; native live/checkpoint query responses never become duplicate PTY input");
        Check(!lifecycle.Contains("TerminalViewHost") && !lifecycle.Contains("ConversationSurface")
            && lifecycle.Contains("ShowPersistentPtyOverlay(view, ptyId);") && lifecycle.Contains("HidePersistentPtyOverlay(owner.View, animate: restoreFocus); owner.View.Renderer.Dispose();"),
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
