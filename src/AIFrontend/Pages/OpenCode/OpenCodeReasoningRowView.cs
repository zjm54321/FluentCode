using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 时间线中的“思考”行（F4，AutomationId <c>OpenCodeReasoningRow.{首个思考部件 key}</c>）：同一段内连续的 reasoning 部件
/// 合并为一行，外观与工具行一致（高 32：类别图标 | “思考” | 首行预览 | 右侧状态）。进行中（S2）即使文本为空也立即显示“思考中…”行与进度环。
/// 完整思考文本在详情中按 Markdown 呈现（H4，与助手正文同一个 <see cref="OpenCodeRichTextPresenter"/>）。
/// 悬停片刻（延迟见 OpenCodeToolDetailPopup.HoverOpenDelayMs）后在共享的 <see cref="OpenCodeToolDetailPopup"/> 中预览完整思考文本（不移动焦点），点击 / Enter / Space 固定，Esc 关闭。
/// 普通 sealed 业务类（不是控件子类）：原生 Button + Grid，内置文本样式与主题画刷。
/// </summary>
internal sealed class OpenCodeReasoningRowView : IOpenCodeDetailContentSource
{
    private readonly Grid _root;
    private readonly Border _selectionBar;
    private readonly Button _row;
    private readonly FontIcon _icon;
    private readonly FontIcon _statusIcon;
    private readonly ProgressRing _progress;
    private readonly TextBlock _nameText;
    private readonly TextBlock _previewText;
    private OpenCodeToolRowModel _model = new() { Name = "思考" };
    private bool _disposed;
    private FrameworkElement? _detailAnchorOverride;
    internal void SetDetailAnchor(FrameworkElement? anchor) => _detailAnchorOverride = anchor;

    internal OpenCodeReasoningRowView()
    {
        _icon = new FontIcon { Glyph = "\uEA80", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _statusIcon = new FontIcon { Glyph = "\uE73E", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(_statusIcon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.Success));
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
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
        var statusCell = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        statusCell.Children.Add(_statusIcon);
        statusCell.Children.Add(_progress);

        _nameText = OpenCodeInteractionUi.StyledText("思考", OpenCodeInteractionUi.CaptionStyleKey);
        _nameText.VerticalAlignment = VerticalAlignment.Center;
        _nameText.TextWrapping = TextWrapping.NoWrap;

        _previewText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _previewText.VerticalAlignment = VerticalAlignment.Center;
        _previewText.TextWrapping = TextWrapping.NoWrap;
        _previewText.TextTrimming = TextTrimming.CharacterEllipsis;

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        Grid.SetColumn(_icon, 0);
        Grid.SetColumn(_nameText, 1);
        Grid.SetColumn(_previewText, 2);
        Grid.SetColumn(statusCell, 3);
        content.Children.Add(_icon);
        content.Children.Add(statusCell);
        content.Children.Add(_nameText);
        content.Children.Add(_previewText);

        _row = new Button
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
        _row.Click += (_, _) => OpenCodeToolDetailPopup.Shared.TogglePinned(this);
        _row.PointerEntered += (_, _) => OpenCodeToolDetailPopup.Shared.HoverEnter(this);
        _row.PointerExited += (_, _) => OpenCodeToolDetailPopup.Shared.HoverExit(this);
        _row.KeyDown += OnRowKeyDown;
        _row.Unloaded += (_, _) => OpenCodeToolDetailPopup.Shared.CloseIf(this);
        AutomationProperties.SetHelpText(_row, "按 Enter 查看完整思考内容，Esc 关闭");

        _selectionBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 6, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        OpenCodeToolTheme.Apply(_selectionBar, OpenCodeToolTheme.Border(OpenCodeToolTheme.Accent));
        _selectionBar.VerticalAlignment = VerticalAlignment.Stretch;

        _root = new Grid { ColumnSpacing = 2 };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_selectionBar, 0);
        Grid.SetColumn(_row, 1);
        _root.Children.Add(_selectionBar);
        _root.Children.Add(_row);
    }

    internal FrameworkElement Element => _root;

    /// <summary>当前合并后的思考文本。</summary>
    internal string Text => _model.OutputText ?? string.Empty;
    internal OpenCodeToolRowStatus Status => _model.Status;

    internal void OpenDetail() => OpenCodeToolDetailPopup.Shared.TogglePinned(this);
    internal void HoverEnter() => OpenCodeToolDetailPopup.Shared.HoverEnter(this);
    internal void HoverExit() => OpenCodeToolDetailPopup.Shared.HoverExit(this);

    FrameworkElement IOpenCodeDetailContentSource.DetailAnchor => _detailAnchorOverride ?? _row;

    OpenCodeDetailHeader IOpenCodeDetailContentSource.DetailHeader => new(
        "\uEA80",
        "思考",
        StatusText: _model.Status == OpenCodeToolRowStatus.Streaming ? "思考中…" : string.Empty,
        AccessibleName: _model.AccessibleName);

    OpenCodeToolDetailView IOpenCodeDetailContentSource.BuildDetailContent()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return OpenCodeToolDetailContent.Wrap(OpenCodeToolDetailContent.Caption(
                _model.Status == OpenCodeToolRowStatus.Streaming ? "思考中…" : "（没有可显示的思考内容）"));
        }

        // 与助手正文同一个 Markdown 渲染器（标题 / 粗体 / 列表 / 代码块等）。
        var presenter = new OpenCodeRichTextPresenter();
        presenter.Render(Text, OpenCodeRichTextStyle.Body);
        return OpenCodeToolDetailContent.Wrap(presenter.Element);
    }

    void IOpenCodeDetailContentSource.OnDetailStateChanged(bool shown, bool pinned)
        => _selectionBar.Visibility = shown && pinned ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>就地更新合并后的思考文本；streaming = 该思考仍在生成（显示进度环）。</summary>
    internal void Update(string text, bool streaming, string rowKey)
    {
        text ??= string.Empty;
        var preview = OpenCodeStepRunPresentation.DescribeReasoningPreview(text);
        _model = new OpenCodeToolRowModel
        {
            PartId = rowKey ?? string.Empty,
            Name = "思考",
            Kind = OpenCodeToolKind.Other,
            Status = streaming ? OpenCodeToolRowStatus.Streaming : OpenCodeToolRowStatus.Completed,
            StatusText = streaming ? "思考中" : "思考完成",
            Summary = preview,
            OutputText = text,
            AccessibleName = "思考" + (streaming ? "（进行中）" : string.Empty) + (preview.Length > 0 ? "：" + preview : string.Empty),
        };

        _nameText.Text = streaming ? "思考中…" : "思考";
        _previewText.Text = preview;
        _progress.IsActive = streaming;
        _progress.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
        _statusIcon.Visibility = streaming ? Visibility.Collapsed : Visibility.Visible;
        // B16: finished reasoning recedes (secondary label, tertiary glyph); streaming keeps the default emphasis.
        var labelKey = OpenCodeProcessedSummaryStyle.RowLabelKey(_model.Status);
        OpenCodeToolTheme.Apply(_nameText, labelKey is null
            ? OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionStyleKey)
            : OpenCodeToolTheme.Text(labelKey, OpenCodeInteractionUi.CaptionStyleKey));
        OpenCodeToolTheme.Apply(_icon, OpenCodeToolTheme.Icon(OpenCodeProcessedSummaryStyle.IsSettledRow(_model.Status)
            ? OpenCodeProcessedSummaryStyle.IconBrushKey
            : OpenCodeToolTheme.TextSecondary));
        AutomationProperties.SetAutomationId(_row, "OpenCodeReasoningRow." + (rowKey ?? string.Empty));
        AutomationProperties.SetName(_row, _model.AccessibleName);

        if (!_disposed)
        {
            OpenCodeToolDetailPopup.Shared.Refresh(this);
        }
    }

    internal void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OpenCodeToolDetailPopup.Shared.CloseIf(this);
    }

    private void OnRowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (OpenCodeToolDetailPopup.Shared.IsShowing(this))
                {
                    OpenCodeToolDetailPopup.Shared.Close();
                    e.Handled = true;
                }

                break;
            case VirtualKey.Up:
            case VirtualKey.Down:
                e.Handled = TryMoveFocus(e.Key == VirtualKey.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down);
                break;
        }
    }

    private bool TryMoveFocus(FocusNavigationDirection direction)
    {
        try
        {
            if (_row.XamlRoot?.Content is { } searchRoot)
            {
                return FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = searchRoot });
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
