// SPDX-License-Identifier: GPL-3.0-or-later
// Lane N0 headless checks: the airspace lease contract (exercised for real, WinUI-free), the C19 error
// card, the C18 monospace font route and the card geometry that must not move.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        // sRGB compositing and WCAG contrast for the badge colour checks (colours as 0xRRGGBB).
        static int Over(int color, double opacity, int surface)
        {
            int Mix(int shift) => (int)Math.Round(((color >> shift) & 0xFF) * opacity + ((surface >> shift) & 0xFF) * (1 - opacity));
            return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }
        static double BadgeContrast(int first, int second)
        {
            static double Channel(int value) { var c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            static double Luminance(int color) => 0.2126 * Channel((color >> 16) & 0xFF) + 0.7152 * Channel((color >> 8) & 0xFF) + 0.0722 * Channel(color & 0xFF);
            var (a, b) = (Luminance(first), Luminance(second));
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
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
            .Where(file => Path.GetFileName(file) != "NativeUiAirspace.cs")
            .SelectMany(file => File.ReadAllLines(file).Where(line => line.Contains("SuppressAirspace(", StringComparison.Ordinal)).Select(line => Path.GetFileName(file)))
            .Distinct().ToArray();
        Check(walkCopies.Length == 0,
            "Only NativeUiAirspace.cs may take terminal suppression leases directly: " + string.Join(",", walkCopies));
        var media = Source(NativeUiFolder + "/NativeUiRenderer.Media.cs");
        Check(media.Contains("NativeUiAirspace? airspace = null;")
            && Regex.IsMatch(media, @"try\s*\{\s*airspace = NativeUiAirspace.Begin\(panel.XamlRoot\?\.Content, snapshot: true\);\s*await viewer.ShowAsync\(\);\s*\}\s*finally\s*\{\s*airspace\?\.Dispose\(\); viewer = null;\s*\}"),
            "The gallery captures shared airspace inside try, holds it across ShowAsync, and releases it and clears the viewer in finally");

        // ---- C19: stock InfoBar template, custom polite notification peer, once per instance, three-line cap ----
        var errorBar = Source(NativeUiFolder + "/NativeUiErrorInfoBar.cs");
        Check(theme.Contains("internal static FrameworkElement Notice(string title, string message, bool error = false)"),
            "The card notice still returns an ordinary framework element");
        Check(theme.Contains("var bar = new NativeUiErrorInfoBar")
            && theme.Contains("Severity = InfoBarSeverity.Error, IsClosable = false, IsIconVisible = true,")
            && theme.Contains("Title = title, Content = details,"),
            "The card-level failure remains an Error-severity InfoBar, not closable, with its icon and diagnostic content");
        Check(errorBar.Contains("internal sealed partial class NativeUiErrorInfoBar : InfoBar")
            && !Regex.IsMatch(errorBar, @"\b(?:Template|DefaultStyleKey|Style)\s*="),
            "The error control remains an InfoBar and does not replace the base constructor's stock style key or template");
        Check(errorBar.Contains("protected override AutomationPeer OnCreateAutomationPeer() => new ErrorPeer(this);")
            && errorBar.Contains("private sealed partial class ErrorPeer : FrameworkElementAutomationPeer")
            && errorBar.Contains("internal ErrorPeer(NativeUiErrorInfoBar owner) : base(owner) { }")
            && errorBar.Contains("protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;")
            && errorBar.Contains("protected override string GetClassNameCore() => \"InfoBar\";")
            && !errorBar.Contains("base.OnCreateAutomationPeer()") && !Regex.IsMatch(errorBar, @"\bInfoBarAutomationPeer\s*\("),
            "The custom framework-element peer reports StatusBar/InfoBar without creating the stock interrupting peer");
        const string OpenAndAnnounce = "bar.Loaded += (_, _) => { bar.IsOpen = true; bar.Announce(title, message); };";
        Check(theme.Contains(OpenAndAnnounce)
            && theme.IndexOf(OpenAndAnnounce, StringComparison.Ordinal) < theme.IndexOf("Fit(bar, wide: false); return bar;", StringComparison.Ordinal),
            "Loaded opens the attached bar before requesting its notification, and the handler is attached before hosting");
        var announce = Between(errorBar, "internal void Announce(string title, string message)", "protected override AutomationPeer OnCreateAutomationPeer()");
        Check(errorBar.Contains("private bool _announced;") && announce.Contains("if (_announced) return;")
            && announce.IndexOf("if (_announced) return;", StringComparison.Ordinal) < announce.IndexOf("_announced = true;", StringComparison.Ordinal)
            && announce.IndexOf("_announced = true;", StringComparison.Ordinal) < announce.IndexOf("RaiseNotificationEvent(", StringComparison.Ordinal)
            && Regex.Matches(errorBar, @"\b_announced\s*=").Count == 1 && !Regex.IsMatch(errorBar, @"\b_announced\s*=\s*false\b")
            && Occurrences(errorBar, "RaiseNotificationEvent(") == 1,
            "An instance-owned, never-reset guard allows only one notification request even after a second Loaded");
        Check(announce.Contains("FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this)")
            && announce.Contains("peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,")
            && announce.Contains("AutomationNotificationProcessing.CurrentThenMostRecent, text, \"NativeUiErrorInfoBarOpened\"")
            && !announce.Contains("AutomationNotificationProcessing.Important"),
            "The single notification uses Other/CurrentThenMostRecent and a stable activity id, not Error's ImportantAll path");
        Check(announce.Contains("var text = \"错误，\" + title + (message.Length is > 0 and <= 200 ? \"，\" + message : \"\");"),
            "The notification includes error and title, plus only a non-empty diagnostic of at most 200 characters");
        Check(theme.Contains("details.MaxLines = 3; details.TextTrimming = TextTrimming.CharacterEllipsis;"),
            "A long card diagnostic still trims to three lines, as it did before the InfoBar");
        Check(theme.Contains("AutomationProperties.SetName(bar, title);")
            && theme.Contains("AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);"),
            "The error bar retains its accessible title and polite live metadata; the custom notification determines processing priority");
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
        // ---- C06: one badge implementation — the shared pill adapter, with or without an icon ----
        var badgeAdapter = Source("src/FluentCode/Controls/CommunityTextBadge.cs");
        var badgeCase = Between(renderer, "case \"Badge\":", "case \"Divider\":");
        Check(badgeCase.Contains("CommunityTextBadge.Create(label, tone, CommunityTextBadgeShape.Pill, label, glyph)") && Occurrences(badgeCase, "CommunityTextBadge.Create(") == 1,
            "Every badge is created through CommunityTextBadge as a Pill, named by its own text for screen readers, with its glyph if it has one");
        Check(badgeCase.Contains("CommunityTextBadge.SetGlyph(pill, glyph); CommunityTextBadge.Update(pill, label); CommunityTextBadge.SetTone(pill, tone); CommunityTextBadge.SetAccessibleName(pill, label);"),
            "A re-render updates the existing pill in place (glyph, text, tone, accessible name) instead of building a second one");
        Check(badgeCase.Contains("var badgeVisual = new Visual(badgeHost);") && Occurrences(badgeCase, "new Visual(") == 1
            && Occurrences(badgeCase, "Sync(badgeHost,") == 1 && badgeCase.Contains("Sync(badgeHost, new UIElement[] { badgePill });"),
            "One host per structural path carries the one pill, so the element a parent holds never changes across re-render");
        Check(badgeCase.Contains("var badgeHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };"),
            "The badge host stays left-aligned and vertically centred, so a pill never stretches across a card column");
        Check(badgeCase.Contains("var glyph = NativeUiStyleMath.Glyph(NativeUiTheme.String(node, \"icon\"));")
            && !badgeCase.Contains("new Border", StringComparison.Ordinal) && !badgeCase.Contains("FontIcon", StringComparison.Ordinal) && !badgeCase.Contains("new TextBlock", StringComparison.Ordinal)
            && !theme.Contains("internal static void Badge(", StringComparison.Ordinal),
            "A badge with an icon is no longer built by hand: the renderer resolves the glyph and the adapter shows it");
        Check(NativeUiStyleMath.Glyph("check") == "\uE73E" && NativeUiStyleMath.Glyph(" Check ") == "\uE73E" && NativeUiStyleMath.Glyph("") is null && NativeUiStyleMath.Glyph("no-such-icon") is null,
            "An icon name resolves to its glyph regardless of case and padding, and an unknown or empty name to no glyph, which is a badge without an icon");
        // Comments may name the library; code must not (CONTRACT-F §2: no DevWinUI type on the consumer side).
        var badgeCode = string.Join('\n', badgeCase.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Check(!badgeCode.Contains(".Foreground", StringComparison.Ordinal) && !badgeCode.Contains("IssueLabel", StringComparison.Ordinal)
            && !renderer.Contains("using DevWinUI", StringComparison.Ordinal) && !theme.Contains("using DevWinUI", StringComparison.Ordinal)
            && !theme.Contains("\"IssueLabel", StringComparison.Ordinal) && !renderer.Contains("\"IssueLabel", StringComparison.Ordinal),
            "Native UI never sets a badge Foreground (it would discard the tone) and never names a DevWinUI type or resource key");

        // The adapter's glyph path: string data in, one cached template per glyph, state on the control itself.
        var glyphTemplate = Between(badgeAdapter, "private static DataTemplate? TitleTemplate(string? glyph, string? ink)", "\n    /// <summary>安全取应用资源");
        Check(badgeAdapter.Contains("string? glyph = null)") && badgeAdapter.Contains("internal static void SetGlyph(FrameworkElement badge, string? glyph)")
            && badgeAdapter.Contains("label.Tag = next;") && badgeAdapter.Contains("if (Equals(label.Tag, next))"),
            "The adapter takes the glyph as a string, keeps it on the control and does nothing when it did not change");
        var retitle = Between(badgeAdapter, "private static void Retitle(IssueLabel label)", "private static DataTemplate? TitleTemplate(");
        Check(retitle.Contains("var template = glyph is null && ink is null ? null : TitleTemplate(glyph, ink);")
            && retitle.Contains("var next = template ?? TryResource(TitleTemplateKey) as DataTemplate;") && retitle.Contains("label.TitleTemplate = next;"),
            "A badge without a glyph, or whose glyph template cannot be built, uses the shared text template");
        Check(glyphTemplate.Contains(": string.Concat(glyph.Select(character => \"&#x\" + ((int)character).ToString(\"X4\") + \";\"))"),
            "The glyph reaches the template as numeric character references, so its content is never parsed as markup");
        Check(glyphTemplate.Contains("<Grid ColumnSpacing=\\\"4\\\"") && glyphTemplate.Contains("<ColumnDefinition Width=\\\"Auto\\\" /><ColumnDefinition Width=\\\"*\\\" />")
            && glyphTemplate.Contains("FontFamily=\\\"{ThemeResource SymbolThemeFontFamily}\\\" FontSize=\\\"12\\\" FontWeight=\\\"Normal\\\"")
            && glyphTemplate.Contains("AutomationProperties.AccessibilityView=\\\"Raw\\\""),
            "The glyph keeps the hand-built badge's measures: 12px icon font at normal weight, 4 DIP before the text, hidden from screen readers");
        Check(glyphTemplate.Contains("Text=\\\"{Binding}\\\" FontFamily=\\\"{ThemeResource ContentControlThemeFontFamily}\\\" TextWrapping=\\\"NoWrap\\\"")
            && glyphTemplate.Contains("IsTextSelectionEnabled=\\\"True\\\""),
            "The text beside a glyph follows the user's font, stays on one line and stays selectable, like the text-only template");
        Check(glyphTemplate.Contains("var color = ink is null ? string.Empty : \" Foreground=\\\"{ThemeResource \" + InkBrushKey + \"}\\\"\";")
            && Occurrences(glyphTemplate, "Foreground") == 1 && !glyphTemplate.Contains("FontIcon", StringComparison.Ordinal),
            "Without an ink, glyph and text take the tone's colour from the title presenter: the template names a colour only for the status tones");
        Check(badgeAdapter.Contains("label.Visibility = string.IsNullOrEmpty(text) && label.Tag is null ? Visibility.Collapsed : Visibility.Visible;"),
            "A badge with a glyph and no text still shows; one with neither does not");
        Check(Regex.Matches(badgeAdapter, @"private static readonly [^\n]*").Select(match => match.Value.Trim())
                .SequenceEqual(new[] { "private static readonly Dictionary<string, DataTemplate?> TitleTemplates = new(StringComparer.Ordinal);" }),
            "The adapter's only static state is the template cache, which holds templates, not controls");

        // ---- D-10: the info badge follows the accent, like the other info elements on a card ----
        var darkTones = Between(badgeAdapter, "private const string DarkTones =", "private const string LightTones =");
        var lightTones = Between(badgeAdapter, "private const string LightTones =", "private const string ToneResources =");
        var toneResources = Between(badgeAdapter, "private const string ToneResources =", "</ResourceDictionary.ThemeDictionaries></ResourceDictionary>\";");
        Check(darkTones.Contains("x:Key=\\\"IssueLabelBlueBackgroundBrush\\\" Color=\\\"{ThemeResource SystemAccentColorLight2}\\\" Opacity=\\\"0.12\\\"")
            && lightTones.Contains("x:Key=\\\"IssueLabelBlueBackgroundBrush\\\" Color=\\\"{ThemeResource SystemAccentColorDark1}\\\" Opacity=\\\"0.12\\\"")
            && theme.Contains("\"@accent\" => light ? \"SystemAccentColorDark1\" : \"SystemAccentColorLight2\",")
            && theme.Contains("x:Key=\\\"NativeUiAccentTintBrush\\\" Color=\\\"{ThemeResource \" + accent + \"}\\\" Opacity=\\\"0.12\\\""),
            "The info badge fill is the accent at 12%, the same colour and strength as the card's accent tint");
        Check(darkTones.Contains("x:Key=\\\"IssueLabelBlueForegroundBrush\\\" Color=\\\"{ThemeResource SystemAccentColorLight3}\\\" />")
            && lightTones.Contains("x:Key=\\\"IssueLabelBlueForegroundBrush\\\" Color=\\\"{ThemeResource SystemAccentColorDark2}\\\" />")
            && theme.Contains("\"@accentText\" => light ? \"SystemAccentColorDark2\" : \"SystemAccentColorLight3\","),
            "The info badge text is the accent text colour the card's info text uses");
        Check(darkTones.Contains("x:Key=\\\"IssueLabelBlueBorderBrush\\\" Color=\\\"{ThemeResource SystemAccentColorLight2}\\\" Opacity=\\\"0.4\\\"")
            && lightTones.Contains("x:Key=\\\"IssueLabelBlueBorderBrush\\\" Color=\\\"{ThemeResource SystemAccentColorDark1}\\\" Opacity=\\\"0.4\\\""),
            "The info badge outline is the accent at 40%");
        Check(toneResources.Contains("<ResourceDictionary x:Key=\\\"Default\\\">\" + DarkTones") && toneResources.Contains("<ResourceDictionary x:Key=\\\"Light\\\">\" + LightTones")
            && toneResources.Contains("<ResourceDictionary x:Key=\\\"HighContrast\\\" />"),
            "The tone overrides exist for dark and light only; high contrast keeps the library's window and window-text colours");
        var create = Between(badgeAdapter, "internal static FrameworkElement Create(", "internal static void Update(");
        var resourcesAt = create.IndexOf("label.Resources = (ResourceDictionary)XamlReader.Load(ToneResources);", StringComparison.Ordinal);
        Check(resourcesAt >= 0 && resourcesAt < create.IndexOf("Apply(label, text, tone, accessibleName);", StringComparison.Ordinal)
            && Occurrences(badgeAdapter, "XamlReader.Load(ToneResources)") == 1,
            "Each badge gets its own tone overrides when it is created, before its tone is applied, and they are never shared between badges");
        // Executed with the default Windows accent ramp: the text must stay readable on the tinted fill over a card.
        Check(BadgeContrast(0x99EBFF, Over(0x4CC2FF, 0.12, 0x2C2C2C)) >= 4.5 && BadgeContrast(0x003E92, Over(0x0067C0, 0.12, 0xFFFFFF)) >= 4.5,
            "With the default accent, info badge text on its tint over the card surface clears 4.5:1 in dark and light");

        // ---- D-13: status tones get the same soft outline as the category tones; their text keeps full strength ----
        var statusRings = Between(badgeAdapter, "private const string StatusRings =", "private const string DarkTones =");
        Check(new[] { "Success", "Caution", "Critical" }.All(status => statusRings.Contains(
                "x:Key=\\\"SystemFillColor" + status + "Brush\\\" Color=\\\"{ThemeResource SystemFillColor" + status + "}\\\" Opacity=\\\"0.4\\\" />"))
            && Occurrences(statusRings, "x:Key=") == 3,
            "The brush a status badge draws its outline with is overridden on the badge to the same status colour at 40%, the strength of the info outline");
        Check(darkTones.TrimEnd().EndsWith("+ StatusRings;", StringComparison.Ordinal) && lightTones.TrimEnd().EndsWith("+ StatusRings;", StringComparison.Ordinal),
            "The soft outline applies in dark and light; high contrast keeps the system's window-text outline");
        Check(Regex.IsMatch(badgeAdapter, @"IssueLabelSeverity\.Success => ""SystemFillColorSuccess"",\s+IssueLabelSeverity\.Warning => ""SystemFillColorCaution"",\s+IssueLabelSeverity\.Error => ""SystemFillColorCritical"",\s+_ => null,"),
            "Exactly the three status tones carry their own text colour; the category tones inherit the library's");
        Check(glyphTemplate.Contains("<ResourceDictionary x:Key=\\\"Default\\\">\" + InkBrush(ink) + \"</ResourceDictionary>")
            && glyphTemplate.Contains("<ResourceDictionary x:Key=\\\"Light\\\">\" + InkBrush(ink) + \"</ResourceDictionary>")
            && glyphTemplate.Contains("<ResourceDictionary x:Key=\\\"HighContrast\\\">\" + InkBrush(\"SystemColorWindowTextColor\") + \"</ResourceDictionary>")
            && glyphTemplate.Contains("\"<Grid.Resources><ResourceDictionary><ResourceDictionary.ThemeDictionaries>\""),
            "Status text is painted by a brush defined on the title template's own root, full strength in dark and light and window text in high contrast, so the softened outline brush never reaches it");
        Check(Occurrences(glyphTemplate, "+ color + \"") == 2,
            "Glyph and text both take the status text colour");
        Check(retitle.Contains("if (ink is not null && template is null)") && retitle.Contains("label.Resources = new ResourceDictionary();"),
            "If the status title template cannot be built the badge drops its overrides and keeps the library colours, rather than showing its text at outline strength");
        Check(Regex.IsMatch(Between(badgeAdapter, "internal static void SetTone(", "internal static void SetAccessibleName("), @"label\.Severity = Severity\(tone\);\s+Retitle\(label\);")
            && create.Contains("Retitle(label);") && Between(badgeAdapter, "internal static void SetGlyph(", "internal static void SetTone(").Contains("Retitle(label);"),
            "The title template is chosen again whenever the tone or the glyph changes, and when the badge is created");
        if (devWinUiTheme is not null && File.Exists(devWinUiTheme))
        {
            var library = File.ReadAllText(devWinUiTheme);
            Check(new[] { ("Success", "Success"), ("Warning", "Caution"), ("Error", "Critical") }.All(pair =>
                {
                    var state = Between(library, "<VisualState x:Name=\"Severity" + pair.Item1 + "\">", "</VisualState>");
                    return state.Contains("<Setter Target=\"SeverityBorder.BorderBrush\" Value=\"{ThemeResource SystemFillColor" + pair.Item2 + "Brush}\" />")
                        && state.Contains("<Setter Target=\"SeverityTitle.Foreground\" Value=\"{ThemeResource SystemFillColor" + pair.Item2 + "Brush}\" />")
                        && state.Contains("<Setter Target=\"SeverityBorder.Background\" Value=\"{ThemeResource SystemFillColor" + pair.Item2 + "BackgroundBrush}\" />");
                }),
                "The pinned DevWinUI still draws a status badge's outline and text from one shared brush and its fill from another, which is what the override relies on");
        }
        // Executed with the WinUI default colours: status text on its fill (unchanged by this row) stays readable.
        Check(new[] { (0x6CCB5F, 0x393D1B), (0xFCE100, 0x433519), (0xFF99A4, 0x442726), (0x0F7B0F, 0xDFF6DD), (0x9D5D00, 0xFFF4CE), (0xC42B1C, 0xFDE7E9) }
                .All(pair => BadgeContrast(pair.Item1, pair.Item2) >= 4.5),
            "Status badge text on its fill clears 4.5:1 in dark and light");

        // The library facts the adapter relies on, read from the locked package so an upgrade that changes them
        // fails here (the runner hands us its theme dictionary: tests/native-ui/run.ps1).
        require(devWinUiTheme is not null && File.Exists(devWinUiTheme), "The locked DevWinUI theme dictionary was not passed to the tests: " + (devWinUiTheme ?? "<null>"));
        if (devWinUiTheme is not null && File.Exists(devWinUiTheme))
        {
            var library = File.ReadAllText(devWinUiTheme);
            var presenters = Regex.Matches(library, @"<ContentPresenter\s+x:Name=""(SeverityTitle|LabelTitle)""[^>]*>").Select(match => match.Value).ToArray();
            Check(presenters.Length == 2 && presenters.All(presenter => presenter.Contains("Content=\"{TemplateBinding Title}\"") && presenter.Contains("ContentTemplate=\"{TemplateBinding TitleTemplate}\"")),
                "The pinned DevWinUI label shows its Title through TitleTemplate in both title presenters, which is where the glyph template goes");
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
            var missing = new[] { "Gray", "Blue", "Purple", "Pink" }
                .SelectMany(tone => new[] { "Background", "Border", "Foreground" }.Select(role => "IssueLabel" + tone + role + "Brush"))
                .Where(key => !themesByKey.TryGetValue(key, out var themes) || !themes.SetEquals(new[] { "Default", "Light", "HighContrast" }))
                .Order(StringComparer.Ordinal).ToArray();
            Check(missing.Length == 0,
                "The pinned DevWinUI defines the twelve categorical tone keys (grey, blue, purple, pink by fill, ring, text) in its dark, light and high-contrast dictionaries"
                + (missing.Length == 0 ? "" : "; missing: " + string.Join(", ", missing)));
        }

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
