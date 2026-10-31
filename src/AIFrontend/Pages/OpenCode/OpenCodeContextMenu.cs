using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AIFrontend.Pages.OpenCode;

/// <summary>One context-menu command. <see cref="Row"/> puts it in the labeled icon row at the bottom.</summary>
internal sealed record OpenCodeContextCommand(string Label, string Glyph, Action Invoke)
{
    /// <summary>Bottom command row (Windows 11 text menu: 剪切 复制 粘贴 全选 删除). Short, common verbs only.</summary>
    public bool Row { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Shortcut hint shown in the list and in the row cell's tooltip. Real shortcuts only.</summary>
    public string? Accelerator { get; init; }
    /// <summary>Tooltip; for disabled commands, the reason.</summary>
    public string? Description { get; init; }
    public bool Critical { get; init; }
    /// <summary>Draws the glyph mirrored (redo from the undo arrow).</summary>
    public bool Mirrored { get; init; }
    public string? AutomationId { get; init; }
}

/// <summary>
/// The app's single context-menu shape (right-click, menu key / Shift+F10 and touch press-and-hold), modelled on the
/// Windows 11 text context menu: ONE acrylic menu surface (MenuFlyoutPresenter brushes, 8px overlay corner, flyout
/// shadow) with the labeled command list on top and, under a separator, a full-width row of equal cells, each an
/// icon over a small label, divided by thin vertical lines.
/// <para>
/// It is a single <see cref="Flyout"/> on purpose. The previous <see cref="CommandBarFlyout"/> drew its row and its
/// list as two separate windowed popups; when it opened downward the list popup's hit area covered the row, so the
/// row buttons could not be clicked (hit-testing the row returned the overflow Popup, never the AppBarButton), and it
/// flipped the row above or below depending on the open direction. One surface fixes both: the order never changes.
/// </para>
/// Ordering convention: list = object-specific commands first, then (after a separator) state-changing ones;
/// row = 剪切 · 复制 · 粘贴 · 全选 · 重命名 · 保存 · 关闭/删除, only those that apply.
/// Commands are rebuilt on every Opening, so availability is never stale; null list entries are separators.
/// </summary>
internal static class OpenCodeContextMenu
{
    internal const string CommandTag = "OpenCodeContextCommand";
    private const double RowHeight = 56, ListItemHeight = 36, MenuMinWidth = 280, CellMinWidth = 64;

    internal static Flyout Create(Func<FlyoutBase, IReadOnlyList<OpenCodeContextCommand?>> build)
    {
        var flyout = new Flyout { FlyoutPresenterStyle = PresenterStyle(), ShouldConstrainToRootBounds = false };
        flyout.Opening += (_, _) => flyout.Content = Build(flyout, build(flyout));
        flyout.Closed += (_, _) => flyout.Content = null; // Release the per-open commands and their captured targets.
        return flyout;
    }

    internal static Flyout Create(IReadOnlyList<OpenCodeContextCommand?> commands) => Create(_ => commands);

    /// <summary>Builds the menu surface; exposed so the fixture can invoke every command through UI Automation.</summary>
    internal static FrameworkElement Build(FlyoutBase flyout, IReadOnlyList<OpenCodeContextCommand?> commands)
    {
        var root = new StackPanel
        {
            MinWidth = MenuMinWidth, Padding = new Thickness(0, 2, 0, 0),
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled, // Arrow keys move like a menu.
            TabFocusNavigation = KeyboardNavigationMode.Cycle,
            Resources = ButtonResources(),
        };
        AutomationProperties.SetName(root, "上下文菜单");
        var list = new List<OpenCodeContextCommand?>();
        var row = new List<OpenCodeContextCommand>();
        foreach (var command in commands)
            if (command is { Row: true }) row.Add(command); else list.Add(command);

        var pendingSeparator = false;
        var listed = 0;
        foreach (var command in list)
        {
            if (command is null) { pendingSeparator = listed > 0; continue; }
            if (pendingSeparator) root.Children.Add(Separator());
            pendingSeparator = false;
            root.Children.Add(ListItem(flyout, command));
            listed++;
        }
        if (row.Count > 0)
        {
            if (listed > 0) root.Children.Add(Separator());
            root.MinWidth = Math.Max(MenuMinWidth, row.Count * CellMinWidth + (row.Count - 1) + 8);
            root.Children.Add(Row(flyout, row));
        }
        else root.Padding = new Thickness(0, 2, 0, 2);
        return root;
    }

    // Same metrics as MenuFlyoutItem: 4,2 margin, 11,8,11,9 padding, 4px corner, 16px icon, 12px gap, caption accelerator.
    private static Button ListItem(FlyoutBase flyout, OpenCodeContextCommand command)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Icon(command));
        var label = new TextBlock { Text = command.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (command.Accelerator is { } key)
        {
            var accelerator = new TextBlock
            {
                Text = key, FontSize = 12, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Style = Themed("TextBlock", "Foreground", command.Enabled ? "TextFillColorSecondaryBrush" : "TextFillColorDisabledBrush"),
            };
            Grid.SetColumn(accelerator, 2);
            grid.Children.Add(accelerator);
        }
        var button = Cell(flyout, command, grid);
        button.Margin = new Thickness(4, 2, 4, 2);
        button.Padding = new Thickness(11, 8, 11, 9);
        button.MinHeight = ListItemHeight;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (command.Description is { } why) ToolTipService.SetToolTip(button, why);
        return button;
    }

    private static Grid Row(FlyoutBase flyout, IReadOnlyList<OpenCodeContextCommand> commands)
    {
        var grid = new Grid { Padding = new Thickness(4, 4, 4, 4) };
        for (var i = 0; i < commands.Count; i++)
        {
            if (i > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                var divider = new Rectangle
                {
                    Width = 1, Height = 24, VerticalAlignment = VerticalAlignment.Center,
                    Style = Themed("Rectangle", "Fill", "DividerStrokeColorDefaultBrush"),
                };
                Grid.SetColumn(divider, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(divider);
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = CellMinWidth });
            var command = commands[i];
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = Icon(command);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(icon);
            content.Children.Add(new TextBlock
            {
                Text = command.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            });
            var cell = Cell(flyout, command, content);
            cell.Margin = new Thickness(2, 0, 2, 0);
            cell.Padding = new Thickness(4, 6, 4, 6);
            cell.MinHeight = RowHeight - 8;
            cell.HorizontalContentAlignment = HorizontalAlignment.Center;
            var tip = command.Accelerator is { } key ? command.Label + " (" + key + ")" : command.Label;
            if (!command.Enabled && command.Description is { } why) tip += "\n" + why;
            else if (command.Description is { } more) tip += "\n" + more;
            ToolTipService.SetToolTip(cell, tip);
            Grid.SetColumn(cell, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static Button Cell(FlyoutBase flyout, OpenCodeContextCommand command, UIElement content)
    {
        var button = new Button
        {
            Content = content, IsEnabled = command.Enabled, Tag = CommandTag,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            UseSystemFocusVisuals = true,
        };
        AutomationProperties.SetName(button, command.Label);
        AutomationProperties.SetAutomationControlType(button, AutomationControlType.MenuItem);
        if (command.Accelerator is { } key) AutomationProperties.SetAcceleratorKey(button, key);
        if (command.AutomationId is { } id) AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) =>
        {
            // Close first (as MenuFlyoutItem does) so dialogs/navigation started by the command are not under the menu.
            flyout.Hide();
            command.Invoke();
        };
        return button;
    }

    private static Border Separator() => new()
    {
        Height = 1, Margin = new Thickness(0, 2, 0, 2),
        Style = Themed("Border", "Background", "DividerStrokeColorDefaultBrush"),
    };

    private static FontIcon Icon(OpenCodeContextCommand command)
    {
        var icon = new FontIcon { Glyph = command.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        if (command.Mirrored)
        {
            icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        // Icon only: the label keeps the theme text colour so the disabled state still reads correctly.
        if (command.Critical && command.Enabled) icon.Style = Themed("FontIcon", "Foreground", "SystemFillColorCriticalBrush");
        return icon;
    }

    private const string Xmlns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
    [ThreadStatic] private static Dictionary<string, Style?>? _styles;

    // ThemeResource setters so colours follow light/dark/high-contrast changes.
    private static Style? Themed(string type, string property, string key)
    {
        _styles ??= new Dictionary<string, Style?>(StringComparer.Ordinal);
        var cacheKey = type + "|" + property + "|" + key;
        if (_styles.TryGetValue(cacheKey, out var cached)) return cached;
        Style? style = null;
        try
        {
            style = (Style)XamlReader.Load("<Style " + Xmlns + " TargetType=\"" + type + "\"><Setter Property=\"" + property
                + "\" Value=\"{ThemeResource " + key + "}\" /></Style>");
        }
        catch (Exception) { }
        return _styles[cacheKey] = style;
    }

    // The menu surface uses the MenuFlyoutPresenter resources, so it is indistinguishable from a stock MenuFlyout.
    private static Style? PresenterStyle()
    {
        _styles ??= new Dictionary<string, Style?>(StringComparer.Ordinal);
        if (_styles.TryGetValue("presenter", out var cached)) return cached;
        Style? style = null;
        try
        {
            style = (Style)XamlReader.Load("<Style " + Xmlns + " TargetType=\"FlyoutPresenter\" BasedOn=\"{StaticResource DefaultFlyoutPresenterStyle}\">"
                + "<Setter Property=\"Padding\" Value=\"0\" />"
                + "<Setter Property=\"MinWidth\" Value=\"0\" /><Setter Property=\"MinHeight\" Value=\"0\" /><Setter Property=\"MaxWidth\" Value=\"480\" />"
                + "<Setter Property=\"Background\" Value=\"{ThemeResource MenuFlyoutPresenterBackground}\" />"
                + "<Setter Property=\"BorderBrush\" Value=\"{ThemeResource MenuFlyoutPresenterBorderBrush}\" />"
                + "<Setter Property=\"BorderThickness\" Value=\"1\" />"
                + "<Setter Property=\"CornerRadius\" Value=\"{ThemeResource OverlayCornerRadius}\" />"
                + "<Setter Property=\"FontFamily\" Value=\"{ThemeResource ContentControlThemeFontFamily}\" />"
                + "<Setter Property=\"ScrollViewer.HorizontalScrollMode\" Value=\"Disabled\" />"
                + "<Setter Property=\"ScrollViewer.HorizontalScrollBarVisibility\" Value=\"Disabled\" />"
                + "</Style>");
        }
        catch (Exception) { }
        return _styles["presenter"] = style;
    }

    // Lightweight styling: Button states mapped to MenuFlyoutItem's subtle fills (transparent rest, subtle hover/press).
    // Brushes over ThemeResource *colours*: a StaticResource alias inside a theme dictionary would freeze the brush of
    // the theme active at load time (white text in a light menu).
    private static ResourceDictionary ButtonResources()
    {
        const string theme = "<SolidColorBrush x:Key=\"ButtonBackground\" Color=\"{ThemeResource SubtleFillColorTransparent}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBackgroundPointerOver\" Color=\"{ThemeResource SubtleFillColorSecondary}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBackgroundPressed\" Color=\"{ThemeResource SubtleFillColorTertiary}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBackgroundDisabled\" Color=\"{ThemeResource SubtleFillColorTransparent}\" />"
            + "<SolidColorBrush x:Key=\"ButtonForeground\" Color=\"{ThemeResource TextFillColorPrimary}\" />"
            + "<SolidColorBrush x:Key=\"ButtonForegroundPointerOver\" Color=\"{ThemeResource TextFillColorPrimary}\" />"
            + "<SolidColorBrush x:Key=\"ButtonForegroundPressed\" Color=\"{ThemeResource TextFillColorSecondary}\" />"
            + "<SolidColorBrush x:Key=\"ButtonForegroundDisabled\" Color=\"{ThemeResource TextFillColorDisabled}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBorderBrush\" Color=\"{ThemeResource SubtleFillColorTransparent}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBorderBrushPointerOver\" Color=\"{ThemeResource SubtleFillColorTransparent}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBorderBrushPressed\" Color=\"{ThemeResource SubtleFillColorTransparent}\" />"
            + "<SolidColorBrush x:Key=\"ButtonBorderBrushDisabled\" Color=\"{ThemeResource SubtleFillColorTransparent}\" />";
        try
        {
            return (ResourceDictionary)XamlReader.Load("<ResourceDictionary " + Xmlns + "><ResourceDictionary.ThemeDictionaries>"
                + "<ResourceDictionary x:Key=\"Default\">" + theme + "</ResourceDictionary>"
                + "<ResourceDictionary x:Key=\"Light\">" + theme + "</ResourceDictionary>"
                + "<ResourceDictionary x:Key=\"HighContrast\" />"
                + "</ResourceDictionary.ThemeDictionaries></ResourceDictionary>");
        }
        catch (Exception) { return new ResourceDictionary(); }
    }
}
