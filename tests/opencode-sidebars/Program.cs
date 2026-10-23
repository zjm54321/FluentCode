using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }

    private static void Main(string[] args)
    {
        var narrow = new OpenCodeSidebarLayout();
        Check(!narrow.IsWide && !narrow.LeftOpen && !narrow.RightOpen, "initial narrow drawers closed");
        Check(!narrow.Resize(1351).IsWide, "enter threshold exclusive below 1352");
        var wide = narrow.Resize(1352);
        Check(wide.IsWide && wide.LeftOpen && wide.RightOpen, "both resident at 1352 epx");
        Check(wide.ForSession(true) == new OpenCodeSidebarSurface(true, true, true, false), "session owns wide resident panels");
        Check(wide.ForSession(false) == default, "home and new-session have no residents or drawer entries at wide width");
        Check(narrow.Toggle(OpenCodeSidebarDrawer.Left).ForSession(false) == default, "leaving session hides an open narrow drawer and its entry");
        Check(narrow.ForSession(true).ShowDrawerButtons, "narrow session retains drawer entries");
        Check(wide.Resize(1304).IsWide, "leave boundary inclusive at 1304");
        Check(wide.Resize(1330).IsWide && !narrow.Resize(1330).IsWide, "hysteresis depends on previous mode");
        Check(!wide.Resize(1303.99).IsWide, "leave wide below 1304");
        foreach (var width in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Check(wide.Resize(width) == wide, "ignore unmeasured/invalid page width " + width);
        Check(narrow.ShowHeaderContext && narrow.ShowHeaderConnection && narrow.ShowHeaderOverview, "all three header entries remain in narrow");
        Check(!wide.ShowHeaderContext && !wide.ShowHeaderConnection && !wide.ShowHeaderOverview, "hide all three redundant header entries in wide");
        var left = narrow.Toggle(OpenCodeSidebarDrawer.Left);
        Check(left.LeftOpen && !left.RightOpen, "left drawer toggle opens");
        Check(left.Toggle(OpenCodeSidebarDrawer.Left) == narrow, "left drawer toggle closes");
        var right = left.Toggle(OpenCodeSidebarDrawer.Right);
        Check(right.RightOpen && !right.LeftOpen, "opening right atomically replaces left");
        Check(right.Toggle(OpenCodeSidebarDrawer.Right) == narrow, "overview is a two-way toggle");
        Check(right.Dismiss(OpenCodeSidebarDrawer.Left) == right, "late left light-dismiss cannot close right");
        Check(right.Dismiss(OpenCodeSidebarDrawer.Right) == narrow, "native right light-dismiss clears drawer");
        Check(left.Dismiss(OpenCodeSidebarDrawer.Left) == narrow, "native left light-dismiss clears drawer");
        Check(right.Escape() == narrow && left.Escape() == narrow, "Esc closes either drawer");
        Check(narrow.Escape() == narrow, "Esc with no drawer is a no-op");
        Check(wide.Toggle(OpenCodeSidebarDrawer.Left) == wide && wide.Toggle(OpenCodeSidebarDrawer.Right) == wide,
            "resident panels cannot be toggled shut");
        Check(wide.Escape() == wide && wide.Dismiss(OpenCodeSidebarDrawer.Left) == wide, "Esc/light-dismiss cannot hide residents");
        Check(left.Resize(1352) == wide && wide.Resize(1200) == narrow, "mode transitions clear transient drawer state");
        Check(narrow.Toggle(OpenCodeSidebarDrawer.None) == narrow, "None is not a drawer");
        Check(left.AnimateDrawers(true) && !left.AnimateDrawers(false), "reduced motion disables drawer animation");
        Check(!wide.AnimateDrawers(true) && !wide.AnimateDrawers(false), "resident layout changes never slide");
        for (var i = 0; i < 100; i++)
        {
            wide = wide.Resize(i % 2 == 0 ? 1320 : 1340);
            narrow = narrow.Resize(i % 2 == 0 ? 1320 : 1340);
        }
        Check(wide.IsWide && !narrow.IsWide, "resize jitter inside dead band does not oscillate");

        var repo = args[0];
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tokens = XDocument.Load(Path.Combine(repo, "src/AIFrontend/Styles/AppTokens.xaml"));
        double Token(string key) => double.Parse(tokens.Descendants().Single(e => (string?)e.Attribute(x + "Key") == key).Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Check(Token("OpenCodeSessionPaneLength") == OpenCodeSidebarLayout.LeftWidth
            && Token("OpenCodeInsightsPaneLength") == OpenCodeSidebarLayout.RightWidth, "threshold width budget matches current pane tokens");
        Console.WriteLine($"PASS {_checks} sidebar policy/token checks (headless; native visuals require user checks).");
    }
}
