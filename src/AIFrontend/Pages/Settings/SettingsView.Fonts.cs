using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIFrontend.Pages.Settings;

/// <summary>
/// 外观 → 字体（F11）：正文 / 衬线 / 等宽三个槽位，各自可选已安装字体或“跟随系统（默认）”，
/// 选择后立即保存到 <c>settings.json</c>（与主题相同的即时保存模型）；应用由
/// <see cref="AppFonts"/> 订阅 <c>SettingsService.Changed</c> 自动完成，本分文件不直接改应用资源。
/// </summary>
public sealed partial class SettingsView
{
    private const string FontDefaultItemText = "跟随系统（默认）";

    private static readonly AppFontSlot[] FontSlots = { AppFontSlot.Body, AppFontSlot.Serif, AppFontSlot.Monospace };

    private readonly Dictionary<AppFontSlot, FontSlotUi> _fontSlots = new();
    private Button _fontResetAllButton = null!;
    private ComboBox _textSizeCombo = null!;
    private Button _textSizeReset = null!;
    private TextBlock _textSizeStatus = null!;
    private TextBlock _fontCatalogStatus = null!;
    private IReadOnlyList<SystemFontFamily>? _fontCatalog;
    private bool _fontCatalogLoading;
    private bool _fontSaveInProgress;

    // 进程内共享一次枚举（设置页每次导航都会新建 SettingsView）；失败结果不缓存，下次进入重试。
    private static Task<IReadOnlyList<SystemFontFamily>>? s_fontCatalogTask;

    /// <summary>单个字体槽位的界面元素。</summary>
    private sealed class FontSlotUi
    {
        public required AppFontSlot Slot { get; init; }
        public required ComboBox Combo { get; init; }
        public required Button ResetButton { get; init; }
        public required TextBlock Preview { get; init; }
        public required TextBlock ResolvedText { get; init; }
    }

    private UIElement BuildFontsSection()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        AutomationProperties.SetAutomationId(panel, "SettingsFontsSection");

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new StackPanel { Spacing = 2 };
        headerText.Children.Add(Styled(new TextBlock { Text = "字体" }, "SubtitleTextBlockStyle"));
        headerText.Children.Add(Styled(
            new TextBlock { Text = "选择后立即保存并应用到整个应用（含 OpenCode 页面与弹出层）。字体列表来自本机已安装字体。" },
            "CaptionSubtleTextBlockStyle"));
        header.Children.Add(headerText);

        _fontResetAllButton = new Button { Content = "字体与字号全部恢复默认", VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(_fontResetAllButton, "SettingsFontResetAll");
        _fontResetAllButton.Click += OnFontResetAllClick;
        Grid.SetColumn(_fontResetAllButton, 1);
        header.Children.Add(_fontResetAllButton);
        panel.Children.Add(header);
        panel.Children.Add(BuildTextSizeCard());

        _fontCatalogStatus = Styled(new TextBlock { Text = "正在读取已安装字体…" }, "CaptionSubtleTextBlockStyle");
        AutomationProperties.SetAutomationId(_fontCatalogStatus, "SettingsFontCatalogStatus");
        panel.Children.Add(_fontCatalogStatus);

        panel.Children.Add(BuildFontSlotCard(
            AppFontSlot.Body,
            "正文字体",
            "界面与对话正文（中英文）。默认：Segoe UI Variable，中文回退 Microsoft YaHei UI。",
            "敏捷的棕色狐狸跳过了懒狗，界面文字清晰易读。\nThe quick brown fox jumps over the lazy dog. 0123456789"));
        panel.Children.Add(BuildFontSlotCard(
            AppFontSlot.Serif,
            "衬线字体",
            "需要衬线排版的长文阅读。默认：Cambria，中文回退 Noto Serif SC / 思源宋体 / 宋体。",
            "落霞与孤鹜齐飞，秋水共长天一色。\nTypography gives language a durable visual form. 1234567890"));
        panel.Children.Add(BuildFontSlotCard(
            AppFontSlot.Monospace,
            "等宽字体",
            "代码、路径、命令与工具输出。默认：Cascadia Mono，回退 Consolas。",
            "fn main() {\n    let greeting = \"你好, world\"; // 0O 1lI |{}[]\n}"));

        _ = LoadFontCatalogAsync();
        return panel;
    }

    private Border BuildTextSizeCard()
    {
        var content = new StackPanel { Spacing = 8, Padding = new Thickness(16) };
        content.Children.Add(Styled(new TextBlock { Text = "文字大小" }, "BodyStrongTextBlockStyle"));
        content.Children.Add(Styled(new TextBlock
        {
            Text = "按比例调整正文、标题、代码与说明文字；图标和按钮点击区域不缩放。默认正文基准为 14 DIP。",
            TextWrapping = TextWrapping.Wrap,
        }, "CaptionSubtleTextBlockStyle"));
        _textSizeCombo = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(_textSizeCombo, "SettingsTextSizeCombo");
        AutomationProperties.SetName(_textSizeCombo, "文字大小，正文基准字号");
        foreach (double size in new double[] { 14, 15, 16, 18, 20, 22, 24 })
        {
            _textSizeCombo.Items.Add(new ComboBoxItem
            {
                Content = size == 14 ? "14 DIP · 100%（默认）" : $"{size:0} DIP · {size / 14:P0}",
                Tag = size,
            });
        }
        _textSizeCombo.SelectionChanged += async (_, _) =>
        {
            if (!_suppressUiEvents && _textSizeCombo.SelectedItem is ComboBoxItem { Tag: double value })
                await SaveFontsAsync(AppFontDefaults.WithTextSize(_settings.Fonts, value));
        };
        _textSizeReset = new Button { Content = "仅恢复默认字号", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(_textSizeReset, "SettingsTextSizeReset");
        _textSizeReset.Click += async (_, _) => await SaveFontsAsync(AppFontDefaults.WithTextSize(_settings.Fonts, null));
        _textSizeStatus = Styled(new TextBlock { TextWrapping = TextWrapping.Wrap }, "CaptionSubtleTextBlockStyle");
        AutomationProperties.SetAutomationId(_textSizeStatus, "SettingsTextSizeCurrent");
        content.Children.Add(_textSizeCombo);
        content.Children.Add(_textSizeStatus);
        content.Children.Add(_textSizeReset);
        var card = new Border { Child = content };
        ApplyResourceStyle(card, "AppSettingsCardStyle");
        return card;
    }

    private Border BuildFontSlotCard(AppFontSlot slot, string title, string description, string previewText)
    {
        string id = slot.ToString();

        var combo = new ComboBox
        {
            MinWidth = 260,
            MaxWidth = 360,
            IsTextSearchEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = FontDefaultItemText,
        };
        AutomationProperties.SetAutomationId(combo, $"SettingsFont{id}Combo");
        AutomationProperties.SetName(combo, title);
        combo.Items.Add(new ComboBoxItem { Content = FontDefaultItemText, Tag = null });
        combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) => OnFontSelectionChanged(slot);

        var reset = new Button { Content = "恢复默认", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(reset, $"SettingsFont{id}Reset");
        AutomationProperties.SetName(reset, $"{title}恢复默认");
        reset.Click += (_, _) => OnFontResetClick(slot);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(combo);
        controls.Children.Add(reset);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Styled(new TextBlock { Text = title }, "BodyStrongTextBlockStyle"));
        text.Children.Add(Styled(new TextBlock { Text = description }, "CaptionSubtleTextBlockStyle"));

        var top = new Grid { ColumnSpacing = 16, Padding = new Thickness(16, 12, 16, 12) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(text);
        Grid.SetColumn(controls, 1);
        top.Children.Add(controls);

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

        var resolved = Styled(new TextBlock { IsTextSelectionEnabled = true }, "CaptionSubtleTextBlockStyle");
        AutomationProperties.SetAutomationId(resolved, $"SettingsFont{id}Resolved");

        var previewPanel = new StackPanel { Spacing = 6 };
        previewPanel.Children.Add(preview);
        previewPanel.Children.Add(resolved);

        var previewHost = new Border
        {
            Padding = new Thickness(16, 12, 16, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = previewPanel,
        };
        ApplyResourceStyle(previewHost, "AppSettingsCardDividerStyle");

        var cardContent = new StackPanel();
        cardContent.Children.Add(top);
        cardContent.Children.Add(previewHost);

        var card = new Border { BorderThickness = new Thickness(1), Child = cardContent };
        ApplyResourceStyle(card, "AppSettingsCardStyle");
        AutomationProperties.SetAutomationId(card, $"SettingsFont{id}Card");

        _fontSlots[slot] = new FontSlotUi
        {
            Slot = slot,
            Combo = combo,
            ResetButton = reset,
            Preview = preview,
            ResolvedText = resolved,
        };

        return card;
    }

    /// <summary>后台枚举已安装字体（DirectWrite），完成后回 UI 线程填充三个下拉框（保留当前选择）。</summary>
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
            _fontCatalogStatus.Text = catalog.Count == 0
                ? "未能读取已安装字体列表，仍可使用默认字体。"
                : $"已找到 {catalog.Count} 个已安装字体。等宽字体列表只列出等宽字体。";

            bool previous = _suppressUiEvents;
            _suppressUiEvents = true;
            try
            {
                foreach (AppFontSlot slot in FontSlots)
                {
                    if (_fontSlots.TryGetValue(slot, out FontSlotUi? ui))
                    {
                        FillFontCombo(ui, catalog);
                    }
                }

                ApplyFontsToUi();
            }
            finally
            {
                _suppressUiEvents = previous;
            }
        });
    }

    private static void FillFontCombo(FontSlotUi ui, IReadOnlyList<SystemFontFamily> catalog)
    {
        IEnumerable<SystemFontFamily> candidates = catalog.Where(f => !f.IsSymbol);
        if (ui.Slot == AppFontSlot.Monospace && catalog.Any(f => f.IsMonospaced && !f.IsSymbol))
        {
            candidates = candidates.Where(f => f.IsMonospaced);
        }

        ui.Combo.Items.Clear();
        ui.Combo.Items.Add(new ComboBoxItem { Content = FontDefaultItemText, Tag = null });
        foreach (SystemFontFamily family in candidates)
        {
            ui.Combo.Items.Add(new ComboBoxItem
            {
                Content = DescribeFontFamily(family),
                Tag = family.FamilyName,
            });
        }
    }

    /// <summary>显示名：本地化名与英文名不同则并列（如“微软雅黑 · Microsoft YaHei”）。</summary>
    private static string DescribeFontFamily(SystemFontFamily family)
        => string.Equals(family.DisplayName, family.FamilyName, StringComparison.OrdinalIgnoreCase)
            ? family.FamilyName
            : $"{family.DisplayName} · {family.FamilyName}";

    /// <summary>把已保存的字体选择应用到界面（不保存、不触发选择事件；调用方负责 _suppressUiEvents）。</summary>
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
            selectedSize = new ComboBoxItem { Content = $"{size:0.##} DIP · {size / 14:P0}", Tag = size };
            _textSizeCombo.Items.Add(selectedSize);
        }
        _textSizeCombo.SelectedItem = selectedSize;
        _textSizeReset.IsEnabled = size != AppFontDefaults.DefaultTextSize;
        _textSizeStatus.Text = $"当前正文基准：{size:0.##} DIP（{size / 14:P0}）；默认：14 DIP。恢复字号不会更改已选字体。";
        foreach (AppFontSlot slot in FontSlots)
        {
            if (!_fontSlots.TryGetValue(slot, out FontSlotUi? ui))
            {
                continue;
            }

            string? family = fonts?.Get(slot);
            ui.Combo.SelectedIndex = FindOrAddFontItem(ui.Combo, family);
            ui.ResetButton.IsEnabled = family is not null;
            UpdateFontPreview(ui, fonts);
        }

        _fontResetAllButton.IsEnabled = fonts is not null;
    }

    /// <summary>找到保存的族名对应项；本机未安装时追加“（未安装）”项，界面仍如实反映已保存的值。</summary>
    private static int FindOrAddFontItem(ComboBox combo, string? family)
    {
        if (family is null)
        {
            return 0;
        }

        for (int i = 1; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string tag } &&
                string.Equals(tag, family, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        combo.Items.Insert(1, new ComboBoxItem { Content = $"{family}（未安装或未读取到）", Tag = family });
        return 1;
    }

    private static void UpdateFontPreview(FontSlotUi ui, AppFontSettings? fonts)
    {
        string source = AppFontDefaults.Resolve(fonts, ui.Slot);
        ui.Preview.FontFamily = new FontFamily(source);
        // Authored baselines: AppFonts' deferred metric pass applies the saved scale once.
        ui.Preview.FontSize = ui.Slot == AppFontSlot.Monospace ? 13 : 15;
        ui.Preview.LineHeight = ui.Slot == AppFontSlot.Monospace ? 20 : 24;
        ui.ResolvedText.Text = source == AppFontDefaults.BodyDefault
            ? "实际字体：系统默认（Segoe UI Variable，中文 Microsoft YaHei UI）"
            : $"实际字体：{source}";
    }

    private async void OnFontSelectionChanged(AppFontSlot slot)
    {
        if (_suppressUiEvents || !_fontSlots.TryGetValue(slot, out FontSlotUi? ui))
        {
            return;
        }

        string? family = (ui.Combo.SelectedItem as ComboBoxItem)?.Tag as string;
        await SaveFontsAsync(AppFontDefaults.With(_settings.Fonts, slot, family));
    }

    private async void OnFontResetClick(AppFontSlot slot)
        => await SaveFontsAsync(AppFontDefaults.With(_settings.Fonts, slot, null));

    private async void OnFontResetAllClick(object sender, RoutedEventArgs e)
        => await SaveFontsAsync(null);

    /// <summary>即时保存字体选择；成功后由 AppFonts 经 SettingsService.Changed 应用，失败时界面回滚到已保存值。</summary>
    private async Task SaveFontsAsync(AppFontSettings? next)
    {
        if (_fontSaveInProgress) return;
        AppFontSettings? normalized = AppFontDefaults.Normalize(next);
        if (Equals(normalized, AppFontDefaults.Normalize(_settings.Fonts)))
        {
            ApplySettingsToUi();
            return;
        }

        _fontSaveInProgress = true;
        _textSizeCombo.IsEnabled = false;
        _textSizeReset.IsEnabled = false;
        _fontResetAllButton.IsEnabled = false;
        foreach (FontSlotUi ui in _fontSlots.Values)
        {
            ui.Combo.IsEnabled = false;
            ui.ResetButton.IsEnabled = false;
        }
        try
        {
            await SaveAsync(
                settings => settings with { Fonts = normalized },
                normalized is null ? "已恢复默认字体与字号。" : "已保存字体与字号。");
        }
        finally
        {
            _fontSaveInProgress = false;
            _textSizeCombo.IsEnabled = true;
            foreach (FontSlotUi ui in _fontSlots.Values) ui.Combo.IsEnabled = true;
            ApplySettingsToUi();
        }
    }

    private static T Styled<T>(T element, string styleKey) where T : FrameworkElement
    {
        ApplyResourceStyle(element, styleKey);
        return element;
    }

    private static void ApplyResourceStyle(FrameworkElement element, string styleKey)
    {
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(styleKey, out object? value) &&
            value is Style style &&
            style.TargetType is { } target &&
            target.IsAssignableFrom(element.GetType()))
        {
            element.Style = style;
        }
    }
}
