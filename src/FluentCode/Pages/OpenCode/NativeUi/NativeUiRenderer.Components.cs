// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    private Flyout? _confirmation;
    private Brush ComponentBrush(string token, bool background = false) => NativeUiComponentTheme.Brush(token, background, _root.ActualTheme);
    private static void ApplySpacers(Grid row)
    {
        foreach (var spacer in row.Children.OfType<FrameworkElement>().Where(child => Equals(child.Tag, "native-ui-spacer")))
        {
            var column = Grid.GetColumn(spacer);
            if (column < row.ColumnDefinitions.Count) row.ColumnDefinitions[column].Width = new(1, GridUnitType.Star);
            row.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }
    private static object? P(NativeUiElement node, string key) => NativeUiTheme.Prop(node, key);
    private static string S(NativeUiElement node, string key) => NativeUiTheme.String(node, key);
    private static double N(NativeUiElement node, string key, double fallback = 0) => NativeUiComponentTheme.Number(node, key, fallback);
    private static bool B(NativeUiElement node, string key) => NativeUiTheme.Flag(node, key);
    private StackPanel Slot(object? value, string path, int depth)
    {
        var panel = new StackPanel { Spacing = NativeUiTheme.Gap(2) };
        Sync(panel, Children(value, path, depth + 1).Cast<UIElement>().ToArray()); return panel;
    }
    private Visual CreateComponent(string type, string key, int depth)
    {
        var specialized = CreateData(type, key, depth) ?? CreateControls(type, key, depth) ?? CreateContent(type, key, depth) ?? CreateDrawing(type, key, depth);
        if (specialized is not null) return specialized;
        switch (type)
        {
            case "Box":
                var panel = new NativeUiFlexPanel(); var border = new Border { Child = panel }; var box = new Visual(border);
                box.Apply = node =>
                {
                    Sync(panel, Children(P(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray());
                    panel.Model = node; panel.InvalidateMeasure();
                    border.Padding = new(NativeUiTheme.Gap(P(node, "padding"), 0)); border.CornerRadius = NativeUiComponentTheme.Radius(P(node, "radius"));
                    border.Background = S(node, "background").Length > 0 ? ComponentBrush(S(node, "background"), true) : null;
                    border.BorderBrush = ComponentBrush("faint"); border.BorderThickness = new(B(node, "border") ? 1 : 0);
                    NativeUiComponentTheme.Length(border, P(node, "width")); NativeUiComponentTheme.Length(border, P(node, "height"), true);
                    border.Tag = node;
                }; return box;
            case "Grid":
                var grid = new Grid(); var gridVisual = new Visual(grid);
                void Layout()
                {
                    var node = gridVisual.Model; var columns = (int)Math.Clamp(N(node, "columns", 2), 1, 12); var min = N(node, "minChildWidth"); var gap = NativeUiTheme.Gap(P(node, "gap"));
                    if (min > 0 && grid.ActualWidth > 0) columns = Math.Max(1, Math.Min(columns, (int)((grid.ActualWidth + gap) / (min + gap))));
                    grid.ColumnSpacing = grid.RowSpacing = gap; grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
                    for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                    for (var i = 0; i < (grid.Children.Count + columns - 1) / columns; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
                    for (var i = 0; i < grid.Children.Count; i++) { Grid.SetRow((FrameworkElement)grid.Children[i], i / columns); Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns); }
                }
                grid.SizeChanged += (_, _) => { if (!gridVisual.Disposed) Layout(); };
                gridVisual.Apply = node => { Sync(grid, Children(P(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray()); Layout(); }; return gridVisual;
            case "Form":
                var form = new StackPanel { Spacing = NativeUiTheme.Gap(3) }; var formVisual = new Visual(form);
                formVisual.Apply = node => Sync(form, Children(P(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray()); return formVisual;
            case "Spacer": return new(new Border { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "native-ui-spacer" });
            case "Carousel":
                var track = new StackPanel { Orientation = Orientation.Horizontal }; var scroll = new ScrollViewer { Content = track, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled }; var carousel = new Visual(scroll);
                carousel.Apply = node => { var items = Children(P(node, "children"), key + "/children", depth + 1).ToArray(); foreach (var item in items) item.Width = Math.Clamp(N(node, "itemWidth", 240), 80, 640); track.Spacing = NativeUiTheme.Gap(P(node, "gap")); Sync(track, items.Cast<UIElement>().ToArray()); }; return carousel;
            case "Popover":
                var triggerContent = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var popoverContent = new StackPanel { Spacing = NativeUiTheme.Gap(2) };
                var trigger = new Button { Content = triggerContent }; var flyout = new Flyout { Content = popoverContent }; var popover = new Visual(trigger); trigger.Flyout = flyout;
                AttachAirspace(flyout); popover.Cleanup = flyout.Hide;
                popover.Apply = node => { Sync(triggerContent, Children(P(node, "trigger"), key + "/trigger", depth + 1).Cast<UIElement>().ToArray()); Sync(popoverContent, Children(P(node, "content"), key + "/content", depth + 1).Cast<UIElement>().ToArray()); }; return popover;
            default: return new(NativeUiTheme.Notice("未支持", type));
        }
    }
    private static void AttachAirspace(Flyout flyout)
    {
        var leases = new List<IDisposable>();
        flyout.Opening += (_, _) =>
        {
            void Visit(DependencyObject root)
            {
                if (root is OpenCodePersistentPtyRenderer terminal) { leases.Add(terminal.SuppressAirspace(snapshot: true)); return; }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Visit(VisualTreeHelper.GetChild(root, i));
            }
            if (flyout.Target?.XamlRoot?.Content is { } content) Visit(content);
        };
        flyout.Closed += (_, _) => { foreach (var lease in leases) lease.Dispose(); leases.Clear(); };
    }
    private async Task<NativeUiHostResult> ConfirmUrlAsync(string url)
    {
        if (!NativeUiSafety.HttpUrl(url)) return new(false, "仅允许 http(s) URL");
        if (_root.XamlRoot is null) return new(false, "卡片当前不可见");
        var completion = new TaskCompletionSource<bool>();
        var content = new StackPanel { Spacing = NativeUiTheme.Gap(3), MaxWidth = 480 };
        content.Children.Add(NativeUiComponentTheme.Text("是否在浏览器中打开此链接？"));
        content.Children.Add(new ScrollViewer { Content = NativeUiComponentTheme.Text(url), MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = NativeUiTheme.Gap(2) }; var open = new Button { Content = "确认打开" }; var cancel = new Button { Content = "取消" };
        buttons.Children.Add(open); buttons.Children.Add(cancel); content.Children.Add(buttons);
        var flyout = new Flyout { Content = content }; AttachAirspace(flyout);
        open.Click += (_, _) => { completion.TrySetResult(true); flyout.Hide(); };
        cancel.Click += (_, _) => flyout.Hide(); flyout.Closed += (_, _) => completion.TrySetResult(false);
        _confirmation?.Hide(); _confirmation = flyout; flyout.ShowAt(_root);
        if (!await completion.Task) return new(false, "已取消打开链接");
        try { return await Launcher.LaunchUriAsync(new Uri(url)) ? new(true, "已在浏览器中打开") : new(false, "系统未能打开此链接"); }
        catch (Exception error) { return new(false, "无法打开链接：" + error.Message); }
    }
    private Task OpenAsync(string url) => _runtime.ExecuteAsync(new(new NativeUiActionStep[] { new("open_url", Url: url) }));
}
