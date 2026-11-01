using FluentCode.Pages.Account;
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

    private void ApplyBloom()
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { Bloom.Background = null; return; }
        bool dark = ActualTheme == ElementTheme.Dark;
        var bloom = new RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 0.18),
            GradientOrigin = new Windows.Foundation.Point(0.5, 0.18),
            RadiusX = 0.6,
            RadiusY = 0.55,
        };
        bloom.GradientStops.Add(new GradientStop { Color = dark ? Color.FromArgb(64, 0x1C, 0x86, 0xDE) : Color.FromArgb(56, 0x62, 0xCD, 0xFF), Offset = 0 });
        bloom.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0x1C, 0x86, 0xDE), Offset = 1 });
        Bloom.Background = bloom;
    }
}
