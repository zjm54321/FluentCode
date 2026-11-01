using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private TerminalAppearanceEditor _terminalAppearance = null!;

    private UIElement BuildTerminalSection()
    {
        _terminalAppearance = new TerminalAppearanceEditor();
        _terminalAppearance.Changed += async appearance =>
        {
            if (_suppressUiEvents || _unloaded) return;
            try { _settings = await _settingsService.UpdateAsync(current => current with { TerminalAppearance = appearance }, _lifetimeCts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { ShowStatus(InfoBarSeverity.Error, "保存失败", DescribeSafeError(error)); }
        };
        return _terminalAppearance;
    }
}
