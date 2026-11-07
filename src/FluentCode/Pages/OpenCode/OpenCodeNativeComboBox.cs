using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

internal sealed record OpenCodeEffortChoice(string? Id);

// Real ComboBox: its template owns the popup, highlight, check, keyboard and disabled states.
public sealed class OpenCodeNativeComboBox : ComboBox
{
    internal sealed record Choice(object Key, string Name, Func<FrameworkElement> CreateVisual, string Identity,
        Func<FrameworkElement>? CreateCompactVisual = null)
    {
        public FrameworkElement Visual => CreateVisual();
        public override string ToString() => Name; // Native incremental text search uses the display name.
    }
    private sealed class ChoiceItem : ComboBoxItem
    {
        private readonly Choice _choice;
        private FrameworkElement _visual;

        internal ChoiceItem(Choice choice)
        {
            _choice = choice;
            _visual = choice.CreateVisual();
            Content = _visual;
        }

        public override string ToString() => _choice.Name;

        // Stock ComboBox moves the selected UIElement row into its closed ContentPresenter and, when the
        // drop-down reopens, writes *whatever that presenter then holds* straight into this item's template
        // ContentPresenter (bypassing ContentControl.Content). OpenCodeNativeComboBox replaces the closed
        // presenter's content with its own (often icon-only) visual, so the selected row came back as a bare
        // gauge/glyph without its label. Reclaim the row: never let a foreign visual render here.
        private ContentPresenter? _presenter;

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (ReferenceEquals(_presenter, GetTemplateChild("ContentPresenter"))) return;
            _presenter = GetTemplateChild("ContentPresenter") as ContentPresenter;
            _presenter?.RegisterPropertyChangedCallback(ContentPresenter.ContentProperty, (_, _) => Reclaim());
            Reclaim();
        }

        private void Reclaim()
        {
            if (_presenter is null || _presenter.Content is null || ReferenceEquals(_presenter.Content, _visual)) return;
            // The original row may still be parented by the closed selection presenter; never share one element.
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(_visual) is not null) { _visual = _choice.CreateVisual(); Content = _visual; }
            _presenter.Content = _visual;
        }
    }

    private bool _rendering;
    private string? _itemsIdentity;
    private object? _current;
    private bool _labelExpanded = true;
    private ContentPresenter? _selectionPresenter;
    private Choice? _selectionVisualChoice;
    private bool _selectionVisualExpanded;
    internal event Action<object>? ChoiceChanged;

    public OpenCodeNativeComboBox()
    {
        // Implicit styles are exact-type lookups. This derived control otherwise falls
        // back to the platform ComboBox template, not WinUI's rounded overlay template.
        Style = (Style)Application.Current.Resources["DefaultComboBoxStyle"];
        Resources["ComboBoxThemeMinWidth"] = 0d; // Stock template background otherwise imposes 64epx in icon-only mode.
        IsTextSearchEnabled = true;
        PlaceholderText = "未知";
        MinWidth = 0;
        MinHeight = 32;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        // Supply native containers directly: runtime Binding to an internal CLR record's
        // Visual getter is not a reliable WinRT property surface in the packaged app.
        SelectionChanged += (_, _) =>
        {
            UpdateSelectionVisual();
            if (_rendering || !IsEnabled || SelectedItem is not ComboBoxItem { Tag: Choice choice } || Equals(_current, choice.Key)) return;
            _current = choice.Key;
            ChoiceChanged?.Invoke(choice.Key);
        };
        DropDownClosed += (_, _) => UpdateSelectionVisual(force: true);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Keep the platform popup, automation peer and keyboard behavior; only remove
        // the redundant closed-control arrow from these compact agent/effort choices.
        if (GetTemplateChild("DropDownGlyph") is FrameworkElement glyph) glyph.Visibility = Visibility.Collapsed;
        _selectionPresenter = GetTemplateChild("ContentPresenter") as ContentPresenter;
        // Stock WinUI reserves a 38px second column for the arrow. Span it rather
        // than replacing the native template or clipping compact selection icons.
        if (_selectionPresenter is not null) Grid.SetColumnSpan(_selectionPresenter, 2);
        UpdateSelectionVisual(force: true);
    }

    internal void SetLabelExpanded(bool expanded)
    {
        _labelExpanded = expanded;
        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual(bool force = false)
    {
        if (_selectionPresenter is null) return;
        var choice = (SelectedItem as ComboBoxItem)?.Tag as Choice;
        if (!force && ReferenceEquals(choice, _selectionVisualChoice) && _selectionVisualExpanded == _labelExpanded) return;
        _selectionVisualChoice = choice;
        _selectionVisualExpanded = _labelExpanded;
        // Only the closed selection visual changes. Popup containers always keep full labels.
        _selectionPresenter.Content = choice is null ? new TextBlock { Text = PlaceholderText }
            : !_labelExpanded && choice.CreateCompactVisual is { } compact ? compact() : choice.CreateVisual();
    }

    internal void Render(IReadOnlyList<Choice> choices, object? current, bool enabled)
    {
        _rendering = true;
        try
        {
            var identity = string.Join("\n", choices.Select(c => c.Identity));
            if (_itemsIdentity != identity)
            {
                _itemsIdentity = identity;
                Items.Clear();
                foreach (var choice in choices)
                {
                    var item = new ChoiceItem(choice) { Style = (Style)Application.Current.Resources["DefaultComboBoxItemStyle"],
                        Tag = choice };
                    AutomationProperties.SetName(item, choice.Name);
                    Items.Add(item);
                }
            }
            _current = current;
            SelectedItem = Items.OfType<ComboBoxItem>().FirstOrDefault(c => c.Tag is Choice choice && Equals(choice.Key, current));
            UpdateSelectionVisual();
            IsEnabled = enabled;
        }
        finally { _rendering = false; }
    }

    internal static Choice Agent(OpenCodeAgentPickerRow row) => new(row.Id, OpenCodeAgentIcons.DisplayName(row.Name), () =>
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(AgentGlyph(row));
        panel.Children.Add(new TextBlock { Text = OpenCodeAgentIcons.DisplayName(row.Name), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(panel, OpenCodeAgentIcons.DisplayName(row.Name));
        return panel;
    }, row.Id + "\t" + row.Name + "\t" + row.Color, () => AgentGlyph(row));

    private static FontIcon AgentGlyph(OpenCodeAgentPickerRow row)
    {
        var glyph = OpenCodeAgentIconTheme.Create(row.Id, row.Color, 16);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    internal static Choice Model(OpenCodeModelInfo model) => new(new OpenCodeModelChoiceKey(model.ProviderId!, model.Id!, null), model.Name!,
        () => OpenCodeModelLogo.Label(model.Id, model.Name!), model.ProviderId + "\t" + model.Id + "\t" + model.Name);

    internal static IReadOnlyList<Choice> Agents(IEnumerable<OpenCodeAgentPickerRow> rows) => rows.Where(r => r.Selectable).Select(Agent).ToArray();
    internal static IReadOnlyList<Choice> Models(IEnumerable<OpenCodeModelInfo> models) => models.Where(m => m.Enabled != false)
        .OrderBy(m => m.ProviderId, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(Model).ToArray();
    private static OpenCodeComposerEffortGauge EffortGauge(string? id, IReadOnlyList<OpenCodeEffortTick>? ticks)
    {
        var gauge = new OpenCodeComposerEffortGauge { VerticalAlignment = VerticalAlignment.Center };
        gauge.Render(ticks ?? OpenCodeComposerEffort.SemanticTicks(id));
        return gauge;
    }
    internal static Choice Effort(string? id, string? label = null, IReadOnlyList<OpenCodeEffortTick>? ticks = null) => new(new OpenCodeEffortChoice(id), label ?? OpenCodeModelPresentation.VariantLabel(id), () =>
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(EffortGauge(id, ticks));
        panel.Children.Add(new TextBlock { Text = label ?? OpenCodeModelPresentation.VariantLabel(id),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }, "effort:" + (id ?? "<null>") + "\t" + label + "\t" + string.Join("|", ticks?.Select(tick => tick.Id + ":" + tick.Angle + ":" + tick.Selected) ?? Array.Empty<string>()),
        () => EffortGauge(id, ticks));

    internal static IReadOnlyList<Choice> Efforts(OpenCodeModelInfo? model) => model is null ? Array.Empty<Choice>()
        : new string?[] { null }.Concat((model.Variants ?? new()).Select(v => v.Id).Where(id => id is not null)).Distinct()
            .Select(id => Effort(id, ticks: OpenCodeComposerEffort.Ticks(model, id))).ToArray();
}
