using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Native acrylic tree. Collapsed groups survive streamed state updates.</summary>
internal sealed class OpenCodeAgentTreeView : UserControl
{
    private readonly TreeView _tree;
    private readonly TextBlock _hint;
    private readonly StackPanel _details;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _output;
    private readonly Dictionary<string, TreeViewNode> _groups = new(StringComparer.Ordinal);
    internal event Action<string>? SessionRequested;
    internal event Action<string>? ShellRequested;
    internal event Action? RefreshRequested;
    internal event Action? OutputRefreshRequested;
    internal event Action? DetailClosed;

    internal OpenCodeAgentTreeView()
    {
        Content = (FrameworkElement)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    Background="{ThemeResource AcrylicInAppFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1"
                    CornerRadius="8" Padding="8,8,8,8" Margin="0,0,0,12">
              <StackPanel Spacing="8">
                <!-- Subtle accent: a slim accent rule instead of a filled pill keeps the header calm on acrylic. -->
                <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8" Padding="4,0,0,0">
                  <Border Width="3" Height="14" CornerRadius="2" VerticalAlignment="Center"
                          Background="{ThemeResource AccentFillColorDefaultBrush}" />
                  <TextBlock Grid.Column="1" Text="智能体 · 子智能体" Style="{ThemeResource CaptionTextBlockStyle}"
                             FontWeight="SemiBold" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                  <Button x:Name="Refresh" Grid.Column="2" Width="28" Height="28" Padding="0"
                          AutomationProperties.Name="刷新智能体与终端" ToolTipService.ToolTip="刷新智能体与终端">
                    <FontIcon Glyph="&#xE72C;" FontSize="12" />
                  </Button>
                </Grid>
                <TextBlock x:Name="Hint" Margin="4,0,0,0" TextWrapping="Wrap"
                           Style="{ThemeResource CaptionTextBlockStyle}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <TreeView x:Name="Tree" SelectionMode="None" MaxHeight="280" AutomationProperties.Name="智能体与子智能体会话树" />
                <StackPanel x:Name="Details" Visibility="Collapsed" Spacing="8" Margin="4,0,0,0">
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
              </StackPanel>
            </Border>
            """);
        var root = (FrameworkElement)Content;
        _tree = (TreeView)root.FindName("Tree");
        _hint = (TextBlock)root.FindName("Hint");
        _details = (StackPanel)root.FindName("Details");
        _detailTitle = (TextBlock)root.FindName("DetailTitle");
        _output = (TextBlock)root.FindName("Output");
        // App monospace token (user font setting) when available; Consolas stays the parse-time fallback.
        if (OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey) is { } code) { _output.Style = code; _output.ClearValue(TextBlock.FontFamilyProperty); }
        var refresh = (Button)root.FindName("Refresh");
        if (OpenCodeInteractionUi.TryStyle("OpenCodeTransparentToolButtonStyle") is { } toolStyle) refresh.Style = toolStyle;
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        ((Button)root.FindName("ReadOutput")).Click += (_, _) => OutputRefreshRequested?.Invoke();
        ((Button)root.FindName("CloseDetail")).Click += (_, _) => { _details.Visibility = Visibility.Collapsed; DetailClosed?.Invoke(); };
        _tree.ItemInvoked += (_, e) =>
        {
            var content = e.InvokedItem is TreeViewNode node ? node.Content : e.InvokedItem;
            if (content is FrameworkElement { Tag: OpenCodeTreeInstance instance }) SessionRequested?.Invoke(instance.Id);
            if (content is FrameworkElement { Tag: OpenCodeTreeShell shell }) ShellRequested?.Invoke(shell.Id);
        };
    }

    internal void Reset()
    {
        _tree.RootNodes.Clear();
        _groups.Clear();
        _details.Visibility = Visibility.Collapsed;
        _output.Text = string.Empty;
        _hint.Text = "正在读取…";
    }

    internal void Render(IReadOnlyList<OpenCodeTreeAgent> agents, IReadOnlyList<OpenCodeTreeShell> shells, string? hint)
    {
        var expanded = _groups.ToDictionary(p => p.Key, p => p.Value.IsExpanded, StringComparer.Ordinal);
        _groups.Clear();
        _tree.RootNodes.Clear();
        _hint.Text = hint ?? string.Empty;
        _hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        foreach (var agent in agents)
        {
            var label = agent.Name + (agent.Mode == "primary" ? " · 主智能体" : agent.Mode == "subagent" ? " · 子智能体" : string.Empty)
                + (agent.Hidden ? " · 隐藏" : string.Empty);
            var group = Group("agent:" + agent.Id, label, expanded);
            ToolTipService.SetToolTip((FrameworkElement)group.Content, agent.Description ?? agent.Name);
            foreach (var instance in agent.Children)
                group.Children.Add(new TreeViewNode { Content = Row(instance.Title, instance.Model, instance.Status, instance) });
        }
        if (shells.Count > 0)
        {
            var group = Group("shell", "Shell / 终端", expanded);
            foreach (var shell in shells)
            {
                var kind = shell.Kind switch { OpenCodeTreeShellKind.Background => "后台", OpenCodeTreeShellKind.Foreground => "前台", _ => "前后台不可用" };
                group.Children.Add(new TreeViewNode { Content = Row(shell.Command, kind, shell.Status, shell) });
            }
        }
    }

    private TreeViewNode Group(string key, string name, Dictionary<string, bool> expanded)
    {
        var label = new TextBlock { Text = name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = TextColumnWidth + 20 };
        var node = new TreeViewNode { Content = label, IsExpanded = !expanded.TryGetValue(key, out var open) || open };
        _groups.Add(key, node);
        _tree.RootNodes.Add(node);
        return node;
    }

    // Rows sit at one tree depth inside the fixed 320epx pane: a fixed text column keeps every status icon on
    // the same vertical line (TreeViewItem content is not stretched), while long titles/models ellipsize.
    private const double TextColumnWidth = 176;
    private const double IndicatorSize = 16;

    private static Grid Row(string title, string subtitle, OpenCodeTreeStatus status, object tag)
    {
        var row = new Grid { Tag = tag, ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2), MinWidth = 130 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TextColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndicatorSize) });
        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var model = (TextBlock)XamlReader.Load("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" FontSize=\"11\" LineHeight=\"14\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" />");
        model.Text = subtitle;
        text.Children.Add(model);
        row.Children.Add(text);
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
        Grid.SetColumn(indicator, 1);
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
