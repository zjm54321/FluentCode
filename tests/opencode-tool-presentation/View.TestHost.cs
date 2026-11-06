using System.Collections.Generic;
using FluentCode.Pages.OpenCode;

// Inert surfaces only. ViewMethods.cs contains the unchanged production method bodies.
internal class FrameworkElement
{
    internal string? Title { get; init; }
    internal object? Value { get; init; }
}
internal sealed class StackPanel : FrameworkElement
{
    internal int Spacing { get; init; }
    internal List<FrameworkElement> Children { get; } = new();
}
internal sealed class Border : FrameworkElement { internal string? BackgroundKey { get; set; } }
internal sealed class FontIcon { internal double Opacity { get; set; } }
internal enum TextWrapping { Wrap }
internal sealed class TextBlock : FrameworkElement { internal TextWrapping TextWrapping { get; set; } }
internal static class OpenCodeToolTheme
{
    internal const string Critical = "critical";
    internal static string Border(string key) => key;
    internal static string Text(string key, string style) => key;
    internal static void Apply(FrameworkElement element, string key)
    {
        if (element is Border border) border.BackgroundKey = key;
    }
}
internal static class OpenCodeInteractionUi
{
    internal const string BodyStyleKey = "body";
    internal static TextBlock StyledText(string text, string style, bool selectable) => new() { Value = text };
}
internal sealed partial class HoverProbe
{
    internal bool _pointerOver, _focusVisible, _headerPointerOver, _headerFocusVisible;
    internal OpenCodeStepRunSummary _summary = new("", "", 0, 0, false, null, false, "");
    internal readonly Border _outline = new();
    internal readonly Border _headerSurface = new();
    internal readonly FontIcon _chevron = new();
    internal void Update() => UpdateChevronAffordance();
}
internal static partial class GenericProbe
{
    internal static StackPanel Build(OpenCodeToolRowModel model) => (StackPanel)BuildGeneric(model);
    private static FrameworkElement Section(string title, FrameworkElement content) => new() { Title = title, Value = content };
    private static FrameworkElement PropertyList(IReadOnlyList<OpenCodeToolProperty> properties) => new() { Value = properties };
    private static FrameworkElement CodeBlock(string text) => new() { Value = text };
    private static FrameworkElement Caption(string text) => new() { Value = text };
}

namespace FluentCode.Services.OpenCode
{
    // Instruction-file transport is outside these tests; fail if anything tries to enter it.
    internal static class OpenCodeClient
    {
        internal static string RelativeTextFilePath(string directory, string path)
            => throw new System.NotSupportedException("Instruction transport is not part of tool-presentation tests.");
    }
}
