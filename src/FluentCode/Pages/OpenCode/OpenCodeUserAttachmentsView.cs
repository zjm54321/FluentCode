using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Realized user-row attachments, same presentation as the composer: each image is a small center-cropped square
/// (hover/focus = larger thumbnail, click/Enter = the shared large image view). The row height is the tiles' height —
/// nothing is reserved for the image's natural size. Bytes load lazily through the session image scope; unload cancels.
/// </summary>
internal sealed class OpenCodeUserAttachmentsView : IDisposable
{
    internal const double TileSize = 52;
    internal const double TileGap = 6;
    private const double PreviewSize = 240;

    private readonly StackPanel _root = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private IReadOnlyList<OpenCodeUserAttachment> _attachments = Array.Empty<OpenCodeUserAttachment>();
    private readonly List<ImageDetail> _details = new();
    internal FrameworkElement Element => _root;

    internal void Update(OpenCodeMessage message)
    {
        var attachments = OpenCodeUserAttachments.FromMessage(message);
        if (_attachments.SequenceEqual(attachments)) return;
        Dispose();
        _attachments = attachments;
        _root.Children.Clear();
        _root.Visibility = attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _root.Margin = new Thickness(0, string.IsNullOrEmpty(message.Text) ? 0 : 8, 0, 0);
        // Fixed square cells: wraps at the bubble width, each row exactly one tile (+gap) tall.
        var images = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = TileSize + TileGap,
            ItemHeight = TileSize + TileGap,
            Margin = new Thickness(0, 0, -TileGap, -TileGap),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(images, "OpenCodeUserImageAttachments");
        foreach (var attachment in attachments)
        {
            if (attachment.Image is { } source)
            {
                var detail = new ImageDetail(attachment.Key, source);
                _details.Add(detail);
                var tile = ImageTile(attachment.Key, source, detail.Open);
                detail.DetailAnchor = tile;
                tile.Unloaded += (_, _) => { if (!tile.IsLoaded) detail.Close(); };
                images.Children.Add(tile);
            }
            else
            {
                var chip = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock { Text = "附件 · " + attachment.Name, TextWrapping = TextWrapping.Wrap } };
                AutomationProperties.SetAutomationId(chip, "OpenCodeUserAttachment");
                OpenCodeToolTheme.Apply(chip, OpenCodeToolTheme.Border(OpenCodeToolTheme.SubtleSecondary));
                _root.Children.Add(chip);
            }
        }
        if (images.Children.Count > 0) _root.Children.Insert(0, images);
    }

    public void Dispose()
    {
        foreach (var detail in _details) detail.Close();
        _details.Clear();
    }

    /// <summary>52px center-cropped tile; its tooltip holds an aspect-preserving ≤240px thumbnail and the file name.</summary>
    private static Button ImageTile(string key, OpenCodeToolImageSource source, Action open)
    {
        var picture = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var face = new Grid();
        var placeholder = new FontIcon { Glyph = "\uEB9F", FontSize = 18, Opacity = 0.6 };
        face.Children.Add(placeholder);
        face.Children.Add(new Border { CornerRadius = new CornerRadius(7), Child = picture });
        var tile = new Button
        {
            Content = face,
            Width = TileSize,
            Height = TileSize,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(tile, "OpenCodeUserImage");
        AutomationProperties.SetName(tile, "查看图片 · " + source.Name);
        tile.Click += (_, _) => open();

        var preview = new Image { Stretch = Stretch.Uniform, MaxWidth = PreviewSize, MaxHeight = PreviewSize, HorizontalAlignment = HorizontalAlignment.Left };
        var caption = OpenCodeInteractionUi.StyledText(source.Name, OpenCodeInteractionUi.CaptionSubtleStyleKey, false);
        caption.MaxLines = 1;
        caption.MaxWidth = PreviewSize;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var tipContent = new StackPanel { Spacing = 6 };
        tipContent.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = preview, HorizontalAlignment = HorizontalAlignment.Left });
        tipContent.Children.Add(caption);
        var tip = new ToolTip { Content = tipContent };
        AutomationProperties.SetAutomationId(tip, "OpenCodeUserImagePreview");
        ToolTipService.SetToolTip(tile, tip);

        var lifetime = new OpenCodeImageRenderLifetime();
        tile.Loaded += async (_, _) =>
        {
            var scope = OpenCodeImageContext.Find(tile);
            var (generation, token) = lifetime.Begin();
            bool Current() => lifetime.IsCurrent(generation, token) && scope is not { IsCurrent: false };
            try
            {
                var file = scope is not null ? await scope.ReadToolImageAsync(key, source, token)
                    : source.Reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                        ? OpenCodeToolImageResolver.DecodeDataUrl(source.Reference)
                        : throw new InvalidOperationException("当前会话未提供图片读取方式");
                if (!Current()) return;
                var square = await DecodeAsync(file.Bytes, TileSize, fill: true);
                if (!Current()) return;
                var large = await DecodeAsync(file.Bytes, PreviewSize, fill: false);
                if (!Current()) return;
                picture.Source = square;
                preview.Source = large;
                placeholder.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!Current()) return;
                placeholder.Glyph = "\uE783"; // Error
                caption.Text = source.Name + " · 无法读取图片：" + error.Message;
                caption.MaxLines = 3;
                caption.TextWrapping = TextWrapping.Wrap;
            }
        };
        tile.Unloaded += (_, _) => { lifetime.Cancel(); picture.Source = null; preview.Source = null; };
        return tile;
    }

    /// <summary>Decodes at display size (logical px): <paramref name="fill"/> sizes the shorter side, else the longer; never upscales.</summary>
    private static async Task<BitmapImage> DecodeAsync(byte[] bytes, double side, bool fill)
    {
        using var stream = await OpenCodeBrowserCaptureImage.StreamAsync(bytes);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var (width, height) = (decoder.PixelWidth, decoder.PixelHeight);
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
        var target = (int)Math.Ceiling(side);
        if ((fill ? Math.Min(width, height) : Math.Max(width, height)) > target)
        {
            if ((width <= height) == fill) bitmap.DecodePixelWidth = target;
            else bitmap.DecodePixelHeight = target;
        }
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private sealed class ImageDetail(string key, OpenCodeToolImageSource source) : IOpenCodeDetailContentSource
    {
        private OpenCodeMarkdownImageScope? _scope;
        private bool _shown;
        public FrameworkElement DetailAnchor { get; set; } = null!;
        public OpenCodeDetailHeader DetailHeader => new("\uEB9F", "图片附件", source.Name, AccessibleName: "图片附件 · " + source.Name);
        internal void Open()
        {
            _scope = OpenCodeImageContext.Find(DetailAnchor);
            OpenCodeToolDetailPopup.Shared.TogglePinned(this);
        }
        public OpenCodeToolDetailView BuildDetailContent()
            => OpenCodeComposerAttachmentImageViewer.FitToWindow(key, source, new() { BrowserImageScope = _scope });
        public void OnDetailStateChanged(bool shown, bool pinned) => _shown = shown;
        internal void Close()
        {
            if (_shown) OpenCodeToolDetailPopup.Shared.CloseIf(this);
            _scope = null;
        }
    }
}
