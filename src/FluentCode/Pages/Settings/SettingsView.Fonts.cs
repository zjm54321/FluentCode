using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.Settings;

/// <summary>
/// 外观 → 文字（F11）：文字大小 + 正文 / 衬线 / 等宽三个字体槽位。字体框可输入也可选择（<see cref="SettingsFontBox"/>），
/// 选择后立即保存到 <c>settings.json</c>；应用由 <see cref="AppFonts"/> 订阅 <c>SettingsService.Changed</c> 自动完成。
/// 每个字体卡片展开后显示实时预览（直接使用该槽位的回退链）。
/// </summary>
public sealed partial class SettingsView
{
    private static readonly AppFontSlot[] FontSlots = { AppFontSlot.Body, AppFontSlot.Serif, AppFontSlot.Monospace };

    private readonly Dictionary<AppFontSlot, FontSlotUi> _fontSlots = new();
    private Button _fontResetAllButton = null!;
    private ComboBox _textSizeCombo = null!;
    private IReadOnlyList<SystemFontFamily>? _fontCatalog;
    private bool _fontCatalogLoading;
    private bool _fontSaveInProgress;

    // 进程内共享一次枚举（每次打开设置都会新建 SettingsView）；失败结果不缓存，下次进入重试。
    private static Task<IReadOnlyList<SystemFontFamily>>? s_fontCatalogTask;

    /// <summary>单个字体槽位的界面元素。</summary>
    private sealed class FontSlotUi
    {
        public required AppFontSlot Slot { get; init; }
        public required SettingsFontBox Box { get; init; }
        public required TextBlock Preview { get; init; }
        public required TextBlock Notice { get; init; }
    }

    private UIElement BuildFontsSection()
    {
        _textSizeCombo = SettingsCards.Sized(new ComboBox());
        AutomationProperties.SetAutomationId(_textSizeCombo, "SettingsTextSizeCombo");
        AutomationProperties.SetName(_textSizeCombo, "文字大小");
        foreach (double size in new double[] { 14, 15, 16, 18, 20, 22, 24 })
            _textSizeCombo.Items.Add(new ComboBoxItem { Content = TextSizeLabel(size), Tag = size });
        _textSizeCombo.SelectionChanged += async (_, _) =>
        {
            if (!_suppressUiEvents && _textSizeCombo.SelectedItem is ComboBoxItem { Tag: double value })
                await SaveFontsAsync(AppFontDefaults.WithTextSize(_settings.Fonts, value));
        };

        _fontResetAllButton = new Button { Content = "恢复默认" };
        AutomationProperties.SetAutomationId(_fontResetAllButton, "SettingsFontResetAll");
        AutomationProperties.SetName(_fontResetAllButton, "字体与字号恢复默认");
        _fontResetAllButton.Click += OnFontResetAllClick;

        var group = SettingsCards.Group("文字",
            SettingsCards.Card("\uE8E9", "文字大小", "图标和点击区域不缩放", _textSizeCombo),
            BuildFontSlotCard(AppFontSlot.Body, "\uE8D2", "正文字体",
                "敏捷的棕色狐狸跳过了懒狗，界面文字清晰易读。\nThe quick brown fox jumps over the lazy dog. 0123456789"),
            BuildFontSlotCard(AppFontSlot.Serif, "\uE8D2", "衬线字体",
                "落霞与孤鹜齐飞，秋水共长天一色。\nTypography gives language a durable visual form. 1234567890"),
            BuildFontSlotCard(AppFontSlot.Monospace, "\uE943", "等宽字体",
                "fn main() {\n    let greeting = \"你好, world\"; // 0O 1lI |{}[]\n}"),
            SettingsCards.Card("\uE777", "恢复默认字体与字号", null, _fontResetAllButton));
        AutomationProperties.SetAutomationId(group, "SettingsFontsSection");

        _ = LoadFontCatalogAsync();
        return group;
    }

    private static string TextSizeLabel(double size) => size == AppFontDefaults.DefaultTextSize
        ? $"{size:0.##}（默认）" : $"{size:0.##} · {size / AppFontDefaults.DefaultTextSize:P0}";

    private UIElement BuildFontSlotCard(AppFontSlot slot, string glyph, string title, string previewText)
    {
        string id = slot.ToString();
        var box = new SettingsFontBox($"SettingsFont{id}Combo", title, slot == AppFontSlot.Monospace);
        box.Submitted += family => _ = SaveFontsAsync(AppFontDefaults.With(_settings.Fonts, slot, family));

        // 预览：直接使用该槽位的完整回退链（不经 ThemeResource），选择后即刻可见。
        var preview = new TextBlock
        {
            Text = previewText,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = slot == AppFontSlot.Monospace ? 13 : 15,
            LineHeight = slot == AppFontSlot.Monospace ? 20 : 24,
            FontFamily = new FontFamily(AppFontDefaults.DefaultFor(slot)),
        };
        AutomationProperties.SetAutomationId(preview, $"SettingsFont{id}Preview");
        AutomationProperties.SetName(preview, $"{title}预览");

        // 说明只在需要时出现（如本机未找到所输入的字体），放在标题行，折叠时也可见。
        var expander = SettingsCards.Expander(glyph, title, out TextBlock notice, box.Control, SettingsCards.SubContent(preview));
        AutomationProperties.SetAutomationId(notice, $"SettingsFont{id}Resolved");
        box.Notice += text => SettingsCards.SetNote(notice, text);
        AutomationProperties.SetAutomationId(expander, $"SettingsFont{id}Card");
        _fontSlots[slot] = new FontSlotUi { Slot = slot, Box = box, Preview = preview, Notice = notice };
        return expander;
    }

    /// <summary>后台枚举已安装字体（DirectWrite），完成后回 UI 线程填充字体框（保留当前值）。</summary>
    private async Task LoadFontCatalogAsync()
    {
        if (_fontCatalog is not null || _fontCatalogLoading)
        {
            return;
        }

        _fontCatalogLoading = true;
        IReadOnlyList<SystemFontFamily> catalog;
        try
        {
            Task<IReadOnlyList<SystemFontFamily>> task = s_fontCatalogTask ??=
                Task.Run(() => SystemFontCatalog.GetInstalledFamilies());
            catalog = await task;
        }
        catch (Exception)
        {
            catalog = Array.Empty<SystemFontFamily>();
        }

        if (catalog.Count == 0)
        {
            s_fontCatalogTask = null;
        }

        RunOnUi(() =>
        {
            _fontCatalogLoading = false;
            _fontCatalog = catalog;
            bool previous = _suppressUiEvents;
            _suppressUiEvents = true;
            try
            {
                foreach (FontSlotUi ui in _fontSlots.Values) ui.Box.SetCatalog(catalog);
                _terminalAppearance?.SetFontCatalog(catalog);
                ApplyFontsToUi();
            }
            finally
            {
                _suppressUiEvents = previous;
            }
        });
    }

    /// <summary>把已保存的字体选择应用到界面（不保存；调用方负责 _suppressUiEvents）。</summary>
    private void ApplyFontsToUi()
    {
        if (_fontSlots.Count == 0)
        {
            return;
        }

        AppFontSettings? fonts = AppFontDefaults.Normalize(_settings.Fonts);
        double size = AppFontDefaults.ResolveTextSize(fonts);
        ComboBoxItem? selectedSize = _textSizeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is double value && value == size);
        if (selectedSize is null)
        {
            selectedSize = new ComboBoxItem { Content = TextSizeLabel(size), Tag = size };
            _textSizeCombo.Items.Add(selectedSize);
        }
        _textSizeCombo.SelectedItem = selectedSize;
        foreach (FontSlotUi ui in _fontSlots.Values)
        {
            ui.Box.Show(fonts?.Get(ui.Slot));
            ui.Preview.FontFamily = new FontFamily(AppFontDefaults.Resolve(fonts, ui.Slot));
            // Authored baselines: AppFonts' deferred metric pass applies the saved scale once.
            ui.Preview.FontSize = ui.Slot == AppFontSlot.Monospace ? 13 : 15;
            ui.Preview.LineHeight = ui.Slot == AppFontSlot.Monospace ? 20 : 24;
        }

        _fontResetAllButton.IsEnabled = fonts is not null;
    }

    private async void OnFontResetAllClick(object sender, RoutedEventArgs e)
    {
        if (await SaveFontsAsync(null)) FluentCode.Motion.ActionFeedback.Success(_fontResetAllButton, "已恢复默认字体与字号");
    }

    /// <summary>即时保存字体选择；成功后由 AppFonts 经 SettingsService.Changed 应用，失败时界面回滚到已保存值。</summary>
    private async Task<bool> SaveFontsAsync(AppFontSettings? next)
    {
        if (_fontSaveInProgress) return false;
        AppFontSettings? normalized = AppFontDefaults.Normalize(next);
        if (Equals(normalized, AppFontDefaults.Normalize(_settings.Fonts)))
        {
            ApplySettingsToUi();
            return false;
        }

        _fontSaveInProgress = true;
        _textSizeCombo.IsEnabled = false;
        _fontResetAllButton.IsEnabled = false;
        foreach (FontSlotUi ui in _fontSlots.Values) ui.Box.IsEnabled = false;
        try
        {
            return await SaveAsync(settings => settings with { Fonts = normalized });
        }
        finally
        {
            _fontSaveInProgress = false;
            _textSizeCombo.IsEnabled = true;
            foreach (FontSlotUi ui in _fontSlots.Values) ui.Box.IsEnabled = true;
            ApplySettingsToUi();
        }
    }
}
