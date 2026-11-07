using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FluentCode.Pages.OpenCode;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FluentCode.Pages;

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
    // The shared RichEditBox adapter owns plain-copy/paste and model undo/redo, in both surfaces.
    // Items stay visible and are disabled when unavailable, so 撤销 is always discoverable.
    private void InitializeComposerContextMenu()
    {
        Composer.ContextFlyout = OpenCodeContextMenu.Create(_ =>
        {
            var state = CurrentComposerEditState();
            // Link chip under the caret: 打开链接 · 复制链接 · 复制为 Markdown · 转回文字 · 删除 (spec §3).
            var link = _composerView?.LinkAtCaret();
            var chip = link is null ? null : OpenCodeLinkChipParser.Parse(link.Url);
            var view = _composerView;
            var linkItems = link is null || chip is null || view is null ? Array.Empty<OpenCodeContextCommand?>() : new OpenCodeContextCommand?[]
            {
                new("打开链接", "\uE8A7", () => { var opening = OpenCodeLinkChipView.OpenAsync(link.Url); }) { Accelerator = "Ctrl+单击" },
                new("复制链接", "\uE71B", () => { OpenCodeLinkChipView.Copy(link.Url); Motion.ActionFeedback.Announce(Composer, "已复制链接"); }),
                new("复制为 Markdown", "\uE943", () => OpenCodeLinkChipView.Copy(OpenCodeLinkChipView.Markdown(chip))),
                new("转回文字", "\uE8E9", () => view.ConvertLinkToText(link)) { Enabled = !Composer.IsReadOnly && !_isComposing },
                new("删除", "\uE74D", () => view.DeleteLink(link)) { Enabled = !Composer.IsReadOnly && !_isComposing },
                null,
            };
            return linkItems.Concat(new OpenCodeContextCommand?[]
            {
                new("剪切", "\uE8C6", Composer.CutSelectionToClipboard) { Row = true, Accelerator = "Ctrl+X", Enabled = state.Cut },
                new("复制", "\uE8C8", () =>
                {
                    try { Composer.CopySelectionToClipboard(); Motion.ActionFeedback.Announce(Composer, "已复制"); }
                    catch (Exception) { Motion.ActionFeedback.Announce(Composer, "复制失败，请重试"); }
                }) { Row = true, Accelerator = "Ctrl+C", Enabled = state.Copy },
                new("粘贴", "\uE77F", Composer.PasteFromClipboard) { Row = true, Accelerator = "Ctrl+V", Enabled = state.Paste },
                new("全选", "\uE8B3", Composer.SelectAll) { Row = true, Accelerator = "Ctrl+A", Enabled = state.SelectAll },
                new("撤销", "\uE7A7", () => { if (Composer.CanUndo) Composer.Undo(); }) { Accelerator = "Ctrl+Z", Enabled = state.Undo },
                new("重做", "\uE7A6", Composer.Redo) { Accelerator = "Ctrl+Y", Enabled = Composer.CanRedo && !_isComposing && !Composer.IsReadOnly },
            }).ToArray();
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
