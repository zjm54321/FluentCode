using System;
using FluentCode.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private ToggleSwitch _linkOnlineToggle = null!;
    private ToggleSwitch _linkMonochromeToggle = null!;

    /// <summary>“链接与图片”：联网补全默认关闭（只影响普通网站的图标）；单色图标。</summary>
    private UIElement BuildLinkChipsSection()
    {
        _linkOnlineToggle = new ToggleSwitch { OnContent = "开", OffContent = "关", MinWidth = 0 };
        AutomationProperties.SetAutomationId(_linkOnlineToggle, "SettingsLinkChipsOnline");
        AutomationProperties.SetName(_linkOnlineToggle, "联网补全");
        _linkOnlineToggle.Toggled += (_, _) => SaveLinkChips(_linkOnlineToggle, s => s with { OnlineMetadata = _linkOnlineToggle.IsOn });
        _linkMonochromeToggle = new ToggleSwitch { OnContent = "开", OffContent = "关", MinWidth = 0 };
        AutomationProperties.SetAutomationId(_linkMonochromeToggle, "SettingsLinkChipsMonochrome");
        AutomationProperties.SetName(_linkMonochromeToggle, "单色图标");
        _linkMonochromeToggle.Toggled += (_, _) => SaveLinkChips(_linkMonochromeToggle, s => s with { MonochromeIcons = _linkMonochromeToggle.IsOn });
        return SettingsCards.Group("链接与图片",
            SettingsCards.Card("\uE774", "联网补全",
                "开启后，普通网站的链接显示该网站自己的图标（按站点缓存）；本机和内网地址从不联网。关闭时显示地球图标。", _linkOnlineToggle),
            SettingsCards.Card("\uE790", "单色图标", "链接图标不使用品牌色；高对比度下始终为单色。", _linkMonochromeToggle));
    }

    private void ApplyLinkChipsToUi()
    {
        var chips = _settings.LinkChips ?? new LinkChipSettings();
        _linkOnlineToggle.IsOn = chips.OnlineMetadata;
        _linkMonochromeToggle.IsOn = chips.MonochromeIcons;
    }

    private async void SaveLinkChips(ToggleSwitch toggle, Func<LinkChipSettings, LinkChipSettings> change)
    {
        if (_suppressUiEvents) return;
        var next = change(_settings.LinkChips ?? new LinkChipSettings());
        if (next == (_settings.LinkChips ?? new LinkChipSettings())) return;
        toggle.IsEnabled = false;
        try { await SaveAsync(settings => settings with { LinkChips = next }); }
        finally
        {
            _suppressUiEvents = true;
            try { ApplyLinkChipsToUi(); }
            finally { _suppressUiEvents = false; }
            toggle.IsEnabled = true;
        }
    }
}
