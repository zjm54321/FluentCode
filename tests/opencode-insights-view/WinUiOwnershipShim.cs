// SYNTHETIC SEAM — NOT WinUI. Minimal, ownership-aware stand-ins for exactly the Microsoft.UI.Xaml surface that
// src/AIFrontend/Pages/OpenCode/OpenCodeSessionInsightsView.cs uses, so the REAL, UNMODIFIED view file can be compiled
// and rendered offline. Modelled rules (documented WinUI single-parent rule, not the native implementation):
//   * an element belongs to at most one UIElementCollection; Add of an owned element throws
//     COMException 0x800F1000 (the HRESULT observed at runtime; the native message/cause is not reproduced);
//   * Clear/Remove release only DIRECT children (detached sub-trees keep their own children);
//   * FrameworkElement.Parent visibility is a switch (ShimTree.ParentVisible). false models the hypothesis that
//     Parent is not observable for a never-loaded (flyout not opened) sub-tree, while real collection ownership
//     is still enforced. This is an inference about native behaviour, not a proven fact.
// Origin-input additions (also synthetic):
//   * TextBox.TextChanging is raised SYNCHRONOUSLY for user typing (SimulateTyping) and programmatic Text writes
//     (documented WinUI behaviour: "occurs synchronously ... before it is rendered"); while it runs, any
//     UIElementCollection mutation or nested Text write THROWS (models WinUI forbidding tree changes there);
//   * one global focused element; removing it (or an ancestor panel) from a collection drops focus;
//   * DispatcherQueue is a manual FIFO drained explicitly by the test (no timers, no threads).
// IME composition, real focus navigation, LostFocus timing and native layout are NOT modelled.
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Microsoft.UI.Xaml
{
    public static class ShimTree
    {
        public const int AlreadyOwnedHResult = unchecked((int)0x800F1000);
        public static bool ParentVisible { get; set; } = true;
        public static UIElement? FailNextAddOf { get; set; }
        public static int AddCalls { get; set; }
        public static int TextChangingDepth { get; set; }
        public static UIElement? Focused { get; set; }
        public static int ForbiddenMutations { get; set; }

        public static void GuardMutation()
        {
            if (TextChangingDepth > 0)
            {
                ForbiddenMutations++;
                throw new InvalidOperationException("shim: visual tree mutation during TextChanging");
            }
        }

        /// <summary>Removing element (or a panel containing the focused element) drops focus.</summary>
        public static void OnRemoved(UIElement element)
        {
            for (var current = Focused; current is not null; current = current.Owner)
            {
                if (ReferenceEquals(current, element))
                {
                    Focused = null;
                    return;
                }
            }
        }
    }

    public enum FocusState { Unfocused, Pointer, Keyboard, Programmatic }

    public class DependencyObject
    {
        internal readonly Dictionary<string, object?> Attached = new();
    }

    public enum Visibility { Visible, Collapsed }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis, WordEllipsis }
    public enum TextAlignment { Left, Center, Right }
    public enum GridUnitType { Auto, Pixel, Star }

    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    {
        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
    }
    public readonly record struct CornerRadius(double Uniform);
    public readonly record struct GridLength(double Value, GridUnitType GridUnitType)
    {
        public static GridLength Auto => new(1, GridUnitType.Auto);
    }

    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public class Style { }
    public class RoutedEventArgs : EventArgs { }

    public class UIElement : DependencyObject
    {
        internal Controls.Panel? Owner;
        public Visibility Visibility { get; set; }
        public bool IsHitTestVisible { get; set; } = true;
        public double Opacity { get; set; } = 1;
        public FocusState FocusState => ReferenceEquals(ShimTree.Focused, this) ? FocusState.Programmatic : FocusState.Unfocused;
        public Microsoft.UI.Dispatching.DispatcherQueue? DispatcherQueue => Microsoft.UI.Dispatching.DispatcherQueue.Shared;
        public event RoutedEventHandler? LostFocus;

        public bool Focus(FocusState state)
        {
            ShimTree.Focused = this;
            return true;
        }

        public void SimulateLostFocus()
        {
            if (ReferenceEquals(ShimTree.Focused, this)) ShimTree.Focused = null;
            LostFocus?.Invoke(this, new RoutedEventArgs());
        }
    }

    public class FrameworkElement : UIElement
    {
        public Controls.Primitives.FlyoutBase? ContextFlyout { get; set; }
        public DependencyObject? Parent => ShimTree.ParentVisible ? Owner : null;
        public double Width { get; set; }
        public double Height { get; set; }
        public double MinWidth { get; set; }
        public double MinHeight { get; set; }
        public double MaxWidth { get; set; }
        public double MaxHeight { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public Thickness Margin { get; set; }
        public object? Tag { get; set; }
        public event RoutedEventHandler? Loaded;
        public event Windows.Foundation.TypedEventHandler<FrameworkElement, object>? ActualThemeChanged;
        public void SimulateThemeChanged() => ActualThemeChanged?.Invoke(this, new object());
        public event RoutedEventHandler? Unloaded;
        public void SimulateLoaded() => Loaded?.Invoke(this, new RoutedEventArgs());
        public void SimulateUnloaded() => Unloaded?.Invoke(this, new RoutedEventArgs());
    }
}

namespace Windows.Foundation
{
    public delegate void TypedEventHandler<TSender, TResult>(TSender sender, TResult args);
}

namespace Windows.UI.Text
{
    [Flags] public enum TextDecorations { None = 0, Underline = 1, Strikethrough = 2 }
}

namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();

    /// <summary>Manual FIFO; the test drains it explicitly. Available=false models TryEnqueue failure.</summary>
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue Shared { get; } = new();
        private readonly Queue<DispatcherQueueHandler> _items = new();
        public bool Available { get; set; } = true;
        public int Pending => _items.Count;
        public int Enqueued { get; private set; }

        public bool TryEnqueue(DispatcherQueueHandler callback)
        {
            if (!Available) return false;
            Enqueued++;
            _items.Enqueue(callback);
            return true;
        }

        public int Drain()
        {
            var ran = 0;
            while (_items.Count > 0)
            {
                _items.Dequeue()();
                ran++;
            }

            return ran;
        }

        public void Reset()
        {
            _items.Clear();
            Available = true;
            Enqueued = 0;
        }
    }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
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
        public static string? GetHelpText(DependencyObject element) => element.Attached.GetValueOrDefault("HelpText") as string;
        public static void SetHelpText(DependencyObject element, string value) => element.Attached["HelpText"] = value;
        public static string? GetAutomationId(DependencyObject element) => element.Attached.GetValueOrDefault("AutomationId") as string;
        public static string? GetName(DependencyObject element) => element.Attached.GetValueOrDefault("Name") as string;
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Text;
    using Microsoft.UI.Xaml.Media;

    public enum Orientation { Vertical, Horizontal }
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
    public enum ScrollBarVisibility { Disabled, Auto, Hidden, Visible }

    public sealed class UIElementCollection : IEnumerable<UIElement>
    {
        private readonly List<UIElement> _items = new();
        private readonly Panel _owner;
        internal UIElementCollection(Panel owner) => _owner = owner;
        public int Count => _items.Count;
        public UIElement this[int index] => _items[index];

        public void Add(UIElement item)
        {
            ShimTree.GuardMutation();
            ShimTree.AddCalls++;
            if (ReferenceEquals(ShimTree.FailNextAddOf, item))
            {
                ShimTree.FailNextAddOf = null;
                throw new InvalidOperationException("injected partial-construction fault");
            }

            if (item.Owner is not null)
            {
                throw new COMException("shim: element already owned by another collection", ShimTree.AlreadyOwnedHResult);
            }

            item.Owner = _owner;
            _items.Add(item);
        }

        public bool Remove(UIElement item)
        {
            ShimTree.GuardMutation();
            if (!_items.Remove(item)) return false;
            ShimTree.OnRemoved(item);
            item.Owner = null;
            return true;
        }

        public int IndexOf(UIElement item) => _items.IndexOf(item);

        public void Clear()
        {
            ShimTree.GuardMutation();
            foreach (var item in _items)
            {
                ShimTree.OnRemoved(item);
                item.Owner = null;
            }

            _items.Clear();
        }

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

    public sealed class ColumnDefinition { public GridLength Width { get; set; } }
    public sealed class RowDefinition { public GridLength Height { get; set; } }

    public class Grid : Panel
    {
        public Thickness Padding { get; set; }
        public double ColumnSpacing { get; set; }
        public double RowSpacing { get; set; }
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public List<RowDefinition> RowDefinitions { get; } = new();
        public static void SetColumn(FrameworkElement element, int value) => element.Attached["Grid.Column"] = value;
        public static void SetRow(FrameworkElement element, int value) => element.Attached["Grid.Row"] = value;
    }

    public class Control : FrameworkElement
    {
        public bool IsTabStop { get; set; }
        public bool IsEnabled { get; set; } = true;
        public Thickness Padding { get; set; }
        public Style? Style { get; set; }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
    }

    public class ContentControl : Control { public object? Content { get; set; } }

    public class UserControl : Control { public UIElement? Content { get; set; } }

    public class Button : ContentControl
    {
        public event RoutedEventHandler? Click;
        public Primitives.FlyoutBase? Flyout { get; set; }
        public void SimulateClick() => Click?.Invoke(this, new RoutedEventArgs());
    }

    public sealed class ToggleButton : Button
    {
        public bool? IsChecked { get; set; } = false;
    }

    public sealed class SplitView : Control
    {
        public bool IsPaneOpen { get; set; }
    }

    public sealed class CheckBox : Button
    {
        public bool IsThreeState { get; set; }
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

    public sealed class ProgressRing : Control
    {
        public bool IsActive { get; set; }
        public bool IsIndeterminate { get; set; } = true;
        public double Minimum { get; set; }
        public double Maximum { get; set; } = 100;
        public double Value { get; set; }
    }

    public sealed class ProgressBar : Control
    {
        public bool IsIndeterminate { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double Value { get; set; }
    }

    public sealed class SelectorBarItem : FrameworkElement
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class SelectorBarSelectionChangedEventArgs : EventArgs { }

    /// <summary>Selection model only (no template). Setting SelectedItem raises SelectionChanged when it changes.</summary>
    public sealed class SelectorBar : Control
    {
        private SelectorBarItem? _selected;
        public List<SelectorBarItem> Items { get; } = new();
        public event Windows.Foundation.TypedEventHandler<SelectorBar, SelectorBarSelectionChangedEventArgs>? SelectionChanged;

        public SelectorBarItem? SelectedItem
        {
            get => _selected;
            set
            {
                if (ReferenceEquals(_selected, value)) return;
                _selected = value;
                SelectionChanged?.Invoke(this, new SelectorBarSelectionChangedEventArgs());
            }
        }
    }

    public enum ListViewSelectionMode { None, Single, Multiple, Extended }

    public sealed class ItemClickEventArgs : EventArgs
    {
        public ItemClickEventArgs(object? clickedItem) => ClickedItem = clickedItem;
        public object? ClickedItem { get; }
    }

    public delegate void ItemClickEventHandler(object sender, ItemClickEventArgs e);

    public sealed class ListViewItem : ContentControl { }

    public sealed class ListView : Control
    {
        public List<object> Items { get; } = new();
        public ListViewSelectionMode SelectionMode { get; set; }
        public bool IsItemClickEnabled { get; set; }
        public event ItemClickEventHandler? ItemClick;
        /// <summary>Models WinUI: for a directly added ListViewItem, ClickedItem is its Content.</summary>
        public void SimulateItemClick(ListViewItem item) => ItemClick?.Invoke(this, new ItemClickEventArgs(item.Content));
    }

    public sealed class TextBoxTextChangingEventArgs : EventArgs
    {
        public TextBoxTextChangingEventArgs(bool isContentChanging) => IsContentChanging = isContentChanging;
        public bool IsContentChanging { get; }
    }

    public sealed class TextBox : Control
    {
        private string _text = string.Empty;
        public string PlaceholderText { get; set; } = string.Empty;
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public event Action<TextBox, TextBoxTextChangingEventArgs>? TextChanging;

        /// <summary>Programmatic write: TextChanging raised synchronously (documented WinUI ordering).</summary>
        public string Text
        {
            get => _text;
            set => Change(value ?? throw new ArgumentNullException(nameof(value)), true);
        }

        /// <summary>Models one user keystroke/paste that results in newText, caret at its end.</summary>
        public void SimulateTyping(string newText)
        {
            Change(newText, true);
            SelectionStart = newText.Length;
            SelectionLength = 0;
        }

        private void Change(string value, bool content)
        {
            if (ShimTree.TextChangingDepth > 0)
            {
                ShimTree.ForbiddenMutations++;
                throw new InvalidOperationException("shim: nested TextBox.Text write during TextChanging");
            }

            if (string.Equals(_text, value, StringComparison.Ordinal)) return;
            _text = value;
            ShimTree.TextChangingDepth++;
            try
            {
                TextChanging?.Invoke(this, new TextBoxTextChangingEventArgs(content));
            }
            finally
            {
                ShimTree.TextChangingDepth--;
            }
        }
    }

    public sealed class Flyout : Primitives.FlyoutBase
    {
        public Style? FlyoutPresenterStyle { get; set; }
        public event EventHandler<object>? Closed;
        public void ShowAt(FrameworkElement target) => SimulateOpening();
        public UIElement? Content { get; set; }
        public event EventHandler<object>? Opening;
        public int HideCalls { get; private set; }
        public void Hide() { HideCalls++; Closed?.Invoke(this, new object()); }
        public void SimulateOpening() => Opening?.Invoke(this, new object());
    }

    public sealed class MenuFlyout : Primitives.FlyoutBase
    {
        public List<MenuFlyoutItem> Items { get; } = new();
    }

    public sealed class MenuFlyoutItem : Button
    {
        public string Text { get; set; } = string.Empty;
    }

    public class TextBlock : FrameworkElement
    {
        public Windows.UI.Text.TextDecorations TextDecorations { get; set; }
        public string Text { get; set; } = string.Empty;
        public Style? Style { get; set; }
        public FontWeight FontWeight { get; set; }
        public double FontSize { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public TextTrimming TextTrimming { get; set; }
        public TextAlignment TextAlignment { get; set; }
        public bool IsTextSelectionEnabled { get; set; }
    }

    public sealed class FontIcon : FrameworkElement
    {
        public Style? Style { get; set; }
        public string Glyph { get; set; } = string.Empty;
        public double FontSize { get; set; }
    }

    public sealed class ScrollViewer : ContentControl
    {
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
    }

    public sealed class Border : FrameworkElement
    {
        public Style? Style { get; set; }
        public Brush? Background { get; set; }
        public CornerRadius CornerRadius { get; set; }
        public Thickness Padding { get; set; }
        public UIElement? Child { get; set; }
    }

    public static class ToolTipService
    {
        public static void SetToolTip(DependencyObject element, object value) => element.Attached["ToolTip"] = value;
    }
}

namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public enum FlyoutPlacementMode { Top, Bottom, Left, Right, Full, Auto, TopEdgeAlignedLeft, TopEdgeAlignedRight,
        BottomEdgeAlignedLeft, BottomEdgeAlignedRight, LeftEdgeAlignedTop, LeftEdgeAlignedBottom, RightEdgeAlignedTop, RightEdgeAlignedBottom }

    public class FlyoutBase
    {
        public FlyoutPlacementMode Placement { get; set; }
        public bool IsOpen { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Shapes
{
    public sealed class Ellipse : FrameworkElement
    {
        public Style? Style { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Markup
{
    // SYNTHETIC SEAM: no XAML parser offline; theme styles resolve to null (controls keep default look).
    public static class XamlReader
    {
        public static int Loads { get; private set; }
        public static object? Load(string xaml)
        {
            Loads++;
            return null;
        }
    }
}

namespace AIFrontend.Pages.OpenCode
{
    using Microsoft.UI.Xaml;
    using Microsoft.UI.Xaml.Controls;
    using Microsoft.UI.Xaml.Media;

    // SYNTHETIC SEAM: stands in for src/AIFrontend/Pages/OpenCode/OpenCodeInteractionUi.cs (WinUI resource lookups).
    // Only shapes (fresh TextBlock per call, nullable brush) are kept; styling values are irrelevant to ownership.
    internal static class OpenCodeInteractionUi
    {
        internal static Brush? TryBrush(string key) => null;
        internal static Style? TryStyle(string key) => null;
        internal const string CaptionStyleKey = "CaptionTextBlockStyle";
        internal const string CaptionSubtleStyleKey = "CaptionSubtleTextBlockStyle";
        internal const string BodyStyleKey = "BodyTextBlockStyle";
        internal const string BodyStrongStyleKey = "BodyStrongTextBlockStyle";
        internal const string BodySubtleStyleKey = "BodySubtleTextBlockStyle";
        internal const string SubtitleStyleKey = "SubtitleTextBlockStyle";
        internal const string CodeStyleKey = "OpenCodeCodeTextBlockStyle";
        internal const string CautionBackgroundBrushKey = "SystemFillColorCautionBackgroundBrush";
        internal const string CriticalBackgroundBrushKey = "SystemFillColorCriticalBackgroundBrush";
        internal static TextBlock StyledText(string text, string styleKey, bool selectable = false)
            => new() { Text = text, Style = TryStyle(styleKey), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = selectable };
        internal static TextBlock Caption(string text, double opacity = 0.65) => new() { Text = text, Opacity = opacity, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        internal static TextBlock Body(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    }
}
