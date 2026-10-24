using System;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode.Browser;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace AIFrontend.Pages.OpenCode;

internal sealed class OpenCodeBrowserSelectedList : Grid
{
    private readonly StackPanel _items = new() { Spacing = 8 };
    private readonly TextBlock _heading = new() { FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _add = new();
    internal readonly CheckBox FullPage = new() { Content = "附带整页截图", IsChecked = OpenCodeBrowserSelectionPolicy.DefaultFullPage };
    internal readonly CheckBox ElementInfo = new() { Content = "附带元素信息", IsChecked = OpenCodeBrowserSelectionPolicy.DefaultElementInfo };
    internal event Action? AddRequested;
    internal event Action<Guid>? Removed;
    internal event Action<Guid, string>? NoteChanged;
    internal event Action<byte[]>? CopyRequested;
    internal event Action<byte[]>? SaveRequested;
    internal OpenCodeBrowserSelectedList()
    {
        Width = 340; Padding = new(12); RowSpacing = 10; Visibility = Visibility.Collapsed;
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"];
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(_heading);
        var scroll = new ScrollViewer { Content = _items, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 1); Children.Add(scroll);
        var bottom = new StackPanel { Spacing = 4 };
        bottom.Children.Add(_status); bottom.Children.Add(FullPage); bottom.Children.Add(ElementInfo);
        bottom.Children.Add(new TextBlock { Text = "发送到：当前会话", Opacity = .7 });
        _add.HorizontalAlignment = HorizontalAlignment.Stretch;
        _add.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _add.Click += (_, _) => AddRequested?.Invoke(); bottom.Children.Add(_add); SetRow(bottom, 2); Children.Add(bottom);
        ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 40, FromVerticalOffset = 0 } };
        Transitions = new TransitionCollection { new EntranceThemeTransition { FromHorizontalOffset = 340, FromVerticalOffset = 0 } };
    }
    internal void Status(string text) => _status.Text = text;
    internal void Render(OpenCodeBrowserSelectionList list)
    {
        Visibility = list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _heading.Text = $"已选择 {list.Items.Count} 项"; _add.Content = $"添加到对话（{list.Items.Count}）";
        _items.Children.Clear();
        foreach (var item in list.Items)
        {
            var row = new StackPanel { Spacing = 6 };
            var header = new Grid { ColumnSpacing = 6 };
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var badge = new Border { CornerRadius = new(12), Padding = new(5, 1, 5, 1), Background = ColorBrush(OpenCodeBrowserSelectionPolicy.Color(item.Kind)), Child = new TextBlock { Text = item.Number.ToString(), Foreground = new SolidColorBrush(Colors.White) } };
            header.Children.Add(badge);
            var title = new TextBlock { Text = OpenCodeBrowserSelectionPolicy.Label(item.Kind) + " · " + item.Selector, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(title, 1); header.Children.Add(title); ToolTipService.SetToolTip(title, title.Text);
            var remove = new Button { Content = "×", Padding = new(5, 0, 5, 0), MinWidth = 24, MinHeight = 24 }; AutomationProperties.SetName(remove, $"移除第 {item.Number} 项");
            remove.Click += (_, _) => Removed?.Invoke(item.Id); SetColumn(remove, 2); header.Children.Add(remove); row.Children.Add(header);
            var element = item.Elements.FirstOrDefault();
            row.Children.Add(new TextBlock { Text = $"{element?.Value ?? element?.Text ?? item.AnnotationText} · {item.Box.Width:0} × {item.Box.Height:0} · {element?.Ref}", MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .7 });
            row.Children.Add(Image(item.Image, 72));
            var note = new TextBox { PlaceholderText = "可选说明", Text = item.Note, MaxLength = 4000 };
            note.TextChanged += (_, _) => NoteChanged?.Invoke(item.Id, note.Text); row.Children.Add(note);
            var menu = new MenuFlyout();
            var copy = new MenuFlyoutItem { Text = "复制图片" }; copy.Click += (_, _) => CopyRequested?.Invoke(item.Image); menu.Items.Add(copy);
            var save = new MenuFlyoutItem { Text = "另存为图片" }; save.Click += (_, _) => SaveRequested?.Invoke(item.Image); menu.Items.Add(save);
            var card = new Border { Padding = new(8), CornerRadius = new(6), BorderThickness = new(1), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], Child = row, ContextFlyout = menu };
            _items.Children.Add(card);
        }
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
