using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Models;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using WinRT;

namespace FluentCode;

public sealed partial class MainWindow : Window
{
    private object? _subscribedPage;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly Services.Appearance.AppThemeController _themeController;

    private Pages.OpenCodePage? _cachedOpenCodePage;

    // ------------------------------------------------------------------
    // Shell 集成接线（见 MainWindow.ShellIntegration / OpenCode / Taskbar 分部）
    // ------------------------------------------------------------------
    // 1) App 事件：OpenCodeSourceChanged（身份注入/换代）、OpenCodeNavigationRequested、
    //    OpenCodeInteractionProcessed、
    //    IntegrationDiagnosticChanged——构造期订阅，窗口关闭时退订。
    // 2) OpenCode 页：SessionTracked / ExecutionStateChanged / NavigationHandled / Loaded——
    //    随 Frame 导航挂接/解除（见 AttachPageEvents / DetachPageEvents），页面重建不重复订阅。
    //
    // FluentCode 外壳：没有导航栏与单独标题栏。OpenCodePage 铺满窗口，其会话标签行即标题栏；
    // 设置以标签打开（Ctrl+, / 首页右下角“设置”）；未添加 OpenCode 时整窗显示欢迎页（MainWindow.Welcome.cs）。

    public MainWindow()
    {
        this.InitializeComponent();
        _themeController = new(RootElement, FluentCode.Services.Configuration.AppServices.Settings.Current?.Theme ?? AppTheme.System);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "app.ico"));

        // 用户字体（F11）：根元素以 ThemeResource 继承正文字体给所有页面内无样式文字；
        // 登记根元素，运行期改字体时由 AppFonts 刷新 ThemeResource（弹出层经 App.xaml 隐式样式覆盖）。
        Services.Appearance.AppFonts.RegisterRoot(RootElement);

        // BG1: Mica outside (title-bar/tab row); the content layer inside is drawn by OpenCodePage.
        TrySetMicaBackdrop();
        Closed += MainWindow_Closed;

        // 自定义标题栏：标签行（或欢迎页顶部）作为拖动区，保留系统原生 caption 按钮
        SetupCustomTitleBar();

        // 设置初始窗口尺寸
        SetInitialWindowSize(1100, 720);

        // 设置：Ctrl+, 打开（或切换到）设置标签。
        var settingsAccelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)188,
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        settingsAccelerator.Invoked += (_, args) => { args.Handled = true; OpenSettingsPage(); };
        RootElement.KeyboardAccelerators.Add(settingsAccelerator);
        RootElement.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

        if (ContentFrame.CurrentSourcePageType != typeof(Pages.OpenCodePage))
        {
            ContentFrame.Navigate(typeof(Pages.OpenCodePage), null, new SuppressNavigationTransitionInfo());
        }

        // 首次运行：尚未添加任何 OpenCode 时显示欢迎页（随设置变化切换）。
        InitializeWelcome();

        // Shell 集成：构造期完成全部 App/窗口订阅，窗口 Activated 后再置 OpenCode 导航 ready；
        // 详见 MainWindow.ShellIntegration.cs / MainWindow.OpenCode.cs / MainWindow.Taskbar.cs。
        InitializeShellIntegration();

        // OpenCode 页 view ACK 的宿主窗口可见性接线（复用 Window/AppWindow 激活与最小化生命周期）。
        InitializeOpenCodeWindowVisibilityTracking();
    }

    /// <summary>
    /// BG1 (user choice B): Mica BaseAlt window material outside (the tab/title-bar row); the content layer
    /// (LayerFillColorDefaultBrush, see OpenCodePage ContentLayer) sits over it inside, so the shell and the content
    /// area differ as in stock WinUI. The material stays active when the window is unfocused. Unsupported systems
    /// fall back to the same theme-aware BaseAlt solid tone used by shell captures.
    /// </summary>
    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            _backdropConfiguration = new SystemBackdropConfiguration { IsInputActive = true };
            UpdateBackdropTheme();
            RootElement.ActualThemeChanged += RootElement_ActualThemeChanged;
            _micaController = new MicaController { Kind = MicaKind.BaseAlt };
            _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
            _micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        }
        else
        {
            SolidBackdropFallback.Visibility = Visibility.Visible;
        }
    }

    private void RootElement_ActualThemeChanged(FrameworkElement sender, object args) => UpdateBackdropTheme();

    private void UpdateBackdropTheme()
    {
        if (_backdropConfiguration is null) return;
        _backdropConfiguration.Theme = RootElement.ActualTheme switch
        {
            ElementTheme.Light => SystemBackdropTheme.Light,
            ElementTheme.Dark => SystemBackdropTheme.Dark,
            _ => SystemBackdropTheme.Default,
        };
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        RootElement.ActualThemeChanged -= RootElement_ActualThemeChanged;
        _themeController.Dispose();
        _micaController?.Dispose();
        _micaController = null;
        _backdropConfiguration = null;

        // 窗口关闭：先保守置窗口不可见并解除可见性订阅（在途 view ACK fence 立即失败；页面 Unloaded 另做 Invalidate）。
        ShutdownOpenCodeWindowVisibilityTracking();

        // Shell 集成收尾：置导航 ready=false、退订 App 事件、清空页面可信身份（禁止旧 client 身份继续操作）。
        ShutdownShellIntegration();

        ShutdownWelcome();
        ShutdownCustomTitleBar();

        // 解除当前页面事件订阅，避免生命周期尾部悬挂
        DetachPageEvents();

        _cachedOpenCodePage?.ShutdownPage();
        _cachedOpenCodePage = null;

    }

    private void SetInitialWindowSize(int width, int height)
    {
        try
        {
            AppWindow?.Resize(new SizeInt32(width, height));
        }
        catch
        {
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // 页面事件订阅切换到新页面实例（旧页面先解除订阅，避免泄漏）
        AttachPageEvents(e.Content);

        // 页面登记为字体根，重新加载时由 AppFonts 延迟补刷（幂等）。
        if (e.Content is FrameworkElement page)
        {
            Services.Appearance.AppFonts.RegisterRoot(page);
        }

        UpdateTitleBarTarget();
    }

    /// <summary>确保 OpenCode 页在 Frame 中（启动、通知导航等）。</summary>
    private void EnsureOpenCodePage()
    {
        if (ContentFrame.Content is not Pages.OpenCodePage)
        {
            ContentFrame.Navigate(typeof(Pages.OpenCodePage), null, Motion.FluentMotion.PageEntrance());
        }
    }

    // ------------------------------------------------------------------
    // 页面事件订阅（随 Frame 导航切换；OpenCodePage：设置 / 会话跟踪 / 执行状态 / 导航结果）
    // ------------------------------------------------------------------

    /// <summary>
    /// Frame 每次实际导航后调用：先解除旧页面订阅，再订阅新 OpenCodePage 的
    /// <c>SettingsRequested</c> / 会话跟踪 / 执行状态 / 导航结果，并接入设置标签宿主。
    /// </summary>
    private void AttachPageEvents(object? content)
    {
        DetachPageEvents();

        switch (content)
        {
            case Pages.OpenCodePage openCodePage:
                _cachedOpenCodePage = openCodePage;
                openCodePage.SessionTitleChanged += SetSessionWindowTitle;
                SetSessionWindowTitle(openCodePage.CurrentSessionTitle);
                openCodePage.SettingsRequested += Page_SettingsRequested;
                openCodePage.SessionTracked += OpenCodePage_SessionTracked;
                openCodePage.SessionRetired += OpenCodePage_SessionRetired;
                openCodePage.ExecutionStateChanged += OpenCodePage_ExecutionStateChanged;
                openCodePage.NavigationHandled += OpenCodePage_NavigationHandled;
                openCodePage.Loaded += OpenCodePage_ShellLoaded;
                openCodePage.SettingsContentFactory = CreateSettingsContent;
                openCodePage.SettingsCloseGuard = ConfirmCloseSettingsAsync;
                openCodePage.SettingsTabClosed += OpenCodePage_SettingsTabClosed;
                _openCodePageLoaded = false;

                // 注入在屏可见性与激活状态（均 fail-closed 默认 false）；后续变化由生命周期订阅推送，
                // Loaded 后另有补推（attach 与 Loaded 的先后顺序不保证）。
                openCodePage.NotifyHostWindowActivation(_openCodeWindowActivated);
                openCodePage.NotifyHostWindowVisibility(_openCodeWindowVisible);
                // 页面实例重建后立即注入当前可信身份（含 null）；身份实际变化时页面自行重置卡片。
                SyncOpenCodeEndpointIdentity();
                _subscribedPage = openCodePage;
                break;
        }
    }

    /// <summary>解除当前已订阅页面的事件（无订阅时为无操作）。</summary>
    private void DetachPageEvents()
    {
        switch (_subscribedPage)
        {
            case Pages.OpenCodePage openCodePage:
                openCodePage.SessionTitleChanged -= SetSessionWindowTitle;
                openCodePage.SettingsRequested -= Page_SettingsRequested;
                openCodePage.SessionTracked -= OpenCodePage_SessionTracked;
                openCodePage.SessionRetired -= OpenCodePage_SessionRetired;
                openCodePage.ExecutionStateChanged -= OpenCodePage_ExecutionStateChanged;
                openCodePage.NavigationHandled -= OpenCodePage_NavigationHandled;
                openCodePage.Loaded -= OpenCodePage_ShellLoaded;
                openCodePage.SettingsTabClosed -= OpenCodePage_SettingsTabClosed;
                openCodePage.SettingsContentFactory = null;
                openCodePage.SettingsCloseGuard = null;
                _openCodePageLoaded = false;
                break;
        }

        _subscribedPage = null;
    }

    private void Page_SettingsRequested(object? sender, EventArgs e)
    {
        // 页面事件在 UI 线程触发：页面发起的设置入口同样打开设置标签。
        OpenSettingsPage();
    }

    private void SetSessionWindowTitle(string? sessionTitle) => Title = string.IsNullOrWhiteSpace(sessionTitle)
        ? "FluentCode" : "FluentCode — " + sessionTitle;

    // ------------------------------------------------------------------
    // 设置标签（单例；内容为原设置页，离开保护只在关闭标签时进行）
    // ------------------------------------------------------------------

    /// <summary>打开（或切换到）设置标签：Ctrl+, / 首页右下角“设置” / 页面内设置入口。</summary>
    private void OpenSettingsPage()
    {
        if (IsWelcomeShown) return; // 欢迎页没有标签栏；连接成功后再打开设置。
        EnsureOpenCodePage();
        (ContentFrame.Content as Pages.OpenCodePage)?.ShowSettingsTab();
    }

    private FrameworkElement CreateSettingsContent()
    {
        var settingsPage = new Pages.Settings.SettingsPage();
        settingsPage.ThemeChanged += SettingsPage_ThemeChanged;
        settingsPage.Open();
        Services.Appearance.AppFonts.RegisterRoot(settingsPage);
        return settingsPage;
    }

    private void OpenCodePage_SettingsTabClosed(object? sender, FrameworkElement content)
    {
        if (content is not Pages.Settings.SettingsPage settingsPage) return;
        settingsPage.ThemeChanged -= SettingsPage_ThemeChanged;
        settingsPage.Close();
    }

    /// <summary>关闭设置标签前：有未保存输入时做页内三态确认（保存并关闭 / 放弃并关闭 / 取消）。</summary>
    private async Task<bool> ConfirmCloseSettingsAsync(FrameworkElement content)
    {
        if (content is not Pages.Settings.SettingsPage { HasUnsavedChanges: true } settingsPage) return true;
        switch (await settingsPage.ConfirmLeaveAsync())
        {
            case Pages.Settings.SettingsLeaveChoice.Discard:
                settingsPage.DiscardPendingEdits();
                return true;
            case Pages.Settings.SettingsLeaveChoice.Save:
                // 保存失败：留在设置标签（错误已由设置视图呈现）。
                return await settingsPage.TrySavePendingEditsAsync();
            default:
                return false;
        }
    }

    /// <summary>设置页内主题保存成功（或加载完成）→ 立即应用到根元素。</summary>
    private void SettingsPage_ThemeChanged(object? sender, Pages.Settings.SettingsThemeChangedEventArgs e)
    {
        ApplyTheme(e.Theme);
    }

    /// <summary>Apply the saved choice; System follows Windows live, not only its startup theme.</summary>
    private void ApplyTheme(AppTheme theme)
    {
        _themeController.Apply(theme);
    }
}
