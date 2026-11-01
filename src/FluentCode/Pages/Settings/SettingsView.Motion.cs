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
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        _reduceMotionToggle = new ToggleSwitch { Header = "减少动画" };
        AutomationProperties.SetAutomationId(_reduceMotionToggle, "SettingsReduceMotion");
        AutomationProperties.SetName(_reduceMotionToggle, "减少动画");
        _reduceMotionToggle.Toggled += OnReduceMotionToggled;
        _reduceMotionNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        panel.Children.Add(_reduceMotionToggle);
        panel.Children.Add(_reduceMotionNote);
        FluentMotion.EnabledChanged += OnMotionEnabledChanged;
        Unloaded += (_, _) => FluentMotion.EnabledChanged -= OnMotionEnabledChanged;
        Loaded += (_, _) =>
        {
            FluentMotion.EnabledChanged -= OnMotionEnabledChanged;
            FluentMotion.EnabledChanged += OnMotionEnabledChanged;
            ApplyMotionToUi();
        };
        return panel;
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
        _reduceMotionNote.Text = systemOff
            ? "Windows 已关闭“动画效果”，应用内动画跟随系统保持关闭。"
            : "开启后，页面切换、弹出卡片和消息出现时不再播放动画。";
    }

    private async void OnReduceMotionToggled(object sender, RoutedEventArgs args)
    {
        if (_suppressUiEvents || !FluentMotion.SystemEnabled || _reduceMotionToggle.IsOn == _settings.ReduceMotion) return;
        bool reduce = _reduceMotionToggle.IsOn;
        _reduceMotionToggle.IsEnabled = false;
        try
        {
            await SaveAsync(settings => settings with { ReduceMotion = reduce }, "已保存设置。");
        }
        finally
        {
            _suppressUiEvents = true;
            try { ApplyMotionToUi(); }
            finally { _suppressUiEvents = false; }
        }
    }
}
