using System;
using System.Globalization;
using System.Linq;
using AIFrontend.Pages.OpenCode;

internal static class AgentColorTests
{
    internal static void Run()
    {
        var names = new[] { "orchestrator", "explorer", "oracle", "designer", "fixer", "council", "councillor", "secretary", "librarian", "observer" };
        var light = new[] { "#0078D4", "#038387", "#8764B8", "#BF0077", "#CA5010", "#986F0B", "#107C10", "#005B70", "#8E562E", "#486860" };
        var dark = new[] { "#4CC2FF", "#00B7C3", "#B4A0FF", "#FF6FCF", "#FF9349", "#FCE100", "#6CCB5F", "#60CDFF", "#E0A060", "#9AB8B0" };
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        Check(names.Select(n => OpenCodeAgentIcons.Color(n, null, false)).Distinct().Count() == names.Length, "AC1: named identities are distinct");
        for (var i = 0; i < names.Length; i++)
        {
            Check(OpenCodeAgentIcons.Color(" " + names[i].ToUpperInvariant() + " ", null, false) == light[i], "AC1: exact normalized light palette");
            Check(OpenCodeAgentIcons.Color(names[i], null, true) == dark[i], "AC1: exact dark palette");
            Check(Contrast(light[i], "#F3F3F3") >= 3 && Contrast(dark[i], "#2C2C2C") >= 3, "AC1: dock glyph contrast >= 3:1");
        }
        foreach (var name in new[] { "councillor-alpha", "councillor-beta", "councillor-gamma" })
            Check(OpenCodeAgentIcons.Color(name, null, true) == "#6CCB5F", "AC1: councillor family identity");
        Check(OpenCodeAgentIcons.Color("raw", "#00FFFF", false) == "#00FFFF"
            && OpenCodeAgentIcons.Color("fixer", "#12aBcD", true) == "#12aBcD", "AC1: backend RGB wins unchanged in either theme");
        Check(OpenCodeAgentIcons.Color("fixer", "invalid", false) == "#CA5010", "AC1: malformed backend color uses identity");
        for (var i = 0; i < 128; i++)
        {
            var name = "unknown-" + i;
            var value = OpenCodeAgentIcons.Color(name, null, false);
            Check(value == OpenCodeAgentIcons.Color(name.ToUpperInvariant(), null, false), "AC1: stable unknown-name hash");
            Check(Contrast(value, "#F3F3F3") >= 3
                && Contrast(OpenCodeAgentIcons.Color(name, null, true), "#2C2C2C") >= 3, "AC1: extra palette contrast >= 3:1");
        }
        Console.WriteLine("PASS AC1 named/extra palettes, runtime precedence, stable hashing, light/dark dock contrast");
    }

    private static double Contrast(string first, string second)
    {
        double Luminance(string hex)
        {
            var rgb = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            double Linear(uint value) { var c = value / 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * Linear((rgb >> 16) & 255) + 0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
