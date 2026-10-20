using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 时间线中工具样式的紧凑单行（F8 / wave5 G4 / H5）：指令（“指令”）、异步完成（“终端完成”“子任务完成”“后台任务完成”）、
/// 插件内部自动消息等。外观与工具行一致（高 32、左对齐）：状态图标（可无）| 类别字形 | 名称（BodyStrong）| 目标（等宽、省略号）| 右侧摘要。
/// 完整内容只在悬停片刻（延迟见 OpenCodeToolDetailPopup.HoverOpenDelayMs）后于共享的 <see cref="OpenCodeToolDetailPopup"/> 中显示；
/// 子代理点击直接打开真实子会话小窗，其余点击固定详情，Esc 关闭。调用/结果属性与正文留在悬停详情中。
/// 普通 sealed 业务类（不是控件子类）：原生 Button（透明工具按钮样式）+ 内置文本样式与主题画刷。
/// </summary>
internal sealed class OpenCodeTimelineChipView : IOpenCodeDetailContentSource
{
    private readonly Grid _root;
    private readonly Border _selectionBar;
    private readonly Border _rowChrome;
    private readonly Button _chip;
    private readonly Grid _statusCell;
    private readonly FontIcon _statusIcon;
    private readonly ProgressRing _progress;
    private readonly FontIcon _icon;
    private readonly TextBlock _label;
    private readonly TextBlock _targetText;
    private readonly TextBlock _summaryText;
    private readonly string _glyph;
    private OpenCodeTimelineRowContent _content = new(string.Empty, string.Empty, string.Empty, null, string.Empty, string.Empty, Array.Empty<OpenCodeToolProperty>(), null);
    private Action? _linkInvoked;
    private bool _disposed;
    private readonly ChildSource _childSource;
    private FrameworkElement? _detailAnchorOverride;
    internal void SetDetailAnchor(FrameworkElement? anchor) => _detailAnchorOverride = anchor;
    internal void OpenDetail() => OpenCodeToolDetailPopup.Shared.TogglePinned(Source);
    internal void HoverEnter() => OpenCodeToolDetailPopup.Shared.HoverEnter(Source);
    internal void HoverExit() => OpenCodeToolDetailPopup.Shared.HoverExit(Source);
    internal OpenCodeTimelineRowContent Content => _content;
    internal string Glyph => EffectiveGlyph;

    /// <param name="automationId">行按钮的 AutomationId。</param>
    /// <param name="glyph">Segoe Fluent Icons 字形。</param>
    /// <param name="labelAutomationId">名称文本的 AutomationId（可空）。</param>
    internal OpenCodeTimelineChipView(string automationId, string glyph, string? labelAutomationId = null)
    {
        _glyph = glyph ?? "\uE946";
        _childSource = new ChildSource(this);
        _statusIcon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
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
        _statusCell = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _statusCell.Children.Add(_statusIcon);
        _statusCell.Children.Add(_progress);

        _icon = new FontIcon { Glyph = _glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));

        _label = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStrongStyleKey);
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.TextWrapping = TextWrapping.NoWrap;
        if (!string.IsNullOrEmpty(labelAutomationId))
        {
            AutomationProperties.SetAutomationId(_label, labelAutomationId);
        }

        _targetText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CodeStyleKey);
        _targetText.IsTextSelectionEnabled = false;
        _targetText.VerticalAlignment = VerticalAlignment.Center;
        _targetText.TextWrapping = TextWrapping.NoWrap;
        _targetText.TextTrimming = TextTrimming.CharacterEllipsis;
        _targetText.Visibility = Visibility.Collapsed;

        _summaryText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        _summaryText.TextWrapping = TextWrapping.NoWrap;
        _summaryText.Visibility = Visibility.Collapsed;

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_statusCell, 0);
        Grid.SetColumn(_icon, 1);
        Grid.SetColumn(_label, 2);
        Grid.SetColumn(_targetText, 3);
        Grid.SetColumn(_summaryText, 4);
        content.Children.Add(_statusCell);
        content.Children.Add(_icon);
        content.Children.Add(_label);
        content.Children.Add(_targetText);
        content.Children.Add(_summaryText);

        _chip = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = 32,
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(0),
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        AutomationProperties.SetAutomationId(_chip, automationId ?? string.Empty);
        AutomationProperties.SetHelpText(_chip, "悬停或按 Enter 查看完整内容，Esc 关闭");
        _chip.Click += (_, _) => OpenCodeToolDetailPopup.Shared.TogglePinned(Source);
        _chip.PointerEntered += (_, _) => OpenCodeToolDetailPopup.Shared.HoverEnter(Source);
        _chip.PointerExited += (_, _) => OpenCodeToolDetailPopup.Shared.HoverExit(Source);
        _chip.KeyDown += OnKeyDown;
        _chip.Unloaded += (_, _) => { OpenCodeToolDetailPopup.Shared.CloseIf(this); OpenCodeToolDetailPopup.Shared.CloseIf(_childSource); };

        _rowChrome = new Border { Child = _chip, CornerRadius = new CornerRadius(4) };

        _selectionBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 6, 0, 6),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        OpenCodeToolTheme.Apply(_selectionBar, OpenCodeToolTheme.Border(OpenCodeToolTheme.Accent));

        _root = new Grid { ColumnSpacing = 2 };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_selectionBar, 0);
        Grid.SetColumn(_rowChrome, 1);
        _root.Children.Add(_selectionBar);
        _root.Children.Add(_rowChrome);
    }

    internal FrameworkElement Element => _root;

    /// <summary>当前名称。</summary>
    internal string Label => _label.Text;

    FrameworkElement IOpenCodeDetailContentSource.DetailAnchor => _detailAnchorOverride ?? _chip;

    OpenCodeDetailHeader IOpenCodeDetailContentSource.DetailHeader
    {
        get
        {
            var (statusText, brush) = _content.Status switch
            {
                OpenCodeToolRowStatus.Completed => ("已完成", OpenCodeToolTheme.Success),
                OpenCodeToolRowStatus.Error => ("出错", OpenCodeToolTheme.Critical),
                OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming => ("运行中", (string?)null),
                _ => (string.Empty, (string?)null),
            };
            return new OpenCodeDetailHeader(_glyph, _content.DetailTitle, _content.Target, statusText, brush, AccessibleName: _content.AccessibleName);
        }
    }

    OpenCodeToolDetailView IOpenCodeDetailContentSource.BuildDetailContent()
    {
        var panel = new StackPanel { Spacing = 12 };
        if (_content.Properties.Count > 0)
        {
            panel.Children.Add(OpenCodeToolDetailContent.PropertyList(_content.Properties));
        }

        if (!_disposed && ReferenceEquals(Source, this) && _linkInvoked is { } invoke && !string.IsNullOrEmpty(_content.LinkText))
        {
            var link = new HyperlinkButton
            {
                Content = _content.LinkText,
                Padding = new Thickness(0, 2, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(link, "OpenCodeTimelineRowLink");
            AutomationProperties.SetName(link, _content.LinkText);
            link.Click += (_, _) =>
            {
                if (!_disposed)
                {
                    // 导航前关闭详情（含大视图），避免其停留在目标会话之上。
                    OpenCodeToolDetailPopup.Shared.Close();
                    invoke();
                }
            };
            panel.Children.Add(link);
        }

        if (!string.IsNullOrEmpty(_content.DetailText))
        {
            if (_content.DetailMarkdown)
            {
                var presenter = new OpenCodeRichTextPresenter();
                presenter.Render(_content.DetailText, OpenCodeRichTextStyle.Body);
                panel.Children.Add(presenter.Element);
            }
            else
            {
                var body = OpenCodeInteractionUi.StyledText(_content.DetailText, OpenCodeInteractionUi.BodyStyleKey, true);
                body.TextWrapping = TextWrapping.Wrap;
                panel.Children.Add(body);
            }
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(OpenCodeToolDetailContent.Caption("（没有内容）"));
        }

        return OpenCodeToolDetailContent.Wrap(panel);
    }

    void IOpenCodeDetailContentSource.OnDetailStateChanged(bool shown, bool pinned)
        => _selectionBar.Visibility = shown && pinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 更新为只有名称的行（无状态图标 / 目标 / 摘要）：detailTitle 为详情标题，properties 为键值说明（可空），detailText 为完整正文（可空）。
    /// </summary>
    internal void Update(
        string label,
        string accessibleName,
        string detailTitle,
        IReadOnlyList<OpenCodeToolProperty>? properties,
        string? detailText)
        => UpdateRow(new OpenCodeTimelineRowContent(
            label ?? string.Empty,
            string.Empty,
            string.Empty,
            null,
            accessibleName ?? label ?? string.Empty,
            detailTitle ?? string.Empty,
            properties ?? Array.Empty<OpenCodeToolProperty>(),
            detailText));

    /// <summary>更新整行内容；linkInvoked 为详情中链接的回调（仅 <see cref="OpenCodeTimelineRowContent.LinkText"/> 非空时显示）。</summary>
    internal void UpdateRow(OpenCodeTimelineRowContent content, Action? linkInvoked = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var previousSource = Source;
        _content = content;
        _linkInvoked = linkInvoked;

        _label.Text = content.Name;
        _targetText.Text = content.Target;
        _targetText.Visibility = content.Target.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _summaryText.Text = content.Summary;
        _summaryText.Visibility = content.Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_chip, content.Target.Length == 0 ? content.Name : content.Name + "  " + content.Target);

        var active = content.Status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming;
        _statusCell.Visibility = content.Status is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsActive = active;
        _progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _statusIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        switch (content.Status)
        {
            case OpenCodeToolRowStatus.Completed:
                _statusIcon.Glyph = "\uE73E";
                OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.Success));
                break;
            case OpenCodeToolRowStatus.Error:
                _statusIcon.Glyph = "\uE711";
                OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.Critical));
                break;
            case OpenCodeToolRowStatus.Unknown:
                _statusIcon.Glyph = "\uE9CE";
                OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
                break;
        }

        if (content.Status == OpenCodeToolRowStatus.Error)
        {
            OpenCodeToolTheme.Apply(_rowChrome, OpenCodeToolTheme.Border(OpenCodeToolTheme.CriticalBackground));
        }
        else
        {
            _rowChrome.Style = null;
            _rowChrome.Background = null;
        }

        AutomationProperties.SetName(_chip, content.AccessibleName);

        if (!_disposed)
        {
            OpenCodeToolDetailPopup.Shared.Refresh(Source);
        }
    }

    internal void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _linkInvoked = null;
        OpenCodeToolDetailPopup.Shared.CloseIf(this);
        OpenCodeToolDetailPopup.Shared.CloseIf(_childSource);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && OpenCodeToolDetailPopup.Shared.IsShowing(Source))
        {
            OpenCodeToolDetailPopup.Shared.Close();
            e.Handled = true;
        }
    }

    private sealed class ChildSource(OpenCodeTimelineChipView owner) : IOpenCodeDetailContentSource, IOpenCodeChildChatSource
    {
        public FrameworkElement DetailAnchor => owner._detailAnchorOverride ?? owner._chip;
        public OpenCodeDetailHeader DetailHeader => ((IOpenCodeDetailContentSource)owner).DetailHeader;
        public OpenCodeToolDetailView BuildDetailContent() => ((IOpenCodeDetailContentSource)owner).BuildDetailContent();
        public void OnDetailStateChanged(bool shown, bool pinned) => ((IOpenCodeDetailContentSource)owner).OnDetailStateChanged(shown, pinned);
        public void OpenChildChat() { if (!owner._disposed) owner._linkInvoked?.Invoke(); }
    }
}
