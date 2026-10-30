using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private MenuFlyoutItem? _composerUndoItem, _composerCutItem, _composerCopyItem, _composerPasteItem, _composerSelectAllItem;

    // Replaces the native text flyout (which hid 撤销 unless it could prove an undo step) with the full edit menu.
    // Every action is the TextBox's own: Undo/Cut/Copy/SelectAll, and PasteFromClipboard raises the existing Paste
    // handler (OpenCodePage.ComposerPaste.cs), so draft sync, IME and attachment paste keep their single paths.
    private void InitializeComposerContextMenu()
    {
        MenuFlyoutItem Item(string text, Symbol symbol, VirtualKey key, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(symbol) };
            item.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
                { Key = key, Modifiers = VirtualKeyModifiers.Control, IsEnabled = false }); // Shown only; the TextBox owns the keys.
            item.Click += (_, _) => action();
            return item;
        }
        _composerUndoItem = Item("撤销", Symbol.Undo, VirtualKey.Z, () => { if (Composer.CanUndo) Composer.Undo(); });
        _composerCutItem = Item("剪切", Symbol.Cut, VirtualKey.X, Composer.CutSelectionToClipboard);
        _composerCopyItem = Item("复制", Symbol.Copy, VirtualKey.C, Composer.CopySelectionToClipboard);
        _composerPasteItem = Item("粘贴", Symbol.Paste, VirtualKey.V, Composer.PasteFromClipboard);
        _composerSelectAllItem = Item("全选", Symbol.SelectAll, VirtualKey.A, Composer.SelectAll);
        var menu = new MenuFlyout();
        menu.Items.Add(_composerUndoItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_composerCutItem);
        menu.Items.Add(_composerCopyItem);
        menu.Items.Add(_composerPasteItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_composerSelectAllItem);
        menu.Opening += (_, _) => RefreshComposerContextMenu();
        Composer.ContextFlyout = menu;
    }

    private void RefreshComposerContextMenu()
    {
        if (_composerUndoItem is null) return;
        bool clipboardText;
        try
        {
            var content = Clipboard.GetContent(); // Text, or an image/file the existing Paste handler attaches.
            clipboardText = content.Contains(StandardDataFormats.Text) || content.Contains(StandardDataFormats.Bitmap)
                || content.Contains(StandardDataFormats.StorageItems);
        }
        catch (Exception) { clipboardText = false; } // Clipboard busy/unavailable: paste stays disabled.
        var state = ComposerEditCommands(Composer.CanUndo, Composer.SelectionLength, (Composer.Text ?? string.Empty).Length,
            clipboardText, _isComposing, Composer.IsReadOnly);
        _composerUndoItem.IsEnabled = state.Undo;
        _composerCutItem!.IsEnabled = state.Cut;
        _composerCopyItem!.IsEnabled = state.Copy;
        _composerPasteItem!.IsEnabled = state.Paste;
        _composerSelectAllItem!.IsEnabled = state.SelectAll;
    }
}
