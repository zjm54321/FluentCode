using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.Settings;

/// <summary>
/// 可嵌入的设置界面（<see cref="UserControl"/>，纯 C# 组合原生 WinUI 控件）。
/// </summary>
/// <remarks>
/// <para>范围与依赖：本控件只消费既有服务 <see cref="SettingsService"/> /
/// <see cref="DpapiSecretStore"/>，不新增网络或存储层，也不修改任何服务契约。</para>
/// <para>版式：本控件是**真正的双栏页面**——左侧是可独立滚动的设置分类列表
/// （<see cref="ListView"/>），右侧是所选分类的具体内容与保存/放弃操作，左右各自按需滚动
/// （<see cref="ScrollViewer"/> 的 Auto/Disabled，依赖有限高度约束，不强行全局禁用滚动条）。
/// 不是把长表单直接塞进一个 Frame。</para>
/// <para>线程与生命周期：控件必须在 UI 线程创建。文件加载经 <see cref="LoadAsync"/> 主动发起；
/// <see cref="SettingsService.Changed"/> 无线程亲和保证，本控件统一调度回 UI 线程，并按锁外快照
/// 刷新界面（不调用 <c>LoadAsync</c> 以免再次触发 <c>Changed</c> 形成回环）。</para>
/// <para>安全：API key / 后端 token 只经 <see cref="DpapiSecretStore"/> 以 DPAPI 密文保存，
/// 绝不写入 <c>settings.json</c>、界面状态文本或日志；控件不回显已保存的密钥明文。
/// 凭据相关失败只展示安全类型与 HRESULT，不输出密钥或含请求头的异常文本。</para>
/// <para>持久化模型：主题等即时选项在用户操作时立即保存；会话浏览器设置显式保存；
/// OpenCode 连接已迁移到用户入口，此页只保留指引；外部 <c>Changed</c> 刷新时保留未提交输入。
/// 存在未提交输入时离开页面会先经页内三态确认（保存并离开 / 放弃并离开 / 取消），不默默丢失。</para>
/// </remarks>
public sealed partial class SettingsView : UserControl
{
    private readonly SettingsService _settingsService;
    private readonly DpapiSecretStore _secretStore;
    private CancellationTokenSource _lifetimeCts = new();
    private AppSettings _settings = AppSettings.Empty;
    private bool _unloaded;
    private bool _subscribed;
    private bool _suppressUiEvents;
    private bool _applying;

    // ---- 页面骨架 ----
    private readonly InfoBar _statusBar = new() { IsOpen = false, IsClosable = true };

    // 双栏主体（确认期间整体禁用）：原生 NavigationView 分类导航 + 右侧内容，左右各自滚动。
    private NavigationView _navigation = null!;
    private ContentControl _contentHost = null!;
    private TextBlock _categoryTitle = null!;
    private ContentControl _categoryContentHost = null!;
    private ScrollViewer _categoryScroll = null!;
    private readonly List<CategoryEntry> _categories = new();
    private readonly List<NavigationViewItem> _categoryItems = new();

    // 内联确认区：本控件由应用内设置页（SettingsPage）承载，确认/编辑一律就地内联展开，
    // 不在页面上叠加模态 ContentDialog（保持非模态、不遮挡、无嵌套对话框限制）。
    private StackPanel _confirmPanel = null!;
    private InfoBar _confirmBar = null!;
    private Button _confirmPrimaryButton = null!;
    private Button _confirmSecondaryButton = null!;
    private Button _confirmCancelButton = null!;
    private TaskCompletionSource<ConfirmChoice>? _pendingConfirm;

    // 主题
    private ComboBox _themeCombo = null!;
    private ComboBox _codeThemeCombo = null!;

    /// <summary>
    /// 主题已被成功持久化后触发，供主窗口把主题应用到自己的根元素。
    /// 仅在 <see cref="SettingsService.SaveAsync"/> 成功后触发；订阅方负责实际应用。
    /// </summary>
    public event EventHandler<SettingsThemeChangedEventArgs>? ThemeChanged;

    /// <summary>
    /// 创建设置界面。
    /// </summary>
    /// <param name="settingsService">共享的设置服务（读写 <c>settings.json</c>）。</param>
    /// <param name="secretStore">共享的 DPAPI 凭据存储（保存 API key / 后端 token）。</param>
    public SettingsView(
        SettingsService settingsService,
        DpapiSecretStore secretStore)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAutomationId(this, "SettingsView");
        AutomationProperties.SetName(this, "设置");

        BuildLayout();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>最近一次已加载/已持久化的设置快照。</summary>
    public AppSettings Settings => _settings;

    /// <summary>
    /// 是否存在尚未提交的会话浏览器设置。供应用内设置页在离开前决定是否提示。
    /// </summary>
    public bool HasUnsavedChanges =>
        !_unloaded &&
        (_browserDirty || _mapDirty);

    /// <summary>
    /// 由外层宿主（应用内设置页 <c>SettingsPage</c>）在离开承载本控件的页面后调用：
    /// 结束任何仍在等待的内联确认（按“取消”处理），确保已 <c>await</c> 的处理器不悬挂。
    /// 未处于确认状态时为无操作。本控件卸载时也会自行收尾，此方法为不依赖 <c>Unloaded</c> 时机的外层兜底。
    /// </summary>
    public void CancelPendingInteractions() => CompleteConfirm(ConfirmChoice.Cancel);

    /// <summary>
    /// 离开前的页内三态确认：保存并离开 / 放弃并离开 / 取消。由应用内设置页在导航被拦下时调用。
    /// 不使用模态 <c>ContentDialog</c>。
    /// </summary>
    public async Task<SettingsLeaveChoice> ConfirmLeaveAsync()
    {
        ConfirmChoice choice = await ConfirmCoreAsync(
            "设置尚未保存",
            "离开前要保存这些修改吗？",
            "保存并离开",
            "放弃并离开");

        return choice switch
        {
            ConfirmChoice.Primary => SettingsLeaveChoice.Save,
            ConfirmChoice.Secondary => SettingsLeaveChoice.Discard,
            _ => SettingsLeaveChoice.Cancel,
        };
    }

    /// <summary>
    /// 提交全部尚未保存的暂存输入（能力编辑 → provider 表单 → 模型表单 →
    /// OpenCode 地址/认证方式/凭据或已兑换未保存的配对结果）。
    /// 任一项校验失败即返回 <c>false</c> 并保留界面（调用方据此留在设置页，不默默丢失）。
    /// 注意：配对链接本身不会被自动兑换（避免消耗一次性兑换码）——仅当兑换结果已在内存中时才提交保存。
    /// </summary>
    public async Task<bool> TrySavePendingEditsAsync()
    {
        if (_unloaded)
        {
            return false;
        }

        if (_browserDirty && !await SaveBrowserAsync()) return false;
        if (_mapDirty && !await SaveNativeUiMapAsync()) return false;
        return true;
    }

    /// <summary>放弃全部尚未保存的暂存输入：还原 OpenCode 字段、清空 provider/模型表单、关闭能力编辑器。不写盘、不连接。</summary>
    public void DiscardPendingEdits()
    {
        if (_unloaded)
        {
            return;
        }

        _browserDirty = false; ApplyBrowserToUi();
        _mapDirty = false; ApplyNativeUiMapToUi();
    }

    /// <summary>
    /// 从磁盘加载最新设置并应用到界面。应在挂载后由调用方 await 一次；本方法不发起任何网络请求。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Subscribe();
        try
        {
            AppSettings loaded = await _settingsService.LoadAsync(cancellationToken);
            _settings = loaded;
            ApplySettingsToUi();
        }
        catch (SettingsCorruptedException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "加载设置失败", ex.Message);
        }
        catch (OperationCanceledException)
        {
            // 取消：保持当前界面，不报错。
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "加载设置失败", DescribeSafeError(ex));
        }
    }

    // ---- 生命周期 ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_lifetimeCts.IsCancellationRequested)
        {
            _lifetimeCts.Dispose();
            _lifetimeCts = new CancellationTokenSource();
        }

        _unloaded = false;

        Subscribe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;

        // 卸载时结束任何仍待处理的内联确认（按“取消”处理），避免等待它的处理器悬挂。
        CompleteConfirm(ConfirmChoice.Cancel);
        CloseAccountSection();

        Unsubscribe();
        _lifetimeCts.Cancel();

    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _settingsService.Changed += OnServiceChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _settingsService.Changed -= OnServiceChanged;
        _subscribed = false;
    }

    // ---- SettingsService.Changed ----

    /// <summary>
    /// 服务端变更（本控件自己或其他 lane 的保存）→ 调度到 UI 线程，使用锁外快照刷新界面。
    /// 不调用 <c>LoadAsync</c>：<c>LoadAsync</c> 也会触发 <c>Changed</c>，会造成回环。
    /// <see cref="SettingsService.Current"/> 是本实例的最新快照（契约：其他实例不广播）。
    /// </summary>
    private void OnServiceChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (_unloaded)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (_unloaded)
            {
                return;
            }

            _settings = _settingsService.Current ?? e.Settings;
            ApplySettingsToUi();
            UpdateDisconnectAvailability();
        });
    }

    // ---- 应用快照到界面 ----

    private void ApplySettingsToUi()
    {
        if (_applying)
        {
            return;
        }

        _applying = true;
        _suppressUiEvents = true;
        try
        {
            _runInBackgroundToggle.IsOn = _settings.RunInBackground;
            ApplyMotionToUi();
            ApplyLinkChipsToUi();
            ApplyThemeToUi();
            ApplyFontsToUi();
            _terminalAppearance?.Apply(_settings.TerminalAppearance);
            ApplyBrowserToUi();
            ApplyNativeUiMapToUi();
        }
        finally
        {
            _suppressUiEvents = false;
            _applying = false;
        }
    }

    // ---- 布局（真正的双栏设置页） ----

    /// <summary>内联确认的三种结果（主操作 / 次操作 / 取消）。</summary>
    private enum ConfirmChoice
    {
        Primary,
        Secondary,
        Cancel,
    }

    /// <summary>设置分类：导航图标、标题与已构建好的内容（构建一次，切换分类时复用）。</summary>
    private sealed record CategoryEntry(string Glyph, string Title, UIElement Content, string AutomationId);

    /// <summary>
    /// Windows 11 设置页版式：原生 <see cref="NavigationView"/> 分类导航（图标 + 名称 + 选中指示条 + 标准键盘处理），
    /// 右侧大标题 + 设置卡片组（SettingsCards）。分类：账号与连接 · 外观（主题 / 文字 / 动画） · 终端 · 浏览器 ·
    /// 原生卡片 · 常规（后台运行；应用目前没有通知设置项，故不单列“通知”）。
    /// </summary>
    /// <remarks>
    /// <para><b>固定左栏</b>：<see cref="NavigationViewPaneDisplayMode.Left"/> 让分类栏始终可见并保持 260 DIP 宽，
    /// 与本页原先固定宽度的分类列表一致（Auto 会在窄窗口折叠成只剩图标的紧凑栏，分类名称就看不见了）；
    /// 该模式下 NavigationView 自己也不显示汉堡按钮，所以窗格标题由 <see cref="NavigationView.PaneHeader"/> 承载。</para>
    /// <para><b>不叠底</b>：本控件已位于页面内容层（<c>LayerFillColorDefaultBrush</c>）之内，NavigationView 默认还会
    /// 给内容层再叠一层同色底、给分类栏叠亚克力。这里只做实例级轻量样式覆盖（不改模板、不影响别处），把这两层背景
    /// 与内容层的 1px 描边、圆角去掉，左右连成一片。</para>
    /// </remarks>
    private void BuildLayout()
    {
        _confirmPanel = BuildConfirmPanel();

        _categories.Add(new CategoryEntry("\uE77B", "账号与连接", BuildAccountSection(), "SettingsCategoryAccount"));
        _categories.Add(new CategoryEntry("\uE790", "外观", BuildAppearanceSection(), "SettingsCategoryAppearance"));
        _categories.Add(new CategoryEntry("\uE756", "终端", BuildTerminalSection(), "SettingsCategoryTerminal"));
        _categories.Add(new CategoryEntry("\uE774", "浏览器", BuildBrowserSection(), "SettingsCategoryBrowser"));
        _categories.Add(new CategoryEntry("\uE707", "原生卡片", BuildNativeUiMapSection(), "SettingsCategoryNativeUi"));
        _categories.Add(new CategoryEntry("\uE713", "常规", BuildGeneralSection(), "SettingsCategoryGeneral"));

        _navigation = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneOpen = true,
            OpenPaneLength = 260,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            // 本控件由设置标签承载，不在窗口自定义标题栏内，无需为标题栏预留顶部内缩。
            IsTitleBarAutoPaddingEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_navigation, "SettingsCategoryList");
        AutomationProperties.SetName(_navigation, "设置分类");

        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // 内容层：设置卡片的背景来自卡片自身，导航视图不再画自己的底色/描边/圆角（原来手写版式也没有）。
        SettingsCards.Override(_navigation, "NavigationViewContentBackground", transparent, includeHighContrast: false);
        // 描边与圆角是“合并左右两栏”的观感处理，高对比度下保留系统默认：那里窗格与内容之间需要那条分隔线。
        SettingsCards.Override(_navigation, "NavigationViewContentGridBorderThickness", new Thickness(0), includeHighContrast: false);
        SettingsCards.Override(_navigation, "NavigationViewContentGridCornerRadius", new CornerRadius(0), includeHighContrast: false);
        SettingsCards.Override(_navigation, "NavigationViewBorderThickness", new Thickness(0), includeHighContrast: false);
        // 分类栏：普通主题下与页面同底色（原来是一列普通文字，没有亚克力面板）；高对比度保留系统默认，
        // 由系统提供的分隔与选中态负责区分窗格，不额外压制。
        SettingsCards.Override(_navigation, "NavigationViewDefaultPaneBackground", transparent, includeHighContrast: false);

        // 窗格标题（原来手写的“设置”标题）：高对比度下用系统窗口文字色，随主题变化。
        _navigation.PaneHeader = SettingsCards.Styled(new TextBlock { Text = "设置", Margin = new Thickness(28, 16, 0, 4) }, "SubtitleTextBlockStyle");

        for (int index = 0; index < _categories.Count; index++)
        {
            CategoryEntry category = _categories[index];
            var item = new NavigationViewItem
            {
                Content = category.Title,
                Icon = new FontIcon { Glyph = category.Glyph, FontSize = 16 },
                Tag = category,
            };
            AutomationProperties.SetName(item, category.Title);
            AutomationProperties.SetAutomationId(item, category.AutomationId);
            _categoryItems.Add(item);
            _navigation.MenuItems.Add(item);
        }
        _navigation.SelectionChanged += OnNavigationSelectionChanged;
        // 与原手写 ListView（SingleSelectionFollowsFocus 默认开启）一致：键盘在分类间移动时即切换分类，
        // 不需要再按一次 Enter；窄窗口下分类栏也保持展开（Left 固定模式），所以没有折叠按钮。
        _navigation.SelectionFollowsFocus = NavigationViewSelectionFollowsFocus.Enabled;

        _categoryTitle = SettingsCards.Styled(new TextBlock { Margin = new Thickness(0, 0, 0, 20) }, "TitleTextBlockStyle");
        _categoryContentHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // 切换分类：右侧内容从下方 12px 淡入（ContentThemeTransition 只在合成层动根节点）；减少动画时清空。
        FluentCode.Motion.FluentMotion.BindTransitions(
            _categoryContentHost,
            transitions => _categoryContentHost.ContentTransitions = transitions,
            () => new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
            {
                new Microsoft.UI.Xaml.Media.Animation.ContentThemeTransition { VerticalOffset = 12 },
            });

        var right = new StackPanel();
        right.Children.Add(_categoryTitle);
        right.Children.Add(_statusBar);
        right.Children.Add(_confirmPanel);
        _statusBar.Margin = new Thickness(0, 0, 0, 12);
        _confirmPanel.Margin = new Thickness(0, 0, 0, 12);

        _contentHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _categoryContentHost,
        };
        // 确认期间整体禁用设置内容（近似模态，不遮挡确认区）。
        right.Children.Add(_contentHost);

        // 右侧内容页：Win11 设置页的内边距（左 8 让标题与内容列左缘对齐，右 28 留出滚动条）。
        _categoryScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 20, 28, 32),
            Content = LeftAlignedColumn(right, 1000),
        };
        _navigation.Content = _categoryScroll;

        Content = _navigation;

        // 默认选中首个分类。
        SelectCategory(0);
    }

    /// <summary>
    /// Win11 设置页式内容列：随窗口变宽直到 <paramref name="maxWidth"/>，之后靠左（不居中）。
    /// 不直接在 Stretch 元素上设 MaxWidth：那会在 ScrollViewer 里居中并越过视口右缘被裁切。
    /// </summary>
    private static Grid LeftAlignedColumn(FrameworkElement content, double maxWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        grid.Children.Add(content);
        return grid;
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: CategoryEntry category })
        {
            SelectCategory(_categories.IndexOf(category));
        }
    }

    /// <summary>切换到指定分类（越界或与当前一致时为空操作）：设置标题、内容、滚动条回到顶部。</summary>
    private void SelectCategory(int index)
    {
        if (index < 0 || index >= _categories.Count)
        {
            return;
        }

        // 由代码切换时 SelectionChanged 仍会回来一次，这里先落到 SelectedItem，事件里只会命中同一个分类。
        if (!ReferenceEquals(_navigation.SelectedItem, _categoryItems[index]))
        {
            _navigation.SelectedItem = _categoryItems[index];
        }

        CategoryEntry category = _categories[index];
        _categoryTitle.Text = category.Title;
        _categoryContentHost.Content = category.Content;
        // 标题是无障碍标题结构中的一级标题，与 Windows 11 设置页一致。
        AutomationProperties.SetHeadingLevel(_categoryTitle, AutomationHeadingLevel.Level1);
        _categoryScroll?.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>
    /// 构建内联确认区：原生 <see cref="InfoBar"/> 承载标题/说明，主操作放 <see cref="InfoBar.ActionButton"/>，
    /// 其余按钮（次操作 / 取消）放 <see cref="InfoBar.Content"/>，与提示条成一体而非在下面另起一排浮动按钮。
    /// 支持二态（主 + 取消）与三态（主 + 次 + 取消）确认。仍是非模态：不出对话框、不遮挡、无嵌套对话框限制。
    /// </summary>
    private StackPanel BuildConfirmPanel()
    {
        _confirmBar = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
        };

        _confirmPrimaryButton = new Button();
        _confirmPrimaryButton.Click += OnConfirmPrimaryClick;
        AutomationProperties.SetAutomationId(_confirmPrimaryButton, "SettingsConfirmPrimary");
        _confirmBar.ActionButton = _confirmPrimaryButton;

        _confirmSecondaryButton = new Button { Visibility = Visibility.Collapsed };
        _confirmSecondaryButton.Click += OnConfirmSecondaryClick;
        AutomationProperties.SetAutomationId(_confirmSecondaryButton, "SettingsConfirmSecondary");

        _confirmCancelButton = new Button { Content = "取消" };
        _confirmCancelButton.Click += OnConfirmCancelClick;
        AutomationProperties.SetAutomationId(_confirmCancelButton, "SettingsConfirmCancel");

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(_confirmSecondaryButton);
        buttonRow.Children.Add(_confirmCancelButton);
        _confirmBar.Content = buttonRow;

        _confirmPanel = new StackPanel { Visibility = Visibility.Collapsed };
        _confirmPanel.Children.Add(_confirmBar);
        return _confirmPanel;
    }

    private void OnConfirmPrimaryClick(object sender, RoutedEventArgs e) => CompleteConfirm(ConfirmChoice.Primary);

    private void OnConfirmSecondaryClick(object sender, RoutedEventArgs e) => CompleteConfirm(ConfirmChoice.Secondary);

    private void OnConfirmCancelClick(object sender, RoutedEventArgs e) => CompleteConfirm(ConfirmChoice.Cancel);

    /// <summary>结束当前确认（若存在）：隐藏确认区、恢复内容可用、以结果完成任务。</summary>
    private void CompleteConfirm(ConfirmChoice choice)
    {
        TaskCompletionSource<ConfirmChoice>? pending = _pendingConfirm;
        if (pending is null)
        {
            return;
        }

        _pendingConfirm = null;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _confirmSecondaryButton.Visibility = Visibility.Collapsed;
        _contentHost.IsEnabled = true;
        pending.TrySetResult(choice);
    }

    private void ApplyThemeToUi()
    {
        int index = _settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0,
        };
        _themeCombo.SelectedIndex = index;
        _codeThemeCombo.SelectedIndex = _settings.CodeTheme == AppCodeTheme.OneDarkPro ? 1 : 0;
    }

    private async void OnCodeThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _codeThemeCombo.SelectedIndex < 0) return;
        var theme = _codeThemeCombo.SelectedIndex == 1 ? AppCodeTheme.OneDarkPro : AppCodeTheme.Fluent;
        if (theme == _settings.CodeTheme) return;
        if (!await SaveAsync(settings => settings with { CodeTheme = theme }))
            ApplySettingsToUi();
    }

    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
        {
            return;
        }

        AppTheme theme = _themeCombo.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.System,
        };

        if (theme == _settings.Theme)
        {
            return;
        }

        if (await SaveAsync(settings => settings with { Theme = theme }))
        {
            ThemeChanged?.Invoke(this, new SettingsThemeChangedEventArgs(theme));
        }
        else
        {
            // 保存失败：回滚选择，保持界面与持久化一致。
            ApplySettingsToUi();
        }
    }

    // ---- 通用保存 / 状态 / 校验 ----

    /// <summary>即时保存。成功不显示文字提示（控件本身已反映新值）；失败用 InfoBar 说明并回滚界面。</summary>
    private async Task<bool> SaveAsync(Func<AppSettings, AppSettings> transform)
    {
        try
        {
            _settings = await _settingsService.UpdateAsync(transform, _lifetimeCts.Token);
            if (_statusBar.Severity != InfoBarSeverity.Informational) _statusBar.IsOpen = false;
            ApplySettingsToUi();
            return true;
        }
        catch (SettingsValidationException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "保存失败：配置未通过校验", ex.Message);
            ApplySettingsToUi();
            return false;
        }
        catch (SettingsCorruptedException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "保存失败：现有设置文件损坏，已拒绝覆盖", ex.Message);
            ApplySettingsToUi();
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "保存失败", DescribeSafeError(ex));
            ApplySettingsToUi();
            return false;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string? message)
    {
        RunOnUi(() =>
        {
            _statusBar.Severity = severity;
            _statusBar.Title = title;
            _statusBar.Message = message ?? string.Empty;
            _statusBar.IsOpen = true;
        });
    }

    private void RunOnUi(Action action)
    {
        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }

    /// <summary>
    /// 内联确认核心：在设置界面内部展示确认区并等待用户选择，返回所选项。
    /// 不使用模态 <c>ContentDialog</c>——本控件由应用内设置页承载，确认一律就地内联展开，
    /// 不叠加对话框、不遮挡内容。确认期间禁用其余设置内容以近似模态。
    /// 任一时刻只允许一个待处理确认（并发请求直接按“取消”返回，不弹出、不抛异常）。
    /// 界面卸载或外层宿主调用 <see cref="CancelPendingInteractions"/> 时按“取消”结束，等待方不会悬挂。
    /// </summary>
    private Task<ConfirmChoice> ConfirmCoreAsync(string title, string message, string primaryText, string? secondaryText)
    {
        if (_unloaded || _pendingConfirm is not null)
        {
            return Task.FromResult(ConfirmChoice.Cancel);
        }

        var completion = new TaskCompletionSource<ConfirmChoice>();
        _pendingConfirm = completion;

        _confirmBar.Title = title;
        _confirmBar.Message = message;
        _confirmPrimaryButton.Content = primaryText;
        _confirmSecondaryButton.Content = secondaryText ?? string.Empty;
        _confirmSecondaryButton.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
        _confirmPanel.Visibility = Visibility.Visible;
        _contentHost.IsEnabled = false;

        // 默认聚焦“取消”：与旧确认对话框 DefaultButton=Close 的语义一致，回车不会触发破坏性操作。
        _confirmCancelButton.Focus(FocusState.Programmatic);
        _confirmPanel.StartBringIntoView();

        return completion.Task;
    }

    /// <summary>二态确认（确认 / 取消）：返回 <c>true</c> 仅当用户显式点击主操作按钮。</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
        => await ConfirmCoreAsync(title, message, primaryText, null) == ConfirmChoice.Primary;

    /// <summary>
    /// 把异常渲染为**可安全展示**的文本：凭据存储失败给出阶段与 HRESULT，OpenCode 异常消息为固定端点模板
    /// （不含 token/响应体/请求头），其余异常只给类型名与 HRESULT。绝不输出密钥或含请求头的异常文本。
    /// </summary>
    private static string DescribeSafeError(Exception ex) => ex switch
    {
        SecretStoreException s =>
            $"凭据存储失败（operation={s.Operation ?? "?"}; stage={s.Stage ?? "?"}; " +
            $"type={s.ErrorType ?? ex.GetType().Name}; HRESULT=0x{(s.ErrorHResult ?? ex.HResult):X8}）",
        OpenCodeException => ex.Message,
        _ => $"{ex.GetType().Name}（HRESULT=0x{ex.HResult:X8}）",
    };

}
