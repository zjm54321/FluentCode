using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Web.WebView2.Core;

namespace AIFrontend.Pages.Web;

/// <summary>
/// 单个官方站点的 WebView2 宿主控件（纯 C# 组合原生控件，无 XAML 依赖）。
/// </summary>
/// <remarks>
/// 生命周期与线程契约：
/// <list type="bullet">
/// <item>控件必须在 UI 线程创建。<see cref="CoreWebView2Environment.CreateWithOptionsAsync(string, string, CoreWebView2EnvironmentOptions)"/>
/// 与 <see cref="WebView2.EnsureCoreWebView2Async(CoreWebView2Environment)"/> 均具有 UI 线程亲和性，
/// 因此全部 <c>await</c> 都不使用 <c>ConfigureAwait(false)</c>，保证续体回到 UI 线程。</item>
/// <item>环境与 WebView2 实例按 provider 懒加载并复用：首次 <see cref="ActivateAsync"/> 时创建，
/// 之后仅复用；<see cref="Deactivate"/> 只隐藏不销毁，登录态（profile / cookie）得以保留。</item>
/// <item>已 <see cref="Close"/> 的实例不可复用；失败恢复由 <see cref="RebuildAsync"/> 重建实例。</item>
/// <item>视觉：内容外包一层圆角容器（<see cref="Border"/>），并对 WebView2 元素可视化与宿主自身
/// 元素可视化<strong>同时</strong>施加 composition 圆角裁剪（见 <see cref="ApplyRoundedClip"/>）——
/// 裁剪须覆盖外宿主层，网页圆角外、宿主子树内的任何矩形绘制一并被裁圆；不依赖
/// <c>Border</c>/<c>Control.CornerRadius</c> 对网页内容的裁剪保证，不更换 WebView2 实现，
/// 不改变环境 / cookie / 安全设置与全部生命周期防护。</item>
/// <item>主题：随 <see cref="FrameworkElement.ActualTheme"/> 同步 WebView 配色偏好
/// （<see cref="CoreWebView2Profile.PreferredColorScheme"/>，Light/Dark 映射）与控件默认底色
/// （<see cref="WebView2.DefaultBackgroundColor"/>，仅作用于网页未设背景/透明区域）；
/// 切换主题不 Reload、不清 cookie、不影响登录态（见 <see cref="ApplyThemeToWebView"/>）。</item>
/// </list>
/// </remarks>
public sealed class WebViewHost : UserControl
{
    private readonly WebProvider _provider;
    private readonly Uri _homeUri;
    private readonly string _userDataFolder;

    private const float RoundedCornerRadius = 8f;   // 与 WinUI 主题资源 OverlayCornerRadius(8,8,8,8) 一致

    private Border? _frame;
    private Grid? _root;
    private WebView2? _webView;
    private Grid? _overlay;
    private ProgressRing? _ring;
    private StackPanel? _errorPanel;
    private TextBlock? _errorText;

    private CoreWebView2Environment? _environment;
    private CompositionRoundedRectangleGeometry? _roundedGeometry;
    private CompositionGeometricClip? _roundedClip;
    private CompositionRoundedRectangleGeometry? _hostRoundedGeometry;
    private CompositionGeometricClip? _hostRoundedClip;
    private Task? _initTask;
    private bool _hasNavigated;
    private bool _firstNavigationCompleted;
    private bool _needsRebuild;
    private bool _closed;
    private bool _active;

    public WebViewHost(WebProvider provider)
    {
        _provider = provider;
        _homeUri = WebProviderInfo.GetHomeUri(provider);
        _userDataFolder = WebProviderInfo.GetUserDataFolder(provider);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Visibility = Visibility.Collapsed;

        BuildVisualTree();
    }

    /// <summary>该宿主对应的 provider。</summary>
    public WebProvider Provider => _provider;

    /// <summary>CoreWebView2 是否已就绪（只读查询，不触发初始化）。</summary>
    public bool IsCoreReady => !_closed && _webView?.CoreWebView2 is not null;

    /// <summary>
    /// 显示并激活：确保环境与实例就绪，首次激活时导航到官网首页。
    /// 幂等，可重复调用；异常已在内部转为错误态展示，不向外抛出。
    /// </summary>
    public async Task ActivateAsync()
    {
        if (_closed)
        {
            return;
        }

        _active = true;
        Visibility = Visibility.Visible;
        if (_webView is not null) _webView.Visibility = Visibility.Visible;

        try
        {
            await EnsureInitializedAsync();

            if (_closed || !_active || _webView?.CoreWebView2 is null)
            {
                return;
            }

            // 每次激活都按当前 ActualTheme 补写主题偏好（幂等；隐藏期间错过的主题变化在此收敛，
            // 保证隐藏 provider 下次进入不留旧主题）。只写偏好，不触发导航与重载。
            ApplyThemeToWebView();

            _webView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            _webView.CoreWebView2.Resume();
            if (!_hasNavigated)
            {
                _hasNavigated = true;
                _webView.CoreWebView2.Navigate(_homeUri.AbsoluteUri);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ShowError($"激活失败：{ex.Message}", rebuild: true);
            }
        }
    }

    /// <summary>
    /// 隐藏但不销毁：保留 WebView2 实例与环境，从而保留登录态。
    /// 再次 <see cref="ActivateAsync"/> 时不会重复导航。
    /// </summary>
    public void Deactivate()
    {
        if (_closed)
        {
            return;
        }

        _active = false;
        Visibility = Visibility.Collapsed;
        if (_webView is not null) _webView.Visibility = Visibility.Collapsed;
        _ = SuspendHiddenAsync();
    }

    private async Task SuspendHiddenAsync()
    {
        if (_closed || _active || _webView?.CoreWebView2 is not { } core) return;
        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            await core.TrySuspendAsync();
            // Restore can race the asynchronous suspend. Do not leave the newly shown tab suspended.
            if (!_closed && _active && ReferenceEquals(core, _webView?.CoreWebView2))
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                core.Resume();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { System.Diagnostics.Debug.WriteLine("WebView2 suspension unavailable: " + ex.GetType().Name); }
    }

    /// <summary>
    /// 清理该 provider 环境的全部 cookie（供未来设置 UI 调用）。
    /// </summary>
    /// <remarks>
    /// 契约：
    /// <list type="bullet">
    /// <item>只调用目标环境 <see cref="CoreWebView2CookieManager.DeleteAllCookies"/>；
    /// 不删除用户数据目录，也不清除缓存 / 本地存储等其他站点数据（不声称清除全部站点存储）。</item>
    /// <item>不导航：若尚未初始化，则在 UI 线程创建环境与实例后立即清理，不加载任何页面。</item>
    /// <item>优先复用现有活动实例；未初始化时由 UI 线程按需创建并保留该实例。</item>
    /// <item>必须在 UI 线程调用（WebView2 线程亲和）。初始化失败时抛 <see cref="InvalidOperationException"/>。</item>
    /// </list>
    /// </remarks>
    public async Task ClearCookiesAsync()
    {
        if (_closed)
        {
            throw new ObjectDisposedException(nameof(WebViewHost));
        }

        await EnsureInitializedAsync();

        if (_closed)
        {
            return;
        }

        if (_webView?.CoreWebView2 is null)
        {
            throw new InvalidOperationException("WebView2 未就绪（初始化失败），无法清理 cookie。");
        }

        _webView.CoreWebView2.CookieManager.DeleteAllCookies();
    }

    /// <summary>
    /// 仅重载当前网页：调用 <see cref="CoreWebView2.Reload"/>，不清除 cookie、
    /// 不重建环境、不影响其他 provider 的实例与登录态。
    /// </summary>
    /// <remarks>
    /// 必须在 UI 线程调用。以下情况为空操作（初始化 / 销毁 / 加载竞态安全）：
    /// <list type="bullet">
    /// <item>已 <see cref="Close"/>；</item>
    /// <item><c>CoreWebView2</c> 尚未就绪（初始化中）；</item>
    /// <item>首次导航尚未结束（页面正在加载，无需也不应重复触发，避免重载到空白文档）。</item>
    /// </list>
    /// 错误态的就地重试由叠层“重试”按钮负责（<see cref="RetryAsync"/>）；本方法不触发整实例重建。
    /// </remarks>
    public void Refresh()
    {
        if (_closed)
        {
            return;
        }

        if (_webView?.CoreWebView2 is { } core && _firstNavigationCompleted)
        {
            // 与错误态“重试”的就地重载路径一致：先进入加载态，由本次 NavigationCompleted 收尾。
            ShowLoading();
            core.Reload();
        }
    }

    /// <summary>关闭并释放 WebView2 实例与环境引用；关闭后不可再使用本实例。</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        TearDown();
    }

    private Task EnsureInitializedAsync()
    {
        _initTask ??= InitializeAsync();
        return _initTask;
    }

    private async Task InitializeAsync()
    {
        // 捕获本次初始化的目标控件：若期间发生重建（TearDown 会置空并重建字段）或关闭，
        // 续体据此放弃，绝不操作已被替换或释放的实例（避免事件重复订阅与叠层误操作）。
        WebView2? webView = _webView;
        if (_closed || webView is null)
        {
            return;
        }

        ShowLoading();

        try
        {
            // 先创建独立环境（固定目录），再交给控件初始化；
            // 绝不预设 Source，以免触发默认环境的隐式创建。
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: _userDataFolder,
                options: null);

            // 初始化期间可能已卸载/关闭：丢弃刚创建的环境引用，不再保留到字段上。
            if (_closed || !ReferenceEquals(webView, _webView))
            {
                return;
            }

            _environment = environment;

            await webView.EnsureCoreWebView2Async(environment);

            if (_closed || !ReferenceEquals(webView, _webView))
            {
                return;
            }

            CoreWebView2 core = webView.CoreWebView2;
            if (!_active) _ = SuspendHiddenAsync();
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;

            // 主题偏好须在 CoreWebView2 就绪后、首次 Navigate 之前写入（PreferredColorScheme 的
            // Auto 仅跟随 OS；应用显式主题按 ActualTheme 映射 Light/Dark，见 ApplyThemeToWebView）。
            ApplyThemeToWebView();

            // 刻意不在此隐藏叠层：加载态需持续到首次 NavigationCompleted，
            // 否则「环境就绪 → 页面开始加载」之间会出现空白闪烁。
        }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(webView, _webView))
            {
                ShowError($"WebView2 初始化失败：{ex.Message}", rebuild: true);
            }
        }
    }

    private void BuildVisualTree()
    {
        _webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _ring = new ProgressRing
        {
            IsActive = false,
            Visibility = Visibility.Collapsed,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _errorText = new TextBlock
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
        };

        var retryButton = new Button
        {
            Content = "重试",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        retryButton.Click += OnRetryClick;

        _errorPanel = new StackPanel
        {
            Spacing = 12,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon
                {
                    Glyph = "\uE7BA",
                    FontSize = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                _errorText,
                retryButton,
            },
        };

        _overlay = new Grid
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _overlay.Children.Add(_ring);
        _overlay.Children.Add(_errorPanel);

        _root = new Grid();
        _root.Children.Add(_webView);
        _root.Children.Add(_overlay);

        // 圆角容器（声明式兜底）：无 SystemBackdrop 场景由 Border 圆角直接裁剪；
        // 本应用启用 Mica（SystemBackdrop）后 Border 圆角对 WinUI3 WebView2 失效
        // （microsoft-ui-xaml#9516，1.8 仍未修复），实际裁剪由 ApplyRoundedClip 的
        // composition 几何裁剪承担：WebView2 内容经 SetElementChildVisual 挂在自身
        // 元素可视化下，对元素可视化施加 Clip 即可覆盖网页内容。
        _frame = new Border
        {
            CornerRadius = new CornerRadius(RoundedCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = _root,
        };
        _frame.SizeChanged += OnFrameSizeChanged;

        // 主题同步订阅：ActualThemeChanged 对隐藏（Collapsed）宿主同样传播；Loaded 首树兜底。
        // 与 TearDown 成对退订；重建路径 TearDown → BuildVisualTree 会重新订阅。
        this.ActualThemeChanged += OnHostActualThemeChanged;
        this.Loaded += OnHostLoaded;
        ApplyThemeToWebView();

        Content = _frame;
    }

    private void TearDown()
    {
        this.ActualThemeChanged -= OnHostActualThemeChanged;
        this.Loaded -= OnHostLoaded;

        // 宿主控件（this）跨重建存活：清掉其可视化上的圆角裁剪，避免残留旧尺寸几何。
        try
        {
            ElementCompositionPreview.GetElementVisual(this).Clip = null;
        }
        catch
        {
            // 纯视觉清理；失败不影响后续重建。
        }

        if (_webView is not null)
        {
            CoreWebView2? core = _webView.CoreWebView2;
            if (core is not null)
            {
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
            }

            try
            {
                _webView.Close();
            }
            catch
            {
                // 关闭失败不致命：控件引用将被丢弃，不再复用。
            }
        }

        if (_frame is not null)
        {
            _frame.SizeChanged -= OnFrameSizeChanged;
            _frame.Child = null;
        }

        _root?.Children.Clear();

        _webView = null;
        _frame = null;
        _root = null;
        _overlay = null;
        _ring = null;
        _errorPanel = null;
        _errorText = null;
        _environment = null;
        _roundedGeometry = null;
        _roundedClip = null;
        _hostRoundedGeometry = null;
        _hostRoundedClip = null;
        _initTask = null;
        _hasNavigated = false;
        _firstNavigationCompleted = false;
        _needsRebuild = false;
    }

    /// <summary>容器尺寸变化：同步圆角几何尺寸并（重新）施加 composition 圆角裁剪。</summary>
    private void OnFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyRoundedClip(e.NewSize);
    }

    /// <summary>
    /// 对 WebView2 元素可视化与宿主自身元素可视化施加<strong>双层</strong>圆角几何裁剪
    /// （<see cref="CompositionGeometricClip"/> + <see cref="CompositionRoundedRectangleGeometry"/>）。
    /// </summary>
    /// <remarks>
    /// Mica（SystemBackdrop）下这是能真正裁到网页内容的机制：Border.CornerRadius 在该场景对
    /// WinUI3 WebView2 失效（microsoft-ui-xaml#9516）。此前仅裁 WebView 元素可视化，外宿主
    /// 不在裁剪内；现对宿主自身可视化同时裁剪，使网页圆角外、宿主子树内的任何矩形绘制
    /// （含控件内部承载 visual）一并被裁圆。不依赖 <c>Border</c>/<c>Control.CornerRadius</c>
    /// 对网页内容的裁剪保证，也不自绘遮罩。纯视觉效果：任何失败均静默放弃
    /// （退回 Border 圆角 / 直角），不影响网页加载、登录态、cookie 与生命周期。
    /// 几何尺寸按 DIP 与容器同步；组合对象在宿主重建时随 <see cref="TearDown"/> 一并丢弃重建。
    /// </remarks>
    private void ApplyRoundedClip(Windows.Foundation.Size size)
    {
        if (_closed || _webView is null)
        {
            return;
        }

        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        try
        {
            Visual hostVisual = ElementCompositionPreview.GetElementVisual(this);
            Visual webViewVisual = ElementCompositionPreview.GetElementVisual(_webView);
            Compositor compositor = webViewVisual.Compositor;

            if (_roundedGeometry is null || _roundedClip is null)
            {
                _roundedGeometry = compositor.CreateRoundedRectangleGeometry();
                _roundedClip = compositor.CreateGeometricClip(_roundedGeometry);
            }

            // 宿主层独立一套几何/裁剪对象（不与 WebView 共用同一 CompositionClip 实例）。
            if (_hostRoundedGeometry is null || _hostRoundedClip is null)
            {
                _hostRoundedGeometry = compositor.CreateRoundedRectangleGeometry();
                _hostRoundedClip = compositor.CreateGeometricClip(_hostRoundedGeometry);
            }

            var corner = new Vector2(RoundedCornerRadius, RoundedCornerRadius);
            var sizeVector = new Vector2((float)size.Width, (float)size.Height);
            _roundedGeometry.CornerRadius = corner;
            _roundedGeometry.Size = sizeVector;
            _hostRoundedGeometry.CornerRadius = corner;
            _hostRoundedGeometry.Size = sizeVector;

            // 每次尺寸变化都重新赋值：幂等，且避免被后续布局误重置。
            // 先 WebView 后宿主：后者若失败，前者保持既有行为（整体仍被 catch 静默）。
            webViewVisual.Clip = _roundedClip;
            hostVisual.Clip = _hostRoundedClip;
        }
        catch
        {
            // 裁剪为纯视觉优化，失败不影响任何功能（见 remarks）。
        }
    }

    private void OnHostActualThemeChanged(FrameworkElement sender, object args)
    {
        // 隐藏（Collapsed）宿主同样收到该事件；CoreWebView2 未就绪时由就绪路径补写（InitializeAsync）。
        ApplyThemeToWebView();
    }

    private void OnHostLoaded(object sender, RoutedEventArgs e)
    {
        // 首次进入活动树兜底同步（幂等；构造期已应用过一次，此处修正可能的初始 ActualTheme 误差）。
        ApplyThemeToWebView();
    }

    /// <summary>
    /// 把当前 <see cref="FrameworkElement.ActualTheme"/> 同步到 WebView：控件默认底色
    /// （<see cref="WebView2.DefaultBackgroundColor"/>）与 profile 配色偏好
    /// （<see cref="CoreWebView2Profile.PreferredColorScheme"/>）。
    /// </summary>
    /// <remarks>
    /// 时序契约：CoreWebView2 就绪后、首次 <c>Navigate</c> 前调用一次（见 <see cref="InitializeAsync"/>）；
    /// 此后随 <c>ActualThemeChanged</c> / <c>Loaded</c> / 每次 <see cref="ActivateAsync"/> 更新。
    /// 切换主题只写偏好，<strong>不</strong> Reload、不重建环境、不清 cookie、不影响登录态。
    /// 语义边界：<c>PreferredColorScheme</c> 经 <c>prefers-color-scheme</c> 供站点参考，
    /// 站点显式主题设置可覆盖之；<c>DefaultBackgroundColor</c> 只作用于网页未指定颜色的区域，
    /// 不强制网站主题。纯视觉偏好：任何失败静默降级。
    /// </remarks>
    private void ApplyThemeToWebView()
    {
        if (_closed || _webView is null)
        {
            return;
        }

        try
        {
            _webView.DefaultBackgroundColor = GetDefaultBackgroundColor(ActualTheme);

            if (_webView.CoreWebView2 is { } core)
            {
                // 不用 Auto：Auto 仅跟随 OS；应用显式主题（System 模式下 ActualTheme 亦已跟随 OS）
                // 一律按 ActualTheme 映射 Light/Dark。
                core.Profile.PreferredColorScheme = ActualTheme == ElementTheme.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
            }
        }
        catch
        {
            // 主题偏好为纯视觉增强；失败不影响加载、登录态与生命周期。
        }
    }

    /// <summary>主题对应的 WebView 默认底色（仅影响网页未设背景/透明区域，不强制网站主题）。</summary>
    /// <remarks>取值与本机 WinUI 主题资源 <c>SolidBackgroundFillColorBase</c> 一致
    /// （generic.xaml：Dark #202020 / Light #F3F3F3；<c>ApplicationPageBackgroundThemeBrush</c> 同源），
    /// 用于避免暗色主题下的初始化白闪，并让未设背景区域贴合应用主题。</remarks>
    private static Windows.UI.Color GetDefaultBackgroundColor(ElementTheme theme) =>
        theme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

    private async Task RebuildAsync()
    {
        if (_closed)
        {
            return;
        }

        TearDown();
        BuildVisualTree();
        await ActivateAsync();
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        // 无论成败都标记“首次导航已结束”：此后刷新（Reload）才有明确的当前文档目标。
        _firstNavigationCompleted = true;

        if (args.IsSuccess)
        {
            HideOverlay();
        }
        else
        {
            ShowError($"页面加载失败（{args.WebErrorStatus}）。", rebuild: false);
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // 一律不弹新窗口、不启动外部浏览器、不执行任意外部 URI。
        args.Handled = true;

        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            WebProviderInfo.IsHostAllowed(_provider, uri.Host))
        {
            // 仅 provider 自身 / 母公司域名：在同一 WebView 内导航，保持同一会话与登录态。
            sender.Navigate(uri.AbsoluteUri);
        }
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        ShowError($"浏览器进程异常（{args.ProcessFailedKind}）。", rebuild: true);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        _ = RetryAsync();
    }

    private async Task RetryAsync()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            if (_needsRebuild)
            {
                await RebuildAsync();
            }
            else if (_webView?.CoreWebView2 is { } core)
            {
                // 就地重载：同样先进入加载态，由本次 NavigationCompleted 收尾。
                ShowLoading();
                core.Reload();
            }
        }
        catch
        {
            if (!_closed)
            {
                ShowError("重试失败，请稍后再试。", rebuild: true);
            }
        }
    }

    private void ShowLoading()
    {
        if (_ring is not null)
        {
            _ring.Visibility = Visibility.Visible;
            _ring.IsActive = true;
        }

        if (_errorPanel is not null)
        {
            _errorPanel.Visibility = Visibility.Collapsed;
        }

        if (_overlay is not null)
        {
            _overlay.Visibility = Visibility.Visible;
        }
    }

    private void HideOverlay()
    {
        if (_ring is not null)
        {
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
        }

        if (_errorPanel is not null)
        {
            _errorPanel.Visibility = Visibility.Collapsed;
        }

        if (_overlay is not null)
        {
            _overlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message, bool rebuild)
    {
        _needsRebuild = rebuild;

        if (_ring is not null)
        {
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
        }

        if (_errorText is not null)
        {
            _errorText.Text = message;
        }

        if (_errorPanel is not null)
        {
            _errorPanel.Visibility = Visibility.Visible;
        }

        if (_overlay is not null)
        {
            _overlay.Visibility = Visibility.Visible;
        }
    }
}
