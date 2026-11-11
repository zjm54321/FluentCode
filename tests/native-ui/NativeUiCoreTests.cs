// SPDX-License-Identifier: GPL-3.0-or-later
// Lane N0 headless checks: the airspace lease contract (exercised for real, WinUI-free), the C19 error
// card, the C18 monospace font route and the card geometry that must not move.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluentCode.Services.NativeUi;

internal static class NativeUiCoreTests
{
    /// <summary>Records each release and can throw from Dispose, so both release paths are exercisable.</summary>
    private sealed class Lease(string name, List<string> log, bool throws = false) : IDisposable
    {
        internal int Releases { get; private set; }

        public void Dispose()
        {
            Releases++;
            log.Add(name);
            if (throws) throw new InvalidOperationException("lease failed: " + name);
        }
    }

    internal static void Run(string repo, Action<bool, string> require)
    {
        var checks = 0;
        void Check(bool condition, string message) { checks++; require(condition, message); }

        const string NativeUiFolder = "src/FluentCode/Pages/OpenCode/NativeUi";
        const string ServicesFolder = "src/FluentCode/Services/NativeUi";
        string Source(string relative)
        {
            var path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
            require(File.Exists(path), "Missing lane N0 source file: " + path);
            return File.ReadAllText(path);
        }
        string[] Folder(string relative) => Directory.GetFiles(Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar)), "*.cs");

        var theme = Source(NativeUiFolder + "/NativeUiTheme.cs");
        var components = Source(NativeUiFolder + "/NativeUiRenderer.Components.cs");
        var airspace = Source(NativeUiFolder + "/NativeUiAirspace.cs");
        var partView = Source(NativeUiFolder + "/OpenCodeNativeUiPartView.cs");
        var renderer = Source(NativeUiFolder + "/NativeUiRenderer.cs");
        var leaseScope = Source(ServicesFolder + "/NativeUiLeaseScope.cs");

        // ---- airspace helper: one lease set per popup session, released exactly once (balanced begin/end) ----
        var log = new List<string>();
        var first = new Lease("first", log); var second = new Lease("second", log);
        var scope = new NativeUiLeaseScope();
        Check(scope.Count == 0, "A fresh airspace lease scope holds nothing");
        scope.Add(first); scope.Add(second);
        Check(scope.Count == 2, "Every terminal captured under the popup shares one lease set");
        scope.Release(); scope.Release();
        Check(first.Releases == 1 && second.Releases == 1 && log.Count == 2 && scope.Count == 0,
            "Releasing an airspace scope releases every lease exactly once, even if release is repeated");
        scope.Release();
        Check(first.Releases == 1 && second.Releases == 1, "A released airspace scope never releases a terminal lease again");
        using (var disposable = new NativeUiLeaseScope())
        {
            var leased = new Lease("disposable", log);
            disposable.Add(leased);
            disposable.Dispose(); disposable.Dispose();
            Check(leased.Releases == 1 && disposable.Count == 0, "Disposing the scope is the same single release");
        }
        var previous = new NativeUiLeaseScope();
        var stale = new Lease("stale", log);
        previous.Add(stale);
        var current = new NativeUiLeaseScope();
        previous.Dispose();
        Check(stale.Releases == 1 && previous.Count == 0 && current.Count == 0,
            "A popup that opens again releases the previous terminal leases instead of holding two sets");
        var survivor = new Lease("survivor", log);
        var throwingScope = new NativeUiLeaseScope();
        throwingScope.Add(new Lease("throws", log, throws: true)); throwingScope.Add(survivor);
        throwingScope.Release();
        Check(survivor.Releases == 1 && throwingScope.Count == 0,
            "A terminal lease that fails to release never blocks the remaining leases");
        var three = Enumerable.Range(0, 3).Select(index => new Lease("terminal-" + index.ToString(CultureInfo.InvariantCulture), log)).ToArray();
        var multi = new NativeUiLeaseScope();
        foreach (var lease in three) multi.Add(lease);
        Check(multi.Count == 3, "Two docked terminals plus a tab group are all leased by one popup session");
        multi.Dispose();
        Check(three.All(lease => lease.Releases == 1), "Every terminal in a multi-renderer scope gets exactly one release");

        Check(airspace.Contains("internal static NativeUiAirspace Begin(DependencyObject? root, bool snapshot = true)")
            && airspace.Contains("internal static void Track(Flyout flyout)")
            && airspace.Contains("public void Dispose() => _leases.Release();"),
            "The airspace helper publishes Begin/Track/Dispose with the contract's signatures");
        Check(airspace.Contains("flyout.Opening += (_, _) =>") && airspace.Contains("flyout.Closed += (_, _) => { scope?.Dispose(); scope = null; }"),
            "A tracked flyout captures on Opening and releases on Closed, as one pair");
        Check(airspace.Contains("if (node is OpenCodePersistentPtyRenderer terminal) { scope._leases.Add(terminal.SuppressAirspace(snapshot)); continue; }")
            && airspace.Contains("catch (Exception)"),
            "The walk takes one lease per terminal and never descends into the terminal's own HWND subtree");
        // Static state is what roots a control past its popup (the leak rules in LANE-RULES.md); a static method is fine.
        var airspaceStatics = airspace.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .Where(line => line.Contains("static", StringComparison.Ordinal) && !line.Contains('(', StringComparison.Ordinal))
            .ToArray();
        Check(airspaceStatics.Length == 0,
            "The airspace helper keeps no static field or property, so a closed popup cannot root a control or keep the terminal hidden: " + string.Join(" | ", airspaceStatics));
        Check(leaseScope.Contains("internal void Release()") && leaseScope.Contains("_leases = null;"),
            "The WinUI-free lease scope is the single owner of the captured leases");

        // Every Native UI popup in this lane's files goes through the helper: no second copy of the walk.
        Check(!components.Contains("AttachAirspace") && !components.Contains("SuppressAirspace("),
            "The copy-pasted airspace walk is gone from the components file");
        Check(components.Contains("NativeUiAirspace.Track(flyout); popover.Cleanup = flyout.Hide;")
            && components.Contains("var flyout = new Flyout { Content = content }; NativeUiAirspace.Track(flyout);")
            && components.Split("NativeUiAirspace.Track(").Length == 3,
            "Both Native UI flyouts (Popover and the URL confirmation) track their airspace through the shared helper");
        var walkCopies = Folder(NativeUiFolder)
            .Where(file => Path.GetFileName(file) != "NativeUiAirspace.cs" && Path.GetFileName(file) != "NativeUiRenderer.Media.cs")
            .SelectMany(file => File.ReadAllLines(file).Where(line => line.Contains("SuppressAirspace(", StringComparison.Ordinal)).Select(line => Path.GetFileName(file)))
            .Distinct().ToArray();
        Check(walkCopies.Length == 0,
            "NativeUiRenderer.Media.cs (lane N2) is the only remaining inline airspace walk; N2 moves it onto the helper next wave: " + string.Join(",", walkCopies));
        var mediaWalk = File.ReadAllLines(Path.Combine(repo, "src/FluentCode/Pages/OpenCode/NativeUi/NativeUiRenderer.Media.cs".Replace('/', Path.DirectorySeparatorChar)))
            .Count(line => line.Contains("SuppressAirspace(", StringComparison.Ordinal));
        Check(mediaWalk == 1, "The gallery dialog's single inline walk is still the only N2 site the contract has to replace");

        // ---- C19: the error card is the stock InfoBar, keeping the polite announcement and the three-line cap ----
        Check(theme.Contains("internal static FrameworkElement Notice(string title, string message, bool error = false)"),
            "The card notice returns a framework element so the stock bar can replace the hand-built Border");
        Check(theme.Contains("Severity = InfoBarSeverity.Error, IsClosable = false, IsIconVisible = true,")
            && theme.Contains("Title = title, Content = details,"),
            "The card-level failure is a stock InfoBar at Error severity, not closable, with its icon");
        Check(theme.Contains("bar.Loaded += (_, _) => bar.IsOpen = true;")
            && theme.IndexOf("bar.Loaded += (_, _) => bar.IsOpen = true;", StringComparison.Ordinal) < theme.IndexOf("Fit(bar, wide: false); return bar;", StringComparison.Ordinal),
            "The error bar opens once it is loaded, so the announcement happens with a live UIA peer, and the handler is attached before the bar reaches its host");
        Check(theme.Contains("details.MaxLines = 3; details.TextTrimming = TextTrimming.CharacterEllipsis;"),
            "A long card diagnostic still trims to three lines, as it did before the InfoBar");
        Check(theme.Contains("AutomationProperties.SetName(bar, title);")
            && theme.Contains("AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);"),
            "The error bar keeps its accessible name and the polite announcement to screen readers");
        Check(!theme.Contains("\\uEB90") && !theme.Contains("details.Margin = new Thickness(24, 0, 0, 0)")
            && !theme.Contains("(\"Background\", \"SystemFillColorCriticalBackgroundBrush\"), (\"BorderBrush\""),
            "The hand-built critical chrome and its raw status glyph are gone from the notice");
        Check(theme.Contains("Child = chip, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(SmallRadius)")
            && theme.Contains("(\"Background\", \"SubtleFillColorSecondaryBrush\")"),
            "The non-error 未支持 chip stays the light inline Border instead of a heavy InfoBar");
        Check(partView.Contains("NativeUiTheme.Notice(\"交互卡片无法渲染\"")
            && partView.Contains("_root.Children.Clear(); _root.Children.Add(NativeUiTheme.Notice("),
            "The parse/validation failure path still shows one card-level notice in the part view");
        Check(renderer.Contains("NativeUiTheme.Notice(\"原生卡片主题更新失败\"")
            && components.Contains("NativeUiTheme.Notice(\"未支持\", type)"),
            "The theme-refresh failure and the unsupported-component chip keep their notices");

        // ---- C18: inline code follows the user's monospace slot instead of a hard-coded family ----
        Check(theme.Contains("if (style.HasFlag(NativeUiStyleMath.InlineStyle.Code)) AppFonts.Apply(run, AppFontSlot.Monospace);"),
            "Inline code runs take the user's monospace font through AppFonts, not a literal family");
        Check(!theme.Contains("new FontFamily(\"Cascadia Mono, Consolas\")") && !theme.Contains("Cascadia Mono"),
            "No monospace literal remains in the Native UI theme");
        var tokens = Source("src/FluentCode/Styles/AppTokens.xaml");
        var fonts = Source("src/FluentCode/Services/Appearance/AppFonts.cs");
        Check(tokens.Contains("<FontFamily x:Key=\"AppMonospaceFontFamily\">") && fonts.Contains("MonospaceResourceKey = \"AppMonospaceFontFamily\""),
            "The app-wide AppMonospaceFontFamily resource stays the single source of the user's choice");
        var remainingFonts = Folder(NativeUiFolder)
            .SelectMany(file => File.ReadAllLines(file).Where(line => line.Contains("new FontFamily(", StringComparison.Ordinal)).Select(line => Path.GetFileName(file) + ": " + line.Trim()))
            .ToArray();
        Check(remainingFonts.Length == 1 && remainingFonts[0].StartsWith("NativeUiRenderer.Content.cs:", StringComparison.Ordinal)
            && remainingFonts[0].Contains("Cambria Math", StringComparison.Ordinal),
            "The only font literal left in the Native UI folder is the Math formula font (C17), which is not a user font slot: " + string.Join(" | ", remainingFonts));

        // ---- Geometry and identity this lane must not move ----
        Check(theme.Contains("internal const double CompactWidth = 480;") && theme.Contains("internal const double WideWidth = 808;")
            && theme.Contains("internal const double MaximumWidth = 640;") && theme.Contains("internal const double WrapCellWidth = 144;"),
            "Card sizing is unchanged: 480 compact, 808 wide, 640 maximum, 144 wrap cell");
        Check(theme.Contains("border.Padding = new Thickness(8, 2, 8, 2); border.MinHeight = 20; border.CornerRadius = new CornerRadius(10);"),
            "Badges stay 20px pills with radius 10 (C06 moves them onto the shared adapter next wave)");
        Check(renderer.Contains("var key = path + \"#\" + node.StatementId + \":\" + node.TypeName; _seen.Add(key);"),
            "The structural-path cache key is untouched, so hosts keep their identity across a re-render");
        Check(partView.Contains("is not FrameworkElement { Tag: NativeUiTheme.PlaceholderTag }"),
            "The streaming placeholder host is reused across updates instead of being rebuilt");
        Check(theme.Contains("Fit(bar, wide: false); return bar;"),
            "The notice keeps the compact 480px fit, so the error card cannot widen past a normal card");

        Console.WriteLine($"PASS native-ui core: airspace lease contract, InfoBar error card, monospace font route and card geometry; {checks} checks");
    }
}
