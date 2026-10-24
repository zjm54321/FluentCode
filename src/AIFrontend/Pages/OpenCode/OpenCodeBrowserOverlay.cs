using System;
using System.ComponentModel;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>BQ5 uses exactly the tool large-view presenter/chrome, bounded before the right dock.</summary>
public sealed class OpenCodeBrowserOverlay : Grid
{
    private readonly OpenCodeToolDetailPopup.Chrome _chrome = new(true);
    private readonly FlyoutPresenter _presenter;
    private readonly TextBox _address = new() { CornerRadius = new(16), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _displayAddress = new() { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new(36, 0, 36, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _security = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(12, 0, 0, 0) };
    private readonly Button _back, _forward, _refresh;
    private readonly Border _agentOutline = new() { BorderThickness = new(3), IsHitTestVisible = false, Opacity = 0 };
    private readonly Border _agentPill;
    private readonly TextBlock _activity = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _takeover;
    private readonly StackPanel _error;
    private readonly TextBlock _errorReason = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
    private readonly TextBlock _errorCode = new() { TextWrapping = TextWrapping.Wrap };
    private readonly OpenCodeBrowserPreviewView _preview = new();
    private readonly TextBlock _serverFile = new() { Text = "服务器文件", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 12, 0), Visibility = Visibility.Collapsed };
    private readonly Grid _body = new();
    private readonly Canvas _pointerLayer = new() { IsHitTestVisible = false };
    private readonly StackPanel _pointer = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pointerName = new();
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _ripple = new() { Width = 28, Height = 28, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private OpenCodeBrowserWorkspace? _workspace;
    private OpenCodeBrowserPage? _page;
    private bool _editing;
    private UIElement? _returnFocus;
    private Storyboard? _outlineFade;
    private bool _wasAgentActive;

    public OpenCodeBrowserOverlay(Grid shownHost)
    {
        var smoke = new Border(); Theme(smoke, Border.BackgroundProperty, "SmokeFillColorDefaultBrush");
        smoke.Tapped += (_, _) => _workspace?.HideOverlay(); Children.Add(smoke);
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = new(52) });
        layout.RowDefinitions.Add(new() { Height = new(46) });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _chrome.TitleBar.Height = 52; _chrome.TitleBar.Padding = new(16, 0, 12, 0);
        _chrome.Name.FontSize = 16; _chrome.Name.MaxWidth = 420; _chrome.Name.TextWrapping = TextWrapping.NoWrap;
        _chrome.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        _chrome.Icon.Glyph = "\uE774";
        _chrome.StatusCapsule.Visibility = Visibility.Collapsed;
        layout.Children.Add(_chrome.TitleBar);
        _chrome.Actions.Children.Add(Button("\uE8A7", "在外部浏览器打开", async () =>
        {
            if (_page?.PreviewPath is null && Uri.TryCreate(_page?.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
        }));
        _chrome.Actions.Children.Add(Button("\uEC7A", "开发者工具", () => _page?.Core.OpenDevToolsWindow()));
        var more = Button("\uE712", "更多", () => { }); more.Flyout = MoreMenu(); _chrome.Actions.Children.Add(more);
        _chrome.Actions.Children.Add(Button("\uE8BB", "关闭浏览器浮窗", () => _workspace?.HideOverlay()));

        var toolbar = new Grid { Padding = new(12, 4, 12, 4), ColumnSpacing = 6 };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _back = Button("\uE72B", "后退", () => _page?.GoBack()); _forward = Button("\uE72A", "前进", () => _page?.GoForward());
        _refresh = Button("\uE72C", "刷新", () => { if (_page?.PreviewPath is not null) _ = _preview.ReadAsync(); else if (_page?.Loading == true) _page.Stop(); else _page?.Reload(); });
        nav.Children.Add(_back); nav.Children.Add(_forward); nav.Children.Add(_refresh); toolbar.Children.Add(nav);
        var addressHost = new Grid(); addressHost.Children.Add(_address); addressHost.Children.Add(_displayAddress); addressHost.Children.Add(_security);
        addressHost.Children.Add(_serverFile);
        Grid.SetColumn(addressHost, 1); toolbar.Children.Add(addressHost);
        AutomationProperties.SetName(_address, "页面地址");
        _address.GotFocus += (_, _) => { _editing = true; _displayAddress.Visibility = Visibility.Collapsed; _security.Visibility = Visibility.Collapsed; _address.Text = _page?.PreviewPath ?? _page?.Url ?? ""; _address.SelectAll(); };
        _address.LostFocus += (_, _) => { _editing = false; Render(); };
        _address.KeyDown += (_, key) =>
        {
            if (key.Key != VirtualKey.Enter || _page is null || _page.PreviewPath is not null) return;
            try { _page.Navigate(OpenCodeBrowserPolicy.NormalizeUrl(_address.Text)); key.Handled = true; _page.View.Focus(FocusState.Programmatic); }
            catch (OpenCodeBrowserException ex) { ToolTipService.SetToolTip(_address, ex.Message); }
        };
        // Selection lane replaces these disabled native tools and fills the right-side slot.
        PageTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var item in new[] { ("\uE8B0", "选择元素"), ("\uE7EF", "框选"), ("\uED5C", "圈选"), ("\uE70F", "标注"), ("\uE722", "截图") })
        {
            var toggle = new ToggleButton { Content = new FontIcon { Glyph = item.Item1, FontSize = 16 }, IsEnabled = false, Width = 32, MinWidth = 0, Padding = new(0) };
            ToolTipService.SetToolTip(toggle, item.Item2); AutomationProperties.SetName(toggle, item.Item2); PageTools.Children.Add(toggle);
        }
        Grid.SetColumn(PageTools, 2); toolbar.Children.Add(PageTools); Grid.SetRow(toolbar, 1); layout.Children.Add(toolbar);
        var content = new Grid(); content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _body.Children.Add(shownHost);
        _preview.Visibility = Visibility.Collapsed; _body.Children.Add(_preview);
        _error = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(32) };
        _error.Children.Add(new TextBlock { Text = "无法访问此页面", FontSize = 28 }); _error.Children.Add(_errorReason); _error.Children.Add(_errorCode);
        var errorActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        errorActions.Children.Add(TextButton("重试", () => _page?.Reload()));
        errorActions.Children.Add(TextButton("把错误发给智能体", () => { if (_page is not null) SendErrorRequested?.Invoke(this, _page); }));
        errorActions.Children.Add(TextButton("复制错误", () => Copy(ErrorText())));
        _error.Children.Add(errorActions); Theme(_error, Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        var errorSurface = new Grid(); Theme(errorSurface, Panel.BackgroundProperty, "SolidBackgroundFillColorBaseBrush"); errorSurface.Children.Add(_error);
        ErrorSurface = errorSurface; _body.Children.Add(errorSurface);
        Theme(_agentOutline, Border.BorderBrushProperty, "AccentFillColorDefaultBrush"); _body.Children.Add(_agentOutline);
        var pointerIcon = new FontIcon { Glyph = "\uE8B0", FontSize = 18 }; OpenCodeToolTheme.Apply(pointerIcon, OpenCodeToolTheme.Icon("AccentFillColorDefaultBrush"));
        OpenCodeToolTheme.Apply(_pointerName, OpenCodeToolTheme.Text("AccentTextFillColorPrimaryBrush"));
        _pointer.Children.Add(pointerIcon); _pointer.Children.Add(_pointerName); _pointerLayer.Children.Add(_pointer); _pointerLayer.Children.Add(_ripple);
        _ripple.Stroke = Brush("AccentFillColorDefaultBrush"); _body.Children.Add(_pointerLayer);
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pillContent.Children.Add(_activity); _takeover = TextButton("接管", () => { if (_page is not null) _page.UserTakeover = !_page.UserTakeover; });
        pillContent.Children.Add(_takeover); pillContent.Children.Add(TextButton("停止", () => { if (_page is not null) StopRequested?.Invoke(this, _page); }));
        _agentPill = new Border { Child = pillContent, Padding = new(12, 4, 4, 4), CornerRadius = new(20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(8), MaxWidth = 900 };
        Theme(_agentPill, Border.BackgroundProperty, "AcrylicInAppFillColorDefaultBrush"); _body.Children.Add(_agentPill);
        content.Children.Add(_body); RightPanelSlot = new Grid(); Grid.SetColumn(RightPanelSlot, 1); content.Children.Add(RightPanelSlot);
        Grid.SetRow(content, 2); layout.Children.Add(content);
        _presenter = OpenCodeToolDetailPopup.CreatePresenter(layout, new(0), 128);
        _presenter.Margin = new(16); _presenter.CornerRadius = new(8);
        Children.Add(_presenter);
        AutomationProperties.SetAutomationId(this, "OpenCodeBrowserOverlay");
        KeyDown += (_, key) => { if (key.Key == VirtualKey.Escape) { if (ExitPageTool?.Invoke() != true) _workspace?.HideOverlay(); key.Handled = true; } };
        _body.SizeChanged += (_, _) => ContentSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public Grid RightPanelSlot { get; }
    public Grid PageToolSurface => _body;
    public StackPanel PageTools { get; }
    public Grid ErrorSurface { get; }
    public OpenCodeBrowserPage? Page => _page;
    public double ContentWidth => _body.ActualWidth;
    public double ContentHeight => _body.ActualHeight;
    public Func<bool>? ExitPageTool { get; set; }
    public event EventHandler? ContentSizeChanged;
    public event EventHandler<OpenCodeBrowserPage>? StopRequested;
    public event EventHandler<OpenCodeBrowserPage>? AddPageRequested;
    public event EventHandler<OpenCodeBrowserPage>? SendErrorRequested;
    public event EventHandler<OpenCodeBrowserPage>? PageChanged;
    public event EventHandler? SelectionBindingChanged;
    public void Bind(OpenCodeBrowserWorkspace? workspace)
    {
        if (_page is not null) { _page.PropertyChanged -= PagePropertyChanged; _page.AgentPointerMoved -= AgentPointerMoved; }
        var wasShown = _page is not null;
        _workspace = workspace; _page = workspace?.Shown as OpenCodeBrowserPage;
        _preview.Bind(workspace, _page);
        if (_page is not null) { _page.PropertyChanged += PagePropertyChanged; _page.AgentPointerMoved += AgentPointerMoved; }
        _pointer.Visibility = Visibility.Collapsed;
        Render();
        if (_page is not null)
        {
            if (!wasShown) _returnFocus = FocusManager.GetFocusedElement(XamlRoot) as UIElement;
            if (_page.PreviewPath is null && _page.Url == "about:blank") _address.Focus(FocusState.Programmatic);
        }
        else { if (_returnFocus is Control control) control.Focus(FocusState.Programmatic); _returnFocus = null; }
        if (_page is not null) PageChanged?.Invoke(this, _page);
        SelectionBindingChanged?.Invoke(this, EventArgs.Empty);
    }
    private void PagePropertyChanged(object? sender, PropertyChangedEventArgs args) => Render();
    private void Render()
    {
        var page = _page; if (page is null) return;
        _chrome.Name.Text = page.Title; _chrome.Target.Text = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var preview = page.PreviewPath is not null;
        _chrome.Icon.Glyph = preview ? "\uE8A5" : "\uE774";
        _chrome.Target.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < 3; i++) _chrome.Actions.Children[i].Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        _serverFile.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        _displayAddress.Margin = new(36, 0, preview ? 110 : 36, 0);
        _preview.UpdateActions();
        if (PageTools.Children.Count == 5) { PageTools.Children[1].Visibility = preview ? Visibility.Collapsed : Visibility.Visible; PageTools.Children[2].Visibility = preview ? Visibility.Collapsed : Visibility.Visible; }
        ToolTipService.SetToolTip(_refresh, preview ? "重新读取服务器文件（仅手动刷新）" : "刷新");
        _back.IsEnabled = page.CanGoBack; _forward.IsEnabled = page.CanGoForward;
        (_refresh.Content as FontIcon)!.Glyph = !preview && page.Loading ? "\uE711" : "\uE72C";
        _address.IsReadOnly = page.PreviewPath is not null;
        if (!_editing)
        {
            _address.Text = ""; _security.Visibility = Visibility.Visible; _security.Glyph = uri?.Scheme == "https" ? "\uE72E" : "\uE946";
            _displayAddress.Inlines.Clear();
            if (page.PreviewPath is not null) { _displayAddress.Text = page.PreviewPath; _security.Glyph = "\uE8A5"; }
            else if (uri?.Scheme is "http" or "https")
            {
                _displayAddress.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = uri.Authority, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                _displayAddress.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = uri.PathAndQuery + uri.Fragment, Foreground = Brush("TextFillColorSecondaryBrush") });
            }
            else _displayAddress.Text = page.Url;
            _displayAddress.Visibility = Visibility.Visible;
        }
        ErrorSurface.Visibility = page.LoadError is null || page.PreviewPath is not null ? Visibility.Collapsed : Visibility.Visible;
        _errorReason.Text = ErrorReason(page.LoadError); _errorCode.Text = page.LoadError ?? "";
        _preview.Visibility = page.PreviewPath is null ? Visibility.Collapsed : Visibility.Visible;
        _activity.Text = page.UserTakeover ? "用户已接管此页面" : page.AgentName + " 正在操作此页面 · " + page.AgentActivity;
        _takeover.Content = page.UserTakeover ? "交还" : "接管";
        var active = page.AgentActivity is not null || page.UserTakeover;
        _agentPill.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) _pointer.Visibility = Visibility.Collapsed;
        if (active) { _outlineFade?.Stop(); _agentOutline.Opacity = 1; }
        else if (_wasAgentActive && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var animation = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
            Storyboard.SetTarget(animation, _agentOutline); Storyboard.SetTargetProperty(animation, "Opacity");
            _outlineFade = new Storyboard(); _outlineFade.Children.Add(animation); _outlineFade.Begin();
        }
        else if (!_wasAgentActive) _agentOutline.Opacity = 0;
        _wasAgentActive = active;
    }
    private async void AgentPointerMoved(object? sender, OpenCodeBrowserPointer pointer)
    {
        _pointerName.Text = _page?.AgentName ?? "智能体"; _pointer.Visibility = Visibility.Visible;
        Canvas.SetLeft(_pointer, pointer.X); Canvas.SetTop(_pointer, pointer.Y);
        if (!pointer.Click) return;
        Canvas.SetLeft(_ripple, pointer.X - 14); Canvas.SetTop(_ripple, pointer.Y - 14); _ripple.Visibility = Visibility.Visible;
        await Task.Delay(250); _ripple.Visibility = Visibility.Collapsed;
    }
    public static string ErrorReason(string? code) => code switch
    {
        "CannotConnect" or "ConnectionAborted" or "ConnectionReset" => "无法连接到服务器。请确认服务已启动，并检查网址和端口。",
        "HostNameNotResolved" => "找不到这个网站，请检查域名和网络连接。",
        "Timeout" => "连接超时，请稍后重试。",
        "CertificateCommonNameIsIncorrect" or "CertificateExpired" or "CertificateIsInvalid" => "此网站的安全证书无效。",
        _ => "此页面未能加载，请检查网络或重试。",
    };
    private string ErrorText() => (_page?.Url ?? "") + "\n" + ErrorReason(_page?.LoadError) + "\n" + _page?.LoadError;
    private MenuFlyout MoreMenu()
    {
        var menu = new MenuFlyout();
        void Item(string text, Action action) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("复制链接", () => Copy(_page?.Url ?? ""));
        var zoom = new MenuFlyoutSubItem { Text = "缩放" };
        foreach (var factor in new[] { .5, .75, 1, 1.25, 1.5, 2.0 }) { var item = new MenuFlyoutItem { Text = factor.ToString("P0") }; item.Click += async (_, _) => { if (_page is not null) await _page.CdpAsync("Emulation.setPageScaleFactor", new() { ["pageScaleFactor"] = factor }); }; zoom.Items.Add(item); }
        menu.Items.Add(zoom);
        Item("打印", () => _page?.Core.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser));
        Item("把此页面加到对话", () => { if (_page is not null) AddPageRequested?.Invoke(this, _page); });
        Item("清除此站点的数据", async () =>
        {
            if (_page is not null && Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                await _page.CdpAsync("Storage.clearDataForOrigin", new() { ["origin"] = uri.GetLeftPart(UriPartial.Authority), ["storageTypes"] = "all" });
        });
        var settings = new MenuFlyoutItem { Text = "浏览器设置", IsEnabled = false };
        ToolTipService.SetToolTip(settings, "浏览器设置尚未提供"); menu.Items.Add(settings); return menu;
    }
    private static Button Button(string glyph, string name, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, Width = 32, Height = 32, MinWidth = 0, Padding = new(0) };
        if (Application.Current.Resources.TryGetValue("OpenCodeTransparentToolButtonStyle", out var style)) button.Style = (Style)style;
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); button.Click += (_, _) => click(); return button;
    }
    private static Button TextButton(string text, Action click) { var button = new Button { Content = text }; button.Click += (_, _) => click(); return button; }
    private static void Copy(string text) { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static void Theme(DependencyObject target, DependencyProperty property, string key)
    {
        var name = property == Border.BorderBrushProperty ? "BorderBrush" : "Background";
        if (target is FrameworkElement element) element.Style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + target.GetType().Name + "\"><Setter Property=\"" + name + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
    }
}
