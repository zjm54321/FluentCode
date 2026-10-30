// Native property/event boundary only. The production ComboBox selection handler and Render execute unchanged.
using System;
using System.Collections.Generic;
namespace Microsoft.UI.Xaml
{
    public class Application { public static Application Current { get; } = new(); public Dictionary<string, object> Resources { get; } = new() { ["DefaultComboBoxStyle"] = new Style(), ["DefaultComboBoxItemStyle"] = new Style() }; }
    public class FrameworkElement { public Dictionary<string, object> Resources { get; } = new(); public Visibility Visibility { get; set; } public Style? Style { get; set; } public double MinWidth { get; set; } public double MinHeight { get; set; } public HorizontalAlignment HorizontalAlignment { get; set; } public VerticalAlignment VerticalAlignment { get; set; } }
    public class Style { }
    public class DataTemplate { }
    public enum HorizontalAlignment { Stretch }
    public enum VerticalAlignment { Center }
    public enum TextTrimming { CharacterEllipsis }
    public enum Visibility { Visible, Collapsed }
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
        public event EventHandler? DropDownClosed;
        public ContentPresenter SelectionPresenter { get; } = new();
        protected virtual void OnApplyTemplate() { }
        public Microsoft.UI.Xaml.FrameworkElement DropDownGlyph { get; } = new();
        protected object GetTemplateChild(string name) => name == "DropDownGlyph" ? DropDownGlyph : SelectionPresenter;
        public void ApplyTemplate() => OnApplyTemplate();
        public void CloseDropDown() => DropDownClosed?.Invoke(this, EventArgs.Empty);
        public object? SelectedItem { get => _selected; set { if (Equals(_selected, value)) return; _selected = value; SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    }
    public class ComboBoxItem : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } public object? Tag { get; set; } }
    public class ContentPresenter : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } }
    public class Grid { public static void SetColumnSpan(object element, int span) { _ = element; _ = span; } }
    public class Button : Microsoft.UI.Xaml.FrameworkElement { public object? Content { get; set; } public object? Tag { get; set; } public bool IsEnabled { get; set; } }
    public enum Orientation { Horizontal }
    public class StackPanel : Microsoft.UI.Xaml.FrameworkElement { public Orientation Orientation { get; set; } public double Spacing { get; set; } public List<Microsoft.UI.Xaml.FrameworkElement> Children { get; } = new(); }
    public class FontIcon : Microsoft.UI.Xaml.FrameworkElement { public string Glyph { get; set; } = ""; public double FontSize { get; set; } public object? Foreground { get; set; } }
    public class TextBlock : Microsoft.UI.Xaml.FrameworkElement { public string Text { get; set; } = ""; public Microsoft.UI.Xaml.TextTrimming TextTrimming { get; set; } }
    public class Border { public object? Child { get; set; } public Microsoft.UI.Xaml.Visibility Visibility { get; set; } }
}
namespace AIFrontend.Pages.OpenCode
{
    // Headless geometry state only; real Canvas rendering is covered by opencode-sidebar-visual.
    internal sealed class OpenCodeComposerEffortGauge : Microsoft.UI.Xaml.FrameworkElement
    {
        internal System.Collections.Generic.IReadOnlyList<OpenCodeEffortTick> Ticks = System.Array.Empty<OpenCodeEffortTick>();
        internal void Render(System.Collections.Generic.IReadOnlyList<OpenCodeEffortTick> ticks) => Ticks = ticks;
    }
    internal sealed record OpenCodeAgentPickerRow(string Id, string Name, string? Description, bool Selectable, string? Reason, string? Color = null);
    internal static class OpenCodeAgentIcons { internal static string Glyph(string id) => id; internal static string DisplayName(string name) => name; }
    internal static class OpenCodeAgentPresentation { internal static object? Color(string? color) => color; }
    internal static class OpenCodeModelLogo { internal static Microsoft.UI.Xaml.FrameworkElement Label(string? id, string name) { _ = id; return new Microsoft.UI.Xaml.Controls.TextBlock { Text = name }; } internal static Microsoft.UI.Xaml.FrameworkElement Create(string? id, string? name) => new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "logo:" + id }; }
    internal sealed partial class OpenCodeSessionControlCard
    {
        internal OpenCodeNativeComboBox AgentButton { get; } = new();
        internal Microsoft.UI.Xaml.Controls.Button ModelButton { get; } = new();
        internal OpenCodeNativeComboBox VariantButton { get; } = new();
        private readonly Microsoft.UI.Xaml.Controls.Border _chrome = new();
        private readonly object _editableRows = new();
        private static void Describe(object control, string name, string scope) { _ = control; _ = name; _ = scope; }
    }
}
