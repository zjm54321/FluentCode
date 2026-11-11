using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private ToggleSwitch _runInBackgroundToggle = null!;

    private UIElement BuildGeneralSection()
    {
        _runInBackgroundToggle = new ToggleSwitch { OnContent = "开", OffContent = "关" };
        AutomationProperties.SetAutomationId(_runInBackgroundToggle, "SettingsRunInBackground");
        AutomationProperties.SetName(_runInBackgroundToggle, "关闭窗口后在后台运行");
        _runInBackgroundToggle.Toggled += OnRunInBackgroundToggled;
        var panel = new StackPanel { Spacing = 24 };
        panel.Children.Add(SettingsCards.Group(null,
            SettingsCards.Card("\uE7E8", "关闭窗口后在后台运行", "可从通知区域图标重新打开", _runInBackgroundToggle)));
        panel.Children.Add(BuildLinkChipsSection());
        return panel;
    }

    private async void OnRunInBackgroundToggled(object sender, RoutedEventArgs args)
    {
        if (_suppressUiEvents || _runInBackgroundToggle.IsOn == _settings.RunInBackground) return;
        bool enabled = _runInBackgroundToggle.IsOn;
        _runInBackgroundToggle.IsEnabled = false;
        try
        {
            await SaveAsync(settings => settings with { RunInBackground = enabled });
        }
        finally
        {
            _runInBackgroundToggle.IsOn = _settings.RunInBackground;
            _runInBackgroundToggle.IsEnabled = true;
        }
    }
}
