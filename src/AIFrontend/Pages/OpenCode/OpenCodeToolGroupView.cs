using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 时间线“处理过程”分组（F5，AutomationId <c>OpenCodeToolGroup.{首个部件 id}</c>）：一个助手回合中两段文本输出之间的
/// 全部思考 / 工具调用折叠为一行：进行中自动展开（实时显示各行），结束后自动收起；加载时已结束的历史分组默认收起。
/// 头部（wave5 H2）：“▸ 已处理 12 秒”，右侧只显示结果计数——成功 “n ✓”（CheckMark 字形 + 成功色）与出错 “m ✗”
/// （Cancel 字形 + 危险色，仅 m &gt; 0 时显示，从不隐藏）；不再显示“思考 · N 个工具调用 · edit×…”文字摘要（仍保留在读屏名称中）。
/// 进行中显示 ProgressRing + “处理中… N 秒”（每秒刷新）。点击头部只展开 / 收起本组（H1：各组独立）为缩进的竖直列表（左侧细竖线）。
/// 同一回合跨多条 assistant 消息（每步一条）时，只有链首分组显示头部，后续分组隐藏头部并跟随链首的展开状态
/// （由 <see cref="OpenCodeConversationView"/> 计算并调用 <see cref="Update"/>）。
/// 原生 Button + StackPanel + Border，内置文本样式与主题画刷；不派生控件、不自定义模板（不使用 Expander 子类）。
/// </summary>
internal sealed class OpenCodeToolGroupView
{
    private readonly Action<string, bool> _onUserToggled;
    private readonly StackPanel _root;
    private readonly Button _header;
    private readonly FontIcon _chevron;
    private readonly FontIcon _icon;
    private readonly TextBlock _headerText;
    private readonly StackPanel _successTally;
    private readonly TextBlock _successText;
    private readonly StackPanel _errorTally;
    private readonly TextBlock _errorText;
    private readonly ProgressRing _progress;
    private readonly Border _itemsBorder;
    private readonly StackPanel _items;
    private OpenCodeStepRunSummary _summary = new(string.Empty, string.Empty, 0, 0, false, null, false, string.Empty);
    private DispatcherQueueTimer? _timer;
    private bool _expanded;
    private bool _headerVisible = true;
    private bool _initialized;
    private bool _disposed;

    internal OpenCodeToolGroupView(string groupId, Action<string, bool> onUserToggled)
    {
        GroupId = groupId ?? string.Empty;
        _onUserToggled = onUserToggled ?? throw new ArgumentNullException(nameof(onUserToggled));

        _chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_chevron, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        _icon = new FontIcon { Glyph = "\uE9F5", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        _headerText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
        _headerText.VerticalAlignment = VerticalAlignment.Center;
        _headerText.TextWrapping = TextWrapping.NoWrap;
        (_successTally, _successText) = CreateTally("\uE73E", OpenCodeToolTheme.Success, "OpenCodeToolGroup.Success." + GroupId);
        (_errorTally, _errorText) = CreateTally("\uE711", OpenCodeToolTheme.Critical, "OpenCodeToolGroup.Errors." + GroupId);
        var tally = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        tally.Children.Add(_successTally);
        tally.Children.Add(_errorTally);
        _progress = new ProgressRing
        {
            Width = 12,
            Height = 12,
            MinWidth = 12,
            MinHeight = 12,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 列：箭头 | 图标/进度 | 主标题 | 弹性空白 | 结果计数（n ✓ / m ✗）。
        var iconCell = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        iconCell.Children.Add(_icon);
        iconCell.Children.Add(_progress);
        var headerContent = new Grid { ColumnSpacing = 8 };
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_chevron, 0);
        Grid.SetColumn(iconCell, 1);
        Grid.SetColumn(_headerText, 2);
        Grid.SetColumn(tally, 4);
        headerContent.Children.Add(_chevron);
        headerContent.Children.Add(iconCell);
        headerContent.Children.Add(_headerText);
        headerContent.Children.Add(tally);

        _header = new Button
        {
            Content = headerContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(0),
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        AutomationProperties.SetAutomationId(_header, "OpenCodeToolGroup." + GroupId);
        _header.Click += (_, _) =>
        {
            if (_disposed)
            {
                return;
            }

            SetExpanded(!_expanded);
            _onUserToggled(GroupId, _expanded);
        };

        _items = new StackPanel { Spacing = 0 };
        _itemsBorder = new Border
        {
            Child = _items,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Margin = new Thickness(15, 0, 0, 4),
            Padding = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        OpenCodeToolTheme.Apply(_itemsBorder, OpenCodeToolTheme.Border("SubtleFillColorTransparentBrush", OpenCodeToolTheme.Divider));

        _root = new StackPanel { Spacing = 0 };
        _root.Children.Add(_header);
        _root.Children.Add(_itemsBorder);
        _root.Unloaded += (_, _) => StopTimer();
        _root.Loaded += (_, _) => UpdateTimer();
    }

    internal string GroupId { get; }

    internal FrameworkElement Element => _root;

    /// <summary>组内行（思考 / 工具）的容器（由会话视图对齐子元素）。</summary>
    internal Panel ItemsHost => _items;

    internal bool IsExpanded => _expanded;

    /// <summary>
    /// 更新摘要与展开状态。headerVisible = false 表示本组是跨消息链的后续段：隐藏头部，仅在链首展开时显示列表。
    /// </summary>
    internal void Update(OpenCodeStepRunSummary summary, bool expanded, bool headerVisible)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (_initialized && summary == _summary && expanded == _expanded && headerVisible == _headerVisible)
        {
            // 每次刷新都会重算全部分组：内容未变时不触碰元素（值相等的摘要记录）。
            return;
        }

        _initialized = true;
        _summary = summary;
        _headerVisible = headerVisible;
        _header.Visibility = headerVisible ? Visibility.Visible : Visibility.Collapsed;
        _successText.Text = summary.SuccessCount.ToString(CultureInfo.InvariantCulture);
        _successTally.Visibility = summary.SuccessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_successTally, summary.SuccessCount.ToString(CultureInfo.InvariantCulture) + " 个成功");
        _errorText.Text = summary.ErrorCount.ToString(CultureInfo.InvariantCulture);
        _errorTally.Visibility = summary.ErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_errorTally, summary.ErrorCount.ToString(CultureInfo.InvariantCulture) + " 个出错");
        _progress.IsActive = summary.IsActive;
        _progress.Visibility = summary.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _icon.Visibility = summary.IsActive ? Visibility.Collapsed : Visibility.Visible;
        _icon.Glyph = summary.ReasoningOnly ? "\uEA80" : "\uE9F5";
        RefreshHeaderText();
        SetExpanded(expanded);
        UpdateTimer();
    }

    internal void Dispose()
    {
        _disposed = true;
        StopTimer();
    }

    /// <summary>结果计数：“n” + 语义色 Fluent 字形（CheckMark / Cancel），数字与字形同色（主题样式，随主题更新）。</summary>
    private static (StackPanel Panel, TextBlock Text) CreateTally(string glyph, string brushKey, string automationId)
    {
        var text = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        OpenCodeToolTheme.Apply(text, OpenCodeToolTheme.Text(brushKey, OpenCodeInteractionUi.CaptionStyleKey));
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextWrapping = TextWrapping.NoWrap;
        var icon = new FontIcon { Glyph = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(brushKey));
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(text);
        panel.Children.Add(icon);
        AutomationProperties.SetAutomationId(panel, automationId);
        return (panel, text);
    }

    private void RefreshHeaderText()
        => _headerText.Text = _summary.IsActive
            ? OpenCodeStepRunPresentation.FormatActiveHeader(_summary.StartedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : _summary.HeaderText;

    /// <summary>进行中且开始时间可信时每秒刷新“处理中… N 秒”；否则停止计时器。</summary>
    private void UpdateTimer()
    {
        var needed = !_disposed && _headerVisible && _summary.IsActive
            && OpenCodeStepRunPresentation.IsPlausibleTimestamp(_summary.StartedAt);
        if (!needed)
        {
            StopTimer();
            return;
        }

        if (_timer is null)
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                return;
            }

            _timer = queue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) =>
            {
                if (_disposed || !_summary.IsActive)
                {
                    StopTimer();
                    return;
                }

                RefreshHeaderText();
            };
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    private void StopTimer()
    {
        if (_timer is { IsRunning: true } timer)
        {
            timer.Stop();
        }
    }

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        _itemsBorder.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_header, _summary.AccessibleName + (expanded ? "，已展开" : "，已折叠"));
        AutomationProperties.SetHelpText(_header, expanded ? "按 Enter 收起处理过程" : "按 Enter 展开处理过程");
    }
}
