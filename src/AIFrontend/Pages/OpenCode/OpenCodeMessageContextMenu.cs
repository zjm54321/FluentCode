using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// Transcript message context menu in the app's shared shape (<see cref="OpenCodeContextMenu"/>): list
/// 复制整段 · 复制代码 | 撤回 · 恢复撤回, bottom row 复制 · 全选. Right-click, menu key and touch press-and-hold all
/// open this one flyout; it is rebuilt on Opening so selection and undo state are never stale.
/// </summary>
internal static class OpenCodeMessageContextMenu
{
    private static readonly ConditionalWeakTable<TextBlock, string> CodeSources = new();

    /// <summary>Marks a rendered code-block text so the menu offers 复制代码 with its exact source.</summary>
    internal static void MarkCode(TextBlock code, string source) => CodeSources.AddOrUpdate(code, source);

    /// <summary>
    /// 复制 · 全选 (+ 复制代码) for rendered Markdown that is not a transcript message (reasoning, tool details).
    /// Replaces the platform TextCommandBarFlyout so every text menu looks the same. One per presenter: a flyout
    /// must not be shared across windows (XamlRoots).
    /// </summary>
    internal static Flyout CreateTextOnly() => Create(null);

    internal static Flyout Create(Func<string?>? wholeText, Func<OpenCodeRollbackMenuState?>? rollback = null,
        Action<bool>? rollbackRequested = null, Func<IEnumerable<OpenCodeContextCommand>>? actions = null)
        => OpenCodeContextMenu.Create(flyout =>
        {
            var commands = Commands(flyout.Target, wholeText, rollback, rollbackRequested);
            if (actions?.Invoke() is { } extra) { commands.Add(null); commands.AddRange(extra); }
            return commands;
        });

    /// <summary>
    /// Replaces the default TextCommandBarFlyout of every selectable text in <paramref name="root"/> with
    /// <paramref name="menu"/> (the copy / select-all commands are re-offered inside it). Idempotent.
    /// </summary>
    internal static void Apply(DependencyObject? root, FlyoutBase menu)
    {
        switch (root)
        {
            case null: return;
            // One menu per gesture: the platform selection bar (TextCommandBarFlyout) would otherwise appear next to
            // this menu on touch/pen press-and-hold. 复制 / 全选 live in the menu, so the bar is redundant.
            case TextBlock { IsTextSelectionEnabled: true } text: text.ContextFlyout = menu; text.SelectionFlyout = null; return;
            case RichTextBlock rich:
                if (rich.IsTextSelectionEnabled) { rich.ContextFlyout = menu; rich.SelectionFlyout = null; }
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

    private static FrameworkElement? FirstText(DependencyObject? root)
    {
        switch (root)
        {
            case TextBlock { IsTextSelectionEnabled: true } text: return text;
            case RichTextBlock { IsTextSelectionEnabled: true } rich: return rich;
            case null: return null;
        }
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FirstText(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    internal static List<OpenCodeContextCommand?> Commands(FrameworkElement? target, Func<string?>? wholeText,
        Func<OpenCodeRollbackMenuState?>? rollback, Action<bool>? rollbackRequested)
    {
        var commands = new List<OpenCodeContextCommand?>();
        // Opened on the bubble/card padding: the row still acts on that message's text, so every message menu
        // has the same bottom row.
        if (target is not (TextBlock or RichTextBlock)) target = FirstText(target) ?? target;
        string? selected = target switch { TextBlock t => t.SelectedText, RichTextBlock r => r.SelectedText, _ => null };
        var whole = wholeText?.Invoke();
        if (target is TextBlock { IsTextSelectionEnabled: true } or RichTextBlock { IsTextSelectionEnabled: true })
        {
            // 复制 copies the selection; with nothing selected it copies the whole message (like 复制整段).
            var copyText = !string.IsNullOrEmpty(selected) ? selected : whole;
            commands.Add(new("复制", "\uE8C8", () => Copy(copyText!))
                { Row = true, Accelerator = "Ctrl+C", Enabled = !string.IsNullOrEmpty(copyText), Description = "先选中文字" });
            commands.Add(new("全选", "\uE8B3", () =>
            {
                if (target is TextBlock t) t.SelectAll();
                else if (target is RichTextBlock r) r.SelectAll();
            }) { Row = true, Accelerator = "Ctrl+A" });
        }
        if (wholeText is not null)
            commands.Add(new("复制整段", "\uE8A5", () => Copy(whole!)) { Enabled = !string.IsNullOrEmpty(whole) });
        if (target is TextBlock code && CodeSources.TryGetValue(code, out var source))
            commands.Add(new("复制代码", "\uE943", () => Copy(source)));
        if (rollback?.Invoke() is { } state && rollbackRequested is not null)
        {
            commands.Add(null);
            if (state.UndoVisible)
                commands.Add(new("撤回", "\uE7A7", () => rollbackRequested(true))
                {
                    Enabled = state.UndoEnabled,
                    Description = state.UndoEnabled || state.UndoHint is null
                        ? "撤回这条消息及之后的回答，并把它放回输入框" : "暂不可用：" + state.UndoHint,
                });
            if (state.RedoVisible)
                commands.Add(new("恢复撤回", "\uE7A7", () => rollbackRequested(false)) { Enabled = state.RedoEnabled, Mirrored = true });
        }
        return commands;
    }
}
