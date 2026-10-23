// Native property/event boundary only. The production ComboBox selection handler and Render execute unchanged.
using System;
using System.Collections.Generic;
namespace Microsoft.UI.Xaml
{
    public class FrameworkElement { public Style? Style { get; set; } public double MinWidth { get; set; } public double MinHeight { get; set; } public HorizontalAlignment HorizontalAlignment { get; set; } public VerticalAlignment VerticalAlignment { get; set; } }
    public class Style { }
    public class DataTemplate { }
    public enum HorizontalAlignment { Stretch }
    public enum VerticalAlignment { Center }
    public enum TextTrimming { CharacterEllipsis }
}
namespace Microsoft.UI.Xaml.Markup { public static class XamlReader { public static object Load(string xaml) => xaml.StartsWith("<Style", StringComparison.Ordinal) ? new Microsoft.UI.Xaml.Style() : new Microsoft.UI.Xaml.DataTemplate(); } }
namespace Microsoft.UI.Xaml.Media { public class SolidColorBrush { public SolidColorBrush(object color) { _ = color; } } }
namespace Microsoft.UI.Xaml.Automation { public static class AutomationProperties { public static void SetName(object element, string name) { _ = element; _ = name; } } }
namespace Microsoft.UI.Xaml.Controls
{
    public class ComboBox : Microsoft.UI.Xaml.FrameworkElement
    {
        private object? _selected;
        public bool IsTextSearchEnabled { get; set; }
        public string PlaceholderText { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
        public Microsoft.UI.Xaml.HorizontalAlignment HorizontalContentAlignment { get; set; }
        public Microsoft.UI.Xaml.DataTemplate? ItemTemplate { get; set; }
        public List<object> Items { get; } = new();
        public event EventHandler? SelectionChanged;
        public object? SelectedItem { get => _selected; set { if (Equals(_selected, value)) return; _selected = value; SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    }
    public enum Orientation { Horizontal }
    public class StackPanel : Microsoft.UI.Xaml.FrameworkElement { public Orientation Orientation { get; set; } public double Spacing { get; set; } public List<Microsoft.UI.Xaml.FrameworkElement> Children { get; } = new(); }
    public class FontIcon : Microsoft.UI.Xaml.FrameworkElement { public string Glyph { get; set; } = ""; public double FontSize { get; set; } public object? Foreground { get; set; } }
    public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { public string Text { get; set; } = ""; public Microsoft.UI.Xaml.TextTrimming TextTrimming { get; set; } }
}
namespace AIFrontend.Pages.OpenCode
{
    internal sealed record OpenCodeAgentPickerRow(string Id, string Name, string? Description, bool Selectable, string? Reason, string? Color = null);
    internal static class OpenCodeAgentIcons { internal static string Glyph(string id) => id; internal static string DisplayName(string name) => name; }
    internal static class OpenCodeAgentPresentation { internal static object? Color(string? color) => color; }
    internal static class OpenCodeModelLogo { internal static Microsoft.UI.Xaml.FrameworkElement Label(string? id, string name) { _ = id; return new Microsoft.UI.Xaml.Controls.TextBlock { Text = name }; } }
}
