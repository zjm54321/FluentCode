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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using WinRT;

namespace FluentCode;

public sealed partial class MainWindow : Window
{
    private object? _lastNavSelection;
    private bool _syncingNavSelection;
    private object? _subscribedPage;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private readonly Services.Appearance.AppThemeController _themeController;

    private Pages.OpenCodePage? _cachedOpenCodePage;

    // 离开设置页的脏数据保护：拦下导航 → 页内三态确认 → 按选择保存/放弃/取消后重放导航。
    private bool _settingsLeaveConfirmed;
    private NavigationMode _pendingLeaveMode = NavigationMode.New;
    private Type? _pendingLeavePageType;
    private object? _pendingLeaveParameter;

    /// <summary>
    /// 搜索可跳转的 OpenCode 导航项（无全局内容搜索服务）。
    /// 名称用于匹配与展示，Tag 与 NavigationViewItem.Tag 一致。
    /// </summary>
    private static readonly (string Name, string Tag)[] SearchTargets =
    {
        ("OpenCode", "OpenCode"),
    };

    // ------------------------------------------------------------------
    // Shell 集成接线（见 MainWindow.ShellIntegration / OpenCode / Taskbar 分部）
    // ------------------------------------------------------------------
    // 1) App 事件：OpenCodeSourceChanged（身份注入/换代）、OpenCodeNavigationRequested、
    //    OpenCodeInteractionProcessed、
    //    IntegrationDiagnosticChanged——构造期订阅，窗口关闭时退订。
    // 2) OpenCode 页：SessionTracked / ExecutionStateChanged / NavigationHandled / Loaded——
    //    随 Frame 导航挂接/解除（见 AttachPageEvents / DetachPageEvents），页面重建不重复订阅。

    public MainWindow()
    {
        this.InitializeComponent();
        _themeController = new(RootElement, FluentCode.Services.Configuration.AppServices.Settings.Current?.Theme ?? AppTheme.System);
        NavView.Loaded += (_, _) =>
        {
            if (NavView.DisplayMode == NavigationViewDisplayMode.Expanded && !_paneOpenedByUser) NavView.IsPaneOpen = false;
        };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon", "app.ico"));

        // 用户字体（F11）：NavView 以 ThemeResource 继承正文字体给所有页面内无样式文字；
        // 登记根元素，运行期改字体时由 AppFonts 刷新 ThemeResource（弹出层经 App.xaml 隐式样式覆盖）。
        Services.Appearance.AppFonts.RegisterRoot(RootElement);

        // BG1: use the real desktop acrylic, including while another window has focus.
        TrySetMicaBackdrop();
        Closed += MainWindow_Closed;

        // 自定义标题栏：由 WinUI 跟踪 XAML 拖拽区，保留系统原生 caption 按钮
        SetupCustomTitleBar();

        // 设置初始窗口尺寸
        SetInitialWindowSize(1100, 720);

        // 默认选中 OpenCode并加载对应页面
        NavView.SelectedItem = NavOpenCode;
        if (ContentFrame.CurrentSourcePageType != typeof(Pages.OpenCodePage))
        {
            ContentFrame.Navigate(typeof(Pages.OpenCodePage), null, new SuppressNavigationTransitionInfo());
        }

        // 初始无导航历史：确保返回按钮为禁用态
        UpdateBackButtonState();

        // 以 NavigationView 实际 DisplayMode 初始化响应式布局（搜索框 ↔ 搜索按钮）
        UpdateResponsiveLayout();

        // Shell 集成：构造期完成全部 App/窗口订阅，窗口 Activated 后再置 OpenCode 导航 ready；
        // 详见 MainWindow.ShellIntegration.cs / MainWindow.OpenCode.cs / MainWindow.Taskbar.cs。
        InitializeShellIntegration();

        // OpenCode 页 view ACK 的宿主窗口可见性接线（复用 Window/AppWindow 激活与最小化生命周期）。
        InitializeOpenCodeWindowVisibilityTracking();
    }

    /// <summary>
    /// BG1 (user choice B): Mica BaseAlt window material outside; the native NavigationView content layer
    /// (LayerFillColorDefaultBrush) sits over it inside, so the shell and the content area differ as in stock WinUI.
    /// The material stays active when the window is unfocused. Unsupported systems fall back to desktop acrylic,
    /// then the default window background.
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
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
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

    // ------------------------------------------------------------------
    // 响应式布局（以 NavigationView 实际 DisplayMode 驱动）
    // ------------------------------------------------------------------

    // 宽窗（Expanded）下左栏只在用户点汉堡时展开：Auto 进入 Expanded 时原生会自动展开，
    // 这里在模式切换结束后按用户上次的选择收回；模式切换期间的自动开合不算用户选择。
    private bool _paneOpenedByUser;
    private bool _displayModeChanging;

    private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        // 完全依赖 NavigationView 自身的 Auto 阈值判定，不自造不同阈值
        UpdateResponsiveLayout();
        _displayModeChanging = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (NavView.DisplayMode == NavigationViewDisplayMode.Expanded && NavView.IsPaneOpen != _paneOpenedByUser)
                NavView.IsPaneOpen = _paneOpenedByUser;
            _displayModeChanging = false;
        });
    }

    private void NavView_PaneOpened(NavigationView sender, object args)
    {
        if (!_displayModeChanging && sender.DisplayMode == NavigationViewDisplayMode.Expanded) _paneOpenedByUser = true;
        UpdateResponsiveLayout();
    }

    private void NavView_PaneClosed(NavigationView sender, object args)
    {
        if (!_displayModeChanging && sender.DisplayMode == NavigationViewDisplayMode.Expanded) _paneOpenedByUser = false;
        UpdateResponsiveLayout();
    }

    /// <summary>
    /// 以 NavigationView 实际 <see cref="NavigationView.DisplayMode"/> 驱动顶栏与汉堡归属
    /// （互斥，任一时刻仅一个汉堡可见）：
    /// 宽/中（Expanded/Compact）→ 汉堡为左栏顶部内置 <c>PaneToggleButton</c>
    /// （IsPaneToggleButtonVisible=true），顶栏 <c>HamburgerButton</c> 折叠，中部显示原生 AutoSuggestBox 搜索框；
    /// 小（Minimal）→ 内置关闭，汉堡进入顶栏左组（顺序 返回/汉堡/搜索，搜索在汉堡右侧），
    /// 搜索框折叠为左组搜索按钮。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        bool minimal = NavView.DisplayMode == NavigationViewDisplayMode.Minimal;

        // 汉堡归属（互斥）：宽/中归左栏顶部内置按钮；小窗归顶栏自有按钮。
        // 内置按钮在 Minimal 下关闭（含侧栏浮出时），避免与顶栏汉堡重复。
        bool builtInToggleVisible = !minimal;
        if (NavView.IsPaneToggleButtonVisible != builtInToggleVisible)
        {
            NavView.IsPaneToggleButtonVisible = builtInToggleVisible;
        }

        Visibility hamburgerVisibility = minimal ? Visibility.Visible : Visibility.Collapsed;
        if (HamburgerButton.Visibility != hamburgerVisibility)
        {
            HamburgerButton.Visibility = hamburgerVisibility;
        }

        Visibility boxVisibility = minimal ? Visibility.Collapsed : Visibility.Visible;
        Visibility buttonVisibility = minimal ? Visibility.Visible : Visibility.Collapsed;

        if (SearchBox.Visibility != boxVisibility)
        {
            SearchBox.Visibility = boxVisibility;
        }

        if (SearchButton.Visibility != buttonVisibility)
        {
            SearchButton.Visibility = buttonVisibility;
        }

        // 顶栏可见性变化改变交互控件布局与 Passthrough 命中区域：
        // 立即重算一次（隐藏时按 Visibility 即刻排除旧 rect），并在布局完成后重算一次
        UpdateTitleBarRegions();
        RequestTitleBarRegionsUpdateAfterLayout();
    }

    private void HamburgerButton_Click(object sender, RoutedEventArgs e)
    {
        // 顶栏汉堡仅小窗 Minimal 可见（宽/中由左栏顶部内置 PaneToggleButton 承担）：
        // Minimal 下开合浮出侧栏。
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    // ------------------------------------------------------------------
    // 顶栏搜索（仅既有导航项匹配跳转，不伪造全局内容搜索）
    // ------------------------------------------------------------------

    private void SearchFlyout_Opened(object? sender, object e)
    {
        // 小窗搜索按钮的原生 Flyout：打开后聚焦输入框
        SearchBoxFlyout.Focus(FocusState.Programmatic);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        string query = sender.Text?.Trim() ?? string.Empty;
        sender.ItemsSource = query.Length == 0
            ? null
            : SearchTargets
                .Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name)
                .ToArray();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string? tag = null;
        if (args.ChosenSuggestion is string chosen)
        {
            tag = SearchTargets.FirstOrDefault(t => t.Name == chosen).Tag;
        }
        else
        {
            string query = args.QueryText?.Trim() ?? string.Empty;
            if (query.Length > 0)
            {
                tag = SearchTargets.FirstOrDefault(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Tag;
            }
        }

        if (tag is null)
        {
            return;
        }

        NavigateToTag(tag);
        sender.Text = string.Empty;
        sender.ItemsSource = null;

        // 小窗搜索按钮的原生 Flyout：跳转后收起 Flyout
        if (ReferenceEquals(sender, SearchBoxFlyout))
        {
            SearchButton.Flyout?.Hide();
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        // 选择建议后仅回填文本（默认行为）；实际跳转在 QuerySubmitted 中执行。
    }

    /// <summary>按 Tag 跳转到既有导航项：优先改变 NavigationView 选中项（触发 SelectionChanged 导航）。</summary>
    private void NavigateToTag(string tag)
    {
        NavigationViewItem? item = tag switch
        {
            "OpenCode" => NavOpenCode,
            _ => null,
        };

        if (item is null)
        {
            return;
        }

        if (!ReferenceEquals(NavView.SelectedItem, item))
        {
            NavView.SelectedItem = item; // 触发 NavView_SelectionChanged → 导航
            return;
        }

        // 已选中但页面可能不一致（理论不发生）：确保内容页正确
        Type? pageType = tag switch
        {
            "OpenCode" => typeof(Pages.OpenCodePage),
            _ => null,
        };
        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, null, Motion.FluentMotion.PageEntrance());
        }
    }

    // ------------------------------------------------------------------
    // 设置：左栏底部齿轮（旋转一圈后打开）与头像 Flyout 入口
    // ------------------------------------------------------------------

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        switch (item.Tag as string)
        {
            // 设置项（SelectsOnInvoked=False）：仅触发齿轮旋转并进入应用内设置页，不改变页面选中。
            case "Settings":
                AnimateSettingsGear();
                OpenSettingsPage();
                break;

            // 账户项（SelectsOnInvoked=False）：仅弹出既有账户菜单，不改变页面选中、不导航。
            case "Account":
                UpdateAccountConnectionSummary();
                FlyoutBase.ShowAttachedFlyout(item);
                break;

            // 任务状态项（SelectsOnInvoked=False）：仅弹出任务列表 Flyout，不改变页面选中、不导航。
            case "Tasks":
                FlyoutBase.ShowAttachedFlyout(item);
                break;
        }
    }

    /// <summary>
    /// 齿轮旋转一圈（0→360°，333ms Point-to-point）。走合成外观属性 Rotation，不触发布局；
    /// 减少动画时跳过（直接打开设置，不阻塞）。
    /// </summary>
    private void AnimateSettingsGear()
    {
        if (!Motion.FluentMotion.Enabled)
        {
            return;
        }

        try
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            SettingsGearIcon.CenterPoint = new System.Numerics.Vector3((float)SettingsGearIcon.ActualWidth / 2, (float)SettingsGearIcon.ActualHeight / 2, 0);
            var spin = compositor.CreateScalarKeyFrameAnimation();
            spin.Target = "Rotation";
            spin.InsertKeyFrame(0f, 0f);
            spin.InsertKeyFrame(1f, 360f, Motion.FluentMotion.Ease(compositor, Motion.FluentMotionTokens.PointToPoint));
            spin.Duration = Motion.FluentMotion.Duration(Motion.FluentMotion.Slow);
            SettingsGearIcon.StartAnimation(spin);
        }
        catch
        {
            // 动画异常不影响设置打开（不向外抛出）
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // 由 Frame 后退反向同步选中项时，不作为用户主动导航处理：
        // 避免再次 Navigate 造成重复入栈或返回循环
        if (_syncingNavSelection)
        {
            return;
        }

        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            // 左栏底部设置/账户/任务状态项：点击不改变页面选中（SelectsOnInvoked=False），此处兜底
            // 恢复 `_lastNavSelection`（进入设置页时该值即 NavSettings），避免内容页未变、选中项却被改走。
            if (selectedItem.Tag as string is "Settings" or "Account" or "Tasks")
            {
                _syncingNavSelection = true;
                try
                {
                    sender.SelectedItem = _lastNavSelection ?? NavOpenCode;
                }
                finally
                {
                    _syncingNavSelection = false;
                }
                return;
            }

            _lastNavSelection = selectedItem;

            string tag = selectedItem.Tag?.ToString() ?? string.Empty;
            Type? targetPageType = tag switch
            {
                    "OpenCode" => typeof(Pages.OpenCodePage),
                _ => null
            };

            // 已在目标页时不重复 Navigate，保证返回栈不产生重复项
            if (targetPageType != null && ContentFrame.CurrentSourcePageType != targetPageType)
            {
                ContentFrame.Navigate(targetPageType, null, Motion.FluentMotion.PageEntrance());
            }
            return;
        }

        // 保证始终有选中项（例如 Ctrl+点击取消选中时恢复上一项）
        if (sender.SelectedItem == null)
        {
            sender.SelectedItem = _lastNavSelection ?? NavOpenCode;
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // 每次实际导航（含 GoBack 后退）后更新返回按钮可用态，并把 Frame 当前页回写为主导航选中项；
        // 同时把页面事件订阅切换到新页面实例（旧页面先解除订阅，避免泄漏）
        UpdateBackButtonState();
        SyncNavSelectionWithFrame(e.SourcePageType);
        AttachPageEvents(e.Content);

        // 页面登记为字体根，重新加载时由 AppFonts 延迟补刷（幂等）。
        if (e.Content is FrameworkElement page)
        {
            Services.Appearance.AppFonts.RegisterRoot(page);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // 仅在有历史时可后退（按钮禁用态由 UpdateBackButtonState 保证）
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack(BackTransition());
        }
    }

    /// <summary>返回：离开设置页时与进入方向相反（从左侧滑回）；其余平级页面用页面刷新式入场。</summary>
    private NavigationTransitionInfo BackTransition()
        => ContentFrame.Content is Pages.Settings.SettingsPage
            ? Motion.FluentMotion.PageSlide(fromRight: false)
            : Motion.FluentMotion.PageEntrance();

    private void UpdateBackButtonState()
    {
        // 有历史：显示返回按钮（Auto 列占位，后续元素自然右移让位）；无历史：折叠隐藏，不显示也不占布局空间。
        // 内部仍保持 IsEnabled 与 CanGoBack 同步（无历史时为 false），保留禁用语义
        bool canGoBack = ContentFrame.CanGoBack;
        BackButton.IsEnabled = canGoBack;

        Visibility target = canGoBack ? Visibility.Visible : Visibility.Collapsed;
        if (BackButton.Visibility == target)
        {
            return;
        }

        BackButton.Visibility = target;

        // 可见性变化同时改变顶栏布局与 Passthrough 命中区域：
        // 先立即重算一次——隐藏时按 Visibility 即刻排除返回按钮旧 rect，不让它继续吞标题拖拽区；
        // 再请求布局完成后的重算——显示时按钮此刻才具备有效尺寸与位置，此时才能正确加入命中区域
        UpdateTitleBarRegions();
        RequestTitleBarRegionsUpdateAfterLayout();
    }

    /// <summary>Frame 内容页类型 → 对应的主导航项（设置页对应 FooterMenuItems 中的原生设置项 NavSettings）。</summary>
    private NavigationViewItem? NavItemForPageType(Type? sourcePageType) =>
        sourcePageType == typeof(Pages.OpenCodePage) ? NavOpenCode :
        sourcePageType == typeof(Pages.Settings.SettingsPage) ? NavSettings :
        null;

    private void SyncNavSelectionWithFrame(Type? sourcePageType)
    {
        NavigationViewItem? target = NavItemForPageType(sourcePageType);

        if (target == null || ReferenceEquals(NavView.SelectedItem, target))
        {
            return;
        }

        // 重入保护：同步选中项会触发 SelectionChanged，须被 _syncingNavSelection 抑制
        // （设置项 Tag=Settings 会命中 SelectionChanged 中的底部项兜底分支，故必须带此抑制）。
        _syncingNavSelection = true;
        try
        {
            _lastNavSelection = target;
            NavView.SelectedItem = target;
        }
        finally
        {
            _syncingNavSelection = false;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        // 账户菜单内的设置入口：与左栏底部设置项同源，进入应用内设置页。
        FlyoutBase.GetAttachedFlyout(NavAccount)?.Hide();
        OpenSettingsPage();
    }

    // ------------------------------------------------------------------
    // 页面事件订阅（随 Frame 导航切换；OpenCodePage：设置 / 会话跟踪 / 执行状态 / 导航结果；
    // SettingsPage：主题与 OpenCode 凭据刷新）
    // ------------------------------------------------------------------

    /// <summary>
    /// Frame 每次实际导航后调用：先解除旧页面订阅，再按类型订阅新页面的
    /// <c>SettingsRequested</c>（OpenCodePage 订阅会话跟踪/执行状态/导航结果；
    /// SettingsPage 订阅主题与 OpenCode 凭据刷新）。
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
                _openCodePageLoaded = false;

                // 注入当前窗口级可见性（fail-closed 默认 false）；窗口后续变化由可见性订阅推送，
                // Loaded 后另有补推（attach 与 Loaded 的先后顺序不保证）。
                openCodePage.NotifyHostWindowVisibility(_openCodeWindowVisible);
                // 页面实例重建后立即注入当前可信身份（含 null）；身份实际变化时页面自行重置卡片。
                SyncOpenCodeEndpointIdentity();
                _subscribedPage = openCodePage;
                break;

            case Pages.Settings.SettingsPage settingsPage:
                settingsPage.ThemeChanged += SettingsPage_ThemeChanged;
                _subscribedPage = settingsPage;
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
                _openCodePageLoaded = false;
                break;

            case Pages.Settings.SettingsPage settingsPage:
                settingsPage.ThemeChanged -= SettingsPage_ThemeChanged;
                break;
        }

        _subscribedPage = null;
    }

    private void Page_SettingsRequested(object? sender, EventArgs e)
    {
        // 页面事件在 UI 线程触发：页面发起的设置入口同样进入应用内设置页。
        OpenSettingsPage();
    }

    private void SetSessionWindowTitle(string? sessionTitle) => Title = string.IsNullOrWhiteSpace(sessionTitle)
        ? "FluentCode" : "FluentCode — " + sessionTitle;

    // ------------------------------------------------------------------
    // 应用内设置页（复用 Frame 导航与返回栈，不再使用 ContentDialog）
    // ------------------------------------------------------------------

    /// <summary>
    /// 进入应用内设置页：以既有 <see cref="ContentFrame"/> 导航到
    /// <see cref="Pages.Settings.SettingsPage"/>，复用原生返回栈（顶栏 BackButton 返回上一页）。
    /// </summary>
    private void OpenSettingsPage()
    {
        // 收起账户菜单，避免其停留在设置页上方。
        FlyoutBase.GetAttachedFlyout(NavAccount)?.Hide();

        // 已在设置页时不重复导航：避免返回栈重复项与重复创建 SettingsView。
        if (ContentFrame.CurrentSourcePageType == typeof(Pages.Settings.SettingsPage))
        {
            return;
        }

        // 选中项无需在此记录：导航完成后由 ContentFrame_Navigated → SyncNavSelectionWithFrame
        // 将 NavSettings 设为选中；取消离开时按当前内容页（设置页）恢复，保持一致。

        ContentFrame.Navigate(typeof(Pages.Settings.SettingsPage), null, Motion.FluentMotion.PageSlide(fromRight: true));
    }

    // ------------------------------------------------------------------
    // 设置页离开保护（未保存输入：保存并离开 / 放弃并离开 / 取消）
    // ------------------------------------------------------------------

    /// <summary>
    /// 在离开设置页的导航真正发生前拦截：若设置页存在未保存输入，取消本次导航并改为页内三态确认，
    /// 用户选择后再重放导航。覆盖顶栏返回与主导航切换（含搜索跳转），不默默丢失。
    /// </summary>
    private void ContentFrame_Navigating(object sender, NavigatingCancelEventArgs e)
    {
        if (_settingsLeaveConfirmed)
        {
            return;
        }

        if (ContentFrame.Content is not Pages.Settings.SettingsPage settingsPage || !settingsPage.HasUnsavedChanges)
        {
            return;
        }

        // 捕获本次导航意图并取消，改为先做页内确认。
        e.Cancel = true;
        _pendingLeaveMode = e.NavigationMode;
        _pendingLeavePageType = e.SourcePageType;
        _pendingLeaveParameter = e.Parameter;

        // 本次导航被取消、内容页仍是设置页：把选中项同步回设置项，避免确认期间选中项与内容页不一致。
        SyncNavSelectionWithFrame(ContentFrame.CurrentSourcePageType);

        _ = PromptLeaveSettingsAsync(settingsPage);
    }

    private async Task PromptLeaveSettingsAsync(Pages.Settings.SettingsPage settingsPage)
    {
        Pages.Settings.SettingsLeaveChoice choice = await settingsPage.ConfirmLeaveAsync();
        switch (choice)
        {
            case Pages.Settings.SettingsLeaveChoice.Cancel:
                RestoreNavSelectionAfterCancelledLeave();
                return;

            case Pages.Settings.SettingsLeaveChoice.Discard:
                settingsPage.DiscardPendingEdits();
                break;

            case Pages.Settings.SettingsLeaveChoice.Save:
                if (!await settingsPage.TrySavePendingEditsAsync())
                {
                    // 保存失败：留在设置页（错误已由设置视图呈现），恢复导航选中项。
                    RestoreNavSelectionAfterCancelledLeave();
                    return;
                }
                break;
        }

        PerformPendingLeave();
    }

    private void PerformPendingLeave()
    {
        _settingsLeaveConfirmed = true;
        try
        {
            if (_pendingLeaveMode == NavigationMode.Back)
            {
                if (ContentFrame.CanGoBack)
                {
                    ContentFrame.GoBack(BackTransition());
                }
            }
            else if (_pendingLeavePageType is not null)
            {
                ContentFrame.Navigate(_pendingLeavePageType, _pendingLeaveParameter, Motion.FluentMotion.PageEntrance());
            }
        }
        finally
        {
            _settingsLeaveConfirmed = false;
        }
    }

    /// <summary>取消离开时，把主导航选中项恢复为与当前内容页（仍为设置页）一致（不触发导航）。</summary>
    private void RestoreNavSelectionAfterCancelledLeave()
    {
        // 内容页未变（设置页），选中项须与内容页一致：按当前 Frame 内容同步（SettingsPage → NavSettings）。
        SyncNavSelectionWithFrame(ContentFrame.CurrentSourcePageType);
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
