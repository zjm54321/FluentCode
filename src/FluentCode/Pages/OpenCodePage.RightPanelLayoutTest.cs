using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentCode.Motion;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    // Invoked only by startup-smoke -PanelFeedback in an isolated profile. No PTY/page is created on the server.
    // Fixture rows exercise the actual controls, visibility callbacks, XAML hosts and WinUI measure/arrange.
    internal async Task VerifyRightPanelLayoutAsync(string output, Func<string, Task> capture)
    {
        if (Environment.GetEnvironmentVariable("FC_PANEL_FEEDBACK_TEST") != "1"
            || _selectedSessionId is not { } session || _browserStrip is not { } browser
            || _rightPanelMetadata is not { } metadata || !_sidebarLayout.IsWide)
            throw new InvalidOperationException("Right-panel regression requires the opt-in loaded wide session.");

        var originalGroups = BrowserPanelGroups();
        var originalShells = _persistentPtyInventoryView.Entries.Where(entry => entry.Kind == OpenCodeTerminalEntryKind.Shell).ToArray();
        var originalSelectorVisibility = _agentSelector.Visibility;
        var originalNoteVisibility = _wslQuickAccessView?.Note.Visibility;
        var fixture = new RightPanelCaptureWorkspace(session);
        var context = new OpenCodePersistentPtyInventoryContext(Guid.NewGuid(), 1, session);
        var terminal = new PersistentPtyInfo("pty_layout_capture", "布局验证终端", "bash", ImmutableArray<string>.Empty,
            _messageBinding?.Directory ?? "/", "exited", 0, 0, session, null, new(120, 32), new(0, 0));
        var results = new List<object>();
        Environment.SetEnvironmentVariable("FC_PANEL_FEEDBACK_FIXTURE", "1");
        try
        {
            browser.BindGroups(new[] { new OpenCodeBrowserStripGroup(fixture, null) });
            _persistentPtyInventoryView.SetShellEntries(Array.Empty<OpenCodeTerminalEntry>());
            _agentSelector.Visibility = Visibility.Collapsed;
            if (_wslQuickAccessView is { } quick) quick.Note.Visibility = Visibility.Collapsed;
            foreach (var (name, showBrowser, showTerminal) in new[]
            {
                ("40-panel-no-strip", false, false), ("41-panel-browser-strip", true, false),
                ("42-panel-terminal-strip", false, true), ("43-panel-both-strips", true, true),
            })
            {
                var browserState = new OpenCodeBrowserPanelState();
                if (showBrowser) browserState.Expand();
                _browserButton!.Render(fixture, browserState);
                BrowserCommandsSlot.Visibility = showBrowser ? Visibility.Visible : Visibility.Collapsed;
                browser.SetExpanded(showBrowser);
                _persistentPtyInventoryView.Render(context, new(OpenCodePersistentPtyInventoryPhase.Ready,
                    showTerminal ? ImmutableArray.Create(terminal) : ImmutableArray<PersistentPtyInfo>.Empty));
                // Include completion of collapse/implicit-offset animation before reading native layout bounds.
                await Task.Delay(FluentMotion.Duration(FluentMotion.Normal) + TimeSpan.FromMilliseconds(100));
                MeasurePanel();
                Check(BrowserStripSlot.Visibility == (showBrowser ? Visibility.Visible : Visibility.Collapsed), name + ": browser host visibility");
                Check(PersistentPtyStripSlot.Visibility == (showTerminal ? Visibility.Visible : Visibility.Collapsed), name + ": terminal host visibility");
                Check(WslQuickAccessNoteSlot.Visibility == Visibility.Collapsed, name + ": no empty note host");
                Check(RightSessionDiagnostics.Visibility == Visibility.Collapsed, name + ": no retired diagnostics space");
                var gaps = SeparatorGaps();
                foreach (var gap in gaps)
                {
                    Check(Math.Abs(gap.BeforeDip - 16) <= 0.5 && Math.Abs(gap.AfterDip - 16) <= 0.5,
                        $"{name}: {gap.Name} separator gaps {gap.BeforeDip:0.##}/{gap.AfterDip:0.##} DIP, expected 16/16");
                }
                results.Add(new { State = name, Browser = showBrowser, Terminal = showTerminal,
                    RasterizationScale = XamlRoot.RasterizationScale, Gaps = gaps });
                File.WriteAllText(Path.Combine(output, "panel-layout.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                await capture(name);
            }

            var copyBefore = Bounds(metadata.CopyButton);
            var dividerBefore = Bounds(RightContextDivider);
            CopyRightPanelSessionId(); // Real clipboard path and real right-hand copy glyph.
            Check(metadata.CopyGlyph.Glyph == ActionFeedback.SuccessGlyph, "copy glyph becomes a check mark");
            await Task.Delay(FluentMotion.Duration(FluentMotion.Fast) + TimeSpan.FromMilliseconds(30));
            MeasurePanel();
            Check(Bounds(metadata.CopyButton) == copyBefore && Bounds(RightContextDivider) == dividerBefore, "copy feedback does not shift layout");
            Check(!Elements(RightSessionContent).OfType<TextBlock>().Any(text => text.Text.Contains("已复制", StringComparison.Ordinal)), "copy feedback has no visible success text");
            await capture("50-copy-feedback");
            await Task.Delay(ActionFeedback.SuccessHold);
            Check(metadata.CopyGlyph.Glyph == "\uE8C8", "copy glyph restores automatically");
            File.WriteAllText(Path.Combine(output, "panel-feedback.json"), JsonSerializer.Serialize(new
            {
                Passed = true, CopyGlyph = ActionFeedback.SuccessGlyph, RestoredGlyph = metadata.CopyGlyph.Glyph,
                CopyBounds = copyBefore, NoLayoutShift = true, NoVisibleSuccessText = true,
                StripData = "isolated native UI fixture; session/context are read-only live WSL data",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FC_PANEL_FEEDBACK_FIXTURE", null);
            browser.BindGroups(originalGroups);
            RenderBrowserPanel();
            _persistentPtyInventoryView.SetShellEntries(originalShells);
            if (_persistentInventoryOwner is { } owner) PaintPersistentInventory(owner);
            else _persistentPtyInventoryView.Render(null, null);
            _agentSelector.Visibility = originalSelectorVisibility;
            if (_wslQuickAccessView is { } quick && originalNoteVisibility is { } note) quick.Note.Visibility = note;
            ActionFeedback.Reset(metadata.CopyGlyph);
        }

        void MeasurePanel()
        {
            PageRoot.InvalidateMeasure();
            PageRoot.Measure(new Size(PageRoot.ActualWidth, PageRoot.ActualHeight));
            PageRoot.UpdateLayout();
        }
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(RightSessionContent)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        SeparatorGap Gap(string name, FrameworkElement previous, FrameworkElement line, FrameworkElement next)
        {
            var before = Bounds(line).Top - Bounds(previous).Bottom;
            var after = Bounds(next).Top - Bounds(line).Bottom;
            return new(name, before, after, before * XamlRoot.RasterizationScale, after * XamlRoot.RasterizationScale);
        }
        List<SeparatorGap> SeparatorGaps()
        {
            var gaps = new List<SeparatorGap>
            {
                Gap("quick-access", RightPanelHeaderSlot, RightQuickAccessDivider, RightQuickAccessButtons),
                Gap("context", WslQuickAccessSection, RightContextDivider, _insightsView),
            };
            // Other sections use the same 16-DIP scale. Measure their actual native siblings, not constants.
            foreach (var line in Elements(_insightsView).OfType<Border>().Where(line => line.Height == 1 && line.ActualHeight > 0))
            {
                if (VisualTreeHelper.GetParent(line) is StackPanel parent)
                {
                    var at = parent.Children.IndexOf(line);
                    var previous = parent.Children.Take(at).OfType<FrameworkElement>().LastOrDefault(element => element.Visibility == Visibility.Visible && element.ActualHeight > 0);
                    var next = parent.Children.Skip(at + 1).OfType<FrameworkElement>().FirstOrDefault(element => element.Visibility == Visibility.Visible && element.ActualHeight > 0);
                    if (previous is not null && next is not null) gaps.Add(Gap("usage-totals", previous, line, next));
                }
                else if (VisualTreeHelper.GetParent(line) is Grid todoCard && AutomationProperties.GetAutomationId(todoCard) == "OpenCodeInsightsTodoCard")
                {
                    var usage = Elements(_insightsView).OfType<FrameworkElement>().Single(element => AutomationProperties.GetAutomationId(element) == "OpenCodeInsightsLayeredUsage");
                    var todo = todoCard.Children.OfType<FrameworkElement>().Single(element => Grid.GetRow(element) == 1);
                    gaps.Add(Gap("todo", usage, line, todo));
                }
            }
            return gaps;
        }
        static void Check(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Right-panel regression: " + detail);
        }
    }

    private sealed record SeparatorGap(string Name, double BeforeDip, double AfterDip, double BeforePixels, double AfterPixels);

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var element in Elements(VisualTreeHelper.GetChild(root, i))) yield return element;
    }

    private sealed class RightPanelCaptureWorkspace(string session) : IOpenCodeBrowserWorkspace
    {
        public string SessionId => session;
        public IReadOnlyList<IOpenCodeBrowserPage> Pages { get; } = new[] { new RightPanelCapturePage() };
        public IOpenCodeBrowserPage? Shown => null;
        public OpenCodeBrowserAvailability Availability => OpenCodeBrowserAvailability.Available;
        public string? AvailabilityReason => null;
        public event EventHandler? PagesChanged { add { } remove { } }
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public IOpenCodeBrowserPage OpenPage(string url) => throw new InvalidOperationException("Read-only layout fixture");
        public void Show(IOpenCodeBrowserPage page) { }
        public void HideOverlay() { }
    }

    private sealed class RightPanelCapturePage : IOpenCodeBrowserPage
    {
        public string Id => "tab_layout_capture";
        public string Url => "about:blank";
        public string Title => "布局验证页面";
        public bool Loading => false;
        public string? LoadError => null;
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public bool OpenedByAgent => false;
        public string? AgentActivity => null;
        public bool UserTakeover { get; set; }
        public string? PreviewPath => null;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void Navigate(string url) { }
        public void GoBack() { }
        public void GoForward() { }
        public void Reload() { }
        public void Stop() { }
        public void Close() { }
    }
}
