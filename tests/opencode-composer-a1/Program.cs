using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIFrontend.Services.OpenCode;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;


namespace AIFrontend.Pages
{
    public sealed partial class OpenCodePage
    {
        internal string LastLayout=""; // Shared inert native host compatibility only.
        private readonly DropDownButton BlankAgentButton = new();
        internal static void Main(string[] args)
        {
            var count = 0;
            void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
            foreach (var mode in new string?[] { "primary", "all", "future-mode", null })
            {
                Check(IsBlankAgentSelectable(new() { Mode = mode }), "normal/unknown mode selectable");
                Check(!IsBlankAgentSelectable(new() { Mode = mode, Hidden = true }), "hidden never selectable");
            }
            Check(!IsBlankAgentSelectable(new() { Mode = "subagent" }), "subagent-only absent");
            var page = new OpenCodePage();
            Check(ComposerChoiceLabels(760) == (true, true, true), "all choices expanded at 760");
            Check(ComposerChoiceLabels(600) == (true, true, false), "effort collapses first at 600");
            Check(ComposerChoiceLabels(480) == (true, false, false), "model collapses second at 480");
            Check(ComposerChoiceLabels(380) == (false, false, false), "agent collapses last at 380");
            Check(HideChildComposer("child", "child", "parent"), "child session has no composer");
            Check(!HideChildComposer("main", "main", null) && !HideChildComposer("new", "old", "parent")
                && !HideChildComposer(null, null, "parent"), "main, missing and stale session metadata do not hide another session composer");
            foreach (var lines in new[] { 1, 2, 3, 5, 8 })
            {
                var overTwo = ComposerOverTwoLines(lines * 20, 20, false);
                Check(UseSingleRowComposer(false, true, overTwo) == (lines <= 2), "wide session switches below editor only beyond two lines");
                Check(!UseSingleRowComposer(false, false, overTwo) && !UseSingleRowComposer(true, true, overTwo)
                    && !UseSingleRowComposer(true, false, overTwo), "narrow/new-session drafts always retain two rows");
                Check(!ShowComposerToolbarChoices(false, true), "wide session never duplicates sidebar choices/shortcuts, including multiline bottom row");
                Check(ShowComposerToolbarChoices(false, false), "narrow session keeps its existing picker/shortcut toolbar at every line count");
                Check(ShowComposerToolbarChoices(true, true) && ShowComposerToolbarChoices(true, false),
                    "new-session toolbar remains available without resident panels at either width");
            }
            Check(!ComposerOverTwoLines(40.4, 20, false) && ComposerOverTwoLines(40.4, 20, true)
                && !ComposerOverTwoLines(40, 20, false) && !ComposerOverTwoLines(40, 20, true), "half-pixel hysteresis suppresses measure jitter; two real lines return to one row");
            var compactWidth = ComposerSingleRowTextWidth(760, 32, 32, 8, 58);
            Check(compactWidth == 622 && ComposerSingleRowTextWidth(760, 32, 70, 8, 58) == 584,
                "single-row measurement reserves plus, send/queue actions, grid gaps and editor chrome");
            Check(ComposerSingleRowTextWidth(50, 32, 70, 8, 58) == 1, "measurement width is positive before layout settles");
            foreach (var (padding, border) in new[] { (9d, 1d), (12d, 2d), (0d, 0d), (9.5, 1.5) })
                Check(ShellPromptTextTop(padding, border) == padding + border,
                    "terminal prompt top equals the editor text top for native/custom/scaled chrome");
            foreach (var literal in new[] { "#123456", "#aBcDeF", "#000000", "#FFFFFF" })
            {
                page.ApplyBlankAgentColor(literal);
                var brush = page.BlankAgentButton.Foreground as SolidColorBrush;
                var rgb = Convert.ToUInt32(literal[1..], 16);
                Check(brush is not null && brush.Color.A == 255 && brush.Color.R == (byte)(rgb >> 16)
                    && brush.Color.G == (byte)(rgb >> 8) && brush.Color.B == (byte)rgb, "valid RGB preserved exactly");
            }
            foreach (var invalid in new string?[] { null, "", "accent", "#abc", "#12345678", "#GG0000", "red", "# 12345", "#12345 " })
            {
                page.ApplyBlankAgentColor("#123456");
                var before = page.BlankAgentButton.Clears;
                page.ApplyBlankAgentColor(invalid);
                Check(page.BlankAgentButton.Foreground is null && page.BlankAgentButton.Clears == before + 1, "invalid/unset removes local RGB and restores theme style");
            }
            foreach (var size in new[] { 0, 1, 3, 4, 5, 7 })
            {
                var model = new OpenCodeModelInfo { Variants = Enumerable.Range(0, size).Select(i => new OpenCodeModelVariant { Id = "opaque/" + i }).ToList() };
                var ticks = OpenCodeComposerEffort.Ticks(model, "opaque/1");
                Check(ticks.Count == size, "tick count comes from current model catalog, never fixed five");
                Check(ticks.Select(t => t.Id).SequenceEqual(model.Variants.Select(v => v.Id)), "raw variant identities/order retained");
                Check(ticks.Count(t => t.Selected) == (size > 1 ? 1 : 0), "only exact current variant highlighted");
                Check(ticks.All(t => double.IsFinite(t.Angle) && t.Angle >= -160 && t.Angle <= -20), "native gauge geometry finite for zero/one/many levels");
                Check(!OpenCodeComposerEffort.Ticks(model, null).Any(t => t.Selected)
                    && !OpenCodeComposerEffort.Ticks(model, "not-offered").Any(t => t.Selected), "default/unknown never fabricated as a level");
                Check(OpenCodeComposerEffort.Ticks(model with { Enabled = false }, "opaque/1").Count == 0, "disabled model does not offer effort levels");
            }
            Check(OpenCodeComposerEffort.Ticks(null, "high").Count == 0, "unavailable catalog hides gauge honestly");
            var root = args[0];
            var xaml = XDocument.Load(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Named(string name) => xaml.Descendants().Single(n => (string?)n.Attribute(x + "Name") == name);
            string? Attr(XElement node, string name) => (string?)node.Attribute(name);
            var terminal = Named("ComposerShellHost");
            var shellEditor = Named("ShellComposer");
            var shellPrompt = Named("ShellPromptGlyph");
            Check(terminal.Elements().SequenceEqual(new[] { shellEditor, shellPrompt })
                && Attr(terminal, "RowDefinitions") is null && Attr(terminal, "ColumnDefinitions") is null,
                "terminal overlays the same editor bounds; prompt does not take width away from command text");
            Check(Attr(shellPrompt, "Text") == "$" && Attr(shellEditor, "Grid.Column") is null
                && Attr(shellEditor, "AcceptsReturn") == "False" && Attr(shellEditor, "TextWrapping") == "NoWrap"
                && Attr(shellEditor, "PlaceholderText") is null, "terminal command follows the inset dollar prompt, without placeholder or extra lines");
            Check(Attr(shellEditor, "AutomationProperties.Name") == "Shell 命令"
                && Attr(shellEditor, "AutomationProperties.HelpText")!.Contains("Esc"), "terminal exit instructions remain accessible without visible copy");
            Check(Attr(shellPrompt, "FontFamily") == "{Binding FontFamily, ElementName=Composer}"
                && Attr(shellEditor, "FontFamily") == "{Binding FontFamily, ElementName=Composer}", "terminal retains the normal editor's live typography");
            foreach (var property in new[] { "Padding", "BorderThickness", "CornerRadius", "FontSize", "FontWeight", "FontStyle", "CharacterSpacing", "IsTextScaleFactorEnabled", "FlowDirection" })
                Check(Attr(shellEditor, property) == "{Binding " + property + ", ElementName=Composer}", "terminal/editor geometry and typography stay identical: " + property);
            // FrameworkElement.Language rejects a Binding at XAML load (XamlParseException, page fails to open).
            Check(Attr(shellEditor, "Language") is null && Attr(shellPrompt, "Language") is null, "terminal does not bind Language (not bindable in XAML)");
            Check(Attr(terminal, "Width") == "{Binding ActualWidth, ElementName=Composer}"
                && Attr(terminal, "Height") == "{Binding ActualHeight, ElementName=Composer}"
                && Attr(terminal, "Padding") is null && Attr(terminal, "Margin") is null,
                "separate terminal surface follows the measured editor without extra or negative padding/margins");
            Check(Named("ComposerCard").Elements().Single() == Named("ComposerSurfaceRoot")
                && Named("ComposerNormalSurface").Parent == terminal.Parent
                && Named("ComposerNormalSurface").Descendants().Contains(Named("ComposerInputLayout")),
                "one card owns both modes and normal layout stays measured underneath terminal overlay");
            foreach (var mode in new[] { "Default", "Light", "HighContrast" })
            {
                // The palette lives in the page theme dictionaries: an element cannot resolve its own
                // attributes from its own Resources, which made the page fail to load.
                var palette = xaml.Root!.Elements().First(n => n.Name.LocalName == "Page.Resources").Descendants()
                    .First(n => n.Name.LocalName == "ResourceDictionary" && (string?)n.Attribute(x + "Key") == mode);
                foreach (var (key, color, systemBrush) in new[] {
                    ("ShellTerminalBackground", "#0C0C0C", "SystemColorWindowColorBrush"),
                    ("ShellTerminalForeground", "#CCCCCC", "SystemColorWindowTextColorBrush"),
                    ("ShellTerminalPrompt", "#13A10E", "SystemColorWindowTextColorBrush") })
                {
                    var brush = palette.Elements().Single(n => (string?)n.Attribute(x + "Key") == key);
                    Check(mode == "HighContrast" ? Attr(brush, "ResourceKey") == systemBrush : Attr(brush, "Color") == color,
                        "terminal palette follows Campbell in both app themes and system brushes in HC: " + mode + "/" + key);
                }
            }
            foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
                foreach (var part in new[] { "Background", "BorderBrush", "Foreground" })
                {
                    var resource = shellEditor.Descendants().Single(n => (string?)n.Attribute(x + "Key") == "TextControl" + part + state);
                    Check(Attr(resource, "ResourceKey") == (part == "Foreground" ? "ShellTerminalForeground" : "ShellTerminalBackground"),
                        "native text control states retain terminal colors: " + part + state);
                }
            Check(Attr(shellEditor, "UseSystemFocusVisuals") == "False"
                && shellEditor.Descendants().Any(n => n.Name.LocalName == "Border" && Attr(n, "BorderThickness") == "{TemplateBinding BorderThickness}")
                && !shellEditor.Descendants().Any(n => n.Name.LocalName == "Button"), "terminal preserves native editor chrome dimensions without adding a focus underline or clear button");
            var shell = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.ShellComposer.cs"));
            Check(Attr(shellPrompt, "VerticalAlignment") == "Top"
                && Attr(shellPrompt, "LineStackingStrategy") == "BlockLineHeight"
                && Attr(shellPrompt, "FontSize") == Attr(shellEditor, "FontSize")
                && Attr(shellPrompt, "FontWeight") == Attr(shellEditor, "FontWeight")
                && Attr(shellPrompt, "FontStyle") == Attr(shellEditor, "FontStyle")
                && Attr(shellPrompt, "CharacterSpacing") == Attr(shellEditor, "CharacterSpacing")
                && Attr(shellPrompt, "IsTextScaleFactorEnabled") == Attr(shellEditor, "IsTextScaleFactorEnabled"),
                "prompt shares editor typography and follows first-line top instead of box centering");
            Check(shell.Contains("ShellPromptTextTop(padding.Top, border.Top)")
                && shell.Contains("measure.FontFamily = ShellComposer.FontFamily")
                && shell.Contains("measure.FontSize = ShellComposer.FontSize")
                && shell.Contains("measure.DesiredSize.Height") && shell.Contains("ShellPromptGlyph.LineHeight = line")
                && shell.Contains("UpdateShellPromptBaseline();"),
                "prompt offset uses native text padding/border and line height is measured from the same live font");
            Check(shell.Contains("ComposerNormalSurface.Opacity = 0;") && shell.Contains("ComposerNormalSurface.IsEnabled = false;")
                && shell.Contains("ComposerNormalSurface.IsHitTestVisible = false;")
                && !shell.Contains("ComposerInputLayout.Visibility =") && !shell.Contains("FadeInComposerSurface")
                && shell.Contains("Composer.TransformToVisual(ComposerSurfaceRoot).TransformPoint(new Point(0, 0))")
                && shell.Contains("Composer.LayoutUpdated +=") && !shell.Contains("RequestedTheme"),
                "terminal switch keeps normal size/placement measured across responsive rows, disables hidden controls and overlays without a fade");
            Check(Named("TerminalComposerMode").Descendants().Where(n => n.Name.LocalName == "Setter")
                .All(n => Attr(n, "Target") is "ComposerCard.Background" or "ComposerCard.BorderBrush"),
                "terminal mode changes only card colors, never its padding, thickness, radius or dimensions");
            Check(Named("TerminalComposerMode").Descendants().Any(n => Attr(n, "Target") == "ComposerCard.Background"
                && Attr(n, "Value") == "{ThemeResource ShellTerminalBackground}")
                && shell.Contains("GoToState(this, \"TerminalComposerMode\", false)")
                && shell.Contains("GoToState(this, \"NormalComposerMode\", false)"), "A3 whole card changes to terminal palette and restores normal card on exit");
            Check(shell.Contains("ShellComposer.IsEnabled = ready;") && shell.Contains("RequestComposerSubmission();")
                && shell.Contains("ExecuteSessionShellGuardedAsync(binding.SessionId, command")
                && shell.Contains("InfoBarSeverity.Error, \"Shell 请求失败\""), "terminal retains keyboard submission, busy disabling and error surfacing");
            foreach (var removed in new[] { "ShellSubmitButton", "ShellExitButton", "ShellModeLabel", "ShellStatusText" })
                Check(!shell.Contains(removed) && !xaml.Descendants().Any(n => (string?)n.Attribute(x + "Name") == removed),
                    "removed terminal adornment has no remaining control or handler: " + removed);
            Check(!xaml.Descendants().Any(n => (string?)n.Attribute(x + "Name") == "SessionStatusLine")
                && Named("RightSessionDiagnostics").Descendants().Contains(Named("AgentSelectorHost")), "identity row retired; switch diagnostics/recovery retained in overview");
            foreach (var name in new[] { "BlankAgentButton", "BlankModelButton", "BlankStrengthButton", "ComposerSubagentsButton", "ComposerConnectionButton", "ComposerExpandButton", "ComposerQueueButton", "SendStopButton" })
                Check(Attr(Named(name), "AutomationProperties.Name") is not null && Attr(Named(name), "ToolTipService.ToolTip") is not null, "every compact entry named and tooltip reachable: " + name);
            var combo = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeNativeComboBox.cs"));
            Check(combo.Contains("AccentTextFillColorPrimaryBrush") && combo.Contains("SolidColorBrush(color)"), "agent glyph uses actual RGB with dynamic theme accent fallback");
            var blank = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs"));
            Check(blank.Contains("_blankAgents.Where(IsBlankAgentSelectable)") && blank.Contains("response.Data!.Where(IsBlankAgentSelectable)"), "defaults and menu use same selection predicate");
            Check(blank.Contains("ToolTipService.SetToolTip(BlankAgentButton, agent?.Description)")
                && blank.Contains("OpenCodeAgentPickerDialog.ShowAsync"), "current agent retains description; choices use the shared compact native picker");
            Check(!blank.Contains("IsEnabled = agent.Mode"), "subagent entries not rendered disabled");
            var layout = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.ComposerLayout.cs"));
            Check(layout.Contains("ArrangeComposerRows(singleRow)") && layout.Contains("Grid.SetRow(ComposerActions, singleRow ? 0 : 1)")
                && layout.Contains("Grid.SetRow(ComposerToolbarLeft, singleRow ? 0 : 1)")
                && !layout.Contains("Children.Remove") && layout.Contains("Composer.TextCompositionEnded +="), "responsive rows change attached layout only, never editor parent or identity");
            Check(layout.Contains("ShowComposerToolbarChoices(_newSessionDraftActive, _sidebarLayout.IsWide)")
                && layout.Contains("BlankToolbarChoices.Visibility = choices ? Visibility.Visible : Visibility.Collapsed;")
                && layout.Contains("ComposerToolbarMiddle.Visibility = choices && !_newSessionDraftActive ? Visibility.Visible : Visibility.Collapsed;"),
                "CB1 toolbar contents follow resident-panel mode, not editor line count or row arrangement");
            Check(Named("BlankToolbarChoices").Descendants().Contains(Named("ComposerToolbarMiddle"))
                && new[] { "BlankAgentButton", "BlankModelButton", "BlankStrengthButton", "ComposerSubagentsButton", "ComposerConnectionButton" }
                    .All(name => Named(name).Ancestors().Contains(Named("BlankToolbarChoices")))
                && Named("ComposerToolbarLeft").Elements().Single() == Named("ComposerLeadingHost")
                && !Named("ComposerActions").Ancestors().Contains(Named("BlankToolbarChoices")),
                "CB1 wide mode hides all five duplicate controls as a group while retaining plus and send/stop hosts");
            Check(layout.Contains("ComposerSingleRowTextWidth(ComposerInputLayout.ActualWidth,")
                && layout.Contains("ComposerOverTwoLines(measure.DesiredSize.Height, line, _composerOverTwoLines)")
                && !layout.Contains("ComposerEditorHost.ActualWidth")
                && layout.IndexOf("ComposerOverTwoLines(measure.DesiredSize.Height", StringComparison.Ordinal)
                    < layout.IndexOf("ArrangeComposerRows(singleRow)", StringComparison.Ordinal),
                "both layouts test identical compact editor width before changing attached properties, preventing wrap oscillation");
            Check(layout.Contains("Grid.SetColumn(ComposerEditorHost, singleRow ? 1 : 0)")
                && layout.Contains("Grid.SetColumnSpan(ComposerEditorHost, singleRow ? 1 : 3)")
                && !layout.Contains("Composer.Focus(") && !layout.Contains("Composer.SelectionStart =")
                && !layout.Contains("Composer.Text ="), "long drafts span the full top row without replacing text, focus or caret");
            Check(Named("ComposerEditorHost").Parent == Named("ComposerInputLayout")
                && Named("ComposerActions").Parent == Named("ComposerInputLayout")
                && Named("ComposerToolbarLeft").Parent == Named("ComposerInputLayout")
                && Named("ComposerExpandButton").Parent == Named("Composer").Parent, "editor, actions and plus share a stable layout; fullscreen stays inside input");
            var binding = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.ComposerToolbar.cs"));
            Check(binding.Contains("RenderNativePickerChoices(BlankAgentButton, BlankModelButton, BlankStrengthButton, enabled, labels.Model)")
                && combo.Contains("OpenCodeModelLogo.Label(") && combo.Contains("OpenCodeModelPresentation.VariantLabel(id)"), "choices use common native model logo/name and display-only effort labels");
            Check(binding.Contains("description = info?.Description") && binding.Contains("owns ? _agentSelector.CurrentAgentDescription : null")
                && binding.Contains("ToolTipService.SetToolTip(BlankAgentButton, description ??"), "visible composer agent tooltip uses owned draft/session description in both responsive modes");
            var card = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeSessionControlCard.cs"));
            Check(card.Contains("internal OpenCodeNativeComboBox AgentButton") && combo.Contains("DisplayName(row.Name)"), "sidebar retains a real native agent ComboBox with full friendly name");
            Check(card.Contains("Describe(AgentButton, \"智能体：\" + agent, scope, (agentDescription ??")
                && card.Contains("ToolTipService.SetToolTip(button, tooltip ??"), "visible sidebar agent tooltip shows description rather than scope");
            var cardBinding = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.SessionControlCard.cs"));
            Check(cardBinding.Contains("BlankStrengthButton.IsEnabled, agent?.Description")
                && cardBinding.Contains("ownsSelection ? _agentSelector.CurrentAgentDescription : null"), "sidebar descriptions read from the same fenced draft/current selection as the name and color");
            Check(binding.Contains("_insights?.CurrentModelInfo") && !binding.Contains("ListModelsAsync"), "toolbar reuses fenced metadata without duplicate reads");
            Check(binding.Contains("SessionPaneToggleButton_Click(sender, e)") && binding.Contains("OpenSessionControlModelPicker(variantOnly: true)")
                && binding.Contains("BlankStrengthButton_Click(sender, e)"), "drawer and existing independent strength routes preserved");
            Check(!binding.Contains("SendStopButton.IsEnabled") && !binding.Contains("ComposerQueueButton.IsEnabled"), "toolbar presentation never changes send/queue availability policy");
            foreach (var name in new[] { "BlankAgentButton", "BlankStrengthButton" })
                Check(Named(name).Name.LocalName == "OpenCodeNativeComboBox" && Attr(Named(name), "Click") is null, "native ComboBox control, not click-opened flyout: " + name);
            var modelDialog = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeComposerNativeDialogs.cs"));
            var pickerFlyout = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodePickerFlyout.cs"));
            Check(Named("BlankModelButton").Name.LocalName == "Button"
                && cardBinding.Contains("model.Click += ComposerModelButton_Click")
                && binding.Contains("OpenSessionControlModelPicker(variantOnly: false, anchor: sender as FrameworkElement)")
                && blank.Contains("ChooseBlankModelAsync(basis, token, anchor: sender as FrameworkElement)")
                && modelDialog.Contains("new AutoSuggestBox") && modelDialog.Contains("search.TextChanged +=")
                && pickerFlyout.Contains("flyout.ShowAt(anchor)") && pickerFlyout.Contains("FlyoutPlacementMode.TopEdgeAlignedLeft"),
                "model trigger removes the redundant dropdown arrow but retains both anchored searchable native picker routes");
            Check(Named("BlankProjectButton").Name.LocalName == "DropDownButton", "directory retains native dropdown");
            Check(Named("BlankProjectButton").Ancestors().Contains(Named("NewSessionHeader"))
                && Named("BlankPreferencesHost").Descendants().Any(n => Attr(n, "Text") == "中开始新会话"), "directory sentence is under the wordmark, above composer");
            Check(layout.Contains("var maximum = OpenCodeComposerHeightPolicy.ViewportHeight(line, chrome)")
                && !layout.Contains("line * 8") && !layout.Contains("blankMinimum"), "new and existing drafts use the same native five-line cap");
            Check(layout.Contains("ComposerCell.Visibility = child ? Visibility.Collapsed")
                && layout.Contains("ComposerQueueHost.Visibility = child ? Visibility.Collapsed"), "SA3 removes composer and queue host rather than disabling their chrome");
            var full = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.FullScreenComposer.cs"));
            Check(full.Contains("PageRoot.Children.Add(overlay)") && full.Contains("Grid.SetRow(overlay, 1)")
                && full.Contains("AcceptsReturn = true, TextWrapping = TextWrapping.Wrap"), "fullscreen native editor covers the page below tabs");
            Check(full.Contains("TryApplyEditorInput(_fullScreenToken") && full.Contains("OwnsComposerBinding(binding)")
                && full.Contains("VirtualKey.Escape") && full.Contains("VirtualKey.Control")
                && full.Contains("_composerController?.Snapshot.Token == token"), "fullscreen shares the guarded draft and fences keyboard/send origins");
            Console.WriteLine($"PASS A1: {count} focused checks (production policy/color + structural XAML/wiring; no native UI)");
        }
    }
}
