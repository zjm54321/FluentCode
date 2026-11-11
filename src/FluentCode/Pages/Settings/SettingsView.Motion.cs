using System;
using FluentCode.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private ToggleSwitch _reduceMotionToggle = null!;
    private TextBlock _reduceMotionNote = null!;

    /// <summary>“减少动画”：只能在系统动画开启时进一步关闭；系统已关闭时开关锁定为开并说明原因。</summary>
    private UIElement BuildMotionSection()
    {
        _reduceMotionToggle = new ToggleSwitch { OnContent = "开", OffContent = "关" };
        AutomationProperties.SetAutomationId(_reduceMotionToggle, "SettingsReduceMotion");
        AutomationProperties.SetName(_reduceMotionToggle, "减少动画");
        _reduceMotionToggle.Toggled += OnReduceMotionToggled;
        FluentMotion.EnabledChanged += OnMotionEnabledChanged;
        Unloaded += (_, _) => FluentMotion.EnabledChanged -= OnMotionEnabledChanged;
        Loaded += (_, _) =>
        {
            FluentMotion.EnabledChanged -= OnMotionEnabledChanged;
            FluentMotion.EnabledChanged += OnMotionEnabledChanged;
            ApplyMotionToUi();
        };
        return SettingsCards.Group("动画", SettingsCards.Card("\uE916", "减少动画", out _reduceMotionNote, _reduceMotionToggle));
    }

    private void OnMotionEnabledChanged(object? sender, EventArgs args)
    {
        bool suppress = _suppressUiEvents;
        _suppressUiEvents = true;
        try { ApplyMotionToUi(); }
        finally { _suppressUiEvents = suppress; }
    }

    private void ApplyMotionToUi()
    {
        bool systemOff = !FluentMotion.SystemEnabled;
        _reduceMotionToggle.IsOn = systemOff || _settings.ReduceMotion;
        _reduceMotionToggle.IsEnabled = !systemOff;
        // 只有不明显的情形才说明：系统已关闭动画时开关被锁定。
        SettingsCards.SetNote(_reduceMotionNote, systemOff ? "Windows 已关闭动画效果" : null);
    }

    private async void OnReduceMotionToggled(object sender, RoutedEventArgs args)
    {
        if (_suppressUiEvents || !FluentMotion.SystemEnabled || _reduceMotionToggle.IsOn == _settings.ReduceMotion) return;
        bool reduce = _reduceMotionToggle.IsOn;
        _reduceMotionToggle.IsEnabled = false;
        try
        {
            await SaveAsync(settings => settings with { ReduceMotion = reduce });
        }
        finally
        {
            _suppressUiEvents = true;
            try { ApplyMotionToUi(); }
            finally { _suppressUiEvents = false; }
        }
    }
}
