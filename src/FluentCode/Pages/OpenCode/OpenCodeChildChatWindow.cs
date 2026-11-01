using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

internal interface IOpenCodeChildChatSource
{
    void OpenChildChat();
}

/// <summary>A read-only instance of the real conversation renderer, without page/composer handlers.</summary>
internal sealed class OpenCodeChildChatWindow
{
    private readonly Popup _popup;
    private readonly Grid _overlay;
    private readonly FlyoutPresenter _surface;
    private readonly OpenCodeConversationView _timeline = new();
    private readonly TextBlock _status;
    private readonly Button _maximize;
    private readonly ScrollViewer _scroller;
    private readonly XamlRoot _root;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _cancellation;
    private readonly UIElement? _restoreFocus;
    private readonly OpenCodeChildChatActions _actions;
    private OpenCodeMarkdownImageScope? _imageScope;
    private bool _constrainingViewport;

    internal OpenCodeChildChatWindow(XamlRoot root, CancellationToken bindingToken, Func<bool> bindingCurrent, Func<Task> maximize,
        Action<OpenCodeConversationView>? initializeTimeline = null)
    {
        _root = root;
        _restoreFocus = FocusManager.GetFocusedElement(root) as UIElement;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(bindingToken);
        _token = _lifetime.Token;
        _status = OpenCodeInteractionUi.StyledText("正在加载子会话…", OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
        _status.TextWrapping = TextWrapping.Wrap;
        _maximize = ChromeButton("\uE740", "最大化并打开子会话", "OpenCodeChildChatMaximize");
        _maximize.IsEnabled = false;
        _actions = new OpenCodeChildChatActions(bindingCurrent, CloseCore, maximize);
        _maximize.Click += async (_, _) => await _actions.MaximizeAsync();
        var close = ChromeButton("\uE711", "关闭子会话预览", "OpenCodeChildChatClose");
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(_maximize);
        actions.Children.Add(close);
        var title = OpenCodeInteractionUi.StyledText("子会话 · 只读", OpenCodeInteractionUi.BodyStrongStyleKey, false);
        title.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(actions, 1);
        header.Children.Add(title);
        header.Children.Add(actions);
        _scroller = new ScrollViewer
        {
            Content = _timeline,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 0, 8, 0),
        };
        OpenCodeToolTheme.Apply(_scroller, OpenCodeToolTheme.ScrollSurface(OpenCodeToolTheme.SolidBase));
        // Keep group expansion/scrolling; suppress detail popups in this nested renderer.
        OpenCodeToolDetailPopup.SetSuppressDetails(_timeline, true);
        // Integration supplies explicit CHILD/session image context here; never inherit the parent's resolver.
        // Runs before the timeline is attached or any child messages are rendered.
        initializeTimeline?.Invoke(_timeline);
        _timeline.LayoutUpdated += (_, _) => ConstrainTimelineViewport();
        _scroller.SizeChanged += (_, _) => ConstrainTimelineViewport();
        var body = new Grid { RowSpacing = 12 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(header);
        Grid.SetRow(_status, 1);
        body.Children.Add(_status);
        Grid.SetRow(_scroller, 2);
        body.Children.Add(_scroller);
        _surface = new FlyoutPresenter
        {
            Content = body, Padding = new Thickness(20, 12, 12, 20),
            MinWidth = 0, MinHeight = 0, MaxWidth = double.PositiveInfinity, MaxHeight = double.PositiveInfinity,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 64),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_surface, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_surface, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(_surface, ScrollMode.Disabled);
        var smoke = new Border();
        OpenCodeToolTheme.Apply(smoke, OpenCodeToolTheme.Border("SmokeFillColorDefaultBrush"));
        smoke.Tapped += (_, _) => Close();
        _overlay = new Grid { TabFocusNavigation = KeyboardNavigationMode.Cycle };
        _overlay.Children.Add(smoke);
        _overlay.Children.Add(_surface);
        _overlay.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Close(); }
        };
        AutomationProperties.SetAutomationId(_surface, "OpenCodeChildChatWindow");
        AutomationProperties.SetName(_surface, "子会话，只读");
        _popup = new Popup { XamlRoot = root, Child = _overlay, IsLightDismissEnabled = false };
        root.Changed += OnRootChanged;
        Layout();
        _popup.IsOpen = true;
        close.Focus(FocusState.Programmatic);
        _cancellation = bindingToken.Register(() => _overlay.DispatcherQueue.TryEnqueue(Close));
    }

    internal CancellationToken Token => _token;
    internal bool IsOpen => !_actions.IsClosed && !Token.IsCancellationRequested;

    /// <summary>Bind after construction/before messages; callback gets the child root and its close-canceled token.</summary>
    internal void InitializeImages(Func<OpenCodeConversationView, CancellationToken, OpenCodeMarkdownImageScope> initialize)
    {
        if (!IsOpen) return;
        _imageScope?.Dispose();
        _imageScope = initialize(_timeline, Token);
    }

    internal void ShowMessages(OpenCodeSessionMessagesResponse response)
    {
        if (!IsOpen) return;
        _timeline.ApplyMessages(OpenCodeChildChatReadSession.OrderMessages(response.Data));
        _status.Text = response.Cursor?.Next is { Length: > 0 }
            ? "只读快照 · 最近 100 条消息 · 最大化可查看完整会话"
            : (_timeline.Children.Count == 0 ? "该子会话暂无消息" : "只读快照 · 最多最近 100 条消息 · 最大化可进入完整会话");
        _maximize.IsEnabled = true;
        _actions.MarkLoaded();
        _scroller.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsOpen) return;
            _scroller.UpdateLayout();
            _scroller.ChangeView(null, _scroller.ScrollableHeight, null, true);
        });
    }

    internal void ShowError(string message)
    {
        if (!IsOpen) return;
        _status.Text = message;
        _maximize.IsEnabled = false;
    }

    internal void Close() => _actions.Close();

    private void CloseCore()
    {
        _lifetime.Cancel();
        _imageScope?.Dispose();
        _imageScope = null;
        OpenCodeImageContext.Unbind(_timeline);
        _cancellation.Unregister();
        _lifetime.Dispose();
        _root.Changed -= OnRootChanged;
        _popup.IsOpen = false;
        _timeline.ClearMessages();
        _popup.Child = null;
        try
        {
            if (_restoreFocus?.XamlRoot is not null) _restoreFocus.Focus(FocusState.Programmatic);
        }
        catch (Exception) { /* The originating page may already have left the visual tree. */ }
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Layout();
    private void Layout()
    {
        _overlay.Width = _root.Size.Width;
        _overlay.Height = _root.Size.Height;
        var size = OpenCodeDetailPopupLayout.LargeView(_root.Size.Width, _root.Size.Height);
        _surface.Width = Math.Max(0, Math.Min(_root.Size.Width - 16, _root.Size.Width * 0.9));
        _surface.Height = size.Height;
        // FlyoutPresenter has its own ScrollViewer template. Give the body a finite measure before
        // descendants (notably horizontal tool rows and Markdown tables) can request infinite width.
        _scroller.Width = Math.Max(0, _surface.Width - _surface.Padding.Left - _surface.Padding.Right);
        ConstrainTimelineViewport();
    }

    private void ConstrainTimelineViewport()
    {
        if (_constrainingViewport) return;
        _constrainingViewport = true;
        try
        {
            var width = Math.Max(0, _scroller.Width - _scroller.Padding.Left - _scroller.Padding.Right - 16);
            if (_scroller.ViewportWidth > 0) width = Math.Min(width, _scroller.ViewportWidth);
            if (_timeline.Width != width) _timeline.Width = width;
            OpenCodeToolDetailContent.EnforceWrapping(_timeline, width);
            if (_scroller.HorizontalOffset > 0) _scroller.ChangeView(0, null, null, true);
        }
        finally { _constrainingViewport = false; }
    }

    private static Button ChromeButton(string glyph, string name, string id)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 }, MinWidth = 40, MinHeight = 40,
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, id);
        ToolTipService.SetToolTip(button, name);
        return button;
    }
}
