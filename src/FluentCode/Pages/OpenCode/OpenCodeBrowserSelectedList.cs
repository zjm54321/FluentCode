using System;
using System.Linq;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode.Browser;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace FluentCode.Pages.OpenCode;

internal sealed class OpenCodeBrowserSelectedList : Grid
{
    private readonly StackPanel _items = new() { Spacing = 8, Padding = new(0, 0, 0, 4) };
    private readonly TextBlock _heading = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 4) };
    private readonly Button _add = new() { Height = 32 };
    internal readonly CheckBox FullPage = new() { Content = "附带整页截图", IsChecked = OpenCodeBrowserSelectionPolicy.DefaultFullPage };
    internal readonly CheckBox ElementInfo = new() { Content = "附带元素信息", IsChecked = OpenCodeBrowserSelectionPolicy.DefaultElementInfo };
    internal event Action? AddRequested;
    internal event Action<Guid>? Removed;
    internal event Action<Guid, string>? NoteChanged;
    internal event Action<byte[]>? CopyRequested;
    internal event Action<byte[]>? SaveRequested;
    internal OpenCodeBrowserSelectedList()
    {
        // Side sheet: one hairline divider toward the page, 16px gutters, cards on a quiet layer.
        Width = 340; Padding = new(16, 12, 16, 16); RowSpacing = 12; Visibility = Visibility.Collapsed;
        BorderThickness = new(1, 0, 0, 0);
        Themed(this, "Background", "SolidBackgroundFillColorSecondaryBrush"); Themed(this, "BorderBrush", "DividerStrokeColorDefaultBrush");
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var headingRow = new Grid { ColumnSpacing = 8, Margin = new(0, 0, -8, 0) };
        headingRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); headingRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        headingRow.Children.Add(_heading);
        var hide = QuietButton("\uE8BB", 32, 12);
        AutomationProperties.SetName(hide, "收起批注"); ToolTipService.SetToolTip(hide, "收起批注");
        hide.Click += (_, _) => Visibility = Visibility.Collapsed;
        SetColumn(hide, 1); headingRow.Children.Add(hide); Children.Add(headingRow);
        var scroll = new ScrollViewer { Content = _items, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 1); Children.Add(scroll);
        var bottom = new StackPanel { Spacing = 0 };
        Themed(_status, "Foreground", "SystemFillColorCriticalBrush");
        FullPage.MinHeight = ElementInfo.MinHeight = 32;
        _add.Margin = new(0, 8, 0, 0);
        bottom.Children.Add(_status); bottom.Children.Add(FullPage); bottom.Children.Add(ElementInfo);
        _add.HorizontalAlignment = HorizontalAlignment.Stretch;
        _add.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _add.Click += (_, _) => AddRequested?.Invoke(); bottom.Children.Add(_add); SetRow(bottom, 2); Children.Add(bottom);
        FluentCode.Motion.FluentMotion.BindTransitions(this, transitions => ChildrenTransitions = transitions, () => new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 40, FromVerticalOffset = 0 } });
        FluentCode.Motion.FluentMotion.BindTransitions(this, transitions => Transitions = transitions, () => new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 340, FromVerticalOffset = 0 } });
    }
    internal void Status(string text)
    {
        _status.Text = text;
        _status.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void Render(OpenCodeBrowserSelectionList list)
    {
        Visibility = list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _heading.Text = $"已选择 {list.Items.Count} 项"; _add.Content = $"添加到对话（{list.Items.Count}）";
        _items.Children.Clear();
        foreach (var item in list.Items)
        {
            var row = new StackPanel { Spacing = 8 };
            var header = new Grid { ColumnSpacing = 8, Margin = new(0, 0, -4, 0) };
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var number = new TextBlock { Text = item.Number.ToString(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 0, 1) };
            var badge = new Border { MinWidth = 20, Height = 20, CornerRadius = new(10), Padding = new(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = ColorBrush(OpenCodeBrowserSelectionPolicy.Color(item.Kind)), Child = number };
            header.Children.Add(badge);
            var title = new TextBlock { Text = OpenCodeBrowserSelectionPolicy.Label(item.Kind) + " · " + item.Selector, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(title, 1); header.Children.Add(title); ToolTipService.SetToolTip(title, title.Text);
            var remove = QuietButton("\uE711", 28, 10); AutomationProperties.SetName(remove, $"移除第 {item.Number} 项"); ToolTipService.SetToolTip(remove, "移除");
            remove.Click += (_, _) => Removed?.Invoke(item.Id); SetColumn(remove, 2); header.Children.Add(remove); row.Children.Add(header);
            if (Details(item, title.Text) is { Length: > 0 } details)
            {
                var meta = new TextBlock { Text = details, FontSize = 12, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(28, -6, 0, 0) };
                Themed(meta, "Foreground", "TextFillColorSecondaryBrush"); ToolTipService.SetToolTip(meta, details); row.Children.Add(meta);
            }
            var thumbnail = Image(item.Image, 72); thumbnail.HorizontalAlignment = HorizontalAlignment.Center;
            var frame = new Border { Child = thumbnail, Padding = new(8), CornerRadius = new(4), BorderThickness = new(1) };
            Themed(frame, "Background", "SubtleFillColorSecondaryBrush"); Themed(frame, "BorderBrush", "CardStrokeColorDefaultBrush");
            row.Children.Add(frame);
            var note = new TextBox { PlaceholderText = "添加批注（可选）", Text = item.Note, MaxLength = 4000 };
            AutomationProperties.SetName(note, $"第 {item.Number} 项批注");
            note.TextChanged += (_, _) => NoteChanged?.Invoke(item.Id, note.Text); row.Children.Add(note);
            // Shared context-menu shape: list 另存为图片; bottom row 复制.
            var image = item.Image;
            var menu = OpenCodeContextMenu.Create(new OpenCodeContextCommand?[]
            {
                new("复制", "\uE8C8", () => CopyRequested?.Invoke(image)) { Row = true },
                new("另存为图片", "\uE74E", () => SaveRequested?.Invoke(image)),
            });
            var card = new Border { Padding = new(12, 10, 12, 12), CornerRadius = new(8), BorderThickness = new(1), Child = row, ContextFlyout = menu };
            Themed(card, "Background", "CardBackgroundFillColorDefaultBrush"); Themed(card, "BorderBrush", "CardStrokeColorDefaultBrush");
            _items.Children.Add(card);
        }
    }
    // Secondary line: captured text/value, size and ref, without repeating what the title already says.
    private static string Details(BrowserSelectionItem item, string title)
    {
        var element = item.Elements.FirstOrDefault();
        var text = string.Join(' ', (element?.Value ?? element?.Text ?? item.AnnotationText ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var size = $"{item.Box.Width:0} × {item.Box.Height:0}";
        return string.Join(" · ", new[] { text, title.Contains(size, StringComparison.Ordinal) ? null : size, element?.Ref }.Where(part => !string.IsNullOrEmpty(part)));
    }
    private static Button QuietButton(string glyph, double size, double glyphSize)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = glyphSize }, Width = size, Height = size, MinWidth = 0, MinHeight = 0, Padding = new(0), BorderThickness = new(0) };
        if (Application.Current.Resources.TryGetValue("DefaultButtonStyle", out var native)) button.Style = (Style)native;
        Themed(button, "Background", "SubtleFillColorTransparentBrush");
        return button;
    }
    private static void Themed(FrameworkElement element, string property, string resource)
    {
        var type = element.GetType();
        while (type.Namespace?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) != true) type = type.BaseType!;
        var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Style xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"" + type.Name + "\"><Setter Property=\"" + property + "\" Value=\"{ThemeResource " + resource + "}\"/></Style>");
        style.BasedOn = element.Style; element.Style = style;
    }
    internal static Image Image(byte[] bytes, double height)
    {
        var image = new Image { Height = height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        _ = LoadImageAsync(image, bytes); return image;
    }
    private static async Task LoadImageAsync(Image image, byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
            stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap;
        }
        catch (Exception) { image.Visibility = Visibility.Collapsed; }
    }
    internal static SolidColorBrush ColorBrush(string hex) => new(Windows.UI.Color.FromArgb(255, Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16)));
}
