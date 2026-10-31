using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    internal readonly record struct ComposerEditState(bool Undo, bool Cut, bool Copy, bool Paste, bool SelectAll);

    // Standard Windows edit order. Items stay visible and are disabled when unavailable, so 撤销 is always discoverable.
    internal static ComposerEditState ComposerEditCommands(bool canUndo, int selectionLength, int textLength,
        bool clipboardHasContent, bool composing, bool readOnly)
    {
        var editable = !composing && !readOnly;
        return new(editable && canUndo, editable && selectionLength > 0, !composing && selectionLength > 0,
            editable && clipboardHasContent, !composing && textLength > 0);
    }

    // Replaces the native text flyout (which hid 撤销 unless it could prove an undo step) with the app's shared
    // context-menu shape (Windows 11 text menu): list 撤销; bottom row 剪切 · 复制 · 粘贴 · 全选.
    // Every action is the TextBox's own: Undo/Cut/Copy/SelectAll, and PasteFromClipboard raises the existing Paste
    // handler (OpenCodePage.ComposerPaste.cs), so draft sync, IME and attachment paste keep their single paths.
    // Items stay visible and are disabled when unavailable, so 撤销 is always discoverable.
    private void InitializeComposerContextMenu()
    {
        Composer.ContextFlyout = OpenCodeContextMenu.Create(_ =>
        {
            var state = CurrentComposerEditState();
            return new OpenCodeContextCommand?[]
            {
                new("剪切", "\uE8C6", Composer.CutSelectionToClipboard) { Row = true, Accelerator = "Ctrl+X", Enabled = state.Cut },
                new("复制", "\uE8C8", Composer.CopySelectionToClipboard) { Row = true, Accelerator = "Ctrl+C", Enabled = state.Copy },
                new("粘贴", "\uE77F", Composer.PasteFromClipboard) { Row = true, Accelerator = "Ctrl+V", Enabled = state.Paste },
                new("全选", "\uE8B3", Composer.SelectAll) { Row = true, Accelerator = "Ctrl+A", Enabled = state.SelectAll },
                new("撤销", "\uE7A7", () => { if (Composer.CanUndo) Composer.Undo(); }) { Accelerator = "Ctrl+Z", Enabled = state.Undo },
            };
        });
        // One menu per gesture: without this the touch selection bar (cut/copy/paste) can open beside the edit menu.
        Composer.SelectionFlyout = null;
    }

    private ComposerEditState CurrentComposerEditState()
    {
        bool clipboardText;
        try
        {
            var content = Clipboard.GetContent(); // Text, or an image/file the existing Paste handler attaches.
            clipboardText = content.Contains(StandardDataFormats.Text) || content.Contains(StandardDataFormats.Bitmap)
                || content.Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception) { clipboardText = false; } // Clipboard busy/unavailable: paste stays disabled.
        return ComposerEditCommands(Composer.CanUndo, Composer.SelectionLength, (Composer.Text ?? string.Empty).Length,
            clipboardText, _isComposing, Composer.IsReadOnly);
    }
}
