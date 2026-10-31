using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.OpenCode;

/// <summary>One context-menu command. <see cref="Row"/> puts it in the icon-only top row.</summary>
internal sealed record OpenCodeContextCommand(string Label, string Glyph, Action Invoke)
{
    /// <summary>Icon-only top row (Windows 11 File Explorer style). Use only for verbs whose icon is unambiguous.</summary>
    public bool Row { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Shortcut hint shown in the list, and in the row button's tooltip. Real shortcuts only.</summary>
    public string? Accelerator { get; init; }
    /// <summary>Tooltip; for disabled commands, the reason.</summary>
    public string? Description { get; init; }
    public bool Critical { get; init; }
    /// <summary>Draws the glyph mirrored (redo from the undo arrow).</summary>
    public bool Mirrored { get; init; }
    public string? AutomationId { get; init; }
}

/// <summary>
/// The app's single context-menu shape (right-click, menu key / Shift+F10 and touch press-and-hold): a Fluent
/// <see cref="CommandBarFlyout"/>, always expanded, with an icon-only row of common verbs on top and a labeled list
/// below. Ordering convention for every menu:
/// <list type="bullet">
/// <item>Row, left to right: 剪切, 复制, 粘贴, 全选, 重命名, 保存/共享, 关闭/删除 (only those that apply).</item>
/// <item>List: object-specific commands first, then a separator and state-changing / destructive ones last.</item>
/// </list>
/// Commands are rebuilt on every Opening from <paramref name="build"/>, so availability is never stale.
/// A null entry in the list is a separator (leading/trailing/double separators are dropped).
/// </summary>
internal static class OpenCodeContextMenu
{
    internal static CommandBarFlyout Create(Func<FlyoutBase, IReadOnlyList<OpenCodeContextCommand?>> build)
    {
        var flyout = new CommandBarFlyout { AlwaysExpanded = true, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => Populate(flyout, build(flyout));
        return flyout;
    }

    /// <summary>
    /// Fixed command list. With no row command (e.g. a lone 复制路径) a CommandBarFlyout would render an empty
    /// bar, so the same items are shown as a plain Fluent MenuFlyout list instead — identical icons and order.
    /// </summary>
    internal static FlyoutBase Create(IReadOnlyList<OpenCodeContextCommand?> commands)
    {
        foreach (var command in commands) if (command?.Row == true) return Create(_ => commands);
        var menu = new MenuFlyout();
        var pendingSeparator = false;
        foreach (var command in commands)
        {
            if (command is null) { pendingSeparator = menu.Items.Count > 0; continue; }
            if (pendingSeparator) menu.Items.Add(new MenuFlyoutSeparator());
            pendingSeparator = false;
            var icon = Icon(command);
            var item = new MenuFlyoutItem { Text = command.Label, Icon = icon, IsEnabled = command.Enabled };
            if (command.Accelerator is { } key) item.KeyboardAcceleratorTextOverride = key;
            if (command.Description is { } why) ToolTipService.SetToolTip(item, why);
            if (command.AutomationId is { } id) AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => command.Invoke();
            menu.Items.Add(item);
        }
        return menu;
    }

    private static void Populate(CommandBarFlyout flyout, IReadOnlyList<OpenCodeContextCommand?> commands)
    {
        flyout.PrimaryCommands.Clear();
        flyout.SecondaryCommands.Clear();
        var pendingSeparator = false;
        foreach (var command in commands)
        {
            if (command is null) { pendingSeparator = flyout.SecondaryCommands.Count > 0; continue; }
            if (command.Row) { flyout.PrimaryCommands.Add(RowButton(command)); continue; }
            if (pendingSeparator) flyout.SecondaryCommands.Add(new AppBarSeparator());
            pendingSeparator = false;
            flyout.SecondaryCommands.Add(ListButton(command));
        }
    }

    private static AppBarButton RowButton(OpenCodeContextCommand command)
    {
        // Icon only; the label stays the accessible name and the tooltip carries label + shortcut.
        var button = Base(command);
        button.LabelPosition = CommandBarLabelPosition.Collapsed;
        button.Width = 40;
        button.MinHeight = 40;
        var tip = command.Accelerator is { } key ? command.Label + " (" + key + ")" : command.Label;
        if (!command.Enabled && command.Description is { } why) tip += "\n" + why;
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    private static AppBarButton ListButton(OpenCodeContextCommand command)
    {
        var button = Base(command);
        if (command.Accelerator is { } key) button.KeyboardAcceleratorTextOverride = key;
        if (command.Description is { } why) ToolTipService.SetToolTip(button, why);
        return button;
    }

    private static AppBarButton Base(OpenCodeContextCommand command)
    {
        var icon = Icon(command);
        var button = new AppBarButton { Label = command.Label, Icon = icon, IsEnabled = command.Enabled };
        AutomationProperties.SetName(button, command.Label);
        if (command.AutomationId is { } id) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => command.Invoke();
        return button;
    }

    private static FontIcon Icon(OpenCodeContextCommand command)
    {
        var icon = new FontIcon { Glyph = command.Glyph };
        if (command.Mirrored)
        {
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        // Icon only: the label keeps the theme text colour so the disabled state still reads correctly.
        if (command.Critical && CriticalIcon() is { } style) icon.Style = style;
        return icon;
    }

    [ThreadStatic] private static Style? _criticalIcon;

    // ThemeResource setter so the colour follows light/dark/high-contrast changes while the menu is reused.
    private static Style? CriticalIcon()
    {
        try
        {
            return _criticalIcon ??= (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"FontIcon\">"
                + "<Setter Property=\"Foreground\" Value=\"{ThemeResource SystemFillColorCriticalBrush}\" /></Style>");
        }
        catch (Exception) { return null; }
    }
}
