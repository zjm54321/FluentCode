using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

/// <summary>
/// FluentCode 外壳：设置以单例标签打开（齿轮图标 + “设置”），内容为宿主提供的原设置页。
/// </summary>
/// <remarks>
/// <para>设置标签不参与会话导航：选中时只把 <see cref="SettingsTabHost"/> 盖在会话区域上，会话、草稿与输入状态原样保留；
/// 切回进入设置前的标签时直接收起，不重新激活会话。切到其他标签时按原有标签逻辑导航。</para>
/// <para>设置页实例随标签存在：切换标签不会丢失未保存的输入；只有关闭标签时经 <see cref="SettingsCloseGuard"/> 确认。</para>
/// </remarks>
public sealed partial class OpenCodePage
{
    private TabViewItem? _settingsTab;
    private FrameworkElement? _settingsContent;
    private object? _settingsUnderItem;
    private bool _settingsSurfaceShown;
    private bool _settingsClosing;
    private Visibility _browserOverlayVisibilityBeforeSettings = Visibility.Collapsed;
    private Visibility _ptyOverlayVisibilityBeforeSettings = Visibility.Collapsed;

    /// <summary>宿主提供的设置内容（每次打开设置标签时新建一次）。</summary>
    internal Func<FrameworkElement>? SettingsContentFactory { get; set; }

    /// <summary>关闭设置标签前的确认（未保存修改时由宿主做三态确认）；返回 false 保持标签打开。</summary>
    internal Func<FrameworkElement, Task<bool>>? SettingsCloseGuard { get; set; }

    /// <summary>设置标签已关闭（宿主据此释放设置页订阅）。</summary>
    internal event EventHandler<FrameworkElement>? SettingsTabClosed;

    /// <summary>标签行右侧的可拖动空白（宿主经 Window.SetTitleBar 登记为标题栏拖动区）。</summary>
    internal FrameworkElement TitleBarDragElement => TitleBarDragRegion;

    internal bool IsSettingsTabSelected => _settingsTab is not null && ReferenceEquals(SessionTabs.SelectedItem, _settingsTab);

    /// <summary>为系统 caption 按钮保留宽度（DIP），避免标签与可拖动区落在窗口按钮下方。</summary>
    internal void SetTitleBarCaptionInset(double right)
    {
        var width = Math.Max(48, right + 48);
        if (Math.Abs(TitleBarDragRegion.MinWidth - width) > 0.5) TitleBarDragRegion.MinWidth = width;
    }

    /// <summary>打开（或切换到）唯一的设置标签。</summary>
    internal void ShowSettingsTab()
    {
        if (_settingsTab is null)
        {
            if (SettingsContentFactory is not { } factory) return;
            _settingsContent = factory();
            SettingsTabHost.Children.Clear();
            SettingsTabHost.Children.Add(_settingsContent);
            var item = new TabViewItem
            {
                Header = "设置",
                IconSource = new FontIconSource { Glyph = "\uE713" },
                IsClosable = true,
                MinWidth = 120,
            };
            AutomationProperties.SetAutomationId(item, "FluentCodeSettingsTab");
            AutomationProperties.SetName(item, "设置");
            ToolTipService.SetToolTip(item, "设置（Ctrl+,）");
            _settingsTab = item;
            var previous = _suppressTabSelection;
            _suppressTabSelection = true;
            try { SessionTabs.TabItems.Add(item); }
            finally { _suppressTabSelection = previous; }
        }

        if (!ReferenceEquals(SessionTabs.SelectedItem, _settingsTab)) SessionTabs.SelectedItem = _settingsTab;
        else SyncSettingsSurface(null);
    }

    /// <summary>选择变化的最先一步：按当前选中项显示/收起设置覆盖层。返回 true 表示本次选择已完全处理。</summary>
    private bool SyncSettingsSurface(SelectionChangedEventArgs? args)
    {
        var selected = SessionTabs.SelectedItem;
        if (_settingsTab is not null && ReferenceEquals(selected, _settingsTab))
        {
            if (!_settingsSurfaceShown)
            {
                // 记录被覆盖的标签：之后的静默选择若仍指向它，说明会话状态未变，停留在设置。
                _settingsUnderItem = args is { RemovedItems.Count: > 0 } ? args.RemovedItems[0] : _settingsUnderItem ?? HomeTab;
                SetSettingsSurface(true);
            }
            return true;
        }

        if (!_settingsSurfaceShown) return false;
        SetSettingsSurface(false);
        var under = _settingsUnderItem;
        _settingsUnderItem = null;
        // 回到进入设置前的同一标签：页面状态本就对应它，不重新激活（不触发会话切换或草稿守卫）。
        return ReferenceEquals(selected, under);
    }

    private void SetSettingsSurface(bool shown)
    {
        _settingsSurfaceShown = shown;
        SettingsTabHost.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        SessionPane.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        UpdateViewAcknowledgementVisibility(!shown && _hostWindowVisible);
        if (shown)
        {
            _browserOverlayVisibilityBeforeSettings = BrowserOverlayHost.Visibility;
            _ptyOverlayVisibilityBeforeSettings = PersistentPtyOverlayHost.Visibility;
            BrowserOverlayHost.Visibility = Visibility.Collapsed;
            PersistentPtyOverlayHost.Visibility = Visibility.Collapsed;
            SessionTitleChanged?.Invoke("设置");
        }
        else
        {
            BrowserOverlayHost.Visibility = _browserOverlayVisibilityBeforeSettings;
            PersistentPtyOverlayHost.Visibility = _ptyOverlayVisibilityBeforeSettings;
            SessionTitleChanged?.Invoke(CurrentSessionTitle);
        }
    }

    /// <summary>静默选择（会话状态回写）在设置标签选中期间：目标未变则保持设置标签；目标变化则记为新的“被覆盖标签”。</summary>
    private bool KeepSettingsSelected(object target)
    {
        if (!IsSettingsTabSelected || _settingsClosing) return false;
        _settingsUnderItem = target;
        return true;
    }

    private async void CloseSettingsTab()
    {
        if (_settingsTab is not { } tab || _settingsClosing) return;
        var content = _settingsContent;
        if (content is not null && SettingsCloseGuard is { } guard)
        {
            if (!ReferenceEquals(SessionTabs.SelectedItem, tab)) SessionTabs.SelectedItem = tab; // 先让用户看到确认内容。
            if (!await guard(content)) return;
        }

        if (!ReferenceEquals(_settingsTab, tab)) return;
        _settingsClosing = true;
        try
        {
            var target = ReferenceEquals(SessionTabs.SelectedItem, tab)
                ? (_settingsUnderItem is { } under && SessionTabs.TabItems.Contains(under) ? under : HomeTab)
                : SessionTabs.SelectedItem;
            if (ReferenceEquals(SessionTabs.SelectedItem, tab)) SessionTabs.SelectedItem = target;
            var previous = _suppressTabSelection;
            _suppressTabSelection = true;
            try { SessionTabs.TabItems.Remove(tab); }
            finally { _suppressTabSelection = previous; }
            _settingsTab = null;
            _settingsContent = null;
            SettingsTabHost.Children.Clear();
            if (_settingsSurfaceShown) SetSettingsSurface(false);
            if (content is not null) SettingsTabClosed?.Invoke(this, content);
        }
        finally { _settingsClosing = false; }
    }

    /// <summary>外部导航（通知 / 跳转）前调用：设置标签保持打开，但先回到被覆盖的标签，使会话内容可见。</summary>
    internal void RevealSessionSurface()
    {
        if (!IsSettingsTabSelected) return;
        var target = _settingsUnderItem is { } under && SessionTabs.TabItems.Contains(under) ? under : HomeTab;
        SessionTabs.SelectedItem = target;
    }

    /// <summary>欢迎页连接成功后进入首页标签（设置标签若仍打开则保留）。</summary>
    internal void ShowHomeTab()
    {
        if (HomeTab is not null && !ReferenceEquals(SessionTabs.SelectedItem, HomeTab)) SessionTabs.SelectedItem = HomeTab;
    }

    private void HomeSettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsTab();

    /// <summary>首页窄布局搜索行“新建”右侧的纯图标设置入口。</summary>
    private Button CreateHomeSettingsButton()
    {
        var button = new Button
        {
            MinWidth = 36,
            MinHeight = 36,
            Padding = new Thickness(8),
            Content = new FontIcon { Glyph = "\uE713", FontSize = 16 },
        };
        AutomationProperties.SetAutomationId(button, "FluentCodeHomeSettingsButton");
        AutomationProperties.SetName(button, "设置");
        ToolTipService.SetToolTip(button, "设置（Ctrl+,）");
        button.Click += HomeSettingsButton_Click;
        return button;
    }

    /// <summary>首页宽布局右下角的“设置”按钮。</summary>
    private Button CreateWideHomeSettingsButton()
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 24, 24),
            Padding = new Thickness(12, 6, 12, 7),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE713", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "设置", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetAutomationId(button, "FluentCodeHomeWideSettingsButton");
        AutomationProperties.SetName(button, "设置");
        ToolTipService.SetToolTip(button, "设置（Ctrl+,）");
        button.Click += HomeSettingsButton_Click;
        return button;
    }
}
