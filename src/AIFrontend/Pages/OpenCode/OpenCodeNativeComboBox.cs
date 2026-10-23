using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace AIFrontend.Pages.OpenCode;

internal sealed record OpenCodeEffortChoice(string? Id);

// Real ComboBox: its template owns the popup, highlight, check, keyboard and disabled states.
public sealed class OpenCodeNativeComboBox : ComboBox
{
    internal sealed record Choice(object Key, string Name, Func<FrameworkElement> CreateVisual, string Identity)
    {
        public FrameworkElement Visual => CreateVisual();
        public override string ToString() => Name; // Native incremental text search uses the display name.
    }

    private bool _rendering;
    private string? _itemsIdentity;
    private object? _current;
    internal event Action<object>? ChoiceChanged;

    public OpenCodeNativeComboBox()
    {
        IsTextSearchEnabled = true;
        PlaceholderText = "未知";
        MinWidth = 0;
        MinHeight = 32;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ItemTemplate = (DataTemplate)XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><ContentPresenter Content=\"{Binding Visual}\"/></DataTemplate>");
        SelectionChanged += (_, _) =>
        {
            if (_rendering || !IsEnabled || SelectedItem is not Choice choice || Equals(_current, choice.Key)) return;
            _current = choice.Key;
            ChoiceChanged?.Invoke(choice.Key);
        };
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
                foreach (var choice in choices) Items.Add(choice);
            }
            _current = current;
            SelectedItem = Items.OfType<Choice>().FirstOrDefault(c => Equals(c.Key, current));
            IsEnabled = enabled;
        }
        finally { _rendering = false; }
    }

    internal static Choice Agent(OpenCodeAgentPickerRow row) => new(row.Id, OpenCodeAgentIcons.DisplayName(row.Name), () =>
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var glyph = new FontIcon { Glyph = OpenCodeAgentIcons.Glyph(row.Id), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        glyph.Style = (Style)XamlReader.Load("<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"FontIcon\"><Setter Property=\"Foreground\" Value=\"{ThemeResource AccentTextFillColorPrimaryBrush}\"/></Style>");
        if (OpenCodeAgentPresentation.Color(row.Color) is { } color)
            glyph.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = OpenCodeAgentIcons.DisplayName(row.Name), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(panel, OpenCodeAgentIcons.DisplayName(row.Name));
        return panel;
    }, row.Id + "\t" + row.Name + "\t" + row.Color);

    internal static Choice Model(OpenCodeModelInfo model) => new(new OpenCodeModelChoiceKey(model.ProviderId!, model.Id!, null), model.Name!,
        () => OpenCodeModelLogo.Label(model.Id, model.Name!), model.ProviderId + "\t" + model.Id + "\t" + model.Name);

    internal static IReadOnlyList<Choice> Agents(IEnumerable<OpenCodeAgentPickerRow> rows) => rows.Where(r => r.Selectable).Select(Agent).ToArray();
    internal static IReadOnlyList<Choice> Models(IEnumerable<OpenCodeModelInfo> models) => models.Where(m => m.Enabled != false)
        .OrderBy(m => m.ProviderId, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Select(Model).ToArray();
    internal static IReadOnlyList<Choice> Efforts(OpenCodeModelInfo? model) => model is null ? Array.Empty<Choice>()
        : new string?[] { null }.Concat((model.Variants ?? new()).Select(v => v.Id).Where(id => id is not null)).Distinct()
            .Select(id => new Choice(new OpenCodeEffortChoice(id), OpenCodeModelPresentation.VariantLabel(id),
                () => new TextBlock { Text = OpenCodeModelPresentation.VariantLabel(id) }, "effort:" + (id ?? "<null>"))).ToArray();
}
