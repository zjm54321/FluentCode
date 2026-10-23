using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Native acrylic 子智能体 card. The working-only filter is the default; the app-wide in-memory toggle
/// and collapsed groups survive session/stream updates. Only real child sessions are navigable.
/// </summary>
internal sealed class OpenCodeAgentTreeView : UserControl
{
    private readonly ItemsRepeater _tree;
    private readonly ToggleButton _all;
    private readonly TextBlock _hint;
    private readonly StackPanel _details;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _output;
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<OpenCodeTreeAgent> _agents = Array.Empty<OpenCodeTreeAgent>();
    private IReadOnlyList<OpenCodeTreeShell> _shells = Array.Empty<OpenCodeTreeShell>();
    private string? _selectedInstance;
    private static bool _showAll;
    internal event Action<string>? SessionRequested;
    internal event Action<string>? ShellRequested;
    internal event Action? OutputRefreshRequested;
    internal event Action? DetailClosed;

    internal OpenCodeAgentTreeView()
    {
        Content = (FrameworkElement)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="{ThemeResource AcrylicInAppFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1"
                    CornerRadius="8" Padding="8,8,8,8" Translation="0,0,16">
              <Border.Shadow>
                <ThemeShadow />
              </Border.Shadow>
              <Grid RowDefinitions="Auto,Auto,*,Auto" RowSpacing="8">
                <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8" Padding="4,0,0,0">
                  <TextBlock Text="子智能体" Style="{ThemeResource BodyStrongTextBlockStyle}"
                              VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                  <ToggleButton x:Name="All" Grid.Column="1" Height="28" Padding="8,0" MinWidth="0"
                                AutomationProperties.Name="全部智能体与终端" ToolTipService.ToolTip="显示全部智能体与终端">
                    <StackPanel Orientation="Horizontal" Spacing="6">
                      <FontIcon Glyph="&#xE71C;" FontSize="12" />
                      <TextBlock Text="全部" FontSize="12" />
                    </StackPanel>
                  </ToggleButton>
                </Grid>
                <TextBlock x:Name="Hint" Grid.Row="1" Margin="4,0,0,0" TextWrapping="Wrap"
                           Style="{ThemeResource CaptionTextBlockStyle}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <ScrollViewer Grid.Row="2" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                  <ItemsRepeater x:Name="Tree" AutomationProperties.Name="子智能体会话树" />
                </ScrollViewer>
                <StackPanel x:Name="Details" Grid.Row="3" Visibility="Collapsed" Spacing="8" Margin="4,0,0,0">
                  <TextBlock x:Name="DetailTitle" TextWrapping="Wrap" IsTextSelectionEnabled="True"
                             Style="{ThemeResource CaptionTextBlockStyle}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                  <Border Background="{ThemeResource CardBackgroundFillColorSecondaryBrush}"
                          BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1"
                          CornerRadius="4" Padding="8,6">
                    <ScrollViewer MaxHeight="200" HorizontalScrollBarVisibility="Auto">
                      <TextBlock x:Name="Output" FontFamily="Consolas" FontSize="12"
                                 LineHeight="18" TextWrapping="Wrap" IsTextSelectionEnabled="True" />
                    </ScrollViewer>
                  </Border>
                  <StackPanel Orientation="Horizontal" Spacing="8">
                    <Button x:Name="ReadOutput" Content="读取更多输出" Padding="8,4" FontSize="12" />
                    <Button x:Name="CloseDetail" Content="收起" Padding="8,4" FontSize="12" />
                  </StackPanel>
                </StackPanel>
              </Grid>
            </Border>
            """);
        var root = (FrameworkElement)Content;
        VerticalAlignment = VerticalAlignment.Top;
        _tree = (ItemsRepeater)root.FindName("Tree");
        _all = (ToggleButton)root.FindName("All");
        _all.IsChecked = _showAll;
        _all.Checked += (_, _) => { _showAll = true; RenderGroups(); };
        _all.Unchecked += (_, _) => { _showAll = false; RenderGroups(); };
        _hint = (TextBlock)root.FindName("Hint");
        _details = (StackPanel)root.FindName("Details");
        _detailTitle = (TextBlock)root.FindName("DetailTitle");
        _output = (TextBlock)root.FindName("Output");
        // App monospace token (user font setting) when available; Consolas stays the parse-time fallback.
        if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey) is { } code) { _output.Style = code; _output.ClearValue(TextBlock.FontFamilyProperty); }
        ((Button)root.FindName("ReadOutput")).Click += (_, _) => OutputRefreshRequested?.Invoke();
        ((Button)root.FindName("CloseDetail")).Click += (_, _) => { _details.Visibility = Visibility.Collapsed; DetailClosed?.Invoke(); };
    }

    internal void Reset()
    {
        _tree.ItemsSource = null;
        _agents = Array.Empty<OpenCodeTreeAgent>();
        _shells = Array.Empty<OpenCodeTreeShell>();
        _expanded.Clear();
        _selectedInstance = null;
        _details.Visibility = Visibility.Collapsed;
        _output.Text = string.Empty;
        _hint.Text = "正在读取…";
        _hint.Visibility = Visibility.Visible;
    }

    internal void Render(IReadOnlyList<OpenCodeTreeAgent> agents, IReadOnlyList<OpenCodeTreeShell> shells, string? hint)
    {
        _agents = agents;
        _shells = shells;
        _hint.Text = hint ?? string.Empty;
        _hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        RenderGroups();
    }

    private void RenderGroups()
    {
        var groups = new List<FrameworkElement>();
        foreach (var agent in OpenCodeAgentTreeProjection.VisibleAgents(_agents, _showAll))
        {
            var kind = (agent.Mode == "subagent" ? "子智能体" : "智能体")
                + (agent.Hidden ? " · 隐藏" : string.Empty);
            var name = OpenCodeAgentIcons.DisplayName(agent.Name);
            var children = OpenCodeAgentTreeProjection.VisibleInstances(agent.Children, _showAll);
            var rows = children.Select(instance => InstanceButton(Row(instance.Title, instance.Model, instance.Status, instance), instance.Id)).ToArray();
            var working = agent.Children.Count(c => c.Status == OpenCodeTreeStatus.Running);
            groups.Add(Group("agent:" + agent.Id, name, OpenCodeAgentIcons.Glyph(agent.Name),
                OpenCodeAgentTreeProjection.CountLabel(working, agent.Children.Count, _showAll), rows,
                agent.Children.Count == 0, name + " · " + kind + (agent.Description is { Length: > 0 } description ? "\n" + description : string.Empty)));
        }
        var shells = OpenCodeAgentTreeProjection.VisibleShells(_shells, _showAll);
        if (shells.Count > 0)
        {
            if (groups.Count > 0) groups.Add((Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Height=\"1\" Margin=\"4,8\" Background=\"{ThemeResource DividerStrokeColorDefaultBrush}\" />"));
            var rows = new List<FrameworkElement>();
            foreach (var shell in shells)
            {
                var kind = shell.Kind switch { OpenCodeTreeShellKind.Background => "后台", OpenCodeTreeShellKind.Foreground => "前台", _ => string.Empty };
                var row = Row(shell.Command, kind, shell.Status, shell);
                var button = RowButton(row);
                button.Click += (_, _) => ShellRequested?.Invoke(shell.Id);
                rows.Add(button);
            }
            groups.Add(Group("shell", "终端", "\uE756",
                OpenCodeAgentTreeProjection.CountLabel(_shells.Count(s => s.Status == OpenCodeTreeStatus.Running), _shells.Count, _showAll),
                rows, false, "终端"));
        }
        _tree.ItemsSource = groups;
    }

    private FrameworkElement Group(string key, string name, string glyph, string count,
        IReadOnlyList<FrameworkElement> rows, bool dimmed, string tooltip)
    {
        var group = new StackPanel();
        var header = (Button)XamlReader.Load("""
            <Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" MinHeight="32"
                          Padding="8,4" BorderThickness="0" Background="Transparent" />
            """);
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        var label = new TextBlock { Text = name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, MaxLines = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Style = OpenCodeInsightsTheme.TreeLabel(dimmed) };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        var counter = Caption(count);
        Grid.SetColumn(counter, 2);
        content.Children.Add(counter);
        var chevron = new FontIcon { FontSize = 10, Opacity = 0 };
        Grid.SetColumn(chevron, 3);
        content.Children.Add(chevron);
        header.Content = content;
        var children = new StackPanel();
        foreach (var row in rows) children.Children.Add(row);
        var open = !_expanded.TryGetValue(key, out var stored) || stored;
        header.IsEnabled = rows.Count > 0;
        void UpdateExpansion()
        {
            children.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = open ? "\uE70D" : "\uE76C";
            AutomationProperties.SetName(header, name + "，" + count + (open ? "，展开" : "，折叠"));
        }
        UpdateExpansion();
        header.Click += (_, _) => { open = !open; _expanded[key] = open; UpdateExpansion(); };
        header.PointerEntered += (_, _) => chevron.Opacity = 1;
        header.PointerExited += (_, _) => chevron.Opacity = 0;
        header.GotFocus += (_, _) => chevron.Opacity = 1;
        header.LostFocus += (_, _) => chevron.Opacity = 0;
        ToolTipService.SetToolTip(header, tooltip);
        group.Children.Add(header);
        group.Children.Add(children);
        return group;
    }

    private Button InstanceButton(Grid row, string id)
    {
        var button = RowButton(row);
        if (_selectedInstance == id)
        {
            var selected = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" BorderBrush=\"{ThemeResource AccentFillColorDefaultBrush}\" BorderThickness=\"3,0,0,0\" CornerRadius=\"2\" />");
            button.Content = selected;
            selected.Child = row;
        }
        button.Click += (_, _) => { _selectedInstance = id; RenderGroups(); SessionRequested?.Invoke(id); };
        return button;
    }

    private static Button RowButton(Grid row)
    {
        var button = (Button)XamlReader.Load("""
            <Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch" Padding="28,4,8,4"
                    MinHeight="30" BorderThickness="0" Background="Transparent" />
            """);
        button.Content = row;
        AutomationProperties.SetName(button, AutomationProperties.GetName(row));
        return button;
    }

    private static TextBlock Caption(string text)
    {
        var caption = (TextBlock)XamlReader.Load("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" FontSize=\"11\" Foreground=\"{ThemeResource TextFillColorTertiaryBrush}\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\" />");
        caption.Text = text;
        return caption;
    }

    private static Grid Row(string title, string subtitle, OpenCodeTreeStatus status, object tag)
    {
        var row = new Grid { Tag = tag, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var task = new TextBlock { Text = title, FontSize = 12, LineHeight = 16, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        if (tag is OpenCodeTreeShell)
        {
            if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey) is { } code) task.Style = code;
            else task.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
        }
        Grid.SetColumn(task, 1);
        row.Children.Add(task);
        var model = Caption(subtitle);
        model.MaxWidth = 90;
        model.VerticalAlignment = VerticalAlignment.Center;
        model.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(model, 2);
        row.Children.Add(model);
        FrameworkElement indicator;
        var statusText = status switch { OpenCodeTreeStatus.Running => "运行中", OpenCodeTreeStatus.Succeeded => "成功", OpenCodeTreeStatus.Failed => "失败", OpenCodeTreeStatus.Interrupted => "已中断", _ => "状态不可用" };
        if (status == OpenCodeTreeStatus.Running) indicator = new ProgressRing { IsActive = true, Width = 14, Height = 14, MinWidth = 0, MinHeight = 0 };
        else
        {
            var resource = status == OpenCodeTreeStatus.Succeeded ? "SystemFillColorSuccessBrush"
                : status == OpenCodeTreeStatus.Failed ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";
            // Segoe Fluent glyphs share one optical size/baseline (text ✓/✕ glyphs drifted between fonts).
            var icon = (FontIcon)XamlReader.Load($"<FontIcon xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{{ThemeResource {resource}}}\" FontSize=\"12\" />");
            icon.Glyph = status switch { OpenCodeTreeStatus.Succeeded => "\uE73E", OpenCodeTreeStatus.Failed => "\uE711", OpenCodeTreeStatus.Interrupted => "\uE738", _ => "\uE9CE" };
            indicator = icon;
        }
        indicator.HorizontalAlignment = HorizontalAlignment.Center;
        indicator.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(indicator, 0);
        row.Children.Add(indicator);
        AutomationProperties.SetName(row, title + "，" + subtitle + "，" + statusText);
        ToolTipService.SetToolTip(row, title + "\n" + subtitle + " · " + statusText);
        return row;
    }

    internal void ShowOutput(OpenCodeTreeShell shell, string output)
    {
        _details.Visibility = Visibility.Visible;
        _detailTitle.Text = shell.Command + "\n" + (shell.Cwd ?? string.Empty) + "\n退出码：" + (shell.Exit ?? "不可用");
        _output.Text = output;
    }
}
