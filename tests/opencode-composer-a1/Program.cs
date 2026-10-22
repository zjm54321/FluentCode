using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIFrontend.Services.OpenCode;
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
            // Exercise pane budgets down to an intentionally tiny width. This proves allocation,
            // not native text measurement or a claim about the Windows minimum window size.
            foreach (var available in new[] { 0d, 12, 64, 140, 200, 320, 500, 900 })
            {
                var w = BlankToolbarWidths(available);
                Check(w.Agent >= 0 && w.Model >= 0 && w.Variant >= 0, "nonnegative control budgets");
                Check(w.Agent + w.Model + w.Variant <= Math.Max(0, available - 12) + 0.001, "controls fit after two gaps");
                Check(w.Agent <= Math.Max(0,available-12)*0.35+0.001 && w.Model <= 260 && w.Variant <= 112, "full-name wrapping gets responsive agent budget, not old104 cap");
            }
            var root = args[0];
            var xaml = XDocument.Load(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XElement Named(string name) => xaml.Descendants().Single(n => (string?)n.Attribute(x + "Name") == name);
            string? Attr(XElement node, string name) => (string?)node.Attribute(name);
            Check(Attr(Named("SessionStatusLine"), "ColumnDefinitions") == "Auto,*,*,Auto"
                && Attr(Named("AgentSelectorHost"), "Grid.Column") == "1", "normal agent host has finite star-column measure so full names wrap, rather than overflow an Auto column");
            var logo = Named("NewConversationWordmark");
            Check(Attr(logo, "Width") == "520" && Attr(logo, "Height") == "93.3333333333", "logo grows from 360 to 520 DIP with aspect preserved");
            Check(Attr(logo.Parent!, "StretchDirection") == "DownOnly" && Attr(logo.Parent!, "MaxWidth") == "520", "wordmark can shrink on narrow panes");
            var toolbar = Named("BlankToolbarChoices");
            Check(Attr(toolbar, "ColumnDefinitions") == "Auto,Auto,Auto" && Attr(toolbar, "HorizontalAlignment") == "Left", "model/variant adjacent rather than spread across row");
            Check(Attr(toolbar, "Margin") == "0,8,0,4" && Attr(toolbar, "VerticalAlignment") == "Center", "choices share bottom-row inset/center");
            foreach (var name in new[] { "BlankAgentButton", "BlankModelButton", "BlankStrengthButton" })
                Check((name=="BlankAgentButton"?Attr(Named(name),"MinHeight")=="32"&&Attr(Named(name),"Height")is null:Attr(Named(name),"Height")=="32") && Attr(Named(name), "MinWidth") == "0" && Attr(Named(name), "VerticalAlignment") == "Center", "choices centered; agent grows for complete wrapped name, others remain32DIP");
            Check(Named("BlankAgentButton").Descendants().Any(n => Attr(n, "Property") == "Foreground"
                && Attr(n, "Value") == "{ThemeResource AccentTextFillColorPrimaryBrush}"), "dynamic theme accent setter (not static/cached brush)");
            Check(Attr(Named("ComposerActions"), "HorizontalAlignment") == "Right" && Attr(Named("SendStopButton"), "Height") == "32", "send stays right at same height");
            var blank = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.BlankConversation.cs"));
            Check(blank.Contains("_blankAgents.Where(IsBlankAgentSelectable)") && blank.Contains("response.Data!.Where(IsBlankAgentSelectable)"), "defaults and menu use same selection predicate");
            Check(blank.Contains("ToolTipService.SetToolTip(BlankAgentButton, agent?.Description)") && blank.Contains("ToolTipService.SetToolTip(item, agent.Description)"), "current and candidates expose description");
            Check(!blank.Contains("IsEnabled = agent.Mode"), "subagent entries not rendered disabled");
            var layout = File.ReadAllText(Path.Combine(root, "src/AIFrontend/Pages/OpenCodePage.ComposerLayout.cs"));
            Check(layout.Contains("ComposerLeadingHost.VerticalAlignment = _blankComposerPlacement ? VerticalAlignment.Center : VerticalAlignment.Bottom")
                && layout.Contains("ComposerActions.VerticalAlignment = _blankComposerPlacement ? VerticalAlignment.Center : VerticalAlignment.Bottom"), "blank centers plus/send, normal session restores bottom");
            Check(layout.Contains("Grid.SetRow(ComposerLeadingHost, bottom ? 1 : 0)") && layout.Contains("Grid.SetRow(ComposerActions, bottom ? 1 : 0)"), "plus/send share row with choices");
            Console.WriteLine($"PASS A1: {count} focused checks (production policy/color + structural XAML/wiring; no native UI)");
        }
    }
}
