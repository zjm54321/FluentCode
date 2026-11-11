// SPDX-License-Identifier: GPL-3.0-or-later
// Lane N0 headless checks: the airspace lease contract (exercised for real, WinUI-free), the C19 error
// card, the C18 monospace font route and the card geometry that must not move.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluentCode.Pages.OpenCode;
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

    internal static void Run(string repo, Action<bool, string> require, string? devWinUiTheme = null)
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
        static int Occurrences(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
        static string Between(string text, string from, string to)
        {
            var start = text.IndexOf(from, StringComparison.Ordinal);
            if (start < 0) return "";
            start += from.Length;
            var end = text.IndexOf(to, start, StringComparison.Ordinal);
            return end < 0 ? "" : text[start..end];
        }

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
        // ---- C06: the icon badge — same box, ring and weight as the adapter pill, and the same palette ----
        var badgeStyling = Between(theme, "internal static void Badge(Border border, NativeUiElement node)", "internal static void Button(Button button, NativeUiElement node)");
        Check(badgeStyling.Contains("border.Style = box;") && badgeStyling.Contains("border.BorderThickness = new Thickness(1);")
            && badgeStyling.Contains("border.Padding = new Thickness(8, 2, 8, 2); border.MinHeight = 20; border.CornerRadius = new CornerRadius(10);"),
            "A badge with a leading glyph keeps the pill box and carries the same 1-DIP tone ring as the adapter pill");
        Check(theme.Contains("\"Badge\" => (\"CaptionTextBlockStyle\", 0d, 0d, \"SemiBold\"),")
            && badgeStyling.Contains("Label: TextStyle(\"TextBlock\", \"CaptionTextBlockStyle\", Foreground(tone), 0d, 0d, \"SemiBold\"));")
            && badgeStyling.Contains("Label: TextStyle(\"TextBlock\", \"CaptionTextBlockStyle\", keys.Foreground, 0d, 0d, \"SemiBold\"))"),
            "The hand-built badge label stays Caption SemiBold in either palette, the weight the adapter pill's style sets");

        // The library's four categorical tones by three roles: the twelve keys the adapter pill paints from (executed).
        var libraryKeys = new Dictionary<string, string> { ["muted"] = "Gray", ["info"] = "Blue", ["purple"] = "Purple", ["pink"] = "Pink" };
        Check(libraryKeys.All(pair => NativeUiStyleMath.BadgeLibraryTone(pair.Key) is { } keys
                && keys.Background == "IssueLabel" + pair.Value + "BackgroundBrush" && keys.Border == "IssueLabel" + pair.Value + "BorderBrush"
                && keys.Foreground == "IssueLabel" + pair.Value + "ForegroundBrush"),
            "The icon badge reads the library's own twelve tone keys (grey, blue, purple, pink by fill, ring, text), so both badge forms share one palette");
        Check(new[] { "success", "warning", "danger", "", "neutral", "chart1", "Info" }.All(tone => NativeUiStyleMath.BadgeLibraryTone(tone) is null),
            "Only the four normalized categorical tones have library keys; the status tones stay on the SystemFillColor keys both badge forms already share");
        Check(libraryKeys.Keys.SelectMany(tone => { var keys = NativeUiStyleMath.BadgeLibraryTone(tone)!.Value; return new[] { keys.Background, keys.Border, keys.Foreground }; }).Distinct(StringComparer.Ordinal).Count() == 12,
            "The twelve library tone keys are distinct");
        // The keys above are only names until the pinned DevWinUI actually defines them, in every theme: a key defined
        // for Light but not for High Contrast would fall back in one theme only. The runner hands us the locked
        // package's theme dictionary (tests/native-ui/run.ps1), so a rename fails here rather than silently
        // degrading every icon badge back to the app tint.
        require(devWinUiTheme is not null && File.Exists(devWinUiTheme), "The locked DevWinUI theme dictionary was not passed to the tests: " + (devWinUiTheme ?? "<null>"));
        if (devWinUiTheme is not null && File.Exists(devWinUiTheme))
        {
            // The file concatenates several control dictionaries, each with its own Default/Light/HighContrast
            // blocks, so attribute every definition to the theme block that encloses it instead of counting.
            var themesByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var currentTheme = "";
            foreach (var line in File.ReadLines(devWinUiTheme))
            {
                foreach (var theme3 in new[] { "Default", "Light", "HighContrast" })
                    if (line.Contains("<ResourceDictionary x:Key=\"" + theme3 + "\"", StringComparison.Ordinal)) currentTheme = theme3;
                var at = line.IndexOf("x:Key=\"IssueLabel", StringComparison.Ordinal);
                if (at < 0 || currentTheme.Length == 0) continue;
                var start = at + "x:Key=\"".Length; var end = line.IndexOf('"', start);
                if (end < 0) continue;
                var name = line[start..end];
                if (!themesByKey.TryGetValue(name, out var themes)) themesByKey[name] = themes = new(StringComparer.Ordinal);
                themes.Add(currentTheme);
            }
            var missing = libraryKeys.Keys
                .SelectMany(tone => { var keys = NativeUiStyleMath.BadgeLibraryTone(tone)!.Value; return new[] { keys.Background, keys.Border, keys.Foreground }; })
                .Where(key => !themesByKey.TryGetValue(key, out var themes) || !themes.SetEquals(new[] { "Default", "Light", "HighContrast" }))
                .Order(StringComparer.Ordinal)
                .Select(key => key + (themesByKey.TryGetValue(key, out var themes) ? " (only in " + string.Join("/", themes.Order(StringComparer.Ordinal)) + ")" : " (not defined)"))
                .ToArray();
            Check(missing.Length == 0,
                "The pinned DevWinUI defines all twelve library tone keys in its dark, light and high-contrast theme dictionaries"
                + (missing.Length == 0 ? "" : "; an icon badge would silently fall back to the app tint for: " + string.Join(", ", missing)));
        }

        // Fallback (executed): a badge is painted from one palette as a whole, never half and half.
        (string? Box, string? Glyph, string? Label) appPalette = ("app-box", "app-glyph", "app-label");
        (string? Box, string? Glyph, string? Label) libraryPalette = ("library-box", "library-glyph", "library-label");
        Check(NativeUiStyleMath.PickBadgePalette(appPalette, libraryPalette) == libraryPalette,
            "When the library's box, glyph and label styles all resolve, the badge is wholly library-toned");
        var partial = new (string? Box, string? Glyph, string? Label)[]
        {
            (null, "library-glyph", "library-label"), ("library-box", null, "library-label"), ("library-box", "library-glyph", null),
            (null, null, "library-label"), (null, "library-glyph", null), ("library-box", null, null), (null, null, null), default,
        };
        Check(partial.All(candidate => NativeUiStyleMath.PickBadgePalette(appPalette, candidate) == appPalette),
            "If any one of the library's three styles is missing, the badge keeps the app tint for box, glyph and label together");
        Check(NativeUiStyleMath.PickBadgePalette<string>(default, libraryPalette) == libraryPalette && NativeUiStyleMath.PickBadgePalette<string>(default, default) == default,
            "The palette choice depends only on the library styles and returns one whole palette");

        // The WinUI half of the fallback: probe the keys before any style is parsed, then pick one whole palette.
        var probe = badgeStyling.IndexOf("TryToneBrush(keys.Background) && TryToneBrush(keys.Border) && TryToneBrush(keys.Foreground)", StringComparison.Ordinal);
        var parse = badgeStyling.IndexOf("(Box: OpenCodeThemedXaml.BrushStyle(\"Border\", (\"Background\", keys.Background), (\"BorderBrush\", keys.Border)),", StringComparison.Ordinal);
        Check(badgeStyling.Contains("var app = (Box: OpenCodeThemedXaml.BrushStyle(\"Border\", (\"Background\", Background(tone)), (\"BorderBrush\", Foreground(tone))),")
            && badgeStyling.Contains("Glyph: OpenCodeThemedXaml.BrushStyle(\"FontIcon\", (\"Foreground\", Foreground(tone))),")
            && badgeStyling.Contains("var library = NativeUiStyleMath.BadgeLibraryTone(tone) is { } keys && TryToneBrush(keys.Background)") && probe >= 0 && parse > probe
            && badgeStyling.Contains("Glyph: OpenCodeThemedXaml.BrushStyle(\"FontIcon\", (\"Foreground\", keys.Foreground)),") && badgeStyling.Contains(": default;")
            && badgeStyling.Contains("var (box, glyph, label) = NativeUiStyleMath.PickBadgePalette(app, library);")
            && badgeStyling.Contains("icon.Style = glyph;") && badgeStyling.Contains("text.Style = label;"),
            "The icon badge builds the app palette and the library palette whole, probes the library keys before parsing any style, and applies exactly one of them");
        Check(theme.Contains("try { return Application.Current?.Resources is { } resources && resources.TryGetValue(key, out var value) && value is Brush; }")
            && theme.Contains("catch { return false; }"),
            "The library key probe is a TryGetValue with a brush type test and never throws");
        Check(!theme.Contains("\"IssueLabel", StringComparison.Ordinal) && !renderer.Contains("\"IssueLabel", StringComparison.Ordinal),
            "The library key names live in one place (NativeUiStyleMath), not as literals scattered through the theme or renderer");

        // ---- C06: text-only badges go through the shared pill adapter; identity and tone mapping are kept ----
        var badgeCase = Between(renderer, "case \"Badge\":", "case \"Divider\":");
        Check(badgeCase.Contains("CommunityTextBadge.Create(label, tone, CommunityTextBadgeShape.Pill, label)"),
            "A text-only badge is created through CommunityTextBadge as a Pill, named by its own text for screen readers");
        Check(Occurrences(badgeCase, "CommunityTextBadge.Create(") == 1
            && badgeCase.Contains("CommunityTextBadge.Update(pill, label); CommunityTextBadge.SetTone(pill, tone); CommunityTextBadge.SetAccessibleName(pill, label);"),
            "A re-render updates the existing pill in place (text, tone, accessible name) instead of building a second one");
        Check(badgeCase.Contains("var badgeVisual = new Visual(badgeHost);") && Occurrences(badgeCase, "new Visual(") == 1
            && badgeCase.Contains("Sync(badgeHost, new UIElement[] { pill });") && badgeCase.Contains("Sync(badgeHost, new UIElement[] { badgeBox });"),
            "One host per structural path carries either badge form, so the element a parent holds never changes across re-render");
        Check(badgeCase.Contains("var badgeHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };"),
            "The badge host stays left-aligned and vertically centred, so a pill never stretches across a card column");
        Check(badgeCase.Contains("if (icon is null)") && badgeCase.Contains("Sync(badgePanel, new UIElement[] { icon, badgeText });")
            && badgeCase.Contains("NativeUiTheme.Badge(badgeBox, node);"),
            "A badge whose icon resolves to a glyph keeps the Border + FontIcon + TextBlock path");
        // Comments may name the library; code must not (CONTRACT-F §2: no DevWinUI type on the consumer side).
        var badgeCode = string.Join('\n', badgeCase.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(!badgeCode.Contains(".Foreground", StringComparison.Ordinal) && !badgeCode.Contains("IssueLabel", StringComparison.Ordinal)
            && !renderer.Contains("using DevWinUI", StringComparison.Ordinal) && !theme.Contains("using DevWinUI", StringComparison.Ordinal),
            "Native UI never sets a badge Foreground (it would discard the tone) and never names a DevWinUI type");
        var toneMap = Between(theme, "internal static CommunityTextBadgeTone BadgeAdapterTone(string color) => BadgeTone(color) switch", "};");
        Check(toneMap.Contains("\"success\" => CommunityTextBadgeTone.Success") && toneMap.Contains("\"warning\" => CommunityTextBadgeTone.Caution")
            && toneMap.Contains("\"danger\" => CommunityTextBadgeTone.Critical") && toneMap.Contains("\"info\" => CommunityTextBadgeTone.Attention")
            && toneMap.Contains("\"purple\" => CommunityTextBadgeTone.Purple") && toneMap.Contains("\"pink\" => CommunityTextBadgeTone.Pink")
            && toneMap.Contains("_ => CommunityTextBadgeTone.Neutral"),
            "All seven Native UI badge tones map onto the adapter: muted/unknown Neutral, info Attention, success, warning Caution, danger Critical, purple, pink");
        Check(theme.Contains("private static string BadgeTone(string color) => color is \"info\" or \"success\" or \"warning\" or \"danger\" or \"purple\" or \"pink\" ? color : \"muted\";"),
            "Unknown badge colours still normalize to muted before the adapter sees them");
        Check(renderer.Contains("var key = path + \"#\" + node.StatementId + \":\" + node.TypeName; _seen.Add(key);"),
            "The structural-path cache key is untouched, so hosts keep their identity across a re-render");
        Check(partView.Contains("is not FrameworkElement { Tag: NativeUiTheme.PlaceholderTag }"),
            "The streaming placeholder host is reused across updates instead of being rebuilt");
        Check(theme.Contains("Fit(bar, wide: false); return bar;"),
            "The notice keeps the compact 480px fit, so the error card cannot widen past a normal card");

        Console.WriteLine($"PASS native-ui core: airspace lease contract, InfoBar error card, monospace font route and card geometry; {checks} checks");
    }
}
