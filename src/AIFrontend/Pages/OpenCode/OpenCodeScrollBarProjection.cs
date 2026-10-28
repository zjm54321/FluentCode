using System;

namespace AIFrontend.Pages.OpenCode;

internal static class OpenCodeScrollBarProjection
{
    internal static double ContentOffset(double viewTop, double paddingTop, double precedingHeight) =>
        viewTop + paddingTop + precedingHeight;

    // ScrollPresenter supplies the real viewport length to the native controller.
    // Labels occupy the whole document, including distinct messages in the last viewport.
    internal static double LabelOffset(double contentOffset, double extentHeight) =>
        Math.Clamp(contentOffset, 0, Math.Max(0, extentHeight));

    internal static string DetailText(bool user, string text) =>
        $"{(user ? "你" : "AI 回复")}\n{text}";
}
