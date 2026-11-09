using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 提问遮罩（重设计 wave3 F9）：与权限遮罩同一位置——输入区同一单元格内、盖住输入卡片，一次显示一个待回答提问。
/// 取代原 ContentDialog 回答窗口（OpenCodeFormAnswerDialog 已退役）。
/// </summary>
/// <remarks>
/// <para>
/// 遮罩只托管条目复用的 <see cref="OpenCodeFormAnswerEditor"/>（输入在条目生命周期内保留）；状态机、指纹、
/// 回调仍归 <see cref="OpenCodeFormCard"/>。提交只经 <see cref="OpenCodeFormCard.TryValidateAnswer"/> +
/// <see cref="OpenCodeFormCard.SubmitValidatedAnswer"/>，页面照旧重新 GetFormAsync、比对指纹、单飞提交、
/// 结果未知进入阻断态（遮罩只镜像状态并转发显式“重新查询 / 必要确认”，绝不自动重发）。
/// </para>
/// <para>
/// 只收可回答（有编辑器）的提问；“暂不支持”的提问仍只在时间线条目里提供“不回答”，不遮住输入框。
/// 显示期间提问内容被更新：载入新内容但禁用提交，直到用户点“继续作答”确认已看到新内容（等同原对话框“关闭后重新打开”）。
/// </para>
/// <para>
/// 外观：原生 Fluent——底层不透明 <c>SolidBackgroundFillColorBaseBrush</c>，卡片 <c>CardBackgroundFillColorDefaultBrush</c>
/// + <c>CardStrokeColorDefaultBrush</c>、<c>OverlayCornerRadius</c>，顶部 2px 强调色细线；标题 Subtitle、题目 BodyStrong、
/// 说明 Body/Caption；RadioButtons / CheckBox / TextBox；多题用“上一个问题 / 下一个问题”逐题显示 + “第 1 / 3 题”；
/// “提交”AccentButtonStyle + “不回答”。全部经 <c>{ThemeResource}</c> / 内置样式，随浅色/深色/高对比度切换。
/// Tab 焦点在遮罩内循环；Ctrl+Enter 提交（输入法组合中不触发）；Esc 在“确认不回答”态返回。
/// </para>
/// </remarks>
internal sealed class OpenCodeQuestionOverlay : Grid
{
    private readonly List<OpenCodeFormCard> _cards = new();
    private readonly ContentControl _presenter;
    private readonly FontIcon? _icon;
    private readonly TextBlock _titleText;
    private readonly TextBlock _positionText;
    private readonly TextBlock _summaryText;
    private readonly InfoBar _staleBar;
    private readonly Button _reloadButton;
    private readonly InfoBar _busyBar;
    private readonly InfoBar _blockedBar;
    private readonly Button _recheckButton;
    private readonly InfoBar _resolveBar;
    private readonly Button _resolveRetryButton;
    private readonly Button _resolveSettledButton;
    private readonly InfoBar _errorBar;
    private readonly ScrollViewer _editorScroll;
    private readonly ContentControl _editorHost;
    private readonly TextBlock _confirmText;
    private readonly StackPanel _pagerPanel;
    private readonly Button _previousButton;
    private readonly TextBlock _pageText;
    private readonly ProgressRing _progress;
    private readonly Button _submitButton;
    private readonly Button _cancelButton;
    private readonly Button _confirmButton;
    private readonly Button _backButton;

    private OpenCodeFormCard? _current;
    private OpenCodeFormAnswerEditor? _editor;
    private string? _pinnedId;
    private int _acceptedRevision;
    private bool _confirming;
    private long _focusEpoch;

    internal OpenCodeQuestionOverlay()
    {
        AutomationProperties.SetAutomationId(this, "OpenCodeQuestionOverlay");
        AutomationProperties.SetName(this, "助手在等你回答");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Custom);
        AutomationProperties.SetLocalizedLandmarkType(this, "提问");
        TabFocusNavigation = KeyboardNavigationMode.Cycle;
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

        // 底层：不透明底色，完全盖住输入卡片（与权限遮罩相同）。
        var baseLayer = OpenCodeThemedXaml.Load<Border>("Border",
            "Background=\"{ThemeResource SolidBackgroundFillColorBaseBrush}\" CornerRadius=\"{ThemeResource OverlayCornerRadius}\"")
            ?? new Border { CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetAccessibilityView(baseLayer, AccessibilityView.Raw);
        Children.Add(baseLayer);

        // ---- 标题行：(?) {提问标题} …………… 1 / 2 ----
        _icon = OpenCodeThemedXaml.Load<FontIcon>("FontIcon",
            "Glyph=\"&#xE9CE;\" FontSize=\"18\" VerticalAlignment=\"Center\" Foreground=\"{ThemeResource AccentTextFillColorPrimaryBrush}\"");
        if (_icon is not null)
        {
            AutomationProperties.SetAccessibilityView(_icon, AccessibilityView.Raw);
        }

        _titleText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.SubtitleStyleKey, selectable: true);
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        _titleText.TextWrapping = TextWrapping.Wrap;
        _titleText.MaxLines = 2;
        _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        _positionText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _positionText.VerticalAlignment = VerticalAlignment.Center;
        _positionText.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAutomationId(_positionText, "OpenCodeQuestionPosition");
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (_icon is not null)
        {
            Place(header, _icon, 0);
        }

        Place(header, _titleText, 1);
        Place(header, _positionText, 2);

        _summaryText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);

        // ---- 状态提示（原生 InfoBar）：提问已更新 / 正在提交 / 结果未知 / 必要确认 / 错误 ----
        _reloadButton = new Button { Content = "继续作答" };
        _reloadButton.Click += (_, _) => AcceptLatestRevision();
        _staleBar = CreateBar(InfoBarSeverity.Warning, "提问已更新");
        _staleBar.Message = "提问内容已变化，已载入最新内容并重置输入。请检查后点“继续作答”。";
        _staleBar.ActionButton = _reloadButton;

        _busyBar = CreateBar(InfoBarSeverity.Informational, "正在处理");

        _recheckButton = new Button { Content = "重新查询状态" };
        _recheckButton.Click += (_, _) => _current?.RequestRecheck();
        _blockedBar = CreateBar(InfoBarSeverity.Warning, "结果未知");
        _blockedBar.ActionButton = _recheckButton;

        _resolveRetryButton = new Button { Content = "仍要重新决定" };
        _resolveRetryButton.Click += (_, _) => _current?.ResolveUncertain(retry: true);
        _resolveSettledButton = new Button { Content = "视为已结算" };
        _resolveSettledButton.Click += (_, _) => _current?.ResolveUncertain(retry: false);
        var resolveButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        resolveButtons.Children.Add(_resolveRetryButton);
        resolveButtons.Children.Add(_resolveSettledButton);
        _resolveBar = CreateBar(InfoBarSeverity.Warning, "需要你确认");
        _resolveBar.Content = resolveButtons;

        _errorBar = CreateBar(InfoBarSeverity.Error, "未能提交");

        // ---- 题目（条目复用的编辑器；过高时在遮罩内滚动，不撑开整页） ----
        _editorHost = new ContentControl
        {
            IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _editorScroll = new ScrollViewer
        {
            Content = _editorHost,
            MaxHeight = 340,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0),
        };

        _confirmText = OpenCodeInteractionUi.StyledText("确定不回答？助手会收到“已取消”。", OpenCodeInteractionUi.BodyStyleKey);
        _confirmText.Visibility = Visibility.Collapsed;

        // ---- 请求内向导：上一个问题 / 下一个问题；只有末题可以提交整份回答 ----
        _previousButton = new Button { Content = "上一个问题", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        _previousButton.Click += (_, _) => MoveQuestion(-1);
        _pageText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _pageText.VerticalAlignment = VerticalAlignment.Center;
        _pageText.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetLiveSetting(_pageText, AutomationLiveSetting.Polite);
        _pagerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        _pagerPanel.Children.Add(_previousButton);

        _progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_progress, "正在处理");

        _submitButton = new Button { Content = "提交", MinWidth = 96 };
        _submitButton.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle");
        _submitButton.Click += (_, _) => HandlePrimaryAction();
        _cancelButton = new Button { Content = "不回答", MinWidth = 88 };
        _cancelButton.Click += (_, _) => BeginCancel();
        ToolTipService.SetToolTip(_cancelButton, "跳过");
        _confirmButton = new Button { Content = "确认不回答", MinWidth = 96, Visibility = Visibility.Collapsed };
        _confirmButton.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle");
        _confirmButton.Click += (_, _) => ConfirmCancel();
        _backButton = new Button { Content = "返回", MinWidth = 88, Visibility = Visibility.Collapsed };
        _backButton.Click += (_, _) => EndCancel();

        // 请求级“不回答”留在标题说明区，不与逐题导航混同。
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_progress);
        buttons.Children.Add(_backButton);
        buttons.Children.Add(_submitButton);
        buttons.Children.Add(_confirmButton);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(footer, _pagerPanel, 0);
        Place(footer, buttons, 1);

        var footerBar = OpenCodeThemedXaml.Load<Border>("Border",
            "BorderBrush=\"{ThemeResource DividerStrokeColorDefaultBrush}\" BorderThickness=\"0,1,0,0\" Padding=\"0,12,0,0\" Margin=\"0,4,0,0\"")
            ?? new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 4, 0, 0) };
        var footerContent = new StackPanel { Spacing = 8 };
        footerContent.Children.Add(_pageText);
        footerContent.Children.Add(footer);
        footerBar.Child = footerContent;

        var layout = new StackPanel { Spacing = 12 };
        var headerBlock = new StackPanel { Spacing = 4 };
        headerBlock.Children.Add(header);
        var summaryRow = new Grid { ColumnSpacing = 8 };
        summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(summaryRow, _summaryText, 0);
        Place(summaryRow, _cancelButton, 1);
        headerBlock.Children.Add(summaryRow);
        layout.Children.Add(headerBlock);
        layout.Children.Add(_staleBar);
        layout.Children.Add(_busyBar);
        layout.Children.Add(_blockedBar);
        layout.Children.Add(_resolveBar);
        layout.Children.Add(_errorBar);
        layout.Children.Add(_editorScroll);
        layout.Children.Add(_confirmText);
        layout.Children.Add(footerBar);

        // 呈现根可聚焦（遮罩出现时焦点落在这里，而不是任何输入或提交按钮，防止正在输入时的 Enter 误提交）。
        _presenter = new ContentControl
        {
            Content = layout,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        _presenter.PreviewKeyDown += Presenter_PreviewKeyDown;

        var card = OpenCodeThemedXaml.Load<Border>("Border",
            "Background=\"{ThemeResource CardBackgroundFillColorDefaultBrush}\" BorderBrush=\"{ThemeResource CardStrokeColorDefaultBrush}\"" +
            " BorderThickness=\"1\" CornerRadius=\"{ThemeResource OverlayCornerRadius}\" Padding=\"16\"")
            ?? new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16) };
        card.Child = _presenter;
        Children.Add(card);

        // 顶部 2px 强调色细线（提问 = 强调色；权限 = 警示色），不参与布局与命中测试。
        var accent = OpenCodeThemedXaml.Load<Border>("Border",
            "Height=\"2\" VerticalAlignment=\"Top\" Margin=\"12,0,12,0\" CornerRadius=\"0,0,1,1\" IsHitTestVisible=\"False\"" +
            " Background=\"{ThemeResource AccentFillColorDefaultBrush}\"");
        if (accent is not null)
        {
            AutomationProperties.SetAccessibilityView(accent, AccessibilityView.Raw);
            Children.Add(accent);
        }
    }

    /// <summary>遮罩中的待回答提问数。</summary>
    internal int Count => _cards.Count;

    /// <summary>当前显示的提问（无提问时为 null）。</summary>
    internal OpenCodeFormCard? Current => _current;

    /// <summary>
    /// 按服务端顺序同步待回答提问（只收有编辑器、未移除的）；保持当前显示项（若仍存在），
    /// 否则切到被“回答”/导航选中的提问或第一个。返回遮罩中的提问数。
    /// </summary>
    internal int Sync(IEnumerable<OpenCodeFormCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var all = cards.Where(static c => c.Id.Length > 0 && !c.IsDetached).ToList();

        foreach (var removed in _subscribed.Except(all).ToList())
        {
            removed.AnswerRequested -= Card_AnswerRequested;
            removed.FocusRequested -= Card_FocusRequested;
            removed.StateChanged -= Card_StateChanged;
            _subscribed.Remove(removed);
        }

        foreach (var added in all.Except(_subscribed).ToList())
        {
            // 也订阅“暂不支持”的条目：其快照更新为可回答后会自动进入遮罩。
            added.AnswerRequested += Card_AnswerRequested;
            added.FocusRequested += Card_FocusRequested;
            added.StateChanged += Card_StateChanged;
            _subscribed.Add(added);
        }

        _cards.Clear();
        _cards.AddRange(all.Where(static c => c.Editor is not null));

        if (_pinnedId is not null && _cards.All(c => c.Id != _pinnedId))
        {
            _pinnedId = null;
        }

        var next = (_pinnedId is not null ? _cards.FirstOrDefault(c => c.Id == _pinnedId) : null)
            ?? (_current is not null && _cards.Contains(_current) ? _current : null)
            ?? _cards.FirstOrDefault();
        Show(next);
        UpdatePosition();
        return _cards.Count;
    }

    private readonly List<OpenCodeFormCard> _subscribed = new();

    /// <summary>键盘焦点当前是否位于遮罩内。</summary>
    internal bool ContainsFocus()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        try
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused is not null)
            {
                if (ReferenceEquals(focused, this))
                {
                    return true;
                }

                focused = VisualTreeHelper.GetParent(focused);
            }
        }
        catch
        {
            // 焦点查询失败按“不在遮罩内”处理。
        }

        return false;
    }

    /// <summary>把焦点放到遮罩呈现根（不聚焦任何输入或提交按钮，避免正在输入时的 Enter 误提交）。</summary>
    internal bool FocusCurrent()
    {
        if (_current is null)
        {
            return false;
        }

        try
        {
            return _presenter.Focus(FocusState.Programmatic);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用户明确要作答（时间线“回答”）：聚焦当前题的输入控件。</summary>
    private void FocusCurrentInput()
    {
        if (_editor?.CurrentInput is not { } input)
        {
            FocusCurrent();
            return;
        }

        try
        {
            if (!input.Focus(FocusState.Programmatic))
            {
                FocusCurrent();
            }
        }
        catch
        {
            FocusCurrent();
        }
    }

    private void QueueFocus(bool input, bool scrollTop = false)
    {
        var card = _current;
        var editor = _editor;
        if (card is null) return;
        var revision = card.Revision;
        var page = editor?.PageIndex;
        var epoch = ++_focusEpoch;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (epoch != _focusEpoch || !ReferenceEquals(_current, card) || !ReferenceEquals(_editor, editor)
                || !ReferenceEquals(card.Editor, editor) || !ReferenceEquals(_editorHost.Content, editor)
                || card.IsDetached || card.Revision != revision || editor?.PageIndex != page
                || input && (IsStale || _confirming || !card.CanSubmit)) return;
            // A queued navigation/Answer/request-switch focus may run after the user has
            // entered Other (including an IME commit). CurrentInput is the radio/check group,
            // not that textbox: do not take focus away from an active, visible editor input.
            if (EditorHasFocus(editor)) return;
            if (scrollTop) _editorScroll.ChangeView(null, 0, null);
            if (input) FocusCurrentInput(); else FocusCurrent();
        });
    }

    private bool EditorHasFocus(OpenCodeFormAnswerEditor? editor)
    {
        if (editor is null || XamlRoot is null) return false;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            // A previous page can still own focus while it is being collapsed. It must not
            // prevent the queued request from focusing the newly displayed question.
            if (focused is UIElement { Visibility: Visibility.Collapsed }) return false;
            if (ReferenceEquals(focused, editor)) return true;
            focused = VisualTreeHelper.GetParent(focused);
        }
        return false;
    }

    private void Card_AnswerRequested(object? sender, EventArgs e)
    {
        if (sender is not OpenCodeFormCard card || !_cards.Contains(card))
        {
            return;
        }

        _pinnedId = card.Id;
        Show(card);
        UpdatePosition();
        // 遮罩可能正因权限请求或输入法组合暂未显示：只记下选择，显示后用户可直接作答。
        QueueFocus(input: true);
    }

    private void Card_FocusRequested(object? sender, EventArgs e)
    {
        if (sender is OpenCodeFormCard card && _cards.Contains(card))
        {
            _pinnedId = card.Id;
            Show(card);
            UpdatePosition();
        }
    }

    private void Card_StateChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _current) || _current is null)
        {
            return;
        }

        if (!ReferenceEquals(_editor, _current.Editor))
        {
            // 快照重建（指纹变化）：换上新编辑器，禁用提交直到用户确认已看到新内容。
            AttachEditor(_current.Editor);
        }

        Refresh();
    }

    private void Show(OpenCodeFormCard? card)
    {
        if (ReferenceEquals(_current, card) && ReferenceEquals(_editor, card?.Editor) && (card is null || _editorHost.Content is not null))
        {
            Refresh();
            return;
        }

        var hadFocus = ContainsFocus();
        var switching = !ReferenceEquals(_current, card);
        if (switching && _current is not null && _confirming)
        {
            _current.ResetConfirm();
        }

        _current = card;
        if (switching)
        {
            _focusEpoch++;
            _confirming = false;
            _acceptedRevision = card?.Revision ?? 0;
        }

        AttachEditor(card?.Editor);
        if (card is null)
        {
            return;
        }

        var id = card.Id;
        AutomationProperties.SetName(this, "助手在等你回答：" + card.QuestionTitle);
        AutomationProperties.SetAutomationId(_staleBar, "OpenCodeFormAnswerStale_" + id);
        AutomationProperties.SetAutomationId(_reloadButton, "OpenCodeFormAnswerReload_" + id);
        AutomationProperties.SetAutomationId(_busyBar, "OpenCodeQuestionBusy_" + id);
        AutomationProperties.SetAutomationId(_blockedBar, "OpenCodeQuestionBlocked_" + id);
        AutomationProperties.SetAutomationId(_recheckButton, "OpenCodeQuestionRecheck_" + id);
        AutomationProperties.SetAutomationId(_resolveBar, "OpenCodeQuestionResolve_" + id);
        AutomationProperties.SetAutomationId(_resolveRetryButton, "OpenCodeQuestionResolveRetry_" + id);
        AutomationProperties.SetAutomationId(_resolveSettledButton, "OpenCodeQuestionResolveSettled_" + id);
        AutomationProperties.SetAutomationId(_errorBar, "OpenCodeQuestionError_" + id);
        AutomationProperties.SetAutomationId(_previousButton, "OpenCodeQuestionPrevious_" + id);
        AutomationProperties.SetAutomationId(_pageText, "OpenCodeQuestionPage_" + id);
        AutomationProperties.SetAutomationId(_submitButton, "OpenCodeFormSubmit_" + id);
        AutomationProperties.SetAutomationId(_cancelButton, "OpenCodeFormCancel_" + id);
        AutomationProperties.SetAutomationId(_confirmButton, "OpenCodeFormCancelConfirm_" + id);
        AutomationProperties.SetAutomationId(_backButton, "OpenCodeFormCancelBack_" + id);
        Refresh();

        if (hadFocus && switching)
        {
            // 处理完一个后焦点跟随到下一个提问的呈现根，而不是掉到被遮住的输入框。
            QueueFocus(input: false);
        }
    }

    /// <summary>把编辑器挂到遮罩（同一时刻只能有一个父元素）；旧编辑器摘下但不清空输入。</summary>
    private void AttachEditor(OpenCodeFormAnswerEditor? editor)
    {
        if (_editor is not null)
        {
            _editor.PageChanged -= Editor_PageChanged;
        }

        _editorHost.Content = null;
        _editor = editor;
        if (editor is null)
        {
            return;
        }

        if (editor.Parent is ContentControl oldHost)
        {
            oldHost.Content = null;
        }
        else if (editor.Parent is Panel oldPanel)
        {
            oldPanel.Children.Remove(editor);
        }

        editor.Visibility = Visibility.Visible;
        editor.SetPaged(editor.PageCount > 1);
        editor.PageChanged += Editor_PageChanged;
        _editorHost.Content = editor;
        UpdatePager();
    }

    private bool IsStale => _current is not null && _current.Revision != _acceptedRevision;

    private void AcceptLatestRevision()
    {
        if (_current is null)
        {
            return;
        }

        _acceptedRevision = _current.Revision;
        Refresh();
        QueueFocus(input: true);
    }

    private void Refresh()
    {
        var card = _current;
        if (card is null)
        {
            return;
        }

        _titleText.Text = card.QuestionTitle;
        // The title is clipped at two lines; hovering still shows all of it.
        ToolTipService.SetToolTip(_titleText, card.QuestionTitle);
        _summaryText.Text = card.Summary;
        _summaryText.Visibility = _summaryText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var stale = IsStale;
        if (_confirming && (!card.IsCancelPending || card.IsBusy || card.IsBlocked))
        {
            // 提交中/阻断/重建会结束“确认不回答”态。
            _confirming = false;
        }

        SetBarOpen(_staleBar, stale);
        SetBarOpen(_busyBar, card.IsBusy);
        _busyBar.Message = card.IsBusy ? card.StatusMessage : string.Empty;
        SetBarOpen(_blockedBar, card.IsBlocked && !card.IsBusy && !card.IsResolutionPending);
        _blockedBar.Message = _blockedBar.IsOpen ? card.StatusMessage : string.Empty;
        _recheckButton.IsEnabled = card.ActionsEnabled;
        SetBarOpen(_resolveBar, card.IsResolutionPending);
        _resolveBar.Message = card.IsResolutionPending ? card.StatusMessage : string.Empty;
        _resolveRetryButton.IsEnabled = card.ActionsEnabled;
        _resolveSettledButton.IsEnabled = card.ActionsEnabled;
        _errorBar.Message = card.ErrorMessage;
        SetBarOpen(_errorBar, card.ErrorMessage.Length > 0 && !card.IsBusy);

        _progress.IsActive = card.IsBusy;
        _progress.Visibility = card.IsBusy ? Visibility.Visible : Visibility.Collapsed;

        _editorScroll.Visibility = _confirming ? Visibility.Collapsed : Visibility.Visible;
        _confirmText.Visibility = _confirming ? Visibility.Visible : Visibility.Collapsed;
        _submitButton.Visibility = _confirming ? Visibility.Collapsed : Visibility.Visible;
        _cancelButton.Visibility = _confirming ? Visibility.Collapsed : Visibility.Visible;
        _confirmButton.Visibility = _confirming ? Visibility.Visible : Visibility.Collapsed;
        _backButton.Visibility = _confirming ? Visibility.Visible : Visibility.Collapsed;

        _submitButton.IsEnabled = !stale && card.CanSubmit;
        // Disable answer controls through their ContentControl ancestor, not the reader:
        // long questions must remain scrollable while busy, stale or outcome-unknown.
        _editorHost.IsEnabled = !stale && card.CanSubmit && !_confirming;
        _cancelButton.IsEnabled = !stale && card.CanCancel;
        _confirmButton.IsEnabled = !stale && card.CanCancel;
        _backButton.IsEnabled = true;
        UpdatePager();
    }

    private void UpdatePosition()
    {
        var index = _current is null ? -1 : _cards.IndexOf(_current);
        _positionText.Text = index >= 0 && _cards.Count > 1 ? $"{index + 1} / {_cards.Count}" : string.Empty;
        _positionText.Visibility = _positionText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_positionText, _positionText.Text.Length > 0
            ? $"第 {index + 1} 个提问，共 {_cards.Count} 个"
            : string.Empty);
    }

    private void UpdatePager()
    {
        var editor = _editor;
        var paged = editor is not null && editor.IsPaged && !_confirming;
        _pagerPanel.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
        _pageText.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
        var last = editor is null || !editor.IsPaged || editor.PageIndex >= editor.PageCount - 1;
        _submitButton.Content = last ? "提交" : "下一个问题";
        // Tooltip = visible label + shortcut (same pattern as “提交（Ctrl+Enter）”); f148a0f had shortened it to a different word.
        ToolTipService.SetToolTip(_submitButton, last ? "提交（Ctrl+Enter）" : "下一个问题（Ctrl+Enter）");
        _previousButton.IsEnabled = CanNavigate && editor is { PageIndex: > 0 };
        if (!paged || editor is null)
        {
            return;
        }

        _pageText.Text = $"第 {editor.PageIndex + 1} / {editor.PageCount} 题" + (editor.PageHasError(editor.PageIndex) ? " · 需修改" : string.Empty);
    }

    private bool CanNavigate => _current is { CanSubmit: true } && _editor is not null
        && ReferenceEquals(_editor, _current.Editor) && !_confirming && !IsStale;

    private void MoveQuestion(int direction)
    {
        if (!CanNavigate || _editor is not { IsPaged: true } editor) return;
        // Inputs remain in this request/revision's field.Key-bound controls. Only visibility changes:
        // invalid/empty/custom values are retained without a second answer cache, validation or callbacks.
        editor.ShowPage(editor.PageIndex + direction);
        QueueFocus(input: true, scrollTop: true);
    }

    private void Editor_PageChanged(object? sender, EventArgs e) => UpdatePager();

    private void HandlePrimaryAction()
    {
        if (!CanNavigate) return;
        if (_editor is { IsPaged: true } editor && editor.PageIndex < editor.PageCount - 1) MoveQuestion(1);
        else TrySubmit();
    }

    private void TrySubmit()
    {
        var card = _current;
        if (card is null || !CanNavigate || _editor is { IsPaged: true } editor && editor.PageIndex < editor.PageCount - 1)
        {
            Refresh();
            return;
        }

        // 逐题校验失败：错误显示在题目下方，分页跳到第一个有错的题；提交仍经条目既有回调交给页面
        // （页面重新查询、比对指纹、单飞提交、结果未知进入阻断态）。
        _focusEpoch++; // A queued navigation focus must not override validation's more specific error target.
        if (card.TryValidateAnswer(out var answer))
        {
            card.SubmitValidatedAnswer(answer, card.Revision);
        }

        Refresh();
    }

    private void BeginCancel()
    {
        var card = _current;
        if (card is null || IsStale || !card.RequestCancel())
        {
            Refresh();
            return;
        }

        _confirming = true;
        Refresh();
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                // 默认落在“返回”上：Enter 不会直接确认不回答。
                _backButton.Focus(FocusState.Programmatic);
            }
            catch
            {
            }
        });
    }

    private void ConfirmCancel()
    {
        var card = _current;
        if (card is null || !_confirming || IsStale || !card.CanCancel)
        {
            Refresh();
            return;
        }

        _confirming = false;
        if (!card.ConfirmCancel())
        {
            card.ResetConfirm();
        }

        Refresh();
    }

    private void EndCancel()
    {
        _confirming = false;
        _current?.ResetConfirm();
        Refresh();
        QueueFocus(input: true);
    }

    private void Presenter_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _confirming)
        {
            e.Handled = true;
            EndCancel();
            return;
        }

        if (e.Key != VirtualKey.Enter || _confirming)
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            // 输入法组合中的 Enter 以 ProcessKey 送达，不会走到这里。
            e.Handled = true;
            HandlePrimaryAction();
        }
    }

    private static InfoBar CreateBar(InfoBarSeverity severity, string title)
    {
        var bar = new InfoBar
        {
            Severity = severity,
            Title = title,
            IsClosable = false,
            IsOpen = false,
            // A closed InfoBar only collapses its template; the control itself stays Visible
            // at zero height, so the layout StackPanel still adds Spacing (12) around each one.
            // Five closed bars used to open a ~60 px hole between the header and the first question.
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);
        return bar;
    }

    private static void SetBarOpen(InfoBar bar, bool open)
    {
        bar.IsOpen = open;
        bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        SetColumn(element, column);
        grid.Children.Add(element);
    }
}
