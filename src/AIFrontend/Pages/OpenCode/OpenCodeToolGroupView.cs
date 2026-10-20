using System;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 时间线“处理过程”分组（F5，AutomationId <c>OpenCodeToolGroup.{首个部件 id}</c>）：一个助手回合中两段文本输出之间的
/// 全部思考 / 工具调用折叠为一行：整轮进行中保持展开，整轮终态首次到达后收起；历史分组保留独立手动选择。
/// 头部（wave5 H2）：“▸ 已处理 12 秒”，右侧只显示结果计数——成功 “n ✓”（CheckMark 字形 + 成功色）与出错 “m ✗”
/// （Cancel 字形 + 危险色，仅 m &gt; 0 时显示，从不隐藏）；不再显示“思考 · N 个工具调用 · edit×…”文字摘要（仍保留在读屏名称中）。
/// 进行中显示右侧 ProgressRing + “处理中… N 秒”。单叶头部直接预览/打开叶详情；多叶历史头部只切换本组列表。
/// 同一回合跨多条 assistant 消息（每步一条）时，只有链首分组显示头部，后续分组隐藏头部并跟随链首的展开状态
/// （由 <see cref="OpenCodeConversationView"/> 计算并调用 <see cref="Update"/>）。
/// 原生 Button + StackPanel + Border，内置文本样式与主题画刷；不派生控件、不自定义模板（不使用 Expander 子类）。
/// </summary>
internal sealed class OpenCodeToolGroupView
{
    private readonly Action<string, bool> _onUserToggled;
    private readonly StackPanel _root;
    private readonly Button _header;
    private readonly Grid _headerRow;
    private readonly Button _backgroundButton;
    private Action? _backgroundAction;
    private readonly FontIcon _chevron;
    private readonly FontIcon _icon;
    private readonly TextBlock _headerText;
    private readonly TextBlock _leafLabel;
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
    private bool _pointerOver;
    private Action? _openLeaf;
    private Action? _enterLeaf;
    private Action? _exitLeaf;
    private string? _leafGlyph;
    private OpenCodeToolRowStatus? _leafStatus;
    private bool _inlineLeaf;

    internal void SetInlineLeaf(bool inline)
    {
        _inlineLeaf = inline;
        _headerRow.Visibility = _headerVisible && !inline ? Visibility.Visible : Visibility.Collapsed;
        _itemsBorder.Margin = inline ? new Thickness(0) : new Thickness(15, 0, 0, 0);
        _itemsBorder.Padding = inline ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        _itemsBorder.BorderThickness = inline ? new Thickness(0) : new Thickness(1, 0, 0, 0);
        _itemsBorder.Visibility = inline || _expanded && _openLeaf is null ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetBackgroundAction(Action? action)
    {
        _backgroundAction = action;
        _backgroundButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void BindSingleLeaf(Action? open, Action? enter, Action? exit, string? name = null, string? glyph = null, OpenCodeToolRowStatus? status = null)
    {
        _openLeaf = open;
        _enterLeaf = enter;
        _exitLeaf = exit;
        _leafGlyph = glyph;
        _leafStatus = status;
        _icon.Glyph = glyph ?? (_summary.ReasoningOnly ? "\uEA80" : "\uE9F5");
        _leafLabel.Text = name ?? string.Empty;
        _leafLabel.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
        _chevron.Visibility = open is null ? Visibility.Visible : Visibility.Collapsed;
        _itemsBorder.Visibility = open is null && _expanded ? Visibility.Visible : Visibility.Collapsed;
        RefreshHeaderText();
        RefreshProgress();
    }

    internal OpenCodeToolGroupView(string groupId, Action<string, bool> onUserToggled)
    {
        GroupId = groupId ?? string.Empty;
        _onUserToggled = onUserToggled ?? throw new ArgumentNullException(nameof(onUserToggled));

        // 展开箭头紧跟标题文字（左对齐，不再位于最右侧）；仅指针悬停于分组内时可见。
        // 用 Opacity 而非 Visibility 切换：占位不变，悬停时标题与计数不跳动。
        _chevron = new FontIcon
        {
            Glyph = "\uE76C",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-4, 0, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        OpenCodeToolTheme.Apply(_chevron, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        _icon = new FontIcon { Glyph = "\uE9F5", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        _headerText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _headerText.VerticalAlignment = VerticalAlignment.Center;
        _headerText.TextWrapping = TextWrapping.NoWrap;
        _leafLabel = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _leafLabel.VerticalAlignment = VerticalAlignment.Center;
        _leafLabel.TextWrapping = TextWrapping.NoWrap;
        _leafLabel.Visibility = Visibility.Collapsed;
        (_successTally, _successText) = CreateTally("\uE73E", OpenCodeToolTheme.Success, "OpenCodeToolGroup.Success." + GroupId);
        (_errorTally, _errorText) = CreateTally("\uE711", OpenCodeToolTheme.Critical, "OpenCodeToolGroup.Errors." + GroupId);
        var tally = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        tally.Children.Add(_successTally);
        tally.Children.Add(_errorTally);
        _progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            MinWidth = 16,
            MinHeight = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 将可选叶名称放入同一标题簇，避免空 Auto 列仍多占一个列间距：各类同行的文字起点一致。
        var titleCluster = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleCluster.Children.Add(_leafLabel);
        titleCluster.Children.Add(_headerText);
        titleCluster.Children.Add(_chevron);
        // 列：图标 | 标题簇（叶名称 / 主标题 / 悬停箭头） | 弹性空白 | 结果计数（右对齐不变）。
        var iconCell = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        iconCell.Children.Add(_icon);
        tally.Children.Add(_progress);
        var headerContent = new Grid { ColumnSpacing = 8 };
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(iconCell, 0);
        Grid.SetColumn(titleCluster, 1);
        Grid.SetColumn(tally, 2);
        headerContent.Children.Add(iconCell);
        headerContent.Children.Add(titleCluster);

        _header = new Button
        {
            Content = headerContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(5, 0, 0, 0),
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

            if (_openLeaf is { } open) { open(); return; }
            if (_summary.IsActive) return;
            SetExpanded(!_expanded);
            _onUserToggled(GroupId, _expanded);
        };
        _header.PointerEntered += (_, _) =>
        {
            _enterLeaf?.Invoke();
        };
        _header.PointerExited += (_, _) =>
        {
            _exitLeaf?.Invoke();
        };

        _items = new StackPanel { Spacing = 0 };
        _itemsBorder = new Border
        {
            Child = _items,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Margin = new Thickness(15, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        OpenCodeToolTheme.Apply(_itemsBorder, OpenCodeToolTheme.Border("SubtleFillColorTransparentBrush", OpenCodeToolTheme.Divider));

        _root = new StackPanel { Spacing = 0 };
        _headerRow = new Grid();
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _headerRow.Children.Add(_header);
        _backgroundButton = new Button
        {
            Content = OpenCodeInteractionUi.StyledText("转后台", OpenCodeInteractionUi.CaptionStyleKey),
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetAutomationId(_backgroundButton, "OpenCodeToolGroup.Background." + GroupId);
        AutomationProperties.SetName(_backgroundButton, "将本会话前台工具转入后台");
        ToolTipService.SetToolTip(_backgroundButton, "将本会话中正在前台运行的终端和子代理转入后台");
        _backgroundButton.Click += (_, _) => { if (!_disposed) _backgroundAction?.Invoke(); };
        Grid.SetColumn(_backgroundButton, 1);
        _headerRow.Children.Add(_backgroundButton);
        tally.Margin = new Thickness(0, 0, 8, 0);
        _headerRow.Children.Add(tally);
        _root.Children.Add(_headerRow);
        _root.Children.Add(_itemsBorder);
        _root.PointerEntered += (_, _) => { _pointerOver = true; UpdateChevronAffordance(); };
        _root.PointerExited += (_, _) => { _pointerOver = false; UpdateChevronAffordance(); };
        _root.PointerCanceled += (_, _) => { _pointerOver = false; UpdateChevronAffordance(); };
        _root.Unloaded += (_, _) => StopTimer();
        _root.Loaded += (_, _) => UpdateTimer();
    }

    internal string GroupId { get; }

    internal FrameworkElement Element => _root;
    internal FrameworkElement DetailAnchor => _header;

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
        _headerRow.Visibility = headerVisible && !_inlineLeaf ? Visibility.Visible : Visibility.Collapsed;
        _successText.Text = summary.SuccessCount.ToString(CultureInfo.InvariantCulture);
        _successTally.Visibility = summary.SuccessCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_successTally, summary.SuccessCount.ToString(CultureInfo.InvariantCulture) + " 个成功");
        _errorText.Text = summary.ErrorCount.ToString(CultureInfo.InvariantCulture);
        _errorTally.Visibility = summary.ErrorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_errorTally, summary.ErrorCount.ToString(CultureInfo.InvariantCulture) + " 个出错");
        AIFrontend.Services.OpenCode.OpenCodeCompletionCrashTrace.Mark("tool-group.progress.before", summary.IsActive.ToString());
        RefreshProgress();
        _icon.Visibility = Visibility.Visible;
        _icon.Glyph = _leafGlyph ?? (summary.ReasoningOnly ? "\uEA80" : "\uE9F5");
        RefreshHeaderText();
        AIFrontend.Services.OpenCode.OpenCodeCompletionCrashTrace.Mark("tool-group.expansion.before", expanded.ToString());
        SetExpanded(expanded);
        AIFrontend.Services.OpenCode.OpenCodeCompletionCrashTrace.Mark("tool-group.expansion.after", expanded.ToString());
        UpdateTimer();
    }

    internal void Dispose()
    {
        _disposed = true;
        _backgroundAction = null;
        _openLeaf = null;
        _enterLeaf = null;
        _exitLeaf = null;
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
    {
        // A single logical operation is the header itself, never “读取 · 已处理”.
        var backgroundRunning = _openLeaf is not null && _leafLabel.Text.EndsWith("（后台）", StringComparison.Ordinal)
            && _leafStatus == OpenCodeToolRowStatus.Running;
        _headerText.Visibility = _openLeaf is null || backgroundRunning ? Visibility.Visible : Visibility.Collapsed;
        if (backgroundRunning) { _headerText.Text = "后台运行中"; return; }
        _headerText.Text = _summary.IsActive
            ? OpenCodeStepRunPresentation.FormatActiveHeader(_summary.StartedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : _summary.HeaderText;
    }

    private void RefreshProgress()
    {
        var active = _openLeaf is not null ? _leafStatus is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming : _summary.IsActive;
        _progress.IsActive = active;
        _progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

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

    /// <summary>箭头仅在指针悬停于分组内时显示；其余时间透明（保留占位，标题不跳动）。</summary>
    private void UpdateChevronAffordance()
        => _chevron.Opacity = _pointerOver ? 1 : 0;

    private void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        _itemsBorder.Visibility = _inlineLeaf || expanded && _openLeaf is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_header, (_openLeaf is null ? string.Empty : _leafLabel.Text + "，") + _summary.AccessibleName + (expanded ? "，已展开" : "，已折叠"));
        AutomationProperties.SetHelpText(_header, _openLeaf is not null ? "按 Enter 查看详情" : _summary.IsActive ? "本轮处理中，处理过程保持展开" : expanded ? "按 Enter 收起处理过程" : "按 Enter 展开处理过程");
    }
}
