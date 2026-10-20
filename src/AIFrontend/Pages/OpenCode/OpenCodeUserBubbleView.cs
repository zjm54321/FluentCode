using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 用户自己的消息气泡（wave5 H14）：默认最多显示约 2 行；内容更长时右下角出现原生 chevron 按钮
/// （AutomationId <c>OpenCodeUserMessageToggle</c>，“展开消息 / 收起消息”）切换完整显示。
/// 内容元素（富文本呈现器的根）实例不重建；折叠只限制可见高度（内部 ScrollViewer 以无限高度测量内容，
/// 据 ExtentHeight 判断是否超出两行），不截断或改写文本。展开状态在同一气泡的后续更新中保留。
/// 普通 sealed 业务类（不是控件子类）：原生 Border + Grid + ScrollViewer + Button，内置样式与主题画刷。
/// </summary>
internal sealed class OpenCodeUserBubbleView
{
    /// <summary>折叠时的可见高度：Body 文本两行（行高 20）。</summary>
    internal const double CollapsedHeight = 40;

    private readonly Border _bubble;
    private readonly ScrollViewer _clip;
    private readonly Button _toggle;
    private readonly FontIcon _chevron;
    private bool _expanded;
    private bool _overflows;

    /// <param name="content">要承载的内容元素（实例不重建；由本视图入树）。</param>
    internal OpenCodeUserBubbleView(FrameworkElement content)
    {
        ArgumentNullException.ThrowIfNull(content);

        _clip = new ScrollViewer
        {
            Content = content,
            MaxHeight = CollapsedHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        _clip.SizeChanged += (_, _) => UpdateOverflow();
        content.SizeChanged += (_, _) => UpdateOverflow();

        _chevron = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
        _toggle = new Button
        {
            Content = _chevron,
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        AutomationProperties.SetAutomationId(_toggle, "OpenCodeUserMessageToggle");
        _toggle.Click += (_, _) => SetExpanded(!_expanded);

        var layout = new Grid { ColumnSpacing = 4 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_clip, 0);
        Grid.SetColumn(_toggle, 1);
        layout.Children.Add(_clip);
        layout.Children.Add(_toggle);

        _bubble = new Border
        {
            Child = layout,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 8, 8),
            BorderThickness = new Thickness(1),
        };
        OpenCodeConversationView.ApplyStyle(_bubble, "OpenCodeSubtleCardChromeStyle");
        ApplyToggleText();
    }

    internal FrameworkElement Element => _bubble;

    internal bool IsExpanded => _expanded;

    private void UpdateOverflow()
    {
        var content = _clip.Content as FrameworkElement;
        var full = Math.Max(_clip.ExtentHeight, content?.ActualHeight ?? 0);
        var overflows = full > CollapsedHeight + 1;
        if (overflows == _overflows)
        {
            return;
        }

        _overflows = overflows;
        _toggle.Visibility = overflows || _expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _clip.MaxHeight = expanded ? double.PositiveInfinity : CollapsedHeight;
        _toggle.Visibility = _overflows || expanded ? Visibility.Visible : Visibility.Collapsed;
        ApplyToggleText();
    }

    private void ApplyToggleText()
    {
        _chevron.Glyph = _expanded ? "\uE70E" : "\uE70D";
        var name = _expanded ? "收起消息" : "展开消息";
        AutomationProperties.SetName(_toggle, name);
        ToolTipService.SetToolTip(_toggle, name);
    }
}
