using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.Web;
using AIFrontend.Services.Configuration;
using AIFrontend.Services.Contracts;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Protocols;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

/// <summary>
/// 可嵌入的设置界面（<see cref="UserControl"/>，纯 C# 组合原生 WinUI 控件）。
/// </summary>
/// <remarks>
/// <para>范围与依赖：本控件只消费既有服务 <see cref="SettingsService"/> /
/// <see cref="DpapiSecretStore"/> / <see cref="IModelCatalogClient"/>（默认实例为
/// <see cref="ModelCatalogClient"/>），不新增网络或存储层，也不修改任何服务契约。</para>
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
/// <para>持久化模型：结构性变更（provider / 模型 / 选中模型 / 主题）在用户操作时立即保存；
/// OpenCode 连接已迁移到用户入口，此页只保留指引；外部 <c>Changed</c> 刷新时保留未提交输入。
/// 存在未提交输入时离开页面会先经页内三态确认（保存并离开 / 放弃并离开 / 取消），不默默丢失。</para>
/// </remarks>
public sealed partial class SettingsView : UserControl
{
    /// <summary>协议下拉的固定顺序，索引即选项顺序。</summary>
    private static readonly ChatProtocol[] ProtocolOrder =
    {
        ChatProtocol.OpenAiResponses,
        ChatProtocol.OpenAiChatCompletions,
        ChatProtocol.AnthropicMessages,
    };

    private readonly SettingsService _settingsService;
    private readonly DpapiSecretStore _secretStore;
    private readonly Func<WebProvider, Task>? _clearCookiesAsync;
    private readonly bool _ownsCatalog;

    private IModelCatalogClient _modelCatalog;
    private bool _catalogDisposed;

    private CancellationTokenSource _lifetimeCts = new();
    private AppSettings _settings = AppSettings.Empty;
    private bool _unloaded;
    private bool _subscribed;
    private bool _suppressUiEvents;
    private bool _applying;

    // ---- 页面骨架 ----
    private readonly InfoBar _statusBar = new() { IsOpen = false, IsClosable = true };

    // 双栏主体（确认期间整体禁用）：左侧分类列表 + 右侧内容，左右各自滚动。
    private ContentControl _contentHost = null!;
    private ListView _categoryList = null!;
    private TextBlock _categoryTitle = null!;
    private TextBlock _categoryDescription = null!;
    private ContentControl _categoryContentHost = null!;
    private ScrollViewer _categoryScroll = null!;
    private readonly List<CategoryEntry> _categories = new();

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

    // Provider
    private StackPanel _providerListPanel = null!;
    private TextBlock _providerListStatus = null!;
    private TextBlock _providerFormTitle = null!;
    private TextBox _providerIdBox = null!;
    private TextBox _providerNameBox = null!;
    private TextBox _providerUrlBox = null!;
    private ComboBox _providerProtocolCombo = null!;
    private PasswordBox _providerKeyBox = null!;
    private Button _providerSubmitButton = null!;
    private Button _providerCancelButton = null!;
    private TextBlock _providerFormStatus = null!;
    private Expander _providerFormExpander = null!;
    private string? _editingProviderId;

    // 模型
    private ComboBox _selectedModelCombo = null!;
    private StackPanel _modelListPanel = null!;
    private ComboBox _modelProviderCombo = null!;
    private TextBox _modelIdBox = null!;
    private TextBox _modelDisplayNameBox = null!;
    private TextBlock _modelFormStatus = null!;
    private ComboBox _discoverProviderCombo = null!;
    private Button _discoverButton = null!;
    private Button _discoverAddButton = null!;
    private ProgressRing _discoverProgress = null!;
    private TextBlock _discoverStatus = null!;
    private StackPanel _discoverResultsPanel = null!;
    private readonly List<ModelDescriptor> _discoverResults = new();
    private readonly List<CheckBox> _discoverChecks = new();
    private StackPanel _capabilityEditorHost = null!;
    private ModelDescriptor? _capabilityEditingModel;
    private ComboBox _capabilitySourceCombo = null!;
    private TextBox _capabilityEffortBox = null!;
    private CheckBox _capabilityAdaptiveCheck = null!;
    private CheckBox _capabilityEnabledCheck = null!;
    private TextBox _capabilityMinBox = null!;
    private TextBox _capabilityMaxBox = null!;
    private TextBlock _capabilityError = null!;

    // Cookie
    private Button _clearChatGptButton = null!;
    private Button _clearGeminiButton = null!;
    private Button _clearClaudeButton = null!;
    private TextBlock _cookieStatus = null!;

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
    /// <param name="clearCookiesAsync">
    /// 可选：清理指定官网环境 cookie 的回调，应在 UI 线程调用，且不得依赖 <c>NormalPage</c> 处于已加载态
    /// （主窗口经 Pages.Web.WebCookieCleaner 提供）。为 null 时三个“清除 Cookies”按钮禁用并说明原因。
    /// </param>
    /// <param name="modelCatalog">可选的模型发现客户端；为 null 时本控件自建 <see cref="ModelCatalogClient"/> 并在卸载时释放。</param>
    public SettingsView(
        SettingsService settingsService,
        DpapiSecretStore secretStore,
        Func<WebProvider, Task>? clearCookiesAsync = null,
        IModelCatalogClient? modelCatalog = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _clearCookiesAsync = clearCookiesAsync;
        _ownsCatalog = modelCatalog is null;
        _modelCatalog = modelCatalog ?? new ModelCatalogClient();

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

    /// <summary>是否已接入 cookie 清理回调（为 false 时三个清除按钮禁用）。</summary>
    public bool HasCookieClearCallback => _clearCookiesAsync is not null;

    /// <summary>
    /// 是否存在尚未提交的输入（OpenCode 地址/认证方式/用户名/凭据暂存或已兑换未保存的配对结果、
    /// provider 表单、模型表单、模型能力编辑）。供应用内设置页在离开前决定是否提示。
    /// </summary>
    public bool HasUnsavedChanges =>
        !_unloaded &&
        (IsProviderFormDirty() || IsModelFormDirty() || IsCapabilityEditorOpen());

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
            "有未保存的修改",
            "当前设置中存在尚未提交的输入（provider 或模型表单、模型能力编辑）。" +
            "请选择如何处理后再离开；选择“取消”将留在设置页。",
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

        if (IsCapabilityEditorOpen() && !await TrySaveCapabilityEditorAsync())
        {
            return false;
        }

        if (IsProviderFormDirty() && !await TrySubmitProviderAsync())
        {
            return false;
        }

        if (IsModelFormDirty() && !await TryAddModelAsync())
        {
            return false;
        }

        return true;
    }

    /// <summary>放弃全部尚未保存的暂存输入：还原 OpenCode 字段、清空 provider/模型表单、关闭能力编辑器。不写盘、不连接。</summary>
    public void DiscardPendingEdits()
    {
        if (_unloaded)
        {
            return;
        }

        ResetProviderForm();
        ResetModelForm();
        CloseCapabilityEditor();
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

        if (_catalogDisposed)
        {
            _modelCatalog = new ModelCatalogClient();
            _catalogDisposed = false;
        }

        _unloaded = false;

        Subscribe();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;

        // 卸载时结束任何仍待处理的内联确认（按“取消”处理），避免等待它的处理器悬挂。
        CompleteConfirm(ConfirmChoice.Cancel);

        Unsubscribe();
        _lifetimeCts.Cancel();

        if (_ownsCatalog && _modelCatalog is IDisposable disposable)
        {
            disposable.Dispose();
            _catalogDisposed = true;
        }
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
            ApplyThemeToUi();
            ApplyFontsToUi();
            ApplyProvidersToUi();
            ApplyModelsToUi();
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

    /// <summary>设置分类条目：标题、说明与已构建好的内容元素（构建一次，切换分类时复用）。</summary>
    private sealed record CategoryEntry(string Title, string Description, UIElement Content);

    private void BuildLayout()
    {
        _confirmPanel = BuildConfirmPanel();

        var header = new StackPanel { Spacing = 2, Margin = new Thickness(24, 16, 24, 0) };
        header.Children.Add(new TextBlock { Text = "设置", FontSize = 24 });
        header.Children.Add(new TextBlock
        {
            Text = "主题、OpenCode 后端、聊天 provider 与模型、浏览器 Cookie。密钥只以 DPAPI 密文保存。",
            FontSize = 13,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        // 左侧：设置分类列表（可独立滚动）。宽度按内容自适应并设最小宽度，不硬编码固定值。
        _categoryList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_categoryList, "SettingsCategoryList");
        AutomationProperties.SetName(_categoryList, "设置分类");
        _categoryList.SelectionChanged += OnCategorySelectionChanged;

        // 分类内容（各构建一次，切换时复用；说明文本作为分类头说明）。
        _categories.Add(new CategoryEntry(
            "外观",
            "主题（跟随系统 / 浅色 / 深色）与字体（正文 / 衬线 / 等宽）。选择后立即保存并应用到整个应用。",
            BuildThemeSection()));
        _categories.Add(new CategoryEntry(
            "OpenCode 后端",
            "连接设置已移至“用户”→“连接 OpenCode”；现有连接与凭据保留。",
            BuildOpenCodeSection()));
        _categories.Add(new CategoryEntry(
            "聊天 provider",
            "聊天 provider 端点（本地或兼容代理）的添加、编辑、删除与连接测试。API key 仅以 DPAPI 密文保存。",
            BuildProvidersSection()));
        _categories.Add(new CategoryEntry(
            "模型",
            "模型的选中、手动添加/删除、/models 发现加入与能力设置。能力未知时不会默认发送可选思考参数。",
            BuildModelsSection()));
        _categories.Add(new CategoryEntry(
            "浏览器 Cookies",
            "清除各官网环境（独立 WebView2 用户数据目录）的浏览器 cookie；只清 cookie，不删除用户数据目录。",
            BuildCookiesSection()));

        foreach (CategoryEntry category in _categories)
        {
            _categoryList.Items.Add(new ListViewItem { Content = category.Title, Tag = category });
        }

        // 右侧：分类标题/说明（固定）+ 分类内容（独立滚动）。
        _categoryTitle = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.Wrap };
        _categoryDescription = CreateSubtleText(string.Empty);
        _categoryContentHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _categoryScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = _categoryContentHost,
        };

        var rightHeader = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        rightHeader.Children.Add(_categoryTitle);
        rightHeader.Children.Add(_categoryDescription);

        var rightPane = new Grid();
        rightPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(rightHeader, 0);
        Grid.SetRow(_categoryScroll, 1);
        rightPane.Children.Add(rightHeader);
        rightPane.Children.Add(_categoryScroll);

        // 双栏主体：左右各占一列；左列宽度自适应，右列占满剩余宽度。整体受有限高度约束，
        // 因而两侧 ScrollViewer 的 Auto 滚动条按原生行为按需出现（不强行全局禁用）。
        var body = new Grid
        {
            Margin = new Thickness(24, 8, 24, 16),
            ColumnSpacing = 24,
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_categoryList, 0);
        Grid.SetColumn(rightPane, 1);
        body.Children.Add(_categoryList);
        body.Children.Add(rightPane);

        // 其余设置内容置于独立宿主：确认期间整体禁用，近似模态且不遮挡确认区。
        _contentHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = body,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 页头
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 状态条
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 内联确认
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 双栏主体
        Grid.SetRow(header, 0);
        Grid.SetRow(_statusBar, 1);
        Grid.SetRow(_confirmPanel, 2);
        Grid.SetRow(_contentHost, 3);
        _statusBar.Margin = new Thickness(24, 0, 24, 0);
        _confirmPanel.Margin = new Thickness(24, 8, 24, 0);
        root.Children.Add(header);
        root.Children.Add(_statusBar);
        root.Children.Add(_confirmPanel);
        root.Children.Add(_contentHost);

        Content = root;

        // 默认选中首个分类。
        _categoryList.SelectedIndex = 0;
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = _categoryList.SelectedIndex;
        if (index < 0 || index >= _categories.Count)
        {
            return;
        }

        CategoryEntry category = _categories[index];
        _categoryTitle.Text = category.Title;
        _categoryDescription.Text = category.Description;
        _categoryContentHost.Content = category.Content;
        _categoryScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>构建内联确认区（Native InfoBar + 主/次/取消按钮，支持二态与三态确认）。</summary>
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

        _confirmSecondaryButton = new Button { Visibility = Visibility.Collapsed };
        _confirmSecondaryButton.Click += OnConfirmSecondaryClick;
        AutomationProperties.SetAutomationId(_confirmSecondaryButton, "SettingsConfirmSecondary");

        _confirmCancelButton = new Button { Content = "取消" };
        _confirmCancelButton.Click += OnConfirmCancelClick;
        AutomationProperties.SetAutomationId(_confirmCancelButton, "SettingsConfirmCancel");

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttonRow.Children.Add(_confirmPrimaryButton);
        buttonRow.Children.Add(_confirmSecondaryButton);
        buttonRow.Children.Add(_confirmCancelButton);

        _confirmPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        _confirmPanel.Children.Add(_confirmBar);
        _confirmPanel.Children.Add(buttonRow);
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

    private UIElement BuildThemeSection()
    {
        var panel = new StackPanel { Spacing = 8 };

        _themeCombo = new ComboBox { Header = "应用主题外观", MinWidth = 220 };
        _themeCombo.Items.Add("跟随系统（推荐）");
        _themeCombo.Items.Add("浅色");
        _themeCombo.Items.Add("深色");
        AutomationProperties.SetAutomationId(_themeCombo, "SettingsThemeCombo");
        _themeCombo.SelectionChanged += OnThemeSelectionChanged;
        panel.Children.Add(_themeCombo);

        _codeThemeCombo = new ComboBox { Header = "代码配色", MinWidth = 220 };
        _codeThemeCombo.Items.Add("跟随应用（默认）");
        _codeThemeCombo.Items.Add("One Dark Pro");
        AutomationProperties.SetAutomationId(_codeThemeCombo, "SettingsCodeThemeCombo");
        _codeThemeCombo.SelectionChanged += OnCodeThemeSelectionChanged;
        panel.Children.Add(_codeThemeCombo);
        panel.Children.Add(new TextBlock { Text = "仅更改代码与源码区域，不改变应用主题。", TextWrapping = TextWrapping.Wrap });

        // 字体（F11）：见 SettingsView.Fonts.cs。
        panel.Children.Add(BuildFontsSection());

        return panel;
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
        if (!await SaveAsync(settings => settings with { CodeTheme = theme }, "已保存代码配色。"))
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

        if (await SaveAsync(settings => settings with { Theme = theme }, "已保存主题。"))
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

    private async Task<bool> SaveAsync(Func<AppSettings, AppSettings> transform, string successMessage)
    {
        try
        {
            _settings = await _settingsService.UpdateAsync(transform, _lifetimeCts.Token);
            ShowStatus(InfoBarSeverity.Success, successMessage, null);
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

    private async Task<string?> ResolveApiKeyAsync(ProviderEndpoint provider, CancellationToken cancellationToken)
    {
        if (provider.CredentialId is null)
        {
            return null;
        }

        // 不吞凭据读取失败或取消：由调用方按安全类型/HRESULT 呈现，不降级为无密钥请求。
        return await _secretStore.GetAsync(provider.CredentialId, cancellationToken);
    }

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

    private static string DescribeProtocol(ChatProtocol protocol) => protocol switch
    {
        ChatProtocol.OpenAiResponses => "Responses",
        ChatProtocol.OpenAiChatCompletions => "Chat Completions",
        ChatProtocol.AnthropicMessages => "Anthropic Messages",
        _ => protocol.ToString(),
    };

    private static string GenerateProviderId() => "p-" + Guid.NewGuid().ToString("N")[..12];

    private static TextBlock CreateSubtleText(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Opacity = 0.75,
    };

    private void FillProviderCombo(ComboBox combo, string? preferredProviderId)
    {
        combo.Items.Clear();
        int selected = -1;
        for (int i = 0; i < _settings.Providers.Count; i++)
        {
            ProviderEndpoint provider = _settings.Providers[i];
            combo.Items.Add($"{provider.DisplayName}（{provider.Id}）");
            if (preferredProviderId is not null && provider.Id == preferredProviderId)
            {
                selected = i;
            }
        }

        combo.SelectedIndex = selected >= 0 ? selected : (_settings.Providers.Count > 0 ? 0 : -1);
        combo.IsEnabled = _settings.Providers.Count > 0;
    }

    // 存储 ID 校验：与 AppDataPaths.IsValidStorageId 规则一致（服务端仍是权威校验）。
    private static bool IsValidStorageId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id is "." or "..")
        {
            return false;
        }

        foreach (char c in id)
        {
            bool allowed =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '.' || c == '_' || c == '-';
            if (!allowed)
            {
                return false;
            }
        }

        return !IsWindowsReservedDeviceName(id);
    }

    private static bool IsWindowsReservedDeviceName(string id)
    {
        int dot = id.IndexOf('.');
        string stem = dot >= 0 ? id.Substring(0, dot) : id;
        if (stem.Length == 0)
        {
            return false;
        }

        switch (stem.ToUpperInvariant())
        {
            case "CON":
            case "PRN":
            case "AUX":
            case "NUL":
                return true;
        }

        if (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            char digit = stem[3];
            return digit >= '1' && digit <= '9';
        }

        return false;
    }

    private static bool IsAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);
}
