using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private TerminalAppearanceEditor? _terminalAppearance;

    private UIElement BuildTerminalSection()
    {
        var editor = new TerminalAppearanceEditor();
        _terminalAppearance = editor;
        if (_fontCatalog is { } catalog) editor.SetFontCatalog(catalog);
        editor.Changed += async appearance =>
        {
            if (_suppressUiEvents || _unloaded) return;
            try { _settings = await _settingsService.UpdateAsync(current => current with { TerminalAppearance = appearance }, _lifetimeCts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowStatus(InfoBarSeverity.Error, "保存失败", DescribeSafeError(error)); }
        };
        return SettingsCards.Group(null, editor);
    }
}
