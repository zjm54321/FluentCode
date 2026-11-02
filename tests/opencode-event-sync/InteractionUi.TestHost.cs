// Inert native surfaces only. The permission card, overlay, interactions container and page
// routing/state machines are linked production sources; this host does not draw pixels or send requests.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum TextWrapping { NoWrap, Wrap }
    public enum FocusState { Unfocused, Programmatic }
    public enum KeyboardNavigationMode { Local, Cycle }
    public enum XYFocusKeyboardNavigationMode { Disabled, Enabled }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    { public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { } }
    public readonly record struct CornerRadius(double Value);
    public class RoutedEventArgs : EventArgs { }
    public class DependencyObject { internal DependencyObject? Parent; }
    public class UIElement : DependencyObject
    {
        public Visibility Visibility { get; set; }
        public bool IsHitTestVisible { get; set; } = true;
        internal string AutomationId = string.Empty, AutomationName = string.Empty;
        public double Opacity { get; set; } = 1;
    }
    public class FrameworkElement : UIElement
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double MinWidth { get; set; }
        public double MaxHeight { get; set; }
        public Thickness Margin { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsLoaded { get; set; } = true;
        public bool IsTabStop { get; set; }
        public bool UseSystemFocusVisuals { get; set; }
        public Style? Style { get; set; }
        public XamlRoot? XamlRoot { get; set; } = new();
        public FocusState FocusState { get; private set; }
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
        public bool Focus(FocusState state)
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.Focused is FrameworkElement previous) previous.FocusState = FocusState.Unfocused;
            FocusState = state; Microsoft.UI.Xaml.Input.FocusManager.Focused = this; return true;
        }
        public void StartBringIntoView(BringIntoViewOptions options) { }
    }
    public class Style { }
    public class XamlRoot { }
    public class BringIntoViewOptions { }
    public class Application
    {
        public static Application? Current { get; set; }
        public Dictionary<object, object> Resources { get; } = new();
    }
}
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueueTimer { }
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml;
    public enum Orientation { Vertical, Horizontal }
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
    public enum ScrollBarVisibility { Auto, Disabled }
    public enum GridUnitType { Auto, Star }
    public readonly record struct GridLength(double Value, GridUnitType Type = GridUnitType.Auto)
    { public static GridLength Auto => new(0); }
    public class ColumnDefinition { public GridLength Width { get; set; } }
    public sealed class UIElementCollection(Panel owner) : Collection<UIElement>
    {
        protected override void InsertItem(int index, UIElement item) { base.InsertItem(index, item); item.Parent = owner; }
        protected override void RemoveItem(int index) { this[index].Parent = null; base.RemoveItem(index); }
        protected override void ClearItems() { foreach (var item in this) item.Parent = null; base.ClearItems(); }
    }
    public class Panel : FrameworkElement
    {
        public Panel() => Children = new(this);
        public UIElementCollection Children { get; }
    }
    public class StackPanel : Panel { public Orientation Orientation { get; set; } public double Spacing { get; set; } }
    public class Grid : Panel
    {
        public double ColumnSpacing { get; set; }
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public KeyboardNavigationMode TabFocusNavigation { get; set; }
        public XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation { get; set; }
        public static void SetColumn(FrameworkElement element, int column) { }
    }
    public class ContentControl : FrameworkElement
    {
        private object? _content;
        public object? Content
        {
            get => _content;
            set { if (_content is DependencyObject old) old.Parent = null; _content = value; if (value is DependencyObject child) child.Parent = this; }
        }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
        public VerticalAlignment VerticalContentAlignment { get; set; }
    }
    public class Border : ContentControl
    {
        public UIElement? Child { get => Content as UIElement; set => Content = value; }
        public Thickness BorderThickness { get; set; }
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
    }
    public class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = string.Empty;
        public double FontSize { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public bool IsTextSelectionEnabled { get; set; }
        public List<Microsoft.UI.Xaml.Documents.Run> Inlines { get; } = new();
    }
    public class TextBox : TextBlock { }
    public class FontIcon : FrameworkElement { public string Glyph { get; set; } = string.Empty; public double FontSize { get; set; } }
    public class Button : ContentControl
    {
        public event Action<object, RoutedEventArgs>? Click;
        internal void ClickForTest() { if (IsEnabled) Click?.Invoke(this, new()); }
    }
    public class Expander : ContentControl { public object? Header { get; set; } public bool IsExpanded { get; set; } }
    public class ScrollViewer : ContentControl
    { public ScrollBarVisibility VerticalScrollBarVisibility { get; set; } public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; } }
    public class ProgressRing : FrameworkElement { public bool IsActive { get; set; } }
    public class InfoBar : FrameworkElement
    {
        public bool IsOpen { get; set; }
        public bool IsClosable { get; set; }
        public bool IsIconVisible { get; set; }
        public InfoBarSeverity Severity { get; set; }
        public string? Title { get; set; }
        public string? Message { get; set; }
    }
    public static class ToolTipService { public static void SetToolTip(FrameworkElement element, string text) { } }
}
namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class FontFamily { }
    public static class VisualTreeHelper { public static Microsoft.UI.Xaml.DependencyObject? GetParent(Microsoft.UI.Xaml.DependencyObject element) => element.Parent; }
}
namespace Microsoft.UI.Xaml.Documents { public class Run { public string? Text { get; set; } public Microsoft.UI.Xaml.Media.FontFamily? FontFamily { get; set; } } }
namespace Microsoft.UI.Xaml.Input
{
    public static class FocusManager
    {
        internal static Microsoft.UI.Xaml.DependencyObject? Focused;
        public static object? GetFocusedElement(Microsoft.UI.Xaml.XamlRoot root) => Focused;
    }
}
namespace Microsoft.UI.Xaml.Automation.Peers { public enum AccessibilityView { Raw } }
namespace Microsoft.UI.Xaml.Automation
{
    public enum AutomationLiveSetting { Polite, Assertive }
    public enum AutomationLandmarkType { Custom }
    public static class AutomationProperties
    {
        public static void SetAutomationId(Microsoft.UI.Xaml.UIElement element, string id) => element.AutomationId = id;
        public static void SetName(Microsoft.UI.Xaml.UIElement element, string name) => element.AutomationName = name;
        public static void SetAccessibilityView(object element, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView view) { }
        public static void SetLiveSetting(object element, AutomationLiveSetting value) { }
        public static void SetLandmarkType(object element, AutomationLandmarkType value) { }
        public static void SetLocalizedLandmarkType(object element, string value) { }
    }
}
namespace FluentCode.Services.Appearance { public static class AppFonts { public static Microsoft.UI.Xaml.Media.FontFamily Monospace { get; } = new(); } }
namespace FluentCode.Motion { public static class ActionFeedback { public static void Announce(object anchor, string message) { } } }
namespace FluentCode.Pages.OpenCode
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Controls;
    internal static class OpenCodeThemedXaml { internal static T? Load<T>(string name, string attributes) where T : FrameworkElement => null; }
    internal sealed class OpenCodeQuestionAnchorHost : ContentControl
    { internal void Attach(FrameworkElement element) => Content = element; internal void Detach(FrameworkElement element) { if (ReferenceEquals(Content, element)) Content = null; } }
    // Form editor/native rendering are outside this permission regression; its page/HTTP/gate path is real.
    internal sealed class OpenCodeFormCard : FrameworkElement
    {
        private readonly Action<OpenCodeFormInfo, IReadOnlyDictionary<string, JsonElement>> _submit;
        internal OpenCodeFormCard(OpenCodeFormInfo form, Action<OpenCodeFormInfo, IReadOnlyDictionary<string, JsonElement>> submit,
            Action<OpenCodeFormInfo> cancel, Action<OpenCodeFormInfo> recheck, Action<OpenCodeFormInfo, bool> resolution)
        { Form = form; _submit = submit; }
        internal OpenCodeFormInfo Form { get; private set; }
        internal void Update(OpenCodeFormInfo form) => Form = form;
        internal void SubmitForTest(IReadOnlyDictionary<string, JsonElement> answer) => _submit(Form, answer);
        internal void MarkDetached() { }
        internal void FocusCard() { }
        internal void Unblock() { }
        internal void MarkBlocked(string detail) { }
        internal void ShowResolutionConfirm(string detail) { }
        internal void SetActionsEnabled(bool enabled) => IsEnabled = enabled;
        internal void SetBusy(bool busy, string? label) { }
    }
    internal sealed class OpenCodeQuestionOverlay : Grid
    {
        internal int Count { get; private set; }
        internal int Sync(IReadOnlyList<OpenCodeFormCard> cards) { Count = cards.Count; return Count; }
        internal bool ContainsFocus() => false;
        internal bool FocusCurrent() => false;
    }
}
