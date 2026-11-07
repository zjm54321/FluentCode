using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

internal sealed record OpenCodeAgentPickerRow(string Id, string Name, string? Description, bool Selectable, string? Reason, string? Color = null);

// Selection is UI-only. The selector applies it through the guarded server switch on the next send.
internal static class OpenCodeAgentPickerDialog
{
    internal static async Task<string?> ShowAsync(XamlRoot? owner, IReadOnlyList<OpenCodeAgentPickerRow> rows,
        string? currentId, string? currentLabel, string? blockedReason, FrameworkElement? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        anchor = OpenCodePickerFlyout.Anchor(owner, anchor, "OpenCodeSidebarAgent");
        if (owner is null || anchor is null || blockedReason is { Length: > 0 }) return null;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            Width = Math.Max(1, Math.Min(288, owner.Size.Width - 48)), MaxHeight = Math.Max(34, Math.Min(360, owner.Size.Height - 80)) };
        AutomationProperties.SetAutomationId(list, "OpenCodeAgentSelectorCombo");
        AutomationProperties.SetName(list, "可切换的智能体");
        foreach (var row in rows)
        {
            if (!row.Selectable) continue;
            var item = RowItem(row, row.Id == currentId);
            list.Items.Add(item);
            if (row.Id == currentId) list.SelectedItem = item;
        }
        return await OpenCodePickerFlyout.ShowAsync<string?>(anchor, list, null, choose =>
            list.ItemClick += (_, e) =>
            {
                if (OpenCodeNativeListChoice.Resolve<OpenCodeAgentPickerRow>(list, e.ClickedItem) is { Selectable: true } row) choose(row.Id);
            }, () =>
            {
                if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
                list.Focus(FocusState.Programmatic);
            });
    }

    private static ListViewItem RowItem(OpenCodeAgentPickerRow row, bool isCurrent)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = OpenCodeAgentIconTheme.Create(row.Id, row.Color, 16);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var name = new TextBlock { Text = OpenCodeAgentIcons.DisplayName(row.Name), TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var item = new ListViewItem { Content = grid, Tag = row, IsEnabled = row.Selectable, MinHeight = 34,
            Padding = new Thickness(10, 4, 10, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(item, "OpenCodeAgentPickerRow." + row.Id);
        AutomationProperties.SetName(item, name.Text);
        return item;
    }
}
