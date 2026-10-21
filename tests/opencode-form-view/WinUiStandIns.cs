// SYNTHETIC STAND-INS — NOT WinUI. Minimal property bags for exactly the Microsoft.UI.Xaml surface used by the REAL,
// UNMODIFIED src/AIFrontend/Pages/OpenCode/OpenCodeFormCard.cs and OpenCodeInteractionUi.cs, so the actual card can be
// compiled and driven offline. Modelled rules (documented WinUI behaviour, not the native implementation):
//   * an element belongs to at most one Panel.Children collection (single-parent rule);
//   * TextBox.Text rejects null and raises TextChanged on change; ComboBox/RadioButtons.SelectedIndex accept only -1..Count-1;
//   * CheckBox raises Checked/Unchecked when IsChecked changes;
//   * Button.Click is raised ONLY by the test's explicit StandInClick() (models a user click; nothing else raises it).
// No native layout/rendering/theme/input routing/IME/UIA. Focus and dispatcher below are explicit
// tracked stand-ins for testing guarded deferred callbacks, not native focus/layout evidence.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.UI.Xaml
{
    public class DependencyObject
    {
        internal readonly Dictionary<string, object?> Attached = new();
        public object? StandInAttached(string key) => Attached.GetValueOrDefault(key);
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue => Microsoft.UI.Dispatching.DispatcherQueue.Shared;
    }

    public enum Visibility { Visible, Collapsed }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public enum FocusState { Unfocused, Pointer, Keyboard, Programmatic }

    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    {
        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
    }

    public readonly record struct CornerRadius(double Uniform);

    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public class RoutedEventArgs : EventArgs { }

    public class Style { }

    public sealed class ResourceDictionary
    {
        private readonly Dictionary<object, object> _items = new();
        public object this[object key] { get => _items[key]; set => _items[key] = value; }
    }

    public class Application
    {
        public static Application? Current { get; set; }
        public ResourceDictionary Resources { get; } = new();
    }

    public class UIElement : DependencyObject
    {
        internal UIElement? StandInOwner;
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; } = 1;
        public bool IsTabStop { get; set; }
        public Microsoft.UI.Xaml.Input.KeyboardNavigationMode TabFocusNavigation { get; set; }
        public Microsoft.UI.Xaml.Input.XYFocusKeyboardNavigationMode XYFocusKeyboardNavigation { get; set; }
        public FocusState FocusState { get; private set; }
        public bool Focus(FocusState state)
        {
            if (Microsoft.UI.Xaml.Input.FocusManager.Focused is UIElement old) old.FocusState = FocusState.Unfocused;
            Microsoft.UI.Xaml.Input.FocusManager.Focused = this;
            FocusState = state;
            return true;
        }
        public event Microsoft.UI.Xaml.Input.KeyEventHandler? PreviewKeyDown;
        public Microsoft.UI.Xaml.Input.KeyRoutedEventArgs StandInKey(Windows.System.VirtualKey key)
        {
            var args = new Microsoft.UI.Xaml.Input.KeyRoutedEventArgs { Key = key };
            PreviewKeyDown?.Invoke(this, args);
            return args;
        }
    }

    public class FrameworkElement : UIElement
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double MinHeight { get; set; }
        public double MinWidth { get; set; }
        public double MaxWidth { get; set; }
        public double MaxHeight { get; set; }
        public UIElement? Parent => StandInOwner;
        public XamlRoot? XamlRoot { get; set; } = new();
        public Thickness Margin { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public Style? Style { get; set; }
    }
    public sealed class XamlRoot { }
    public readonly record struct GridLength(double Value, GridUnitType Type = GridUnitType.Pixel)
    { public static GridLength Auto => new(0, GridUnitType.Auto); }
    public enum GridUnitType { Auto, Pixel, Star }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public static class VisualTreeHelper
    { public static Microsoft.UI.Xaml.DependencyObject? GetParent(Microsoft.UI.Xaml.DependencyObject element) => (element as Microsoft.UI.Xaml.UIElement)?.StandInOwner; }
}

namespace Microsoft.UI.Text
{
    public readonly record struct FontWeight(ushort Weight);
    public static class FontWeights { public static FontWeight SemiBold => new(600); }
}

namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetAutomationId(DependencyObject element, string value) => element.Attached["AutomationId"] = value;
        public static void SetName(DependencyObject element, string value) => element.Attached["Name"] = value;
        public static void SetHelpText(DependencyObject element, string value) => element.Attached["HelpText"] = value;
        public static string? GetAutomationId(DependencyObject element) => element.Attached.GetValueOrDefault("AutomationId") as string;
        public static void SetLiveSetting(DependencyObject element, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting value) => element.Attached["LiveSetting"] = value;
        public static void SetLandmarkType(DependencyObject element, AutomationLandmarkType value) => element.Attached["Landmark"] = value;
        public static void SetLocalizedLandmarkType(DependencyObject element, string value) => element.Attached["LandmarkText"] = value;
        public static void SetAccessibilityView(DependencyObject element, AccessibilityView value) => element.Attached["Accessibility"] = value;
    }
    public enum AutomationLandmarkType { Custom }
    public enum AccessibilityView { Raw }
}

namespace Microsoft.UI.Xaml.Automation.Peers
{
    public enum AutomationLiveSetting { Off, Polite, Assertive }
}

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Text;
    using Microsoft.UI.Xaml.Media;

    public enum Orientation { Vertical, Horizontal }

    public sealed class UIElementCollection : IEnumerable<UIElement>
    {
        private readonly List<UIElement> _items = new();
        private readonly UIElement _owner;
        internal UIElementCollection(UIElement owner) => _owner = owner;
        public int Count => _items.Count;
        public UIElement this[int index] => _items[index];

        public void Add(UIElement item)
        {
            if (item.StandInOwner is not null) throw new InvalidOperationException("stand-in: element already has a parent");
            item.StandInOwner = _owner;
            _items.Add(item);
        }

        public void Clear()
        {
            foreach (var item in _items) item.StandInOwner = null;
            _items.Clear();
        }
        public bool Remove(UIElement item)
        { if (!_items.Remove(item)) return false; item.StandInOwner = null; return true; }

        public IEnumerator<UIElement> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public abstract class Panel : FrameworkElement
    {
        protected Panel() => Children = new UIElementCollection(this);
        public UIElementCollection Children { get; }
    }

    public class StackPanel : Panel
    {
        public Orientation Orientation { get; set; }
        public double Spacing { get; set; }
    }
    public class ColumnDefinition { public GridLength Width { get; set; } }
    public class Grid : Panel
    {
        public double ColumnSpacing { get; set; }
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public static void SetColumn(FrameworkElement element, int column) => element.Attached["GridColumn"] = column;
    }

    public class Border : FrameworkElement
    {
        private UIElement? _child;
        public Brush? Background { get; set; }
        public Brush? BorderBrush { get; set; }
        public Thickness BorderThickness { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Thickness Padding { get; set; }

        public UIElement? Child
        {
            get => _child;
            set
            {
                if (value?.StandInOwner is not null) throw new InvalidOperationException("stand-in: element already has a parent");
                if (_child is not null) _child.StandInOwner = null;
                _child = value;
                if (value is not null) value.StandInOwner = this;
            }
        }
    }

    public class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = string.Empty;
        public double FontSize { get; set; } = 14;
        public FontWeight FontWeight { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public bool IsTextSelectionEnabled { get; set; }
        public int MaxLines { get; set; }
        public TextTrimming TextTrimming { get; set; }
        private Brush? _foreground;
        public bool HasLocalForeground { get; private set; }
        public Brush? Foreground
        {
            get => _foreground;
            set { _foreground = value; HasLocalForeground = true; }
        }
    }

    public class Control : FrameworkElement
    {
        public bool IsEnabled { get; set; } = true;
        public Thickness Padding { get; set; }
    }

    public class ContentControl : Control
    {
        private object? _content;
        public object? Content
        {
            get => _content;
            set
            {
                if (ReferenceEquals(_content, value)) return;
                if (value is UIElement incoming && incoming.StandInOwner is not null) throw new InvalidOperationException("stand-in: content already parented");
                if (_content is UIElement old) old.StandInOwner = null;
                _content = value;
                if (value is UIElement next) next.StandInOwner = this;
            }
        }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
        public VerticalAlignment VerticalContentAlignment { get; set; }
        public bool UseSystemFocusVisuals { get; set; }
    }

    public class Button : ContentControl
    {
        public event RoutedEventHandler? Click;

        /// <summary>Stand-in only: models one explicit user click.</summary>
        public void StandInClick() => Click?.Invoke(this, new RoutedEventArgs());
    }

    public class CheckBox : ContentControl
    {
        private bool? _isChecked = false;
        public event RoutedEventHandler? Checked;
        public event RoutedEventHandler? Unchecked;

        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                if (value == true) Checked?.Invoke(this, new RoutedEventArgs());
                else if (value == false) Unchecked?.Invoke(this, new RoutedEventArgs());
            }
        }
    }

    public class RadioButton : ContentControl { }

    public delegate void SelectionChangedEventHandler(object sender, SelectionChangedEventArgs e);
    public sealed class SelectionChangedEventArgs : RoutedEventArgs { }

    public class RadioButtons : Control
    {
        private int _selectedIndex = -1;
        public List<object> Items { get; } = new();
        public event SelectionChangedEventHandler? SelectionChanged;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < -1 || value >= Items.Count) throw new ArgumentException("stand-in: SelectedIndex out of range");
                if (_selectedIndex == value) return;
                _selectedIndex = value;
                SelectionChanged?.Invoke(this, new SelectionChangedEventArgs());
            }
        }
    }

    public delegate void TextChangedEventHandler(object sender, TextChangedEventArgs e);
    public sealed class TextChangedEventArgs : RoutedEventArgs { }

    public class TextBox : Control
    {
        private string _text = string.Empty;
        public string PlaceholderText { get; set; } = string.Empty;
        public TextWrapping TextWrapping { get; set; }
        public bool AcceptsReturn { get; set; }
        public event TextChangedEventHandler? TextChanged;

        public string Text
        {
            get => _text;
            set
            {
                var next = value ?? throw new ArgumentNullException(nameof(value), "stand-in: TextBox.Text rejects null");
                if (string.Equals(_text, next, StringComparison.Ordinal)) return;
                _text = next;
                TextChanged?.Invoke(this, new TextChangedEventArgs());
            }
        }
    }

    public class ComboBox : Control
    {
        private int _selectedIndex = -1;
        public string PlaceholderText { get; set; } = string.Empty;
        public object? ItemsSource { get; set; }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                var count = ItemsSource is ICollection items ? items.Count : 0;
                if (value < -1 || value >= count) throw new ArgumentException("stand-in: SelectedIndex out of range");
                _selectedIndex = value;
            }
        }
    }

    public class ProgressRing : Control
    {
        public bool IsActive { get; set; }
    }
    public class FontIcon : Control { public string Glyph { get; set; } = ""; public double FontSize { get; set; } }
    public enum InfoBarSeverity { Informational, Warning, Error, Success }
    public class InfoBar : ContentControl
    {
        public InfoBarSeverity Severity { get; set; }
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public bool IsClosable { get; set; }
        public bool IsOpen { get; set; }
        public Button? ActionButton { get; set; }
    }
    public enum ScrollBarVisibility { Disabled, Auto }
    public class ScrollViewer : ContentControl
    {
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public int StandInScrollChanges { get; private set; }
        public bool ChangeView(double? horizontal, double? vertical, float? zoom) { StandInScrollChanges++; return true; }
    }
    public static class ToolTipService
    { public static void SetToolTip(DependencyObject element, object value) => element.Attached["Tooltip"] = value; }
}

namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue Shared { get; } = new();
        private readonly Queue<Action> _pending = new();
        public bool TryEnqueue(Action action) { _pending.Enqueue(action); return true; }
        public void StandInDrain() { while (_pending.TryDequeue(out var action)) action(); }
    }
}
namespace Windows.System { public enum VirtualKey { Enter, Escape, ProcessKey, Control } }
namespace Windows.UI.Core { [Flags] public enum CoreVirtualKeyStates { None = 0, Down = 1 } }
namespace Microsoft.UI.Input
{
    public static class InputKeyboardSource
    {
        public static bool StandInControlDown;
        public static Windows.UI.Core.CoreVirtualKeyStates GetKeyStateForCurrentThread(Windows.System.VirtualKey key)
            => StandInControlDown ? Windows.UI.Core.CoreVirtualKeyStates.Down : Windows.UI.Core.CoreVirtualKeyStates.None;
    }
}
namespace Microsoft.UI.Xaml.Input
{
    public delegate void KeyEventHandler(object sender, KeyRoutedEventArgs args);
    public enum KeyboardNavigationMode { Cycle }
    public enum XYFocusKeyboardNavigationMode { Enabled }
    public sealed class KeyRoutedEventArgs : EventArgs { public Windows.System.VirtualKey Key { get; set; } public bool Handled { get; set; } }
    public static class FocusManager
    { public static DependencyObject? Focused; public static DependencyObject? GetFocusedElement(XamlRoot root) => Focused; }
}
namespace AIFrontend.Pages.OpenCode
{
    // Boundary double for runtime XAML/resource loading: exercise the production fallback controls.
    internal static class OpenCodeThemedXaml
    { internal static T? Load<T>(string element, string attributes) where T : class => null; }
}
