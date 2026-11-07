using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    partial void InitializeComposerPaste() => Composer.SpecialPasteRequested += (editor, content) => { _ = PasteComposerImagesAsync(content); };

    private async Task PasteComposerImagesAsync(DataPackageView content)
    {
        if (_composerController is not { } controller || _composerView is not { } view
            || _composerBinding is not { } binding || !OwnsComposerBinding(binding)
            || GetComposerSnapshot().IsBusy || HasPendingNativeAction() || !view.PrepareSubmission()) return;
        var token = controller.Snapshot.Token;
        var ticket = controller.BeginPick(token);
        if (ticket is null) return;
        var operation = ++_composerPickEpoch;
        var activation = _draftActivationVersion;
        var selectionStart = Composer.SelectionStart;
        var selectionLength = Composer.SelectionLength;
        var editorText = Composer.Text;
        _composerPickerPending = true;
        UpdateComposerButtons();
        RefreshComposerView();
        try
        {
            using var cancellation = CreateOperationToken(binding.Source);
            var ct = cancellation.Token;
            IReadOnlyList<OpenCodeComposerFileSource> batch;
            string? fallbackText = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (OpenCodeClipboardImagePolicy.Classify(content.Contains(StandardDataFormats.Bitmap), Array.Empty<string>())
                    == OpenCodeClipboardImageKind.Bitmap)
                {
                    var reference = await content.GetBitmapAsync().AsTask(ct);
                    var name = OpenCodeClipboardImagePolicy.PngName(DateTime.Now);
                    batch = new[] { new OpenCodeComposerFileSource(name, "image/png", c => EncodeClipboardPngAsync(reference, c)) };
                }
                else
                {
                    var items = await content.GetStorageItemsAsync().AsTask(ct);
                    var files = items.OfType<StorageFile>().ToArray();
                    var kind = OpenCodeClipboardImagePolicy.Classify(false, files.Select(f => f.Name).ToArray());
                    batch = kind == OpenCodeClipboardImageKind.ImageFiles
                        ? files.Where(f => OpenCodeClipboardImagePolicy.IsImageFile(f.Name))
                            .Select(f => new OpenCodeComposerFileSource(f.Name, null, c => OpenClipboardFileAsync(f, c))).ToArray()
                        : Array.Empty<OpenCodeComposerFileSource>();
                    // StorageItems can coexist with text but contain no supported images. Preserve that text
                    // from THIS clipboard snapshot, rather than rereading a clipboard that may have changed.
                    if (kind == OpenCodeClipboardImageKind.DefaultText && content.Contains(StandardDataFormats.Text))
                        fallbackText = await content.GetTextAsync().AsTask(ct);
                }
            }
            catch (Exception error)
            {
                // Complete the original ticket exactly once through the existing atomic/error path.
                batch = new[] { new OpenCodeComposerFileSource("clipboard.png", "image/png", _ => Task.FromException<Stream>(error)) };
            }
            var paste = await controller.StageImagePasteAsync(ticket, batch, selectionStart, selectionLength, ct);
            if (paste is not null && Current()) view.InsertImagePaste(paste);
            if (fallbackText is not null && Current() && controller.Snapshot.Token == token
                && Composer.Text == editorText && Composer.SelectionStart == selectionStart && Composer.SelectionLength == selectionLength)
                view.InsertPlainPaste(token, fallbackText);
        }
        finally
        {
            controller.CancelPick(ticket); // Also releases a staged paste whose source/selection changed before insertion.
            if (operation == _composerPickEpoch && activation == _draftActivationVersion)
            {
                _composerPickerPending = false;
                RefreshComposerView();
                if (_pageActive) UpdateComposerButtons();
            }
        }
        bool Current() => operation == _composerPickEpoch && activation == _draftActivationVersion
            && OwnsComposerBinding(binding) && !binding.Source.IsRevoked;
    }

    private static Task<Stream> EncodeClipboardPngAsync(RandomAccessStreamReference reference, CancellationToken ct)
        => Task.Run<Stream>(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var input = await reference.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);
            using var bitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct).ConfigureAwait(false);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(ct).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
            if (output.Size > OpenCodeAttachment.MaxFileBytes) throw new ArgumentException("单个附件不得超过 10 MiB。");
            ct.ThrowIfCancellationRequested();
            output.Seek(0);
            using var reader = new DataReader(output);
            await reader.LoadAsync((uint)output.Size).AsTask(ct).ConfigureAwait(false);
            var bytes = new byte[(int)output.Size];
            reader.ReadBytes(bytes);
            return new MemoryStream(bytes, writable: false);
        }, ct);

    private static async Task<Stream> OpenClipboardFileAsync(StorageFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stream = await file.OpenStreamForReadAsync().ConfigureAwait(false);
        if (ct.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        return stream;
    }
}
