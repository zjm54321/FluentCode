using System;
using System.Linq;
using System.Threading.Tasks;
using AIFrontend.Services.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AIFrontend.Pages.OpenCode;

// Native builtin dialogs require the page's XamlRoot (no Window.Current), hold no client/lease/gate/binding,
// and return a typed result the page re-validates. Only model favorites perform preference IO.

internal static class OpenCodeModelPickerDialog
{
    // Legacy optional parameters are retained for the existing page callers; there is no refresh UI.
    internal static async Task<OpenCodeModelPickerResult> ShowAsync(XamlRoot? owner, OpenCodeModelPickerPresentation presentation,
        Func<Task<OpenCodeModelPickerPresentation?>>? refresh = null, string? sourceKey = null, bool allowStrengthSelection = true,
        FrameworkElement? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        anchor = OpenCodePickerFlyout.Anchor(owner, anchor, "OpenCodeSidebarModel");
        if (owner is null || anchor is null) return new(OpenCodeModelPickerOutcome.OwnerUnavailable, null, null);
        var cancelled = new OpenCodeModelPickerResult(OpenCodeModelPickerOutcome.Cancelled, null, null);
        var state = new OpenCodeModelPickerState(presentation);
        var favoriteStore = new OpenCodeModelFavoriteStore(sourceKey ?? string.Empty);
        string? favoriteError = null;
        try { await favoriteStore.LoadAsync(); }
        catch (Exception) { favoriteError = "收藏设置未能读取；更改收藏时将再次尝试保存。"; }
        if (anchor.XamlRoot != owner || !anchor.IsLoaded) return cancelled;
        var favorites = favoriteStore.Favorites;
        var search = new AutoSuggestBox { PlaceholderText = "搜索模型或提供方", QueryIcon = new SymbolIcon(Symbol.Find) };
        AutomationProperties.SetAutomationId(search, "OpenCodeModelPickerSearch");
        AutomationProperties.SetName(search, "搜索模型");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            MaxHeight = ModelListMaxHeight(owner), Padding = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(list, "OpenCodeModelPickerList");
        AutomationProperties.SetName(list, "可选模型");
        var favoriteList = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            Padding = new Thickness(0, 4, 0, 4) };
        AutomationProperties.SetAutomationId(favoriteList, "OpenCodeModelPickerFavorites");
        AutomationProperties.SetName(favoriteList, "收藏模型");
        var favoriteSection = new Grid { RowSpacing = 4 };
        favoriteSection.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        favoriteSection.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        favoriteSection.Children.Add(OpenCodeInteractionUi.StyledText("收藏", OpenCodeInteractionUi.BodyStrongStyleKey));
        Grid.SetRow(favoriteList, 1);
        favoriteSection.Children.Add(favoriteList);
        var lists = new Grid { Height = ModelListMaxHeight(owner), RowSpacing = 8 };
        lists.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lists.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lists.Children.Add(favoriteSection); // Auto row stays pinned, outside the all-models scroller.
        Grid.SetRow(list, 1);
        lists.Children.Add(list);
        var body = new StackPanel { Spacing = 8, Width = ModelBodyWidth(owner), Padding = new Thickness(8) };
        body.Children.Add(OpenCodeInteractionUi.StyledText("选择模型", OpenCodeInteractionUi.BodyStrongStyleKey));
        body.Children.Add(search);
        var favoriteStatus = OpenCodeInteractionUi.Caption(favoriteError ?? string.Empty, 0.85);
        favoriteStatus.Visibility = favoriteError is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetLiveSetting(favoriteStatus, AutomationLiveSetting.Polite);
        body.Children.Add(favoriteStatus);
        body.Children.Add(lists);
        ToolTipService.SetToolTip(favoriteSection, favoriteStore.Status);
        var savingFavorite = false;
        async void ToggleFavorite(OpenCodeModelPickerRow row)
        {
            if (savingFavorite || state.IsClosed) return;
            savingFavorite = true;
            var value = favorites.Toggle(row);
            Render();
            try
            {
                await favoriteStore.SaveAsync(row, value);
                favoriteStatus.Text = favoriteStore.Status.StartsWith("无法", StringComparison.Ordinal) ? favoriteStore.Status : string.Empty;
            }
            catch (Exception)
            {
                favorites.Toggle(row);
                favoriteStatus.Text = "收藏未能保存，请重试。";
            }
            finally
            {
                savingFavorite = false;
                if (!state.IsClosed)
                {
                    favoriteStatus.Visibility = favoriteStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                    ToolTipService.SetToolTip(favoriteSection, favoriteStore.Status);
                    Render();
                }
            }
        }
        void Refit(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            body.Width = ModelBodyWidth(sender);
            list.MaxHeight = ModelListMaxHeight(sender);
            lists.Height = ModelListMaxHeight(sender);
            favoriteList.MaxHeight = Math.Max(34, lists.Height * 0.4 - 28);
            favoriteSection.MaxHeight = lists.Height * 0.4;
        }
        Refit(owner, null!);
        owner.Changed += Refit;
        void Render()
        {
            list.Items.Clear();
            favoriteList.Items.Clear();
            var filter = search.Text.Trim();
            var current = presentation.Current switch { OpenCodeModelCurrent.Explicit e => e.Key,
                OpenCodeModelCurrent.Inherited i => i.Default, _ => null };
            var sections = favorites.Filter(OpenCodeModelPresentation.Order(presentation.Rows, sourceKey), filter);
            foreach (var row in sections.Favorites)
            {
                var item = RowItem(row, favorites.Contains(row), !savingFavorite, () => ToggleFavorite(row));
                favoriteList.Items.Add(item);
                if (current is not null && row.ProviderId == current.ProviderId && row.ModelId == current.ModelId) favoriteList.SelectedItem = item;
            }
            if (favoriteList.Items.Count == 0)
                favoriteList.Items.Add(new ListViewItem { Content = filter.Length == 0 ? "尚无收藏模型" : "没有匹配的收藏模型", IsEnabled = false, IsTabStop = false });
            foreach (var provider in sections.All.GroupBy(row => row.ProviderId))
            {
                var label = OpenCodeInteractionUi.StyledText(provider.First().ProviderName ?? provider.Key,
                    OpenCodeInteractionUi.CaptionSubtleStyleKey);
                list.Items.Add(new ListViewItem { Content = label, IsEnabled = false, IsTabStop = false,
                    Padding = new Thickness(10, 12, 10, 4), MinHeight = 28 });
                foreach (var row in provider)
                {
                    var item = RowItem(row, favorites.Contains(row), !savingFavorite, () => ToggleFavorite(row));
                    list.Items.Add(item);
                    if (current is not null && row.ProviderId == current.ProviderId && row.ModelId == current.ModelId) list.SelectedItem = item;
                }
            }
            if (list.Items.Count == 0)
                list.Items.Add(new ListViewItem { Content = "没有匹配的模型。", IsEnabled = false, IsTabStop = false });
        }
        search.TextChanged += (_, _) => { if (!state.IsClosed) Render(); };
        search.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Down && e.Key != VirtualKey.Up) return;
            var rows = favoriteList.Items.OfType<ListViewItem>().Concat(list.Items.OfType<ListViewItem>())
                .Where(item => item.IsEnabled && item.Tag is OpenCodeModelPickerRow);
            var target = e.Key == VirtualKey.Down ? rows.FirstOrDefault() : rows.LastOrDefault();
            if (target is null) return;
            var targetList = favoriteList.Items.Contains(target) ? favoriteList : list;
            targetList.SelectedItem = target;
            targetList.ScrollIntoView(target);
            target.Focus(FocusState.Keyboard);
            e.Handled = true; // Native ListView arrows/Enter handle the rest; no custom selection renderer.
        };
        Render();
        try
        {
            return await OpenCodePickerFlyout.ShowAsync(anchor, body, cancelled, choose =>
            {
                void Select(object sender, ItemClickEventArgs e)
                {
                    if (e.ClickedItem is not ListViewItem { Tag: OpenCodeModelPickerRow row }
                        || !state.SelectModel(row.ProviderId, row.ModelId)) return;
                    // Re-selecting the current model must not silently reset its effort to default.
                    if (presentation.Current is OpenCodeModelCurrent.Explicit current
                        && current.Key.ProviderId == row.ProviderId && current.Key.ModelId == row.ModelId)
                        state.SelectVariant(current.Key.Variant);
                    choose(state.TryFreezeConfirmation() ?? cancelled);
                }
                list.ItemClick += Select;
                favoriteList.ItemClick += Select;
            }, () =>
                {
                    if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
                    search.Focus(FocusState.Programmatic);
                });
        }
        finally { state.Close(); owner.Changed -= Refit; }
    }

    private static double ModelBodyWidth(XamlRoot? owner) =>
        Math.Max(1, Math.Min(368, (owner?.Size.Width ?? 560) - 48));

    private static double ModelListMaxHeight(XamlRoot? owner) =>
        Math.Max(34, Math.Min(640, (owner?.Size.Height ?? 760) - 192));

    private static ListViewItem RowItem(OpenCodeModelPickerRow row, bool isFavorite, bool canToggle, Action toggleFavorite)
    {
        var grid = OpenCodeModelLogo.Label(row.ModelId, row.Name);
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(grid);
        var favorite = new ToggleButton { IsChecked = isFavorite, IsEnabled = canToggle, MinWidth = 32, MinHeight = 28,
            Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = isFavorite ? "\uE735" : "\uE734", FontSize = 16 } };
        if (isFavorite && Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out var accent)
            && accent is Brush brush) ((FontIcon)favorite.Content).Foreground = brush;
        var favoriteName = isFavorite ? "取消收藏" : "收藏";
        ToolTipService.SetToolTip(favorite, favoriteName);
        AutomationProperties.SetAutomationId(favorite, "OpenCodeModelFavorite:" + row.ProviderId + "/" + row.ModelId);
        AutomationProperties.SetName(favorite, favoriteName + " " + row.Name);
        // The native button consumes its gesture before the containing ListViewItem can invoke.
        favorite.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        favorite.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => e.Handled = true), true);
        favorite.KeyDown += (_, e) => { if (e.Key is VirtualKey.Enter or VirtualKey.Space) e.Handled = true; };
        favorite.Click += (_, _) => toggleFavorite(); // Never selects a model or calls choose/flyout.Hide.
        Grid.SetColumn(favorite, 1);
        content.Children.Add(favorite);
        var item = new ListViewItem { Content = content, Tag = row, IsEnabled = row.Available, MinHeight = 34,
            Padding = new Thickness(10, 4, 10, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, row.Name + (row.Available ? string.Empty : "，不可用"));
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
    internal int SelectionStart { get; private set; }
    internal int SelectionLength { get; private set; }

    /// <param name="initialText">The authoritative draft text read by the page AFTER flushing the main editor.</param>
    /// <param name="apply">Page seam: re-check ownership, <c>TryApplyClientText(ticket, text)</c>, render the main editor.</param>
    internal async Task<OpenCodeDraftEditorResult> ShowAsync(XamlRoot? owner, string initialText,
        Func<string, OpenCodeDraftEditorApplyOutcome> apply, int selectionStart = 0, int selectionLength = 0)
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
        SelectionStart = Math.Clamp(selectionStart, 0, initialText.Length);
        SelectionLength = Math.Clamp(selectionLength, 0, initialText.Length - SelectionStart);
        box.Select(SelectionStart, SelectionLength);
        box.SelectionChanged += (_, _) => { SelectionStart = box.SelectionStart; SelectionLength = box.SelectionLength; };
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
