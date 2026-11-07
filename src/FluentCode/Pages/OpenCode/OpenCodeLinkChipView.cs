using System;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.Configuration;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Focusable transcript chips. The inherited text metrics own their height and inline baseline;
/// icons fit inside that line. Neutral fill and brand-coloured icons unless monochrome/High Contrast.
/// </summary>
internal static class OpenCodeLinkChipView
{
    private const string GitHubMark = "M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z";
    private const string GitLabMark = "M8 15.2 1 9.9 2.5 1.4 4.6 6.6h6.8l2.1-5.2 1.5 8.5Z";
    private const string CodebergMark = "M8 1.5 15.2 14.5H.8Z";
    private const string GiteeMark = "M8 0a8 8 0 1 0 0 16A8 8 0 0 0 8 0Zm3.9 4.4H6.9a2.5 2.5 0 0 0-2.5 2.5v4.2c0 .3.2.5.5.5h4.2a1.9 1.9 0 0 0 1.9-1.9V8.6a.5.5 0 0 0-.5-.5H7.2a.5.5 0 0 0-.5.5v.9c0 .3.2.5.5.5h2.1v.4a.6.6 0 0 1-.6.6H6.3a.4.4 0 0 1-.4-.4V6.9c0-.6.5-1 1-1h5a.5.5 0 0 0 .5-.5v-.5a.5.5 0 0 0-.5-.5Z";

    internal static bool HighContrast
    {
        get
        {
            try { return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
            catch (Exception) { return false; }
        }
    }

    internal static bool OnlineEnabled => AppServices.Settings.Current?.LinkChips?.OnlineMetadata ?? false;
    internal static bool Monochrome => HighContrast || (AppServices.Settings.Current?.LinkChips?.MonochromeIcons ?? false);

    private static Brush ThemeBrush(string key, Windows.UI.Color fallback)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b ? b : new SolidColorBrush(fallback);

    internal static (Windows.UI.Color Fill, Windows.UI.Color Stroke, Windows.UI.Color Hover) Colors(bool dark) => dark
        ? (Windows.UI.Color.FromArgb(0x13, 255, 255, 255), Windows.UI.Color.FromArgb(0x1D, 255, 255, 255), Windows.UI.Color.FromArgb(0x1F, 255, 255, 255))
        : (Windows.UI.Color.FromArgb(0x0B, 0, 0, 0), Windows.UI.Color.FromArgb(0x1B, 0, 0, 0), Windows.UI.Color.FromArgb(0x14, 0, 0, 0));

    private static Windows.UI.Color? BrandColor(OpenCodeLinkSite site, bool dark) => site switch
    {
        OpenCodeLinkSite.GitHub => dark ? Windows.UI.Color.FromArgb(255, 0xF0, 0xF6, 0xFC) : Windows.UI.Color.FromArgb(255, 0x1F, 0x23, 0x28),
        OpenCodeLinkSite.GitLab => Windows.UI.Color.FromArgb(255, 0xFC, 0x6D, 0x26),
        OpenCodeLinkSite.Gitee => Windows.UI.Color.FromArgb(255, 0xC7, 0x1D, 0x23),
        OpenCodeLinkSite.Codeberg => Windows.UI.Color.FromArgb(255, 0x21, 0x85, 0xD0),
        OpenCodeLinkSite.Npm => Windows.UI.Color.FromArgb(255, 0xCB, 0x38, 0x37),
        OpenCodeLinkSite.Crates => Windows.UI.Color.FromArgb(255, 0xE4, 0x8A, 0x1F),
        OpenCodeLinkSite.PyPI => Windows.UI.Color.FromArgb(255, 0x37, 0x75, 0xA9),
        OpenCodeLinkSite.Jira => Windows.UI.Color.FromArgb(255, 0x26, 0x84, 0xFF),
        OpenCodeLinkSite.Linear => Windows.UI.Color.FromArgb(255, 0x5E, 0x6A, 0xD2),
        OpenCodeLinkSite.StackOverflow => Windows.UI.Color.FromArgb(255, 0xF4, 0x80, 0x24),
        _ => null,
    };

    /// <summary>14 px icon: bundled brand mark, a Fluent glyph, or (opt-in, generic public sites) the site's favicon.</summary>
    internal static FrameworkElement CreateIcon(OpenCodeLinkChip chip, bool dark, CancellationToken ct = default)
    {
        var mono = Monochrome;
        Brush foreground = !mono && BrandColor(chip.Site, dark) is { } brand ? new SolidColorBrush(brand)
            : ThemeBrush("TextFillColorPrimaryBrush", dark ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(0xE4, 0, 0, 0));
        string? path = chip.Site switch
        {
            OpenCodeLinkSite.GitHub => GitHubMark, OpenCodeLinkSite.GitLab => GitLabMark,
            OpenCodeLinkSite.Gitee => GiteeMark, OpenCodeLinkSite.Codeberg => CodebergMark, _ => null,
        };
        if (path is not null)
        {
            var shape = new Microsoft.UI.Xaml.Shapes.Path { Fill = foreground, Stretch = Stretch.Uniform, Width = 14, Height = 14 };
            shape.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), path);
            return shape;
        }
        var glyph = chip.Site switch
        {
            // This computer (localhost / loopback) vs. a network host (LAN IP, intranet name).
            OpenCodeLinkSite.Local => chip.Host is "localhost" or "127.0.0.1" or "::1" || chip.Host.EndsWith(".localhost", StringComparison.Ordinal)
                ? "\uE7F8" : "\uE968",
            OpenCodeLinkSite.Npm or OpenCodeLinkSite.Crates or OpenCodeLinkSite.PyPI => "\uE7B8",
            OpenCodeLinkSite.Jira or OpenCodeLinkSite.Linear => "\uE9D5",
            OpenCodeLinkSite.Mdn or OpenCodeLinkSite.Wikipedia or OpenCodeLinkSite.StackOverflow => "\uE736",
            _ => "\uE774",
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 14, Width = 14, Height = 14, Foreground = foreground };
        if (chip.Site != OpenCodeLinkSite.Generic || !OpenCodeLinkChipParser.ShouldFetchFavicon(chip, OnlineEnabled)) return icon;
        // Opt-in favicon: the globe stays until a cached/fetched icon decodes; any failure keeps the globe.
        var host = new Grid { Width = 14, Height = 14 };
        host.Children.Add(icon);
        _ = LoadFaviconAsync(host, icon, chip, ct);
        return host;
    }

    private static async Task LoadFaviconAsync(Grid host, FontIcon fallback, OpenCodeLinkChip chip, CancellationToken ct)
    {
        try
        {
            var bytes = await OpenCodeFaviconCache.Shared.GetAsync(chip, OnlineEnabled, ct);
            if (bytes is null || ct.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = 28 };
            await bitmap.SetSourceAsync(stream);
            if (ct.IsCancellationRequested) return;
            host.Children.Clear();
            host.Children.Add(new Image { Source = bitmap, Width = 14, Height = 14, Stretch = Stretch.Uniform });
        }
        catch (Exception) { /* Undecodable icon: keep the globe. */ _ = fallback; }
    }

    /// <summary>Hover card (≈500 ms): full address (secrets masked), site, kind and warnings.</summary>
    internal static FrameworkElement CreateCard(OpenCodeLinkChip chip)
    {
        var panel = new StackPanel { Spacing = 4, MaxWidth = 420 };
        var kind = chip.Kind switch
        {
            OpenCodeLinkKind.Issue => "Issue", OpenCodeLinkKind.PullRequest => "Pull request", OpenCodeLinkKind.MergeRequest => "Merge request",
            OpenCodeLinkKind.Commit => "提交", OpenCodeLinkKind.File => "文件", OpenCodeLinkKind.Release => "版本",
            OpenCodeLinkKind.Repository => "仓库", OpenCodeLinkKind.Owner => "用户 / 组织", OpenCodeLinkKind.Package => "软件包",
            OpenCodeLinkKind.Ticket => "工单", _ => "网页",
        };
        var title = new TextBlock { Text = OpenCodeLinkChipParser.SiteName(chip.Site) + " · " + kind, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(title);
        if (chip.Owner is { } owner && chip.Repository is { } repo)
            panel.Children.Add(new TextBlock { Text = owner + "/" + repo + (chip.Number is { } n && chip.Kind is not OpenCodeLinkKind.Commit ? (chip.Kind == OpenCodeLinkKind.MergeRequest ? " !" : " #") + n : string.Empty), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = chip.DisplayUrl, TextWrapping = TextWrapping.WrapWholeWords, FontSize = 12, Opacity = 0.8, IsTextSelectionEnabled = false });
        if (chip.IsConfusable) panel.Children.Add(new TextBlock { Text = "⚠ 域名含易混淆字符，显示为 " + chip.Host, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (chip.HasCredentials) panel.Children.Add(new TextBlock { Text = "链接包含用户名或令牌（已隐藏）", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (chip.IsPrivate) panel.Children.Add(new TextBlock { Text = "本机或内网地址，不联网补全", FontSize = 12, Opacity = 0.8 });
        return panel;
    }

    /// <summary>Only absolute http/https ever reach the system browser.</summary>
    internal static async Task<bool> OpenAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        try { return await Launcher.LaunchUriAsync(uri); }
        catch (Exception) { return false; }
    }

    internal static void Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception) { /* Clipboard busy: nothing copied. */ }
    }

    internal static string Markdown(OpenCodeLinkChip chip) => "[" + chip.Label.Replace("]", "\\]", StringComparison.Ordinal) + "](" + chip.Url + ")";

    /// <summary>Transcript chip: a real button (Tab focus, Enter/click opens, Shift+F10 menu, Fluent focus visual).</summary>
    internal static Button CreateTranscriptChip(OpenCodeLinkChip chip, bool dark)
    {
        var hc = HighContrast;
        var (fill, stroke, hover) = Colors(dark);
        var label = new TextBlock { Text = chip.Label, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Full };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, -1, 0, -1) };
        var icon = new Viewbox { Child = CreateIcon(chip, dark), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(label);
        if (chip.IsConfusable || chip.HasCredentials)
            content.Children.Add(new FontIcon { Glyph = chip.IsConfusable ? "\uE7BA" : "\uE72E", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(4, 0, 6, 0),
            MinHeight = 0, MinWidth = 0,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = true,
        };
        if (!hc)
        {
            button.Background = new SolidColorBrush(fill);
            button.BorderBrush = new SolidColorBrush(stroke);
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
            button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(hover);
            button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(stroke);
            button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(stroke);
        }
        else
        {
            // HighContrast: system Window/WindowText/Highlight colours from the theme dictionary (button defaults).
            button.Background = ThemeBrush("SystemColorWindowColorBrush", Windows.UI.Color.FromArgb(255, 0, 0, 0));
            button.BorderBrush = ThemeBrush("SystemColorWindowTextColorBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }
        AutomationProperties.SetName(button, chip.AccessibleName);
        AutomationProperties.SetHelpText(button, chip.DisplayUrl);
        AutomationProperties.SetAutomationId(button, "OpenCodeLinkChip");
        ToolTipService.SetToolTip(button, new ToolTip { Content = CreateCard(chip) });
        button.Click += async (_, _) => await OpenAsync(chip.Url);
        var menu = new MenuFlyout();
        menu.Items.Add(Item("打开链接", "\uE8A7", () => _ = OpenAsync(chip.Url)));
        menu.Items.Add(Item("复制链接", "\uE8C8", () => Copy(chip.Url)));
        menu.Items.Add(Item("复制为 Markdown", "\uE943", () => Copy(Markdown(chip))));
        button.ContextFlyout = menu;
        AlignInline(button, label, icon);
        return button;
    }

    internal static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Transcript "[Image N]" / "[PDF N]" chip backed by an actual file part (presentation of the literal text).</summary>
    internal static Border CreateAttachmentChip(int number, bool pdf, bool dark)
    {
        var (fill, stroke, _) = Colors(dark);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, -1, 0, -1) };
        var icon = new Viewbox { Child = new FontIcon { Glyph = pdf ? "\uEA90" : "\uEB9F", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = (pdf ? "PDF " : "图片 ") + number, VerticalAlignment = VerticalAlignment.Center, TextLineBounds = TextLineBounds.Full };
        content.Children.Add(icon);
        content.Children.Add(label);
        var chip = new Border
        {
            Child = content, Padding = new Thickness(4, 0, 6, 0), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        if (!HighContrast) { chip.Background = new SolidColorBrush(fill); chip.BorderBrush = new SolidColorBrush(stroke); }
        else chip.BorderBrush = ThemeBrush("SystemColorWindowTextColorBrush", Windows.UI.Color.FromArgb(255, 255, 255, 255));
        AutomationProperties.SetName(chip, (pdf ? "PDF " : "图片 ") + number + "，已附加的" + (pdf ? "文件" : "图片"));
        ToolTipService.SetToolTip(chip, (pdf ? "[PDF " : "[Image ") + number + "]");
        AlignInline(chip, label, icon);
        return chip;
    }

    private static void AlignInline(FrameworkElement chip, TextBlock label, Viewbox icon)
    {
        void Update()
        {
            label.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            var height = label.DesiredSize.Height;
            if (height <= 0) return;
            chip.Height = height;
            // InlineUIContainer puts the child's bottom on the baseline. Keep the label's
            // descent BELOW it, rather than guessing -4 for every font, CJK fallback and scale.
            chip.Margin = new Thickness(0, 0, 0, label.BaselineOffset - height);
            icon.Width = icon.Height = Math.Min(height - 2, label.FontSize);
        }
        chip.Loaded += (_, _) => Update();
        label.SizeChanged += (_, _) => Update();
        label.RegisterPropertyChangedCallback(TextBlock.FontSizeProperty, (_, _) => Update());
        label.RegisterPropertyChangedCallback(TextBlock.FontFamilyProperty, (_, _) => Update());
        Update();
    }

}
