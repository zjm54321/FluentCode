// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace FluentCode.Pages.OpenCode;

internal sealed partial class NativeUiRenderer
{
    private static (string Value, string Label, string Description)[] Choices(NativeUiElement node)
        => (P(node, "options") as IReadOnlyList<object?> ?? []).Select(option => option is IReadOnlyDictionary<string, object?> obj
            ? (NativeUiValues.Text(obj.GetValueOrDefault("value")), NativeUiValues.Text(obj.GetValueOrDefault("label")), NativeUiValues.Text(obj.GetValueOrDefault("description")))
            : (NativeUiValues.Text(option), NativeUiValues.Text(option), "")).ToArray();
    private Visual? CreateControls(string type, string key, int depth)
    {
        switch (type)
        {
            case "TextArea":
                var area = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = NativeUiCatalog.Default.Limits.StringLength }; var areaVisual = new Visual(area);
                area.TextChanged += (_, _) => SetField(areaVisual, key, "value", S(areaVisual.Model, "label"), area.Text);
                areaVisual.Apply = node => { area.Header = S(node, "label"); area.PlaceholderText = S(node, "placeholder"); NativeUiTheme.Input(area, node); area.MinHeight = 32 + 20 * Math.Clamp(N(node, "rows", 3), 2, 20); var text = NativeUiValues.Text(Field(areaVisual, key, "value", S(node, "label"))); if (text != area.Text) area.Text = text; AutomationProperties.SetName(area, S(node, "label")); }; return areaVisual;
            case "SegmentedControl":
                var segments = new Segmented { SelectionMode = ListViewSelectionMode.Single }; var segmented = new Visual(segments); string segmentSignature = "";
                segments.SelectionChanged += (_, _) => { if (segments.SelectedItem is SegmentedItem item) SetField(segmented, key, "value", "", item.Tag); };
                segmented.Apply = node =>
                {
                    var choices = Choices(node); var signature = NativeUiValues.ToJson(P(node, "options"))?.ToJsonString() ?? "";
                    if (signature != segmentSignature) { segments.Items.Clear(); foreach (var item in choices) segments.Items.Add(new SegmentedItem { Content = item.Label, Tag = item.Value }); segmentSignature = signature; }
                    segments.SelectedIndex = Array.FindIndex(choices, item => item.Value == NativeUiValues.Text(Field(segmented, key, "value", "")));
                    NativeUiTheme.Segmented(segments);
                    segments.HorizontalAlignment = B(node, "block") ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
                    AutomationProperties.SetName(segments, node.StatementId ?? "选项");
                }; return segmented;
            case "RadioGroup":
                var radioPanel = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var radioVisual = new Visual(radioPanel);
                RadioButtons? radios = null; Segmented? pills = null; string radioSignature = ""; var cardStyle = false;
                // The `cards` variant paints each tile from the checked state the host owns, so every selection change
                // repaints it, including a move between options that share a value.
                void StyleCards()
                {
                    if (!cardStyle || radios is null) return;
                    foreach (var tile in radios.Items.OfType<RadioButton>()) NativeUiTheme.RadioCard(tile);
                }
                // The host lays each item out at its own desired width, while the StackPanel it replaces stretched every
                // row, so a tile's minimum width follows the group. Reading the panel keeps this shrink-safe: a narrower
                // card narrows the panel, and the tiles follow it down again.
                void FitTiles()
                {
                    if (radioVisual.Disposed || radios is null) return;
                    var width = radioPanel.ActualWidth > 0 ? radioPanel.ActualWidth : radios.ActualWidth;
                    if (width <= 0) return;
                    foreach (var tile in radios.Items.OfType<RadioButton>()) if (tile.MinWidth != width) tile.MinWidth = width;
                }
                radioPanel.SizeChanged += (_, _) => FitTiles();
                radioVisual.Apply = node =>
                {
                    var choices = Choices(node); var variant = S(node, "variant"); var label = S(node, "label");
                    var signature = label + variant + NativeUiValues.ToJson(P(node, "options"))?.ToJsonString();
                    if (signature != radioSignature)
                    {
                        radioSignature = signature; radioPanel.Children.Clear(); radios = null; pills = null; cardStyle = variant == "cards";
                        if (variant == "pills")
                        {
                            if (label.Length > 0) radioPanel.Children.Add(NativeUiComponentTheme.Text(label));
                            pills = new Segmented { SelectionMode = ListViewSelectionMode.Single };
                            foreach (var option in choices) pills.Items.Add(new SegmentedItem { Content = option.Label, Tag = option.Value });
                            pills.SelectionChanged += (_, _) => { if (pills.SelectedItem is SegmentedItem item) SetField(radioVisual, key, "value", S(radioVisual.Model, "label"), item.Tag); };
                            radioPanel.Children.Add(pills);
                        }
                        else
                        {
                            // One host owns the group: arrow-key roving focus, a single tab stop and the group's UIA name
                            // come from it. The items stay RadioButtons, so the `cards` style is unchanged.
                            var host = new RadioButtons { MaxColumns = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
                            if (label.Length > 0) host.Header = NativeUiComponentTheme.Text(label);
                            // The header presenter would still contribute its margin without a label, so drop it.
                            else host.Resources["RadioButtonsTopHeaderMargin"] = new Thickness(0);
                            AutomationProperties.SetName(host, label.Length > 0 ? label : node.StatementId ?? "选项");
                            foreach (var choice in choices)
                            {
                                var tileLabel = new StackPanel { Spacing = NativeUiTheme.Gap(1) }; tileLabel.Children.Add(NativeUiComponentTheme.Text(choice.Label));
                                if (choice.Description.Length > 0) tileLabel.Children.Add(NativeUiComponentTheme.Text(choice.Description, true));
                                host.Items.Add(new RadioButton { Content = tileLabel, Tag = choice.Value });
                            }
                            host.Loaded += (_, _) => FitTiles();
                            // The commit point is unchanged: a new selection writes the field immediately. The host also
                            // re-asserts its own index while it loads, and that echo carries the value already applied
                            // above, so the field write is skipped without skipping the checked-state repaint.
                            host.SelectionChanged += (_, _) =>
                            {
                                if (radioVisual.Disposed) return;
                                var current = Choices(radioVisual.Model);
                                if (host.SelectedIndex >= 0 && host.SelectedIndex < current.Length)
                                {
                                    var value = current[host.SelectedIndex].Value;
                                    if (value != NativeUiValues.Text(Field(radioVisual, key, "value", label)))
                                        SetField(radioVisual, key, "value", label, value);
                                }
                                if (!radioVisual.Disposed) StyleCards();
                            };
                            radios = host; radioPanel.Children.Add(host); FitTiles();
                        }
                    }
                    var selected = NativeUiValues.Text(Field(radioVisual, key, "value", label));
                    if (radios is not null) { radios.SelectedIndex = Array.FindIndex(choices, choice => choice.Value == selected); StyleCards(); }
                    if (pills is not null) { pills.SelectedIndex = Array.FindIndex(choices, choice => choice.Value == selected); NativeUiTheme.Segmented(pills); }
                }; return radioVisual;
            case "DatePicker":
                var date = new CalendarDatePicker { DateFormat = "{year.full}-{month.integer(2)}-{day.integer(2)}", HorizontalAlignment = HorizontalAlignment.Stretch }; var dateVisual = new Visual(date);
                date.DateChanged += (_, _) => SetField(dateVisual, key, "value", S(dateVisual.Model, "label"), date.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
                dateVisual.Apply = node =>
                {
                    static DateTimeOffset? Parse(string text) => DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? new DateTimeOffset(value) : null;
                    var min = Parse(S(node, "min")) ?? new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero); var max = Parse(S(node, "max")) ?? new DateTimeOffset(2100, 12, 31, 0, 0, 0, TimeSpan.Zero);
                    if (max < min) max = min;
                    if (min > date.MaxDate) date.MaxDate = max;
                    if (max < date.MinDate) date.MinDate = min;
                    date.MaxDate = max; date.MinDate = min;
                    date.Date = Parse(NativeUiValues.Text(Field(dateVisual, key, "value", S(node, "label")))); date.Header = S(node, "label"); AutomationProperties.SetName(date, S(node, "label"));
                }; return dateVisual;
            case "IconButton":
                var iconButton = new Button(); var iconVisual = new Visual(iconButton);
                iconButton.Click += async (_, _) => { if (!iconVisual.Disposed && P(iconVisual.Model, "action") is NativeUiActionPlan plan) await _runtime.ExecuteAsync(plan); };
                iconVisual.Apply = node => { iconButton.Content = NativeUiTheme.Icon(S(node, "icon")); AutomationProperties.SetName(iconButton, S(node, "label") is { Length: > 0 } label ? label : S(node, "icon")); ToolTipService.SetToolTip(iconButton, S(node, "label")); NativeUiTheme.Button(iconButton, node); iconButton.IsEnabled = !_runtime.IsExecuting; }; return iconVisual;
            case "Pressable":
                var tileContent = new StackPanel { Spacing = NativeUiTheme.Gap(2) };
                // ContentControl's default template draws no Background/Border, so the tile colour, selection ring and
                // radius were invisible; this template paints them and stretches the content (Col align centres).
                var tile = new ContentControl { IsTabStop = true, Content = tileContent, Template = NativeUiTheme.TileTemplate, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch }; var pressable = new Visual(tile);
                async void Press() { if (!pressable.Disposed && P(pressable.Model, "action") is NativeUiActionPlan plan) await _runtime.ExecuteAsync(plan); }
                tile.Tapped += (_, args) => { if (args.OriginalSource is DependencyObject source) { for (var parent = source; parent is not null && !ReferenceEquals(parent, tile); parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent)) if (parent is Button or TextBox or CheckBox or ComboBox or Slider or RadioButton or Segmented) return; } Press(); args.Handled = true; };
                tile.KeyDown += (_, args) => { if (ReferenceEquals(args.OriginalSource, tile) && args.Key is VirtualKey.Enter or VirtualKey.Space) { Press(); args.Handled = true; } };
                pressable.Apply = node => { Slot(tileContent, P(node, "children"), key + "/children", depth); tile.Padding = new(NativeUiTheme.Gap(P(node, "padding"), 3)); tile.Background = ComponentBrush(S(node, "background") is { Length: > 0 } background ? background : "surface", true); tile.RequestedTheme = NativeUiTheme.ContrastTheme(S(node, "background")); tile.BorderThickness = new(B(node, "selected") ? 2 : 1); tile.BorderBrush = ComponentBrush(B(node, "selected") ? "info" : "faint"); tile.CornerRadius = NativeUiComponentTheme.Radius(P(node, "radius")); tile.IsEnabled = !_runtime.IsExecuting; }; return pressable;
            case "Timer":
                var timerPanel = new StackPanel { Spacing = NativeUiTheme.Gap(2) }; var timerRead = NativeUiComponentTheme.Text(""); var timerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = NativeUiTheme.Gap(2) }; var toggle = new Button { Content = "开始" }; var reset = new Button { Content = "重置" };
                timerButtons.Children.Add(toggle); timerButtons.Children.Add(reset); timerPanel.Children.Add(timerRead); timerPanel.Children.Add(timerButtons); var timerVisual = new Visual(timerPanel);
                var timer = timerPanel.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromSeconds(1); double initial = 0;
                double Left() { var value = NativeUiValues.Number(Field(timerVisual, key, "seconds", S(timerVisual.Model, "label"))); return double.IsFinite(value) ? Math.Max(0, Math.Floor(value)) : 0; }
                void OnTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) { if (timerVisual.Disposed || Left() <= 0) { timer.Stop(); return; } SetField(timerVisual, key, "seconds", S(timerVisual.Model, "label"), Math.Max(0, Left() - 1)); if (Left() <= 0) timer.Stop(); toggle.Content = timer.IsRunning ? "暂停" : "开始"; }
                timer.Tick += OnTimerTick;
                toggle.Click += (_, _) => { if (timer.IsRunning) timer.Stop(); else if (Left() > 0) timer.Start(); toggle.Content = timer.IsRunning ? "暂停" : "继续"; };
                reset.Click += (_, _) => { timer.Stop(); SetField(timerVisual, key, "seconds", S(timerVisual.Model, "label"), initial); toggle.Content = "开始"; };
                timerPanel.Unloaded += (_, _) => { timer.Stop(); toggle.Content = "继续"; }; timerVisual.Cleanup = () => { timer.Stop(); timer.Tick -= OnTimerTick; };
                timerVisual.Apply = node => { var left = Left(); initial = NativeUiValues.Number(_runtime.Field("$ui:" + key + ":timerInitial", left)); timerRead.Text = S(node, "label") + "  " + TimeSpan.FromSeconds(Math.Min(left, 35999999)).ToString(left >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture); toggle.IsEnabled = left > 0 && !_runtime.IsExecuting; reset.IsEnabled = left != initial; AutomationProperties.SetName(timerPanel, S(node, "label")); }; return timerVisual;
            default: return null;
        }
    }
}
