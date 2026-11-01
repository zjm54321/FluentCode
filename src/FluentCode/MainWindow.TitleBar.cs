using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace FluentCode;

/// <summary>
/// 资源管理器式标题栏：没有单独的标题栏行。OpenCodePage 的会话标签行就是标题栏——
/// 首页标签固定在最左，会话标签、“+”之后的空白（<c>TitleBarDragRegion</c>）可拖动窗口，右侧为系统 caption 按钮。
/// 欢迎页显示时，改由欢迎页顶部的空白承担拖动区。
/// </summary>
public sealed partial class MainWindow
{
    private AppWindowTitleBar? _appWindowTitleBar;
    private XamlRoot? _hookedXamlRoot;
    private bool _titleBarClosed;
    private FrameworkElement? _titleBarElement;

    private void SetupCustomTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        _appWindowTitleBar = AppWindow.TitleBar;
        // Let WinUI own the caption region and track the XAML drag element from its first layout.
        ExtendsContentIntoTitleBar = true;
        _appWindowTitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        _appWindowTitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        // Caption buttons stay within the 40px tab row (tabs sit at its bottom edge, like File Explorer).
        _appWindowTitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        RootElement.Loaded += TitleBarRoot_Loaded;
        Activated += TitleBarWindow_Activated;
    }

    private void TitleBarRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hookedXamlRoot != RootElement.XamlRoot)
        {
            if (_hookedXamlRoot != null) _hookedXamlRoot.Changed -= XamlRoot_Changed;
            _hookedXamlRoot = RootElement.XamlRoot;
            if (_hookedXamlRoot != null) _hookedXamlRoot.Changed += XamlRoot_Changed;
        }
        UpdateTitleBarTarget();
    }

    // Includes DPI-only changes and visibility changes after restoring from the tray.
    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateCaptionInset();

    private void TitleBarWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        UpdateCaptionInset();
    }

    /// <summary>按当前界面（欢迎页 / 会话标签）登记拖动区；切换时调用。</summary>
    private void UpdateTitleBarTarget()
    {
        if (_appWindowTitleBar == null || _titleBarClosed) return;
        FrameworkElement? target = _welcome is { } welcome ? welcome.TitleBarDragElement
            : (ContentFrame.Content as Pages.OpenCodePage)?.TitleBarDragElement;
        if (target is not null && !ReferenceEquals(target, _titleBarElement))
        {
            _titleBarElement = target;
            SetTitleBar(target);
        }
        UpdateCaptionInset();
    }

    /// <summary>标签行右端为系统 caption 按钮保留宽度（RightInset 为物理像素，换算为 DIP）。</summary>
    private void UpdateCaptionInset()
    {
        if (_appWindowTitleBar == null || _titleBarClosed || RootElement.XamlRoot is not { } root) return;
        double scale = root.RasterizationScale;
        // Minimized / RDP-reconnect windows can report a zero scale or garbage insets: ignore them.
        if (!(scale > 0) || double.IsInfinity(scale)) return;
        double inset = _appWindowTitleBar.RightInset / scale;
        if (!double.IsFinite(inset) || inset < 0) return;
        (ContentFrame.Content as Pages.OpenCodePage)?.SetTitleBarCaptionInset(inset);
    }

    private void ShutdownCustomTitleBar()
    {
        _titleBarClosed = true;
        Activated -= TitleBarWindow_Activated;
        RootElement.Loaded -= TitleBarRoot_Loaded;
        if (_hookedXamlRoot != null) _hookedXamlRoot.Changed -= XamlRoot_Changed;
        _hookedXamlRoot = null;
        _titleBarElement = null;
    }
}
