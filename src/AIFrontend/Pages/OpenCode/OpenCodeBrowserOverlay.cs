using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>One-row native browser chrome in a whole-content overlay.</summary>
public sealed class OpenCodeBrowserOverlay : Grid
{
    private readonly FontIcon _siteIcon = new() { Glyph = "\uE774", FontFamily = new("Segoe Fluent Icons"), FontSize = 18 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly FlyoutPresenter _presenter;
    private readonly TextBox _address = new() { CornerRadius = new(16), MinWidth = 0, Height = 32, FontSize = 13, Padding = new(32, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _displayAddress = new() { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new(36, 0, 36, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _security = new() { FontFamily = new("Segoe Fluent Icons"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(12, 0, 0, 0), IsHitTestVisible = false };
    private readonly Image _favicon = new() { Width = 18, Height = 18, Visibility = Visibility.Collapsed };
    private readonly Border _network;
    private readonly Button _back, _forward, _refresh;
    private readonly Border _agentOutline = new() { BorderThickness = new(3), IsHitTestVisible = false, Opacity = 0 };
    private readonly Border _agentPill;
    private readonly TextBlock _activity = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _agentIcon = new() { FontFamily = new("Segoe Fluent Icons"), FontSize = 14 };
    private readonly Button _takeover;
    private readonly StackPanel _error;
    private readonly TextBlock _errorReason = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
    private readonly TextBlock _errorCode = new() { TextWrapping = TextWrapping.Wrap };
    private readonly OpenCodeBrowserPreviewView _preview = new();
    private readonly OpenCodeBrowserExternalView _external = new();
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
        layout.RowDefinitions.Add(new() { Height = new(OpenCodeBrowserOverlayLayout.ToolbarHeight) });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _actions.Children.Add(Button("\uE8A7", "在外部浏览器打开", async () =>
        {
            if (_page?.Presentation == OpenCodeBrowserPresentation.ExternalWindow) await RunPageActionAsync(page => page.FocusAsync(page.Lifetime));
            else if (_page?.PreviewPath is null && Uri.TryCreate(_page?.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
        }));
        var more = Button("\uE712", "更多", () => { }); more.Flyout = MoreMenu(); _actions.Children.Add(more);
        var divider = new Border { Width = 1, Height = 20, Margin = new(8, 0, 8, 0) };
        Theme(divider, Border.BackgroundProperty, "DividerStrokeColorDefaultBrush"); _actions.Children.Add(divider);
        var close = Button("\uE8BB", "关闭浏览器浮窗", () => _workspace?.HideOverlay());
        _actions.Children.Add(close);

        var toolbar = new Grid { Padding = new(8, 0, 8, 0), ColumnSpacing = 6, BorderThickness = new(0, 0, 0, 1) };
        Theme(toolbar, Grid.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var siteIcon = new Grid { Width = 24, VerticalAlignment = VerticalAlignment.Center };
        siteIcon.Children.Add(_siteIcon); siteIcon.Children.Add(_favicon); toolbar.Children.Add(siteIcon);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _back = Button("\uE72B", "后退", () => _page?.GoBack()); _forward = Button("\uE72A", "前进", () => _page?.GoForward());
        _refresh = Button("\uE72C", "刷新", () => { if (_page?.PreviewPath is not null) _ = _preview.ReadAsync(); else if (_page?.Loading == true) _page.Stop(); else _page?.Reload(); });
        nav.Children.Add(_back); nav.Children.Add(_forward); nav.Children.Add(_refresh); Grid.SetColumn(nav, 1); toolbar.Children.Add(nav);
        var addressHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        addressHost.Children.Add(_address); addressHost.Children.Add(_displayAddress); addressHost.Children.Add(_security);
        var networkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new FontIcon { Glyph = "\uE968", FontFamily = new("Segoe Fluent Icons"), FontSize = 10 }, new TextBlock { Text = "WSL 网络", FontSize = 11 } } };
        _network = new Border { Child = networkRow, Height = 22, Padding = new(8, 0, 8, 0), CornerRadius = new(11), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Theme(_network, Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); addressHost.Children.Add(_network);
        addressHost.Children.Add(_serverFile);
        Grid.SetColumn(addressHost, 2); toolbar.Children.Add(addressHost);
        AutomationProperties.SetName(_address, "页面地址");
        _address.GotFocus += (_, _) => { _editing = true; _displayAddress.Visibility = Visibility.Collapsed; _network.Visibility = Visibility.Collapsed; _address.Text = _page?.PreviewPath ?? _page?.Url ?? ""; _address.SelectAll(); };
        _address.LostFocus += (_, _) => { _editing = false; Render(); };
        _address.KeyDown += (_, key) =>
        {
            if (key.Key != VirtualKey.Enter || _page is null || _page.PreviewPath is not null) return;
            try { _page.Navigate(OpenCodeBrowserPolicy.NormalizeUrl(_address.Text)); key.Handled = true; if (_page.EmbeddedView is Control view) view.Focus(FocusState.Programmatic); }
            catch (OpenCodeBrowserException ex) { ToolTipService.SetToolTip(_address, ex.Message); }
        };
        // The selection owner adds exactly the element and region tools here.
        PageTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        PageTools.Resources = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Style TargetType=\"ToggleButton\" BasedOn=\"{StaticResource DefaultToggleButtonStyle}\"><Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorTransparentBrush}\"/><Setter Property=\"BorderThickness\" Value=\"0\"/><Setter Property=\"Width\" Value=\"34\"/><Setter Property=\"Height\" Value=\"32\"/><Setter Property=\"Padding\" Value=\"0\"/></Style><Style TargetType=\"SplitButton\"><Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorTransparentBrush}\"/><Setter Property=\"BorderThickness\" Value=\"0\"/><Setter Property=\"Height\" Value=\"32\"/></Style></ResourceDictionary>");
        var toolGroup = new Border { Child = PageTools, Padding = new(4, 2, 4, 2), CornerRadius = new(6), VerticalAlignment = VerticalAlignment.Center };
        Theme(toolGroup, Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        Grid.SetColumn(toolGroup, 3); toolbar.Children.Add(toolGroup);
        Grid.SetColumn(_actions, 4); toolbar.Children.Add(_actions); layout.Children.Add(toolbar);
        toolbar.SizeChanged += (_, _) =>
        {
            var compact = toolbar.ActualWidth < 560;
            _back.Visibility = _forward.Visibility = siteIcon.Visibility = divider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            _actions.Children[0].Visibility = compact || _page?.PreviewPath is not null ? Visibility.Collapsed : Visibility.Visible;
        };
        var content = new OpenCodeBrowserContentHost(_body);
        _body.Children.Add(shownHost);
        _preview.Visibility = Visibility.Collapsed; _body.Children.Add(_preview);
        _external.Visibility = Visibility.Collapsed; _body.Children.Add(_external);
        _error = new StackPanel { Spacing = 16, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(32) };
        var errorIcon = new FontIcon { Glyph = "\uEB55", FontFamily = new("Segoe Fluent Icons"), FontSize = 52, HorizontalAlignment = HorizontalAlignment.Left };
        OpenCodeToolTheme.Apply(errorIcon, OpenCodeToolTheme.Icon("TextFillColorTertiaryBrush")); _error.Children.Add(errorIcon);
        _error.Children.Add(new TextBlock { Text = "无法访问此页面", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }); _error.Children.Add(_errorReason);
        _errorCode.FontFamily = new("Cascadia Mono"); _errorCode.FontSize = 12;
        var codeSurface = new Border { Child = _errorCode, Padding = new(14, 10, 14, 10), CornerRadius = new(6) };
        Theme(codeSurface, Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); _error.Children.Add(codeSurface);
        var errorActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var retry = TextButton("重试", () => _page?.Reload(), "\uE72C"); retry.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle"); errorActions.Children.Add(retry);
        errorActions.Children.Add(TextButton("把错误发给智能体", () => { if (_page is not null) SendErrorRequested?.Invoke(this, _page); }, "\uE724"));
        errorActions.Children.Add(TextButton("复制错误", () => Copy(ErrorText()), "\uE8C8"));
        _error.Children.Add(errorActions);
        var errorSurface = new Grid(); Theme(errorSurface, Panel.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush"); errorSurface.Children.Add(_error);
        ErrorSurface = errorSurface; _body.Children.Add(errorSurface);
        Theme(_agentOutline, Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        var glow = new Grid { IsHitTestVisible = false };
        foreach (var (thickness, opacity) in new[] { (12d, .04), (8d, .08), (5d, .12) })
        {
            var edge = new Border { BorderThickness = new(thickness), Opacity = opacity };
            Theme(edge, Border.BorderBrushProperty, "AccentFillColorDefaultBrush"); glow.Children.Add(edge);
        }
        _agentOutline.Child = glow; _body.Children.Add(_agentOutline);
        var pointerIcon = new FontIcon { Glyph = "\uE8B0", FontFamily = new("Segoe Fluent Icons"), FontSize = 18, VerticalAlignment = VerticalAlignment.Top }; OpenCodeToolTheme.Apply(pointerIcon, OpenCodeToolTheme.Icon("AccentFillColorDefaultBrush"));
        _pointerName.FontSize = 11;
        OpenCodeToolTheme.Apply(_pointerName, OpenCodeToolTheme.Text("TextOnAccentFillColorPrimaryBrush"));
        var pointerLabel = new Border { Child = _pointerName, Padding = new(8, 1, 8, 1), CornerRadius = new(10), Margin = new(-4, 18, 0, 0) };
        Theme(pointerLabel, Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        _pointer.Children.Add(pointerIcon); _pointer.Children.Add(pointerLabel); _pointerLayer.Children.Add(_pointer); _pointerLayer.Children.Add(_ripple);
        _ripple.Stroke = Brush("AccentFillColorDefaultBrush"); _body.Children.Add(_pointerLayer);
        var pillContent = new Grid { ColumnSpacing = 10 };
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) pillContent.ColumnDefinitions.Add(new() { Width = width });
        var liveDot = new Border { Width = 6, Height = 6, CornerRadius = new(3), VerticalAlignment = VerticalAlignment.Center };
        Theme(liveDot, Border.BackgroundProperty, "AccentFillColorDefaultBrush"); pillContent.Children.Add(liveDot);
        OpenCodeToolTheme.Apply(_agentIcon, OpenCodeToolTheme.Icon("AccentFillColorDefaultBrush")); Grid.SetColumn(_agentIcon, 1); pillContent.Children.Add(_agentIcon);
        Grid.SetColumn(_activity, 2); pillContent.Children.Add(_activity);
        _takeover = TextButton("接管", () => { if (_page is not null) _page.UserTakeover = !_page.UserTakeover; }, "\uE8A7");
        _takeover.CornerRadius = new(16); Grid.SetColumn(_takeover, 3); pillContent.Children.Add(_takeover);
        var stop = TextButton("停止", () => { if (_page is not null) StopRequested?.Invoke(this, _page); }, "\uE71A");
        stop.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle"); stop.CornerRadius = new(16); Grid.SetColumn(stop, 4); pillContent.Children.Add(stop);
        _agentPill = new Border { Child = pillContent, Height = 44, Padding = new(14, 0, 6, 0), BorderThickness = new(1), CornerRadius = new(22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(14), MaxWidth = 900,
            Shadow = new ThemeShadow(), Translation = new Vector3(0, 0, 32) };
        Theme(_agentPill, Border.BackgroundProperty, "AcrylicInAppFillColorDefaultBrush"); _body.Children.Add(_agentPill);
        _agentPill.BorderBrush = Brush("FlyoutBorderThemeBrush");
        RightPanelSlot = content.AnnotationSlot;
        Grid.SetRow(content, 1); layout.Children.Add(content);
        _presenter = OpenCodeToolDetailPopup.CreatePresenter(layout, new(0), 128);
        _presenter.HorizontalAlignment = HorizontalAlignment.Center; _presenter.VerticalAlignment = VerticalAlignment.Center;
        _presenter.HorizontalContentAlignment = HorizontalAlignment.Stretch; _presenter.VerticalContentAlignment = VerticalAlignment.Stretch;
        // FlyoutPresenter's native template contains a ScrollViewer whose defaults are Left/Top.
        // Stretch that viewport too, otherwise content can keep its natural width inside the surface.
        _presenter.Resources = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Style TargetType=\"ScrollViewer\" BasedOn=\"{StaticResource DefaultScrollViewerStyle}\"><Setter Property=\"HorizontalContentAlignment\" Value=\"Stretch\"/><Setter Property=\"VerticalContentAlignment\" Value=\"Stretch\"/></Style></ResourceDictionary>");
        _presenter.CornerRadius = new(8); _presenter.BorderThickness = new(OpenCodeBrowserOverlayLayout.BorderWidth);
        Theme(_presenter, Control.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        Children.Add(_presenter);
        AutomationProperties.SetAutomationId(this, "OpenCodeBrowserOverlay");
        KeyDown += (_, key) => { if (key.Key == VirtualKey.Escape) { if (ExitPageTool?.Invoke() != true) _workspace?.HideOverlay(); key.Handled = true; } };
        _body.SizeChanged += (_, _) => { _agentPill.MaxWidth = Math.Max(0, Math.Min(900, _body.ActualWidth - 28)); ContentSizeChanged?.Invoke(this, EventArgs.Empty); };
        PageTools.Loaded += (_, _) => RefreshTheme(); ActualThemeChanged += (_, _) => RefreshTheme();
    }

    public Grid RightPanelSlot { get; }
    internal void SetSurfaceSize(double width, double height) { _presenter.Width = width; _presenter.Height = height; }
    private void RefreshTheme()
    {
        PageTools.Resources["ToggleButtonBackgroundChecked"] = Brush("SubtleFillColorSecondaryBrush");
        PageTools.Resources["ToggleButtonBackgroundCheckedPointerOver"] = Brush("SubtleFillColorSecondaryBrush");
        PageTools.Resources["ToggleButtonForegroundChecked"] = Brush("AccentTextFillColorPrimaryBrush");
        PageTools.Resources["ToggleButtonForegroundCheckedPointerOver"] = Brush("AccentTextFillColorPrimaryBrush");
        _agentPill.BorderBrush = Brush("FlyoutBorderThemeBrush");
    }
    public Grid PageToolSurface => _body;
    public StackPanel PageTools { get; }
    public Grid ErrorSurface { get; }
    public OpenCodeBrowserPage? Page => _page;
    public double ContentWidth => _body.ActualWidth;
    public double ContentHeight => _body.ActualHeight;
    public Func<bool>? ExitPageTool { get; set; }
    public Func<Task>? PickExternalElementAsync { get; set; }
    public event EventHandler? SettingsRequested;
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
        var page = _page; if (page is null) { _external.Bind(null); return; }
        Uri.TryCreate(page.Url, UriKind.Absolute, out var uri);
        var preview = page.PreviewPath is not null;
        _siteIcon.Glyph = preview ? "\uE8A5" : "\uE774";
        var external = !preview && page.Presentation == OpenCodeBrowserPresentation.ExternalWindow;
        _external.PickElementAsync = PickExternalElementAsync;
        _external.Bind(external ? page : null);
        var favicon = (page.EmbeddedView as WebView2)?.CoreWebView2?.FaviconUri;
        var hasFavicon = !preview && page.LoadError is null && Uri.TryCreate(favicon, UriKind.Absolute, out _);
        _favicon.Source = hasFavicon ? new BitmapImage(new Uri(favicon!)) : null;
        _favicon.Visibility = hasFavicon ? Visibility.Visible : Visibility.Collapsed;
        _siteIcon.Visibility = hasFavicon ? Visibility.Collapsed : Visibility.Visible;
        _actions.Children[0].Visibility = preview || _presenter.ActualWidth is > 0 and < 560 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_actions.Children[0], external ? "在 Chrome 中显示" : "在外部浏览器打开");
        ToolTipService.SetToolTip(_actions.Children[0], external ? "在 Chrome 中显示（独立窗口）" : "在外部浏览器打开");
        _serverFile.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        var local = !preview && uri is { IsLoopback: true };
        _network.Visibility = local && !_editing ? Visibility.Visible : Visibility.Collapsed;
        _displayAddress.Margin = new(32, 0, preview ? 110 : local ? 100 : 12, 0);
        _preview.UpdateActions();
        ToolTipService.SetToolTip(_refresh, preview ? "重新读取服务器文件" : page.Loading ? "停止加载" : "刷新");
        _back.IsEnabled = page.CanGoBack; _forward.IsEnabled = page.CanGoForward;
        (_refresh.Content as FontIcon)!.Glyph = !preview && page.Loading ? "\uE711" : "\uE72C";
        _address.IsReadOnly = page.PreviewPath is not null;
        if (!_editing)
        {
            _address.Text = ""; _security.Visibility = Visibility.Visible; _security.Glyph = uri?.Scheme == "https" ? "\uE72E" : "\uE946";
            _displayAddress.Inlines.Clear();
            if (page.PreviewPath is not null) { _displayAddress.Text = page.PreviewPath; _security.Glyph = "\uE8A5"; }
            else _displayAddress.Text = page.LoadError is not null ? "无法访问此页面" : string.IsNullOrWhiteSpace(page.Title) ? page.Url : page.Title;
            ToolTipService.SetToolTip(_address, page.PreviewPath ?? page.Url);
            _displayAddress.Visibility = Visibility.Visible;
        }
        ErrorSurface.Visibility = page.LoadError is null || page.PreviewPath is not null ? Visibility.Collapsed : Visibility.Visible;
        _errorReason.Text = page.LoadError is "CannotConnect" or "HostNameNotResolved" or "Timeout" ? ErrorReason(page.LoadError, page.Url) : page.LoadError ?? "";
        _errorCode.Text = (page.LoadError ?? "") + " · GET " + page.Url;
        _preview.Visibility = page.PreviewPath is null ? Visibility.Collapsed : Visibility.Visible;
        _activity.Inlines.Clear();
        if (page.UserTakeover) _activity.Text = "用户已接管此页面";
        else
        {
            _activity.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = page.AgentName + " " });
            _activity.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "正在操作此页面", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            _activity.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " · " + page.AgentActivity, Foreground = Brush("TextFillColorSecondaryBrush") });
        }
        _agentIcon.Glyph = OpenCodeAgentIcons.Glyph(page.AgentName);
        ((TextBlock)((StackPanel)_takeover.Content).Children[1]).Text = page.UserTakeover ? "交还" : "接管";
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
        if (_page?.Presentation == OpenCodeBrowserPresentation.ExternalWindow) return;
        _pointerName.Text = _page?.AgentName ?? "智能体"; _pointer.Visibility = Visibility.Visible;
        Canvas.SetLeft(_pointer, pointer.X); Canvas.SetTop(_pointer, pointer.Y);
        if (!pointer.Click) return;
        Canvas.SetLeft(_ripple, pointer.X - 14); Canvas.SetTop(_ripple, pointer.Y - 14); _ripple.Visibility = Visibility.Visible;
        await Task.Delay(250); _ripple.Visibility = Visibility.Collapsed;
    }
    public static string ErrorReason(string? code, string? url = null) => code switch
    {
        "CannotConnect" when Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback => $"{uri.Authority} 拒绝了连接。服务器（WSL）上没有程序在监听 {uri.Port} 端口，请确认开发服务器已启动。",
        "CannotConnect" or "ConnectionAborted" or "ConnectionReset" => "无法连接到服务器。请确认服务已启动，并检查网址和端口。",
        "HostNameNotResolved" => "找不到这个网站，请检查域名和网络连接。",
        "Timeout" => "连接超时，请稍后重试。",
        "CertificateCommonNameIsIncorrect" or "CertificateExpired" or "CertificateIsInvalid" => "此网站的安全证书无效。",
        _ => "此页面未能加载，请检查网络或重试。",
    };
    private string ErrorText() => (_page?.Url ?? "") + "\n" + ErrorReason(_page?.LoadError, _page?.Url) + "\n" + _page?.LoadError;
    private MenuFlyout MoreMenu()
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action action, string glyph) { var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } }; item.Click += (_, _) => action(); menu.Items.Add(item); return item; }
        var back = Item("后退", () => _page?.GoBack(), "\uE72B"); var forward = Item("前进", () => _page?.GoForward(), "\uE72A");
        menu.Items.Add(new MenuFlyoutSeparator());
        var open = Item("在外部浏览器打开", () => _ = RunPageActionAsync(async page =>
        {
            if (page.Presentation == OpenCodeBrowserPresentation.ExternalWindow) await page.FocusAsync(page.Lifetime);
            else if (Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
        }), "\uE8A7");
        Item("复制链接", () => Copy(_page?.Url ?? ""), "\uE71B");
        var devtools = Item("开发者工具", () => _ = RunPageActionAsync(async page =>
        {
            if (page.EmbeddedView is WebView2 view) view.CoreWebView2?.OpenDevToolsWindow();
            else { await page.FocusAsync(page.Lifetime); ToolTipService.SetToolTip(_address, "请在 Chrome 窗口按 F12 打开开发者工具。"); }
        }), "\uEC7A");
        var zoom = new MenuFlyoutSubItem { Text = "缩放", Icon = new FontIcon { Glyph = "\uE71E" } };
        foreach (var factor in new[] { .5, .75, 1, 1.25, 1.5, 2.0 }) { var item = new MenuFlyoutItem { Text = factor.ToString("P0") }; item.Click += async (_, _) => { if (_page is not null) await _page.CdpAsync("Emulation.setPageScaleFactor", new() { ["pageScaleFactor"] = factor }); }; zoom.Items.Add(item); }
        menu.Items.Add(zoom);
        var print = Item("打印", () => _ = RunPageActionAsync(async page =>
        {
            if (page.EmbeddedView is WebView2 view) view.CoreWebView2?.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.Browser);
            else { await page.FocusAsync(page.Lifetime); await page.ExecuteScriptAsync("window.print()"); }
        }), "\uE749");
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("把此页面加到对话", () => { if (_page is not null) AddPageRequested?.Invoke(this, _page); }, "\uE710");
        Item("清除此站点的数据", async () =>
        {
            if (_page is not null && Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                await _page.CdpAsync("Storage.clearDataForOrigin", new() { ["origin"] = uri.GetLeftPart(UriPartial.Authority), ["storageTypes"] = "all" });
        }, "\uE74D");
        menu.Opening += (_, _) =>
        {
            var external = _page?.Presentation == OpenCodeBrowserPresentation.ExternalWindow;
            back.IsEnabled = _page?.CanGoBack == true; forward.IsEnabled = _page?.CanGoForward == true;
            open.Text = external ? "在 Chrome 中显示" : "在外部浏览器打开";
            devtools.Text = external ? "开发者工具 · 在 Chrome 中按 F12" : "开发者工具";
            print.Text = external ? "在 Chrome 中打印" : "打印";
            devtools.IsEnabled = print.IsEnabled = _page is { IsReady: true, PreviewPath: null };
        };
        menu.Items.Add(new MenuFlyoutSeparator());
        Item("浏览器设置", () => SettingsRequested?.Invoke(this, EventArgs.Empty), "\uE713"); return menu;
    }
    private async Task RunPageActionAsync(Func<OpenCodeBrowserPage, Task> action)
    {
        var page = _page; if (page is null || page.PreviewPath is not null) return;
        try { await page.EnsureReadyAsync(page.Lifetime); await action(page); }
        catch (Exception ex) { ToolTipService.SetToolTip(_address, ex.Message); }
    }
    private static Button Button(string glyph, string name, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = 14 }, Width = 34, Height = 32, MinWidth = 0, Padding = new(0), BorderThickness = new(0) };
        if (Application.Current.Resources.TryGetValue("OpenCodeTransparentToolButtonStyle", out var style)) button.Style = (Style)style;
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); button.Click += (_, _) => click(); return button;
    }
    private static Button TextButton(string text, Action click, string glyph)
    {
        var button = new Button { Height = 32, FontSize = 13, Padding = new(14, 0, 14, 0), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
            new FontIcon { Glyph = glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = 12 }, new TextBlock { Text = text } } } };
        AutomationProperties.SetName(button, text); button.Click += (_, _) => click(); return button;
    }
    private static void Copy(string text) { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); }
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static void Theme(DependencyObject target, DependencyProperty property, string key)
    {
        var name = property == Border.BorderBrushProperty || property == Grid.BorderBrushProperty ? "BorderBrush" : "Background";
        if (target is FrameworkElement element) element.Style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + target.GetType().Name + "\"><Setter Property=\"" + name + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
    }
}
