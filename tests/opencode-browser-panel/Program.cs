using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode.Browser;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    private static void Main(string[] args)
    {
        for (var count = 0; count < 5; count++)
        {
            Check(OpenCodeBrowserPanelState.ColoredIcon(count) == (count > 0), "color follows real inventory");
            Check(OpenCodeBrowserPanelState.ShowCount(count) == (count >= 2), "badge only from two pages");
            Check(OpenCodeBrowserPanelState.TooltipTitle(count) == (count == 0 ? "浏览器 · 打开浏览器页面" : $"浏览器 · {count} 个页面 · 点击展开缩略图"), "tooltip exact count/copy");
            var state = new OpenCodeBrowserPanelState();
            Check(!state.ShowCommands(count), "collapsed hides both row commands and strip");
            state.Toggle(); Check(state.ShowCommands(count) == (count > 0), "expanded commands require pages");
        }
        foreach (var availability in Enum.GetValues<OpenCodeBrowserAvailability>())
        {
            var brush = availability switch
            {
                OpenCodeBrowserAvailability.Available => "SystemFillColorSuccessBrush",
                OpenCodeBrowserAvailability.Attaching or OpenCodeBrowserAvailability.Reconnecting => "SystemFillColorCautionBrush",
                _ => "SystemFillColorCriticalBrush",
            };
            Check(OpenCodeBrowserPanelState.StatusBrushKey(availability) == brush, "BI2 success/caution/critical status dot " + availability);
            var text = OpenCodeBrowserPanelState.TooltipStatus(availability, "真实的原因");
            Check(text == (availability == OpenCodeBrowserAvailability.Available ? "智能体可以使用浏览器（45 个操作）" : "真实的原因"), "truthful availability tooltip");
        }
        Check(OpenCodeBrowserPanelState.TooltipStatus(OpenCodeBrowserAvailability.Detached, null).Contains("尚未接入"), "unknown status is not a failure count");
        Check(OpenCodeBrowserPanelState.TooltipStatus(OpenCodeBrowserAvailability.Attaching, null).Contains("正在接入"), "BI2 attaching has full state text");
        Check(OpenCodeBrowserPanelState.TooltipStatus(OpenCodeBrowserAvailability.Reconnecting, null).Contains("正在重连"), "BI2 reconnecting has full state text");
        Check(OpenCodeBrowserPanelState.TooltipStatus(OpenCodeBrowserAvailability.Replaced, null).Contains("另一个桌面应用"), "BI2 replaced has full state text");
        Check(OpenCodeBrowserPanelState.TooltipStatus(OpenCodeBrowserAvailability.Unsupported, null).Contains("不支持"), "BI2 unsupported has full state text");
        Check(OpenCodeBrowserPanelState.ReclaimLabel(OpenCodeBrowserAvailability.Replaced) == "改为在这里提供", "replace offers reclaim");
        Check(OpenCodeBrowserPanelState.ReclaimLabel(OpenCodeBrowserAvailability.Reconnecting) == "立即重连", "disconnect offers reconnect");
        Check(OpenCodeBrowserPanelState.ReclaimLabel(OpenCodeBrowserAvailability.Unsupported) is null, "unsupported does not invent reconnect");

        var a = new FakeWorkspace("a"); var b = new FakeWorkspace("b");
        var states = new Dictionary<IOpenCodeBrowserWorkspace, OpenCodeBrowserPanelState> { [a] = new(), [b] = new() };
        states[a].Expand(); Check(states[a].Expanded && !states[b].Expanded, "expansion isolated across sessions");
        states[b].AgentActivated(); Check(states[b].Expanded && b.Shown is null && b.ShowCalls == 0, "agent activation expands without opening overlay");
        states[a].Toggle(); Check(!states[a].Expanded && states[b].Expanded, "return preserves each session choice");
        var replacement = new FakeWorkspace("a"); states[replacement] = new();
        Check(!states[replacement].Expanded, "replacement source cannot inherit unrelated same-id session");
        var userPage = new FakePage(false); var agentPage = new FakePage(true);
        a.Inventory.Add(userPage); a.Inventory.Add(agentPage);
        Check(OpenCodeBrowserPanelState.AgentOpened(a).SequenceEqual(new[] { agentPage }), "close-agent filter uses origin, not current activity");
        foreach (var page in OpenCodeBrowserPanelState.AgentOpened(a)) page.Close();
        Check(agentPage.Closed && !userPage.Closed, "agent-only close preserves user page");
        Check(OpenCodeBrowserPanelState.PreviewKind("/server/doc.md") == "MD", "preview type is path extension");
        Check(OpenCodeBrowserPanelState.PageState(userPage, true) == "正在浮窗中显示", "overlay indicator clears with shown state");

        var throttle = new OpenCodeBrowserPreviewThrottle(); var now = DateTimeOffset.UtcNow;
        Check(throttle.TryBegin(now), "first preview allowed");
        throttle.Invalidate(); Check(!throttle.TryBegin(now.AddSeconds(3)), "never concurrent");
        throttle.Complete(); Check(!throttle.TryBegin(now.AddMilliseconds(1999)), "two-second minimum");
        Check(throttle.TryBegin(now.AddSeconds(2)), "dirty refresh at boundary"); throttle.Complete();
        Check(!throttle.TryBegin(now.AddSeconds(8)), "unchanged pages keep last frame");
        throttle.Invalidate(); Check(throttle.TryBegin(now.AddSeconds(8)), "new changes refresh"); throttle.Complete();
        throttle.Invalidate(); Check(!throttle.TryBegin(now.AddSeconds(9)), "failed attempts still throttled");
        throttle.Complete(now.AddSeconds(10));
        Check(!throttle.TryBegin(now.AddSeconds(11)), "capture completion also spaces the next attempt");
        Check(throttle.TryBegin(now.AddSeconds(12)), "change retained during cooldown"); throttle.Complete();
        var folder = Path.Combine(Path.GetTempPath(), "aif-browser-panel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pref = new OpenCodeBrowserDiscoveryPreference(Path.Combine(folder, "discovery.txt"));
            Check(!pref.Seen(), "new installation shows NEW"); pref.MarkSeen();
            Check(new OpenCodeBrowserDiscoveryPreference(Path.Combine(folder, "discovery.txt")).Seen(), "first click persists across instances");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        string Source(string path) => File.ReadAllText(Path.Combine(args[0], "src/AIFrontend/" + path));
        var panel = Source("Pages/OpenCodePage.BrowserPanel.cs"); var strip = Source("Pages/OpenCode/OpenCodeBrowserStrip.cs");
        var agentHandler = panel.Split("private void BrowserPanelAgentActivated")[1].Split("private void BrowserPanelChanged")[0];
        Check(!agentHandler.Contains("workspace.Show("), "STATIC: agent event does not show overlay");
        Check(panel.Contains("workspace.PageActivatedByAgent -=") && panel.Contains("workspace.AvailabilityChanged -=") && strip.Contains("_page.PropertyChanged -= Changed"), "STATIC: release subscriptions");
        Check(strip.Contains("new ItemsRepeater") && strip.Contains("new ScrollView") && strip.Contains("Orientation.Horizontal"), "STATIC: native horizontal repeater");
        Check(strip.Contains("FromMilliseconds(400)") && strip.Contains("AnimationsEnabled") && strip.Contains("FromMilliseconds(200)"), "STATIC: hover delay and reduced-motion gate");
        Check(panel.Contains("workspace.OpenPage(\"about:blank\")") && panel.Contains("manager.ReclaimAsync(workspace.SessionId)"), "STATIC: real engine and peer APIs");
        Check(!panel.Contains("OpenCodeConnectionKind.Wsl"), "STATIC: browser does not depend on WSL mode");
        var button = Source("Pages/OpenCode/OpenCodeBrowserButton.cs");
        var wsl = Source("Pages/OpenCode/OpenCodeWslQuickAccessView.cs");
        Check(button.Contains("Width = 40, Height = 36, MinWidth = 0, Padding = new Thickness(0)") && wsl.Contains("Width = 40, Height = 36, MinWidth = 0, Padding = new Thickness(0)"), "STATIC BI1: browser and WSL primary targets share size and padding");
        Check(button.Contains("FontSize = 18") && wsl.Contains("FontSize = 18"), "STATIC BI1: browser and WSL glyph size matches");
        Check(button.Contains("ThemeResource ButtonBorderBrush") && button.Contains("ThemeResource ButtonBorderThemeThickness") && button.Contains("ThemeResource ControlCornerRadius") && !button.Contains("_toggle.BorderThickness = new Thickness(0)"), "STATIC BI1: native button frame is retained, not stripped");
        Check(button.Contains("ToggleButtonBackgroundChecked\"] = Brush(\"SubtleFillColorSecondaryBrush\")") && !button.Contains("_bar"), "STATIC BI1: subtle checked fill without selection underline");
        Check(button.Contains("ms-appx:///Assets/OpenCode/browser.svg") && button.Contains("AccessibilitySettings().HighContrast") && !button.Contains("LinearGradientBrush"), "STATIC BI1: standard multicolor browser mark with high-contrast fallback, not accent tint");
        Check(button.Contains("var colored = _toggle.IsChecked == true && OpenCodeBrowserPanelState.ColoredIcon(_pages)") && button.Contains("_globe.Visibility = colored ? Visibility.Collapsed : Visibility.Visible"), "STATIC BI1: multicolor requires selected/expanded state and returns to native neutral glyph on collapse");
        var render = button.Split("public void Render(")[1];
        Check(render.IndexOf("_toggle.IsChecked =", StringComparison.Ordinal) < render.IndexOf("RefreshTheme();", StringComparison.Ordinal), "STATIC BI1: Render refreshes icon color after assigning the current selection state");
        Check(strip.Contains("Width = 96; Height = 60") && strip.Contains("ExpandedHeight = 72") && strip.Contains("Width = 254, Height = 159"), "STATIC: mini previews stay compact until hover");
        Check(strip.Contains("向左滚动浏览器页面") && strip.Contains("向右滚动浏览器页面") && strip.Contains("PointerWheelChangedEvent") && strip.Contains("IsHorizontalMouseWheel"), "STATIC: compact scroll arrows preserve horizontal and vertical wheel input");
        Check(strip.Contains("HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden") && strip.Contains("HorizontalScrollMode = ScrollingScrollMode.Enabled") && !strip.Contains("HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Auto"), "STATIC: arrows replace scrollbar rail without disabling horizontal gestures");
        Check(button.Contains("_statusButton = new()") && button.Contains("VerticalAlignment = VerticalAlignment.Top") && button.Contains("_statusDot = new() { Width = 6, Height = 6") && !button.Contains("Text = \"断开\""), "STATIC BI2: small top-right dot replaces disconnected text");
        Check(button.Contains("_statusButton.Visibility = workspace is not null ? Visibility.Visible : Visibility.Collapsed"), "STATIC BI2: attached and unavailable sessions both show status on button");
        Check(button.Contains("AutomationProperties.SetName(_statusButton, \"浏览器连接状态 · \" + _tipStatus.Text)") && button.Contains("ToolTipService.SetToolTip(_statusButton, _tipStatus.Text)") && button.Contains("AutomationProperties.SetName(_toggle, _tipTitle.Text + \" · \" + _tipStatus.Text)"), "STATIC BI2: dot tooltip and automation names carry full state");
        Check(button.Contains("_statusButton.Flyout = _availabilityFlyout") && button.Contains("_statusDot.Background = Brush(OpenCodeBrowserPanelState.StatusBrushKey(_availability))"), "STATIC BI2: themed dot retains real reconnect/reclaim flyout");
        Check(button.Contains("_count = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top"), "STATIC BI2: page count stays separate from top-right status and bottom accent");
        Console.WriteLine($"PASS {_checks} browser-panel checks (pure policy + explicitly static composition/style fences)");
    }

    private sealed class FakePage(bool agent) : IOpenCodeBrowserPage
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Url => "https://example.test";
        public string Title => "真实页面";
        public bool Loading => false;
        public string? LoadError => null;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool OpenedByAgent => agent;
        public string? AgentActivity => null;
        public bool UserTakeover { get; set; }
        public string? PreviewPath => null;
        public bool Closed { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Navigate(string url) { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void Stop() { }
        public void Close() => Closed = true;
    }
    private sealed class FakeWorkspace(string id) : IOpenCodeBrowserWorkspace
    {
        public string SessionId => id;
        public List<IOpenCodeBrowserPage> Inventory { get; } = new();
        public IReadOnlyList<IOpenCodeBrowserPage> Pages => Inventory;
        public IOpenCodeBrowserPage? Shown { get; private set; }
        public int ShowCalls { get; private set; }
        public OpenCodeBrowserAvailability Availability => OpenCodeBrowserAvailability.Available;
        public string? AvailabilityReason => null;
        public event EventHandler? PagesChanged { add { } remove { } }
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public IOpenCodeBrowserPage OpenPage(string url) => throw new NotSupportedException();
        public void Show(IOpenCodeBrowserPage page) { Shown = page; ShowCalls++; }
        public void HideOverlay() => Shown = null;
    }
}
