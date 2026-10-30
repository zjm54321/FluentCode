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
    internal static class OpenCodeThemedXaml
    {
        internal static Style BrushStyle(string element, params (string Property, string Resource)[] brushes)
        {
            var style = new Style();
            foreach(var (property, resource) in brushes) style.BrushKeys[property] = resource;
            return style;
        }
    }
    internal partial class OpenCodeToolDetailPopup { internal static FrameworkElement? RetainedAnchor => null; }
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

    internal sealed class OpenCodeToolDetailOptions { }

    public sealed class OpenCodeToolPartView
    {
        internal Action<OpenCodeToolDetailOptions, string?, Func<string, bool>>? ConfigureDetail { get; set; }
        internal Func<string, (string? Agent, string? Description)?>? FindChild { get; set; }
        private Action<string, OpenCodeContentPart>? _backgroundAction;
        private OpenCodeContentPart? _lastPart;
        internal void SetBackgroundAction(Action<string, OpenCodeContentPart>? action) => _backgroundAction = action;
        internal Action? BackgroundAction => _backgroundAction is not null && _lastPart is not null && OpenCodeToolRowPresentation.Build(_lastPart, AsyncCompletion, ManualBackground).CanMoveToBackground
            ? () => { if (!IsDisposed && _parentMessageId is not null && _lastPart is not null) _backgroundAction?.Invoke(_parentMessageId, _lastPart); } : null;
        internal FrameworkElement? DetailAnchorOverride { get; private set; }
        internal void SetDetailAnchor(FrameworkElement? anchor) => DetailAnchorOverride = anchor;
        internal void TogglePinnedDetail() => ConversationViewTestProbe.Record("OpenLeaf", this);
        internal void HoverEnterDetail() => ConversationViewTestProbe.Record("HoverLeaf", this);
        internal void HoverExitDetail() => ConversationViewTestProbe.Record("ExitLeaf", this);
        internal static string KindGlyph(OpenCodeToolKind kind) => kind == OpenCodeToolKind.Subagent ? OpenCodeStepRunPresentation.SubagentGlyph : "tool";
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
        internal bool ManualBackground { get; private set; }
        internal int? FileIndex { get; private set; }
        internal int UpdateCount { get; private set; }

        public event Action<string>? TerminalRequested { add { } remove { } }
        public event Action<string?, string>? BrowserPageRequested { add { } remove { } }
        internal void Update(OpenCodeContentPart part, string? parentMessageId, OpenCodeAsyncCompletion? asyncCompletion, bool manualBackground = false, int? fileIndex = null)
        {
            _parentMessageId = parentMessageId;
            _lastPart = part;
            AsyncCompletion = asyncCompletion;
            ManualBackground = manualBackground;
            FileIndex = fileIndex;
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

    // No dispatcher/compositor in headless tests; the real clock receives explicit ticks.
    internal static class OpenCodeWorkingClockComposition
    {
        internal static void EnsureInitialized() { }
    }

    internal sealed class OpenCodeSubagentCompletionView : IDisposable
    {
        private Action<string>? _navigation;
        public FrameworkElement Element { get; }
        internal FrameworkElement ProcessContent { get; } = new StackPanel();
        internal OpenCodeTimelineRowContent ProcessPresentation { get; private set; } = null!;
        internal void OpenDetail() => ConversationViewTestProbe.Record("OpenLeaf", this);
        internal void HoverEnter() => ConversationViewTestProbe.Record("HoverLeaf", this);
        internal void HoverExit() => ConversationViewTestProbe.Record("ExitLeaf", this);
        internal void SetDetailAnchor(FrameworkElement? anchor) { }
        internal void SetProcessGroup(FrameworkElement group) { }
        internal int DisposeCount { get; private set; }
        internal bool IsDisposed { get; private set; }

        public OpenCodeSubagentCompletionView(FrameworkElement content, Action<string>? onChildSessionRequested = null)
        {
            Element = new Border { Child = content };
            _navigation = onChildSessionRequested;
            ConversationViewTestProbe.Record("Construct", this, Element, content);
        }

        public void Update(OpenCodeSubagentCompletionInfo? info, string? taskTitle = null, string? body = null)
        {
            if (info is not null) ProcessPresentation = OpenCodeStepRunPresentation.DescribeAsyncCompletion(new OpenCodeAsyncCompletion(
                OpenCodeAsyncCompletionKind.Subagent, info.ChildSessionId, info.State ?? string.Empty, taskTitle ?? string.Empty, null, body ?? string.Empty, info.ChildSessionId, null));
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
        internal FrameworkElement DetailAnchor => _root;
        internal void SetVisibility(bool visible) => _root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        internal Action? OpenLeaf { get; private set; }
        internal Action? EnterLeaf { get; private set; }
        internal Action? ExitLeaf { get; private set; }
        internal Action? BackgroundAction { get; private set; }
        internal void SetBackgroundAction(Action? action) => BackgroundAction = action;
        internal bool InlineLeaf { get; private set; }
        internal string? ExecutingModelId { get; private set; }
        internal string? CurrentStep { get; private set; }
        internal void SetCurrentStep(string step) => CurrentStep = step;
        internal bool WorkingReasoning { get; private set; }
        internal bool WorkingTool { get; private set; }
        internal void SetWorkingContext(bool reasoning, bool toolRunning, string? executingModelId, OpenCodeToolRowModel? runningTool = null)
            => (WorkingReasoning, WorkingTool, ExecutingModelId) = (reasoning, toolRunning, executingModelId);
        internal void SetInlineLeaf(bool inline) => InlineLeaf = inline;
        internal string? LeafName { get; private set; }
        internal string? LeafGlyph { get; private set; }
        internal OpenCodeToolRowStatus? LeafStatus { get; private set; }
        internal void BindSingleLeaf(Action? open, Action? enter, Action? exit, string? name = null, string? glyph = null, OpenCodeToolRowStatus? status = null)
            => (OpenLeaf, EnterLeaf, ExitLeaf, LeafName, LeafGlyph, LeafStatus) = (open, enter, exit, name, glyph, status);
        internal Panel ItemsHost => _items;
        internal bool IsExpanded { get; private set; }
        internal bool ShowsItems => InlineLeaf || IsExpanded && (Summary?.IsActive == true || OpenLeaf is null);
        internal bool HeaderVisible { get; private set; } = true;
        internal OpenCodeStepRunSummary? Summary { get; private set; }
        internal int DisposeCount { get; private set; }
        internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
        {
            if (Summary == summary && IsExpanded == expanded && HeaderVisible == headerVisible) return;
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
        internal FrameworkElement? DetailAnchorOverride { get; private set; }
        internal void SetDetailAnchor(FrameworkElement? anchor) => DetailAnchorOverride = anchor;
        internal void OpenDetail() => ConversationViewTestProbe.Record("OpenLeaf", this);
        internal void HoverEnter() => ConversationViewTestProbe.Record("HoverLeaf", this);
        internal void HoverExit() => ConversationViewTestProbe.Record("ExitLeaf", this);
        internal FrameworkElement Element { get; } = new StackPanel();
        internal string Text { get; private set; } = string.Empty;
        internal bool Streaming { get; private set; }
        internal OpenCodeToolRowStatus Status => Streaming ? OpenCodeToolRowStatus.Streaming : OpenCodeToolRowStatus.Completed;
        internal string RowKey { get; private set; } = string.Empty;
        internal int DisposeCount { get; private set; }
        internal OpenCodeReasoningRowView(OpenCodeReasoningExpansionState? expansion = null, Action<Action>? changeInline = null)
            => ConversationViewTestProbe.Record("Construct", this, Element);
        internal void Update(string text, bool streaming, string rowKey, bool encrypted = false)
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
        private readonly string _glyph;
        internal string Glyph => _glyph;
        internal FrameworkElement? DetailAnchorOverride { get; private set; }
        internal void SetDetailAnchor(FrameworkElement? anchor) => DetailAnchorOverride = anchor;
        internal void OpenDetail() => ConversationViewTestProbe.Record("OpenLeaf", this);
        internal void HoverEnter() => ConversationViewTestProbe.Record("HoverLeaf", this);
        internal void HoverExit() => ConversationViewTestProbe.Record("ExitLeaf", this);
        internal FrameworkElement Element { get; } = new StackPanel();
        internal string Label { get; private set; } = string.Empty;
        internal string? DetailText { get; private set; }
        internal OpenCodeTimelineRowContent Content { get; private set; } = new(string.Empty, string.Empty, string.Empty, null, string.Empty, string.Empty, Array.Empty<OpenCodeToolProperty>(), null);
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
            _glyph = glyph;
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
        internal bool IsExpanded { get; private set; }
        internal void SetExpanded(bool expanded) => IsExpanded = expanded;
        internal FrameworkElement Element { get; }
        internal bool Delivered { get; }
        internal OpenCodeUserBubbleView(FrameworkElement content, bool delivered = true, FrameworkElement? attachments = null)
        {
            Delivered = delivered;
            Element = new Border { Child = content };
            ConversationViewTestProbe.Record("Construct", this, Element, content);
        }
    }

    internal sealed class OpenCodeUserAttachmentsView : IDisposable
    {
        internal FrameworkElement Element { get; } = new StackPanel();
        internal void Update(OpenCodeMessage message) => ConversationViewTestProbe.Record("Attachments", this, message);
        public void Dispose() { }
    }

    internal sealed class OpenCodeTurnChipView
    {
        internal FrameworkElement Element { get; } = new StackPanel();
        internal OpenCodeTurnChipView(OpenCodeMessage message)
        {
            ConversationViewTestProbe.Record("Construct", this, message, Element);
            Update(message);
        }
        internal void Update(OpenCodeMessage message) { }
        internal void Dispose() { }
    }

    // Safe DTO presenter probe; explicit file reads belong to the native view, not the projection.
    internal sealed class OpenCodeInstructionNoticeView
    {
        internal FrameworkElement Element { get; } = new StackPanel();
        internal void OpenDetail() => ConversationViewTestProbe.Record("OpenLeaf", this);
        internal void HoverEnter() => ConversationViewTestProbe.Record("HoverLeaf", this);
        internal void HoverExit() => ConversationViewTestProbe.Record("ExitLeaf", this);
        internal void SetDetailAnchor(FrameworkElement? anchor) { }
        internal void Dispose() { }

        internal OpenCodeInstructionNoticeView(OpenCodeInstructionNotice notice)
        {
            ConversationViewTestProbe.Record("Construct", this, notice, Element);
            Update(notice);
        }

        internal void Update(OpenCodeInstructionNotice notice)
            => ConversationViewTestProbe.Record("Update", this, notice);
    }
}

// Inert dispatcher only: no clock, background work or native animation is simulated.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool TryEnqueue(Action callback) => true; // inert: tests explicitly stimulate layout/viewport
        public static DispatcherQueue? GetForCurrentThread() => null;
        public DispatcherQueueTimer CreateTimer() => new();
    }
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public event Action<DispatcherQueueTimer, object>? Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
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
        public Microsoft.UI.Xaml.Controls.Flyout? ContextFlyout { get; set; }
        public event EventHandler<object>? PointerEntered { add { } remove { } }
        public event EventHandler<object>? PointerExited { add { } remove { } }
        public event EventHandler<object>? GotFocus { add { } remove { } }
        public event EventHandler<object>? LostFocus { add { } remove { } }
        public static readonly object PointerPressedEvent = new(), PointerWheelChangedEvent = new(), KeyDownEvent = new();
        public void AddHandler(object routedEvent, object handler, bool handledEventsToo) { }
        public void RemoveHandler(object routedEvent, object handler) { }
        public Microsoft.UI.Xaml.Media.RectangleGeometry? Clip { get; set; }
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; } = 1;
        internal UIElement? Parent { get; set; }
        internal string AutomationId { get; set; } = string.Empty;
        internal string AutomationName { get; set; } = string.Empty;
    }

    public class FrameworkElement : UIElement
    {
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
        public XamlRoot? XamlRoot { get; set; } = new(); // inert root identity, not native window evidence
        public double ActualWidth { get; set; } = 700;
        public double ActualHeight { get; set; } = 600;
        public bool IsLoaded { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double TestTop { get; set; } = 400;
        // Optional explicit geometry stimulus. Default keeps the historical inert TestTop behavior.
        public Func<UIElement?, Microsoft.UI.Xaml.Media.GeneralTransform>? TestTransform { get; set; }
        public Microsoft.UI.Xaml.Media.GeneralTransform TransformToVisual(UIElement? target) => TestTransform?.Invoke(target) ?? new(TestTop);
        public event EventHandler<object>? LayoutUpdated;
        public void RaiseLayoutUpdatedForTest() => LayoutUpdated?.Invoke(this, new());
        public event RoutedEventHandler? Unloaded;
        public void RaiseUnloadedForTest() => Unloaded?.Invoke(this, new());
        public event RoutedEventHandler? Loaded;
        public void RaiseLoadedForTest() => Loaded?.Invoke(this, new());
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public Thickness Margin { get; set; }
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        public double MaxHeight { get; set; } = double.PositiveInfinity;
        public Style? Style { get; set; }
    }

    public class Style { public Dictionary<string,string> BrushKeys { get; } = new(); }
    public class XamlRoot { public Windows.Foundation.Size Size { get; set; } = new(800, 600); }
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
    public enum ScrollMode { Disabled, Enabled, Auto }

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
        public string SelectedText { get; set; } = string.Empty;
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
        public bool IsTabStop { get; set; }
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
        public ExpandDirection ExpandDirection { get; set; }
        public object? Header { get; set; }
        public bool IsExpanded { get; set; }
    }

    public enum ExpandDirection { Down, Up }

    public class Button : ContentControl
    {
        public Flyout? Flyout { get; set; }
        public bool IsEnabled { get; set; } = true;
        public event RoutedEventHandler? Click;
        internal void RaiseClickForTest() => Click?.Invoke(this, new RoutedEventArgs());
    }

    public class FontIcon : FrameworkElement
    {
        public string Glyph { get; set; } = string.Empty;
        public double FontSize { get; set; }
        public Brush? Foreground { get; set; }
    }

    // Inert menu boundary for the existing pending-message view tests; no native rendering claim.
    public class MenuFlyout : Flyout
    {
        public List<MenuFlyoutItem> Items { get; } = new();
    }
    public class MenuFlyoutItem : Button
    {
        public string Text { get; set; } = string.Empty;
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

    public class RichTextBlock : FrameworkElement { public string SelectedText { get; set; } = string.Empty; }
    public class ScrollViewerViewChangedEventArgs : EventArgs { public bool IsIntermediate { get; set; } }
    public class ScrollViewer : ContentControl
    {
        public double VerticalOffset { get; set; }
        public double ScrollableHeight { get; set; }
        public double ViewportHeight { get; set; } = 800;
        public event EventHandler<ScrollViewerViewChangedEventArgs>? ViewChanged;
        public Action? LayoutForTest { get; set; }
        public List<double> ProgrammaticOffsetsForTest { get; } = new();
        public void ScrollForTest(double offset, bool intermediate)
        {
            VerticalOffset = offset;
            ViewChanged?.Invoke(this, new() { IsIntermediate = intermediate });
        }
        public void NotifyExtentForTest() => ViewChanged?.Invoke(this, new());
        public bool DeferChangeForTest { get; set; }
        public double? PendingOffsetForTest { get; private set; }
        public void CommitChangeForTest()
        {
            if (PendingOffsetForTest is not { } offset) return;
            PendingOffsetForTest = null;
            VerticalOffset = offset;
            ViewChanged?.Invoke(this, new());
        }
        public void UpdateLayout() => LayoutForTest?.Invoke();
        public void ChangeView(double? horizontal, double? vertical, double? zoom, bool disableAnimation)
        {
            if (vertical is { } offset) ProgrammaticOffsetsForTest.Add(offset);
            if (DeferChangeForTest) { PendingOffsetForTest = vertical; return; }
            VerticalOffset = vertical ?? VerticalOffset; ViewChanged?.Invoke(this, new());
        }
        public ScrollMode HorizontalScrollMode { get; set; }
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
    }
    public class ScrollContentPresenter : ContentControl { }
    public enum ScrollingInteractionState { Idle, Interaction, Inertia, Animation }
    public enum ScrollingAnimationMode { Enabled, Disabled }
    public enum ScrollingSnapPointsMode { Ignore }
    public sealed record ScrollingScrollOptions(ScrollingAnimationMode AnimationMode, ScrollingSnapPointsMode SnapPointsMode);
    public class ScrollView : ScrollViewer
    {
        public ScrollView()
        {
            base.ViewChanged += (_, e) =>
            {
                State = e.IsIntermediate ? ScrollingInteractionState.Inertia : ScrollingInteractionState.Idle;
                ViewChanged?.Invoke(this, new());
            };
        }
        public double HorizontalOffset { get; set; }
        public ScrollingInteractionState State { get; private set; }
        public new event Action<ScrollView, object>? ViewChanged;
        public event Action<ScrollView, object>? StateChanged;
        public void SetStateForTest(ScrollingInteractionState state)
        {
            State = state;
            StateChanged?.Invoke(this, new());
        }
        public int ScrollTo(double horizontal, double vertical, ScrollingScrollOptions options)
        {
            _ = horizontal;
            ChangeView(null, vertical, null, options.AnimationMode == ScrollingAnimationMode.Disabled);
            return 1;
        }
    }
    public class Flyout
    {
        public Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode Placement { get; set; }
        public object? Content { get; set; }
        public bool IsOpen { get; private set; }
        public event EventHandler<object>? Opening;
        public void OpenForTest() { Opening?.Invoke(this, new()); IsOpen = true; }
        public void Hide() => IsOpen = false;
    }
}

namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public enum FlyoutPlacementMode { TopEdgeAlignedLeft }
}

namespace Microsoft.UI.Xaml.Input
{
    public class PointerRoutedEventArgs : RoutedEventArgs { }
    public class KeyRoutedEventArgs : RoutedEventArgs { }
    public delegate void PointerEventHandler(object sender, PointerRoutedEventArgs e);
    public delegate void KeyEventHandler(object sender, KeyRoutedEventArgs e);
    public static class FocusManager
    {
        public static object? FocusedForTest { get; set; }
        public static object? GetFocusedElement(XamlRoot root) => FocusedForTest;
    }
}

namespace Windows.Foundation
{
    public readonly record struct Point(double X, double Y);
    public readonly record struct Size(double Width, double Height);
    public readonly record struct Rect(double X, double Y, double Width, double Height)
    { public double Right => X + Width; public double Bottom => Y + Height; }
}

namespace Microsoft.UI.Xaml.Media
{
    public sealed class GeneralTransform(double y, double x = 0, double scaleX = 1, double scaleY = 1, double skew = 0)
    {
        public Windows.Foundation.Point TransformPoint(Windows.Foundation.Point p) => new(x + p.X * scaleX + p.Y * skew, p.Y * scaleY + y);
    }
    public class Transform { }
    public class Geometry { public Transform? Transform { get; set; } }
    public class RectangleGeometry : Geometry { public Windows.Foundation.Rect Rect { get; set; } }
    public static class VisualTreeHelper
    {
        public static DependencyObject? GetParent(DependencyObject node) => (node as UIElement)?.Parent;
        private static IReadOnlyList<UIElement> Children(DependencyObject node) => node switch
        {
            Panel panel => panel.Children,
            Border { Child: { } child } => new[] { child },
            ContentControl { Content: UIElement child } => new[] { child },
            _ => Array.Empty<UIElement>(),
        };
        public static int GetChildrenCount(DependencyObject node) => Children(node).Count;
        public static DependencyObject GetChild(DependencyObject node, int index) => Children(node)[index];
    }
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
