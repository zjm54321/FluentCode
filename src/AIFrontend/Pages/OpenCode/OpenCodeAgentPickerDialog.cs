using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>智能体选择对话框中的一行（只读快照；不含客户端、租约或会话绑定）。</summary>
internal sealed record OpenCodeAgentPickerRow(string Id, string Name, string? Description, bool Selectable, string? Reason, string? Color = null);

/// <summary>
/// 重设计 §7：<c>/agents</c> 打开的原生 ContentDialog。只负责“选出一个目标”，不做任何 IO、不切换：
/// 返回的目标由 <see cref="OpenCodeAgentSelector"/> 在对话框关闭后以打开时捕获的 origin 复核，
/// 再走既有 <see cref="AIFrontend.Services.Notifications.AgentSwitchCoordinator"/> 流程（operationId、未知结果恢复不变）。
/// </summary>
internal static class OpenCodeAgentPickerDialog
{
    /// <returns>用户以“切换”确认的可选目标 id；取消、无变化或无法显示时为 null。</returns>
    internal static async Task<string?> ShowAsync(XamlRoot? owner, IReadOnlyList<OpenCodeAgentPickerRow> rows,
        string? currentId, string? currentLabel, string? blockedReason)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (owner is null) return null;

        var current = OpenCodeInteractionUi.StyledText(
            "当前：" + (currentLabel is { Length: > 0 } ? currentLabel : "未知"), OpenCodeInteractionUi.CaptionSubtleStyleKey);
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 360,
            MinWidth = 0,
        };
        AutomationProperties.SetAutomationId(list, "OpenCodeAgentSelectorCombo");
        AutomationProperties.SetName(list, "可切换的智能体");

        var selectedIndex = -1;
        foreach (var row in rows)
        {
            list.Items.Add(RowItem(row, string.Equals(row.Id, currentId, StringComparison.Ordinal)));
            if (string.Equals(row.Id, currentId, StringComparison.Ordinal)) selectedIndex = list.Items.Count - 1;
        }

        var body = new StackPanel { Spacing = 10, MinWidth = 0, MaxWidth = 520, Width = Math.Max(1, Math.Min(520, owner.Size.Width - 80)) };
        body.Children.Add(current);
        if (rows.Count == 0)
        {
            body.Children.Add(OpenCodeInteractionUi.StyledText("没有读取到可切换的智能体。", OpenCodeInteractionUi.BodyStyleKey));
        }
        else
        {
            body.Children.Add(list);
        }

        if (blockedReason is { Length: > 0 })
        {
            body.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Message = blockedReason,
            });
        }

        body.Children.Add(OpenCodeInteractionUi.StyledText(
            "只影响之后的回合。点“切换”后会重新核对会话状态；结果未知时不会自动重试。", OpenCodeInteractionUi.CaptionSubtleStyleKey));

        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = "切换智能体",
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "切换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None, // Enter in the list never switches by accident.
            IsPrimaryButtonEnabled = false,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        AutomationProperties.SetAutomationId(dialog, "OpenCodeAgentPickerDialog");
        AutomationProperties.SetName(dialog, "切换智能体");

        string? Selected() => list.SelectedItem is ListViewItem { Tag: OpenCodeAgentPickerRow { Selectable: true } row } ? row.Id : null;

        void UpdatePrimary()
            => dialog.IsPrimaryButtonEnabled = blockedReason is not { Length: > 0 }
                && Selected() is { } id && !string.Equals(id, currentId, StringComparison.Ordinal);

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListViewItem { Tag: OpenCodeAgentPickerRow { Selectable: false } })
            {
                list.SelectedIndex = -1; // Hidden/subagent rows are visible for context but never a target.
            }

            UpdatePrimary();
        };
        dialog.Opened += (_, _) =>
        {
            // ContentDialog template buttons: stable automation names for UI tests (template part names are fixed).
            if (FindButton(dialog, "PrimaryButton") is { } primary)
            {
                AutomationProperties.SetAutomationId(primary, "OpenCodeAgentSelectorSwitch");
                AutomationProperties.SetName(primary, "切换智能体");
            }

            if (FindButton(dialog, "CloseButton") is { } close)
            {
                AutomationProperties.SetAutomationId(close, "OpenCodeAgentPickerCancel");
            }

            if (list.SelectedItem is { } item) list.ScrollIntoView(item);
            list.Focus(FocusState.Programmatic);
        };

        list.SelectedIndex = selectedIndex;
        UpdatePrimary();

        // The target is frozen at the actual primary gesture.
        string? frozen = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            frozen = dialog.IsPrimaryButtonEnabled ? Selected() : null;
            if (frozen is null) args.Cancel = true;
        };

        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? frozen : null;
        }
        catch (Exception)
        {
            return null; // e.g. another dialog is already open: nothing is switched.
        }
    }

    private static ListViewItem RowItem(OpenCodeAgentPickerRow row, bool isCurrent)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var check = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
        };
        AutomationProperties.SetName(check, "当前");
        grid.Children.Add(check);

        var text = new StackPanel { Spacing = 2 };
        var name = OpenCodeInteractionUi.StyledText(row.Name, OpenCodeInteractionUi.BodyStrongStyleKey);
        OpenCodeAgentPresentation.WrapName(name);
        OpenCodeAgentPresentation.Apply(name, row.Color);
        ToolTipService.SetToolTip(name, row.Description);
        text.Children.Add(name);
        var detail = row.Selectable ? row.Description : row.Reason;
        if (detail is { Length: > 0 })
        {
            var caption = OpenCodeInteractionUi.StyledText(detail, OpenCodeInteractionUi.CaptionSubtleStyleKey);
            caption.MaxLines = 2;
            caption.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(caption);
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var item = new ListViewItem { Content = grid, Tag = row, IsEnabled = row.Selectable, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(item, "OpenCodeAgentPickerRow." + row.Id);
        AutomationProperties.SetName(item, row.Name + (isCurrent ? "（当前）" : string.Empty)
            + (detail is { Length: > 0 } ? "，" + detail : string.Empty));
        return item;
    }

    private static Button? FindButton(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && string.Equals(button.Name, name, StringComparison.Ordinal)) return button;
            if (FindButton(child, name) is { } nested) return nested;
        }

        return null;
    }
}
