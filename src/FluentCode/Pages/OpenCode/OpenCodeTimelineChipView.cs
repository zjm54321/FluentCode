using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 时间线中工具样式的紧凑单行（F8 / wave5 G4 / H5）：指令（“指令”）、异步完成（“终端完成”“子任务完成”“后台任务完成”）、
/// 插件内部自动消息等。外观与工具行一致（进行中 36px / 已结束 30px、24px 图标列）：类别字形 | 名称 | 目标 | 摘要 | 右侧状态字形。
/// 完整内容只在悬停片刻（延迟见 OpenCodeToolDetailPopup.HoverOpenDelayMs）后于共享的 <see cref="OpenCodeToolDetailPopup"/> 中显示；
/// 子智能体点击直接打开真实子会话小窗，其余点击固定详情，Esc 关闭。调用/结果属性与正文留在悬停详情中。
/// 普通 sealed 业务类（不是控件子类）：原生 Button（透明工具按钮样式）+ 内置文本样式与主题画刷。
/// </summary>
internal sealed class OpenCodeTimelineChipView : IOpenCodeDetailContentSource
{
    private readonly Grid _root;
    private readonly Border _selectionBar;
    private readonly Border _rowChrome;
    private readonly Button _chip;
    private readonly OpenCodeTimelineStatusCell _status = new();
    private readonly FontIcon _statusIcon;
    private readonly FontIcon _icon;
    private readonly OpenCodeTerminalGlyphIcon _iconVisual;
    private readonly TextBlock _label;
    private readonly TextBlock _targetText;
    private readonly TextBlock _summaryText;
    private readonly string _glyph;
    private readonly bool _completionNotice;
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
    private IOpenCodeDetailContentSource Source => (_completionNotice || OpenCodeStepRunPresentation.IsSubagentLabel(_content.Name)) && _linkInvoked is not null ? _childSource : this;
    /// <summary>Segoe Fluent “Completed” (circle check, same as other async-completion rows).</summary>
    internal const string CompletionNoticeDoneGlyph = "\uE930";

    /// <summary>Segoe Fluent “Error” (circle exclamation): the same outlined family for failed / cancelled notices.</summary>
    internal const string CompletionNoticeFailedGlyph = "\uE783";

    private string EffectiveGlyph => _completionNotice
        ? _content.Status == OpenCodeToolRowStatus.Completed ? CompletionNoticeDoneGlyph : CompletionNoticeFailedGlyph
        : OpenCodeStepRunPresentation.IsSubagentLabel(_content.Name) ? OpenCodeStepRunPresentation.SubagentGlyph
        : _content.Name == "终端（后台）" ? OpenCodeStepRunPresentation.TerminalGlyph : _glyph;

    /// <param name="automationId">行按钮的 AutomationId。</param>
    /// <param name="glyph">Segoe Fluent Icons 字形。</param>
    /// <param name="labelAutomationId">名称文本的 AutomationId（可空）。</param>
    /// <param name="completionNotice">父会话完成通知：Fluent 完成 / 出错字形、强调色名称/终态、次要色任务，无工具状态列。</param>
    internal OpenCodeTimelineChipView(string automationId, string glyph, string? labelAutomationId = null, bool completionNotice = false)
    {
        _glyph = glyph ?? "\uE946";
        _completionNotice = completionNotice;
        _childSource = new ChildSource(this);
        _statusIcon = _status.Icon;
        _statusIcon.Visibility = Visibility.Collapsed;

        _icon = new FontIcon { Glyph = _glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        _iconVisual = new(_icon);

        _label = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.TextWrapping = TextWrapping.NoWrap;
        if (!string.IsNullOrEmpty(labelAutomationId))
        {
            AutomationProperties.SetAutomationId(_label, labelAutomationId);
        }

        _targetText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        _targetText.IsTextSelectionEnabled = false;
        _targetText.VerticalAlignment = VerticalAlignment.Center;
        _targetText.TextWrapping = TextWrapping.NoWrap;
        _targetText.TextTrimming = TextTrimming.CharacterEllipsis;
        _targetText.Visibility = Visibility.Collapsed;
        // "Designer 已完成 · 任务" reads as one phrase: keep the old 4px join without leaving the shared 8px icon gap.
        if (_completionNotice) _targetText.Margin = new Thickness(-4, 0, 0, 0);

        _summaryText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _summaryText.VerticalAlignment = VerticalAlignment.Center;
        _summaryText.TextWrapping = TextWrapping.NoWrap;
        _summaryText.Visibility = Visibility.Collapsed;

        var content = new Grid { ColumnSpacing = OpenCodeTimelineRowMetrics.ColumnGap };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(OpenCodeTimelineRowMetrics.IconColumn) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_iconVisual, 0);
        Grid.SetColumn(_label, 1);
        Grid.SetColumn(_targetText, 2);
        Grid.SetColumn(_summaryText, 3);
        content.Children.Add(_iconVisual);
        content.Children.Add(_label);
        content.Children.Add(_targetText);
        content.Children.Add(_summaryText);

        _chip = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = OpenCodeTimelineRowMetrics.Height(null),
            Padding = new Thickness(8, 0, 8, 0),
            BorderThickness = new Thickness(0),
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        AutomationProperties.SetAutomationId(_chip, automationId ?? string.Empty);
        AutomationProperties.SetHelpText(_chip, _completionNotice ? "按 Enter 打开子会话，悬停查看结果" : "悬停或按 Enter 查看完整内容，Esc 关闭");
        _chip.Click += (_, _) => OpenCodeToolDetailPopup.Shared.TogglePinned(Source);
        // A8: capture the entry point immediately (not on first move) so the preview centres on the pointer.
        // Touch never starts the hover preview (a pan would leave it open); tap opens the pinned detail.
        _chip.PointerEntered += (_, e) => { if (OpenCodeTouchInput.IsTouch(e)) return; OpenCodeToolDetailPopup.Shared.TrackPointer(_chip, e); OpenCodeToolDetailPopup.Shared.HoverEnter(Source); };
        _chip.PointerMoved += (_, e) => { if (!OpenCodeTouchInput.IsTouch(e)) OpenCodeToolDetailPopup.Shared.TrackPointer(_chip, e); };
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

        // Same trailing columns as tool rows: [actions (empty here)][shared status cell], so rings line up row to row.
        // Completion notices have no status column (their glyph carries the outcome).
        _root = new Grid { ColumnSpacing = 2 };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_selectionBar, 0);
        Grid.SetColumn(_rowChrome, 1);
        _root.Children.Add(_selectionBar);
        _root.Children.Add(_rowChrome);
        if (!_completionNotice)
        {
            Grid.SetColumn(_status.Element, 3);
            _root.Children.Add(_status.Element);
        }
        _root.ActualThemeChanged += (_, _) => ApplyRowStyle();
        ApplyRowStyle();
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
            return new OpenCodeDetailHeader(EffectiveGlyph, _content.DetailTitle, _content.Target, statusText, brush, AccessibleName: _content.AccessibleName);
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
    internal void UpdateRow(OpenCodeTimelineRowContent content, Action? linkInvoked = null, bool cancelled = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        var previousSource = Source;
        _content = content;
        _linkInvoked = linkInvoked;
        if (!ReferenceEquals(previousSource, Source)) OpenCodeToolDetailPopup.Shared.CloseIf(previousSource);
        _iconVisual.SetGlyph(EffectiveGlyph);

        _label.Text = content.Name;
        _targetText.Text = _completionNotice && content.Target.Length > 0 ? "· " + content.Target : content.Target;
        _targetText.Visibility = content.Target.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // B14: unrecovered failure = whole-row red semantic instead of a lone red X.
        var chrome = OpenCodeToolRowChrome.For(content.Status);
        var summary = chrome.IsFailure ? chrome.ComposeSummary(content.Summary) : content.Summary;
        _summaryText.Text = summary;
        _summaryText.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var textStyle = chrome.TextKey is { } textKey
            ? OpenCodeToolTheme.Text(textKey, OpenCodeInteractionUi.CaptionStyleKey)
            : OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey);
        OpenCodeToolTheme.Apply(_label, textStyle);
        OpenCodeToolTheme.Apply(_targetText, textStyle);
        OpenCodeToolTheme.Apply(_summaryText, chrome.TextKey is { } summaryKey
            ? OpenCodeToolTheme.Text(summaryKey, OpenCodeInteractionUi.CaptionStyleKey)
            : OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey));
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(chrome.KindIconKey));
        // A8: no native text tooltip on the chip — the custom hover preview already shows name/target.

        var active = content.Status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming;
        // The 16px slot stays reserved (like tool rows) so summaries end at the same x; only its content changes.
        _status.SetRunning(active && !_completionNotice);
        _statusIcon.Visibility = active || content.Status is null || !chrome.ShowsStatusIcon ? Visibility.Collapsed : Visibility.Visible;
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

        OpenCodeToolPartView.ApplyRowChrome(_rowChrome, chrome);
        if (_completionNotice)
        {
            var tone = cancelled ? OpenCodeToolTheme.Caution
                : content.Status == OpenCodeToolRowStatus.Error ? OpenCodeToolTheme.Critical : OpenCodeToolTheme.Accent;
            OpenCodeToolTheme.Apply(_label, OpenCodeToolTheme.Text(tone, OpenCodeInteractionUi.CaptionStyleKey));
            // The name stays the accent "open child" tone; the glyph carries the outcome like sibling status icons.
            OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(!cancelled && content.Status == OpenCodeToolRowStatus.Completed
                ? OpenCodeToolTheme.Success : tone));
            OpenCodeToolTheme.Apply(_targetText, OpenCodeToolTheme.Text(OpenCodeToolTheme.TextSecondary, OpenCodeInteractionUi.CaptionStyleKey));
            OpenCodeToolPartView.ApplyRowChrome(_rowChrome, OpenCodeToolRowChrome.For(null));
            _summaryText.Visibility = Visibility.Collapsed;
        }
        ApplyRowStyle();

        AutomationProperties.SetName(_chip, content.AccessibleName);

        if (!_disposed)
        {
            OpenCodeToolDetailPopup.Shared.Refresh(Source);
        }
    }

    private void ApplyRowStyle()
        => OpenCodeTimelineRowStyle.Apply(_root, _chip, _content.Status, _label, _targetText, _summaryText);

    internal void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _status.Dispose();
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

// Mirrors i-process OpenCodeProcessedRowMetrics without importing another lane's commits.
// Kept separate from the native styling boundary so the exact policy is headlessly testable.
internal static class OpenCodeTimelineRowMetrics
{
    // Shared transcript grid for every secondary row in a turn (tool, reasoning, completion chip, 已处理 header):
    // [3px selection bar][2px gap] | 8px padding | 24px icon column | 8px gap | text … | 16px status | 8px.
    // Icon column x = 13…37 (glyph centre 25), text x = 45, trailing status centre = right − 16.
    internal const double LeadInset = 5;
    internal const double Padding = 8;
    internal const double IconColumn = 24;
    internal const double ColumnGap = 8;
    internal const double StatusColumn = 16;
    /// <summary>Expanded group children indent so their icon column starts where the header text starts.</summary>
    internal const double ChildIndent = IconColumn + ColumnGap;
    internal static bool IsRunning(OpenCodeToolRowStatus? status) => status is OpenCodeToolRowStatus.Running or OpenCodeToolRowStatus.Streaming;
    internal static double Height(OpenCodeToolRowStatus? status) => IsRunning(status) ? 36 : 30;
    internal static double FontSize(OpenCodeToolRowStatus? status) => IsRunning(status) ? 14 : 13;
    internal static double VerticalGap(OpenCodeToolRowStatus? status) => IsRunning(status) ? 2 : 0;
}

internal static class OpenCodeTimelineRowStyle
{
    internal static void Apply(Grid root, Button row, OpenCodeToolRowStatus? status, params TextBlock[] labels)
    {
        // Paint the rounded root itself, once, rather than relying on an unclipped rounded child.
        root.CornerRadius = new CornerRadius(4);
        row.CornerRadius = new CornerRadius(4);
        row.Height = OpenCodeTimelineRowMetrics.Height(status);
        var gap = OpenCodeTimelineRowMetrics.VerticalGap(status);
        // A4: each row owns its tint; its margins remain untinted even beside another active row.
        root.Margin = new Thickness(0, gap, 0, gap);
        foreach (var label in labels) label.FontSize = OpenCodeTimelineRowMetrics.FontSize(status);
        var active = OpenCodeTimelineRowMetrics.IsRunning(status);
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            root.ClearValue(Panel.BackgroundProperty);
            OpenCodeToolTheme.Apply(root, OpenCodeToolTheme.Grid(active
                ? "SystemColorHighlightColorBrush" : "SubtleFillColorTransparentBrush"));
            return;
        }
        root.Style = null;
        root.Background = active ? new SolidColorBrush(root.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(26, 96, 205, 255) : Color.FromArgb(19, 0, 95, 184)) : null;
    }
}
