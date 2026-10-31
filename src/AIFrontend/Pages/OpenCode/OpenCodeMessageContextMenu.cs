using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Transcript message context menu (right-click, Shift+F10 / menu key, and touch press-and-hold all raise
/// ContextRequested, so this ONE MenuFlyout serves every input): 复制 / 全选 for the selection, 复制整段 /
/// 复制代码, then 撤回 / 恢复. Items are rebuilt on Opening so state is never stale.
/// </summary>
internal static class OpenCodeMessageContextMenu
{
    private static readonly ConditionalWeakTable<TextBlock, string> CodeSources = new();

    /// <summary>Marks a rendered code-block text so the menu offers 复制代码 with its exact source.</summary>
    internal static void MarkCode(TextBlock code, string source) => CodeSources.AddOrUpdate(code, source);

    internal static MenuFlyout Create(Func<string?> wholeText, Func<OpenCodeRollbackMenuState?>? rollback = null,
        Action<bool>? rollbackRequested = null)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => Populate(menu, wholeText, rollback, rollbackRequested);
        return menu;
    }

    /// <summary>
    /// Replaces the default TextCommandBarFlyout of every selectable text in <paramref name="root"/> with
    /// <paramref name="menu"/> (the copy / select-all commands are re-offered inside it). Idempotent.
    /// </summary>
    internal static void Apply(DependencyObject? root, FlyoutBase menu)
    {
        switch (root)
        {
            case null: return;
            case TextBlock { IsTextSelectionEnabled: true } text: text.ContextFlyout = menu; return;
            case RichTextBlock rich:
                if (rich.IsTextSelectionEnabled) rich.ContextFlyout = menu;
                return;
            case Panel panel:
                foreach (var child in panel.Children) Apply(child, menu);
                return;
            case Border border: Apply(border.Child, menu); return;
            case Viewbox viewbox: Apply(viewbox.Child, menu); return;
            case UserControl user: Apply(user.Content, menu); return;
            case Expander expander:
                Apply(expander.Header as DependencyObject, menu);
                Apply(expander.Content as DependencyObject, menu);
                return;
            case ContentControl content: Apply(content.Content as DependencyObject, menu); return;
        }
    }

    internal static void Copy(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception) { /* Clipboard owned by another process: menu copy is best effort. */ }
    }

    private static void Populate(MenuFlyout menu, Func<string?> wholeText, Func<OpenCodeRollbackMenuState?>? rollback,
        Action<bool>? rollbackRequested)
    {
        menu.Items.Clear();
        var target = menu.Target;
        string? selected = target switch
        {
            TextBlock t => t.SelectedText,
            RichTextBlock r => r.SelectedText,
            _ => null,
        };
        var selectable = target is TextBlock { IsTextSelectionEnabled: true } or RichTextBlock { IsTextSelectionEnabled: true };

        if (selectable)
        {
            var copy = Item("复制", "\uE8C8", "Ctrl+C", () => Copy(selected!));
            copy.IsEnabled = !string.IsNullOrEmpty(selected);
            menu.Items.Add(copy);
            menu.Items.Add(Item("全选", "\uE8B3", "Ctrl+A", () =>
            {
                if (target is TextBlock t) t.SelectAll();
                else if (target is RichTextBlock r) r.SelectAll();
            }));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var whole = wholeText();
        var copyWhole = Item("复制整段", "\uE8A5", null, () => Copy(whole!));
        copyWhole.IsEnabled = !string.IsNullOrEmpty(whole);
        menu.Items.Add(copyWhole);
        if (target is TextBlock code && CodeSources.TryGetValue(code, out var source))
            menu.Items.Add(Item("复制代码", "\uE943", null, () => Copy(source)));

        if (rollback?.Invoke() is { } state && rollbackRequested is not null && (state.UndoVisible || state.RedoVisible))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            if (state.UndoVisible)
            {
                var undo = Item(state.UndoEnabled || state.UndoHint is null ? "撤回" : "撤回（" + state.UndoHint + "）",
                    "\uE7A7", "/undo", () => rollbackRequested(true));
                undo.IsEnabled = state.UndoEnabled;
                ToolTipService.SetToolTip(undo, "撤回这条消息及之后的回答，并把它放回输入框");
                menu.Items.Add(undo);
            }
            if (state.RedoVisible)
            {
                var redo = Item("恢复撤回的消息", "\uE7A6", "/redo", () => rollbackRequested(false));
                redo.IsEnabled = state.RedoEnabled;
                menu.Items.Add(redo);
            }
        }
    }

    private static MenuFlyoutItem Item(string text, string glyph, string? accelerator, Action invoke)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        if (accelerator is not null) item.KeyboardAcceleratorTextOverride = accelerator;
        item.Click += (_, _) => invoke();
        return item;
    }
}
