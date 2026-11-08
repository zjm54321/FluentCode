using System;
using System.IO;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Browser;

internal static partial class FeedbackTooltipProbe
{
    internal static string Format(string announcement, string? detail = null) => FailureTooltip(announcement, detail);
}

internal static partial class CapabilityTooltipProbe
{
    internal static string Format(string reason) => StateTooltip(new(OpenCodeCapabilityAvailability.Unknown, reason));
}

internal static partial class BrowserTooltipProbe
{
    internal static string Format(OpenCodeBrowserAvailability availability, string? reason) => CompactTooltipStatus(availability, reason);
}

internal static partial class Program
{
    private static void TooltipCopy(string repo)
    {
        foreach (var (announcement, detail, tooltip) in new (string, string?, string)[]
        {
            ("复制失败；可选择指纹后手动复制", null, "复制失败"),
            ("打开失败", "请检查安装与目录访问权限", "应用或目录不可访问"),
            ("撤回未确认", "请查看提示并核对对话记录", "撤回未确认"),
            ("复制失败", "请选择代码复制", "复制失败"),
            ("复制失败", "剪贴板暂不可用，请重试", "剪贴板不可用"),
            ("复制失败", "请稍后重试", "复制失败"),
            ("复制失败", "拒绝访问\nC:\\src\\完整文件名.cs", "拒绝访问\nC:\\src\\完整文件名.cs"),
        })
            Check(FeedbackTooltipProbe.Format(announcement, detail) == tooltip, "failure tooltip projection: " + tooltip);

        foreach (var (reason, tooltip) in new[]
        {
            (OpenCodeCapabilityStatus.LspUnavailableReason, "LSP 状态不可用"),
            ("MCP 状态目录可用；不表示所有 MCP 已连接", "MCP 状态可用"),
            ("服务器插件目录可用；配置存在不等于实际运行", "插件列表可用"),
            ("原生 To Do 由 experimental.todo 与当前会话 todowrite 记录确认", "待办状态未知"),
            ("状态查询失败（HTTP 503），无法确认", "状态查询失败（HTTP 503），无法确认"),
        })
            Check(CapabilityTooltipProbe.Format(reason) == tooltip, "capability tooltip projection: " + tooltip);

        foreach (var (availability, reason, tooltip) in new (OpenCodeBrowserAvailability, string?, string)[]
        {
            (OpenCodeBrowserAvailability.Available, "智能体可以使用浏览器（45 个操作）", "已连接"),
            (OpenCodeBrowserAvailability.Attaching, null, "连接中"),
            (OpenCodeBrowserAvailability.Replaced, "这个会话的浏览器现在由另一台电脑 / 另一个窗口提供", "已由其他应用接管"),
            (OpenCodeBrowserAvailability.Reconnecting, "智能体暂时不能使用浏览器：接入失败，请重试", "连接失败"),
            (OpenCodeBrowserAvailability.Unsupported, null, "当前版本不支持"),
            (OpenCodeBrowserAvailability.Detached, "请求失败：服务拒绝访问", "请求失败：服务拒绝访问"),
        })
            Check(BrowserTooltipProbe.Format(availability, reason) == tooltip, "browser tooltip projection: " + tooltip);

        Check(OpenCodeComposerInputPolicy.AttachmentLimitDetails(2, 2048, 10, 25 * 1024 * 1024, 10 * 1024 * 1024)
            == "附件 2 个（2 KB）", "attachment tooltip shows count and size, without a limit list");

        string Read(string path) => File.ReadAllText(Path.Combine(repo, "src/FluentCode", path));
        var composer = Read("Pages/OpenCode/OpenCodeComposerView.cs");
        Check(composer.Contains("ToolTipService.SetToolTip(item, row.Selectable ? \"/\" + row.Name")
            && composer.Contains("AutomationProperties.SetName(item, automation)"), "command-row tooltip is concise without changing its accessible name");
        Check(!composer.Contains("Text = image.Details"), "attachment tooltip does not expose raw selection metadata");
        var ring = Read("Pages/OpenCode/OpenCodeContextRing.cs");
        Check(ring.Contains("AutomationProperties.SetHelpText(_button, Summary)")
            && !ring.Contains("ToolTipService.SetToolTip(_button, Summary)"), "context ring preserves its accessible summary independently");
        var toolbar = Read("Pages/OpenCodePage.ComposerToolbar.cs");
        Check(toolbar.Contains("ToolTipService.SetToolTip(BlankAgentButton, description)")
            && !toolbar.Contains("\"智能体说明不可用\"") && !toolbar.Contains(" · 下次发送生效"), "unknown agents have no tooltip and pending picker tooltips have no scope explanation");
        var control = Read("Pages/OpenCode/OpenCodeSessionControlCard.cs");
        Check(control.Contains("AutomationProperties.SetHelpText(button, scope)")
            && control.Contains("tooltip ?? name + (readOnly ? \"（只读）\" : string.Empty)"), "session controls keep accessibility scope outside tooltips");
        var browser = Read("Pages/OpenCode/OpenCodeBrowserButton.cs");
        Check(browser.Contains("_reason.Text = _tipStatus.Text") && browser.Contains("CompactTooltipStatus(_availability, workspace?.AvailabilityReason)"), "browser hover copy does not alter the visible connection reason");
        var strip = Read("Pages/OpenCode/OpenCodeBrowserStrip.cs");
        Check(strip.Contains("Children = { _label, _address, _large }")
            && strip.Contains("_address.Text = _page.PreviewPath ?? _page.Url")
            && strip.Contains("AutomationProperties.SetName(_show, _label.Text + \" · \" + _status.Text)"), "browser hover keeps full title/address and thumbnail without changing its accessible name");
        var page = Read("Pages/OpenCodePage.xaml.cs");
        Check(page.IndexOf("_connectionStatusDetail = tooltip;", StringComparison.Ordinal) < page.IndexOf("tooltip = text;", StringComparison.Ordinal)
            && page.Contains("ToolTipService.SetToolTip(ConnectionStatusButton, tooltip)"), "connection details remain intact while hover only shows the status");
        var preview = Read("Pages/OpenCode/OpenCodeBrowserPreviewView.cs");
        Check(preview.Contains("ToolTipService.SetToolTip(_source, _readError)"), "preview tooltip keeps dynamic read failures");
        var feedback = Read("Motion/ActionFeedback.cs");
        Check(feedback.Contains("Announce(control, announcement + (detail is null ? string.Empty : \"：\" + detail))"), "failure tooltip projection does not change announcements");

        var terminal = Read("Pages/OpenCode/OpenCodePersistentPtyView.cs");
        Check(terminal.Contains("(_find, \"查找（Ctrl+Shift+F）\")")
            && terminal.Contains("(_copy, \"复制（Ctrl+Shift+C）\")")
            && terminal.Contains("(_paste, \"粘贴（Ctrl+Shift+V）\")")
            && terminal.Contains("(_back, \"关闭终端（Ctrl+`）\")")
            && terminal.Contains("\"关闭窗口 · Ctrl+`（终端继续运行）\""), "terminal action tooltips use shortcuts without changing their accessible names");
        Check(terminal.Contains("(_menu, \"终端列表（Ctrl+Shift+L）\")")
            && terminal.Contains("case VirtualKey.L: SetListExpanded(_listColumn.Width.Value == 0)"), "terminal list tooltip matches its real keyboard action");

        var tabs = Read("Pages/OpenCodePage.Tabs.cs");
        var newTab = Read("Pages/OpenCodePage.NewSessionTab.cs");
        var nativeCommands = Read("Pages/OpenCode/OpenCodeComposerController.cs");
        var nativeActions = Read("Pages/OpenCodePage.BuiltinActions.cs");
        Check(tabs.Contains("ToolTipService.SetToolTip(add, \"新建会话（/new）\")")
            && tabs.Contains("SessionTabs_AddTabButtonClick(TabView sender, object args) => OpenNewSessionTab()")
            && newTab.Contains("RequestNewSessionTab() => DispatcherQueue.TryEnqueue(OpenNewSessionTab)")
            && newTab.Contains("ToolTipService.SetToolTip(item, \"新会话\")")
            && nativeCommands.Contains("Native(OpenCodeComposerClientActionKind.NewSession, \"new\")"), "new action advertises /new while existing blank tabs retain value-only titles");
        var xaml = Read("Pages/OpenCodePage.xaml");
        Check(xaml.Contains("AutomationProperties.Name=\"首页\" ToolTipService.ToolTip=\"首页（/sessions）\"")
            && nativeCommands.Contains("Native(OpenCodeComposerClientActionKind.ChooseSession, \"sessions\")")
            && nativeActions.Contains("DispatcherQueue.TryEnqueue(ShowHomePage)"), "home action tooltip matches the native sessions route");
        Check(xaml.Contains("AutomationProperties.Name=\"展开输入框\" ToolTipService.ToolTip=\"展开输入框（/editor）\"")
            && nativeCommands.Contains("Native(OpenCodeComposerClientActionKind.EditDraft, \"editor\")")
            && Read("Pages/OpenCodePage.FullScreenComposer.cs").Contains("private void QueueFullScreenComposer(OpenCodeComposerClientActionTicket ticket)"), "expand tooltip advertises the same native draft-editor action");
        Check(Read("Pages/OpenCode/OpenCodeHomeView.cs").Contains("ToolTipService.SetToolTip(create, \"新建会话（/new）\")"), "home new-session action shows its native shortcut");

        var overlay = Read("Pages/OpenCode/OpenCodeBrowserOverlay.cs");
        Check(overlay.Contains("ToolTipService.SetToolTip(close, \"关闭（Esc）\")")
            && overlay.Contains("\"关闭浏览器浮窗\", () => _workspace?.HideOverlay()")
            && overlay.Contains("key.Key == VirtualKey.Escape"), "browser close hover uses Esc and preserves its accessible name");
        Check(overlay.Contains("ToolTipService.SetToolTip(_address, \"开发者工具（F12）\")"), "external browser developer-tools hint is an action shortcut phrase");

        var contextMenu = Read("Pages/OpenCode/OpenCodeContextMenu.cs");
        var messageMenu = Read("Pages/OpenCode/OpenCodeMessageContextMenu.cs");
        Check(contextMenu.Contains("command.Tooltip")
            && messageMenu.Contains("Tooltip = \"撤回（/undo）\"")
            && messageMenu.Contains("Tooltip = \"恢复撤回（/redo）\"")
            && nativeCommands.Contains("Native(OpenCodeComposerClientActionKind.Undo, \"undo\")")
            && nativeCommands.Contains("Native(OpenCodeComposerClientActionKind.Redo, \"redo\")"), "rollback action tooltips use native shortcuts without adding visible menu hints");
        Check(Read("Pages/OpenCode/OpenCodeToolPartView.cs").Contains("ToolTipService.SetToolTip(_backgroundButton, \"转入后台\")"), "background action does not advertise an unbound shortcut");
    }
}
