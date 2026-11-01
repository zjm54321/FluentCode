using System;
using System.Collections.Generic;
using FluentCode.Models;
using FluentCode.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

/// <summary>终端外观：字体（可输入也可选择，只列等宽字体）· 字号 · 配色 · 光标形状 · 光标闪烁。任一改动即时发布。</summary>
internal sealed class TerminalAppearanceEditor : StackPanel
{
    private readonly SettingsFontBox _font = new("SettingsTerminalFont", "终端字体", monospaceOnly: true);
    private readonly NumberBox _size = new() { Minimum = 6, Maximum = 72, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 140 };
    private readonly ComboBox _scheme = SettingsCards.Sized(new ComboBox());
    private readonly ComboBox _cursor = SettingsCards.Sized(new ComboBox());
    private readonly ToggleSwitch _blink = new() { OnContent = "开", OffContent = "关", MinWidth = 0 };
    private readonly TextBlock _fontNote;
    private readonly TextBlock _preview = new()
    {
        Text = "PS C:\\Users\\you> opencode --version\n~/project $ ls -la | grep \"src\"  # 0O 1lI {}[]",
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };
    private string _fontFamily = new TerminalAppearanceSettings().FontFamily;
    private bool _applying;
    internal event Action<TerminalAppearanceSettings>? Changed;

    internal TerminalAppearanceEditor()
    {
        Spacing = SettingsCards.RowSpacing;
        foreach (var name in TerminalAppearanceSettings.ColorSchemes) _scheme.Items.Add(name == "Default" ? "跟随应用主题" : name);
        foreach (var name in new[] { "竖线", "方块", "下划线" }) _cursor.Items.Add(name);
        AutomationProperties.SetName(_size, "终端字号");
        AutomationProperties.SetName(_scheme, "终端配色");
        AutomationProperties.SetName(_cursor, "光标形状");
        AutomationProperties.SetName(_blink, "光标闪烁");
        AutomationProperties.SetAutomationId(_preview, "SettingsTerminalFontPreview");
        AutomationProperties.SetName(_preview, "终端字体预览");
        var fontCard = SettingsCards.Expander("\uE8D2", "字体", out _fontNote, _font.Control, SettingsCards.SubContent(_preview));
        AutomationProperties.SetAutomationId(fontCard, "SettingsTerminalFontCard");
        Children.Add(fontCard);
        Children.Add(SettingsCards.Card("\uE8E9", "字号", null, _size));
        Children.Add(SettingsCards.Card("\uE790", "配色", null, _scheme));
        Children.Add(SettingsCards.Card("\uE70F", "光标形状", null, _cursor));
        Children.Add(SettingsCards.Card("\uE916", "光标闪烁", null, _blink));
        _font.Submitted += family => { _fontFamily = family ?? new TerminalAppearanceSettings().FontFamily; _font.Show(_fontFamily); UpdatePreview(); Publish(); };
        _font.Notice += note => SettingsCards.SetNote(_fontNote, note);
        _size.ValueChanged += (_, _) => { UpdatePreview(); Publish(); };
        _scheme.SelectionChanged += (_, _) => Publish();
        _cursor.SelectionChanged += (_, _) => Publish();
        _blink.Toggled += (_, _) => Publish();
        Apply(new());
    }

    internal void SetFontCatalog(IReadOnlyList<SystemFontFamily> catalog) => _font.SetCatalog(catalog);

    internal void Apply(TerminalAppearanceSettings settings)
    {
        _applying = true;
        try
        {
            _fontFamily = settings.FontFamily;
            _font.Show(settings.FontFamily);
            _size.Value = settings.FontSize;
            _scheme.SelectedIndex = Array.IndexOf(TerminalAppearanceSettings.ColorSchemes, settings.ColorScheme);
            _cursor.SelectedIndex = Array.IndexOf(TerminalAppearanceSettings.CursorShapes, settings.CursorShape);
            _blink.IsOn = settings.CursorBlink;
            UpdatePreview();
        }
        finally { _applying = false; }
    }

    /// <summary>实时预览：所选族名 + 等宽回退链，字号与终端一致。</summary>
    private void UpdatePreview()
    {
        string family = string.IsNullOrWhiteSpace(_fontFamily) ? new TerminalAppearanceSettings().FontFamily : _fontFamily.Trim();
        _preview.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family + ", " + AppFontDefaults.DefaultFor(AppFontSlot.Monospace));
        if (!double.IsNaN(_size.Value)) _preview.FontSize = Math.Clamp(_size.Value, 6, 72);
    }

    private void Publish()
    {
        if (_applying || _scheme.SelectedIndex < 0 || _cursor.SelectedIndex < 0 || double.IsNaN(_size.Value)) return;
        var settings = new TerminalAppearanceSettings { FontFamily = _fontFamily.Trim(), FontSize = (int)_size.Value,
            ColorScheme = TerminalAppearanceSettings.ColorSchemes[_scheme.SelectedIndex],
            CursorShape = TerminalAppearanceSettings.CursorShapes[_cursor.SelectedIndex], CursorBlink = _blink.IsOn };
        if (settings.ValidationError() is null) Changed?.Invoke(settings);
    }
}
