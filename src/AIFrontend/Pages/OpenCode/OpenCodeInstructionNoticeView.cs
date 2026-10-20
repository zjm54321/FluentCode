using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Safe instruction file names only. The projection contains no file contents: no detail popup or filesystem reads.</summary>
internal sealed class OpenCodeInstructionNoticeView
{
    internal const string Glyph = "\uE82D";
    private readonly StackPanel _root = new() { Spacing = 0 };
    private readonly Grid _header;
    private readonly TextBlock _target;
    private readonly FontIcon _chevron;
    private readonly Button _toggle;
    private readonly StackPanel _files = new() { Spacing = 2, Margin = new Thickness(37, 0, 8, 4), Visibility = Visibility.Collapsed };
    private bool _expanded;
    private bool _multiple;
    private bool _pointerOver;

    internal OpenCodeInstructionNoticeView(OpenCodeInstructionNotice notice)
    {
        var icon = new FontIcon { Glyph = Glyph, FontSize = 14, Width = 16, VerticalAlignment = VerticalAlignment.Center };
        OpenCodeToolTheme.Apply(icon, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        var label = OpenCodeInteractionUi.StyledText("指令", OpenCodeInteractionUi.CaptionStyleKey);
        label.VerticalAlignment = VerticalAlignment.Center;
        _target = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey, selectable: true);
        _target.VerticalAlignment = VerticalAlignment.Center;
        _target.TextWrapping = TextWrapping.NoWrap;
        _target.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(_target, "OpenCodeInstructionNotice.Summary");
        _chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-4, 0, 0, 0), Opacity = 0, IsHitTestVisible = false };
        OpenCodeToolTheme.Apply(_chevron, OpenCodeToolTheme.Icon(OpenCodeToolTheme.TextSecondary));
        var titleCluster = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleCluster.Children.Add(label);
        titleCluster.Children.Add(_chevron);
        _header = new Grid { ColumnSpacing = 8, Height = 32, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(5, 0, 0, 0) };
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(titleCluster, 1); Grid.SetColumn(_target, 2);
        _header.Children.Add(icon); _header.Children.Add(titleCluster); _header.Children.Add(_target);
        _toggle = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle"),
        };
        _toggle.Click += (_, _) => { _expanded = !_expanded; RefreshExpansion(); };
        _root.PointerEntered += (_, _) => { _pointerOver = true; RefreshExpansion(); };
        _root.PointerExited += (_, _) => { _pointerOver = false; RefreshExpansion(); };
        _root.PointerCanceled += (_, _) => { _pointerOver = false; RefreshExpansion(); };
        _root.Unloaded += (_, _) => { _pointerOver = false; RefreshExpansion(); };
        AutomationProperties.SetAutomationId(_root, "OpenCodeInstructionNotice");
        Update(notice);
    }

    internal FrameworkElement Element => _root;
    // Compatibility with the common process-leaf seam: an instruction is inline, never a detail action.
    internal void OpenDetail() { }
    internal void HoverEnter() { }
    internal void HoverExit() { }
    internal void SetDetailAnchor(FrameworkElement? anchor) { }
    internal void Dispose() { }

    internal void Update(OpenCodeInstructionNotice notice)
    {
        _target.Text = OpenCodeStepRunPresentation.DescribeInstructionTarget(notice);
        if (_target.Text.Length == 0) _target.Text = "来源未提供";
        _multiple = !notice.SourceKeys.IsDefaultOrEmpty && notice.SourceKeys.Length > 1;
        // Explicitly detach before switching between a static single-file row and the file-list toggle.
        _toggle.Content = null;
        _root.Children.Clear();
        _files.Children.Clear();
        if (_multiple)
        {
            _toggle.Content = _header;
            _root.Children.Add(_toggle);
            foreach (var key in notice.SourceKeys)
            {
                var file = OpenCodeInteractionUi.StyledText(OpenCodeStepRunPresentation.ShortSourceName(key), OpenCodeInteractionUi.CaptionStyleKey, selectable: true);
                file.TextWrapping = TextWrapping.Wrap;
                ToolTipService.SetToolTip(file, key);
                _files.Children.Add(file);
            }
            _root.Children.Add(_files);
        }
        else _root.Children.Add(_header);
        AutomationProperties.SetName(_root, "指令：" + _target.Text);
        ToolTipService.SetToolTip(_target, notice.SourceKeys.IsDefaultOrEmpty ? notice.Summary : string.Join("\n", notice.SourceKeys));
        RefreshExpansion();
    }

    private void RefreshExpansion()
    {
        // Reserve the caret's slot: hovering never moves the label or filename. Focus alone does not show it.
        _chevron.Opacity = _multiple && _pointerOver ? 1 : 0;
        _chevron.Glyph = _expanded ? "\uE70D" : "\uE76C";
        _files.Visibility = _multiple && _expanded ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_toggle, "指令：" + _target.Text + (_expanded ? "，收起文件列表" : "，展开文件列表"));
    }
}
