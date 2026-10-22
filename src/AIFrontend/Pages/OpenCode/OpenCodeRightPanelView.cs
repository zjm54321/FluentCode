using System;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Native controls mounted only in B2's two slots; no session-content ownership.</summary>
internal sealed class OpenCodeRightPanelView
{
    public FrameworkElement Header { get; }
    public Button Footer { get; }
    public Button CopyButton { get; }
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _id = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly TextBlock _directory = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _hint = new() { FontSize = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock _version = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Image _logo = new() { Width = 72, Height = 16, Stretch = Stretch.Uniform };
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
                Children = { _id, new FontIcon { Glyph = "\uE8C8", FontSize = 12, Margin = new Thickness(8, 0, 0, 0) } },
            },
        };
        Grid.SetColumn((FrameworkElement)((Grid)CopyButton.Content).Children[1], 1);
        AutomationProperties.SetAutomationId(CopyButton, "OpenCodeRightPanelCopySessionId");
        AutomationProperties.SetName(CopyButton, "复制会话 ID");
        AutomationProperties.SetName(_directory, "工作目录（只读）");
        _hint.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _directory.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CaptionSubtleStyleKey);
        Header = new ScrollViewer
        {
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(16, 16, 16, 8), Spacing = 8,
                Children = { _title, CopyButton, _hint, new TextBlock { Text = "工作目录", FontSize = 12 }, _directory },
            },
        };
        _dot.Style = OpenCodeInsightsTheme.Dot(OpenCodeInsightsTheme.Success);
        Footer = new Button
        {
            Margin = new Thickness(12, 8, 12, 12), Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _logo, _version, _dot } },
        };
        AutomationProperties.SetAutomationId(Footer, "OpenCodeRightPanelConnection");
        ToolTipService.SetToolTip(Footer, "查看 OpenCode 服务版本、MCP 与 LSP 详情");
        Footer.ActualThemeChanged += (_, _) => UpdateLogo();
        UpdateLogo();
    }

    public void RenderSession(OpenCodeRightPanelPresentation? session)
    {
        _title.Text = session?.Title ?? string.Empty;
        _id.Text = session?.SessionId ?? string.Empty;
        _directory.Text = session?.Directory ?? string.Empty;
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
