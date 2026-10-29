using System;
using System.Linq;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodePersistentPtyInventoryView : UserControl
{
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = 200,
        HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh = new() { Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 }, Width = 28, Height = 28, MinWidth = 0, Padding = new Thickness(0) };
    private OpenCodePersistentPtyInventoryContext? _context;
    private OpenCodePersistentPtyInventoryState? _state;
    private sealed record Item(OpenCodePersistentPtyInventoryContext Context, PersistentPtyInfo Info);
    internal event Action<OpenCodePersistentPtyInventoryRequest>? TerminalRequested;
    internal event Action<OpenCodePersistentPtyInventoryContext>? RefreshRequested;
    internal OpenCodePersistentPtyInventoryView()
    {
        Margin = new Thickness(0, 8, 0, 0);
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "持久终端", VerticalAlignment = VerticalAlignment.Center,
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.BodyStrongStyleKey) });
        Grid.SetColumn(_refresh, 1); header.Children.Add(_refresh);
        var body = new StackPanel { Spacing = 4, Children = { header, _status, _list } };
        Content = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Style = OpenCodeInsightsTheme.DockCard(), Child = body };
        AutomationProperties.SetName(_list, "当前会话的持久终端，与命令输出终端分开");
        AutomationProperties.SetName(_refresh, "刷新持久终端列表");
        AutomationProperties.SetAutomationId(this, "OpenCodePersistentPtyInventory");
        _refresh.Click += (_, _) => { if (_context is { } context) RefreshRequested?.Invoke(context); };
        _list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is Item item && item.Context == _context && _state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready)
                TerminalRequested?.Invoke(new(item.Context, item.Info.Id));
        };
        _list.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue || args.Item is not Item item) return;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = item.Info.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.CodeStyleKey), VerticalAlignment = VerticalAlignment.Center });
            var status = new TextBlock { Text = item.Info.Status == "exited" ? "已退出" : "运行中", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(status, 1); row.Children.Add(status);
            args.ItemContainer.Content = row;
            ToolTipService.SetToolTip(args.ItemContainer, item.Info.Command + "\n" + item.Info.Cwd);
            AutomationProperties.SetName(args.ItemContainer, item.Info.Title + "，" + status.Text + "，点击只读查看");
            args.Handled = true;
        };
        Render(null, null);
    }
    internal void Render(OpenCodePersistentPtyInventoryContext? context, OpenCodePersistentPtyInventoryState? state)
    {
        _context = context; _state = state;
        Visibility = context is null ? Visibility.Collapsed : Visibility.Visible;
        _refresh.IsEnabled = context is not null && state?.Phase != OpenCodePersistentPtyInventoryPhase.Loading;
        _status.Text = state?.Phase switch
        {
            OpenCodePersistentPtyInventoryPhase.Loading => "正在读取…",
            OpenCodePersistentPtyInventoryPhase.Unsupported => "当前服务不支持持久终端",
            OpenCodePersistentPtyInventoryPhase.Ready => state.Rows.Length == 0 ? "此会话没有持久终端" : "点击只读查看；接管需要单独确认",
            _ => "暂时无法确认持久终端列表",
        };
        _list.IsEnabled = state?.Phase == OpenCodePersistentPtyInventoryPhase.Ready;
        _list.ItemsSource = context is not null && state is not null ? state.Rows.Select(info => new Item(context, info)).ToArray() : null;
    }
}
