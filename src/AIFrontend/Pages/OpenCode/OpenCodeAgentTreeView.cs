using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Layout D top-left floating card: native acrylic 子智能体 tree. Configured agents that have not run in this
/// session are dimmed; agents with running / finished instances are bright. Collapsed groups survive streamed updates.
/// </summary>
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
                    CornerRadius="8" Padding="8,8,8,8" Translation="0,0,16">
              <Border.Shadow>
                <ThemeShadow />
              </Border.Shadow>
              <Grid RowDefinitions="Auto,Auto,*,Auto" RowSpacing="8">
                <!-- Subtle accent: a slim accent rule instead of a filled pill keeps the header calm on acrylic. -->
                <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8" Padding="4,0,0,0">
                  <Border Width="3" Height="14" CornerRadius="2" VerticalAlignment="Center"
                          Background="{ThemeResource AccentFillColorDefaultBrush}" />
                  <TextBlock Grid.Column="1" Text="子智能体" Style="{ThemeResource BodyStrongTextBlockStyle}"
                             VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                  <Button x:Name="Refresh" Grid.Column="2" Width="28" Height="28" Padding="0"
                          AutomationProperties.Name="刷新智能体与终端" ToolTipService.ToolTip="刷新智能体与终端">
                    <FontIcon Glyph="&#xE72C;" FontSize="12" />
                  </Button>
                </Grid>
                <TextBlock x:Name="Hint" Grid.Row="1" Margin="4,0,0,0" TextWrapping="Wrap"
                           Style="{ThemeResource CaptionTextBlockStyle}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <!-- Node mode: without a template WinUI shows TreeViewNode.Content.ToString() (type names). -->
                <!-- The card is top-aligned in the dock; the tree takes the remaining height and scrolls inside it. -->
                <TreeView x:Name="Tree" Grid.Row="2" SelectionMode="None" AutomationProperties.Name="子智能体会话树">
                  <TreeView.ItemTemplate>
                    <DataTemplate>
                      <ContentPresenter Content="{Binding Content}" />
                    </DataTemplate>
                  </TreeView.ItemTemplate>
                </TreeView>
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
            var kind = (agent.Mode == "primary" ? "主智能体" : agent.Mode == "subagent" ? "子智能体" : "智能体")
                + (agent.Hidden ? " · 隐藏" : string.Empty);
            // Idle configured agents (no instance in this session) are dimmed; running / finished ones stay bright.
            var group = Group("agent:" + agent.Id, agent.Name, expanded, dimmed: agent.Children.Count == 0);
            var content = (FrameworkElement)group.Content;
            ToolTipService.SetToolTip(content, agent.Name + " · " + kind + (agent.Description is { Length: > 0 } description ? "\n" + description : string.Empty));
            AutomationProperties.SetName(content, agent.Name + "，" + kind + (agent.Children.Count == 0 ? "，空闲" : "，" + agent.Children.Count + " 个实例"));
            foreach (var instance in agent.Children)
                group.Children.Add(new TreeViewNode { Content = Row(instance.Title, instance.Model, instance.Status, instance) });
        }
        if (shells.Count > 0)
        {
            var group = Group("shell", "Shell", expanded, dimmed: false);
            AutomationProperties.SetName((FrameworkElement)group.Content, "Shell 终端，" + shells.Count + " 个");
            foreach (var shell in shells)
            {
                var kind = shell.Kind switch { OpenCodeTreeShellKind.Background => "后台", OpenCodeTreeShellKind.Foreground => "前台", _ => "前后台不可用" };
                group.Children.Add(new TreeViewNode { Content = Row(shell.Command, kind, shell.Status, shell) });
            }
        }
    }

    private TreeViewNode Group(string key, string name, Dictionary<string, bool> expanded, bool dimmed)
    {
        var label = new TextBlock { Text = name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, MaxLines = 0,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = TextColumnWidth + 20,
            Style = OpenCodeInsightsTheme.TreeLabel(dimmed) };
        var node = new TreeViewNode { Content = label, IsExpanded = !expanded.TryGetValue(key, out var open) || open };
        _groups.Add(key, node);
        _tree.RootNodes.Add(node);
        return node;
    }

    // TreeViewItem content is not stretched. Reserve the same row budget at one tree depth so
    // models occupy the space to the right of the title and every status stays on the same vertical line.
    // Budget for the 264epx dock card (8 padding each side, one tree depth of indent).
    private const double TextColumnWidth = 148;
    private const double TitleColumnWidth = 76;
    private const double IndicatorSize = 16;

    private static Grid Row(string title, string subtitle, OpenCodeTreeStatus status, object tag)
    {
        var row = new Grid { Tag = tag, ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2), MinWidth = 130 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TitleColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TextColumnWidth - TitleColumnWidth - row.ColumnSpacing) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndicatorSize) });
        row.Children.Add(new TextBlock { Text = title, FontSize = 12, LineHeight = 16, VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        var model = (TextBlock)XamlReader.Load("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" FontSize=\"11\" LineHeight=\"14\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" TextWrapping=\"NoWrap\" TextTrimming=\"CharacterEllipsis\" />");
        model.Text = subtitle;
        model.VerticalAlignment = VerticalAlignment.Center;
        model.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(model, 1);
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
        Grid.SetColumn(indicator, 2);
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
