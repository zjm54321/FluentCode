using System;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// Composer image attachments open in the same large image view as transcript image attachments
/// (shared <see cref="OpenCodeToolDetailPopup"/>: ~80% window, smoke backdrop, ✕ / Esc / click outside closes).
/// Read-only: the bytes are the staged draft image; nothing is uploaded or changed.
/// </summary>
internal sealed class OpenCodeComposerAttachmentImageViewer : IOpenCodeDetailContentSource
{
    private readonly string _key;
    private readonly OpenCodeToolImageSource _source;
    private bool _shown;

    private OpenCodeComposerAttachmentImageViewer(OpenCodeComposerAttachmentImageEventArgs e)
    {
        _key = "composer-attachment:" + e.Id.ToString("N");
        _source = new("data:" + e.Mime + ";base64," + Convert.ToBase64String(e.Bytes), e.Mime, e.Name);
        DetailAnchor = e.Anchor;
    }

    public FrameworkElement DetailAnchor { get; }

    public OpenCodeDetailHeader DetailHeader => new("\uEB9F", "图片附件", _source.Name, AccessibleName: "图片附件 · " + _source.Name);

    public OpenCodeToolDetailView BuildDetailContent() => FitToWindow(_key, _source, new());

    /// <summary>Shared large image view content (composer and sent-message attachments).</summary>
    internal static OpenCodeToolDetailView FitToWindow(string key, OpenCodeToolImageSource source, OpenCodeToolDetailOptions options)
    {
        var section = OpenCodeToolDetailContent.BuildImage(key, source, options);
        var view = OpenCodeToolDetailContent.Wrap(section);
        // Fit to the dialog: never wider than the image itself, never taller than the visible area (no scrolling
        // to see the bottom). The shared renderer's frame/caption stay as they are.
        if (section is Panel { Children.Count: > 0 } panel && panel.Children[0] is Border { Child: Image picture } frame)
        {
            frame.HorizontalAlignment = HorizontalAlignment.Center;
            picture.HorizontalAlignment = HorizontalAlignment.Center;
            if (panel.Children.Count > 1 && panel.Children[1] is FrameworkElement caption) caption.HorizontalAlignment = HorizontalAlignment.Center;
            // From the window, not the dialog (the dialog sizes to its content): ~80% window minus title and caption.
            var root = view.Root;
            XamlRoot? observed = null;
            void Fit(XamlRoot sender, XamlRootChangedEventArgs args) => picture.MaxHeight = Math.Max(160, sender.Size.Height * 0.8 - 176);
            root.Loaded += (_, _) =>
            {
                if (root.XamlRoot is not { } xamlRoot || observed is not null) return;
                observed = xamlRoot;
                xamlRoot.Changed += Fit;
                Fit(xamlRoot, null!);
            };
            root.Unloaded += (_, _) => { if (observed is not null) observed.Changed -= Fit; observed = null; };
        }
        return view;
    }

    public void OnDetailStateChanged(bool shown, bool pinned) => _shown = shown;

    internal static void Open(OpenCodeComposerAttachmentImageEventArgs e)
    {
        var viewer = new OpenCodeComposerAttachmentImageViewer(e);
        OpenCodeToolDetailPopup.Shared.TogglePinned(viewer);
        void Unloaded(object sender, RoutedEventArgs args)
        {
            if (e.Anchor.IsLoaded) return;
            e.Anchor.Unloaded -= Unloaded;
            if (viewer._shown) OpenCodeToolDetailPopup.Shared.CloseIf(viewer);
        }
        e.Anchor.Unloaded += Unloaded;
    }
}
