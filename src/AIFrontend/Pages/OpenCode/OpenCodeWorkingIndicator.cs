using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// B17：会话时间线进行中分组的 working 指示器——平滑转动的齿轮 + 逐步点亮的省略号 + 装饰状态词 + 实时耗时。
/// </summary>
/// <remarks>
/// <para>动画全部由 B13 共享工作时钟的合成相位驱动（<see cref="OpenCodeWorkingClockComposition"/>），不新建任何计时器：
/// 齿轮 <c>RotationAngleInDegrees = Phase × 360</c>（一周 = 时钟周期 1.8 秒），省略号按 <c>Floor(Phase × 4)</c> 逐个点亮
/// （0 → 1 → 2 → 3 个点，每步 0.45 秒）。所有 working 指示器与标签流光同相。</para>
/// <para>仅“已加载 + working”时加入时钟；完成（Done）、隐藏、卸载、释放即离开，最后一个离开时时钟停机。
/// 系统关闭动画时降级为静止齿轮 + 静态“…”。</para>
/// <para>词条只是装饰：由宿主写入的真实状态文本（错误 / 提问 / 权限）不经过这里。</para>
/// </remarks>
internal sealed class OpenCodeWorkingIndicator : IOpenCodeWorkingClockSubscriber
{
    internal const string GearGlyph = "\uE9F5";

    private readonly Grid _gear;
    private readonly FontIcon _gearIcon;
    private readonly Grid _label;
    private readonly TextBlock _phrase;
    private readonly StackPanel _dots;
    private readonly TextBlock[] _dotTexts = new TextBlock[OpenCodeWorkingMotion.DotCount];
    private readonly TextBlock _elapsed;
    private bool _active;
    private bool _loaded;
    private bool _animating;
    private string _fullPhrase = string.Empty;
    private readonly List<(UIElement Element, long Token)> _visibilityWatches = new();

    internal OpenCodeWorkingIndicator(string automationSuffix)
    {
        _gearIcon = new FontIcon { Glyph = GearGlyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_gearIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.Accent));
        // 固定 16×16 方格，旋转中心恒为 (8, 8)：齿轮原地转动，不晃动布局。
        _gear = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _gear.Children.Add(_gearIcon);
        AutomationProperties.SetAccessibilityView(_gear, AccessibilityView.Raw);

        _phrase = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _phrase.FontSize = 13;
        _phrase.VerticalAlignment = VerticalAlignment.Center;
        _phrase.TextWrapping = TextWrapping.NoWrap;
        // 窄窗格：状态词 / 当前步骤（可能是长命令或路径）先以省略号收尾，点与耗时始终完整可见。
        _phrase.TextTrimming = TextTrimming.CharacterEllipsis;
        _dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        for (var i = 0; i < _dotTexts.Length; i++)
        {
            var dot = OpenCodeInteractionUi.StyledText(".", OpenCodeInteractionUi.CaptionStyleKey);
            dot.FontSize = 13;
            dot.VerticalAlignment = VerticalAlignment.Center;
            _dotTexts[i] = dot;
            _dots.Children.Add(dot);
        }

        AutomationProperties.SetAccessibilityView(_dots, AccessibilityView.Raw);
        _elapsed = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _elapsed.FontSize = 13;
        OpenCodeToolTheme.Apply(_elapsed, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CaptionStyleKey));
        _elapsed.VerticalAlignment = VerticalAlignment.Center;
        _elapsed.TextWrapping = TextWrapping.NoWrap;
        _elapsed.Margin = new Thickness(6, 0, 0, 0);

        // 左对齐 Grid：状态词在“按内容、但不超过可用宽度”的星号列中，可截断；点与耗时为 Auto 列，紧随其后。
        _label = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _label.Children.Add(_phrase);
        _label.Children.Add(_dots);
        _label.Children.Add(_elapsed);
        Grid.SetColumn(_dots, 1);
        Grid.SetColumn(_elapsed, 2);
        AutomationProperties.SetAutomationId(_label, "OpenCodeWorkingIndicator." + automationSuffix);
        _label.Loaded += (_, _) => { _loaded = true; WatchVisibility(); Update(); };
        _label.Unloaded += (_, _) => { _loaded = false; UnwatchVisibility(); Update(); };
    }

    /// <summary>放在分组头部图标格中的齿轮。</summary>
    internal FrameworkElement Gear => _gear;

    /// <summary>放在分组标题簇中的“状态词 + 点 + 耗时”。</summary>
    internal FrameworkElement Label => _label;

    internal bool IsActive => _active;
    internal bool IsVisibleWhileLoaded { get; private set; }
    internal event Action? VisibilityChanged;

    /// <summary>当前完整状态词（含“…”），供读屏与测试。</summary>
    internal string Phrase => _fullPhrase;

    /// <summary>由宿主按真实 working 状态切换；false 时立即离开共享时钟并隐藏。</summary>
    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        var visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _gear.Visibility = visibility;
        _label.Visibility = visibility;
        Update();
    }

    internal void SetText(string phrase, string elapsed, bool decorative = true)
    {
        _fullPhrase = phrase ?? string.Empty;
        var stem = decorative ? OpenCodeWorkingPhrases.Stem(_fullPhrase) : _fullPhrase;
        _dots.Visibility = decorative ? Visibility.Visible : Visibility.Collapsed;
        if (!string.Equals(_phrase.Text, stem, StringComparison.Ordinal)) _phrase.Text = stem;
        if (!string.Equals(_elapsed.Text, elapsed, StringComparison.Ordinal)) _elapsed.Text = elapsed ?? string.Empty;
        _elapsed.Visibility = string.IsNullOrEmpty(elapsed) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_label, string.IsNullOrEmpty(elapsed) ? _fullPhrase : _fullPhrase + " " + elapsed);
    }

    /// <summary>宿主释放：离开时钟、停掉合成动画。</summary>
    internal void Dispose()
    {
        _active = false;
        _loaded = false;
        UnwatchVisibility();
        Update();
    }

    void IOpenCodeWorkingClockSubscriber.OnWorkingClockStateChanged(OpenCodeWorkingClock clock) => Render();

    private void Update()
    {
        var clock = OpenCodeWorkingClock.Shared;
        var visible = _active && _loaded && AncestorsVisible();
        if (IsVisibleWhileLoaded != visible)
        {
            IsVisibleWhileLoaded = visible;
            VisibilityChanged?.Invoke();
        }
        if (visible)
        {
            OpenCodeWorkingClockComposition.EnsureInitialized();
            if (!clock.Join(this)) Render();
        }
        else
        {
            clock.Leave(this);
            Render();
        }
    }

    private bool AncestorsVisible()
    {
        for (DependencyObject? node = _label; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private void WatchVisibility()
    {
        UnwatchVisibility();
        for (DependencyObject? node = _label; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element)
                _visibilityWatches.Add((element, element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Update())));
    }

    private void UnwatchVisibility()
    {
        foreach (var (element, token) in _visibilityWatches)
            element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        _visibilityWatches.Clear();
    }

    private void Render()
    {
        var clock = OpenCodeWorkingClock.Shared;
        if (clock.IsJoined(this) && clock.MotionEnabled) StartMotion();
        else StopMotion();
    }

    private void StartMotion()
    {
        if (_animating) return;
        try
        {
            var gearVisual = ElementCompositionPreview.GetElementVisual(_gear);
            var compositor = gearVisual.Compositor;
            var phase = OpenCodeWorkingClockComposition.PhaseSet(compositor);
            gearVisual.CenterPoint = new Vector3(8, 8, 0);
            var rotation = compositor.CreateExpressionAnimation(OpenCodeWorkingMotion.GearExpression);
            rotation.SetReferenceParameter("clock", phase);
            gearVisual.StartAnimation(nameof(Visual.RotationAngleInDegrees), rotation);
            for (var i = 0; i < _dotTexts.Length; i++)
            {
                var visual = ElementCompositionPreview.GetElementVisual(_dotTexts[i]);
                var step = compositor.CreateExpressionAnimation(OpenCodeWorkingMotion.DotExpression);
                step.SetReferenceParameter("clock", phase);
                step.SetScalarParameter("i", i);
                visual.StartAnimation(nameof(Visual.Opacity), step);
            }

            _animating = true;
        }
        catch
        {
            // 合成不可用时退回静态外观（装饰动画失败不影响会话状态）。
            _animating = true;
            StopMotion();
        }
    }

    /// <summary>静态外观：齿轮回正、三个点全亮（静态“…”）。</summary>
    private void StopMotion()
    {
        if (!_animating) return;
        _animating = false;
        try
        {
            var gearVisual = ElementCompositionPreview.GetElementVisual(_gear);
            gearVisual.StopAnimation(nameof(Visual.RotationAngleInDegrees));
            gearVisual.RotationAngleInDegrees = 0;
            foreach (var dot in _dotTexts)
            {
                var visual = ElementCompositionPreview.GetElementVisual(dot);
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.Opacity = 1;
            }
        }
        catch
        {
        }
    }
}
