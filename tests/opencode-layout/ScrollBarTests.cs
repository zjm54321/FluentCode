using System;
using AIFrontend.Pages.OpenCode;

internal static class ScrollBarTests
{
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _passed++;
        Console.WriteLine("PASS: SB3a: " + name);
    }

    private static double NativeFactor(double railTravel, double maximum, double viewportLength)
    {
        // AnnotatedScrollBar.cpp:461-473 (WinUI): label and panning multiplier mapping.
        var range = maximum == 0 ? 1 : maximum;
        return railTravel / range * (1 - viewportLength / (viewportLength + range));
    }

    private static void Verify(double maximum, double offset, string state)
    {
        var position = OpenCodeScrollBarProjection.Position(maximum, offset);
        Check(position.Offset == offset, state + ": controller/expression offset equals VerticalOffset");
        Check(position.Maximum == maximum && position.ViewportLength == 0, state + ": reachable-position range, no document-span compression");
        var factor = NativeFactor(500, position.Maximum, position.ViewportLength);
        var multiplier = -1 / factor; // Written by native PanningInfo, not the consumer.
        var thumb = position.Offset / -multiplier;
        Check(Math.Abs(thumb - offset / maximum * 500) < 0.0001, state + ": native thumb tracks scroll progress");
        Check(Math.Abs(thumb / factor - offset) < 0.0001, state + ": native drag/track round trip preserves offset");
    }

    private static void Main()
    {
        Check(Math.Abs(400 * NativeFactor(500, 400, 600) / 500 - 0.4) < 0.0001,
            "regression reproduces old 40%-at-bottom mapping");
        Verify(400, 0, "top");
        Verify(400, 200, "middle");
        Verify(400, 400, "bottom");
        Verify(900, 900, "streaming/follow after extent growth");
        Verify(2400, 1600, "older-page prepend anchor correction");
        Verify(2150, 1350, "measured window-height correction");
        Verify(2150, 2150, "bottom after window-height correction");
        Check(OpenCodeScrollBarProjection.Position(0, 0) == (0, 0, 0), "empty/unscrollable position is zero");
        Check(OpenCodeScrollBarProjection.Position(400, 450).Offset == 400, "extent shrink clamps stale viewport to bottom");
        Check(OpenCodeScrollBarProjection.Position(400, -10).Offset == 0, "top overscroll clamps to zero");
        Check(OpenCodeScrollBarProjection.ContentOffset(75, 12, 240) == 327, "label includes conversation content top and padding");
        Check(OpenCodeScrollBarProjection.ContentOffset(75, 12, 360) == 447, "label recomputes from corrected measured row heights");
        Check(OpenCodeScrollBarProjection.ContentOffset(115, 12, 360) == 487, "label follows conversation content-top changes");
        Check(OpenCodeScrollBarProjection.ScrollTarget(327, 400) == 327, "reachable label stays in viewport-offset coordinates");
        Check(OpenCodeScrollBarProjection.ScrollTarget(487, 400) == 400, "last-viewport label maps to bottom, not beyond track");
        var userDetail = OpenCodeScrollBarProjection.DetailText(true, "User message\nsecond line");
        Check(!string.IsNullOrWhiteSpace(userDetail) && userDetail == "你\nUser message\nsecond line", "SB3b: user detail is non-empty role plus full source text");
        var assistantDetail = OpenCodeScrollBarProjection.DetailText(false, "Assistant message");
        Check(!string.IsNullOrWhiteSpace(assistantDetail) && assistantDetail == "AI 回复\nAssistant message", "SB3b: assistant detail is non-empty role plus full source text");
        Console.WriteLine($"Scroll bar computations passed: {_passed}");
        AnnotatedScrollBarBridgeTests.Run();
    }
}
