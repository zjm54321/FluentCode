using System;
using AIFrontend.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

internal sealed class TerminalAppearanceEditor : StackPanel
{
    private readonly TextBox _font = new() { Header = "字体", MinWidth = 280 };
    private readonly NumberBox _size = new() { Header = "字号", Minimum = 6, Maximum = 72, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly ComboBox _scheme = new() { Header = "配色", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _cursor = new() { Header = "光标", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ToggleSwitch _blink = new() { Header = "光标闪烁", OnContent = "开", OffContent = "关" };
    private bool _applying;
    internal event Action<TerminalAppearanceSettings>? Changed;

    internal TerminalAppearanceEditor()
    {
        Spacing = 12;
        foreach (var name in TerminalAppearanceSettings.ColorSchemes) _scheme.Items.Add(name == "Default" ? "跟随应用主题" : name);
        foreach (var name in new[] { "竖线", "方块", "下划线" }) _cursor.Items.Add(name);
        Children.Add(_font); Children.Add(_size); Children.Add(_scheme); Children.Add(_cursor); Children.Add(_blink);
        _font.LostFocus += (_, _) => Publish();
        _size.ValueChanged += (_, _) => Publish();
        _scheme.SelectionChanged += (_, _) => Publish();
        _cursor.SelectionChanged += (_, _) => Publish();
        _blink.Toggled += (_, _) => Publish();
        Apply(new());
    }

    internal void Apply(TerminalAppearanceSettings settings)
    {
        _applying = true;
        try
        {
            _font.Text = settings.FontFamily; _size.Value = settings.FontSize;
            _scheme.SelectedIndex = Array.IndexOf(TerminalAppearanceSettings.ColorSchemes, settings.ColorScheme);
            _cursor.SelectedIndex = Array.IndexOf(TerminalAppearanceSettings.CursorShapes, settings.CursorShape);
            _blink.IsOn = settings.CursorBlink;
        }
        finally { _applying = false; }
    }

    private void Publish()
    {
        if (_applying || _scheme.SelectedIndex < 0 || _cursor.SelectedIndex < 0 || double.IsNaN(_size.Value)) return;
        var settings = new TerminalAppearanceSettings { FontFamily = _font.Text.Trim(), FontSize = (int)_size.Value,
            ColorScheme = TerminalAppearanceSettings.ColorSchemes[_scheme.SelectedIndex],
            CursorShape = TerminalAppearanceSettings.CursorShapes[_cursor.SelectedIndex], CursorBlink = _blink.IsOn };
        if (settings.ValidationError() is null) Changed?.Invoke(settings);
    }
}
