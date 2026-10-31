using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.Local;
using AIFrontend.Pages.Web;
using AIFrontend.Services.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIFrontend.Pages;

/// <summary>
/// 普通界面页：顶部原生选择栏（ChatGPT / Gemini / Claude / Local）+ 内容宿主。
/// 前三个标签承载对应官网的 WinUI3 WebView2 宿主；Local 标签承载本地聊天视图
/// <see cref="LocalChatView"/>（首次选中时懒创建，之后切换仅显隐、不销毁，以保留当前会话）。
/// </summary>
/// <remarks>
/// 网页保活：本页设置 <see cref="NavigationCacheMode.Required"/>，由 Frame 缓存实例——
/// 主导航离开（<c>Unloaded</c>）仅挂起（隐藏）全部 WebView2 宿主而不关闭，返回时是同一实例、
/// 同一 WebView2、当前 URL 与登录态（不重新导航首页）；仅窗口最终关闭路径调用
/// <see cref="ShutdownWebViews"/> 做真正释放。Local 视图不受影响，仍按既有契约在 Unloaded 收尾。
/// <para>
/// 本页同时充当“应用级共享服务”的暴露点：<see cref="SharedSettingsService"/> /
/// <see cref="SharedSecretStore"/> / <see cref="SharedSessionStore"/> 为惰性单例。
/// Local 聊天与未来主窗口的设置界面<strong>必须</strong>复用同一实例，否则
/// <see cref="SettingsService.Changed"/> 不会广播到 <see cref="LocalChatView"/>，并会产生重复订阅。
/// </para>
/// </remarks>
public sealed partial class NormalPage : Page
{
    // 应用级共享服务（惰性单例）。构造在嵌套类型内完成，避免与外层同名成员产生解析歧义。
    private static readonly Lazy<SharedServices> Shared = new(static () => new SharedServices());

    private readonly Dictionary<WebProvider, WebViewHost> _hosts = new();
    private LocalChatView? _localView;

    // Local 视图生命周期任务：视图创建时开启新一轮，其在途 Local 执行真正终结（DisposeLocalView 收尾完成）时置完成。
    // 供 MainWindow 判断“是否仍需保留对旧 NormalPage 的 Local 运行订阅”（取消中的终态不得丢失）。
    private TaskCompletionSource? _localTeardownTcs;
    private Task _localTeardownTask = Task.CompletedTask;

    // null 表示 Local 标签；非 null 表示某个官方站点 provider。
    private WebProvider? _selectedProvider = WebProvider.ChatGPT;
    private bool _loaded;
    private bool _hostWindowVisible = true;
    private bool _hostsBuilt;

    // 最终释放标记：窗口关闭路径经 ShutdownWebViews 置位后，宿主不再重建/复活
    // （缓存实例即使再收到 Loaded 也为空操作）。
    private bool _webShutdown;

    public NormalPage()
    {
        this.InitializeComponent();

        // 网页保活（Required 缓存）：Frame 保留本页实例跨主导航复用——ChatGPT / Gemini / Claude
        // 三个 WebView2 宿主随页面保活，返回时同一实例、同一 WebView2、当前 URL 与登录态，
        // 不重新导航首页、不重建环境。最终释放仅发生在窗口关闭路径（ShutdownWebViews）。
        NavigationCacheMode = NavigationCacheMode.Required;

        BuildHosts();

        // 默认选中 ChatGPT；实际激活推迟到 Loaded，避免在未挂载时初始化 WebView2。
        ProviderSelector.SelectedItem = ProviderItemChatGpt;

        this.Loaded += OnPageLoaded;
        this.Unloaded += OnPageUnloaded;
        // 品牌图标（ChatGPT）含浅/深两版；主题切换后按新主题重应用，保持图标配色跟随。
        Services.Appearance.ThemeResources.Observe(this, () => OnPageActualThemeChanged(this, EventArgs.Empty));
    }

    /// <summary>应用级共享的设置服务（惰性单例）；供 Local 与未来主窗口设置界面复用。</summary>
    public static SettingsService SharedSettingsService => Shared.Value.Settings;

    /// <summary>应用级共享的凭据存储（惰性单例）。</summary>
    public static DpapiSecretStore SharedSecretStore => Shared.Value.Secrets;

    /// <summary>应用级共享的本地会话存储（惰性单例）。</summary>
    public static LocalSessionStore SharedSessionStore => Shared.Value.Sessions;

    /// <summary>本页使用的共享设置服务实例（等价于 <see cref="SharedSettingsService"/>）。</summary>
    public SettingsService SettingsService => SharedSettingsService;

    /// <summary>本页使用的共享凭据存储实例（等价于 <see cref="SharedSecretStore"/>）。</summary>
    public DpapiSecretStore SecretStore => SharedSecretStore;

    /// <summary>本页使用的共享会话存储实例（等价于 <see cref="SharedSessionStore"/>）。</summary>
    public LocalSessionStore SessionStore => SharedSessionStore;

    /// <summary>
    /// Local 视图空配置引导中点击“打开设置”时<strong>转发</strong>的事件。
    /// </summary>
    /// <remarks>
    /// 精确签名：<c>public event EventHandler? SettingsRequested;</c>。
    /// <c>sender</c> 为<strong>本页实例</strong>（非 Local 视图），<c>EventArgs</c> 原样转发。
    /// 本页不直接引用设置界面类型；消费者（主窗口）据此打开设置界面。
    /// </remarks>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Local 视图一轮“真实成功”完成时<strong>转发</strong>的事件。
    /// </summary>
    /// <remarks>
    /// 精确签名：<c>public event EventHandler&lt;AIFrontend.Pages.Local.LocalTaskCompletedEventArgs&gt;? LocalTaskCompleted;</c>。
    /// <c>sender</c> 为<strong>本页实例</strong>，参数（<see cref="LocalTaskCompletedEventArgs.SourceRequestId"/> /
    /// <see cref="LocalTaskCompletedEventArgs.SessionId"/>）原样转发。
    /// 仅当 Local 流返回 <c>Completed</c> 且 <c>FinishReason == Stop</c> 时触发；本页<strong>不</strong>注册、
    /// <strong>不</strong>发送任何系统通知。
    /// </remarks>
    public event EventHandler<LocalTaskCompletedEventArgs>? LocalTaskCompleted;

    /// <summary>
    /// Local 视图一轮执行的<strong>结构化状态</strong>（Working / Completed / Failed / Cancelled）转发事件。
    /// </summary>
    /// <remarks>
    /// 精确签名：<c>public event EventHandler&lt;AIFrontend.Pages.Local.LocalTaskStatusEventArgs&gt;? LocalTaskStatusChanged;</c>。
    /// <c>sender</c> 为<strong>本页实例</strong>，参数原样转发。每轮执行先 Working、随后恰好一次终态；
    /// 所有异常/取消均终结 Working。载荷只含结构化状态与不透明 ID（<c>SourceRequestId</c> / <c>SessionId</c>），
    /// <strong>不含</strong>消息正文或凭据。本页不注册、不发送任何系统通知。
    /// </remarks>
    public event EventHandler<LocalTaskStatusEventArgs>? LocalTaskStatusChanged;

    /// <summary>
    /// 本页 Local 生命周期终结通知：当在途 Local 执行真正结束时（<see cref="DisposeLocalView"/> 收尾完成）触发。
    /// </summary>
    /// <remarks>
    /// 供 MainWindow 判定可安全解除对旧 <see cref="NormalPage"/> 实例 Local 运行事件的订阅——
    /// 在此之前必须保留订阅，否则页面切换取消产生的终态会丢失。已在 UI 线程触发。
    /// </remarks>
    internal event EventHandler? LocalTeardownCompleted;

    /// <summary>本页当前是否已无在途 Local 生命周期（未创建过 Local 视图，或其收尾已完成）。</summary>
    internal bool IsLocalTeardownComplete => _localTeardownTask.IsCompleted;

    /// <summary>
    /// 外部/通知导航：把 Local 视图定位到指定会话（校验 ID；<strong>不覆盖未发送草稿</strong>）。
    /// 需本页已加载且 Local 视图已创建；校验失败返回 <c>false</c>，不切换、不抛异常、不造假成功。须在 UI 线程调用。
    /// </summary>
    /// <remarks>
    /// 成功返回 <c>true</c> 并切到 Local 标签后交由 <see cref="LocalChatView.TryActivateSession"/> 选中；
    /// 该方法只校验会话 ID 是否已在已加载会话集合中（Ordinal 精确匹配），不读取/清空输入框内容。
    /// 若 Local 视图尚未加载会话（首次进入且异步加载未完成），校验会如实返回 <c>false</c>，不伪造定位结果。
    /// </remarks>
    public bool TryActivateLocalSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || !_loaded)
        {
            return false;
        }

        ProviderSelector.SelectedItem = ProviderItemLocal;
        LocalChatView view = EnsureLocalView();
        return view.TryActivateSession(sessionId);
    }

    /// <summary>
    /// 外部/通知导航（可 await）：切换到 Local 标签并<strong>等待设置与会话的实际加载真正完成</strong>后再定位指定会话。
    /// </summary>
    /// <remarks>
    /// 需本页已加载（<c>_loaded</c>）；复用 Local 视图的同一加载 Task（不并发重复 Load）。返回
    /// <c>true</c> 仅当定位真实完成；<c>false</c> 表示加载已真正完成但会话不存在（或本页尚未加载）。
    /// 关闭/页面卸载经 <paramref name="cancellationToken"/> 取消并抛出 <see cref="OperationCanceledException"/>——
    /// 调用方据此区分“取消”与“确实不存在”，不把取消当成功。校验/切换语义与 <see cref="TryActivateLocalSession"/> 一致，
    /// 不读取、清空或改写未发送草稿。须在 UI 线程调用。
    /// </remarks>
    public async Task<bool> TryActivateLocalSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId) || !_loaded)
        {
            return false;
        }

        ProviderSelector.SelectedItem = ProviderItemLocal;
        LocalChatView view = EnsureLocalView();
        return await view.TryActivateSessionAsync(sessionId, cancellationToken);
    }

    /// <summary>
    /// 清理指定 provider 环境的全部 cookie（供未来设置 UI 调用）。
    /// </summary>
    /// <remarks>
    /// 调用契约：
    /// <list type="bullet">
    /// <item>必须在 UI 线程调用。宿主随页面构建，并在缓存保活期间保持存在（主导航离开不再关闭宿主，
    /// 见 <see cref="ShutdownWebViews"/>）；仅当宿主缺失（已最终释放）时抛
    /// <see cref="InvalidOperationException"/>（不静默失败）。</item>
    /// <item>优先复用该 provider 的现有活动实例；未初始化时由 UI 线程按需创建（不导航）后清理。</item>
    /// <item>只清 cookie，不删除用户数据目录，也不清除其他站点存储。</item>
    /// </list>
    /// 页面处于缓存未挂载态（如设置页显示期间）调用本方法的可用性未经验证——WebView2 初始化需要
    /// 已加载可视树，失败会如实抛出，不假成功。未来设置 UI 可先通过 <c>ContentFrame</c> 取回当前
    /// NormalPage 实例，再调用本方法。
    /// </remarks>
    public async Task ClearCookiesAsync(WebProvider provider)
    {
        if (!_hosts.TryGetValue(provider, out WebViewHost? host))
        {
            throw new InvalidOperationException(
                "NormalPage 当前未持有该 provider 的宿主（页面可能未加载），无法清理 cookie。");
        }

        await host.ClearCookiesAsync();
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new FrameworkElementAutomationPeer(this);
    }

    private void BuildHosts()
    {
        if (_webShutdown)
        {
            // 最终释放后不再重建宿主（不复活）：缓存实例即使再收到 Loaded 也保持已释放态。
            return;
        }

        foreach (WebProvider provider in WebProviderInfo.All)
        {
            var host = new WebViewHost(provider) { Visibility = Visibility.Collapsed };
            _hosts[provider] = host;
            ContentHost.Children.Add(host);
        }

        _hostsBuilt = true;
    }

    private void DisposeHosts()
    {
        foreach (WebViewHost host in _hosts.Values)
        {
            host.Close();
            ContentHost.Children.Remove(host);
        }

        _hosts.Clear();
        _hostsBuilt = false;
    }

    /// <summary>
    /// 最终释放：关闭并移除全部 WebView2 宿主（连同环境引用）。<strong>仅</strong>窗口最终关闭路径调用
    /// （MainWindow 在 <c>Closed</c> 中对缓存的 NormalPage 实例调用）；幂等，重复调用为无操作。
    /// </summary>
    /// <remarks>
    /// 主导航离开<strong>不会</strong>调用本方法：页面经 <see cref="NavigationCacheMode.Required"/> 由
    /// Frame 缓存，宿主与 WebView2 实例跨主导航保活（同一实例、当前 URL、登录态），<c>Unloaded</c>
    /// 仅挂起（隐藏）。调用后本页进入“已最终释放”态：即使再收到 <c>Loaded</c> 也不重建宿主（不复活）；
    /// <see cref="ClearCookiesAsync"/> 会因宿主缺失抛 <see cref="InvalidOperationException"/>（不静默假成功）。
    /// 不触碰 Local 视图生命周期（其收尾经既有 <c>DisposeLocalView</c>/<c>ShutdownAsync</c> 链独立进行）。
    /// 必须在 UI 线程调用（WebView2 线程亲和）。
    /// </remarks>
    public void ShutdownWebViews()
    {
        _webShutdown = true;
        DisposeHosts();
    }

    /// <summary>
    /// 首次选中 Local 标签时懒创建并挂载 <see cref="LocalChatView"/>；已存在则直接复用。
    /// </summary>
    private LocalChatView EnsureLocalView()
    {
        if (_localView is not null)
        {
            return _localView;
        }

        var view = new LocalChatView(SharedSettingsService, SharedSecretStore, SharedSessionStore)
        {
            Visibility = Visibility.Collapsed,
        };
        view.SettingsRequested += OnLocalSettingsRequested;
        view.TaskCompleted += OnLocalTaskCompleted;
        view.TaskStatusChanged += OnLocalTaskStatusChanged;

        _localView = view;
        ContentHost.Children.Add(view);

        // 开启新一轮 Local 生命周期：其运行终结（见 DisposeLocalView/ShutdownLocalViewAsync）前，
        // 本页对视图的 Local 运行事件订阅必须保留，使取消中的终态仍可转发。
        _localTeardownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _localTeardownTask = _localTeardownTcs.Task;
        return view;
    }

    /// <summary>
    /// 卸载 Local 视图：按 <see cref="LocalChatView.ShutdownAsync"/> 安全清理（<strong>等待在途 Local 执行真正终结</strong>），
    /// 期间<strong>保留</strong>对视图的运行事件转发订阅，收尾完成后才退订并从可视树移除。
    /// </summary>
    /// <remarks>
    /// 关键顺序（修复取消终态丢失）：<c>TaskCompleted</c> / <c>TaskStatusChanged</c> 的转发订阅保留到
    /// <see cref="LocalChatView.ShutdownAsync"/> 完成——页面切换取消在途 Local 流时，终态（Cancelled）仍能经本页转发，
    /// 使 MainWindow 按真实 <c>SourceRequestId</c> 收尾任务栏，不再滞留 Working。<c>Unloaded</c> 处无法 <c>await</c>，
    /// 故即发即弃一个内部收尾任务；退订与移除均在其完成（UI 线程）后执行，随后通知 <see cref="LocalTeardownCompleted"/>。
    /// </remarks>
    private void DisposeLocalView()
    {
        LocalChatView? view = _localView;
        _localView = null;
        if (view is null)
        {
            return;
        }

        // 设置入口可立即退订；两个 Local 运行事件订阅须保留至 ShutdownAsync 完成。
        view.SettingsRequested -= OnLocalSettingsRequested;

        TaskCompletionSource? teardown = _localTeardownTcs;
        _localTeardownTcs = null;
        _ = ShutdownLocalViewAsync(view, teardown);
    }

    /// <summary>等待 Local 视图真正收尾后，退订其事件并从可视树移除，最后通知本页 Local 生命周期终结。</summary>
    private async Task ShutdownLocalViewAsync(LocalChatView view, TaskCompletionSource? teardown)
    {
        try
        {
            await view.ShutdownAsync();
        }
        catch
        {
            // ShutdownAsync 内部对取消/关闭均已兜底；此处确保后续退订与移除仍会执行。
        }
        finally
        {
            view.TaskCompleted -= OnLocalTaskCompleted;
            view.TaskStatusChanged -= OnLocalTaskStatusChanged;
            ContentHost.Children.Remove(view);

            teardown?.TrySetResult();
            LocalTeardownCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // 缓存实例重入（主导航返回）时宿主已存在，不重建、不重复订阅；
        // 若已最终释放（窗口关闭路径）则保持已释放态，不复活。
        if (!_webShutdown && !_hostsBuilt)
        {
            BuildHosts();
        }

        _loaded = true;
        ApplySelection();

        // 官方品牌图标接入点：Assets/Brands 下存在对应官方资源时替换占位图标（PNG/SVG 均可）。
        // 资源缺失或加载失败时静默保留通用 Segoe 占位字形（见 ProviderBrandIcons）。
        await ApplyBrandIconsAsync();
    }

    /// <summary>
    /// 尝试把三个官网项的占位图标替换为官方品牌图标（<see cref="ProviderBrandIcons"/>）。
    /// </summary>
    /// <remarks>
    /// 不改变任何选择 / 导航逻辑；Local 项不在替换范围（保持通用占位字形）。
    /// 资源缺失或加载失败时静默保留占位图标，不抛异常、不影响页面可用性。
    /// </remarks>
    private async Task ApplyBrandIconsAsync()
    {
        await ProviderBrandIcons.TryApplyAsync(ProviderItemChatGpt, WebProvider.ChatGPT);
        await ProviderBrandIcons.TryApplyAsync(ProviderItemGemini, WebProvider.Gemini);
        await ProviderBrandIcons.TryApplyAsync(ProviderItemClaude, WebProvider.Claude);
    }

    private void OnPageActualThemeChanged(FrameworkElement sender, object args)
    {
        // 仅在已加载时重应用；未加载则 Loaded 时会重新应用。异常在 ProviderBrandIcons 内已吞并（失败保留占位）。
        if (_loaded)
        {
            _ = ApplyBrandIconsAsync();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;

        // 网页保活：主导航离开时**不**关闭 WebView2 宿主（页面经 NavigationCacheMode.Required
        // 由 Frame 缓存，返回时是同一实例、同一 WebView2、当前 URL 与登录态，不重新导航首页）。
        // 这里只把全部宿主挂起（Deactivate = 仅隐藏、不销毁）；再次 Loaded 时 ApplySelection
        // 按当前选中项恢复显示。真正的关闭只发生在窗口最终关闭路径（MainWindow 调 ShutdownWebViews）。
        foreach (WebViewHost host in _hosts.Values)
        {
            host.Deactivate();
        }

        // Local 视图仍按契约 ShutdownAsync 清理（会话内容已持久化，重入时从磁盘重新加载）；
        // Local 取消/终态结算/晚退订链与网页保活相互独立，保持既有语义不变。
        DisposeLocalView();
    }

    private void ProviderSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not SelectorBarItem item || item.Tag is not string tag)
        {
            return;
        }

        // 封闭枚举：仅接受枚举内已知 provider；"Local" 等其它项一律视为本地标签。
        _selectedProvider = WebProviderInfo.TryParseProvider(tag, out WebProvider provider)
            ? provider
            : null;

        if (!_loaded)
        {
            return;
        }

        ApplySelection();
    }

    private void ApplySelection()
    {
        // 刷新按钮仅对官网 WebView2 有意义：Local 标签下完全隐藏（Collapsed，不占位），
        // 切回任一官网恢复显示（见 RefreshButton_Click）。
        RefreshButton.Visibility = _selectedProvider is null ? Visibility.Collapsed : Visibility.Visible;

        foreach (KeyValuePair<WebProvider, WebViewHost> entry in _hosts)
        {
            bool isActive = _loaded && _hostWindowVisible && _selectedProvider == entry.Key;

            if (isActive)
            {
                entry.Value.Visibility = Visibility.Visible;

                // 首次显示时才初始化并导航；重复切换只复用现有实例。
                _ = entry.Value.ActivateAsync();
            }
            else
            {
                entry.Value.Deactivate();
            }
        }

        if (_selectedProvider is null)
        {
            // Local：首次选中时懒创建并挂载，随后显示。
            LocalChatView view = EnsureLocalView();
            view.Visibility = Visibility.Visible;
        }
        else if (_localView is not null)
        {
            // 仅隐藏：保留当前会话、模型选择与转录 WebView2；切回时不重建、不重复订阅。
            _localView.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        // 仅重载当前选中官网的 WebView2；Local 标签下按钮已隐藏，此处再兜底校验（空操作）。
        // 不清除 cookie、不重建环境、不触碰其他 provider；不重发 Local API、不清除 Local 历史。
        if (_selectedProvider is WebProvider provider &&
            _hosts.TryGetValue(provider, out WebViewHost? host))
        {
            host.Refresh();
        }
    }

    internal void NotifyHostWindowVisibility(bool visible)
    {
        if (_hostWindowVisible == visible) return;
        _hostWindowVisible = visible;
        if (_loaded) ApplySelection();
    }

    private void OnLocalSettingsRequested(object? sender, EventArgs e)
    {
        SettingsRequested?.Invoke(this, e);
    }

    private void OnLocalTaskCompleted(object? sender, LocalTaskCompletedEventArgs e)
    {
        LocalTaskCompleted?.Invoke(this, e);
    }

    private void OnLocalTaskStatusChanged(object? sender, LocalTaskStatusEventArgs e)
    {
        LocalTaskStatusChanged?.Invoke(this, e);
    }

    /// <summary>应用级共享服务的惰性单例容器。</summary>
    private sealed class SharedServices
    {
        public SettingsService Settings { get; } = new();

        public DpapiSecretStore Secrets { get; } = new();

        public LocalSessionStore Sessions { get; } = new();
    }
}
