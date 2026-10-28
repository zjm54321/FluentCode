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

    private static void Main()
    {
        Check(OpenCodeScrollBarProjection.ContentOffset(75, 12, 240) == 327, "label includes conversation content top and padding");
        Check(OpenCodeScrollBarProjection.ContentOffset(75, 12, 360) == 447, "label recomputes from corrected measured row heights");
        Check(OpenCodeScrollBarProjection.ContentOffset(115, 12, 360) == 487, "label follows conversation content-top changes");
        Check(OpenCodeScrollBarProjection.LabelOffset(327, 1000) == 327, "label retains its native document offset");
        Check(OpenCodeScrollBarProjection.LabelOffset(487, 1000) == 487, "last-viewport label is not collapsed to scrollable maximum 400");
        Check(OpenCodeScrollBarProjection.LabelOffset(900, 1000) == 900, "separate last-viewport messages retain separate marks");
        Check(OpenCodeScrollBarProjection.LabelOffset(1100, 1000) == 1000, "extent shrink bounds a stale label");
        Check(OpenCodeScrollBarProjection.LabelOffset(-10, 1000) == 0 && OpenCodeScrollBarProjection.LabelOffset(100, 0) == 0, "negative and empty-document labels are bounded");
        var userDetail = OpenCodeScrollBarProjection.DetailText(true, "User message\nsecond line");
        Check(!string.IsNullOrWhiteSpace(userDetail) && userDetail == "你\nUser message\nsecond line", "SB3b: user detail is non-empty role plus full source text");
        var assistantDetail = OpenCodeScrollBarProjection.DetailText(false, "Assistant message");
        Check(!string.IsNullOrWhiteSpace(assistantDetail) && assistantDetail == "AI 回复\nAssistant message", "SB3b: assistant detail is non-empty role plus full source text");
        Console.WriteLine($"Scroll bar computations passed: {_passed}");
        AnnotatedScrollBarBridgeTests.Run();
    }
}
