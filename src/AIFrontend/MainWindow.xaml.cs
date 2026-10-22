using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.Web;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace AIFrontend;

public sealed partial class MainWindow : Window
{
    private AppWindowTitleBar? _appWindowTitleBar;
    private object? _lastNavSelection;
    private XamlRoot? _hookedXamlRoot;
    private bool _updatingTitleBarRegions;
    private bool _pendingTitleBarRegionsUpdate;
    private bool _syncingNavSelection;
    private object? _subscribedPage;

    // 已解除“设置/加载”订阅、但 Local 运行事件订阅仍暂留（等待其 Local 执行真正终结）的旧 NormalPage 实例。
    private readonly HashSet<Pages.NormalPage> _detachedNormalPages = new();

    // Frame 缓存的 NormalPage 实例（该页 NavigationCacheMode.Required）：主导航离开后仍存活并持有
    // 三个 WebView2 宿主；窗口关闭时即使 Frame 当前停在 OpenCode/Settings，也必须对它做最终释放
    // （见 ReleaseCachedNormalPageWebViews），避免缓存实例带着存活 WebView2 成为孤儿。
    private Pages.NormalPage? _cachedNormalPage;

    // 离开设置页的脏数据保护：拦下导航 → 页内三态确认 → 按选择保存/放弃/取消后重放导航。
    private bool _settingsLeaveConfirmed;
    private NavigationMode _pendingLeaveMode = NavigationMode.New;
    private Type? _pendingLeavePageType;
    private object? _pendingLeaveParameter;

    /// <summary>
    /// 搜索可跳转的导航项（仅既有三项；无全局内容搜索服务，不伪造全局搜索）。
    /// 名称用于匹配与展示，Tag 与 NavigationViewItem.Tag 一致。
    /// </summary>
    private static readonly (string Name, string Tag)[] SearchTargets =
    {
        ("普通界面", "Normal"),
        ("OpenCode", "OpenCode"),
        ("看板", "Board"),
    };

    // ------------------------------------------------------------------
    // Shell 集成接线（见 MainWindow.ShellIntegration / OpenCode / Taskbar / Local 分部）
    // ------------------------------------------------------------------
    // 1) App 事件：OpenCodeSourceChanged（身份注入/换代）、OpenCodeNavigationRequested、
    //    LocalNavigationRequested（Local 完成定位）、OpenCodeInteractionProcessed、
    //    IntegrationDiagnosticChanged——构造期订阅，窗口关闭时退订。
    // 2) Local：LocalTaskStatusChanged → 任务栏唯一执行 ID（SourceRequestId）；LocalTaskCompleted
    //    转发保持（App 已订阅本窗口事件发送完成通知；本窗口不注册、不发送任何通知）。
    // 3) OpenCode 页：SessionTracked / ExecutionStateChanged / NavigationHandled / Loaded——
    //    随 Frame 导航挂接/解除（见 AttachPageEvents / DetachPageEvents），页面重建不重复订阅。
    // 4) 设置页：OpenCodeCredentialsChanged → App.RefreshOpenCodeCredentials()（仅真实保存成功且凭据变更）。

    /// <summary>
    /// 由当前 <see cref="Pages.NormalPage"/> 转发的“Local 一轮真实成功完成”事件
    /// （仅 Completed + Stop 终态；<c>sender</c> 为本窗口，参数原样转发）。
    /// </summary>
    /// <remarks>
    /// 本窗口只做转发：不注册、不发送任何系统通知，也不实现网络逻辑。
    /// App 已订阅本事件（App.Notifications 分部），并以 <c>SourceRequestId</c> 调用
    /// <c>CompletionNotificationService.NotifyCompleted(NotificationSource.LocalApi, …)</c>；
    /// 任务栏状态经 <c>LocalTaskStatusChanged</c> 单独映射，不重复发送完成通知。
    /// </remarks>
    public event EventHandler<Pages.Local.LocalTaskCompletedEventArgs>? LocalTaskCompleted;

    public MainWindow()
    {
        this.InitializeComponent();

        // 用户字体（F11）：NavView 以 ThemeResource 继承正文字体给所有页面内无样式文字；
        // 登记根元素，运行期改字体时由 AppFonts 刷新 ThemeResource（弹出层经 App.xaml 隐式样式覆盖）。
        Services.Appearance.AppFonts.RegisterRoot(RootElement);

        // 启用 WinUI 3 原生 Mica 背景材质
        TrySetMicaBackdrop();

        // 自定义标题栏：内容延伸进标题栏区域；保留系统原生 caption 按钮与默认拖拽区
        SetupCustomTitleBar();

        // 设置初始窗口尺寸
        SetInitialWindowSize(1100, 720);

        // 默认选中“普通界面”并加载对应页面
        NavView.SelectedItem = NavNormal;
        if (ContentFrame.CurrentSourcePageType != typeof(Pages.NormalPage))
        {
            ContentFrame.Navigate(typeof(Pages.NormalPage));
        }

        // 初始无导航历史：确保返回按钮为禁用态
        UpdateBackButtonState();

        // 以 NavigationView 实际 DisplayMode 初始化响应式布局（搜索框 ↔ 搜索按钮）
        UpdateResponsiveLayout();

        // Shell 集成：构造期完成全部 App/窗口订阅，窗口 Activated 后再置 OpenCode 导航 ready；
        // 详见 MainWindow.ShellIntegration.cs / MainWindow.OpenCode.cs / MainWindow.Taskbar.cs / MainWindow.Local.cs。
        InitializeShellIntegration();

        // OpenCode 页 view ACK 的宿主窗口可见性接线（复用 Window/AppWindow 激活与最小化生命周期）。
        InitializeOpenCodeWindowVisibilityTracking();
    }

    /// <summary>
    /// 窗口材质（H15）：Windows 11 用 Mica Alt（<see cref="MicaKind.BaseAlt"/>，官方推荐给带标签页 / 多层内容的应用，
    /// 比 Base 更能透出桌面色调）；不支持 Mica 的系统（Windows 10）回退到 DesktopAcrylic；两者都不支持时保持系统默认。
    /// SystemBackdrop 自动跟随根元素 ActualTheme（浅 / 深 / 跟随系统），无需手工配置。
    /// 根 Grid / 顶栏 / NavigationView 左栏均透明，内容层与面板用 Layer / Card / AcrylicInApp 主题画刷（见 App.xaml），
    /// 因此材质在两种主题下都能透出。
    /// </summary>
    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            this.SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    private void SetupCustomTitleBar()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            _appWindowTitleBar = AppWindow.TitleBar;
            _appWindowTitleBar.ExtendsContentIntoTitleBar = true;

            // 让系统标题栏按钮背景透明以衬出 Mica
            _appWindowTitleBar.ButtonBackgroundColor = Colors.Transparent;
            _appWindowTitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            // 顶栏高 48px 且内含交互控件：Tall 让系统 caption 按钮与顶栏同高（官方对标题栏交互内容的建议）
            _appWindowTitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            // 不调用 Window.SetTitleBar / AppWindowTitleBar.SetDragRectangles：
            // 保留系统默认全宽拖拽区；布局变化（Loaded/SizeChanged）与
            // XamlRoot 变化（DPI-only 缩放等）都会触发重算 caption 保留列宽与交互控件 Passthrough 区域
            AppTitleBar.Loaded += AppTitleBar_Loaded;
            AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;
            this.Closed += MainWindow_Closed;
        }
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        // 官方建议：布局完成后再做首次区域计算，确保元素尺寸已就绪
        UpdateTitleBarRegions();

        // DPI-only 变化（RasterizationScale 改变但 XAML 逻辑尺寸不变）不会触发 SizeChanged：
        // 挂接 XamlRoot.Changed（WinUI 3 稳定事件，随缩放/尺寸/可见性等属性变化触发）兜底重算
        if (_hookedXamlRoot == null && AppTitleBar.XamlRoot is { } xamlRoot)
        {
            _hookedXamlRoot = xamlRoot;
            _hookedXamlRoot.Changed += XamlRoot_Changed;
        }
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // 仅重算区域；UpdateTitleBarRegions 自带重入保护，避免列宽→布局→事件的自激循环
        UpdateTitleBarRegions();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // 窗口关闭：先保守置窗口不可见并解除可见性订阅（在途 view ACK fence 立即失败；页面 Unloaded 另做 Invalidate）。
        ShutdownOpenCodeWindowVisibilityTracking();

        // Shell 集成收尾：置导航 ready=false、退订 App 事件、清空页面可信身份（禁止旧 client 身份继续操作）。
        ShutdownShellIntegration();

        // 窗口关闭时解除 XamlRoot 事件订阅，避免生命周期尾部悬挂
        if (_hookedXamlRoot != null)
        {
            _hookedXamlRoot.Changed -= XamlRoot_Changed;
            _hookedXamlRoot = null;
        }

        // 解除可能挂起的一次性“布局完成后重算”订阅，避免生命周期尾部悬挂
        if (_pendingTitleBarRegionsUpdate)
        {
            AppTitleBar.LayoutUpdated -= TitleBarLayoutUpdatedOnce;
            _pendingTitleBarRegionsUpdate = false;
        }

        // 解除当前页面事件订阅（SettingsRequested / LocalTaskCompleted），避免生命周期尾部悬挂
        DetachPageEvents();

        // 最终释放缓存 NormalPage 的 WebView2 宿主：该页经 NavigationCacheMode.Required 跨主导航保活，
        // 当前 Frame 可能已停在 OpenCode/Settings——仍须关闭其宿主，避免孤儿 WebView2 与进程泄漏。
        ReleaseCachedNormalPageWebViews();

        // 兜底解除仍在等待 Local 真正收尾的旧页面订阅（不泄漏）。
        DetachDeferredNormalPages();
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 官方建议：只在标题栏元素尺寸变化时更新（窗口 Changed 事件可能早于布局更新导致取值错误）
        UpdateTitleBarRegions();
    }

    private void UpdateTitleBarRegions()
    {
        if (_appWindowTitleBar == null || AppTitleBar.XamlRoot == null || _updatingTitleBarRegions)
        {
            return;
        }

        // 重入保护：设置保留列宽会再次触发布局与事件（SizeChanged/XamlRoot.Changed），
        // 同值 GridLength 赋值虽为无操作，仍在最外层兜底，确保不会形成自激循环
        _updatingTitleBarRegions = true;
        try
        {
            // AppWindow 使用物理像素：用 XamlRoot.RasterizationScale 换算为 XAML 逻辑尺寸
            double scale = AppTitleBar.XamlRoot.RasterizationScale;

            // 使用系统实际 caption 保留区宽度（RightInset/LeftInset），而不是猜测的硬编码值
            RightPaddingColumn.Width = new GridLength(_appWindowTitleBar.RightInset / scale);
            LeftPaddingColumn.Width = new GridLength(_appWindowTitleBar.LeftInset / scale);

            // 顶栏内交互元素（返回、汉堡、搜索框/搜索按钮）位于系统 caption 按钮之旁或顶栏中部；
            // 将其区域声明为 Passthrough，使鼠标点击命中控件而不是被标题栏拖拽区域吞掉。
            // 折叠/不可见的元素由 TryGetInteractiveTitleBarRect 自动排除，不残留旧命中区。
            var passthroughRects = new List<RectInt32>(4);
            foreach (FrameworkElement element in new FrameworkElement[] { BackButton, HamburgerButton, SearchBox, SearchButton })
            {
                if (TryGetInteractiveTitleBarRect(element, scale, out var rect))
                {
                    passthroughRects.Add(rect);
                }
            }

            if (passthroughRects.Count == 0)
            {
                return;
            }

            var nonClientInput = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClientInput.SetRegionRects(NonClientRegionKind.Passthrough, passthroughRects.ToArray());
        }
        finally
        {
            _updatingTitleBarRegions = false;
        }
    }

    private static bool TryGetInteractiveTitleBarRect(FrameworkElement element, double scale, out RectInt32 rect)
    {
        rect = default;

        // 折叠（或尚不可见）的元素不参与命中：即使 ActualWidth 仍残留上一轮布局的旧值，
        // 也必须立即排除，否则隐藏后的旧 rect 会继续吞掉标题栏拖拽区域
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        var bounds = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        rect = new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
        return true;
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

    private void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        // 完全依赖 NavigationView 自身的 Auto 阈值判定，不自造不同阈值
        UpdateResponsiveLayout();
    }

    private void NavView_PaneOpened(NavigationView sender, object args) => UpdateResponsiveLayout();

    private void NavView_PaneClosed(NavigationView sender, object args) => UpdateResponsiveLayout();

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
            "Normal" => NavNormal,
            "OpenCode" => NavOpenCode,
            "Board" => NavBoard,
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
            "Normal" => typeof(Pages.NormalPage),
            "OpenCode" => typeof(Pages.OpenCodePage),
            "Board" => typeof(Pages.BoardPage),
            _ => null,
        };
        if (pageType is not null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
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
    /// 齿轮旋转一圈（0→360°）。系统关闭动画偏好时跳过动画（直接打开设置，不阻塞）。
    /// 每次点击重建 RotateTransform，避免上一轮动画的结束值残留导致无法重播。
    /// </summary>
    private void AnimateSettingsGear()
    {
        if (!SystemAnimationsEnabled())
        {
            return;
        }

        try
        {
            SettingsGearIcon.RenderTransform = new RotateTransform();
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(animation, SettingsGearIcon);
            Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(RotateTransform.Angle)");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            // 动画异常不影响设置打开（不向外抛出）
        }
    }

    /// <summary>读取系统“动画效果”偏好（关闭时返回 false）。读取失败时按“允许动画”处理。</summary>
    private static bool SystemAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
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
                    sender.SelectedItem = _lastNavSelection ?? NavNormal;
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
                "Normal" => typeof(Pages.NormalPage),
                "OpenCode" => typeof(Pages.OpenCodePage),
                "Board" => typeof(Pages.BoardPage),
                _ => null
            };

            // 已在目标页时不重复 Navigate，保证返回栈不产生重复项
            if (targetPageType != null && ContentFrame.CurrentSourcePageType != targetPageType)
            {
                ContentFrame.Navigate(targetPageType);
            }
            return;
        }

        // 保证始终有选中项（例如 Ctrl+点击取消选中时恢复上一项）
        if (sender.SelectedItem == null)
        {
            sender.SelectedItem = _lastNavSelection ?? NavNormal;
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        // 每次实际导航（含 GoBack 后退）后更新返回按钮可用态，并把 Frame 当前页回写为主导航选中项；
        // 同时把页面事件订阅切换到新页面实例（旧页面先解除订阅，避免泄漏）
        UpdateBackButtonState();
        SyncNavSelectionWithFrame(e.SourcePageType);
        AttachPageEvents(e.Content);

        // Frame 缓存页（NormalPage）离开主视觉树期间会错过运行期字体刷新：登记为字体根，重新加载时由 AppFonts 延迟补刷（幂等）。
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
            ContentFrame.GoBack();
        }
    }

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

    private void RequestTitleBarRegionsUpdateAfterLayout()
    {
        // 重入保护：布局完成前多次请求（如快速连续前进/后退）只保留一个一次性订阅
        if (_pendingTitleBarRegionsUpdate)
        {
            return;
        }

        _pendingTitleBarRegionsUpdate = true;
        AppTitleBar.LayoutUpdated += TitleBarLayoutUpdatedOnce;
    }

    private void TitleBarLayoutUpdatedOnce(object? sender, object e)
    {
        // LayoutUpdated 在整轮布局（含全部 SizeChanged）之后触发，此时元素尺寸/位置已是终值；
        // 官方语义：sender 恒为 null，不依赖 sender；触发一次后即解除订阅
        AppTitleBar.LayoutUpdated -= TitleBarLayoutUpdatedOnce;
        _pendingTitleBarRegionsUpdate = false;

        UpdateTitleBarRegions();
    }

    /// <summary>Frame 内容页类型 → 对应的主导航项（设置页对应 FooterMenuItems 中的原生设置项 NavSettings）。</summary>
    private NavigationViewItem? NavItemForPageType(Type? sourcePageType) =>
        sourcePageType == typeof(Pages.NormalPage) ? NavNormal :
        sourcePageType == typeof(Pages.OpenCodePage) ? NavOpenCode :
        sourcePageType == typeof(Pages.BoardPage) ? NavBoard :
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
    // 页面事件订阅（随 Frame 导航切换；NormalPage：SettingsRequested / LocalTaskCompleted /
    // LocalTaskStatusChanged；OpenCodePage：SettingsRequested / 会话跟踪 / 执行状态 / 导航结果；
    // SettingsPage：主题与 OpenCode 凭据刷新）
    // ------------------------------------------------------------------

    /// <summary>
    /// Frame 每次实际导航后调用：先解除旧页面订阅，再按类型订阅新页面的
    /// <c>SettingsRequested</c>（NormalPage 另订阅 <c>LocalTaskCompleted</c> 转发与
    /// <c>LocalTaskStatusChanged</c> 任务栏映射；OpenCodePage 订阅会话跟踪/执行状态/导航结果；
    /// SettingsPage 订阅主题与 OpenCode 凭据刷新）。
    /// </summary>
    private void AttachPageEvents(object? content)
    {
        DetachPageEvents();

        switch (content)
        {
            case Pages.NormalPage normalPage:
                // 记录缓存保活实例：窗口关闭时即使 Frame 已停在其他页面也要对其做 WebView2 最终释放。
                _cachedNormalPage = normalPage;

                normalPage.SettingsRequested += Page_SettingsRequested;

                // 缓存实例重入（主导航返回同一实例）：若该页仍登记为“等待 Local 收尾”，其
                // LocalTaskCompleted / LocalTaskStatusChanged 订阅从未解除——仅撤销暂留登记与收尾回调，
                // 不重复订阅（防事件重复转发、重复通知）；否则按常规成对订阅。
                if (_detachedNormalPages.Remove(normalPage))
                {
                    normalPage.LocalTeardownCompleted -= NormalPage_LocalTeardownCompleted;
                }
                else
                {
                    normalPage.LocalTaskCompleted += NormalPage_LocalTaskCompleted;
                    normalPage.LocalTaskStatusChanged += NormalPage_LocalTaskStatusChanged;
                }

                _subscribedPage = normalPage;
                break;

            case Pages.OpenCodePage openCodePage:
                openCodePage.SettingsRequested += Page_SettingsRequested;
                openCodePage.SessionTracked += OpenCodePage_SessionTracked;
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
                settingsPage.OpenCodeCredentialsChanged += SettingsPage_OpenCodeCredentialsChanged;
                _subscribedPage = settingsPage;
                break;
        }
    }

    /// <summary>解除当前已订阅页面的事件（无订阅时为无操作）。</summary>
    private void DetachPageEvents()
    {
        switch (_subscribedPage)
        {
            case Pages.NormalPage normalPage:
                normalPage.SettingsRequested -= Page_SettingsRequested;
                // Local 运行事件订阅不在此立即解除：若该页仍有在途 Local 执行（页面切换即取消），
                // 其终态必须在真正终结后经本窗口转发给任务栏，否则 Working 会滞留。改由 LocalTeardownCompleted 驱动退订。
                if (normalPage.IsLocalTeardownComplete)
                {
                    normalPage.LocalTaskCompleted -= NormalPage_LocalTaskCompleted;
                    normalPage.LocalTaskStatusChanged -= NormalPage_LocalTaskStatusChanged;
                }
                else if (_detachedNormalPages.Add(normalPage))
                {
                    // Add 去重：同一缓存实例可被反复挂接/解除，重复进入暂留态时不重复挂收尾回调。
                    normalPage.LocalTeardownCompleted += NormalPage_LocalTeardownCompleted;
                }

                break;

            case Pages.OpenCodePage openCodePage:
                openCodePage.SettingsRequested -= Page_SettingsRequested;
                openCodePage.SessionTracked -= OpenCodePage_SessionTracked;
                openCodePage.ExecutionStateChanged -= OpenCodePage_ExecutionStateChanged;
                openCodePage.NavigationHandled -= OpenCodePage_NavigationHandled;
                openCodePage.Loaded -= OpenCodePage_ShellLoaded;
                _openCodePageLoaded = false;
                break;

            case Pages.Settings.SettingsPage settingsPage:
                settingsPage.ThemeChanged -= SettingsPage_ThemeChanged;
                settingsPage.OpenCodeCredentialsChanged -= SettingsPage_OpenCodeCredentialsChanged;
                break;
        }

        _subscribedPage = null;
    }

    /// <summary>
    /// 旧 NormalPage 的在途 Local 执行真正终结：此时才安全解除对其 Local 运行事件的订阅（避免取消终态丢失/订阅泄漏）。
    /// </summary>
    private void NormalPage_LocalTeardownCompleted(object? sender, EventArgs e)
    {
        if (sender is not Pages.NormalPage page)
        {
            return;
        }

        page.LocalTeardownCompleted -= NormalPage_LocalTeardownCompleted;

        // 该页若已重新成为当前订阅页（缓存实例在收尾完成前被重新挂接），其 Local 运行订阅是现役的，
        // 不得解除；仅清除暂留登记（重挂接路径见 AttachPageEvents）。
        if (!ReferenceEquals(_subscribedPage, page))
        {
            page.LocalTaskCompleted -= NormalPage_LocalTaskCompleted;
            page.LocalTaskStatusChanged -= NormalPage_LocalTaskStatusChanged;
        }

        _detachedNormalPages.Remove(page);
    }

    /// <summary>窗口关闭时兜底解除所有仍在等待 Local 收尾的旧页面订阅（不泄漏）。</summary>
    private void DetachDeferredNormalPages()
    {
        foreach (Pages.NormalPage page in _detachedNormalPages)
        {
            page.LocalTeardownCompleted -= NormalPage_LocalTeardownCompleted;
            page.LocalTaskCompleted -= NormalPage_LocalTaskCompleted;
            page.LocalTaskStatusChanged -= NormalPage_LocalTaskStatusChanged;
        }

        _detachedNormalPages.Clear();
    }

    /// <summary>
    /// 窗口最终关闭：对缓存保活的 NormalPage 实例做 WebView2 最终释放
    /// （<see cref="Pages.NormalPage.ShutdownWebViews"/>，幂等、须 UI 线程），并兜底释放仍登记为
    /// “等待 Local 收尾”的暂留实例。释放后清空缓存引用；在途激活（通知导航等）不得重开宿主
    /// （<see cref="ShutdownShellIntegration"/> 已先行取消其生命周期与 ready 门控）。
    /// </summary>
    private void ReleaseCachedNormalPageWebViews()
    {
        _cachedNormalPage?.ShutdownWebViews();
        _cachedNormalPage = null;

        // 兜底：Required 缓存下暂留实例通常即缓存实例（ShutdownWebViews 幂等）；即使出现不同实例也不留孤儿。
        foreach (Pages.NormalPage page in _detachedNormalPages)
        {
            page.ShutdownWebViews();
        }
    }

    private void Page_SettingsRequested(object? sender, EventArgs e)
    {
        // 页面事件在 UI 线程触发：页面发起的设置入口同样进入应用内设置页。
        OpenSettingsPage();
    }

    private void NormalPage_LocalTaskCompleted(object? sender, Pages.Local.LocalTaskCompletedEventArgs e)
    {
        // 仅转发：本窗口不注册、不发送任何系统通知（App 已订阅本公开事件发送完成通知）。
        LocalTaskCompleted?.Invoke(this, e);
    }

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

        // Cookie 清理回调：由本窗口提供，委托 Pages.Web.WebCookieCleaner（以 TransientCookieHostLayer
        // 为已加载容器）清理目标官网环境的 cookie；不依赖 NormalPage 是否处于加载态（内嵌设置页显示
        // 期间该页已卸载），只清 cookie、不删除用户数据目录、不对已卸载的 WebView2 调用。回调在 UI 线程执行。
        ContentFrame.Navigate(
            typeof(Pages.Settings.SettingsPage),
            (Func<WebProvider, Task>)ClearCookiesForProviderAsync);
    }

    // ------------------------------------------------------------------
    // 设置页 Cookie 清理（委托 WebCookieCleaner，以窗口隐藏宿主层为容器；不依赖 NormalPage 加载态）
    // ------------------------------------------------------------------

    /// <summary>
    /// 清理指定官网环境的全部 cookie：委托 Web 范围的 <see cref="WebCookieCleaner"/>（cla-10 已完成的
    /// 未挂载态安全清理接口），以本窗口的隐藏宿主层 <c>TransientCookieHostLayer</c> 作为“已加载容器”；
    /// 由其内部临时挂载 1×1 <see cref="WebViewHost"/>、执行清理后立即关闭并移除。
    /// 只清 cookie，不删除用户数据目录，也不清除其他站点数据；不依赖 <c>NormalPage</c> 处于已加载态，
    /// 也不对已卸载/已关闭的 WebView2 调用。必须在 UI 线程调用（由 SettingsView 在 UI 线程触发）。
    /// </summary>
    private Task ClearCookiesForProviderAsync(WebProvider provider)
        => WebCookieCleaner.ClearCookiesAsync(TransientCookieHostLayer, provider);

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
                    ContentFrame.GoBack();
                }
            }
            else if (_pendingLeavePageType is not null)
            {
                ContentFrame.Navigate(_pendingLeavePageType, _pendingLeaveParameter);
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

    /// <summary>把持久化的应用主题应用到根元素（System → Default，跟随系统）。</summary>
    private void ApplyTheme(AppTheme theme)
    {
        RootElement.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }
}
