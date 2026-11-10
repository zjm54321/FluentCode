// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Motion;
using FluentCode.Services.NativeUi;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.Settings;

public sealed partial class SettingsView
{
    private bool _mapDirty, _mapSaving;
    private readonly ComboBox _mapProvider = SettingsCards.Sized(new ComboBox());
    private readonly PasswordBox _mapKey = SettingsCards.Sized(new PasswordBox { PlaceholderText = "留空保留已保存密钥" });
    private readonly CheckBox _mapClearKey = new() { Content = "删除已保存的密钥" };
    private readonly TextBlock _mapKeyNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _mapTemplate = new() { PlaceholderText = "https://tiles.example.org/{z}/{x}/{y}.png", TextWrapping = TextWrapping.Wrap, MinWidth = 240 };
    private readonly TextBox _mapAttribution = new() { PlaceholderText = "地图数据与瓦片提供方署名", TextWrapping = TextWrapping.Wrap, MinWidth = 240 };
    private readonly StackPanel _mapTianDiTu = new() { Spacing = SettingsCards.RowSpacing };
    private readonly StackPanel _mapBaidu = new() { Spacing = SettingsCards.RowSpacing, Visibility = Visibility.Collapsed };
    private readonly StackPanel _mapCustom = new() { Spacing = SettingsCards.RowSpacing };
    private readonly Button _mapSave = new() { Content = "保存" };

    private UIElement BuildNativeUiMapSection()
    {
        foreach (var provider in Enum.GetValues<NativeUiMapProvider>())
            _mapProvider.Items.Add(new ComboBoxItem { Content = NativeUiMapProviders.Name(provider), Tag = provider });
        AutomationProperties.SetName(_mapProvider, "原生卡片地图提供方"); AutomationProperties.SetAutomationId(_mapProvider, "SettingsNativeUiMapProvider");
        AutomationProperties.SetName(_mapKey, "天地图用户密钥"); AutomationProperties.SetName(_mapTemplate, "自定义 XYZ HTTPS 瓦片模板"); AutomationProperties.SetName(_mapAttribution, "自定义地图署名");
        _mapTianDiTu.Children.Add(SettingsCards.Card("\uE72E", "天地图用户密钥", "免费用户密钥在 Windows DPAPI 中加密保存；界面不会回显已有密钥", _mapKey));
        _mapTianDiTu.Children.Add(SettingsCards.Indented(new StackPanel { Spacing = 8, Children = { _mapKeyNote, _mapClearKey } }));
        _mapBaidu.Children.Add(SettingsCards.Card("\uE946", "百度地图", "百度地图服务条款适用。", new HyperlinkButton { Content = "服务条款", NavigateUri = new Uri("https://lbsyun.baidu.com/docs/pcsa?title=law/open/law") }));
        _mapCustom.Children.Add(SettingsCards.Expander("\uE774", "XYZ 瓦片地址", "必须为 HTTPS，并包含 {z}、{x}、{y}", null, SettingsCards.SubContent(_mapTemplate)));
        _mapCustom.Children.Add(SettingsCards.Expander("\uE946", "提供方署名", "按提供方要求填写；会显示在地图上", null, SettingsCards.SubContent(_mapAttribution)));
        var panel = new StackPanel { Spacing = SettingsCards.GroupSpacing };
        panel.Children.Add(SettingsCards.Group("原生交互卡片",
            SettingsCards.Card("\uE707", "地图提供方", "卡片先显示地点列表；点击“加载地图”后才会请求瓦片", _mapProvider), _mapTianDiTu, _mapBaidu, _mapCustom));
        panel.Children.Add(SettingsCards.Group(null, SettingsCards.Card("\uE946", "地图服务条款", "请遵守所选提供方的服务条款及授权范围。", null)));
        panel.Children.Add(SettingsCards.Group(null, SettingsCards.Card("\uE74E", "保存地图设置", "下次点击加载或重新加载卡片地图时生效；媒体和图片也只在点击后加载", _mapSave)));
        _mapProvider.SelectionChanged += (_, _) => MapDraftChanged(); _mapKey.PasswordChanged += (_, _) => MapDraftChanged();
        _mapClearKey.Checked += (_, _) => MapDraftChanged(); _mapClearKey.Unchecked += (_, _) => MapDraftChanged();
        _mapTemplate.TextChanged += (_, _) => MapDraftChanged(); _mapAttribution.TextChanged += (_, _) => MapDraftChanged();
        _mapSave.Click += async (_, _) => await SaveNativeUiMapAsync();
        ApplyNativeUiMapToUi(); return panel;
    }

    private NativeUiMapProvider SelectedMapProvider => (_mapProvider.SelectedItem as ComboBoxItem)?.Tag is NativeUiMapProvider provider ? provider : NativeUiMapProvider.OpenStreetMap;
    private void MapDraftChanged()
    {
        _mapTianDiTu.Visibility = SelectedMapProvider == NativeUiMapProvider.TianDiTu ? Visibility.Visible : Visibility.Collapsed;
        _mapBaidu.Visibility = SelectedMapProvider == NativeUiMapProvider.Baidu ? Visibility.Visible : Visibility.Collapsed;
        _mapCustom.Visibility = SelectedMapProvider == NativeUiMapProvider.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (!_suppressUiEvents) _mapDirty = true;
        _mapSave.IsEnabled = _mapDirty && !_mapSaving;
    }
    private void ApplyNativeUiMapToUi()
    {
        if (_mapDirty) return;
        var suppress = _suppressUiEvents; _suppressUiEvents = true;
        try
        {
            var map = _settings.NativeUiMap;
            _mapProvider.SelectedIndex = (int)map.Provider; _mapTemplate.Text = map.CustomTileUrlTemplate ?? ""; _mapAttribution.Text = map.CustomAttribution ?? "";
            _mapKey.Password = ""; _mapClearKey.IsChecked = false;
            _mapKeyNote.Text = map.TianDiTuCredentialId is null ? "尚未保存天地图用户密钥。" : "已保存加密密钥；留空保留，输入新值替换。";
            MapDraftChanged(); _mapSave.IsEnabled = false;
        }
        finally { _suppressUiEvents = suppress; }
    }
    private async Task<bool> SaveNativeUiMapAsync()
    {
        if (_mapSaving) return false;
        var previous = _settings.NativeUiMap; var id = _mapClearKey.IsChecked is true ? null : previous.TianDiTuCredentialId;
        var draft = previous with { Provider = SelectedMapProvider, CustomTileUrlTemplate = string.IsNullOrWhiteSpace(_mapTemplate.Text) ? null : _mapTemplate.Text.Trim(), CustomAttribution = string.IsNullOrWhiteSpace(_mapAttribution.Text) ? null : _mapAttribution.Text.Trim() };
        if (draft.ValidationError() is { } reason) { ShowStatus(InfoBarSeverity.Error, "地图设置未保存", reason); return false; }
        string? created = null; _mapSaving = true; _mapSave.IsEnabled = false; ActionFeedback.Busy(_mapSave);
        try
        {
            if (_mapKey.Password.Length > 0)
            {
                created = "native-map-tianditu-" + Guid.NewGuid().ToString("N");
                await _secretStore.SetAsync(created, _mapKey.Password, _lifetimeCts.Token); id = created;
            }
            draft = draft with { TianDiTuCredentialId = id };
            if (!await SaveAsync(settings => settings with { NativeUiMap = draft }))
            {
                // Changed notifications can fail after a committed save; never delete its live credential.
                if (created is not null && _settingsService.Current?.NativeUiMap.TianDiTuCredentialId != created) await _secretStore.RemoveAsync(created);
                ActionFeedback.Failure(_mapSave, "地图设置未保存"); return false;
            }
            _mapDirty = false; ApplyNativeUiMapToUi();
            if (previous.TianDiTuCredentialId is { } old && old != id) await _secretStore.RemoveAsync(old);
            ActionFeedback.Success(_mapSave, "已保存地图设置"); return true;
        }
        catch (Exception error) { ShowStatus(InfoBarSeverity.Error, "地图设置保存失败", DescribeSafeError(error)); ActionFeedback.Failure(_mapSave, "保存失败"); return false; }
        finally { _mapSaving = false; _mapSave.IsEnabled = _mapDirty; }
    }
}
