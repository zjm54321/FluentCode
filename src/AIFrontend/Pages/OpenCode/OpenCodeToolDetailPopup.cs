using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace AIFrontend.Pages.OpenCode;

/// <summary>工具详情弹出层的数据来源（工具行）。</summary>
internal interface IOpenCodeToolDetailSource
{
    OpenCodeToolRowModel DetailModel { get; }

    FrameworkElement DetailAnchor { get; }

    /// <summary>弹出层显示/固定（大视图）/关闭状态变化（行据此显示“已选中”强调条）。</summary>
    void OnDetailStateChanged(bool shown, bool pinned);
}

/// <summary>
/// 通用详情来源的标题信息（思考、子会话预览、提示条等非工具来源）。
/// <paramref name="Glyph"/> 为 Segoe Fluent Icons 字形；<paramref name="StatusBrushKey"/> 为内置画刷键
/// （例如 <see cref="OpenCodeToolTheme.Success"/> / <see cref="OpenCodeToolTheme.Critical"/>），null = 次要文字色。
/// </summary>
internal sealed record OpenCodeDetailHeader(
    string Glyph,
    string Title,
    string Target = "",
    string StatusText = "",
    string? StatusBrushKey = null,
    string DurationText = "",
    string AccessibleName = "");

/// <summary>
/// 通用详情来源（与工具行共用同一个弹出层：悬停小预览、点击大详情视图、Esc / ✕ / 点击外部关闭）。
/// 内容由来源自行构建；可用 <see cref="OpenCodeToolDetailContent.TextView"/> /
/// <see cref="OpenCodeToolDetailContent.Wrap"/> / <see cref="OpenCodeToolDetailContent.PropertyList"/> 组装。
/// <see cref="BuildDetailContent"/> 在预览与大视图中各调用一次（每次返回新元素树）。
/// 同一个类不要同时实现本接口与 <see cref="IOpenCodeToolDetailSource"/>（弹出层方法按接口重载）。
/// </summary>
internal interface IOpenCodeDetailContentSource
{
    FrameworkElement DetailAnchor { get; }

    OpenCodeDetailHeader DetailHeader { get; }

    /// <summary>构建内容（显示时与 <see cref="OpenCodeToolDetailPopup.Refresh(IOpenCodeDetailContentSource)"/> 时调用）。</summary>
    OpenCodeToolDetailView BuildDetailContent();

    void OnDetailStateChanged(bool shown, bool pinned);
}

/// <summary>
/// 工具 / 子代理 / 思考等详情弹出层（G7）：全局同一时刻只有一个，两种形态——
/// <list type="bullet">
/// <item><b>悬停预览</b>（AutomationId <c>OpenCodeToolDetailPreview</c>）：鼠标停留 <see cref="HoverOpenDelayMs"/> 后显示的小浮层
/// （原生 <see cref="FlyoutPresenter"/>：亚克力、Flyout 描边、OverlayCornerRadius + ThemeShadow），紧贴来源行，<b>没有关闭按钮</b>；
/// 鼠标离开行即隐藏（移入预览可保持；行与预览都离开即隐藏）。不移动键盘焦点。在预览内点击 = 打开大视图。</item>
/// <item><b>大详情视图</b>（AutomationId <c>OpenCodeToolDetailPopup</c>，遮罩 <c>OpenCodeToolDetailOverlay</c>）：点击行 / Enter / Space 打开，
/// 约为应用窗口的 80%、居中，系统遮罩层（SmokeFillColorDefaultBrush）+ 原生 FlyoutPresenter 表面，内容完整（不截断）纵向滚动；
/// ✕ 按钮 / Esc / 点击遮罩关闭，随窗口大小改变；打开时焦点移入（Tab 在视图内循环），关闭后还给原焦点（与 ContentDialog 一致）。</item>
/// </list>
/// 所有详情文字默认换行（G8），没有“换行”开关；不提供复制按钮（F2）。
/// </summary>
internal sealed class OpenCodeToolDetailPopup
{
    /// <summary>首次悬停到弹出预览的延迟（毫秒）；用户要求约 1–2 秒，避免鼠标划过即弹出。</summary>
    internal const int HoverOpenDelayMs = 1200;

    /// <summary>预览已打开时悬停切换到另一行的延迟（毫秒）。</summary>
    internal const int HoverSwitchDelayMs = 300;

    private const string SmokeBrushKey = "SmokeFillColorDefaultBrush";

    private static OpenCodeToolDetailPopup? _shared;

    // 悬停预览。
    private readonly Popup _previewPopup;
    private readonly FlyoutPresenter _previewPresenter;
    private readonly Chrome _previewChrome;

    // 大详情视图。
    private readonly Popup _dialogPopup;
    private readonly Grid _overlay;
    private readonly Border _smoke;
    private readonly FlyoutPresenter _dialogPresenter;
    private readonly Chrome _dialogChrome;
    private readonly ToggleButton _viewModeButton;
    private readonly Button _closeButton;

    private readonly OpenCodeDetailHoverTracker _hover = new();

    private DispatcherQueueTimer? _hoverTimer;
    private DispatcherQueueTimer? _closeTimer;

    /// <summary>当前来源：<see cref="IOpenCodeToolDetailSource"/> 或 <see cref="IOpenCodeDetailContentSource"/>。</summary>
    private object? _source;
    private object? _pendingSource;
    private OpenCodeDetailPopupMode _mode;
    private OpenCodeToolDetailOptions _options = new();
    private ScrollViewer? _scroller;
    private FrameworkElement? _previewAnchor;
    private XamlRoot? _observedRoot;
    private UIElement? _restoreFocus;
    private bool _updatingToggles;

    private OpenCodeToolDetailPopup()
    {
        // ---------------- 悬停预览 ----------------
        _previewChrome = new Chrome(large: false);
        var hint = OpenCodeInteractionUi.StyledText("点击查看完整内容", OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
        hint.TextWrapping = TextWrapping.Wrap;
        var previewLayout = new Grid { RowSpacing = 8 };
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(previewLayout, _previewChrome.TitleBar, 0);
        AddRow(previewLayout, _previewChrome.Target, 1);
        AddRow(previewLayout, _previewChrome.ContentHost, 2);
        AddRow(previewLayout, hint, 3);

        _previewPresenter = CreatePresenter(previewLayout, new Thickness(12, 10, 12, 10), 32);
        AutomationProperties.SetAutomationId(_previewPresenter, "OpenCodeToolDetailPreview");
        _previewPresenter.PointerEntered += (_, _) =>
        {
            _hover.EnterPreview();
            _closeTimer?.Stop();
        };
        _previewPresenter.PointerExited += (_, _) =>
        {
            _hover.ExitPreview();
            ScheduleHoverHide();
        };
        // 预览内点击（未被链接等子控件处理的轻点）= 打开大视图；滚轮仍可在预览内滚动。
        _previewPresenter.Tapped += (_, e) =>
        {
            if (_mode == OpenCodeDetailPopupMode.Preview && _source is { } source)
            {
                e.Handled = true;
                Show(source, OpenCodeDetailPopupMode.Large);
            }
        };
        _previewPresenter.SizeChanged += (_, _) => PlacePreview();

        _previewPopup = new Popup
        {
            Child = _previewPresenter,
            IsLightDismissEnabled = false,
            ChildTransitions = new TransitionCollection { new PopupThemeTransition() },
        };

        // ---------------- 大详情视图 ----------------
        _dialogChrome = new Chrome(large: true);

        _viewModeButton = new ToggleButton { Content = "统一视图" };
        AutomationProperties.SetAutomationId(_viewModeButton, "OpenCodeToolDetailViewMode");
        AutomationProperties.SetName(_viewModeButton, "并排 / 统一视图");
        _viewModeButton.Click += (_, _) =>
        {
            if (_updatingToggles)
            {
                return;
            }

            _options.Unified = _viewModeButton.IsChecked == true;
            Rebuild(keepScroll: false);
        };

        _closeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 14 },
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAutomationId(_closeButton, "OpenCodeToolDetailClose");
        AutomationProperties.SetName(_closeButton, "关闭详情");
        ToolTipService.SetToolTip(_closeButton, "关闭（Esc）");
        _closeButton.Click += (_, _) => Close();

        _dialogChrome.Actions.Children.Add(_viewModeButton);
        _dialogChrome.Actions.Children.Add(_closeButton);

        var dialogLayout = new Grid { RowSpacing = 12 };
        dialogLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialogLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialogLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AddRow(dialogLayout, _dialogChrome.TitleBar, 0);
        AddRow(dialogLayout, _dialogChrome.Target, 1);
        AddRow(dialogLayout, _dialogChrome.ContentHost, 2);

        // ContentDialog 同级的阴影高度与 24px 内边距。
        _dialogPresenter = CreatePresenter(dialogLayout, new Thickness(24, 16, 16, 24), 128);
        _dialogPresenter.HorizontalAlignment = HorizontalAlignment.Center;
        _dialogPresenter.VerticalAlignment = VerticalAlignment.Center;
        _dialogPresenter.Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 24, IsStaggeringEnabled = false },
        };
        AutomationProperties.SetAutomationId(_dialogPresenter, "OpenCodeToolDetailPopup");

        // 系统遮罩层（与 ContentDialog 的 SmokeFill 同一画刷，ThemeResource 跟随主题）；点击遮罩 = 关闭。
        _smoke = new Border();
        OpenCodeToolTheme.Apply(_smoke, OpenCodeToolTheme.Border(SmokeBrushKey));
        AutomationProperties.SetAutomationId(_smoke, "OpenCodeToolDetailSmoke");
        AutomationProperties.SetName(_smoke, "关闭详情");
        AutomationProperties.SetAccessibilityView(_smoke, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _smoke.Tapped += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        _overlay = new Grid { TabFocusNavigation = KeyboardNavigationMode.Cycle };
        _overlay.Children.Add(_smoke);
        _overlay.Children.Add(_dialogPresenter);
        AutomationProperties.SetAutomationId(_overlay, "OpenCodeToolDetailOverlay");
        _overlay.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        _dialogPopup = new Popup
        {
            Child = _overlay,
            IsLightDismissEnabled = false,
        };
    }

    internal static OpenCodeToolDetailPopup Shared => _shared ??= new OpenCodeToolDetailPopup();

    private static readonly ConditionalWeakTable<DependencyObject, object> SuppressedRoots = new();

    internal static void SetSuppressDetails(DependencyObject element, bool value)
    {
        SuppressedRoots.Remove(element);
        if (value) SuppressedRoots.Add(element, new object());
    }

    private static bool DetailsSuppressed(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (SuppressedRoots.TryGetValue(node, out _)) return true;
        return false;
    }

    /// <summary>当前形态（预览 / 大视图 / 未显示）。</summary>
    internal OpenCodeDetailPopupMode Mode => _mode;

    // ------------------------------------------------------------------
    // 工具行来源（既有 API，签名不变）
    // ------------------------------------------------------------------

    /// <summary>当前是否为某来源显示（预览或大视图）。</summary>
    internal bool IsShowing(IOpenCodeToolDetailSource source) => IsShowingCore(source);

    /// <summary>当前是否为某来源显示大详情视图（“固定”）。</summary>
    internal bool IsPinned(IOpenCodeToolDetailSource source) => IsShowingCore(source) && _mode == OpenCodeDetailPopupMode.Large;

    /// <summary>鼠标进入工具行：停留 <see cref="HoverOpenDelayMs"/> 后预览（已在预览时 <see cref="HoverSwitchDelayMs"/> 切换）；大视图打开时不打扰。</summary>
    internal void HoverEnter(IOpenCodeToolDetailSource source) => HoverEnterCore(source);

    /// <summary>鼠标离开工具行：取消待显示；预览态下若鼠标未进入预览则立即隐藏。</summary>
    internal void HoverExit(IOpenCodeToolDetailSource source) => HoverExitCore(source);

    /// <summary>点击行 / Enter / Space：打开大详情视图；已为该行打开时再次触发则关闭。</summary>
    internal void TogglePinned(IOpenCodeToolDetailSource source) => TogglePinnedCore(source);

    /// <summary>来源数据更新（轮询）：正在显示时刷新内容并保持滚动位置（位于底部时跟随）。</summary>
    internal void Refresh(IOpenCodeToolDetailSource source) => RefreshCore(source);

    /// <summary>来源被移除 / 停用：若正为其显示则关闭。</summary>
    internal void CloseIf(IOpenCodeToolDetailSource source) => CloseIfCore(source);

    // ------------------------------------------------------------------
    // 通用来源（思考文本、子会话预览、提示条等；行为与工具行完全一致）
    // ------------------------------------------------------------------

    internal bool IsShowing(IOpenCodeDetailContentSource source) => IsShowingCore(source);

    internal bool IsPinned(IOpenCodeDetailContentSource source) => IsShowingCore(source) && _mode == OpenCodeDetailPopupMode.Large;

    internal void HoverEnter(IOpenCodeDetailContentSource source) => HoverEnterCore(source);

    internal void HoverExit(IOpenCodeDetailContentSource source) => HoverExitCore(source);

    internal void TogglePinned(IOpenCodeDetailContentSource source) => TogglePinnedCore(source);

    internal void Refresh(IOpenCodeDetailContentSource source) => RefreshCore(source);

    internal void CloseIf(IOpenCodeDetailContentSource source) => CloseIfCore(source);

    /// <summary>关闭预览或大视图（大视图关闭后把焦点还给打开前的元素）。</summary>
    internal void Close()
    {
        _hoverTimer?.Stop();
        _closeTimer?.Stop();
        _pendingSource = null;
        var wasLarge = _mode == OpenCodeDetailPopupMode.Large;
        var restore = _restoreFocus;
        _restoreFocus = null;

        if (_previewPopup.IsOpen)
        {
            _previewPopup.IsOpen = false;
        }

        if (_dialogPopup.IsOpen)
        {
            _dialogPopup.IsOpen = false;
        }

        ObserveRoot(null);
        var previous = _source;
        _source = null;
        _mode = OpenCodeDetailPopupMode.None;
        _scroller = null;
        _previewAnchor = null;
        _previewChrome.ContentHost.Child = null;
        _dialogChrome.ContentHost.Child = null;
        _hover.Reset();
        Notify(previous, false, false);

        if (wasLarge && restore is not null && restore.XamlRoot is not null)
        {
            try
            {
                restore.Focus(FocusState.Programmatic);
            }
            catch (Exception)
            {
                // 原焦点元素已失效：不抛出。
            }
        }
    }

    private bool IsShowingCore(object source) => _mode != OpenCodeDetailPopupMode.None && ReferenceEquals(_source, source);

    private void HoverEnterCore(object source)
    {
        if (DetailsSuppressed(AnchorOf(source))) return;
        if (_mode == OpenCodeDetailPopupMode.Large)
        {
            return;
        }

        if (IsShowingCore(source))
        {
            _hover.EnterAnchor();
            _closeTimer?.Stop();
            return;
        }

        _pendingSource = source;
        var timer = EnsureHoverTimer();
        if (timer is null)
        {
            return;
        }

        // 用户要求（wave3 反馈）：鼠标停留约 1–2 秒才弹出预览；已有预览打开时切换到相邻行仍较快。
        timer.Interval = TimeSpan.FromMilliseconds(_mode == OpenCodeDetailPopupMode.Preview ? HoverSwitchDelayMs : HoverOpenDelayMs);
        timer.Stop();
        timer.Start();
    }

    private void HoverExitCore(object source)
    {
        if (ReferenceEquals(_pendingSource, source))
        {
            _pendingSource = null;
            _hoverTimer?.Stop();
        }

        if (_mode == OpenCodeDetailPopupMode.Preview && ReferenceEquals(_source, source))
        {
            _hover.ExitAnchor();
            ScheduleHoverHide();
        }
    }

    private void TogglePinnedCore(object source)
    {
        _hoverTimer?.Stop();
        _pendingSource = null;
        if (IsShowingCore(source) && _mode == OpenCodeDetailPopupMode.Large)
        {
            Close();
            return;
        }

        Show(source, OpenCodeDetailPopupMode.Large);
    }

    private void RefreshCore(object source)
    {
        if (!IsShowingCore(source))
        {
            return;
        }

        UpdateTitle(source);
        Rebuild(keepScroll: true);
    }

    private void CloseIfCore(object source)
    {
        if (ReferenceEquals(_pendingSource, source))
        {
            _pendingSource = null;
            _hoverTimer?.Stop();
        }

        if (ReferenceEquals(_source, source))
        {
            Close();
        }
    }

    private static FrameworkElement AnchorOf(object source) => source switch
    {
        IOpenCodeToolDetailSource tool => tool.DetailAnchor,
        IOpenCodeDetailContentSource content => content.DetailAnchor,
        _ => throw new ArgumentException("Unsupported detail source.", nameof(source)),
    };

    private static void Notify(object? source, bool shown, bool pinned)
    {
        switch (source)
        {
            case IOpenCodeToolDetailSource tool:
                tool.OnDetailStateChanged(shown, pinned);
                break;
            case IOpenCodeDetailContentSource content:
                content.OnDetailStateChanged(shown, pinned);
                break;
        }
    }

    private Chrome ActiveChrome => _mode == OpenCodeDetailPopupMode.Large ? _dialogChrome : _previewChrome;

    private void Show(object source, OpenCodeDetailPopupMode mode)
    {
        if (DetailsSuppressed(AnchorOf(source))) return;
        if (mode == OpenCodeDetailPopupMode.Large && source is IOpenCodeChildChatSource child)
        {
            Close();
            child.OpenChildChat();
            return;
        }

        var anchor = AnchorOf(source);
        if (anchor.XamlRoot is not { } root)
        {
            return;
        }

        var previous = _source;
        var previousMode = _mode;
        if (!ReferenceEquals(previous, source))
        {
            _options = new OpenCodeToolDetailOptions { Unified = _options.Unified };
            _source = source;
            Notify(previous, false, false);
        }

        _closeTimer?.Stop();
        _mode = mode;
        _scroller = null;
        _previewChrome.ContentHost.Child = null;
        _dialogChrome.ContentHost.Child = null;

        if (mode == OpenCodeDetailPopupMode.Large)
        {
            // 大视图展示完整内容（不截断；长列表由 ItemsRepeater 虚拟化）。
            _options.ShowAll = true;
            _hover.Reset();
            if (_previewPopup.IsOpen)
            {
                _previewPopup.IsOpen = false;
            }

            _previewAnchor = null;
            if (previousMode != OpenCodeDetailPopupMode.Large)
            {
                _restoreFocus = FocusManager.GetFocusedElement(root) as UIElement;
            }

            UpdateTitle(source);
            Rebuild(keepScroll: false);
            _dialogPopup.XamlRoot = root;
            ObserveRoot(root);
            LayoutLarge(root.Size);
            if (!_dialogPopup.IsOpen)
            {
                _dialogPopup.IsOpen = true;
            }

            // 与 ContentDialog 一致：焦点移入视图（Esc 可用、Tab 在视图内循环），关闭时还给原焦点。
            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null || !queue.TryEnqueue(DispatcherQueuePriority.Low, FocusCloseButton))
            {
                FocusCloseButton();
            }
        }
        else
        {
            _hover.Reset();
            _hover.EnterAnchor();
            _previewAnchor = anchor;
            UpdateTitle(source);
            Rebuild(keepScroll: false);
            _previewPopup.XamlRoot = root;
            var width = OpenCodeDetailPopupLayout.PreviewWidth(root.Size.Width);
            _previewPresenter.Width = width;
            _previewPresenter.MaxHeight = OpenCodeDetailPopupLayout.PreviewHeightLimit(root.Size.Height);
            PlacePreview();
            if (!_previewPopup.IsOpen)
            {
                _previewPopup.IsOpen = true;
            }
        }

        Notify(source, true, mode == OpenCodeDetailPopupMode.Large);
    }

    private void FocusCloseButton()
    {
        if (_mode == OpenCodeDetailPopupMode.Large && _dialogPopup.IsOpen)
        {
            _closeButton.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateTitle(object source)
    {
        var chrome = ActiveChrome;
        if (source is IOpenCodeToolDetailSource tool)
        {
            UpdateToolTitle(chrome, tool.DetailModel);
            return;
        }

        if (source is not IOpenCodeDetailContentSource content)
        {
            return;
        }

        var header = content.DetailHeader;
        chrome.Set(header.Glyph, header.Title, header.Target, header.StatusText, header.StatusBrushKey ?? OpenCodeToolTheme.TextSecondary, header.DurationText);
        _viewModeButton.Visibility = Visibility.Collapsed;
        var name = "详情：" + (header.AccessibleName.Length > 0 ? header.AccessibleName : header.Title);
        AutomationProperties.SetName(_dialogPresenter, name);
        AutomationProperties.SetName(_previewPresenter, name);
    }

    private void UpdateToolTitle(Chrome chrome, OpenCodeToolRowModel model)
    {
        var status = model.StatusText;
        var statusKey = model.Status switch
        {
            OpenCodeToolRowStatus.Completed => OpenCodeToolTheme.Success,
            OpenCodeToolRowStatus.Error => OpenCodeToolTheme.Critical,
            _ => OpenCodeToolTheme.TextSecondary,
        };
        if (model.Kind == OpenCodeToolKind.Shell && model.ExitCode is { } exit && exit != 0)
        {
            status = "退出码 " + exit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            statusKey = OpenCodeToolTheme.Critical;
        }

        chrome.Set(OpenCodeToolPartView.KindGlyph(model.Kind), model.Name, model.Target, status, statusKey, model.DurationText);

        // 并排 / 统一视图只在大视图中提供（diff）；没有“换行”开关——详情文字总是换行（G8）。
        _viewModeButton.Visibility = OpenCodeToolRowPresentation.IsDiffKind(model.Kind) && model.DiffFiles.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _updatingToggles = true;
        _viewModeButton.IsChecked = _options.Unified;
        _updatingToggles = false;
        var name = "工具详情：" + model.AccessibleName;
        AutomationProperties.SetName(_dialogPresenter, name);
        AutomationProperties.SetName(_previewPresenter, name);
    }

    private void Rebuild(bool keepScroll)
    {
        if (_source is null || _mode == OpenCodeDetailPopupMode.None)
        {
            return;
        }

        double? offset = null;
        var follow = false;
        if (keepScroll && _scroller is { } old)
        {
            offset = old.VerticalOffset;
            follow = old.ScrollableHeight > 0 && old.VerticalOffset >= old.ScrollableHeight - 4;
        }

        OpenCodeToolDetailView view;
        try
        {
            view = _source switch
            {
                IOpenCodeToolDetailSource tool => OpenCodeToolDetailContent.Build(tool.DetailModel, _options, () => Rebuild(keepScroll: true)),
                IOpenCodeDetailContentSource content => content.BuildDetailContent(),
                _ => new OpenCodeToolDetailView(OpenCodeToolDetailContent.Caption("无法显示详情内容。"), null),
            };
        }
        catch (Exception)
        {
            // 构建失败（极端数据）时如实提示，不让弹出层崩溃。
            view = new OpenCodeToolDetailView(OpenCodeToolDetailContent.Caption("无法显示该项的详情内容。"), null);
        }

        ActiveChrome.ContentHost.Child = view.Root;
        _scroller = view.Scroller;
        if (_scroller is { } scroller && (offset is not null || follow))
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                scroller.Loaded -= OnLoaded;
                scroller.UpdateLayout();
                scroller.ChangeView(null, follow ? scroller.ScrollableHeight : offset, null, true);
            }

            scroller.Loaded += OnLoaded;
        }
    }

    /// <summary>大视图：遮罩铺满窗口，表面为窗口 80% 并居中（窗口改变时重算）。</summary>
    private void LayoutLarge(Size rootSize)
    {
        var rect = OpenCodeDetailPopupLayout.LargeView(rootSize.Width, rootSize.Height);
        _overlay.Width = rootSize.Width;
        _overlay.Height = rootSize.Height;
        _dialogPresenter.Width = rect.Width;
        _dialogPresenter.Height = rect.Height;
        _dialogPopup.HorizontalOffset = 0;
        _dialogPopup.VerticalOffset = 0;
    }

    /// <summary>预览：紧贴来源行（右侧或下/上方），按实际高度夹在窗口内。</summary>
    private void PlacePreview()
    {
        if (_mode != OpenCodeDetailPopupMode.Preview || _previewAnchor is not { XamlRoot: { } root } anchor)
        {
            return;
        }

        Point origin;
        try
        {
            origin = anchor.TransformToVisual(null).TransformPoint(new Point(0, 0));
        }
        catch (Exception)
        {
            origin = new Point(0, 0);
        }

        var width = _previewPresenter.Width;
        var height = _previewPresenter.ActualHeight > 0 ? _previewPresenter.ActualHeight : _previewPresenter.MaxHeight;
        var rect = OpenCodeDetailPopupLayout.PreviewPlacement(
            origin.X, origin.Y, anchor.ActualWidth, anchor.ActualHeight, width, height, root.Size.Width, root.Size.Height);
        _previewPopup.HorizontalOffset = rect.X;
        _previewPopup.VerticalOffset = rect.Y;
    }

    private void ObserveRoot(XamlRoot? root)
    {
        if (ReferenceEquals(_observedRoot, root))
        {
            return;
        }

        if (_observedRoot is { } old)
        {
            old.Changed -= OnRootChanged;
        }

        _observedRoot = root;
        if (root is not null)
        {
            root.Changed += OnRootChanged;
        }
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_mode == OpenCodeDetailPopupMode.Large)
        {
            LayoutLarge(sender.Size);
        }
    }

    private void ScheduleHoverHide()
    {
        if (_mode != OpenCodeDetailPopupMode.Preview || !_hover.ShouldHide)
        {
            return;
        }

        var timer = EnsureCloseTimer();
        if (timer is null)
        {
            Close();
            return;
        }

        timer.Stop();
        timer.Start();
    }

    private DispatcherQueueTimer? EnsureHoverTimer()
    {
        if (_hoverTimer is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _hoverTimer = queue.CreateTimer();
            _hoverTimer.IsRepeating = false;
            _hoverTimer.Tick += (_, _) =>
            {
                var pending = _pendingSource;
                _pendingSource = null;
                if (pending is not null && _mode != OpenCodeDetailPopupMode.Large)
                {
                    Show(pending, OpenCodeDetailPopupMode.Preview);
                }
            };
        }

        return _hoverTimer;
    }

    private DispatcherQueueTimer? EnsureCloseTimer()
    {
        if (_closeTimer is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _closeTimer = queue.CreateTimer();
            _closeTimer.IsRepeating = false;
            _closeTimer.Interval = TimeSpan.FromMilliseconds(OpenCodeDetailPopupLayout.HoverLeaveGraceMs);
            _closeTimer.Tick += (_, _) =>
            {
                if (_mode == OpenCodeDetailPopupMode.Preview && _hover.ShouldHide)
                {
                    Close();
                }
            };
        }

        return _closeTimer;
    }

    private static FlyoutPresenter CreatePresenter(UIElement content, Thickness padding, float elevation)
    {
        // 原生 FlyoutPresenter：背景/描边/圆角全部来自其默认样式（FlyoutPresenterBackground 亚克力、
        // FlyoutBorderThemeBrush、OverlayCornerRadius）；只解除默认最大尺寸并关闭模板内滚动（内容区自带滚动）。
        // 阴影为系统 ThemeShadow。
        var presenter = new FlyoutPresenter
        {
            Content = content,
            Padding = padding,
            MinWidth = 0,
            MinHeight = 0,
            MaxWidth = double.PositiveInfinity,
            MaxHeight = double.PositiveInfinity,
            IsTabStop = false,
            Translation = new Vector3(0, 0, elevation),
            Shadow = new ThemeShadow(),
        };
        ScrollViewer.SetVerticalScrollMode(presenter, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(presenter, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(presenter, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(presenter, ScrollBarVisibility.Disabled);
        return presenter;
    }

    private static void AddRow(Grid grid, FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    /// <summary>一套标题栏 + 内容宿主（预览与大视图各一套；内置文本样式 + 主题画刷，文字全部可换行）。</summary>
    private sealed class Chrome
    {
        internal Chrome(bool large)
        {
            Icon = new FontIcon { FontSize = large ? 16 : 14, VerticalAlignment = VerticalAlignment.Center };
            Name = OpenCodeInteractionUi.StyledText(string.Empty, large ? OpenCodeInteractionUi.SubtitleStyleKey : OpenCodeInteractionUi.BodyStrongStyleKey, false);
            Name.VerticalAlignment = VerticalAlignment.Center;
            Name.TextWrapping = TextWrapping.Wrap;
            Target = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CodeStyleKey, false);
            Target.TextWrapping = TextWrapping.Wrap;
            Status = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey, false);
            Status.VerticalAlignment = VerticalAlignment.Center;
            Duration = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
            Duration.VerticalAlignment = VerticalAlignment.Center;
            Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            ContentHost = new Border();

            TitleBar = new Grid { ColumnSpacing = large ? 12 : 8 };
            foreach (var width in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            {
                TitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            }

            var statusLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            statusLine.Children.Add(Status);
            statusLine.Children.Add(Duration);
            Grid.SetColumn(Icon, 0);
            Grid.SetColumn(Name, 1);
            Grid.SetColumn(statusLine, 2);
            Grid.SetColumn(Actions, 3);
            TitleBar.Children.Add(Icon);
            TitleBar.Children.Add(Name);
            TitleBar.Children.Add(statusLine);
            TitleBar.Children.Add(Actions);
        }

        internal FontIcon Icon { get; }

        internal TextBlock Name { get; }

        internal TextBlock Target { get; }

        internal TextBlock Status { get; }

        internal TextBlock Duration { get; }

        internal StackPanel Actions { get; }

        internal Grid TitleBar { get; }

        internal Border ContentHost { get; }

        internal void Set(string glyph, string name, string target, string status, string statusBrushKey, string duration)
        {
            Icon.Glyph = glyph;
            Name.Text = name;
            Target.Text = target;
            Target.Visibility = string.IsNullOrEmpty(target) ? Visibility.Collapsed : Visibility.Visible;
            Status.Text = status;
            OpenCodeToolTheme.Apply(Status, OpenCodeToolTheme.Text(statusBrushKey, OpenCodeInteractionUi.CaptionStyleKey));
            Duration.Text = duration;
        }
    }
}
