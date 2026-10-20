// Headless doubles for executing the real AppFonts lifecycle/dispatcher code.
// These model tree membership, native DP-store identity and Loaded ordering, not native rendering/resource lookup.
using System;
using System.Collections.Generic;

namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority { Low }

    public sealed class DispatcherQueue
    {
        private readonly Queue<Action> _callbacks = new();
        private static readonly DispatcherQueue Current = new();
        public static DispatcherQueue GetForCurrentThread() => Current;
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action callback) { _callbacks.Enqueue(callback); return true; }
        public bool TryEnqueue(DispatcherQueuePriority priority, Action callback) => TryEnqueue(callback);
        public void Drain()
        {
            int count = 0;
            while (_callbacks.TryDequeue(out Action? callback))
            {
                if (++count > 100) throw new InvalidOperationException("Font refresh did not settle.");
                callback();
            }
        }
    }
}

namespace Microsoft.UI.Xaml
{
    public class DependencyObject
    {
        public static List<DependencyObject> All { get; } = new();
        // A new projection of one native element retains its dependency-property store, not its
        // managed wrapper identity (the old ConditionalWeakTable implementation lost its metric).
        public DependencyObject(DependencyObject? nativePeer = null)
        {
            _local = nativePeer?._local ?? new();
            All.Add(this);
        }
        private readonly Dictionary<DependencyProperty, object> _local;
        public object ReadLocalValue(DependencyProperty property) => _local.TryGetValue(property, out object? value) ? value : DependencyProperty.UnsetValue;
        public object? GetValue(DependencyProperty property) => _local.TryGetValue(property, out object? value) ? value : property.DefaultValue;
        public void SetValue(DependencyProperty property, object value) => _local[property] = value;
        public double GetSize(DependencyProperty property)
        {
            if (_local.TryGetValue(property, out object? value)) return (double)value;
            if (this is FrameworkElement fe)
                for (Style? style = fe.Style; style is not null; style = style.BasedOn)
                    foreach (SetterBase item in style.Setters)
                        if (item is Setter setter && setter.Property == property) return (double)setter.Value;
            return Media.VisualTreeHelper.GetParent(this)?.GetSize(property) ?? 14;
        }
        public List<DependencyObject> TestChildren { get; } = new();
    }

    public sealed class PropertyMetadata(object? defaultValue) { public object? DefaultValue { get; } = defaultValue; }
    public sealed class DependencyProperty
    {
        public static object UnsetValue { get; } = new();
        public object? DefaultValue { get; private init; }
        public static DependencyProperty RegisterAttached(string name, Type type, Type owner, PropertyMetadata metadata)
            => new() { DefaultValue = metadata.DefaultValue };
    }
    public class SetterBase { }
    public sealed class Setter : SetterBase { public DependencyProperty Property { get; set; } = null!; public object Value { get; set; } = null!; }
    public sealed class Style { public Style? BasedOn { get; set; } public List<SetterBase> Setters { get; } = new(); }

    public sealed class RoutedEventArgs : EventArgs { }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public sealed class XamlRoot { public List<Controls.Primitives.Popup> Popups { get; } = new(); }

    public class FrameworkElement : DependencyObject
    {
        public FrameworkElement(DependencyObject? nativePeer = null) : base(nativePeer) { }
        public Style? Style { get; set; }
        public event EventHandler<object>? LayoutUpdated;
        public void UpdateLayout() => LayoutUpdated?.Invoke(this, EventArgs.Empty);
        public bool IsLoaded { get; private set; }
        public XamlRoot XamlRoot { get; } = new();
        public event RoutedEventHandler? Loaded;
        public event RoutedEventHandler? Unloaded;
        public void SetLoaded(bool loaded)
        {
            IsLoaded = loaded;
            if (loaded) Loaded?.Invoke(this, new RoutedEventArgs());
            else Unloaded?.Invoke(this, new RoutedEventArgs());
        }
    }

    public sealed class ResourceDictionary : Dictionary<string, object> { }
    public sealed class Application
    {
        public static Application Current { get; } = new();
        public ResourceDictionary Resources { get; } = new();
    }
}

namespace Microsoft.UI.Xaml.Media
{
    public sealed class FontFamily
    {
        public FontFamily(string source) => Source = source;
        public string Source { get; }
    }

    public static class VisualTreeHelper
    {
        public static DependencyObject? GetParent(DependencyObject node) => DependencyObject.All.Find(p => p.TestChildren.Contains(node));
        public static int GetChildrenCount(DependencyObject node) => node.TestChildren.Count;
        public static DependencyObject GetChild(DependencyObject node, int index) => node.TestChildren[index];
        public static IReadOnlyList<Controls.Primitives.Popup> GetOpenPopupsForXamlRoot(XamlRoot root)
            => root.Popups;
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Control : FrameworkElement
    {
        public static DependencyProperty FontSizeProperty { get; } = new();
        public double FontSize { get => GetSize(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public Media.FontFamily FontFamily { get; set; } = new("XamlAutoFontFamily");
    }

    public sealed class TextBlock : FrameworkElement
    {
        public TextBlock(TextBlock? nativePeer = null) : base(nativePeer)
        {
            if (nativePeer is null) return;
            Text = nativePeer.Text;
            Style = nativePeer.Style;
            LineHeight = nativePeer.LineHeight;
            FontFamily = nativePeer.FontFamily;
        }
        public static DependencyProperty FontSizeProperty => Control.FontSizeProperty;
        public double FontSize { get => GetSize(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public double LineHeight { get; set; }
        public string Text { get; set; } = string.Empty;
        public Media.FontFamily FontFamily { get; set; } = new("XamlAutoFontFamily");
        public Documents.InlineCollection Inlines { get; } = new();
    }

    public sealed class RichTextBlock : FrameworkElement
    {
        public static DependencyProperty FontSizeProperty => Control.FontSizeProperty;
        public double FontSize { get => GetSize(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public double LineHeight { get; set; }
        public Media.FontFamily FontFamily { get; set; } = new("XamlAutoFontFamily");
        public List<Documents.Block> Blocks { get; } = new();
    }

    public sealed class ContentPresenter : FrameworkElement
    {
        public static DependencyProperty FontSizeProperty => Control.FontSizeProperty;
        public double FontSize { get => GetSize(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public Media.FontFamily FontFamily { get; set; } = new("XamlAutoFontFamily");
    }

    public sealed class RichTextBlockOverflow : FrameworkElement { }
}

namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public sealed class Popup : FrameworkElement
    {
        public FrameworkElement? Child { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Documents
{
    public class TextElement : DependencyObject
    {
        public static DependencyProperty FontSizeProperty => Controls.Control.FontSizeProperty;
        public double FontSize { get => GetSize(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public Media.FontFamily FontFamily { get; set; } = new("XamlAutoFontFamily");
    }
    public class Inline : TextElement { }
    public sealed class InlineCollection : List<Inline> { }
    public sealed class Span : Inline { public InlineCollection Inlines { get; } = new(); }
    public class Block : TextElement { public double LineHeight { get; set; } }
    public sealed class Paragraph : Block { public InlineCollection Inlines { get; } = new(); }
}
