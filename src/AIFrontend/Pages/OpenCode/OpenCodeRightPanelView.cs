using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Layout D metadata: <see cref="Header"/> (title → copyable session id) opens the right session card;
/// <see cref="Directory"/> (read-only cwd) sits above <see cref="Footer"/> (OpenCode logo · version · green dot)
/// in the bottom-right card. No session-content ownership.
/// </summary>
internal sealed class OpenCodeRightPanelView
{
    public FrameworkElement Header { get; }
    public FrameworkElement Directory { get; }
    public Button Footer { get; }
    public Button CopyButton { get; }
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _id = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _directory = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock _version = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextLineBounds = TextLineBounds.Tight };
    private readonly Image _logo = new() { Width = 72, Height = 14, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };

    public OpenCodeRightPanelView()
    {
        CopyButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                Children = { _id, new FontIcon { Glyph = "\uE8C8", FontSize = 12, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) } },
            },
        };
        Grid.SetColumn((FrameworkElement)((Grid)CopyButton.Content).Children[1], 1);
        AutomationProperties.SetAutomationId(CopyButton, "OpenCodeRightPanelCopySessionId");
        AutomationProperties.SetName(CopyButton, "复制会话 ID");
        AutomationProperties.SetName(_directory, "工作目录（只读）");
        _hint.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        // Hierarchy: title (Subtitle ramp) → session ID (quiet monospace copy row) → cwd (label + readable path).
        _title.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.SubtitleStyleKey);
        _id.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey);
        _id.IsTextSelectionEnabled = false; // the row is a copy button; selection would swallow its click
        _directory.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey);
        // Transparent until hover: reads as metadata, still a focusable native button with the copy glyph.
        CopyButton.Padding = new Thickness(8, 4, 8, 4);
        // The card supplies padding and scrolling; the copy row's negative margin lines its text up with the title.
        Header = new StackPanel { Spacing = 8, Children = { _title, CopyButton, _hint } };
        // Read-only directory in a quiet inset box: folder glyph + one-line path (full path in the tooltip; selectable).
        var folder = new FontIcon { Glyph = "\uE8B7", FontSize = 14, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        var directoryRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
            Children = { folder, _directory },
        };
        Grid.SetColumn(_directory, 1);
        var directoryBox = new Border
        {
            Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Child = directoryRow,
        };
        directoryBox.Style = OpenCodeInsightsTheme.InsetBox(); // ThemeResource setters follow light / dark / high contrast
        AutomationProperties.SetAutomationId(directoryBox, "OpenCodeRightPanelDirectory");
        Directory = directoryBox;
        _dot.Style = OpenCodeInsightsTheme.Dot(OpenCodeInsightsTheme.Success);
        _version.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        // Logo · version · green dot (no connected label); chevron says it opens details. Quiet until hover.
        var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var footerRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _logo, _version, _dot } },
                chevron,
            },
        };
        Grid.SetColumn(chevron, 1);
        Footer = new Button
        {
            Margin = new Thickness(-4, 0, -4, 0), Padding = new Thickness(4, 6, 4, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = footerRow,
        };
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } footerStyle) Footer.Style = footerStyle;
        Footer.BorderThickness = new Thickness(0); // a quiet row on the card; hover/pressed fill still marks it as a button
        AutomationProperties.SetAutomationId(Footer, "OpenCodeRightPanelConnection");
        ToolTipService.SetToolTip(Footer, "查看 OpenCode 服务版本、MCP 与 LSP 详情");
        Footer.ActualThemeChanged += (_, _) => UpdateLogo();
        UpdateLogo();
    }

    public void RenderSession(OpenCodeRightPanelPresentation? session)
    {
        _title.Text = session?.Title ?? string.Empty;
        ToolTipService.SetToolTip(_title, string.IsNullOrEmpty(_title.Text) ? null : _title.Text);
        _id.Text = session?.SessionId ?? string.Empty;
        _directory.Text = session?.Directory ?? string.Empty;
        ToolTipService.SetToolTip(_directory, string.IsNullOrEmpty(_directory.Text) ? null : _directory.Text);
        Directory.Visibility = string.IsNullOrEmpty(_directory.Text) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(CopyButton, session is null ? null : "复制会话 ID：" + session.SessionId);
        CopyButton.IsEnabled = session is not null;
    }

    public void ShowCopyHint(string? text)
    {
        _hint.Text = text ?? string.Empty;
        _hint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RenderConnection(string? version, bool connected)
    {
        _version.Text = OpenCodeRightPanelPresentation.Version(version);
        _dot.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(Footer, "OpenCode 服务版本：" + _version.Text + "；查看连接详情");
    }

    private void UpdateLogo() => _logo.Source = new SvgImageSource(new Uri(Footer.ActualTheme == ElementTheme.Dark
        ? "ms-appx:///Assets/OpenCode/logo-dark.svg" : "ms-appx:///Assets/OpenCode/logo-light.svg"));
}
