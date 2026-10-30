using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.Settings;

public sealed partial class SettingsView
{
    private ToggleSwitch _runInBackgroundToggle = null!;

    private UIElement BuildGeneralSection()
    {
        _runInBackgroundToggle = new ToggleSwitch { Header = "关闭窗口后在后台运行" };
        AutomationProperties.SetAutomationId(_runInBackgroundToggle, "SettingsRunInBackground");
        AutomationProperties.SetName(_runInBackgroundToggle, "关闭窗口后在后台运行");
        _runInBackgroundToggle.Toggled += OnRunInBackgroundToggled;
        return _runInBackgroundToggle;
    }

    private async void OnRunInBackgroundToggled(object sender, RoutedEventArgs args)
    {
        if (_suppressUiEvents || _runInBackgroundToggle.IsOn == _settings.RunInBackground) return;
        bool enabled = _runInBackgroundToggle.IsOn;
        _runInBackgroundToggle.IsEnabled = false;
        try
        {
            await SaveAsync(settings => settings with { RunInBackground = enabled }, "已保存设置。");
        }
        finally
        {
            _runInBackgroundToggle.IsOn = _settings.RunInBackground;
            _runInBackgroundToggle.IsEnabled = true;
        }
    }
}
