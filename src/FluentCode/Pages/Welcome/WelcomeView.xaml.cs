using System;
using FluentCode.Pages.Account;
using FluentCode.Services.Appearance;
using FluentCode.Services.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentCode.Pages.Welcome;

/// <summary>
/// 首次运行欢迎页（尚未添加任何 OpenCode，或在设置中断开连接后）。
/// 表单即原有 <see cref="OpenCodeConnectionView"/>（三种连接方式、校验与状态文案不变，只隐藏“当前配置”卡片）。
/// 连接成功由主窗口通过设置变化感知，并在短暂显示成功状态后切换到首页标签。
/// </summary>
public sealed partial class WelcomeView : UserControl
{
    private readonly OpenCodeConnectionView _form;

    public WelcomeView(SettingsService settings, DpapiSecretStore secrets)
    {
        InitializeComponent();
        _form = new OpenCodeConnectionView(settings, secrets, showCurrentConnection: false);
        FormHost.Content = _form;
        ActualThemeChanged += (_, _) => ApplyBloom();
        Loaded += (_, _) => ApplyBloom();
    }

    /// <summary>标题栏可拖动区（欢迎态时由主窗口登记）。</summary>
    internal FrameworkElement TitleBarDragElement => DragRegion;

    internal OpenCodeConnectionView Form => _form;

    /// <summary>连接操作进行中（主窗口不在此期间切走欢迎页）。</summary>
    internal bool IsBusy => _form.IsBusy;

    /// <summary>离开欢迎页：取消进行中的操作并清空表单中的秘密。</summary>
    internal void Close() => _form.Close();

    /// <summary>
    /// 卡片后面的强调色光晕。颜色取自系统强调色主题资源（<c>AccentFillColorDefaultBrush</c>：
    /// 深色主题下是较亮的强调色、浅色主题下是较暗的强调色），因此跟随用户在 Windows 里选择的强调色，
    /// 而不再是写死的蓝色。高对比度下整层关闭，让背景保持系统窗口色。
    /// 解析失败（资源不可用的极端时机）时同样关闭光晕，不显示错误颜色。
    /// </summary>
    private void ApplyBloom()
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { Bloom.Background = null; return; }
        if (AccentColor() is not { } accent) { Bloom.Background = null; return; }

        bool dark = ActualTheme == ElementTheme.Dark;
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

    private Color? AccentColor()
    {
        try
        {
            return ThemeResources.Brush(this, "AccentFillColorDefaultBrush") is SolidColorBrush brush ? brush.Color : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
