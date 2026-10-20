// Linked-source test host only. Link the real ConversationView, InstructionNotice projection,
// SubagentPresentation (including its event args/summary view), TaskTitles, DTOs and ToolStates.
// Do NOT also link the four presenter implementations replaced below or another WinUI test host.
// No dispatch, sanitization, title, fingerprint, parsing or ID-validation algorithm lives here.
// Probe data stays in memory; callers must not print raw inputs. Reset between isolated fixtures.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode
{
    internal static class ConversationViewTestProbe
    {
        // A class rather than a record: default ToString must not dump captured input fields.
        internal sealed class Observation
        {
            internal string Operation { get; init; } = string.Empty;
            internal object Subject { get; init; } = null!;
            internal object? Input { get; init; }
            internal object? Detail { get; init; }
        }

        private static readonly List<Observation> _records = new();
        internal static IReadOnlyList<Observation> Records => _records;
        internal static void Reset() => _records.Clear();

        internal static void Record(string operation, object subject, object? input = null, object? detail = null)
            => _records.Add(new Observation { Operation = operation, Subject = subject, Input = input, Detail = detail });

        // Ancestor removal does not synthesize child-removal or disposal events. This query checks
        // whether a presenter's stable Element remains under the conversation's root instead.
        internal static bool IsAttachedTo(UIElement element, UIElement root)
        {
            for (UIElement? current = element; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, root)) return true;
            }
            return false;
        }
    }

    public enum OpenCodeRichTextStyle { Body = 0, Reasoning = 1 }

    public sealed class OpenCodeRichTextPresenter
    {
        public FrameworkElement Element { get; } = new StackPanel();
        public OpenCodeRichTextPresenter() => ConversationViewTestProbe.Record("Construct", this, Element);
        public void Render(string? source, OpenCodeRichTextStyle style)
            => ConversationViewTestProbe.Record("Render", this, source, style);
    }

    public sealed class OpenCodeToolPartView
    {
        private EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? _navigation;
        private string? _parentMessageId;
        public Expander Element { get; } = new();
        internal int DisposeCount { get; private set; }
        internal bool IsDisposed { get; private set; }
        internal int SubscriberCount => _navigation?.GetInvocationList().Length ?? 0;

        public OpenCodeToolPartView() => ConversationViewTestProbe.Record("Construct", this, Element);

        public event EventHandler<OpenCodeSubagentSessionRequestedEventArgs>? SubagentSessionRequested
        {
            add
            {
                _navigation += value;
                ConversationViewTestProbe.Record("Subscribe", this);
            }
            remove
            {
                _navigation -= value;
                ConversationViewTestProbe.Record("Unsubscribe", this);
            }
        }

        public void Update(OpenCodeContentPart part, string? parentMessageId = null)
            => Update(part, parentMessageId, null);

        // Records the async completion (background shell / subagent) the renderer matched to this call.
        internal OpenCodeAsyncCompletion? AsyncCompletion { get; private set; }
        internal int UpdateCount { get; private set; }

        internal void Update(OpenCodeContentPart part, string? parentMessageId, OpenCodeAsyncCompletion? asyncCompletion)
        {
            _parentMessageId = parentMessageId;
            AsyncCompletion = asyncCompletion;
            UpdateCount++;
            ConversationViewTestProbe.Record("Update", this, part, parentMessageId);
        }

        // Test stimulus only: supplied IDs are not parsed or validated by the host.
        internal void RaiseNavigationForTest(string childSessionId)
        {
            var args = new OpenCodeSubagentSessionRequestedEventArgs(_parentMessageId, childSessionId);
            ConversationViewTestProbe.Record("NavigationAttempt", this, args, SubscriberCount);
            if (!IsDisposed) _navigation?.Invoke(this, args);
        }

        public void Dispose()
        {
            DisposeCount++;
            ConversationViewTestProbe.Record("Dispose", this);
            IsDisposed = true;
            _navigation = null;
        }
    }

    internal sealed class OpenCodeSubagentCompletionView : IDisposable
    {
        private Action<string>? _navigation;
        public FrameworkElement Element { get; }
        internal int DisposeCount { get; private set; }
        internal bool IsDisposed { get; private set; }

        public OpenCodeSubagentCompletionView(FrameworkElement content, Action<string>? onChildSessionRequested = null)
        {
            Element = new Border { Child = content };
            _navigation = onChildSessionRequested;
            ConversationViewTestProbe.Record("Construct", this, Element, content);
        }

        public void Update(OpenCodeSubagentCompletionInfo? info, string? taskTitle = null)
        {
            ConversationViewTestProbe.Record("Update", this, info, taskTitle);
        }

        // Explicit callback stimulus, not a simulation of the real card's click eligibility.
        internal void RaiseNavigationForTest(string childSessionId)
        {
            ConversationViewTestProbe.Record("NavigationAttempt", this, childSessionId);
            if (!IsDisposed) _navigation?.Invoke(childSessionId);
        }

        public void Dispose()
        {
            DisposeCount++;
            ConversationViewTestProbe.Record("Dispose", this);
            IsDisposed = true;
            _navigation = null;
        }
    }

    // Inert stand-in for the real collapsible tool group (§6.2). Element/ItemsHost identities are stable;
    // ConversationView aligns tool-row elements into ItemsHost. No grouping algorithm lives here.
    internal sealed class OpenCodeToolGroupView
    {
        private readonly Action<string, bool> _onUserToggled;
        private readonly StackPanel _root = new();
        private readonly StackPanel _items = new();
        internal OpenCodeToolGroupView(string groupId, Action<string, bool> onUserToggled)
        {
            GroupId = groupId;
            _onUserToggled = onUserToggled;
            _root.Children.Add(_items);
            ConversationViewTestProbe.Record("Construct", this, groupId);
        }
        internal string GroupId { get; }
        internal FrameworkElement Element => _root;
        internal Panel ItemsHost => _items;
        internal bool IsExpanded { get; private set; }
        internal bool HeaderVisible { get; private set; } = true;
        internal OpenCodeStepRunSummary? Summary { get; private set; }
        internal int DisposeCount { get; private set; }
        internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
        {
            Summary = summary;
            IsExpanded = expanded;
            HeaderVisible = headerVisible;
            ConversationViewTestProbe.Record("Update", this, summary, expanded);
        }
        // Test stimulus for the real header click: flips state and reports the user toggle.
        internal void ToggleForTest()
        {
            IsExpanded = !IsExpanded;
            _onUserToggled(GroupId, IsExpanded);
        }
        internal void Dispose()
        {
            DisposeCount++;
            ConversationViewTestProbe.Record("Dispose", this);
        }
    }

    // Inert stand-in for the real merged "思考" row (F4): records the merged text it was given.
    internal sealed class OpenCodeReasoningRowView
    {
        internal FrameworkElement Element { get; } = new StackPanel();
        internal string Text { get; private set; } = string.Empty;
        internal bool Streaming { get; private set; }
        internal string RowKey { get; private set; } = string.Empty;
        internal int DisposeCount { get; private set; }
        internal OpenCodeReasoningRowView() => ConversationViewTestProbe.Record("Construct", this, Element);
        internal void Update(string text, bool streaming, string rowKey)
        {
            Text = text;
            Streaming = streaming;
            RowKey = rowKey;
            ConversationViewTestProbe.Record("Update", this, text, streaming);
        }
        internal void Dispose()
        {
            DisposeCount++;
            ConversationViewTestProbe.Record("Dispose", this);
        }
    }

    // Inert stand-in for the real tool-like timeline row (F8 / G4 / H5): name + hover-only detail.
    internal sealed class OpenCodeTimelineChipView
    {
        internal FrameworkElement Element { get; } = new StackPanel();
        internal string Label { get; private set; } = string.Empty;
        internal string? DetailText { get; private set; }
        internal OpenCodeTimelineRowContent? Content { get; private set; }
        internal Action? LinkInvoked { get; private set; }
        internal void UpdateRow(OpenCodeTimelineRowContent content, Action? linkInvoked = null)
        {
            Content = content;
            Label = content.Name;
            DetailText = content.DetailText;
            LinkInvoked = linkInvoked;
            ConversationViewTestProbe.Record("Update", this, content.Name, content.DetailText);
        }
        internal int DisposeCount { get; private set; }
        internal OpenCodeTimelineChipView(string automationId, string glyph, string? labelAutomationId = null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(Element, automationId);
            ConversationViewTestProbe.Record("Construct", this, Element);
        }
        internal void Update(string label, string accessibleName, string detailTitle,
            IReadOnlyList<OpenCodeToolProperty>? properties, string? detailText)
        {
            Label = label;
            DetailText = detailText;
            ConversationViewTestProbe.Record("Update", this, label, detailText);
        }
        internal void Dispose()
        {
            DisposeCount++;
            ConversationViewTestProbe.Record("Dispose", this);
        }
    }

    // Inert stand-in for the real two-line user bubble (H14): stable Element hosting the presenter root.
    internal sealed class OpenCodeUserBubbleView
    {
        internal FrameworkElement Element { get; }
        internal OpenCodeUserBubbleView(FrameworkElement content)
        {
            Element = new Border { Child = content };
            ConversationViewTestProbe.Record("Construct", this, Element, content);
        }
    }

    // Intentionally has neither IDisposable nor an event, matching the real safe-DTO-only view.
    internal sealed class OpenCodeInstructionNoticeView
    {
        internal FrameworkElement Element { get; } = new StackPanel();

        internal OpenCodeInstructionNoticeView(OpenCodeInstructionNotice notice)
        {
            ConversationViewTestProbe.Record("Construct", this, notice, Element);
            Update(notice);
        }

        internal void Update(OpenCodeInstructionNotice notice)
            => ConversationViewTestProbe.Record("Update", this, notice);
    }
}

namespace AIFrontend.Services.Appearance
{
    // Inert stand-in for the real font service: records which slot an element asked for.
    public enum AppFontSlot { Body = 0, Serif = 1, Monospace = 2 }

    public static class AppFonts
    {
        public static void Apply(Microsoft.UI.Xaml.FrameworkElement element, AppFontSlot slot)
            => AIFrontend.Pages.OpenCode.ConversationViewTestProbe.Record("Font", element, slot);
    }
}

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Left, Center, Right, Stretch }
    public enum VerticalAlignment { Top, Center, Bottom, Stretch }
    public enum TextWrapping { NoWrap, Wrap, WrapWholeWords }
    public enum TextTrimming { None, CharacterEllipsis }
    public class RoutedEventArgs : EventArgs { }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);

    public readonly struct Thickness
    {
        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
        public Thickness(double left, double top, double right, double bottom)
        { Left = left; Top = top; Right = right; Bottom = bottom; }
        public double Left { get; }
        public double Top { get; }
        public double Right { get; }
        public double Bottom { get; }
    }

    public readonly struct CornerRadius
    {
        public CornerRadius(double uniform) { TopLeft = TopRight = BottomRight = BottomLeft = uniform; }
        public double TopLeft { get; }
        public double TopRight { get; }
        public double BottomRight { get; }
        public double BottomLeft { get; }
    }

    public class DependencyObject { }
    public class UIElement : DependencyObject
    {
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; } = 1;
        internal UIElement? Parent { get; set; }
        internal string AutomationId { get; set; } = string.Empty;
        internal string AutomationName { get; set; } = string.Empty;
    }

    public class FrameworkElement : UIElement
    {
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public Thickness Margin { get; set; }
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        public double MaxHeight { get; set; } = double.PositiveInfinity;
        public Style? Style { get; set; }
    }

    public class Style { }
    public class ResourceDictionary : Dictionary<object, object> { }
    public class Application
    {
        public static Application? Current { get; set; }
        public ResourceDictionary Resources { get; } = new();
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    using AIFrontend.Pages.OpenCode;
    using Microsoft.UI.Xaml.Media;

    public enum Orientation { Vertical, Horizontal }
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
    public enum ScrollBarVisibility { Disabled, Auto, Hidden, Visible }

    // Only direct membership changes are recorded. Failed Remove calls produce no removal record.
    public sealed class UIElementCollection : Collection<UIElement>
    {
        private readonly UIElement _owner;
        internal UIElementCollection(UIElement owner) => _owner = owner;
        protected override void InsertItem(int index, UIElement item)
        {
            if (item.Parent is not null) throw new InvalidOperationException("Element already has a parent.");
            base.InsertItem(index, item);
            item.Parent = _owner;
            ConversationViewTestProbe.Record("Insert", item, _owner, index);
        }
        protected override void RemoveItem(int index)
        {
            var item = this[index];
            base.RemoveItem(index);
            item.Parent = null;
            ConversationViewTestProbe.Record("Remove", item, _owner, index);
        }
        protected override void SetItem(int index, UIElement item)
        {
            if (ReferenceEquals(this[index], item)) return;
            if (item.Parent is not null) throw new InvalidOperationException("Element already has a parent.");
            RemoveItem(index);
            InsertItem(index, item);
        }
        protected override void ClearItems()
        {
            while (Count > 0) RemoveItem(Count - 1);
        }
    }

    public class Panel : FrameworkElement
    {
        public Panel() => Children = new UIElementCollection(this);
        public UIElementCollection Children { get; }
        public Brush? Background { get; set; }
    }

    public class StackPanel : Panel
    {
        public Orientation Orientation { get; set; }
        public double Spacing { get; set; }
        public Thickness Padding { get; set; }
    }

    public enum GridUnitType { Auto, Pixel, Star }
    public readonly struct GridLength
    {
        public GridLength(double value, GridUnitType type = GridUnitType.Pixel) { Value = value; GridUnitType = type; }
        public double Value { get; }
        public GridUnitType GridUnitType { get; }
        public static GridLength Auto => new(1, GridUnitType.Auto);
    }
    public sealed class ColumnDefinition { public GridLength Width { get; set; } }
    public class Grid : Panel
    {
        public double ColumnSpacing { get; set; }
        private static readonly Dictionary<UIElement, int> Columns = new();
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public static void SetColumn(FrameworkElement element, int value) => Columns[element] = value;
        public static int GetColumn(FrameworkElement element) => Columns.TryGetValue(element, out var value) ? value : 0;
    }

    internal static class TestHostTree
    {
        internal static void Replace(UIElement owner, object? previous, object? next)
        {
            if (ReferenceEquals(previous, next)) return;
            if (next is UIElement { Parent: not null })
                throw new InvalidOperationException("Element already has a parent.");
            if (previous is UIElement oldElement)
            {
                oldElement.Parent = null;
                ConversationViewTestProbe.Record("Remove", oldElement, owner);
            }
            if (next is UIElement newElement)
            {
                newElement.Parent = owner;
                ConversationViewTestProbe.Record("Insert", newElement, owner);
            }
        }
    }

    public class Border : FrameworkElement
    {
        private UIElement? _child;
        public UIElement? Child
        {
            get => _child;
            set { TestHostTree.Replace(this, _child, value); _child = value; }
        }
        public Brush? Background { get; set; }
        public Brush? BorderBrush { get; set; }
        public Thickness BorderThickness { get; set; }
        public Thickness Padding { get; set; }
        public CornerRadius CornerRadius { get; set; }
    }

    public class TextBlock : FrameworkElement
    {
        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set { _text = value; ConversationViewTestProbe.Record("Text", this, value); }
        }
        public double FontSize { get; set; }
        public TextTrimming TextTrimming { get; set; }
        public int MaxLines { get; set; }
        public TextWrapping TextWrapping { get; set; }
        public bool IsTextSelectionEnabled { get; set; }
        public FontFamily? FontFamily { get; set; }
        public Windows.UI.Text.FontWeight FontWeight { get; set; }
        public Brush? Foreground { get; set; }
    }

    public class ContentControl : FrameworkElement
    {
        private object? _content;
        public object? Content
        {
            get => _content;
            set { TestHostTree.Replace(this, _content, value); _content = value; }
        }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
    }

    public class Expander : ContentControl
    {
        public object? Header { get; set; }
        public bool IsExpanded { get; set; }
    }

    public class Button : ContentControl
    {
        public bool IsEnabled { get; set; } = true;
        public event RoutedEventHandler? Click;
        internal void RaiseClickForTest() => Click?.Invoke(this, new RoutedEventArgs());
    }

    public class FontIcon : FrameworkElement
    {
        public string Glyph { get; set; } = string.Empty;
        public double FontSize { get; set; }
    }

    public static class ToolTipService
    {
        public static void SetToolTip(DependencyObject element, object value) { }
    }

    public class InfoBar : FrameworkElement
    {
        public InfoBarSeverity Severity { get; set; }
        public bool IsClosable { get; set; }
        public bool IsOpen { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public class ScrollViewer : ContentControl
    {
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class FontFamily
    {
        public FontFamily(string source) => Source = source;
        public string Source { get; }
    }
}

namespace Windows.UI.Text
{
    public struct FontWeight { public ushort Weight { get; set; } }
}

namespace Microsoft.UI.Text
{
    public static class FontWeights
    {
        public static Windows.UI.Text.FontWeight SemiBold => new() { Weight = 600 };
    }
}

namespace Microsoft.UI.Xaml.Automation
{
    using AIFrontend.Pages.OpenCode;

    public static class AutomationProperties
    {
        public static void SetAutomationId(DependencyObject element, string value)
        {
            ((UIElement)element).AutomationId = value;
            ConversationViewTestProbe.Record("AutomationId", element, value);
        }
        public static string GetAutomationId(DependencyObject element) => ((UIElement)element).AutomationId;
        public static void SetName(DependencyObject element, string value)
        {
            ((UIElement)element).AutomationName = value;
            ConversationViewTestProbe.Record("AutomationName", element, value);
        }
        public static string GetName(DependencyObject element) => ((UIElement)element).AutomationName;
        public static void SetAccessibilityView(DependencyObject element, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView value) { }
    }
}

namespace Microsoft.UI.Xaml.Automation.Peers
{
    public enum AccessibilityView { Raw, Control, Content }
}
