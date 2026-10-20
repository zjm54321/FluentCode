using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

// Native builtin dialogs. Each requires the owning XamlRoot supplied by the page (no Window.Current), performs no IO,
// holds no client/lease/gate/binding, and returns a typed result the page re-validates after the dialog closes.

internal static class OpenCodeModelPickerDialog
{
    /// <param name="refresh">Optional read-only re-read, run only on an explicit click (single flight, never looped).
    /// Returning null keeps the current presentation.</param>
    internal static async Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot? owner, OpenCodeModelPickerPresentation presentation,
        Func<Task<OpenCodeModelPickerPresentation?>>? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (owner is null) return new(OpenCodeModelPickerOutcome.OwnerUnavailable, null, null);
        var state = new OpenCodeModelPickerState(presentation);
        // Redesign §7 (look only): search box, rows grouped by provider, current-model check mark, variants as a
        // horizontal radio group. Selection/confirmation semantics remain entirely in OpenCodeModelPickerState.
        var current = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        var note = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionStyleKey);
        var search = new TextBox { PlaceholderText = "搜索模型或提供方" };
        AutomationProperties.SetAutomationId(search, "OpenCodeModelPickerSearch");
        AutomationProperties.SetName(search, "搜索模型");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 340 };
        AutomationProperties.SetAutomationId(list, "OpenCodeModelPickerList");
        AutomationProperties.SetName(list, "可选模型");
        var variantLabel = OpenCodeInteractionUi.StyledText("变体", OpenCodeInteractionUi.BodyStrongStyleKey);
        var variants = new RadioButtons();
        AutomationProperties.SetAutomationId(variants, "OpenCodeModelPickerVariant");
        AutomationProperties.SetName(variants, "模型变体");
        var variantRow = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        variantRow.Children.Add(variantLabel);
        variantRow.Children.Add(variants);
        var refreshButton = new HyperlinkButton { Content = "重新读取模型列表", Padding = new Thickness(0), Visibility = refresh is null ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAutomationId(refreshButton, "OpenCodeModelPickerRefresh");
        var body = new StackPanel { Spacing = 10, MinWidth = 380, MaxWidth = 540 };
        body.Children.Add(current);
        body.Children.Add(search);
        body.Children.Add(list);
        body.Children.Add(variantRow);
        body.Children.Add(note);
        body.Children.Add(OpenCodeInteractionUi.StyledText("只对之后的回合生效，不影响已发送的内容。点“确认更改”前会重新检查一次。", OpenCodeInteractionUi.CaptionSubtleStyleKey));
        body.Children.Add(refreshButton);
        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = "选择模型",
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "确认更改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None, // Enter in the list never confirms by accident.
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        var rendering = false;

        void RenderVariants()
        {
            rendering = true;
            try
            {
                variants.Items.Clear();
                var row = state.SelectedRow;
                variantRow.Visibility = row is { Variants.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
                if (row is null) return;
                variants.Items.Add(new RadioButton { Content = "默认", Tag = null });
                foreach (var v in row.Variants) variants.Items.Add(new RadioButton { Content = v, Tag = v });
                variants.MaxColumns = Math.Max(1, variants.Items.Count); // Horizontal: one row of choices.
                var index = 0;
                for (var i = 1; i < variants.Items.Count; i++)
                    if (variants.Items[i] is RadioButton { Tag: string tag } && tag == state.SelectedVariant) index = i;
                variants.SelectedIndex = index;
            }
            finally { rendering = false; }
        }

        void RenderNote()
        {
            var p = state.Presentation;
            note.Text = state.ChoiceWithdrawnNotice ?? p.Note ?? (p.Rows.Count == 0 ? "没有读取到可选模型。" : string.Empty);
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Render()
        {
            rendering = true;
            try
            {
                var p = state.Presentation;
                current.Text = OpenCodeModelPickerState.Describe(p.Current, p.Rows);
                RenderNote();
                list.Items.Clear();
                var filter = search.Text.Trim();
                string? provider = null;
                foreach (var row in p.Rows)
                {
                    // Display filter only: a hidden row is not deselected in the state (confirmation is unaffected).
                    if (filter.Length > 0 && !(row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                        || row.ModelId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (row.ProviderName ?? row.ProviderId).Contains(filter, StringComparison.CurrentCultureIgnoreCase))) continue;
                    if (!string.Equals(provider, row.ProviderId, StringComparison.Ordinal))
                    {
                        provider = row.ProviderId;
                        list.Items.Add(GroupHeader(row.ProviderName ?? row.ProviderId));
                    }
                    list.Items.Add(RowItem(row, p.Current));
                }
                if (list.Items.Count == 0)
                    list.Items.Add(new ListViewItem { Content = OpenCodeInteractionUi.StyledText(p.Rows.Count == 0 ? "没有可选模型。" : "没有匹配的模型。", OpenCodeInteractionUi.CaptionSubtleStyleKey), IsEnabled = false, IsTabStop = false });
                list.SelectedIndex = -1;
                for (var i = 0; i < list.Items.Count; i++)
                    if (state.SelectedRow is { } s && list.Items[i] is ListViewItem { Tag: OpenCodeModelPickerRow r }
                        && r.ProviderId == s.ProviderId && r.ModelId == s.ModelId) list.SelectedIndex = i;
            }
            finally { rendering = false; }
            RenderVariants();
            dialog.IsPrimaryButtonEnabled = state.CanConfirm;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (rendering || state.IsClosed) return;
            if (list.SelectedItem is ListViewItem { Tag: OpenCodeModelPickerRow row } && state.SelectModel(row.ProviderId, row.ModelId))
            {
                RenderNote(); // A fresh explicit choice clears the "choice withdrawn" notice.
                RenderVariants();
            }
            else if (list.SelectedItem is not null) list.SelectedIndex = -1; // Unavailable rows cannot be chosen.
            dialog.IsPrimaryButtonEnabled = state.CanConfirm;
        };
        variants.SelectionChanged += (_, _) =>
        {
            if (rendering || state.IsClosed) return;
            if (variants.SelectedItem is RadioButton item) state.SelectVariant(item.Tag as string);
            dialog.IsPrimaryButtonEnabled = state.CanConfirm;
        };
        search.TextChanged += (_, _) =>
        {
            if (state.IsClosed) return;
            Render();
            if (refresh is not null && !refreshButton.IsEnabled) dialog.IsPrimaryButtonEnabled = false; // Refresh outstanding.
        };
        refreshButton.Click += async (_, _) =>
        {
            if (refresh is null || state.BeginRefresh() is not { } ticket) return;
            refreshButton.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false; // No confirmation while a refresh is outstanding.
            OpenCodeModelPickerPresentation? updated = null;
            var failed = false;
            try { updated = await refresh(); }
            catch (Exception) { failed = true; }
            // Fence: a completion for a closed dialog or a superseded ticket touches NO state and NO control.
            if (!state.CompleteRefresh(ticket, failed ? null : updated)) return;
            refreshButton.IsEnabled = true;
            Render();
            if (failed) { note.Text = "重新读取失败；列表保持不变。"; note.Visibility = Visibility.Visible; }
        };
        // The result is frozen at the actual primary gesture; a refresh that completes later cannot retarget it.
        OpenCodeModelPickerResult? frozen = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            frozen = state.TryFreezeConfirmation();
            if (frozen is null) args.Cancel = true; // Refresh outstanding / nothing confirmable: stay open, no result.
        };
        Render();
        try
        {
            var result = await dialog.ShowAsync();
            state.Close();
            return result == ContentDialogResult.Primary && frozen is { } confirmed
                ? confirmed : new(OpenCodeModelPickerOutcome.Cancelled, null, null);
        }
        catch (Exception)
        {
            state.Close();
            return new(OpenCodeModelPickerOutcome.OwnerUnavailable, null, null); // e.g. another dialog already open.
        }
    }

    private static ListViewItem GroupHeader(string provider)
    {
        var header = OpenCodeInteractionUi.StyledText(provider, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        header.Margin = new Thickness(0, 8, 0, 0);
        var item = new ListViewItem { Content = header, IsEnabled = false, IsTabStop = false, MinHeight = 28 };
        AutomationProperties.SetName(item, "提供方：" + provider);
        return item;
    }

    private static ListViewItem RowItem(OpenCodeModelPickerRow row, OpenCodeModelCurrent current)
    {
        var isCurrent = current is OpenCodeModelCurrent.Explicit e && e.Key.ProviderId == row.ProviderId && e.Key.ModelId == row.ModelId;
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var check = new FontIcon { Glyph = "\uE73E", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0),
            Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed };
        grid.Children.Add(check);
        var stack = new StackPanel { Spacing = 1 };
        var title = OpenCodeInteractionUi.StyledText(row.Name.Length > 0 ? row.Name : row.ModelId, OpenCodeInteractionUi.BodyStrongStyleKey);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(title);
        var detail = row.ModelId
            + (row.Variants.Count > 0 ? $" · {row.Variants.Count} 个变体" : string.Empty)
            + (isCurrent ? " · 当前" : string.Empty)
            + (row.Detail is { Length: > 0 } d ? " · " + d : string.Empty)
            + (row.Available ? string.Empty : " · 不可用");
        stack.Children.Add(OpenCodeInteractionUi.StyledText(detail, OpenCodeInteractionUi.CaptionSubtleStyleKey));
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        // Unavailable rows stay visible (and announced) but are disabled; selection is still rejected by the state.
        var item = new ListViewItem { Content = grid, Tag = row, IsEnabled = row.Available };
        AutomationProperties.SetName(item, $"{title.Text}，{row.ProviderName ?? row.ProviderId} · {detail}");
        return item;
    }
}

internal static class OpenCodeRollbackConfirmationDialog
{
    internal static async Task<OpenCodeRollbackConfirmationResult> ShowAsync(XamlRoot? owner, OpenCodeRollbackPreviewPresentation preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (owner is null) return new(OpenCodeRollbackOutcome.OwnerUnavailable, null, false, false);
        var state = new OpenCodeRollbackConfirmationState(preview);
        var undo = preview.Kind == OpenCodeRollbackKind.Undo;
        var body = new StackPanel { Spacing = 10, MinWidth = 360, MaxWidth = 540 };
        body.Children.Add(OpenCodeInteractionUi.Caption(preview.SessionLabel, 0.75));
        if (undo && !string.IsNullOrEmpty(preview.TargetText))
        {
            body.Children.Add(OpenCodeInteractionUi.Caption("将放回输入框的文字" + (preview.TargetLabel is { } l ? "（" + l + "）" : string.Empty) + "：", 0.85));
            var excerpt = new TextBlock
            {
                Text = preview.TargetText,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 8,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true,
            };
            body.Children.Add(new Border
            {
                Child = excerpt,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Style = OpenCodeInteractionUi.TryStyle("OpenCodeSubtleCardChromeStyle"),
            });
        }
        var warnings = new StackPanel { Spacing = 4 };
        foreach (var w in state.Warnings())
        {
            var line = new Grid { ColumnSpacing = 6 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = "\uE7BA", FontSize = 12, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                Style = OpenCodeInteractionUi.TryStyle("OpenCodeCautionIconStyle") };
            var text = OpenCodeInteractionUi.Caption(w, 0.85);
            Grid.SetColumn(text, 1);
            line.Children.Add(icon);
            line.Children.Add(text);
            warnings.Children.Add(line);
        }
        AutomationProperties.SetName(warnings, "风险说明");
        body.Children.Add(warnings);
        var dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = undo ? "撤回最近一条消息？" : "恢复已撤回的消息？",
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = undo ? "撤回" : "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, // A risky action is never the default.
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        if (state.OffersFileRestoration)
        {
            var files = new CheckBox { Content = "同时恢复文件（使用服务器快照；默认不恢复）", IsChecked = false };
            AutomationProperties.SetAutomationId(files, "OpenCodeRollbackRestoreFiles");
            files.Checked += (_, _) => state.SetRestoreFiles(true);
            files.Unchecked += (_, _) => state.SetRestoreFiles(false);
            body.Children.Add(files);
        }
        if (state.RequiresTextOnlyAcknowledgment)
        {
            var ack = new CheckBox { Content = "我知道只会恢复文字，引用的文件、智能体或技能不会恢复", IsChecked = false };
            AutomationProperties.SetAutomationId(ack, "OpenCodeRollbackTextOnly");
            ack.Checked += (_, _) => { state.SetTextOnlyAcknowledged(true); dialog.IsPrimaryButtonEnabled = state.CanConfirm; };
            ack.Unchecked += (_, _) => { state.SetTextOnlyAcknowledged(false); dialog.IsPrimaryButtonEnabled = state.CanConfirm; };
            body.Children.Add(ack);
        }
        if (state.BlockingReason is { } reason)
        {
            var blocked = OpenCodeInteractionUi.Caption(reason, 0.9);
            blocked.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.QuestionCautionStyleKey);
            body.Children.Add(blocked);
        }
        dialog.IsPrimaryButtonEnabled = state.CanConfirm;
        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? state.Confirm() : new(OpenCodeRollbackOutcome.Cancelled, null, false, false);
        }
        catch (Exception)
        {
            return new(OpenCodeRollbackOutcome.OwnerUnavailable, null, false, false);
        }
    }
}

/// <summary>
/// AIFrontend's expanded draft editor: a detached buffer, NOT a remote VISUAL/EDITOR and no external process. Apply calls
/// the page's exact-ticket seam (which uses <c>TryApplyClientText</c> and the main editor's guarded reconciliation) and
/// never sends. Cancel discards only the buffer. <see cref="MarkStale"/> (page: source/session/draft changed) disables
/// Apply for good and keeps the text visible and copyable; nothing revives the old ticket.
/// </summary>
internal sealed class OpenCodeDraftEditorDialog
{
    private OpenCodeDraftEditorSession? _session;
    private ContentDialog? _dialog;
    private TextBox? _box;
    private TextBlock? _staleText;
    private bool _allowClose;

    internal bool IsOpen => _dialog is not null;

    /// <param name="initialText">The authoritative draft text read by the page AFTER flushing the main editor.</param>
    /// <param name="apply">Page seam: re-check ownership, <c>TryApplyClientText(ticket, text)</c>, render the main editor.</param>
    internal async Task<OpenCodeDraftEditorResult> ShowAsync(XamlRoot? owner, string initialText,
        Func<string, OpenCodeDraftEditorApplyOutcome> apply)
    {
        ArgumentNullException.ThrowIfNull(initialText);
        ArgumentNullException.ThrowIfNull(apply);
        if (owner is null || _dialog is not null) return new(OpenCodeDraftEditorOutcome.OwnerUnavailable, null);
        _applied = _allowClose = false;
        var session = _session = new OpenCodeDraftEditorSession(initialText);
        var box = _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 260,
            MaxHeight = 460,
            MinWidth = 420,
            FontFamily = AppFonts.Monospace,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(box, "OpenCodeDraftEditorBuffer");
        AutomationProperties.SetName(box, "草稿内容");
        session.Write(() => box.Text = initialText); // Initialization echo is not a user edit.
        box.TextChanging += (sender, _) => { session.OnTextChanging(sender.Text); Update(); };
        box.TextCompositionStarted += (_, _) => { session.OnCompositionStarted(); Update(); };
        box.TextCompositionEnded += (_, _) => { session.OnCompositionEnded(DateTimeOffset.UtcNow); Update(); };
        box.LostFocus += (_, _) => { if (session.Composing) { session.OnCompositionEnded(DateTimeOffset.UtcNow); Update(); } };
        box.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+Enter applies (never sends); plain Enter is a newline; IME confirmation never applies.
            if (e.Key != VirtualKey.Enter || !IsControlDown()) return;
            e.Handled = true;
            if (session.KeyboardApplyAllowed(DateTimeOffset.UtcNow)) TryApply(apply);
        };
        var staleText = _staleText = OpenCodeInteractionUi.Caption(string.Empty, 0.95);
        staleText.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.QuestionCautionStyleKey);
        staleText.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(staleText, AutomationLiveSetting.Assertive);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(OpenCodeInteractionUi.Caption("这是 AIFrontend 内置的编辑框，不会打开外部编辑器。只修改输入框中的文字；已选命令和附件保持不变。"
            + "“应用到输入框”不会发送。Ctrl+Enter 应用，Enter 换行。", 0.75));
        body.Children.Add(box);
        body.Children.Add(staleText);
        var dialog = _dialog = new ContentDialog
        {
            XamlRoot = owner,
            Title = "编辑草稿",
            Content = body,
            PrimaryButtonText = "应用到输入框",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            Style = OpenCodeInteractionUi.TryStyle("DefaultContentDialogStyle"),
        };
        var outcome = OpenCodeDraftEditorOutcome.Cancelled;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // Close only after a successful apply; a stale result keeps the text on screen.
            TryApply(apply);
        };
        dialog.CloseButtonClick += (_, _) => _allowClose = true; // 取消/关闭 is the explicit way out (buffer discarded).
        dialog.Closing += (_, args) =>
        {
            if (_allowClose) return;
            // Esc/back while composing, or with an unsaved buffer, does not throw the text away: use 取消/关闭.
            if (args.Result == ContentDialogResult.None && (session.Composing || session.Dirty || session.Stale)) args.Cancel = true;
        };
        Update();
        try
        {
            var result = await dialog.ShowAsync();
            if (session.Stale) outcome = OpenCodeDraftEditorOutcome.ClosedStale;
            else if (_applied) outcome = OpenCodeDraftEditorOutcome.Applied;
            else outcome = result == ContentDialogResult.Primary ? OpenCodeDraftEditorOutcome.Applied : OpenCodeDraftEditorOutcome.Cancelled;
        }
        catch (Exception)
        {
            outcome = OpenCodeDraftEditorOutcome.OwnerUnavailable;
        }
        finally
        {
            _dialog = null;
            _box = null;
            _staleText = null;
        }
        return new(outcome, outcome is OpenCodeDraftEditorOutcome.ClosedStale or OpenCodeDraftEditorOutcome.OwnerUnavailable
            ? session.Buffer : null);
    }

    private bool _applied;

    /// <summary>UI thread. Page detected a source/session/draft change while the editor is open.</summary>
    internal void MarkStale(string? reason = null)
    {
        if (_session is null) return;
        _session.MarkStale(reason ?? "会话或草稿已变化，不能再应用这段文字。文字仍保留在这里，可以复制后关闭。");
        Update();
    }

    private void TryApply(Func<string, OpenCodeDraftEditorApplyOutcome> apply)
    {
        if (_session is not { } session || _dialog is not { } dialog) return;
        var outcome = session.Apply(apply);
        if (outcome == OpenCodeDraftEditorApplyOutcome.Applied)
        {
            _applied = true;
            _allowClose = true;
            dialog.Hide();
            return;
        }
        Update();
    }

    private void Update()
    {
        if (_session is not { } session || _dialog is not { } dialog) return;
        dialog.IsPrimaryButtonEnabled = session.CanApply;
        if (session.Stale)
        {
            dialog.CloseButtonText = "关闭";
            if (_box is { } box) box.IsReadOnly = true; // Still selectable/copyable.
            if (_staleText is { } t) { t.Text = session.StaleReason ?? string.Empty; t.Visibility = Visibility.Visible; }
        }
    }

    private static bool IsControlDown()
    {
        try
        {
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }
        catch { return false; }
    }
}
