using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

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
                    CornerRadius="8" Padding="8" Margin="0,0,0,12">
              <StackPanel Spacing="6">
                <Grid ColumnDefinitions="*,Auto">
                  <Border Background="{ThemeResource AccentFillColorDefaultBrush}" BackgroundTransition="{x:Null}"
                          CornerRadius="4" Padding="6,2" HorizontalAlignment="Left">
                    <TextBlock Text="智能体 · 子智能体" Foreground="{ThemeResource TextOnAccentFillColorPrimaryBrush}" FontSize="12" />
                  </Border>
                  <Button x:Name="Refresh" Grid.Column="1" Padding="5" AutomationProperties.Name="刷新智能体与终端">
                    <FontIcon Glyph="&#xE72C;" FontSize="12" />
                  </Button>
                </Grid>
                <TextBlock x:Name="Hint" FontSize="12" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <TreeView x:Name="Tree" SelectionMode="None" MaxHeight="300" AutomationProperties.Name="智能体与子智能体会话树" />
                <StackPanel x:Name="Details" Visibility="Collapsed" Spacing="6">
                  <TextBlock x:Name="DetailTitle" TextWrapping="Wrap" FontSize="12" IsTextSelectionEnabled="True" />
                  <ScrollViewer MaxHeight="200" HorizontalScrollBarVisibility="Auto">
                    <TextBlock x:Name="Output" FontFamily="Consolas" FontSize="12" TextWrapping="Wrap" IsTextSelectionEnabled="True" />
                  </ScrollViewer>
                  <StackPanel Orientation="Horizontal" Spacing="6">
                    <Button x:Name="ReadOutput" Content="读取更多输出" Padding="6,3" />
                    <Button x:Name="CloseDetail" Content="收起" Padding="6,3" />
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
        ((Button)root.FindName("Refresh")).Click += (_, _) => RefreshRequested?.Invoke();
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
        var label = new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var node = new TreeViewNode { Content = label, IsExpanded = !expanded.TryGetValue(key, out var open) || open };
        _groups.Add(key, node);
        _tree.RootNodes.Add(node);
        return node;
    }

    private static Grid Row(string title, string subtitle, OpenCodeTreeStatus status, object tag)
    {
        var row = new Grid { Tag = tag, ColumnSpacing = 6, Padding = new Thickness(0, 3, 0, 3), MinWidth = 130 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, MaxWidth = 160 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        var model = (TextBlock)XamlReader.Load("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" FontSize=\"11\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextTrimming=\"CharacterEllipsis\" />");
        model.Text = subtitle;
        text.Children.Add(model);
        row.Children.Add(text);
        FrameworkElement indicator;
        var statusText = status switch { OpenCodeTreeStatus.Running => "运行中", OpenCodeTreeStatus.Succeeded => "成功", OpenCodeTreeStatus.Failed => "失败", OpenCodeTreeStatus.Interrupted => "已中断", _ => "状态不可用" };
        if (status == OpenCodeTreeStatus.Running) indicator = new ProgressRing { IsActive = true, Width = 14, Height = 14 };
        else
        {
            var resource = status == OpenCodeTreeStatus.Succeeded ? "SystemFillColorSuccessBrush"
                : status == OpenCodeTreeStatus.Failed ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";
            var icon = (TextBlock)XamlReader.Load($"<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Foreground=\"{{ThemeResource {resource}}}\" FontSize=\"14\" VerticalAlignment=\"Center\" />");
            icon.Text = status switch { OpenCodeTreeStatus.Succeeded => "✓", OpenCodeTreeStatus.Failed => "✕", OpenCodeTreeStatus.Interrupted => "—", _ => "?" };
            indicator = icon;
        }
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
