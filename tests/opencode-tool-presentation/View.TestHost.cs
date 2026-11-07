using System.Collections.Generic;
using FluentCode.Pages.OpenCode;

// Inert surfaces only. ViewMethods.cs contains the unchanged production method bodies.
internal class FrameworkElement
{
    internal string? Title { get; init; }
    internal object? Value { get; init; }
    internal virtual Visibility Visibility { get; set; } = Visibility.Visible;
    internal bool IsTabStop { get; set; } = true;
    internal double Width { get; set; }
    internal double MaxHeight { get; set; }
}
internal enum Visibility { Visible, Collapsed }
internal enum FocusState { Unfocused, Keyboard, Pointer, Programmatic }
internal sealed class ContentControl : FrameworkElement { internal object? Content { get; set; } }
internal sealed class Button : FrameworkElement
{
    internal FocusState FocusState { get; set; }
    internal int FocusCalls { get; private set; }
    internal override Visibility Visibility
    {
        get => base.Visibility;
        set
        {
            base.Visibility = value;
            if (value == Visibility.Collapsed) FocusState = FocusState.Unfocused;
        }
    }
    internal bool Focus(FocusState state)
    {
        FocusState = state;
        FocusCalls++;
        return true;
    }
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
internal sealed partial class HomeLayoutProbe
{
    internal double ActualWidth { get; private set; }
    internal double ActualHeight { get; private set; } = 900;
    internal bool _narrow;
    internal readonly FrameworkElement _leftCard = new(), _right = new(), _chipScroller = new(), _chipRow = new(), _center = new();
    internal readonly ContentControl _settingsHost = new() { Content = new Button(), IsTabStop = false };
    internal readonly Button _wideSettingsButton = new();
    internal Button NarrowSettingsButton => (Button)_settingsHost.Content!;
    internal void Resize(double width)
    {
        ActualWidth = width;
        ApplyLayout();
    }
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
