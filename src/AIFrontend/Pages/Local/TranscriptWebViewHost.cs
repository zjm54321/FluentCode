using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AIFrontend.Pages.Local;

/// <summary>
/// 整体转录的 WebView2 宿主（纯 C#，独立于官网 Web 环境）。安全约束：
/// 禁脚本 / CSP <c>default-src 'none'</c>（仅固定内联样式）/ 禁外部资源 / 禁止任意导航 /
/// 禁止新窗口 / 无 host objects / 无 web messaging。
/// </summary>
/// <remarks>
/// 生命周期与线程：
/// <list type="bullet">
/// <item>必须在 UI 线程创建与调用；<c>CreateWithOptionsAsync</c> / <c>EnsureCoreWebView2Async</c> 均具 UI 线程亲和性，
/// 所有 <c>await</c> 均<strong>不</strong>使用 <c>ConfigureAwait(false)</c>，保证续体回到 UI 线程。</item>
/// <item>运行时缺失或初始化失败时，通过 <see cref="RenderUnavailable"/> 明确上报并<strong>不</strong>渲染任何内容，
/// 由上层降级为纯文本；绝不把“未渲染”当作成功。</item>
/// <item>文档经 <c>NavigateToString</c> 注入；<c>NavigationStarting</c> 仅放行 about:/data:text/html，
/// 其余一律取消；<c>NewWindowRequested</c> 一律标记已处理且不导航。</item>
/// </list>
/// </remarks>
internal sealed class TranscriptWebViewHost : UserControl
{
    private readonly Grid _root = new();
    private WebView2? _webView;
    private CoreWebView2Environment? _environment;
    private Task? _initTask;
    private bool _unavailable;
    private bool _closed;

    /// <summary>运行时缺失或初始化失败时触发（在 UI 线程），上层据此切换纯文本降级视图。</summary>
    public event EventHandler? RenderUnavailable;

    public TranscriptWebViewHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _root.Children.Add(_webView);
        Content = _root;
    }

    /// <summary>是否已判定为不可用（运行时缺失/初始化失败）。</summary>
    public bool IsUnavailable => _unavailable;

    /// <summary>不可用原因（供上层展示明确错误）；可用时为 null。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>注入完整转录文档；懒初始化环境与实例。不可用时静默返回（由上层负责降级展示）。</summary>
    public async Task SetHtmlAsync(string html)
    {
        if (_closed || _unavailable || html is null)
        {
            return;
        }

        await EnsureInitializedAsync();

        if (_closed || _unavailable)
        {
            return;
        }

        CoreWebView2? core = _webView?.CoreWebView2;
        if (core is null)
        {
            return;
        }

        core.NavigateToString(html);
    }

    /// <summary>关闭 WebView2 实例；关闭后不可复用。</summary>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        TearDownWebView();
        _root.Children.Clear();
    }

    private Task EnsureInitializedAsync()
    {
        _initTask ??= InitializeAsync();
        return _initTask;
    }

    private async Task InitializeAsync()
    {
        if (_closed)
        {
            return;
        }

        // 显式探测运行时：缺失时抛异常 → 明确降级，而不是让 WebView 处于半初始化状态。
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            MarkUnavailable("未检测到可用的 WebView2 运行时。");
            return;
        }

        WebView2? webView = _webView;
        if (_closed || webView is null)
        {
            return;
        }

        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: GetUserDataFolder(),
                options: null);

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
            ConfigureSettings(core.Settings);
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
        }
        catch (Exception)
        {
            if (!_closed && ReferenceEquals(webView, _webView))
            {
                MarkUnavailable("WebView2 初始化失败。");
            }
        }
    }

    /// <summary>收紧全部可控设置：禁脚本、禁消息、禁 host objects、禁导航/菜单/缩放/加速键等。</summary>
    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // 仅放行本控件注入的文档（NavigateToString 产生 about:blank）；其余一律取消。
        if (!IsAllowedNavigation(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private static bool IsAllowedNavigation(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return true;
        }

        return uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // 不弹新窗口、不启动外部浏览器、不执行任意 URI。
        args.Handled = true;
    }

    private void MarkUnavailable(string reason)
    {
        if (_unavailable)
        {
            return;
        }

        _unavailable = true;
        UnavailableReason = reason;
        TearDownWebView();
        RenderUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private void TearDownWebView()
    {
        if (_webView is not null)
        {
            CoreWebView2? core = _webView.CoreWebView2;
            if (core is not null)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NewWindowRequested -= OnNewWindowRequested;
            }

            try
            {
                _webView.Close();
            }
            catch
            {
                // 关闭失败不致命：实例引用将被丢弃，不再复用。
            }

            _webView = null;
        }

        _environment = null;
        _initTask = null;
    }

    private static string GetUserDataFolder()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("无法获取 LocalApplicationData，无法为转录环境定位用户数据目录。");
        }

        return Path.Combine(localData, "AIFrontend", "LocalTranscript");
    }
}
