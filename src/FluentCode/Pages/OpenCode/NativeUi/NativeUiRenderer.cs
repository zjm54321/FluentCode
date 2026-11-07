// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace FluentCode.Pages.OpenCode;

// UI-thread owned. Reconcile by structural path so inputs retain focus/caret and slider drag identity.
internal sealed partial class NativeUiRenderer : IDisposable
{
    private sealed class Visual(FrameworkElement element)
    {
        internal FrameworkElement Element { get; } = element;
        internal NativeUiElement Model { get; set; } = new("", new Dictionary<string, object?>());
        internal Action<NativeUiElement> Apply { get; set; } = _ => { };
        internal bool Updating, Disposed;
        internal Action? Cleanup;
    }
    private readonly NativeUiRuntime _runtime;
    private readonly Border _root = new();
    private readonly Dictionary<string, Visual> _visuals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private NativeUiBudget? _budget;
    private NativeUiElement? _lastRoot;
    private bool _disposed;
    internal FrameworkElement Element => _root;
    internal NativeUiRenderer(NativeUiRuntime runtime)
    {
        _runtime = runtime; NativeUiTheme.Host(_root); _runtime.OpenUrl = ConfirmUrlAsync;
        _root.ActualThemeChanged += (_, _) => _root.DispatcherQueue.TryEnqueue(() => { if (!_disposed && _lastRoot is { } root) Render(root); });
    }
    internal void Render(NativeUiElement root)
    {
        _lastRoot = root;
        _seen.Clear(); _budget = new(NativeUiCatalog.Default.Limits);
        var element = Node(root, "root", 0);
        if (!ReferenceEquals(_root.Child, element)) { Detach(element); _root.Child = element; }
        foreach (var key in _visuals.Keys.Where(key => !_seen.Contains(key)).ToArray()) { var old = _visuals[key]; old.Disposed = true; old.Cleanup?.Invoke(); _visuals.Remove(key); }
    }
    private FrameworkElement Node(NativeUiElement node, string path, int depth)
    {
        _budget!.Visit(depth);
        var key = path + "#" + node.StatementId + ":" + node.TypeName; _seen.Add(key);
        if (!_visuals.TryGetValue(key, out var visual)) { visual = Create(node.TypeName, key, depth); _visuals[key] = visual; }
        visual.Model = node; visual.Updating = true;
        try { visual.Apply(node); } finally { visual.Updating = false; }
        return visual.Element;
    }
    private IEnumerable<FrameworkElement> Children(object? value, string path, int depth)
    {
        _budget!.Visit(depth);
        if (value is NativeUiElement element) yield return Node(element, path, depth + 1);
        else if (value is IReadOnlyList<object?> list)
            for (var index = 0; index < list.Count; index++) foreach (var child in Children(list[index], path + "/" + index, depth + 1)) yield return child;
        else if (value is not null)
        {
            var text = new TextBlock { Text = NativeUiValues.Text(value) }; NativeUiTheme.Text(text, new("Text", new Dictionary<string, object?>())); yield return text;
        }
    }
    private static void Detach(UIElement element)
    {
        if (element is not FrameworkElement control) return;
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(element); break;
            case Border border when ReferenceEquals(border.Child, element): border.Child = null; break;
            case ContentControl content when ReferenceEquals(content.Content, element): content.Content = null; break;
        }
    }
    private static void Sync(Panel panel, IReadOnlyList<UIElement> elements)
    {
        for (var index = panel.Children.Count - 1; index >= 0; index--) if (!elements.Contains(panel.Children[index])) panel.Children.RemoveAt(index);
        for (var index = 0; index < elements.Count; index++)
        {
            var child = elements[index];
            if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], child)) continue;
            Detach(child); panel.Children.Insert(index, child);
        }
    }
    private string FieldName(Visual visual, string key, string label) => string.IsNullOrEmpty(label) ? visual.Model.StatementId ?? key : label;
    private object? Field(Visual visual, string key, string prop, string label) => _runtime.Field(FieldName(visual, key, label), NativeUiTheme.Prop(visual.Model, prop));
    private void SetField(Visual visual, string key, string prop, string label, object? value)
    {
        if (visual.Updating || visual.Disposed) return;
        _runtime.TrySetField(FieldName(visual, key, label), NativeUiTheme.Prop(visual.Model, prop), value);
    }
    private Visual Create(string type, string key, int depth)
    {
        switch (type)
        {
            case "Card":
                var cardPanel = new StackPanel(); var card = new Border { Child = cardPanel }; var cardVisual = new Visual(card);
                cardVisual.Apply = node => { Sync(cardPanel, Children(NativeUiTheme.Prop(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray()); NativeUiTheme.Card(card, cardPanel, node); };
                return cardVisual;
            case "Col":
                var col = new StackPanel(); var colVisual = new Visual(col);
                colVisual.Apply = node => { Sync(col, Children(NativeUiTheme.Prop(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray()); NativeUiTheme.Column(col, node); };
                return colVisual;
            case "Row":
                var row = new Grid(); var rowVisual = new Visual(row);
                row.SizeChanged += (_, _) => { if (!rowVisual.Disposed) { NativeUiTheme.Row(row, rowVisual.Model); ApplySpacers(row); } };
                rowVisual.Apply = node => { Sync(row, Children(NativeUiTheme.Prop(node, "children"), key + "/children", depth + 1).Cast<UIElement>().ToArray()); NativeUiTheme.Row(row, node); ApplySpacers(row); };
                return rowVisual;
            case "Title": case "Caption":
                var title = new TextBlock(); var titleVisual = new Visual(title);
                titleVisual.Apply = node => { title.Text = NativeUiTheme.String(node, "text"); NativeUiTheme.Text(title, node); };
                return titleVisual;
            case "Text":
                var text = new RichTextBlock(); var paragraph = new Paragraph(); text.Blocks.Add(paragraph); var textVisual = new Visual(text);
                textVisual.Apply = node =>
                {
                    foreach (var inline in paragraph.Inlines.OfType<InlineUIContainer>()) inline.Child = null;
                    paragraph.Inlines.Clear(); NativeUiTheme.RichText(text, node);
                    void Inline(object? value, string path, int childDepth)
                    {
                        if (value is NativeUiElement element) paragraph.Inlines.Add(new InlineUIContainer { Child = Node(element, path, childDepth + 1) });
                        else if (value is IReadOnlyList<object?> array) for (var i = 0; i < array.Count; i++) Inline(array[i], path + "/" + i, childDepth + 1);
                        else { var run = new Run { Text = NativeUiValues.Text(value) }; NativeUiTheme.Inline(run, node); paragraph.Inlines.Add(run); }
                    }
                    Inline(NativeUiTheme.Prop(node, "text"), key + "/text", depth + 1);
                };
                return textVisual;
            case "Badge":
                var badgePanel = new StackPanel { Orientation = Orientation.Horizontal }; var badgeText = new TextBlock(); var badge = new Border { Child = badgePanel }; var badgeVisual = new Visual(badge);
                badgeVisual.Apply = node =>
                {
                    badgeText.Text = NativeUiTheme.String(node, "text"); NativeUiTheme.Text(badgeText, node); NativeUiTheme.Badge(badge, node);
                    var icon = NativeUiTheme.Icon(NativeUiTheme.String(node, "icon"));
                    Sync(badgePanel, icon is null ? new UIElement[] { badgeText } : new UIElement[] { icon, badgeText });
                };
                return badgeVisual;
            case "Divider":
                var divider = new Border(); NativeUiTheme.Divider(divider); return new(divider);
            case "Markdown":
                var markdown = new OpenCodeRichTextPresenter(nativeUiSandbox: true); var markdownVisual = new Visual(markdown.Element);
                markdownVisual.Apply = node => markdown.Render(NativeUiTheme.String(node, "text"), OpenCodeRichTextStyle.Body);
                return markdownVisual;
            case "Button":
                var button = new Button(); var buttonVisual = new Visual(button);
                button.Click += async (_, _) =>
                {
                    if (buttonVisual.Disposed || !button.IsEnabled) return;
                    var node = buttonVisual.Model;
                    var plan = NativeUiTheme.Prop(node, "action") as NativeUiActionPlan
                        ?? new NativeUiActionPlan(new NativeUiActionStep[] { new("continue_conversation", Message: NativeUiTheme.String(node, "label")) });
                    await _runtime.ExecuteAsync(plan);
                };
                buttonVisual.Apply = node =>
                {
                    var label = NativeUiTheme.String(node, "label"); var icon = NativeUiTheme.Icon(NativeUiTheme.String(node, "icon"));
                    button.Content = icon is null ? label : new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = label } } };
                    AutomationProperties.SetName(button, label);
                    button.IsEnabled = !NativeUiTheme.Flag(node, "disabled") && !_runtime.IsExecuting; NativeUiTheme.Button(button, node);
                };
                return buttonVisual;
            case "Input":
                var input = new TextBox(); var inputVisual = new Visual(input); object? shown = NativeUiValues.Missing;
                input.TextChanged += (_, _) =>
                {
                    if (inputVisual.Updating || inputVisual.Disposed) return;
                    var node = inputVisual.Model; object? value = input.Text;
                    if (NativeUiTheme.String(node, "type") == "number")
                    {
                        if (input.Text.Trim().Length == 0) value = null;
                        else if (double.TryParse(input.Text.Replace(",", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) value = number;
                        else return; // Preserve an incomplete numeric edit until it parses or focus leaves.
                    }
                    shown = value; SetField(inputVisual, key, "value", NativeUiTheme.String(node, "label"), value);
                };
                input.LostFocus += (_, _) =>
                {
                    if (inputVisual.Disposed) return;
                    inputVisual.Updating = true;
                    try { input.Text = NativeUiValues.Text(Field(inputVisual, key, "value", NativeUiTheme.String(inputVisual.Model, "label"))); }
                    finally { inputVisual.Updating = false; }
                };
                inputVisual.Apply = node =>
                {
                    input.Header = NativeUiTheme.String(node, "label"); input.PlaceholderText = NativeUiTheme.String(node, "placeholder");
                    input.MaxLength = NativeUiCatalog.Default.Limits.StringLength;
                    var value = Field(inputVisual, key, "value", NativeUiTheme.String(node, "label"));
                    if (!Equals(value, shown)) { input.Text = NativeUiValues.Text(value); shown = value; }
                    AutomationProperties.SetName(input, NativeUiTheme.String(node, "label"));
                };
                return inputVisual;
            case "Slider":
                var slider = new Slider(); var sliderVisual = new Visual(slider);
                slider.ValueChanged += (_, _) => SetField(sliderVisual, key, "value", NativeUiTheme.String(sliderVisual.Model, "label"), slider.Value);
                sliderVisual.Apply = node =>
                {
                    var min = NativeUiValues.Number(NativeUiTheme.Prop(node, "min")); var max = NativeUiValues.Number(NativeUiTheme.Prop(node, "max"));
                    if (!double.IsFinite(min)) min = 0; if (!double.IsFinite(max) || max <= min) max = min + 100;
                    slider.Minimum = Math.Min(slider.Minimum, min); slider.Maximum = max; slider.Minimum = min;
                    var step = NativeUiValues.Number(NativeUiTheme.Prop(node, "step")); slider.StepFrequency = double.IsFinite(step) && step > 0 ? step : 1;
                    var value = NativeUiValues.Number(Field(sliderVisual, key, "value", NativeUiTheme.String(node, "label"))); if (!double.IsFinite(value)) value = min;
                    slider.Value = Math.Clamp(value, min, max);
                    var unit = NativeUiTheme.String(node, "unit"); var label = NativeUiTheme.String(node, "label");
                    var readout = unit.StartsWith('¥') || unit.StartsWith('$') || unit.StartsWith('€') ? unit + NativeUiValues.Text(slider.Value) : NativeUiValues.Text(slider.Value) + unit;
                    slider.Header = label.Length > 0 || unit.Length > 0 ? label + "   " + readout : null;
                    slider.IsThumbToolTipEnabled = true; AutomationProperties.SetName(slider, label.Length > 0 ? label : "数值");
                };
                return sliderVisual;
            case "Select":
                var select = new ComboBox(); var selectVisual = new Visual(select); string previousOptions = "";
                select.SelectionChanged += (_, _) =>
                {
                    if (select.SelectedItem is ComboBoxItem item) SetField(selectVisual, key, "value", NativeUiTheme.String(selectVisual.Model, "label"), item.Tag);
                };
                selectVisual.Apply = node =>
                {
                    var options = NativeUiTheme.Prop(node, "options") as IReadOnlyList<object?> ?? Array.Empty<object?>();
                    var choices = options.Select(option => option is IReadOnlyDictionary<string, object?> obj ? (Value: NativeUiValues.Text(obj.GetValueOrDefault("value")), Label: NativeUiValues.Text(obj.GetValueOrDefault("label"))) : (Value: NativeUiValues.Text(option), Label: NativeUiValues.Text(option))).ToArray();
                    var signature = NativeUiValues.ToJson(options)!.ToJsonString();
                    if (signature != previousOptions) { select.Items.Clear(); foreach (var choice in choices) select.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value }); previousOptions = signature; }
                    var value = NativeUiValues.Text(Field(selectVisual, key, "value", NativeUiTheme.String(node, "label")));
                    select.SelectedIndex = Array.FindIndex(choices, choice => choice.Value == value);
                    select.Header = NativeUiTheme.String(node, "label"); select.PlaceholderText = NativeUiTheme.String(node, "placeholder");
                    NativeUiTheme.Select(select); AutomationProperties.SetName(select, NativeUiTheme.String(node, "label"));
                };
                return selectVisual;
            case "Checkbox":
                var checkboxText = new TextBlock(); var checkbox = new CheckBox { Content = checkboxText }; var checkboxVisual = new Visual(checkbox);
                RoutedEventHandler checkboxChanged = (_, _) => SetField(checkboxVisual, key, "checked", NativeUiTheme.String(checkboxVisual.Model, "label"), checkbox.IsChecked == true);
                checkbox.Checked += checkboxChanged; checkbox.Unchecked += checkboxChanged;
                checkboxVisual.Apply = node =>
                {
                    checkboxText.Text = NativeUiTheme.String(node, "label"); checkbox.IsChecked = Field(checkboxVisual, key, "checked", NativeUiTheme.String(node, "label")) is true;
                    NativeUiTheme.Checkbox(checkboxText, checkbox.IsChecked == true, node);
                    AutomationProperties.SetName(checkbox, NativeUiTheme.String(node, "label"));
                };
                return checkboxVisual;
            default: return CreateComponent(type, key, depth);
        }
    }
    public void Dispose()
    {
        _disposed = true; _lastRoot = null;
        _confirmation?.Hide(); _confirmation = null;
        foreach (var visual in _visuals.Values) { visual.Disposed = true; visual.Cleanup?.Invoke(); }
        _visuals.Clear(); _root.Child = null;
    }
}
