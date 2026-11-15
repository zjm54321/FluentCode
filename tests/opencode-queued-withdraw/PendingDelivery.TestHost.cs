// Inert WinUI/page boundaries only. Delivery handlers, receipt projection, list rendering,
// pending-steer rendering, the touch reveal helper, HTTP transport and ownership predicates are production source.
// The stubs record layout inputs (columns, sizes, style keys, tooltips); nothing here measures or draws.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages
{
    public sealed partial class OpenCodePage
    {
        private readonly OpenCodePageSource _source;
        private readonly OpenCodeClient _client;
        private readonly long _sourceRevision;
        private int _sourceGeneration = 1, _pageGeneration = 1, _sessionGeneration = 1;
        private bool _pageActive = true;
        private string? _selectedSessionId;
        private string? _queuedInboxSessionId;
        private IReadOnlyList<OpenCodeInboxItem>? _queuedInbox;
        private bool _queuedWithdrawInFlight;
        private readonly List<string> _pendingAckIds = new();
        private readonly OpenCodeQueuedPromptsView _queuedPromptsView = new();
        private readonly OpenCodeConversationView _conversationView = new();
        private readonly Grid QueuedPromptsHost = new();
        internal List<string> Notices { get; } = new();
        internal int UncertainChanges => _uncertainDelivery.Count;
        internal IReadOnlyList<OpenCodeInboxItem>? InboxSnapshot => _queuedInbox;
        internal OpenCodeConversationView Conversation => _conversationView;
        internal OpenCodeQueuedPromptsView Queue => _queuedPromptsView;

        internal OpenCodePage(OpenCodeClient client, string session)
        {
            _client = client;
            _source = new OpenCodePageSource("delivery-test-" + Guid.NewGuid(), 1, () => client);
            _sourceRevision = _source.Revision;
            _selectedSessionId = session;
            InitializePendingDeliveryActions();
            _conversationView.PendingSteerDeliveryChangeRequested += (_, request) => _queuedPromptsView.RequestDeliveryChange(request);
            _queuedPromptsView.BusyChanged += (_, _) => _conversationView.SetPendingSteerBusy(_queuedPromptsView.IsWithdrawBusy);
        }

        private bool SourceReady => _pageActive && !_source.IsRevoked;
        private CancellationTokenSource CreateOperationToken(OpenCodePageSource source)
            => CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken);
        private sealed record MessageHistoryRead(OpenCodeClient Client, string SessionId, int Page, int Source, int Session);
        private MessageHistoryRead CaptureMessageHistoryRead(OpenCodeClient client, string session)
            => new(client, session, _pageGeneration, _sourceGeneration, _sessionGeneration);
        private bool IsCurrentMessageHistoryRead(MessageHistoryRead read) => SourceReady && ReferenceEquals(read.Client, _client)
            && read.SessionId == _selectedSessionId && read.Page == _pageGeneration
            && read.Source == _sourceGeneration && read.Session == _sessionGeneration;
        partial void OnComposerPromptAdmitted(string sessionId, OpenCodeInboxUser receipt);
        private void ShowMessage(InfoBarSeverity severity, string title, string message) => Notices.Add(title + "：" + message);
        private void UpdateWaitingText() { }
        private void UpdateComposerButtons() { }

        internal void Own(string id) => _pendingAckIds.Add(id);
        internal bool CanChange(OpenCodeInboxItem item) => CanUpdatePendingDelivery(item);
        internal bool CanWithdraw(OpenCodeInboxItem item) => CanWithdrawQueued(item);
        internal void Admit(OpenCodeInboxUser receipt) => OnComposerPromptAdmitted(_selectedSessionId!, receipt);
        internal void Publish(IReadOnlyList<OpenCodeInboxItem> inbox, bool trimAcknowledgements = false)
        {
            _queuedInboxSessionId = _selectedSessionId;
            _queuedInbox = inbox;
            if (trimAcknowledgements) _pendingAckIds.RemoveAll(id => !inbox.Any(item => item.Id == id));
            UpdateQueuedPrompts();
        }
        internal void ReplaceSession(string session) { _selectedSessionId = session; _sessionGeneration++; _pendingAckIds.Clear(); UpdateQueuedPrompts(); }
        internal void Revoke() => _source.Revoke();
        internal void SetBusy(bool busy) => _queuedWithdrawInFlight = busy;
        internal Task Change(string id, string delivery) => Dispatch(() => _queuedPromptsView.RequestDeliveryChange(new(id, delivery)));
        internal Task Click(Button button) => Dispatch(button.RaiseClick);
        internal Task Run(Action action) => Dispatch(action);

        // Async-void event completion is tracked by its actual synchronization-context operations,
        // not sleeps or busy-flag sampling (which can precede the final view update).
        private static Task Dispatch(Action action)
        {
            var previous = SynchronizationContext.Current;
            var context = new EventCompletionContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try { action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            return context.Completion;
        }
        private sealed class EventCompletionContext : SynchronizationContext
        {
            private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Completion => _done.Task;
            public override void OperationCompleted() => _done.TrySetResult();
            public override void Post(SendOrPostCallback callback, object? state) => ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try { callback(state); }
                catch (Exception error) { _done.TrySetException(error); }
                finally { SetSynchronizationContext(previous); }
            });
        }
    }
}

namespace FluentCode.Pages.OpenCode
{
    public sealed partial class OpenCodeConversationView : StackPanel
    {
        private void InvalidateIdleViewport() { }
        internal bool ContainsRenderedMessage(string id) => _renderedUserMessageIds.Contains(id);
        internal IReadOnlyList<string> PendingIds => _steerOrder.Where(id => ReferenceEquals(_steerRows[id].Element.Parent, this)).ToArray();
        internal void Delivered(string id) { _renderedUserMessageIds.Add(id); ReconcileSteerRows(); }
        internal object? TextMenu(string id) => _steerRows[id].Presenter.TextContextFlyout;
    }
    internal static class OpenCodeInteractionUi
    {
        internal const string BodyStyleKey = "body", CaptionSubtleStyleKey = "caption-subtle", CaptionStyleKey = "caption";
        internal static TextBlock StyledText(string text, string key, bool selectable = false) => new() { Text = text };
        internal static Style? TryStyle(string key) => new(key);
    }
    internal enum OpenCodeRichTextStyle { Body }
    internal sealed class OpenCodeRichTextPresenter
    {
        internal FrameworkElement Element { get; } = new TextBlock();
        internal (int Images, int Pdfs) AttachmentCounts { get; set; }
        // Like the real presenter, the menu lands on the rendered text itself.
        internal object? TextContextFlyout { get => Element.ContextFlyout; set => Element.ContextFlyout = value; }
        internal void Render(string? text, OpenCodeRichTextStyle style) => ((TextBlock)Element).Text = text ?? string.Empty;
    }
    internal sealed class OpenCodeUserAttachmentsView : IDisposable
    {
        internal FrameworkElement Element { get; } = new Grid();
        internal void Update(IReadOnlyList<OpenCodeUserAttachment> attachments, bool hasText) { }
        public void Dispose() { }
    }
    internal sealed class OpenCodeUserBubbleView
    {
        internal FrameworkElement Element { get; }
        internal OpenCodeUserBubbleView(FrameworkElement content, bool delivered, FrameworkElement? attachments)
        {
            var body = new StackPanel();
            body.Children.Add(new ContentControl { Content = content, MaxHeight = 40 });
            if (attachments is not null) body.Children.Add(attachments);
            Element = body;
        }
    }
    internal sealed record OpenCodeContextCommand(string Label, string Glyph, Action Run)
    {
        internal bool Enabled { get; init; }
        internal string? AutomationId { get; init; }
    }
    // Inert menu: keeps the production delegates so a test can open it and run an entry headlessly.
    internal sealed class OpenCodeMessageMenuProbe(Func<string?> text, Func<IEnumerable<OpenCodeContextCommand>> actions)
    {
        internal Func<string?> Text => text;
        internal Func<IEnumerable<OpenCodeContextCommand>> Actions => actions;
    }
    internal static class OpenCodeMessageContextMenu
    {
        internal static OpenCodeMessageMenuProbe Create(Func<string?> text, Func<IEnumerable<OpenCodeContextCommand>> actions) => new(text, actions);
    }
}

namespace Microsoft.UI.Input { public enum PointerDeviceType { Mouse, Touch, Pen } }
namespace Microsoft.UI.Xaml.Input
{
    public sealed class Pointer(Microsoft.UI.Input.PointerDeviceType type) { public Microsoft.UI.Input.PointerDeviceType PointerDeviceType => type; }
    public sealed class PointerRoutedEventArgs(Microsoft.UI.Input.PointerDeviceType type) : EventArgs { public Pointer Pointer { get; } = new(type); }
}
namespace Microsoft.UI { public static class Colors { public static object Transparent { get; } = new(); } }
namespace Microsoft.UI.Xaml.Media { public sealed class SolidColorBrush { public SolidColorBrush(object color) { } } }
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public enum HorizontalAlignment { Stretch, Left, Center, Right }
    public enum VerticalAlignment { Stretch, Top, Center, Bottom }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { CharacterEllipsis }
    public enum GridUnitType { Auto, Pixel, Star }
    public readonly record struct GridLength(double Value, GridUnitType Unit = GridUnitType.Pixel) { public static GridLength Auto => new(1, GridUnitType.Auto); }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom)
    {
        public Thickness(double uniform) : this(uniform, uniform, uniform, uniform) { }
    }
    public sealed class Style(string key) { public string Key => key; }
    public sealed class RoutedEventArgs : EventArgs { }
    public class UIElement
    {
        public UIElement? Parent { get; internal set; }
        public Visibility Visibility { get; set; }
        public double Opacity { get; set; } = 1;
        public bool IsHitTestVisible { get; set; } = true;
        public object? ContextFlyout { get; set; }
        internal string AutomationId = "", AutomationName = "";
        internal object? ToolTip;
        internal int Column;
        public event EventHandler<Input.PointerRoutedEventArgs>? PointerEntered;
        public event EventHandler<Input.PointerRoutedEventArgs>? PointerExited;
        public event EventHandler<RoutedEventArgs>? GotFocus;
        public event EventHandler<RoutedEventArgs>? LostFocus;
        internal void Touch() => PointerEntered?.Invoke(this, new(Microsoft.UI.Input.PointerDeviceType.Touch));
        internal void Focus() => GotFocus?.Invoke(this, new());
        internal void Blur() => LostFocus?.Invoke(this, new());
        internal void MouseExit() => PointerExited?.Invoke(this, new(Microsoft.UI.Input.PointerDeviceType.Mouse));
        internal void MouseEnter() => PointerEntered?.Invoke(this, new(Microsoft.UI.Input.PointerDeviceType.Mouse));
        internal void TouchExit() => PointerExited?.Invoke(this, new(Microsoft.UI.Input.PointerDeviceType.Touch));
    }
    public class FrameworkElement : UIElement
    {
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
        public double MaxHeight { get; set; }
        public double MaxWidth { get; set; } = double.PositiveInfinity;
        public double Width { get; set; } = double.NaN;
        public double Height { get; set; } = double.NaN;
        public double ActualWidth { get; set; } // Never laid out here: 0 unless a test supplies a transcript width.
        public Thickness Margin { get; set; }
        public Style? Style { get; set; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
    public enum ScrollBarVisibility { Auto, Disabled }
    public enum ScrollMode { Disabled }
    public enum ListViewSelectionMode { None }
    public enum ExpandDirection { Up }
    public enum Orientation { Horizontal, Vertical }
    public class Panel : FrameworkElement
    {
        public Panel() => Children = new ElementCollection(this);
        public ElementCollection Children { get; }
        public object? Background { get; set; }
    }
    public sealed class ElementCollection(UIElement owner) : Collection<UIElement>
    {
        protected override void InsertItem(int index, UIElement item) { base.InsertItem(index, item); item.Parent = owner; }
        protected override void RemoveItem(int index) { this[index].Parent = null; base.RemoveItem(index); }
    }
    public class Grid : Panel
    {
        public double ColumnSpacing { get; set; }
        public List<ColumnDefinition> ColumnDefinitions { get; } = new();
        public static void SetColumn(UIElement child, int column) => child.Column = column;
        public static int GetColumn(UIElement child) => child.Column;
    }
    public sealed class ColumnDefinition { public GridLength Width { get; set; } }
    public class StackPanel : Panel { public double Spacing { get; set; } public Orientation Orientation { get; set; } }
    public class ContentControl : FrameworkElement
    {
        private object? _content;
        public object? Content
        {
            get => _content;
            set { if (_content is UIElement old) old.Parent = null; _content = value; if (value is UIElement element) element.Parent = this; }
        }
        public bool IsTabStop { get; set; } = true;
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
    }
    public sealed class ScrollViewer : ContentControl
    {
        public ScrollBarVisibility VerticalScrollBarVisibility { get; set; }
        public ScrollBarVisibility HorizontalScrollBarVisibility { get; set; }
        public ScrollMode HorizontalScrollMode { get; set; }
    }
    public sealed class ListView : FrameworkElement
    {
        public ListViewSelectionMode SelectionMode { get; set; }
        public bool IsItemClickEnabled { get; set; }
        public bool CanReorderItems { get; set; }
        public bool CanDragItems { get; set; }
        public bool AllowDrop { get; set; }
        public HorizontalAlignment HorizontalContentAlignment { get; set; }
        public List<object> Items { get; } = new();
    }
    public sealed class Expander : ContentControl { public ExpandDirection ExpandDirection { get; set; } public object? Header { get; set; } public bool IsExpanded { get; set; } }
    public sealed class TextBlock : FrameworkElement
    {
        public string Text { get; set; } = "";
        public TextWrapping TextWrapping { get; set; }
        public TextTrimming TextTrimming { get; set; }
        public int MaxLines { get; set; }
    }
    public sealed class FontIcon : FrameworkElement { public string Glyph { get; set; } = ""; public double FontSize { get; set; } }
    public class Button : ContentControl
    {
        public Thickness Padding { get; set; }
        public bool IsEnabled { get; set; } = true;
        public event EventHandler<RoutedEventArgs>? Click;
        internal void RaiseClick() { if (IsEnabled) Click?.Invoke(this, new()); }
    }
    public static class ToolTipService { public static void SetToolTip(UIElement element, object tooltip) => element.ToolTip = tooltip; }
}
namespace Microsoft.UI.Xaml.Automation
{
    public static class AutomationProperties
    {
        public static void SetAutomationId(UIElement element, string id) => element.AutomationId = id;
        public static string GetAutomationId(UIElement element) => element.AutomationId;
        public static void SetName(UIElement element, string name) => element.AutomationName = name;
    }
}
