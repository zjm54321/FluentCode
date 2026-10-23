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
            Check(!ExpandBlankComposerChoices(true, 599.99) && ExpandBlankComposerChoices(true, 600), "new-session labels collapse only below 600 editor epx");
            Check(!ExpandBlankComposerChoices(false, 960), "session never duplicates labeled sidebar choices");
            Check(UseSingleRowComposer(false, true) && !UseSingleRowComposer(false, false)
                && !UseSingleRowComposer(true, true) && !UseSingleRowComposer(true, false), "only wide sessions use a single composer row");
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
            Check(!xaml.Descendants().Any(n => (string?)n.Attribute(x + "Name") == "SessionStatusLine")
                && Named("RightSessionDiagnostics").Descendants().Contains(Named("AgentSelectorHost")), "identity row retired; switch diagnostics/recovery retained in overview");
            foreach (var name in new[] { "BlankAgentButton", "BlankModelButton", "BlankStrengthButton", "ComposerSubagentsButton", "ComposerConnectionButton", "ComposerExpandButton", "ComposerQueueButton", "SendStopButton" })
                Check(Attr(Named(name), "AutomationProperties.Name") is not null && Attr(Named(name), "ToolTipService.ToolTip") is not null, "every compact entry named and tooltip reachable: " + name);
            Check(Named("BlankAgentButton").Descendants().Any(n => Attr(n, "Property") == "Foreground"
                && Attr(n, "Value") == "{ThemeResource AccentTextFillColorPrimaryBrush}"), "dynamic theme accent setter (not static/cached brush)");
            var blank = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs"));
            Check(blank.Contains("_blankAgents.Where(IsBlankAgentSelectable)") && blank.Contains("response.Data!.Where(IsBlankAgentSelectable)"), "defaults and menu use same selection predicate");
            Check(blank.Contains("ToolTipService.SetToolTip(BlankAgentButton, agent?.Description)") && blank.Contains("ToolTipService.SetToolTip(item, agent.Description)"), "current and candidates expose description");
            Check(!blank.Contains("IsEnabled = agent.Mode"), "subagent entries not rendered disabled");
            var layout = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.ComposerLayout.cs"));
            Check(layout.Contains("ArrangeComposerRows(singleRow)") && layout.Contains("Grid.SetRow(ComposerActions, singleRow ? 0 : 1)")
                && layout.Contains("Grid.SetRow(ComposerToolbarLeft, singleRow ? 0 : 1)")
                && !layout.Contains("Children.Remove") && layout.Contains("Composer.TextCompositionEnded +="), "responsive rows change attached layout only, never editor parent or identity");
            Check(Named("ComposerEditorHost").Parent == Named("ComposerInputLayout")
                && Named("ComposerActions").Parent == Named("ComposerInputLayout")
                && Named("ComposerToolbarLeft").Parent == Named("ComposerInputLayout")
                && Named("ComposerExpandButton").Parent == Named("Composer").Parent, "editor, actions and plus share a stable layout; fullscreen stays inside input");
            var binding = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.ComposerToolbar.cs"));
            Check(binding.Contains("OpenCodeModelLogo.Label(") && binding.Contains("OpenCodeAgentPresentation.Apply(agentLabel, color)")
                && binding.Contains("expanded ? OpenCodeModelPresentation.VariantLabel(selected?.Variant)"), "expanded choices retain colored agent name, model logo/name and translated effort");
            Check(binding.Contains("description = info?.Description") && binding.Contains("owns ? _agentSelector.CurrentAgentDescription : null")
                && binding.Contains("ToolTipService.SetToolTip(BlankAgentButton, description ??"), "visible composer agent tooltip uses owned draft/session description in both responsive modes");
            var card = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCode/OpenCodeSessionControlCard.cs"));
            Check(card.Contains("_agentName = OpenCodeAgentPresentation.Name(string.Empty)") && card.Contains("AccentTextFillColorPrimaryBrush")
                && card.Contains("AgentButton.Style = (Style)XamlReader.Load"), "sidebar full name wraps and uncolored agent returns to dynamic system accent style");
            Check(card.Contains("Describe(AgentButton, \"智能体：\" + agent, scope, agentDescription ??")
                && card.Contains("ToolTipService.SetToolTip(button, tooltip ??"), "visible sidebar agent tooltip shows description rather than scope");
            var cardBinding = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.SessionControlCard.cs"));
            Check(cardBinding.Contains("BlankStrengthButton.IsEnabled, agent?.Description")
                && cardBinding.Contains("ownsSelection ? _agentSelector.CurrentAgentDescription : null"), "sidebar descriptions read from the same fenced draft/current selection as the name and color");
            Check(binding.Contains("OpenCodeComposerEffort.Ticks(model, selected?.Variant)") && binding.Contains("_insights?.CurrentModelInfo")
                && !binding.Contains("ListModelsAsync"), "gauge reuses existing fenced Model.Info metadata without duplicate reads");
            Check(binding.Contains("SessionPaneToggleButton_Click(sender, e)") && binding.Contains("OpenSessionControlModelPicker(variantOnly: true)")
                && binding.Contains("BlankStrengthButton_Click(sender, e)"), "drawer and existing independent strength routes preserved");
            Check(!binding.Contains("SendStopButton.IsEnabled") && !binding.Contains("ComposerQueueButton.IsEnabled"), "toolbar presentation never changes send/queue availability policy");
            Console.WriteLine($"PASS A1: {count} focused checks (production policy/color + structural XAML/wiring; no native UI)");
        }
    }
}
