using System;
using FluentCode.Pages.Account;
using FluentCode.Services.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace FluentCode.Pages.Welcome;

/// <summary>
/// 首次运行欢迎页（尚未添加任何 OpenCode，或在设置中断开连接后）。
/// 表单即原有 <see cref="OpenCodeConnectionView"/>（三种连接方式、校验与状态文案不变，只隐藏“当前配置”卡片）。
/// 连接成功由主窗口通过设置变化感知，并在短暂显示成功状态后切换到首页标签。
/// </summary>
public sealed partial class WelcomeView : UserControl
{
    private readonly OpenCodeConnectionView _form;

    // 系统颜色（强调色 / 高对比度）变化的通知来源。只在本页显示期间订阅，见 WatchColors。
    private readonly UISettings _colors = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private bool _watchingColors;
    private bool _closed;

    public WelcomeView(SettingsService settings, DpapiSecretStore secrets)
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue;
        _form = new OpenCodeConnectionView(settings, secrets, showCurrentConnection: false);
        FormHost.Content = _form;
        ActualThemeChanged += (_, _) => ApplyBloom();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>标题栏可拖动区（欢迎态时由主窗口登记）。</summary>
    internal FrameworkElement TitleBarDragElement => DragRegion;

    internal OpenCodeConnectionView Form => _form;

    /// <summary>连接操作进行中（主窗口不在此期间切走欢迎页）。</summary>
    internal bool IsBusy => _form.IsBusy;

    /// <summary>离开欢迎页：取消进行中的操作并清空表单中的秘密。</summary>
    internal void Close()
    {
        _closed = true;
        WatchColors(false);
        _form.Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        WatchColors(true);
        ApplyBloom();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        // WinUI 可能在重新插入并 Loaded 之后才送达旧的 Unloaded；仍在树上时不退订。
        if (!IsLoaded) WatchColors(false);
    }

    /// <summary>
    /// 本页显示期间跟随系统颜色变化：用户在 Windows 里换强调色时主题（深 / 浅）不变，
    /// <see cref="FrameworkElement.ActualThemeChanged"/> 不会触发，光晕要靠这里刷新。
    /// 离开页面（<see cref="Close"/>）或从界面树移除时退订，系统对象不再引用本页。
    /// </summary>
    private void WatchColors(bool watch)
    {
        if (watch == _watchingColors || (watch && _closed)) return;
        _watchingColors = watch;
        if (watch) _colors.ColorValuesChanged += OnColorValuesChanged;
        else _colors.ColorValuesChanged -= OnColorValuesChanged;
    }

    /// <summary>系统在后台线程上通知；回到界面线程再改画刷，页面已离开则不处理。</summary>
    private void OnColorValuesChanged(UISettings sender, object args)
        => _dispatcher.TryEnqueue(() => { if (_watchingColors) ApplyBloom(); });

    /// <summary>
    /// 卡片后面的强调色光晕。颜色与 <c>AccentFillColorDefaultBrush</c> 相同
    /// （深色主题下是较亮的强调色 Light2、浅色主题下是较暗的强调色 Dark1），因此跟随用户在 Windows 里选择的强调色，
    /// 而不再是写死的蓝色。高对比度下整层关闭，让背景保持系统窗口色。
    /// 取色失败时同样关闭光晕，不显示错误颜色。
    /// </summary>
    private void ApplyBloom()
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { Bloom.Background = null; return; }
        bool dark = ActualTheme == ElementTheme.Dark;
        if (AccentColor(dark) is not { } accent) { Bloom.Background = null; return; }

        var bloom = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 0.18),
            GradientOrigin = new Windows.Foundation.Point(0.5, 0.18),
            RadiusX = 0.6,
            RadiusY = 0.55,
        };
        // 与既有观感一致：深色主题稍强、浅色主题稍弱，向同色透明过渡。
        bloom.GradientStops.Add(new GradientStop { Color = Color.FromArgb(dark ? (byte)64 : (byte)56, accent.R, accent.G, accent.B), Offset = 0 });
        bloom.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 });
        Bloom.Background = bloom;
    }

    /// <summary>
    /// 直接向系统读当前强调色，而不是解析 XAML 主题资源：颜色变化的通知到达时，
    /// XAML 自己的强调色资源不一定已经更新，读资源可能还是旧颜色。
    /// </summary>
    private Color? AccentColor(bool dark)
    {
        try
        {
            return _colors.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
