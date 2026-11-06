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

namespace FluentCode.Pages.OpenCode;

/// <summary>工具详情弹出层的数据来源（工具行）。</summary>
internal interface IOpenCodeToolDetailSource
{
    OpenCodeToolRowModel DetailModel { get; }

    FrameworkElement DetailAnchor { get; }

    /// <summary>弹出层显示/固定（大视图）/关闭状态变化（行据此显示“已选中”强调条）。</summary>
    void OnDetailStateChanged(bool shown, bool pinned);
}

/// <summary>Optional PT1 selector. The exact path wins over the index; projected single-file models need neither.</summary>
internal interface IOpenCodeToolDetailFileSource
{
    int? DetailFileIndex { get; }
    string? DetailFileKey { get; }
}

internal interface IOpenCodeToolDetailOptionsSource
{
    void ConfigureDetailOptions(OpenCodeToolDetailOptions options);
}

/// <summary>Optional host routes; no global selected-session lookup is used for file or terminal actions.</summary>
internal interface IOpenCodeToolDetailActionSource
{
    Action<string, int?>? OpenDetailFile { get; }
    Action<string>? RevealDetailFile { get; }
    Action? OpenDetailTerminal { get; }
}

/// <summary>Rows with inline/navigation activation can preserve exactly the same action from their hover preview.</summary>
internal interface IOpenCodeDetailActivationSource
{
    void ActivateDetail();
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
/// 工具 / 子智能体 / 思考等详情弹出层（G7）：全局同一时刻只有一个，两种形态——
/// <list type="bullet">
/// <item><b>悬停预览</b>（AutomationId <c>OpenCodeToolDetailPreview</c>）：鼠标停留 <see cref="HoverOpenDelayMs"/> 后显示的小浮层
/// （原生 <see cref="FlyoutPresenter"/>：亚克力、Flyout 描边、OverlayCornerRadius + ThemeShadow），在来源行上/下方并让出相邻一行（<see cref="OpenCodeDetailPopupLayout.PreviewGap"/>），<b>没有关闭按钮</b>；
/// 鼠标离开行即隐藏（移入预览可保持；行与预览都离开即隐藏）。不移动键盘焦点。在预览内点击 = 打开大视图。</item>
/// <item><b>大详情视图</b>（AutomationId <c>OpenCodeToolDetailPopup</c>，遮罩 <c>OpenCodeToolDetailOverlay</c>）：点击行 / Enter / Space 打开，
/// 按内容收缩（最大约为应用窗口的 80%；并排 diff / 浏览器 / 图片占满这一宽度）、居中，系统遮罩层（SmokeFillColorDefaultBrush）+ 原生 FlyoutPresenter 表面，内容完整（不截断）纵向滚动；
/// ✕ 按钮 / Esc / 点击遮罩关闭，随窗口大小改变；打开时焦点移入（Tab 在视图内循环），关闭后还给原焦点（与 ContentDialog 一致）。</item>
/// </list>
    /// 所有详情文字默认换行（G8），没有“换行”开关；复制等工具操作集中在详情页脚。
/// </summary>
internal sealed class OpenCodeToolDetailPopup
{
    internal static FrameworkElement? RetainedAnchor => _shared?._source is { } source ? AnchorOf(source)
        : _shared?._pendingSource is { } pending ? AnchorOf(pending) : null;
    /// <summary>首次悬停到弹出预览的延迟（毫秒）；800ms 避免鼠标划过即弹出。</summary>
    internal const int HoverOpenDelayMs = 800;

    /// <summary>预览已打开时悬停切换到另一行的延迟（毫秒）。</summary>
    internal const int HoverSwitchDelayMs = HoverOpenDelayMs;

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
    private readonly SelectorBar _viewModeButton;
    private readonly SelectorBarItem _sideBySideItem;
    private readonly SelectorBarItem _unifiedItem;
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
    private FrameworkElement? _themeAnchor;
    private UIElement? _restoreFocus;
    private bool _updatingToggles;
    private bool _placingPreview;
    private readonly TextBlock _previewHint;
    private bool _previewTruncated;
    private bool _previewRelayoutQueued;
    private double _previewExtentHeight = -1;
    private double _previewViewportWidth = -1;

    // A8：最近一次指针位置（根坐标 DIP）及其所在元素；仅当该元素就是当前预览锚点时才用于横向定位。
    private WeakReference<FrameworkElement>? _pointerElement;
    private double _pointerRootX = double.NaN;
    private double _previewPointerX = double.NaN;
    private bool _previewPlaced;
    private double _previewFullHeight;

    /// <summary>大视图宽度正等待原生布局按内容测出，下一次布局后固定。</summary>
    private bool _largeFitPending;

    /// <summary>消息内容列（OpenCodePage.xaml 的 MessageHost）：预览横向边界的来源，而不是整个窗口。</summary>
    private const string ContentColumnName = "MessageHost";

    private OpenCodeToolDetailPopup()
    {
        // ---------------- 悬停预览 ----------------
        _previewChrome = new Chrome(large: false);
        _previewHint = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
        _previewHint.TextWrapping = TextWrapping.Wrap;
        _previewHint.Visibility = Visibility.Collapsed;
        var previewLayout = new Grid { RowSpacing = 8 };
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(previewLayout, _previewChrome.TitleBar, 0);
        AddRow(previewLayout, _previewChrome.ContentHost, 2);
        AddRow(previewLayout, _previewHint, 3);

        _previewPresenter = CreatePresenter(previewLayout, new Thickness(14, 10, 14, 10), 32);
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
        // Native layout may change the rendered height, but must never start another width search.
        _previewPresenter.SizeChanged += (_, _) => PlacePreview(remeasure: false);

        _previewPopup = new Popup
        {
            Child = _previewPresenter,
            IsLightDismissEnabled = false,
            ChildTransitions = new TransitionCollection { new PopupThemeTransition() },
        };

        // ---------------- 大详情视图 ----------------
        _dialogChrome = new Chrome(large: true);

        _sideBySideItem = new SelectorBarItem { Text = "并排" };
        _unifiedItem = new SelectorBarItem { Text = "统一" };
        _viewModeButton = new SelectorBar();
        _viewModeButton.Items.Add(_sideBySideItem);
        _viewModeButton.Items.Add(_unifiedItem);
        AutomationProperties.SetAutomationId(_viewModeButton, "OpenCodeToolDetailViewMode");
        AutomationProperties.SetName(_viewModeButton, "并排 / 统一视图");
        _viewModeButton.SelectionChanged += (_, _) =>
        {
            if (_updatingToggles)
            {
                return;
            }

            _options.Unified = _viewModeButton.SelectedItem == _unifiedItem;
            Rebuild(keepScroll: false);
        };

        _closeButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 14 },
            Width = 32, Height = 32, Padding = new Thickness(0),
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
        AddRow(dialogLayout, _dialogChrome.ContentHost, 2);

        // ContentDialog 同级的阴影高度与 24px 内边距。
        _dialogPresenter = CreatePresenter(dialogLayout, new Thickness(0), 128);
        _dialogPresenter.HorizontalAlignment = HorizontalAlignment.Center;
        _dialogPresenter.VerticalAlignment = VerticalAlignment.Center;
        _dialogPresenter.SizeChanged += (_, _) => ApplyResponsiveDiff();
        _dialogPresenter.LayoutUpdated += (_, _) => FreezeLargeWidth();
        FluentCode.Motion.FluentMotion.BindTransitions(_dialogPresenter, transitions => _dialogPresenter.Transitions = transitions, () => new TransitionCollection
        {
            new EntranceThemeTransition { FromHorizontalOffset = 0, FromVerticalOffset = 24, IsStaggeringEnabled = false },
        });
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

    /// <summary>
    /// A8：记录指针在 <paramref name="element"/>（来源行 / 折叠单项分组标题）上的位置。
    /// W3: only the first entry point is retained. Legacy callers may still report moves;
    /// those reports must never move an open preview or change a pending entry position.
    /// </summary>
    internal void TrackPointer(FrameworkElement element, PointerRoutedEventArgs e)
    {
        if (_pointerElement is { } previous && previous.TryGetTarget(out var tracked) && ReferenceEquals(tracked, element)) return;
        double x;
        try
        {
            var local = e.GetCurrentPoint(element).Position;
            x = element.TransformToVisual(null).TransformPoint(local).X;
        }
        catch (Exception)
        {
            return;
        }

        if (!double.IsFinite(x)) return;
        _pointerElement = new WeakReference<FrameworkElement>(element);
        _pointerRootX = x;
    }

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
        FollowTheme(null);
        var previous = _source;
        _source = null;
        _mode = OpenCodeDetailPopupMode.None;
        if (_scroller is { } scroll) scroll.LayoutUpdated -= OnPreviewContentLayoutUpdated;
        _scroller = null;
        _previewAnchor = null;
        _previewPlaced = false;
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

        if (_mode == OpenCodeDetailPopupMode.Preview && HoverSwitchDelayMs == 0)
        {
            timer.Stop();
            _pendingSource = null;
            Show(source, OpenCodeDetailPopupMode.Preview);
            return;
        }

        timer.Interval = TimeSpan.FromMilliseconds(_mode == OpenCodeDetailPopupMode.Preview ? HoverSwitchDelayMs : HoverOpenDelayMs);
        timer.Stop();
        timer.Start();
    }

    private void HoverExitCore(object source)
    {
        if (_pointerElement is { } pointer && pointer.TryGetTarget(out var element) && ReferenceEquals(element, AnchorOf(source)))
            _pointerElement = null;
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
        if (mode == OpenCodeDetailPopupMode.Large && source is IOpenCodeDetailActivationSource activation)
        {
            Close();
            activation.ActivateDetail();
            return;
        }
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
        if (source is IOpenCodeToolDetailFileSource fileSource)
        {
            _options.SelectedFile = fileSource.DetailFileIndex ?? 0;
            _options.SelectedFileKey = fileSource.DetailFileKey;
        }
        if (source is IOpenCodeToolDetailActionSource actions)
        {
            _options.OpenFile = actions.OpenDetailFile;
            _options.RevealFile = actions.RevealDetailFile;
            _options.OpenTerminal = actions.OpenDetailTerminal;
        }

        _closeTimer?.Stop();
        FollowTheme(anchor);
        _mode = mode;
        _scroller = null;
        _previewChrome.ContentHost.Child = null;
        _dialogChrome.ContentHost.Child = null;

        if (mode == OpenCodeDetailPopupMode.Large)
        {
            _options.Unified = root.Size.Width < 900;
            // 大视图展示完整内容（不截断；长列表由 ItemsRepeater 虚拟化）。
            _options.ShowAll = false;
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
                // The card keeps its EntranceThemeTransition; the smoke behind it now fades in (167ms) instead of popping.
                FluentCode.Motion.FluentMotion.FadeIn(_smoke);
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
            _previewPointerX = TryAnchorOrigin(anchor, out var entryOrigin) ? PointerX(anchor, entryOrigin) : double.NaN;
            _previewPlaced = false;
            _previewPopup.XamlRoot = root;
            ObserveRoot(root);
            UpdateTitle(source);
            Rebuild(keepScroll: false); // Measure natural width and fit once before opening.
            if (_mode != OpenCodeDetailPopupMode.Preview || !_previewPlaced)
            {
                // No valid content-column bounds (unloaded / different root / non-finite): never fall back
                // to the window centre or (0,0) — just don't show.
                if (_mode != OpenCodeDetailPopupMode.None) Close();
                return;
            }

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
        if (model.Kind == OpenCodeToolKind.Shell && model.ExitCode is { } exit)
        {
            status = "退出码 " + exit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (exit != 0) statusKey = OpenCodeToolTheme.Critical;
        }

        // The terminal body contains the complete command once; don't repeat its first line in chrome.
        var target = model.Target;
        if (model.DiffFiles.Count > 0)
        {
            var selected = Math.Clamp(_options.SelectedFile, 0, model.DiffFiles.Count - 1);
            if (_options.SelectedFileKey is { } key)
                for (var index = 0; index < model.DiffFiles.Count; index++)
                    if (model.DiffFiles[index].Path == key) { selected = index; break; }
            target = model.DiffFiles[selected].Path;
        }
        var timing = model.DurationText;
        if (model.Kind == OpenCodeToolKind.Shell) target = string.Empty;
        if (model.Kind == OpenCodeToolKind.WebFetch)
        {
            target = _mode == OpenCodeDetailPopupMode.Preview && Uri.TryCreate(model.Url, UriKind.Absolute, out var uri)
                ? uri.Host + "/" + uri.Segments[^1] : string.Empty;
            if (_mode == OpenCodeDetailPopupMode.Preview && OpenCodeToolDetailContent.PreviewFormat(model) is { } format) timing = format;
        }
        if (_mode == OpenCodeDetailPopupMode.Preview && model.Kind == OpenCodeToolKind.WebSearch
            && OpenCodeToolDetailContent.PreviewProvider(model) is { } provider) timing = provider;
        chrome.Set(OpenCodeToolPartView.KindGlyph(model.Kind), model.Name, target, status, statusKey, timing);

        // 并排 / 统一视图只在大视图中提供（diff）；没有“换行”开关——详情文字总是换行（G8）。
        _viewModeButton.Visibility = OpenCodeToolRowPresentation.IsDiffKind(model.Kind) && model.DiffFiles.Count > 0
            && !OpenCodeFileDetailPolicy.IsFullWrite(model.Kind, model.DiffFiles[Math.Clamp(_options.SelectedFile, 0, model.DiffFiles.Count - 1)])
            ? Visibility.Visible
            : Visibility.Collapsed;
        _updatingToggles = true;
        _viewModeButton.SelectedItem = _options.Unified ? _unifiedItem : _sideBySideItem;
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

        using var detailScope = OpenCodeToolDetailContent.DetailBuildScope(_mode == OpenCodeDetailPopupMode.Large);
        OpenCodeToolDetailView view;
        try
        {
            if (_source is IOpenCodeToolDetailOptionsSource optionsSource) optionsSource.ConfigureDetailOptions(_options);
            view = _source switch
            {
                IOpenCodeToolDetailSource tool => OpenCodeToolDetailContent.Build(tool.DetailModel, Compact(_options), () => Rebuild(keepScroll: true)),
                IOpenCodeDetailContentSource content => content.BuildDetailContent(),
                _ => new OpenCodeToolDetailView(OpenCodeToolDetailContent.Caption("无法显示详情内容。"), null),
            };
        }
        catch (Exception)
        {
            // 构建失败（极端数据）时如实提示，不让弹出层崩溃。
            view = new OpenCodeToolDetailView(OpenCodeToolDetailContent.Caption("无法显示该项的详情内容。"), null);
        }

        // Popups are separate visual roots: carry only the originating anchor's explicit session scope.
        // This is not a selected-session/global resolver fallback.
        if (OpenCodeImageContext.Find(AnchorOf(_source)) is { } imageScope)
            OpenCodeImageContext.Bind(view.Root, imageScope);
        ActiveChrome.ContentHost.Child = view.Root;
        if (_scroller is { } previousScroller) previousScroller.LayoutUpdated -= OnPreviewContentLayoutUpdated;
        _scroller = view.Scroller;
        _previewExtentHeight = -1;
        _previewViewportWidth = -1;
        if (_scroller is { } currentScroller) currentScroller.LayoutUpdated += OnPreviewContentLayoutUpdated;
        _previewTruncated = view.IsTruncated;
        _previewHint.Text = view.PreviewHint ?? (view.HiddenLineCount > 0 ? $"还有 {view.HiddenLineCount} 行" : "内容未完整显示");
        if (_mode == OpenCodeDetailPopupMode.Preview) PlacePreview();
        // Refreshed / toggled content re-fits once; the open view never shrinks under the reader.
        else if (_dialogPopup.IsOpen && _observedRoot is { } root) FitLarge(root.Size, growOnly: true);
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

    private OpenCodeToolDetailOptions Compact(OpenCodeToolDetailOptions options)
    {
        options.Compact = _mode == OpenCodeDetailPopupMode.Preview;
        return options;
    }

    /// <summary>
    /// 大视图：遮罩铺满窗口；表面按内容收缩并居中，最大为窗口 80%（窗口改变时重算）。
    /// 宽度先由原生布局按内容决定，第一次布局后固定下来（<see cref="FreezeLargeWidth"/>），
    /// 滚动时虚拟化列表实现的新行不会让视图忽宽忽窄；内容刷新只允许变宽。
    /// </summary>
    private void LayoutLarge(Size rootSize)
    {
        _overlay.Width = rootSize.Width;
        _overlay.Height = rootSize.Height;
        FitLarge(rootSize, growOnly: false);
        _dialogPopup.HorizontalOffset = 0;
        _dialogPopup.VerticalOffset = 0;
        ApplyResponsiveDiff();
    }

    private void FitLarge(Size rootSize, bool growOnly)
    {
        var cap = OpenCodeDetailPopupLayout.LargeView(rootSize.Width, rootSize.Height);
        var fill = LargeFillsWidth();
        var (minWidth, maxWidth) = OpenCodeDetailPopupLayout.LargeViewWidthRange(cap, fill);
        var (minHeight, maxHeight) = OpenCodeDetailPopupLayout.LargeViewHeightRange(cap);
        if (growOnly && !fill && _dialogPresenter.ActualWidth > 0)
            minWidth = OpenCodeDetailPopupLayout.LargeFitWidth(_dialogPresenter.ActualWidth, cap, fill);
        _dialogPresenter.MaxWidth = maxWidth;
        _dialogPresenter.MinWidth = minWidth;
        _dialogPresenter.Width = fill ? maxWidth : double.NaN;
        _largeFitPending = !fill;
        _dialogPresenter.Height = double.NaN;
        _dialogPresenter.MinHeight = minHeight;
        _dialogPresenter.MaxHeight = maxHeight;
    }

    /// <summary>并排 diff、浏览器与图片结果占满上限宽度；其余（思考、To Do、文本、属性等）按内容收缩。</summary>
    private bool LargeFillsWidth()
    {
        if (_source is not IOpenCodeToolDetailSource tool) return false;
        var model = tool.DetailModel;
        var fileView = model.Kind == OpenCodeToolKind.Write && !_options.ShowDifference && model.DiffFiles.Count > 0
            && model.DiffFiles[Math.Clamp(_options.SelectedFile, 0, model.DiffFiles.Count - 1)].After is not null;
        return OpenCodeDetailPopupLayout.LargeViewFillsWidth(model.Kind, OpenCodeToolDetailContent.ToolImages(model).Count > 0, fileView);
    }

    private void FreezeLargeWidth()
    {
        if (!_largeFitPending || _mode != OpenCodeDetailPopupMode.Large || !_dialogPopup.IsOpen) return;
        var width = _dialogPresenter.ActualWidth;
        if (width <= 0) return;
        _largeFitPending = false;
        _dialogPresenter.Width = Math.Clamp(width, _dialogPresenter.MinWidth, _dialogPresenter.MaxWidth);
    }

    private void ApplyResponsiveDiff()
    {
        if (_mode != OpenCodeDetailPopupMode.Large || _source is not IOpenCodeToolDetailSource tool
            || !OpenCodeToolRowPresentation.IsDiffKind(tool.DetailModel.Kind)) return;
        var narrow = AnchorOf(_source).XamlRoot?.Size.Width < 900;
        if (_options.AutoUnified == narrow) return;
        _options.AutoUnified = narrow;
        _options.Unified = narrow;
        UpdateTitle(_source);
        Rebuild(keepScroll: true);
    }

    /// <summary>预览：自然内容尺寸，只在来源行上/下方，最大不超过较宽裕一侧。</summary>
    private void OnPreviewContentLayoutUpdated(object? sender, object e)
    {
        if (_mode != OpenCodeDetailPopupMode.Preview || _placingPreview || _scroller is not { } scroll) return;
        // A new viewport belongs to our own width pass, not a content change. Seed the
        // final extent without scheduling another search (also covers first layout).
        if (Math.Abs(scroll.ViewportWidth - _previewViewportWidth) >= 0.5)
        {
            _previewExtentHeight = scroll.ExtentHeight;
            _previewViewportWidth = scroll.ViewportWidth;
            return;
        }
        if (Math.Abs(scroll.ExtentHeight - _previewExtentHeight) < 0.5
            && Math.Abs(scroll.ViewportWidth - _previewViewportWidth) < 0.5) return;
        _previewExtentHeight = scroll.ExtentHeight;
        _previewViewportWidth = scroll.ViewportWidth;
        if (_previewRelayoutQueued) return;
        _previewRelayoutQueued = true;
        if (!scroll.DispatcherQueue.TryEnqueue(() => { _previewRelayoutQueued = false; PlacePreview(); }))
            _previewRelayoutQueued = false;
    }

    private void PlacePreview(bool remeasure = true)
    {
        if (_placingPreview || _mode != OpenCodeDetailPopupMode.Preview || _previewAnchor is not { XamlRoot: { } root } anchor)
        {
            return;
        }

        if (!anchor.IsLoaded || !TryAnchorOrigin(anchor, out var origin) || !TryContentBand(anchor, root.Size.Width, out var band))
        {
            // Invalid bounds: never place against the whole window or (0,0); close a stale preview instead.
            _previewPlaced = false;
            if (_previewPopup.IsOpen) Close();
            return;
        }

        var pointerX = _previewPointerX;
        _placingPreview = true;
        try
        {
        if (!remeasure)
        {
            if (!_previewPlaced) return;
            var positioned = PlaceBelowByDefault(pointerX, origin.Y, anchor.ActualHeight,
                _previewPresenter.Width, Math.Min(_previewFullHeight, _previewPresenter.MaxHeight), band, root.Size.Height);
            _previewPopup.HorizontalOffset = positioned.X;
            _previewPopup.VerticalOffset = positioned.Y;
            return;
        }
        // Widths are column-based; keep the existing safe band and frozen entry X.
        var columnWidth = band.Width / (1 - 2 * OpenCodeDetailPopupLayout.ContentEdgeFraction);
        var sizingAnchor = Math.Min(columnWidth, band.Width / OpenCodeDetailPopupLayout.PreviewMaxRowFraction);
        var maxWidth = band.Width;
        var height = Math.Min(340, OpenCodeDetailPopupLayout.PreviewHeightLimit(root.Size.Height));
        var capacity = OpenCodeDetailPopupLayout.PreviewPlacement(
            pointerX, origin.Y, anchor.ActualHeight, maxWidth, height, band, root.Size.Height);
        var availableHeight = Math.Min(height, capacity.Height);
        _previewHint.Visibility = Visibility.Collapsed;
        // Clear the previous explicit width/cap before measuring native unwrapped content.
        _previewPresenter.Width = double.NaN;
        _previewPresenter.Height = double.NaN;
        _previewPresenter.MaxWidth = double.PositiveInfinity;
        _previewPresenter.MaxHeight = double.PositiveInfinity;
        _previewPresenter.ApplyTemplate();
        _scroller?.ApplyTemplate();
        _previewPresenter.InvalidateMeasure();
        _previewPresenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var naturalWidth = _previewPresenter.DesiredSize.Width;
        OpenCodeDetailPreviewMeasure MeasureContent(double width)
        {
            _previewPresenter.Width = width;
            _previewPresenter.Height = double.NaN;
            _previewPresenter.MaxWidth = maxWidth;
            _previewPresenter.MaxHeight = double.PositiveInfinity;
            _previewPresenter.InvalidateMeasure();
            _previewPresenter.Measure(new Size(width, double.PositiveInfinity));
            var measuredHeight = _previewPresenter.DesiredSize.Height;
            // ScrollViewer.DesiredSize stops at its MaxHeight. Measure the native body
            // separately so the compact 260px cap cannot hide overflow from FitPreview.
            var overflow = MeasurePreviewOverflow(_previewChrome.ContentHost.Child, Math.Max(0, width - 28));
            return new OpenCodeDetailPreviewMeasure(measuredHeight + overflow.ExtraHeight, overflow.Clipped);
        }
        var fit = OpenCodeDetailPopupLayout.FitPreview(sizingAnchor, band, naturalWidth, availableHeight, MeasureContent);
        _previewHint.Visibility = fit.Clipped || _previewTruncated ? Visibility.Visible : Visibility.Collapsed;
        // Restore the presenter's final measure after inspecting inner scroll contents;
        // include the hint in final height, without starting another width pass.
        _previewPresenter.InvalidateMeasure();
        _previewPresenter.Measure(new Size(fit.Width, double.PositiveInfinity));
        var fullHeight = _previewPresenter.DesiredSize.Height;
        _previewFullHeight = fullHeight;
        var rect = PlaceBelowByDefault(
            pointerX, origin.Y, anchor.ActualHeight, fit.Width, Math.Min(fullHeight, availableHeight), band, root.Size.Height);
        _previewPresenter.Width = rect.Width;
        // Keep height automatic so streamed text / decoded images can grow naturally up to this side's capacity.
        _previewPresenter.Height = double.NaN;
        _previewPresenter.MaxHeight = rect.Height;
        _previewPopup.HorizontalOffset = rect.X;
        _previewPopup.VerticalOffset = rect.Y;
        _previewPlaced = true;
        _previewViewportWidth = -1; // Next native viewport/extent pair is our final layout, not new content.
        }
        finally { _placingPreview = false; }
    }

    private static (double ExtraHeight, bool Clipped) MeasurePreviewOverflow(DependencyObject? node, double width)
    {
        if (node is null) return (0, false);
        var extraHeight = 0d;
        var clipped = false;
        if (node is FrameworkElement element)
        {
            width = Math.Max(0, width - element.Margin.Left - element.Margin.Right);
            clipped = element.MinWidth > width || (double.IsFinite(element.Width) && element.Width > width);
            width = Math.Min(width, Math.Min(element.MaxWidth, element.DesiredSize.Width));
        }
        if (node is TextBlock { TextWrapping: TextWrapping.NoWrap, TextTrimming: TextTrimming.None } text)
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            clipped |= text.DesiredSize.Width > width + 0.5;
            text.Measure(new Size(width, double.PositiveInfinity));
        }
        if (node is RichTextBlock { TextWrapping: TextWrapping.NoWrap } rich)
        {
            rich.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            clipped |= rich.DesiredSize.Width > width + 0.5;
            rich.Measure(new Size(width, double.PositiveInfinity));
        }
        if (node is ScrollViewer { Content: FrameworkElement content } scroll)
        {
            var innerWidth = Math.Max(0, width - scroll.Padding.Left - scroll.Padding.Right);
            // Horizontal scrolling gives a native unbounded layout. Its natural width
            // must fit the candidate viewport, even when DesiredSize was constrained.
            if (scroll.HorizontalScrollMode != ScrollMode.Disabled)
            {
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                clipped |= content.DesiredSize.Width > innerWidth + 0.5;
            }
            content.Measure(new Size(innerWidth, double.PositiveInfinity));
            var fullHeight = content.DesiredSize.Height + scroll.Padding.Top + scroll.Padding.Bottom;
            var cap = Math.Min(scroll.MaxHeight, double.IsFinite(scroll.Height) ? scroll.Height : double.PositiveInfinity);
            clipped |= fullHeight > cap + 0.5;
            extraHeight = Math.Max(0, fullHeight - scroll.DesiredSize.Height);
            var inner = MeasurePreviewOverflow(content, innerWidth);
            return (extraHeight + inner.ExtraHeight, clipped || inner.Clipped);
        }
        if (node is Border border) width = Math.Max(0, width - border.Padding.Left - border.Padding.Right);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = MeasurePreviewOverflow(VisualTreeHelper.GetChild(node, i), width);
            extraHeight = Math.Max(extraHeight, child.ExtraHeight);
            clipped |= child.Clipped;
        }
        return (extraHeight, clipped);
    }

    /// <summary>W3: below by default; above only when the content does not fit below.</summary>
    private static OpenCodeDetailRect PlaceBelowByDefault(double pointerX, double anchorY, double anchorHeight,
        double width, double height, OpenCodeDetailBand band, double rootHeight)
    {
        const double margin = OpenCodeDetailPopupLayout.WindowMargin;
        const double gap = OpenCodeDetailPopupLayout.PreviewGap;
        var vertical = OpenCodeProcessedPreviewPlacement.Vertical(anchorY, anchorHeight, height, rootHeight, margin, gap);
        width = Math.Min(width, band.Width);
        return new OpenCodeDetailRect(OpenCodeDetailPopupLayout.PreviewX(pointerX, width, band),
            vertical.Y, width, vertical.Height);
    }

    /// <summary>当前指针 X（根坐标）；指针未在该锚点上记录过（键盘 / 其它来源）时退回锚点中心——仍受内容列安全区夹紧。</summary>
    private double PointerX(FrameworkElement anchor, Point origin)
        => _pointerElement is { } weak && weak.TryGetTarget(out var element) && ReferenceEquals(element, anchor) && double.IsFinite(_pointerRootX)
            ? _pointerRootX
            : origin.X + (anchor.ActualWidth / 2);

    private static bool TryAnchorOrigin(FrameworkElement anchor, out Point origin)
    {
        try
        {
            origin = anchor.TransformToVisual(null).TransformPoint(new Point(0, 0));
        }
        catch (Exception)
        {
            origin = default;
            return false;
        }

        return double.IsFinite(origin.X) && double.IsFinite(origin.Y);
    }

    /// <summary>
    /// A8：预览横向安全区 = 锚点所在消息内容列（MessageHost，去掉其左右 Padding）与其外层可见滚动视口的交集，
    /// 变换到同一 XamlRoot 坐标后两侧再各留约 10%。找不到内容列时用最近的滚动视口内容；都没有则无效（不退回窗口）。
    /// </summary>
    private static bool TryContentBand(FrameworkElement anchor, double rootWidth, out OpenCodeDetailBand band)
    {
        band = default;
        FrameworkElement? column = null;
        ScrollViewer? viewport = null;
        ScrollViewer? nearestScroll = null;
        for (DependencyObject? node = VisualTreeHelper.GetParent(anchor); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (column is null && node is FrameworkElement { Name: ContentColumnName } named)
            {
                column = named;
                continue;
            }

            if (node is ScrollViewer scroll)
            {
                nearestScroll ??= scroll;
                if (column is not null)
                {
                    viewport = scroll;
                    break;
                }
            }
        }

        if (column is null)
        {
            if (nearestScroll is null) return false;
            viewport = nearestScroll;
            column = nearestScroll.Content as FrameworkElement ?? nearestScroll;
        }

        if (!TryHorizontalExtent(column, out var left, out var right)) return false;
        var padding = column switch
        {
            Grid grid => grid.Padding,
            Border border => border.Padding,
            StackPanel panel => panel.Padding,
            _ => default,
        };
        left += padding.Left;
        right -= padding.Right;
        if (viewport is not null && !ReferenceEquals(viewport, column))
        {
            if (!TryHorizontalExtent(viewport, out var viewLeft, out var viewRight)) return false;
            if (viewport.ViewportWidth > 0) viewRight = Math.Min(viewRight, viewLeft + viewport.ViewportWidth);
            left = Math.Max(left, viewLeft);
            right = Math.Min(right, viewRight);
        }

        if (OpenCodeDetailPopupLayout.PreviewSafeBand(left, right, rootWidth) is not { } safe) return false;
        band = safe;
        return true;
    }

    private static bool TryHorizontalExtent(FrameworkElement element, out double left, out double right)
    {
        left = right = 0;
        if (element.ActualWidth <= 0 || !TryAnchorOrigin(element, out var origin)) return false;
        left = origin.X;
        right = origin.X + element.ActualWidth;
        return true;
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

    /// <summary>
    /// Popups are separate visual roots: they never inherit the window root's RequestedTheme, so their
    /// ThemeResources would otherwise stay on Application.RequestedTheme (the startup theme). Mirror the
    /// source row's ActualTheme — set by AppThemeController for app and live Windows changes — onto both
    /// popup children, at open and whenever it changes while open. High Contrast still wins natively.
    /// </summary>
    private void FollowTheme(FrameworkElement? anchor)
    {
        if (!ReferenceEquals(_themeAnchor, anchor))
        {
            if (_themeAnchor is { } old) old.ActualThemeChanged -= OnAnchorThemeChanged;
            _themeAnchor = anchor;
            if (anchor is not null) anchor.ActualThemeChanged += OnAnchorThemeChanged;
        }

        if (anchor is not null) ApplyTheme(anchor.ActualTheme);
    }

    private void OnAnchorThemeChanged(FrameworkElement sender, object args)
    {
        if (ReferenceEquals(sender, _themeAnchor) && _mode != OpenCodeDetailPopupMode.None) ApplyTheme(sender.ActualTheme);
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (_previewPresenter.RequestedTheme != theme) _previewPresenter.RequestedTheme = theme;
        if (_overlay.RequestedTheme != theme) _overlay.RequestedTheme = theme;
    }

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_mode == OpenCodeDetailPopupMode.Large)
        {
            LayoutLarge(sender.Size);
        }
        else if (_mode == OpenCodeDetailPopupMode.Preview)
        {
            PlacePreview();
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

    internal static FlyoutPresenter CreatePresenter(UIElement content, Thickness padding, float elevation)
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
    internal sealed class Chrome
    {
        internal Chrome(bool large)
        {
            Icon = new FontIcon { FontSize = large ? 20 : 14, VerticalAlignment = VerticalAlignment.Center };
            _iconVisual = new(Icon);
            Name = OpenCodeInteractionUi.StyledText(string.Empty, large ? OpenCodeInteractionUi.SubtitleStyleKey : OpenCodeInteractionUi.BodyStrongStyleKey, false);
            Name.VerticalAlignment = VerticalAlignment.Center;
            Name.TextWrapping = TextWrapping.Wrap;
            Target = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CodeStyleKey, false);
            Target.TextWrapping = TextWrapping.NoWrap;
            Target.TextTrimming = TextTrimming.CharacterEllipsis;
            Target.FontSize = large ? 13 : 12;
            Target.VerticalAlignment = VerticalAlignment.Center;
            Status = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey, false);
            Status.VerticalAlignment = VerticalAlignment.Center;
            Status.TextWrapping = TextWrapping.Wrap;
            Status.TextAlignment = TextAlignment.Right;
            StatusCapsule = new Border { Child = Status, Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center };
            Duration = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
            Duration.VerticalAlignment = VerticalAlignment.Center;
            Duration.TextWrapping = TextWrapping.Wrap;
            Actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            ContentHost = new Border();
            OpenCodeToolTheme.Apply(ContentHost, OpenCodeToolTheme.Border(OpenCodeToolTheme.SolidBase));

            TitleBar = new Grid { ColumnSpacing = 12, Height = large ? 56 : 22, Padding = large ? new Thickness(24, 0, 16, 0) : new Thickness(0) };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            {
                TitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            }

            var statusLine = StatusLine = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            statusLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusLine.Children.Add(StatusCapsule);
            Grid.SetColumn(Duration, 1);
            statusLine.Children.Add(Duration);
            Grid.SetColumn(_iconVisual, 0);
            Grid.SetColumn(Name, 1);
            Grid.SetColumn(Target, 2);
            Grid.SetColumn(statusLine, 3);
            Grid.SetColumn(Actions, 4);
            TitleBar.Children.Add(_iconVisual);
            TitleBar.Children.Add(Name);
            TitleBar.Children.Add(Target);
            TitleBar.Children.Add(statusLine);
            TitleBar.Children.Add(Actions);
        }

        internal FontIcon Icon { get; }
        private readonly OpenCodeTerminalGlyphIcon _iconVisual;

        internal TextBlock Name { get; }

        internal TextBlock Target { get; }

        internal TextBlock Status { get; }
        internal Border StatusCapsule { get; }

        // FrameworkElement.Parent of a Panel child is null before the tree loads; expose the row directly.
        internal Grid StatusLine { get; }

        internal TextBlock Duration { get; }

        internal StackPanel Actions { get; }

        internal Grid TitleBar { get; }

        internal Border ContentHost { get; }

        internal void Set(string glyph, string name, string target, string status, string statusBrushKey, string duration)
        {
            _iconVisual.SetGlyph(glyph);
            Name.Text = name;
            Target.Text = target;
            ToolTipService.SetToolTip(Target, target);
            Target.Visibility = string.IsNullOrEmpty(target) ? Visibility.Collapsed : Visibility.Visible;
            Status.Text = status;
            OpenCodeToolTheme.Apply(Status, OpenCodeToolTheme.Text(statusBrushKey, OpenCodeInteractionUi.CaptionStyleKey));
            OpenCodeToolTheme.Apply(StatusCapsule, OpenCodeToolTheme.Border(statusBrushKey == OpenCodeToolTheme.Success
                ? OpenCodeToolTheme.SuccessBackground : statusBrushKey == OpenCodeToolTheme.Critical
                    ? OpenCodeToolTheme.CriticalBackground : OpenCodeToolTheme.SubtleSecondary));
            StatusCapsule.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
            Duration.Text = duration;
        }
    }
}
