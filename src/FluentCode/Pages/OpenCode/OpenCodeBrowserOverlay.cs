using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;
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

namespace FluentCode.Pages.OpenCode;

/// <summary>One-row native browser chrome in a whole-content overlay.</summary>
public sealed class OpenCodeBrowserOverlay : Grid
{
    private const double OriginLabelMinWidth = 720, CommandLabelMinWidth = 1000;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly FlyoutPresenter _presenter;
    private readonly Grid _toolbar = new() { Padding = new(8, 0, 8, 0), BorderThickness = new(0, 0, 0, 1) };
    private readonly TextBox _address = new() { CornerRadius = new(16), MinWidth = 0, Height = 32, FontSize = 13, Padding = new(36, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly OpenCodeBrowserAddressLine _displayAddress = new() { Margin = new(11, 0, 0, 0) };
    private readonly FontIcon _originIcon = new() { FontFamily = new("Segoe Fluent Icons"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _originLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _originChip = new() { Orientation = Orientation.Horizontal, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _windowDivider = new() { Width = 1, Height = 20, Margin = new(6, 0, 6, 0) };
    private readonly ProgressBar _loadingLine = new() { IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Button _back, _forward, _refresh, _openExternal;
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
    private readonly Grid _body = new();
    private readonly Canvas _pointerLayer = new() { IsHitTestVisible = false };
    private readonly StackPanel _pointer = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly TextBlock _pointerName = new();
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _ripple = new() { Width = 28, Height = 28, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private OpenCodeBrowserWorkspace? _workspace;
    private OpenCodeBrowserPage? _page;
    private bool _editing;
    private bool _compactToolbar;
    private string? _faviconUri;
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
        _openExternal = Button("\uE8A7", "在外部浏览器打开", async () =>
        {
            if (_page?.Presentation == OpenCodeBrowserPresentation.ExternalWindow) await RunPageActionAsync(page => page.FocusAsync(page.Lifetime));
            else if (_page?.PreviewPath is null && Uri.TryCreate(_page?.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
        });
        _actions.Children.Add(_openExternal);
        var more = Button("\uE712", "更多", () => { }); more.Flyout = MoreMenu(); _actions.Children.Add(more);
        Theme(_windowDivider, Border.BackgroundProperty, "DividerStrokeColorDefaultBrush"); _actions.Children.Add(_windowDivider);
        var close = Button("\uE8BB", "关闭浏览器浮窗", () => _workspace?.HideOverlay());
        _actions.Children.Add(close);

        // Reading order: navigation | address | preview commands | page tools | more and close.
        Theme(_toolbar, Grid.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) _toolbar.ColumnDefinitions.Add(new() { Width = width });
        // Native button hover is nearly invisible on this surface in the light theme, and disabled buttons would keep a faint box.
        Services.Appearance.ThemeResources.Aliases(_toolbar,
            ("ButtonBackgroundPointerOver", "SubtleFillColorSecondaryBrush"),
            ("ButtonBackgroundPressed", "SubtleFillColorTertiaryBrush"),
            ("ButtonBackgroundDisabled", "SubtleFillColorTransparentBrush"));
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) };
        _back = Button("\uE72B", "后退", () => _page?.GoBack()); _forward = Button("\uE72A", "前进", () => _page?.GoForward());
        _refresh = Button("\uE72C", "刷新", () => { if (_page?.PreviewPath is not null) _ = _preview.ReadAsync(); else if (_page?.Loading == true) _page.Stop(); else _page?.Reload(); });
        nav.Children.Add(_back); nav.Children.Add(_forward); nav.Children.Add(_refresh); _toolbar.Children.Add(nav);
        var addressHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) };
        // The address text is painted over the empty TextBox until it gets focus; Narrator reads it from the field's help text instead.
        var addressDisplay = new Grid { IsHitTestVisible = false, Margin = new(12, 0, 14, 0) };
        AutomationProperties.SetAccessibilityView(addressDisplay, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) addressDisplay.ColumnDefinitions.Add(new() { Width = width });
        OpenCodeToolTheme.Apply(_originIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary)); addressDisplay.Children.Add(_originIcon);
        OpenCodeToolTheme.Apply(_originLabel, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary)); _originChip.Children.Add(_originLabel);
        var originDivider = new Border { Width = 1, Height = 16, Margin = new(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Theme(originDivider, Border.BackgroundProperty, "DividerStrokeColorDefaultBrush"); _originChip.Children.Add(originDivider);
        Grid.SetColumn(_originChip, 1); addressDisplay.Children.Add(_originChip);
        Grid.SetColumn(_displayAddress, 2); addressDisplay.Children.Add(_displayAddress);
        addressHost.Children.Add(_address); addressHost.Children.Add(addressDisplay);
        Grid.SetColumn(addressHost, 1); _toolbar.Children.Add(addressHost);
        AutomationProperties.SetName(_address, "页面地址");
        // Quiet pill at rest, solid field with an accent ring while editing. High contrast keeps the native field brushes.
        Services.Appearance.ThemeResources.Aliases(_address,
            ("TextControlBackground", "SubtleFillColorSecondaryBrush"),
            ("TextControlBorderBrush", "SubtleFillColorTransparentBrush"),
            ("TextControlBackgroundPointerOver", "ControlAltFillColorQuarternaryBrush"),
            ("TextControlBorderBrushPointerOver", "ControlStrokeColorDefaultBrush"),
            ("TextControlBackgroundFocused", "ControlFillColorInputActiveBrush"),
            ("TextControlBorderBrushFocused", "AccentFillColorDefaultBrush"));
        _address.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(2);
        _address.GotFocus += (_, _) =>
        {
            _editing = true; _displayAddress.Visibility = Visibility.Collapsed;
            _address.Text = _page?.PreviewPath ?? _page?.Url ?? "";
            if (_address.Text == "about:blank") _address.Text = "";
            _address.SelectAll(); UpdateToolbarLayout();
        };
        _address.LostFocus += (_, _) => { _editing = false; Render(); };
        _address.KeyDown += (_, key) =>
        {
            if (key.Key != VirtualKey.Enter || _page is null || _page.PreviewPath is not null) return;
            try { _page.Navigate(OpenCodeBrowserPolicy.NormalizeUrl(_address.Text)); key.Handled = true; if (_page.EmbeddedView is Control view) view.Focus(FocusState.Programmatic); }
            catch (OpenCodeBrowserException ex) { ToolTipService.SetToolTip(_address, ex.Message); }
        };
        _preview.Commands.Margin = new(0, 0, 4, 0); _preview.Commands.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_preview.Commands, 2); _toolbar.Children.Add(_preview.Commands);
        // The selection owner adds exactly the element and region tools here.
        PageTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        ApplyToolToggleStyle(PageTools);
        Grid.SetColumn(PageTools, 3); _toolbar.Children.Add(PageTools);
        _actions.Margin = new(2, 0, 0, 0);
        Grid.SetColumn(_actions, 4); _toolbar.Children.Add(_actions); layout.Children.Add(_toolbar);
        AutomationProperties.SetAccessibilityView(_loadingLine, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        layout.Children.Add(_loadingLine);
        _toolbar.SizeChanged += (_, _) => UpdateToolbarLayout();
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
        Button? copyError = null;
        copyError = TextButton("复制错误", () => Copy(ErrorText(), copyError), "\uE8C8");
        errorActions.Children.Add(copyError);
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
        Theme(_ripple, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "AccentFillColorDefaultBrush"); _body.Children.Add(_pointerLayer);
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
        // 外层卡片容器只承载“从原位置展开”的合成层动画；presenter 的 Translation(Z=128) 留给阴影，二者不混用。
        var card = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(_presenter);
        Children.Add(card);
        Motion = new FluentCode.Motion.OverlayCardMotion(smoke, card);
        AutomationProperties.SetAutomationId(this, "OpenCodeBrowserOverlay");
        KeyDown += (_, key) => { if (key.Key == VirtualKey.Escape) { if (ExitPageTool?.Invoke() != true) _workspace?.HideOverlay(); key.Handled = true; } };
        _body.SizeChanged += (_, _) => { _agentPill.MaxWidth = Math.Max(0, Math.Min(900, _body.ActualWidth - 28)); ContentSizeChanged?.Invoke(this, EventArgs.Empty); };
        // Keep background and border in one ThemeResource style; no application-theme brush snapshot.
        _agentPill.Style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Border\"><Setter Property=\"Background\" Value=\"{ThemeResource AcrylicInAppFillColorDefaultBrush}\"/><Setter Property=\"BorderBrush\" Value=\"{ThemeResource FlyoutBorderThemeBrush}\"/></Style>");
    }

    public Grid RightPanelSlot { get; }

    internal FluentCode.Motion.OverlayCardMotion Motion { get; }
    internal void SetSurfaceSize(double width, double height) { _presenter.Width = width; _presenter.Height = height; }
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
        var preview = page.PreviewPath is not null;
        var external = !preview && page.Presentation == OpenCodeBrowserPresentation.ExternalWindow;
        _external.PickElementAsync = PickExternalElementAsync;
        _external.Bind(external ? page : null);
        var favicon = (page.EmbeddedView as WebView2)?.CoreWebView2?.FaviconUri;
        var hasFavicon = !preview && page.LoadError is null && Uri.TryCreate(favicon, UriKind.Absolute, out _);
        var shownFavicon = hasFavicon ? favicon : null;
        if (shownFavicon != _faviconUri)
        {
            _faviconUri = shownFavicon;
            _displayAddress.Favicon.Source = shownFavicon is null ? null : new BitmapImage(new Uri(shownFavicon));
        }
        _displayAddress.Favicon.Visibility = hasFavicon ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_openExternal, external ? "在 Chrome 中显示" : "在外部浏览器打开");
        ToolTipService.SetToolTip(_openExternal, external ? "在 Chrome 中显示（独立窗口）" : "在外部浏览器打开");
        var address = OpenCodeBrowserChromePresentation.Describe(page.Url, page.Title, page.PreviewPath, page.LoadError, FluentCode.Services.Configuration.AppServices.Settings.Current?.OpenCode.Kind);
        _originIcon.Glyph = address.Glyph; _originLabel.Text = address.OriginLabel ?? "";
        _preview.UpdateActions();
        var refreshName = preview ? "重新读取服务器文件" : page.Loading ? "停止加载" : "刷新";
        ToolTipService.SetToolTip(_refresh, refreshName); AutomationProperties.SetName(_refresh, refreshName);
        _back.IsEnabled = page.CanGoBack; _forward.IsEnabled = page.CanGoForward;
        (_refresh.Content as FontIcon)!.Glyph = !preview && page.Loading ? "\uE711" : "\uE72C";
        _loadingLine.Visibility = !preview && page.Loading && FluentCode.Motion.FluentMotion.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _address.IsReadOnly = page.PreviewPath is not null;
        _address.PlaceholderText = address.IsBlank ? "输入网址" : "";
        if (!_editing)
        {
            _address.Text = "";
            _displayAddress.Show(address);
            ToolTipService.SetToolTip(_address, address.ToolTip);
            AutomationProperties.SetHelpText(_address, address.ToolTip);
        }
        UpdateToolbarLayout();
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
            var activity = (Microsoft.UI.Xaml.Documents.Run)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<Run xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/>");
            activity.Text = " · " + page.AgentActivity;
            _activity.Inlines.Add(activity);
        }
        _agentIcon.Glyph = OpenCodeAgentIcons.Glyph(page.AgentName);
        ((TextBlock)((StackPanel)_takeover.Content).Children[1]).Text = page.UserTakeover ? "交还" : "接管";
        var active = page.AgentActivity is not null || page.UserTakeover;
        _agentPill.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) _pointer.Visibility = Visibility.Collapsed;
        if (active) { _outlineFade?.Stop(); _agentOutline.Opacity = 1; }
        else if (_wasAgentActive && FluentCode.Motion.FluentMotion.Enabled)
        {
            // Opacity 是独立动画（不触发布局）；时长/缓动走统一 token：167ms 退场加速。
            var animation = new DoubleAnimation { To = 0, Duration = new Duration(FluentCode.Motion.FluentMotion.Duration(FluentCode.Motion.FluentMotion.Fast)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(animation, _agentOutline); Storyboard.SetTargetProperty(animation, "Opacity");
            _outlineFade = new Storyboard(); _outlineFade.Children.Add(animation); _outlineFade.Begin();
        }
        else _agentOutline.Opacity = 0; // A running fade keeps animating toward the same 0.
        _wasAgentActive = active;
    }
    /// <summary>Hides what does not apply to the page, then what no longer fits. Anything hidden for width stays reachable from the more menu.</summary>
    private void UpdateToolbarLayout()
    {
        var preview = _page?.PreviewPath is not null;
        var width = _toolbar.ActualWidth;
        var compact = width > 0 && _toolbar.ActualWidth < 560;
        _compactToolbar = compact;
        _back.Visibility = _forward.Visibility = _openExternal.Visibility = compact || preview ? Visibility.Collapsed : Visibility.Visible;
        _windowDivider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _originChip.Visibility = _editing || width < OriginLabelMinWidth || _originLabel.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _displayAddress.ShowSide = !compact;
        _preview.Commands.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        _preview.SetCommandDensity(compact, width >= CommandLabelMinWidth);
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
        var previewEditor = Item("在编辑器中打开", () => _ = _preview.OpenAsync(true), "\uE70F");
        var previewExplorer = Item("在文件资源管理器中显示", () => _ = _preview.OpenAsync(false), "\uE838");
        var previewSeparator = new MenuFlyoutSeparator(); menu.Items.Add(previewSeparator);
        var back = Item("后退", () => _page?.GoBack(), "\uE72B"); var forward = Item("前进", () => _page?.GoForward(), "\uE72A");
        var navSeparator = new MenuFlyoutSeparator(); menu.Items.Add(navSeparator);
        var open = Item("在外部浏览器打开", () => _ = RunPageActionAsync(async page =>
        {
            if (page.Presentation == OpenCodeBrowserPresentation.ExternalWindow) await page.FocusAsync(page.Lifetime);
            else if (Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await Launcher.LaunchUriAsync(uri);
        }), "\uE8A7");
        var copyAddress = Item("复制链接", () => Copy(_page?.PreviewPath ?? _page?.Url ?? ""), "\uE71B");
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
        var pageSeparator = new MenuFlyoutSeparator(); menu.Items.Add(pageSeparator);
        Item("把此页面加到对话", () => { if (_page is not null) AddPageRequested?.Invoke(this, _page); }, "\uE710");
        var clearSite = Item("清除此站点的数据", async () =>
        {
            if (_page is not null && Uri.TryCreate(_page.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                await _page.CdpAsync("Storage.clearDataForOrigin", new() { ["origin"] = uri.GetLeftPart(UriPartial.Authority), ["storageTypes"] = "all" });
        }, "\uE74D");
        menu.Opening += (_, _) =>
        {
            var external = _page?.Presentation == OpenCodeBrowserPresentation.ExternalWindow;
            var preview = _page?.PreviewPath is not null;
            // A file preview has no history, site data or page zoom; only the commands that act on the file remain.
            back.Visibility = forward.Visibility = navSeparator.Visibility = open.Visibility = devtools.Visibility = zoom.Visibility = print.Visibility = pageSeparator.Visibility = clearSite.Visibility
                = preview ? Visibility.Collapsed : Visibility.Visible;
            previewEditor.Visibility = previewExplorer.Visibility = previewSeparator.Visibility = preview && _compactToolbar ? Visibility.Visible : Visibility.Collapsed;
            previewEditor.IsEnabled = _preview.CanOpen(true); previewExplorer.IsEnabled = _preview.CanOpen(false);
            copyAddress.Text = preview ? "复制路径" : "复制链接";
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
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = 16 }, Width = 36, Height = 32, MinWidth = 0, Padding = new(0), BorderThickness = new(0) };
        if (Application.Current.Resources.TryGetValue("OpenCodeTransparentToolButtonStyle", out var style)) button.Style = (Style)style;
        AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); button.Click += (_, _) => click(); return button;
    }
    /// <summary>Icon toggles in the toolbar share the size and checked look of its icon buttons.</summary>
    internal static void ApplyToolToggleStyle(FrameworkElement host)
    {
        host.Resources = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Style TargetType=\"ToggleButton\" BasedOn=\"{StaticResource DefaultToggleButtonStyle}\"><Setter Property=\"Background\" Value=\"{ThemeResource SubtleFillColorTransparentBrush}\"/><Setter Property=\"BorderThickness\" Value=\"0\"/><Setter Property=\"Width\" Value=\"36\"/><Setter Property=\"Height\" Value=\"32\"/><Setter Property=\"Padding\" Value=\"0\"/></Style></ResourceDictionary>");
        Services.Appearance.ThemeResources.Aliases(host,
            ("ToggleButtonBackgroundPointerOver", "SubtleFillColorSecondaryBrush"),
            ("ToggleButtonBackgroundPressed", "SubtleFillColorTertiaryBrush"),
            ("ToggleButtonBackgroundDisabled", "SubtleFillColorTransparentBrush"),
            ("ToggleButtonBackgroundChecked", "SubtleFillColorSecondaryBrush"),
            ("ToggleButtonBackgroundCheckedPointerOver", "SubtleFillColorSecondaryBrush"),
            ("ToggleButtonBackgroundCheckedPressed", "SubtleFillColorTertiaryBrush"),
            ("ToggleButtonForegroundChecked", "AccentTextFillColorPrimaryBrush"),
            ("ToggleButtonForegroundCheckedPointerOver", "AccentTextFillColorPrimaryBrush"),
            ("ToggleButtonForegroundCheckedPressed", "AccentTextFillColorPrimaryBrush"));
    }
    private static Button TextButton(string text, Action click, string glyph)
    {
        var button = new Button { Height = 32, FontSize = 13, Padding = new(14, 0, 14, 0), Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
            new FontIcon { Glyph = glyph, FontFamily = new("Segoe Fluent Icons"), FontSize = 12 }, new TextBlock { Text = text } } } };
        AutomationProperties.SetName(button, text); button.Click += (_, _) => click(); return button;
    }
    private void Copy(string text, Button? button = null)
    {
        if (button is null) { OpenCodeMessageContextMenu.Copy(text, this); return; }
        if (OpenCodeMessageContextMenu.Copy(text)) FluentCode.Motion.ActionFeedback.Success(button, "已复制页面错误");
        else FluentCode.Motion.ActionFeedback.Failure(button, "复制失败", "请重试");
    }
    private static void Theme(DependencyObject target, DependencyProperty property, string key)
    {
        var name = property == Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty ? "Stroke"
            : property == Border.BorderBrushProperty || property == Grid.BorderBrushProperty ? "BorderBrush" : "Background";
        if (target is FrameworkElement element) element.Style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + target.GetType().Name + "\"><Setter Property=\"" + name + "\" Value=\"{ThemeResource " + key + "}\"/></Style>");
    }
}
