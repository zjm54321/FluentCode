using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AIFrontend.Pages.OpenCode;

internal enum OpenCodeComposerPickOutcome
{
    /// <summary>No owning window / HWND: nothing opened, no ticket taken.</summary>
    MissingOwner,
    /// <summary>Controller refused a ticket (stale token, busy, inactive draft): nothing opened.</summary>
    NotAllowed,
    /// <summary>User cancelled, or scope/unload won while the dialog was open; ticket released, nothing read.</summary>
    Cancelled,
    /// <summary>Files handed to the controller's bounded reader; the snapshot reports the result.</summary>
    Delivered,
    /// <summary>The native picker failed; ticket released, nothing read.</summary>
    Failed,
}

/// <summary>
/// Legacy WinRT FileOpenPicker adapter (StorageFile, no paths). Never reads bytes itself, never
/// creates data URLs, never submits. The bounded/atomic read is the controller's loader.
/// </summary>
/// <remarks>
/// Must be called on the UI thread of <paramref name="owner"/>. The ticket is captured before the
/// native dialog opens; any later staleness (A→B→A, unload, discard) is fenced by the controller.
/// </remarks>
internal static class OpenCodeComposerFilePicker
{
    internal static async Task<OpenCodeComposerPickOutcome> PickAsync(IOpenCodeComposerController controller,
        OpenCodeComposerToken token, Window? owner, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (ct.IsCancellationRequested) return OpenCodeComposerPickOutcome.Cancelled;
        if (owner is null) return OpenCodeComposerPickOutcome.MissingOwner;

        IntPtr hwnd;
        try { hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner); }
        catch (Exception) { return OpenCodeComposerPickOutcome.MissingOwner; }
        if (hwnd == IntPtr.Zero) return OpenCodeComposerPickOutcome.MissingOwner;

        // Ticket BEFORE the native dialog: controller is now busy (Send AND Stop disabled).
        var ticket = controller.BeginPick(token);
        if (ticket is null) return OpenCodeComposerPickOutcome.NotAllowed;

        // From here on the ticket reaches AddFilesAsync EXACTLY ONCE on every path (there is no CancelPick):
        // selected files → bounded read; cancel / scope loss / any exception → empty batch.
        IReadOnlyList<OpenCodeComposerFileSource> batch = Array.Empty<OpenCodeComposerFileSource>();
        var batchToken = CancellationToken.None;
        var outcome = OpenCodeComposerPickOutcome.Failed;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "附加",
            };
            foreach (var extension in OpenCodeComposerInputPolicy.PickerExtensions) picker.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            IReadOnlyList<StorageFile>? files = await picker.PickMultipleFilesAsync();

            // Null/empty = user cancelled; unload/scope loss while open = discard silently.
            if (files is null || files.Count == 0 || ct.IsCancellationRequested)
            {
                outcome = OpenCodeComposerPickOutcome.Cancelled;
            }
            else
            {
                var sources = new OpenCodeComposerFileSource[files.Count];
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    // File name only (no directory). MIME from the same allow-list; core re-validates.
                    sources[i] = new(file.Name, OpenCodeComposerInputPolicy.MimeForName(file.Name), c => OpenAsync(file, c));
                }
                batch = sources;
                batchToken = ct;
                outcome = OpenCodeComposerPickOutcome.Delivered;
            }
        }
        catch (Exception)
        {
            batch = Array.Empty<OpenCodeComposerFileSource>();
            batchToken = CancellationToken.None;
            outcome = OpenCodeComposerPickOutcome.Failed;
        }

        // Single completion call. Controller fences ownership before each open/after each await; batch is atomic.
        try { await controller.AddFilesAsync(ticket, batch, batchToken).ConfigureAwait(true); }
        catch (Exception) { return OpenCodeComposerPickOutcome.Failed; } // Already reached once; never retried.
        return outcome;
    }

    /// <summary>Cancellable opener: if cancellation wins the (non-cancellable) open, the stream is disposed.</summary>
    private static async Task<Stream> OpenAsync(StorageFile file, CancellationToken ct)
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
