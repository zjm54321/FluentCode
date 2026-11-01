using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

/// <summary>外观：主题（应用主题 / 代码配色）· 文字（字号 / 三种字体）· 动画。全部即时保存。</summary>
public sealed partial class SettingsView
{
    private UIElement BuildAppearanceSection()
    {
        _themeCombo = SettingsCards.Sized(new ComboBox());
        _themeCombo.Items.Add("跟随系统");
        _themeCombo.Items.Add("浅色");
        _themeCombo.Items.Add("深色");
        AutomationProperties.SetAutomationId(_themeCombo, "SettingsThemeCombo");
        AutomationProperties.SetName(_themeCombo, "应用主题");
        _themeCombo.SelectionChanged += OnThemeSelectionChanged;

        _codeThemeCombo = SettingsCards.Sized(new ComboBox());
        _codeThemeCombo.Items.Add("跟随应用");
        _codeThemeCombo.Items.Add("One Dark Pro");
        AutomationProperties.SetAutomationId(_codeThemeCombo, "SettingsCodeThemeCombo");
        AutomationProperties.SetName(_codeThemeCombo, "代码配色");
        _codeThemeCombo.SelectionChanged += OnCodeThemeSelectionChanged;

        var panel = new StackPanel { Spacing = SettingsCards.GroupSpacing };
        panel.Children.Add(SettingsCards.Group("主题",
            SettingsCards.Card("\uE790", "应用主题", null, _themeCombo),
            SettingsCards.Card("\uE943", "代码配色", "只影响代码块与源码预览", _codeThemeCombo)));
        panel.Children.Add(BuildFontsSection());
        panel.Children.Add(BuildMotionSection());
        return panel;
    }
}
