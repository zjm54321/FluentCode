using System;
using System.Collections.Generic;
using System.Linq;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Realized user-row attachments; shared image renderer owns lazy loading and unload cancellation.</summary>
internal sealed class OpenCodeUserAttachmentsView : IDisposable
{
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
        var images = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 256, ItemHeight = 280 };
        AutomationProperties.SetAutomationId(images, "OpenCodeUserImageAttachments");
        // Native wrap panel: fill a row, then wrap; fit even when the message column is narrow.
        images.SizeChanged += (_, _) =>
        {
            if (images.ActualWidth <= 0) return;
            images.ItemWidth = Math.Min(256, images.ActualWidth);
            images.MaximumRowsOrColumns = Math.Max(1, (int)(images.ActualWidth / images.ItemWidth));
        };
        foreach (var attachment in attachments)
        {
            if (attachment.Image is { } source)
            {
                var detail = new ImageDetail(attachment.Key, source);
                _details.Add(detail);
                var thumbnail = OpenCodeToolDetailContent.BuildImage(attachment.Key, source, new(), thumbnail: true, openLarge: detail.Open);
                thumbnail.Margin = new Thickness(0, 0, 8, 8);
                detail.DetailAnchor = thumbnail;
                thumbnail.Unloaded += (_, _) => { if (!thumbnail.IsLoaded) detail.Close(); };
                images.Children.Add(thumbnail);
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
        public OpenCodeToolDetailView BuildDetailContent() => OpenCodeToolDetailContent.Wrap(
            OpenCodeToolDetailContent.BuildImage(key, source, new() { BrowserImageScope = _scope }));
        public void OnDetailStateChanged(bool shown, bool pinned) => _shown = shown;
        internal void Close()
        {
            if (_shown) OpenCodeToolDetailPopup.Shared.CloseIf(this);
            _scope = null;
        }
    }
}
