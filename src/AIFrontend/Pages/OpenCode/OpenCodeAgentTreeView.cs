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
/// Native theme-solid 子智能体 card. The working-only filter is the default; the app-wide in-memory toggle
/// and collapsed groups survive session/stream updates. Only real child sessions are navigable.
/// </summary>
internal sealed class OpenCodeAgentTreeView : UserControl
{
    private readonly TreeView _tree;
    private readonly ToggleButton _all;
    private readonly FrameworkElement _card;
    private readonly Button _parentReturn;
    private readonly TextBlock _hint;
    private readonly StackPanel _details;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _output;
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeViewNode> _groups = new(StringComparer.Ordinal);
    private IReadOnlyList<OpenCodeTreeAgent> _agents = Array.Empty<OpenCodeTreeAgent>();
    private IReadOnlyList<OpenCodeTreeShell> _shells = Array.Empty<OpenCodeTreeShell>();
    private string? _readHint;
    private static bool _showAll;
    internal event Action<string>? SessionRequested;
    internal event Action<string>? ShellRequested;
    internal event Action? OutputRefreshRequested;
    internal event Action? DetailClosed;
    internal event Action? ParentReturnRequested;

    internal OpenCodeAgentTreeView()
    {
        Content = (FrameworkElement)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border x:Name="Card" Padding="8,8,8,8" Translation="0,0,16">
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
                <TreeView x:Name="Tree" Grid.Row="2" SelectionMode="None" CanDragItems="False"
                          CanReorderItems="False" AllowDrop="False" AutomationProperties.Name="子智能体会话树">
                  <TreeView.ItemTemplate>
                    <DataTemplate><ContentPresenter Content="{Binding Content}" HorizontalAlignment="Stretch" /></DataTemplate>
                  </TreeView.ItemTemplate>
                  <TreeView.ItemContainerStyle>
                    <Style TargetType="TreeViewItem">
                      <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                      <Setter Property="UseSystemFocusVisuals" Value="True" />
                      <Setter Property="MinHeight" Value="30" />
                      <Setter Property="GlyphSize" Value="10" />
                      <Setter Property="Template">
                        <Setter.Value>
                          <!-- Native TreeViewItem named parts and template settings retain native keyboard,
                                ExpandCollapse automation and keyboard input. Row invocation owns pointer toggling. -->
                          <ControlTemplate TargetType="TreeViewItem">
                            <Grid x:Name="ContentPresenterGrid" Background="{TemplateBinding Background}"
                                  BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                                  CornerRadius="{ThemeResource ControlCornerRadius}" Margin="2,0" Padding="4,3">
                              <VisualStateManager.VisualStateGroups>
                                <VisualStateGroup x:Name="CommonStates">
                                  <VisualState x:Name="Normal" />
                                  <VisualState x:Name="PointerOver">
                                    <VisualState.Setters>
                                      <Setter Target="ContentPresenterGrid.Background" Value="{ThemeResource TreeViewItemBackgroundPointerOver}" />
                                      <Setter Target="ChevronHover.Opacity" Value="1" />
                                    </VisualState.Setters>
                                  </VisualState>
                                  <VisualState x:Name="Pressed">
                                    <VisualState.Setters>
                                      <Setter Target="ContentPresenterGrid.Background" Value="{ThemeResource TreeViewItemBackgroundPressed}" />
                                      <Setter Target="ChevronHover.Opacity" Value="1" />
                                    </VisualState.Setters>
                                  </VisualState>
                                  <VisualState x:Name="Selected">
                                    <VisualState.Setters>
                                      <Setter Target="ContentPresenterGrid.Background" Value="{ThemeResource TreeViewItemBackgroundSelected}" />
                                    </VisualState.Setters>
                                  </VisualState>
                                  <VisualState x:Name="PointerOverSelected">
                                    <VisualState.Setters>
                                      <Setter Target="ContentPresenterGrid.Background" Value="{ThemeResource TreeViewItemBackgroundSelectedPointerOver}" />
                                      <Setter Target="ChevronHover.Opacity" Value="1" />
                                    </VisualState.Setters>
                                  </VisualState>
                                  <VisualState x:Name="PressedSelected">
                                    <VisualState.Setters>
                                      <Setter Target="ContentPresenterGrid.Background" Value="{ThemeResource TreeViewItemBackgroundSelectedPressed}" />
                                      <Setter Target="ChevronHover.Opacity" Value="1" />
                                    </VisualState.Setters>
                                  </VisualState>
                                  <VisualState x:Name="Disabled">
                                    <VisualState.Setters><Setter Target="ContentPresenterGrid.Opacity" Value="0.5" /></VisualState.Setters>
                                  </VisualState>
                                  <VisualState x:Name="SelectedDisabled">
                                    <VisualState.Setters>
                                      <Setter Target="ContentPresenterGrid.Opacity" Value="0.5" />
                                    </VisualState.Setters>
                                  </VisualState>
                                </VisualStateGroup>
                              </VisualStateManager.VisualStateGroups>
                              <Grid x:Name="MultiSelectGrid" ColumnDefinitions="Auto,*,Auto" HorizontalAlignment="Left"
                                    Padding="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=TreeViewItemTemplateSettings.Indentation}">
                                <CheckBox x:Name="MultiSelectCheckBox" Visibility="Collapsed" IsTabStop="False" AutomationProperties.AccessibilityView="Raw" />
                                <ContentPresenter x:Name="ContentPresenter" Grid.Column="1" Margin="6,0,0,0"
                                                  Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
                                                  ContentTransitions="{TemplateBinding ContentTransitions}"
                                                  Foreground="{TemplateBinding Foreground}" HorizontalAlignment="Stretch" VerticalAlignment="Center" />
                                <Grid x:Name="ChevronHover" Grid.Column="2" Opacity="0">
                                  <!-- Let the whole row invoke once, including the inline glyph; the native
                                       chevron must not also toggle independently on the same pointer input. -->
                                  <Grid x:Name="ExpandCollapseChevron" Width="20" IsHitTestVisible="False" Opacity="{TemplateBinding GlyphOpacity}">
                                    <TextBlock x:Name="CollapsedGlyph" Text="{TemplateBinding CollapsedGlyph}"
                                               Visibility="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=TreeViewItemTemplateSettings.CollapsedGlyphVisibility}"
                                               FontFamily="{ThemeResource SymbolThemeFontFamily}" FontSize="{TemplateBinding GlyphSize}"
                                               Foreground="{TemplateBinding GlyphBrush}" VerticalAlignment="Center" HorizontalAlignment="Center"
                                               IsHitTestVisible="False" AutomationProperties.AccessibilityView="Raw" />
                                    <TextBlock x:Name="ExpandedGlyph" Text="{TemplateBinding ExpandedGlyph}"
                                               Visibility="{Binding RelativeSource={RelativeSource TemplatedParent}, Path=TreeViewItemTemplateSettings.ExpandedGlyphVisibility}"
                                               FontFamily="{ThemeResource SymbolThemeFontFamily}" FontSize="{TemplateBinding GlyphSize}"
                                               Foreground="{TemplateBinding GlyphBrush}" VerticalAlignment="Center" HorizontalAlignment="Center"
                                               IsHitTestVisible="False" AutomationProperties.AccessibilityView="Raw" />
                                  </Grid>
                                </Grid>
                              </Grid>
                            </Grid>
                          </ControlTemplate>
                        </Setter.Value>
                      </Setter>
                    </Style>
                  </TreeView.ItemContainerStyle>
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
            <Button x:Name="ParentReturn" Content="返回主智能体" Visibility="Collapsed"
                    HorizontalAlignment="Stretch" HorizontalContentAlignment="Center" Padding="12,10"
                    AutomationProperties.Name="返回主智能体" />
            </Grid>
            """);
        var root = (FrameworkElement)Content;
        VerticalAlignment = VerticalAlignment.Top;
        _card = (FrameworkElement)root.FindName("Card");
        _card.Style = OpenCodeInsightsTheme.DockCard();
        _parentReturn = (Button)root.FindName("ParentReturn");
        _parentReturn.Click += (_, _) => ParentReturnRequested?.Invoke();
        _tree = (TreeView)root.FindName("Tree");
        _tree.ItemInvoked += (_, e) =>
        {
            var node = e.InvokedItem as TreeViewNode;
            var content = node?.Content ?? e.InvokedItem;
            if (node is not null && _groups.Values.Contains(node)) node.IsExpanded = !node.IsExpanded;
            if (content is FrameworkElement { Tag: OpenCodeTreeInstance instance }) SessionRequested?.Invoke(instance.Id);
            if (content is FrameworkElement { Tag: OpenCodeTreeShell shell }) ShellRequested?.Invoke(shell.Id);
        };
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
        _card.Visibility = Visibility.Visible;
        _parentReturn.Visibility = Visibility.Collapsed;
        _tree.RootNodes.Clear();
        _agents = Array.Empty<OpenCodeTreeAgent>();
        _shells = Array.Empty<OpenCodeTreeShell>();
        _expanded.Clear();
        _groups.Clear();
        _details.Visibility = Visibility.Collapsed;
        _output.Text = string.Empty;
        _readHint = "正在读取…";
        _hint.Text = _readHint;
        _hint.Visibility = Visibility.Visible;
    }

    internal void Render(IReadOnlyList<OpenCodeTreeAgent> agents, IReadOnlyList<OpenCodeTreeShell> shells, string? hint)
    {
        _card.Visibility = Visibility.Visible;
        _parentReturn.Visibility = Visibility.Collapsed;
        _agents = agents;
        _shells = shells;
        _readHint = hint;
        RenderGroups();
    }

    internal void ShowParentReturn(bool enabled)
    {
        _card.Visibility = Visibility.Collapsed;
        _parentReturn.Visibility = Visibility.Visible;
        _parentReturn.IsEnabled = enabled;
    }

    private void RenderGroups()
    {
        foreach (var (key, node) in _groups) _expanded[key] = node.IsExpanded;
        _groups.Clear();
        _tree.RootNodes.Clear();
        foreach (var agent in OpenCodeAgentTreeProjection.VisibleAgents(_agents, _showAll))
        {
            var kind = (agent.Mode == "subagent" ? "子智能体" : "智能体")
                + (agent.Hidden ? " · 隐藏" : string.Empty);
            var name = OpenCodeAgentIcons.DisplayName(agent.Name);
            var children = OpenCodeAgentTreeProjection.VisibleInstances(agent.Children, _showAll);
            var rows = children.Select(instance => Row(instance.Title, instance.Model, instance.Status, instance)).ToArray();
            var working = agent.Children.Count(c => c.Status == OpenCodeTreeStatus.Running);
            Group("agent:" + agent.Id, name, OpenCodeAgentIcons.Glyph(agent.Name),
                OpenCodeAgentTreeProjection.CountLabel(working, agent.Children.Count, _showAll), rows,
                agent.Children.Count == 0, name + " · " + kind + (agent.Description is { Length: > 0 } description ? "\n" + description : string.Empty));
        }
        var shells = OpenCodeAgentTreeProjection.VisibleShells(_shells, _showAll);
        if (shells.Count > 0)
        {
            var rows = new List<FrameworkElement>();
            foreach (var shell in shells)
            {
                var kind = shell.Kind switch { OpenCodeTreeShellKind.Background => "后台", OpenCodeTreeShellKind.Foreground => "前台", _ => string.Empty };
                rows.Add(Row(shell.Command, kind, shell.Status, shell));
            }
            Group("shell", "终端", "\uE756",
                OpenCodeAgentTreeProjection.CountLabel(_shells.Count(s => s.Status == OpenCodeTreeStatus.Running), _shells.Count, _showAll),
                rows, false, "终端");
        }
        var hint = _readHint ?? OpenCodeAgentTreeProjection.EmptyHint(_groups.Count, _showAll);
        _hint.Text = hint ?? string.Empty;
        _hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    private TreeViewNode Group(string key, string name, string glyph, string count,
        IReadOnlyList<FrameworkElement> rows, bool dimmed, string tooltip)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
        ToolTipService.SetToolTip(content, tooltip);
        FrameworkElement header = content;
        if (key == "shell" && _groups.Count > 0)
        {
            var divider = (Border)XamlReader.Load("<Border xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" BorderBrush=\"{ThemeResource DividerStrokeColorDefaultBrush}\" BorderThickness=\"0,1,0,0\" Padding=\"0,8,0,0\" Margin=\"0,8,0,0\" />");
            divider.Child = content;
            header = divider;
        }
        NameTreeContent(header, name + "，" + count);
        var group = new TreeViewNode { Content = header, IsExpanded = !_expanded.TryGetValue(key, out var open) || open };
        foreach (var row in rows) group.Children.Add(new TreeViewNode { Content = row });
        _groups.Add(key, group);
        _tree.RootNodes.Add(group);
        return group;
    }

    private static void NameTreeContent(FrameworkElement content, string name)
    {
        AutomationProperties.SetName(content, name);
        // Node mode's native peer otherwise announces TreeViewNode.ToString(), not its UI content.
        content.Loaded += (_, _) =>
        {
            DependencyObject? parent = content;
            while ((parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent)) is not null)
                if (parent is TreeViewItem item) { AutomationProperties.SetName(item, name); break; }
        };
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
        NameTreeContent(row, title + "，" + subtitle + "，" + statusText);
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
