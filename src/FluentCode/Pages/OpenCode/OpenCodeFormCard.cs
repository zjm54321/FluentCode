using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 单个待处理 V2 Form 的时间线紧凑条目（重设计 v3 §4.1）：一行“此处有个提问：{标题} [回答]”+ 一行说明；
/// 作答在盖住输入区的提问遮罩（OpenCodeQuestionOverlay，重设计 wave3 F9）中进行，字段控件由 OpenCodeFormAnswerEditor 承载。
/// </summary>
/// <remarks>
/// <para>
/// 支持矩阵（全有或全无，不静默丢题、不伪造答案）：string 无选项 → 多行文本框；string 有选项 →
/// 单选 RadioButtons；string 有选项且 custom=true → 单选 + 最后一项“其他（自己输入）”（输入即选中“其他”）；
/// boolean → 是/否两个单选（未选择 = 不回答）；number / integer → 文本输入并本地校验；
/// multiselect → 复选框组（答案为选项 value 的字符串数组）。
/// external、未知类型、hidden=true、未建模的附加约束、重复 key/value、空选项列表等一律
/// 明确不支持：条目显示“暂不支持”，不可提交，但仍可“不回答”（取消表单）。
/// </para>
/// <para>
/// 安全状态机与改版前完全一致：条目/对话框自身不发送任何请求；答案经回调交给页面，由页面重新
/// GetFormAsync、校验状态仍为 pending 且指纹未变化后才提交。结果未知时进入阻断态，只保留
/// “重新查询状态”，绝不自动重发；必要确认（仍要重新决定 / 视为已结算）仍需用户显式点击。
/// 输入控件（编辑器）在条目生命周期内复用：关闭对话框、启用状态变化、阻断/解除都不会重置输入；
/// 只有快照指纹变化（或身份变化）才按既有语义整份重建并提示。
/// </para>
/// </remarks>
internal sealed class OpenCodeFormCard : StackPanel
{
    private readonly Action<OpenCodeFormInfo, IReadOnlyDictionary<string, JsonElement>> _onSubmit;
    private readonly Action<OpenCodeFormInfo> _onCancel;
    private readonly Action<OpenCodeFormInfo> _onRecheck;
    private readonly Action<OpenCodeFormInfo, bool> _onResolution;

    private readonly TextBlock _titleText;
    private readonly TextBlock _summaryText;
    private readonly TextBlock _noticeText;
    private readonly TextBlock _unsupportedText;
    private readonly TextBlock _unsupportedDetailText;
    private readonly TextBlock _errorText;
    private readonly StackPanel _actionsPanel;
    private readonly Button _answerButton;
    private readonly Button _cancelButton;
    private readonly StackPanel _confirmPanel;
    private readonly TextBlock _confirmText;
    private readonly Button _confirmButton;
    private readonly Button _confirmCancelButton;
    private readonly StackPanel _statusPanel;
    private readonly ProgressRing _progress;
    private readonly TextBlock _statusText;
    private readonly Button _recheckButton;

    private OpenCodeFormPlan? _plan;
    private OpenCodeFormAnswerEditor? _editor;
    private bool _pendingCancel;
    private bool _pendingResolution;
    private bool _blocked;
    private bool _busy;
    private bool _actionsEnabled = true;

    internal OpenCodeFormCard(
        OpenCodeFormInfo form,
        Action<OpenCodeFormInfo, IReadOnlyDictionary<string, JsonElement>> onSubmit,
        Action<OpenCodeFormInfo> onCancel,
        Action<OpenCodeFormInfo> onRecheck,
        Action<OpenCodeFormInfo, bool> onResolution)
    {
        ArgumentNullException.ThrowIfNull(form);
        _onSubmit = onSubmit ?? throw new ArgumentNullException(nameof(onSubmit));
        _onCancel = onCancel ?? throw new ArgumentNullException(nameof(onCancel));
        _onRecheck = onRecheck ?? throw new ArgumentNullException(nameof(onRecheck));
        _onResolution = onResolution ?? throw new ArgumentNullException(nameof(onResolution));

        var id = form.Id ?? string.Empty;

        _titleText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStrongStyleKey);
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        _titleText.IsTextSelectionEnabled = true;

        _answerButton = new Button { Content = "回答", VerticalAlignment = VerticalAlignment.Center };
        _answerButton.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle");
        _answerButton.Click += (_, _) => RequestAnswerDialog();
        AutomationProperties.SetAutomationId(_answerButton, "OpenCodeFormAnswer_" + id);

        // 仅“暂不支持”时作为条目内按钮出现；可回答时“不回答”在对话框里（同一 AutomationId 契约）。
        _cancelButton = new Button { Content = "不回答", VerticalAlignment = VerticalAlignment.Center };
        _cancelButton.Click += (_, _) => OnInlineCancelClicked();
        AutomationProperties.SetAutomationId(_cancelButton, "OpenCodeFormCancel_" + id);

        _actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _actionsPanel.Children.Add(_answerButton);
        _actionsPanel.Children.Add(_cancelButton);

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var glyph = OpenCodeInteractionUi.StyledText("❓", OpenCodeInteractionUi.BodyStyleKey);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        headerRow.Children.Add(glyph);
        headerRow.Children.Add(_titleText);
        headerRow.Children.Add(_actionsPanel);

        _summaryText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);

        _noticeText = OpenCodeInteractionUi.StyledText(
            "提问内容已更新，已重置输入，请重新确认后再回答。", OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _noticeText.Visibility = Visibility.Collapsed;

        _unsupportedText = OpenCodeInteractionUi.StyledText(
            "此提问包含暂不支持的输入，无法在这里回答", OpenCodeInteractionUi.QuestionCautionStyleKey);
        _unsupportedText.Visibility = Visibility.Collapsed;
        _unsupportedDetailText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey, true);
        _unsupportedDetailText.Visibility = Visibility.Collapsed;

        _errorText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.QuestionErrorStyleKey, true);
        _errorText.Visibility = Visibility.Collapsed;

        _confirmText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
        _confirmButton = new Button { Content = "确认不回答" };
        _confirmCancelButton = new Button { Content = "返回" };
        _confirmButton.Click += (_, _) =>
        {
            if (_pendingResolution)
            {
                ResolveUncertain(retry: true);
                return;
            }

            ConfirmCancel();
        };
        _confirmCancelButton.Click += (_, _) =>
        {
            if (_pendingResolution)
            {
                ResolveUncertain(retry: false);
                return;
            }

            ResetConfirm();
        };
        var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmButtons.Children.Add(_confirmButton);
        confirmButtons.Children.Add(_confirmCancelButton);
        _confirmPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        _confirmPanel.Children.Add(_confirmText);
        _confirmPanel.Children.Add(confirmButtons);

        _progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusText = OpenCodeInteractionUi.StyledText(string.Empty, "OpenCodeQuestionStatusTextStyle", true);
        _recheckButton = new Button { Content = "重新查询状态" };
        AutomationProperties.SetAutomationId(_recheckButton, "OpenCodeFormRecheck_" + id);
        _recheckButton.Click += (_, _) => RequestRecheck();
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        statusRow.Children.Add(_progress);
        statusRow.Children.Add(_statusText);
        _statusPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        _statusPanel.Children.Add(statusRow);
        _statusPanel.Children.Add(_recheckButton);

        var root = new StackPanel { Spacing = 4 };
        root.Children.Add(headerRow);
        root.Children.Add(_summaryText);
        root.Children.Add(_noticeText);
        root.Children.Add(_unsupportedText);
        root.Children.Add(_unsupportedDetailText);
        root.Children.Add(_errorText);
        root.Children.Add(_confirmPanel);
        root.Children.Add(_statusPanel);

        // 卡片底 + 左侧 3px 强调色条（内置主题画刷，不定义颜色）。
        var chrome = new Border
        {
            Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.QuestionChromeStyleKey),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Child = root,
        };
        Children.Add(chrome);
        IsTabStop = true;

        RebuildContent(form, changed: false);
    }

    /// <summary>用户点击“回答”：由提问遮罩（OpenCodeQuestionOverlay）切到本提问并聚焦第一题；条目自身不打开任何窗口。</summary>
    internal event EventHandler? AnswerRequested;

    /// <summary>导航（通知点击等）要求显示本提问：遮罩据此切到本提问（不聚焦任何输入/提交按钮）。</summary>
    internal event EventHandler? FocusRequested;

    /// <summary>快照重建 / 阻断 / 忙碌 / 错误 / 移除等状态变化（提问遮罩据此刷新提示并禁用提交）。</summary>
    internal event EventHandler? StateChanged;

    /// <summary>当前显示的表单快照（提交前由页面重新查询并比对指纹）。</summary>
    internal OpenCodeFormInfo Form { get; private set; } = null!;

    /// <summary>当前快照指纹（不含 state；提交前与原指纹比对，变化则不得提交）。</summary>
    internal string Fingerprint { get; private set; } = string.Empty;

    internal string Id => Form.Id ?? string.Empty;

    /// <summary>显示标题（单题用题目标题）。</summary>
    internal string DisplayTitle => _titleText.Text;

    /// <summary>提问标题本身（不含“此处有个提问：”前缀；遮罩标题用）。</summary>
    internal string QuestionTitle { get; private set; } = string.Empty;

    /// <summary>一行说明（如“单选 · 2 个选项 · 可自己输入”）；暂不支持时为空串。</summary>
    internal string Summary => _summaryText.Visibility == Visibility.Visible ? _summaryText.Text : string.Empty;

    /// <summary>每次整份重建递增；对话框打开时记下，变化即“提问已更新”。</summary>
    internal int Revision { get; private set; }

    /// <summary>已被拥有者移除（已处理/已消失）；打开中的对话框据此提示“提问已被处理”。</summary>
    internal bool IsDetached { get; private set; }

    /// <summary>当前可回答时的字段编辑器（不支持时为 null）。生命周期内复用以保留输入。</summary>
    internal OpenCodeFormAnswerEditor? Editor => _editor;

    /// <summary>当前是否允许从对话框提交（未阻断、未忙碌、整体启用且表单可表达）。</summary>
    internal bool CanSubmit => _plan is not null && _editor is not null && !_blocked && !_busy && _actionsEnabled && !IsDetached;

    /// <summary>当前是否允许发起“不回答”（未阻断、未忙碌、整体启用）。</summary>
    internal bool CanCancel => !_blocked && !_busy && _actionsEnabled && !IsDetached;

    /// <summary>是否处于“确认不回答”待确认态。</summary>
    internal bool IsCancelPending => _pendingCancel;

    /// <summary>条目内可见错误（服务端拒绝等）；无则为空串。</summary>
    internal string ErrorMessage => _errorText.Visibility == Visibility.Visible ? _errorText.Text : string.Empty;

    // ---- 供离线测试读取的呈现部件（不改变行为） ----
    internal UIElement NoticeElement => _noticeText;

    internal UIElement ActionsElement => _actionsPanel;

    internal UIElement ConfirmElement => _confirmPanel;

    internal UIElement StatusElement => _statusPanel;

    internal Button AnswerButton => _answerButton;

    // ---- 供提问遮罩镜像的状态（只读；状态机仍只在本条目内） ----

    /// <summary>正在提交/查询（遮罩显示进度，禁用提交与不回答）。</summary>
    internal bool IsBusy => _busy;

    /// <summary>结果未知等阻断态（只允许显式重新查询或必要确认）。</summary>
    internal bool IsBlocked => _blocked;

    /// <summary>是否处于“仍要重新决定 / 视为已结算”的必要确认态。</summary>
    internal bool IsResolutionPending => _pendingResolution;

    /// <summary>全局是否允许操作（另有交互在提交/查询时为 false）。</summary>
    internal bool ActionsEnabled => _actionsEnabled;

    /// <summary>当前状态说明（进度/阻断/必要确认文案）；无则为空串。</summary>
    internal string StatusMessage => _pendingResolution
        ? _confirmText.Text
        : _statusPanel.Visibility == Visibility.Visible ? _statusText.Text : string.Empty;

    /// <summary>
    /// 显式“重新查询状态”（时间线条目按钮与遮罩按钮共用）：仅阻断态、未忙碌、全局可操作时调用回调。
    /// 返回是否已调用。
    /// </summary>
    internal bool RequestRecheck()
    {
        if (!_blocked || _busy || !_actionsEnabled || _pendingResolution)
        {
            return false;
        }

        _onRecheck(Form);
        return true;
    }

    /// <summary>
    /// 必要确认（时间线条目按钮与遮罩按钮共用）：retry=true “仍要重新决定”、false “视为已结算”。
    /// 仅在必要确认态且全局可操作时调用回调；绝不自动触发。返回是否已调用。
    /// </summary>
    internal bool ResolveUncertain(bool retry)
    {
        if (!_pendingResolution || !_actionsEnabled)
        {
            return false;
        }

        _pendingResolution = false;
        _onResolution(Form, retry);
        RaiseStateChanged();
        return true;
    }

    /// <summary>身份与指纹未变时仅刷新快照；否则重建并重置输入与确认/阻断/错误状态。</summary>
    internal void Update(OpenCodeFormInfo form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (string.Equals(Form.Id, form.Id, StringComparison.Ordinal)
            && string.Equals(Form.SessionId, form.SessionId, StringComparison.Ordinal)
            && string.Equals(Fingerprint, OpenCodeInteractionFingerprint.ForForm(form), StringComparison.Ordinal))
        {
            // 内容未变的重查不重建控件、不解除阻断，也不代替用户的显式确认。
            Form = form;
            return;
        }

        RebuildContent(form, changed: true);
        RaiseStateChanged();
    }

    /// <summary>全局忙碌（另有交互在提交/查询）时禁用本条目全部按钮。</summary>
    internal void SetActionsEnabled(bool enabled)
    {
        _actionsEnabled = enabled;
        _answerButton.IsEnabled = enabled;
        _cancelButton.IsEnabled = enabled;
        _confirmButton.IsEnabled = enabled;
        _confirmCancelButton.IsEnabled = enabled;
        _recheckButton.IsEnabled = enabled;
        RaiseStateChanged();
    }

    /// <summary>提交中覆盖层：隐藏操作区并显示进度；结束时保持阻断态（若有）。</summary>
    internal void SetBusy(bool busy, string? label)
    {
        _busy = busy;
        if (busy)
        {
            _pendingCancel = false;
            _pendingResolution = false;
            _actionsPanel.Visibility = Visibility.Collapsed;
            _confirmPanel.Visibility = Visibility.Collapsed;
            _statusPanel.Visibility = Visibility.Visible;
            _recheckButton.Visibility = Visibility.Collapsed;
            _progress.IsActive = true;
            _progress.Visibility = Visibility.Visible;
            _statusText.Text = label ?? "正在提交…";
            _statusText.Style = OpenCodeInteractionUi.TryStyle("OpenCodeQuestionStatusTextStyle");
            RaiseStateChanged();
            return;
        }

        _progress.IsActive = false;
        _progress.Visibility = Visibility.Collapsed;
        if (_blocked)
        {
            // 阻断态保持，直到重新查询成功或快照更新。
            _statusText.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.QuestionCautionStyleKey);
            RaiseStateChanged();
            return;
        }

        _statusPanel.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = Visibility.Visible;
        RaiseStateChanged();
    }

    /// <summary>结果未知/被作废：进入阻断态，只允许显式“重新查询状态”，绝不自动重发。</summary>
    internal void MarkBlocked(string detail)
    {
        _blocked = true;
        _pendingCancel = false;
        _pendingResolution = false;
        _progress.IsActive = false;
        _progress.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Visible;
        _recheckButton.Visibility = Visibility.Visible;
        _statusText.Text = detail;
        _statusText.Style = OpenCodeInteractionUi.TryStyle(OpenCodeInteractionUi.QuestionCautionStyleKey);
        RaiseStateChanged();
    }

    /// <summary>重新查询确认仍待处理且内容未变化：仅退出阻断态并恢复操作区（保留已输入内容）。</summary>
    internal void Unblock()
    {
        if (!_blocked)
        {
            return;
        }

        _blocked = false;
        _progress.IsActive = false;
        _statusText.Style = OpenCodeInteractionUi.TryStyle("OpenCodeQuestionStatusTextStyle");
        _statusPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = Visibility.Visible;
        RaiseStateChanged();
    }

    /// <summary>错误文本（服务端拒绝等）：常驻到下次提交/快照更新。</summary>
    internal void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.Visibility = Visibility.Visible;
        RaiseStateChanged();
    }

    /// <summary>
    /// 显式重新查询后该表单仍 pending 且此前结果未知：在条目内提供“仍要重新决定 / 视为已结算”的必要确认。
    /// 确认“仍要重新决定”需由用户显式接受可能重复生效的风险；绝不自动重发。
    /// </summary>
    internal void ShowResolutionConfirm(string detail)
    {
        _pendingCancel = false;
        _pendingResolution = true;
        _blocked = true;
        _progress.IsActive = false;
        _actionsPanel.Visibility = Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Collapsed;
        _confirmText.Text = detail;
        _confirmButton.Content = "仍要重新决定";
        _confirmCancelButton.Content = "视为已结算";
        AutomationProperties.SetAutomationId(_confirmButton, "OpenCodeFormResolveRetry_" + Id);
        AutomationProperties.SetAutomationId(_confirmCancelButton, "OpenCodeFormResolveSettled_" + Id);
        _confirmPanel.Visibility = Visibility.Visible;
        RaiseStateChanged();
    }

    /// <summary>
    /// 把键盘/程序化焦点移到本条目自身（不聚焦任何输入/提交按钮，避免导航后误操作）。
    /// </summary>
    internal void FocusCard()
    {
        FocusRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            Focus(FocusState.Programmatic);
        }
        catch
        {
            // 聚焦失败不影响条目可用性（导航仍已把条目滚动到可视区）。
        }
    }

    /// <summary>拥有者移除本条目（已处理/已消失/会话切换）：打开中的对话框据此提示并禁用提交。</summary>
    internal void MarkDetached()
    {
        if (IsDetached)
        {
            return;
        }

        IsDetached = true;
        RaiseStateChanged();
    }

    /// <summary>
    /// 对话框“提交回答”：在编辑器中逐题校验（错误显示在题目下方）；全部通过才经回调交给页面。
    /// 返回 true 表示回调已调用（对话框应关闭）；false 表示保持打开（校验失败或当前不可提交）。
    /// </summary>
    internal bool TrySubmitAnswer()
    {
        if (!TryValidateAnswer(out var answer))
        {
            return false;
        }

        return SubmitValidatedAnswer(answer, Revision);
    }

    /// <summary>只校验并装配答案（错误显示在题目下方），不提交。当前不可提交时返回 false。</summary>
    internal bool TryValidateAnswer(out Dictionary<string, JsonElement> answer)
    {
        answer = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var plan = _plan;
        var editor = _editor;
        if (!CanSubmit || plan is null || editor is null)
        {
            return false;
        }

        return editor.TryBuildAnswer(plan, out answer);
    }

    /// <summary>
    /// 提交已校验的答案：仅当条目仍可提交且快照修订号与校验时一致（期间未重建）才经回调交给页面；
    /// 页面照旧重新查询并比对指纹。返回是否已调用回调。
    /// </summary>
    internal bool SubmitValidatedAnswer(IReadOnlyDictionary<string, JsonElement> answer, int revision)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (!CanSubmit || revision != Revision)
        {
            return false;
        }

        _errorText.Text = string.Empty;
        _errorText.Visibility = Visibility.Collapsed;
        _onSubmit(Form, answer);
        return true;
    }

    /// <summary>对话框/条目“不回答”第一步：进入待确认态（阻断/忙碌时无效）。返回是否已进入待确认态。</summary>
    internal bool RequestCancel()
    {
        if (!CanCancel)
        {
            return false;
        }

        _pendingCancel = true;
        _pendingResolution = false;
        return true;
    }

    /// <summary>“确认不回答”：仅在待确认态下调用取消回调（显式确认，绝不自动）。</summary>
    internal bool ConfirmCancel()
    {
        if (!_pendingCancel || _blocked)
        {
            return false;
        }

        _pendingCancel = false;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        _onCancel(Form);
        return true;
    }

    /// <summary>“返回”：退出待确认态（不发送任何请求）。</summary>
    internal void ResetConfirm()
    {
        _pendingCancel = false;
        _pendingResolution = false;
        _confirmPanel.Visibility = Visibility.Collapsed;
        if (!_blocked && !_busy)
        {
            _actionsPanel.Visibility = Visibility.Visible;
        }
    }

    private void RequestAnswerDialog()
    {
        if (!CanSubmit && !CanCancel)
        {
            return;
        }

        AnswerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnInlineCancelClicked()
    {
        if (!RequestCancel())
        {
            return;
        }

        _confirmText.Text = "确定不回答？助手会收到“已取消”。";
        _confirmButton.Content = "确认不回答";
        _confirmCancelButton.Content = "返回";
        AutomationProperties.SetAutomationId(_confirmButton, "OpenCodeFormCancelConfirm_" + Id);
        AutomationProperties.SetAutomationId(_confirmCancelButton, "OpenCodeFormCancelBack_" + Id);
        _actionsPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Visible;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RebuildContent(OpenCodeFormInfo form, bool changed)
    {
        Form = form;
        Fingerprint = OpenCodeInteractionFingerprint.ForForm(form);
        Revision++;
        var id = form.Id ?? string.Empty;

        var labels = new List<string>();
        if (form.Fields is { } fields)
        {
            foreach (var field in fields)
            {
                labels.Add(field?.Title ?? field?.Key ?? string.Empty);
            }
        }

        var title = OpenCodeFormFieldPresentation.QuestionTitle(form.Title, labels);
        QuestionTitle = title;
        _titleText.Text = "此处有个提问：" + title;

        if (OpenCodeFormInputPolicy.TryCreatePlan(form, out var plan, out var reasons))
        {
            _plan = plan;
            _editor = new OpenCodeFormAnswerEditor(id, plan!);
            _summaryText.Text = OpenCodeFormFieldPresentation.QuestionSummary(plan!);
            _summaryText.Visibility = Visibility.Visible;
            _unsupportedText.Visibility = Visibility.Collapsed;
            _unsupportedDetailText.Visibility = Visibility.Collapsed;
            _answerButton.Visibility = Visibility.Visible;
            _cancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _plan = null;
            _editor = null;
            _summaryText.Visibility = Visibility.Collapsed;
            _unsupportedText.Visibility = Visibility.Visible;
            _unsupportedDetailText.Text = "• " + string.Join("\n• ", reasons) + "\n可在支持这些输入的客户端中回答，或选择不回答。";
            _unsupportedDetailText.Visibility = Visibility.Visible;
            _answerButton.Visibility = Visibility.Collapsed;
            _cancelButton.Visibility = Visibility.Visible;
        }

        _noticeText.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;

        _blocked = false;
        _busy = false;
        _pendingCancel = false;
        _pendingResolution = false;
        _errorText.Text = string.Empty;
        _errorText.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Collapsed;
        _progress.IsActive = false;
        _actionsPanel.Visibility = Visibility.Visible;

        AutomationProperties.SetAutomationId(this, "OpenCodeFormCard_" + id);
        AutomationProperties.SetName(this, "提问：" + title);
    }
}

/// <summary>
/// 回答对话框的内容（逐题原生控件 + 题目下方错误）。在条目生命周期内复用，关闭对话框不重置输入。
/// </summary>
/// <remarks>
/// 默认值只在此处（新建字段控件时）作为可见、可编辑的初始内容写入一次；之后的启用状态/忙碌/阻断变化
/// 都不触碰字段控件。提交仍只读取控件当前内容：逐题用 <see cref="OpenCodeFormInputPolicy.TryBuildAnswer"/>
/// 校验（错误显示在该题下方），全部通过后再整表装配一次；服务端仍是最终校验者。
/// </remarks>
internal sealed class OpenCodeFormAnswerEditor : StackPanel
{
    private sealed class FieldSlot
    {
        public required OpenCodeFormFieldPlan Field { get; init; }

        public required Func<OpenCodeFormFieldInput> Read { get; init; }

        public required TextBlock Error { get; init; }

        public required Control FocusTarget { get; init; }

        /// <summary>“其他（自己输入）”已选但内容为空时返回 true（单独提示）。</summary>
        public Func<bool>? OtherEmpty { get; init; }

        /// <summary>“其他（自己输入）”为空时的聚焦目标（已展开的自定义文本框）。</summary>
        public Control? OtherFocusTarget { get; init; }
    }

    private readonly string _formId;
    private readonly List<FieldSlot> _slots = new();
    private readonly List<UIElement> _sections = new();
    private readonly TextBlock _summaryError;
    private int _pageIndex = -1;

    internal OpenCodeFormAnswerEditor(string formId, OpenCodeFormPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _formId = formId ?? string.Empty;
        Orientation = Orientation.Vertical;
        Spacing = 16;

        _summaryError = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.QuestionErrorStyleKey, true);
        _summaryError.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_summaryError, AutomationLiveSetting.Assertive);

        for (var i = 0; i < plan.Fields.Count; i++)
        {
            var section = BuildField(plan.Fields[i], i > 0);
            _sections.Add(section);
            Children.Add(section);
        }

        Children.Add(_summaryError);
    }

    /// <summary>分页（多题逐题显示）切换当前题后触发（遮罩据此刷新页码）。</summary>
    internal event EventHandler? PageChanged;

    /// <summary>题目数（= 分页页数）。</summary>
    internal int PageCount => _sections.Count;

    /// <summary>当前显示的题目序号（0 起）；未分页时为 -1（全部显示）。</summary>
    internal int PageIndex => _pageIndex;

    /// <summary>是否逐题分页显示（遮罩在多题时开启；默认全部显示）。</summary>
    internal bool IsPaged => _pageIndex >= 0;

    /// <summary>第一题的输入控件（遮罩“回答”时聚焦）。</summary>
    internal Control? FirstInput => _slots.Count > 0 ? _slots[0].FocusTarget : null;

    /// <summary>当前题的输入控件（分页时为当前页；未分页时为第一题）。</summary>
    internal Control? CurrentInput => _pageIndex >= 0 && _pageIndex < _slots.Count ? _slots[_pageIndex].FocusTarget : FirstInput;

    /// <summary>
    /// 开启/关闭逐题分页。只切换各题区块的 Visibility，绝不重建或清空任何输入控件。
    /// </summary>
    internal void SetPaged(bool paged)
    {
        if (!paged || _sections.Count <= 1)
        {
            _pageIndex = -1;
            foreach (var section in _sections)
            {
                section.Visibility = Visibility.Visible;
            }

            PageChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ShowPage(_pageIndex < 0 ? 0 : _pageIndex);
    }

    /// <summary>逐题显示第 index 题（越界按边界处理；只有一题时无效）。只切换可见性，不改输入。</summary>
    internal void ShowPage(int index)
    {
        if (_sections.Count == 0 || (_pageIndex < 0 && _sections.Count <= 1))
        {
            return;
        }

        var target = Math.Clamp(index, 0, _sections.Count - 1);
        for (var i = 0; i < _sections.Count; i++)
        {
            _sections[i].Visibility = i == target ? Visibility.Visible : Visibility.Collapsed;
        }

        _pageIndex = target;
        PageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>指定题目当前是否显示校验错误（分页指示与跳转用）。</summary>
    internal bool PageHasError(int index)
        => index >= 0 && index < _slots.Count && _slots[index].Error.Visibility == Visibility.Visible;

    /// <summary>当前所有可见错误文本（逐题 + 汇总）。</summary>
    internal IReadOnlyList<string> VisibleErrors
    {
        get
        {
            var list = new List<string>();
            foreach (var slot in _slots)
            {
                if (slot.Error.Visibility == Visibility.Visible)
                {
                    list.Add(slot.Error.Text);
                }
            }

            if (_summaryError.Visibility == Visibility.Visible)
            {
                list.Add(_summaryError.Text);
            }

            return list;
        }
    }

    /// <summary>逐题校验并装配答案；失败时错误显示在题目下方并聚焦第一个错误题。</summary>
    internal bool TryBuildAnswer(OpenCodeFormPlan plan, out Dictionary<string, JsonElement> answer)
    {
        ArgumentNullException.ThrowIfNull(plan);
        answer = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var input = new Dictionary<string, OpenCodeFormFieldInput>(StringComparer.Ordinal);
        FieldSlot? firstError = null;

        foreach (var slot in _slots)
        {
            var value = slot.Read();
            input[slot.Field.Key] = value;

            string? message = null;
            if (slot.OtherEmpty?.Invoke() == true)
            {
                message = "选择了“其他”，请填写内容。";
            }
            else if (!OpenCodeFormInputPolicy.TryBuildAnswer(
                         new OpenCodeFormPlan(new[] { slot.Field }),
                         new Dictionary<string, OpenCodeFormFieldInput>(StringComparer.Ordinal) { [slot.Field.Key] = value },
                         out _,
                         out var fieldErrors))
            {
                message = string.Join("\n", fieldErrors);
            }

            if (message is null)
            {
                slot.Error.Text = string.Empty;
                slot.Error.Visibility = Visibility.Collapsed;
            }
            else
            {
                slot.Error.Text = message;
                slot.Error.Visibility = Visibility.Visible;
                firstError ??= slot;
            }
        }

        _summaryError.Text = string.Empty;
        _summaryError.Visibility = Visibility.Collapsed;
        if (firstError is not null)
        {
            // 分页时先切到第一个有错的题，再聚焦（折叠中的控件无法获得焦点）。
            if (_pageIndex >= 0)
            {
                ShowPage(_slots.IndexOf(firstError));
            }

            var target = firstError.OtherEmpty?.Invoke() == true && firstError.OtherFocusTarget is { Visibility: Visibility.Visible } otherBox
                ? otherBox
                : firstError.FocusTarget;
            TryFocus(target);
            return false;
        }

        // 逐题都通过后仍整表装配一次（与改版前同一策略入口，含未知字段等整表检查）。
        if (!OpenCodeFormInputPolicy.TryBuildAnswer(plan, input, out answer, out var errors))
        {
            _summaryError.Text = string.Join("\n", errors);
            _summaryError.Visibility = Visibility.Visible;
            return false;
        }

        return true;
    }

    private static void TryFocus(Control control)
    {
        try
        {
            control.Focus(FocusState.Programmatic);
        }
        catch
        {
        }
    }

    private string FieldAutomationId(string key) => "OpenCodeFormField_" + _formId + "_" + key;

    // Keep existing IDs for ordinary options and generated Other/Custom controls.
    // Reserved option values use a distinct prefix, not a suffix another option can impersonate.
    private string OptionAutomationId(string key, string value) => value is "Other" or "Custom"
        ? "OpenCodeFormOption_" + _formId + "_" + key + "_" + value
        : FieldAutomationId(key) + "_" + value;

    private UIElement BuildField(OpenCodeFormFieldPlan field, bool separated)
    {
        var section = new StackPanel { Spacing = 8 };
        if (separated)
        {
            // 多题之间的分隔。
            section.Margin = new Thickness(0, 4, 0, 0);
        }

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(OpenCodeInteractionUi.StyledText(
            field.Label + (field.Required ? "（必答）" : string.Empty), OpenCodeInteractionUi.BodyStrongStyleKey, true));

        if (!string.IsNullOrWhiteSpace(field.Description))
        {
            header.Children.Add(OpenCodeInteractionUi.StyledText(field.Description!, OpenCodeInteractionUi.BodyStyleKey, true));
        }

        section.Children.Add(header);

        var error = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.QuestionErrorStyleKey, true);
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(error, "OpenCodeFormFieldError_" + _formId + "_" + field.Key);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);

        switch (field.Kind)
        {
            case OpenCodeFormFieldKind.Text:
            {
                var box = CreateTextBox(field, field.Placeholder ?? "请输入", multiline: true);
                box.Text = OpenCodeFormFieldPresentation.InitialText(field) ?? string.Empty;
                section.Children.Add(box);
                _slots.Add(new FieldSlot { Field = field, Error = error, FocusTarget = box, Read = () => new OpenCodeFormFieldInput(Text: box.Text) });
                break;
            }

            case OpenCodeFormFieldKind.Choice:
            {
                var radios = CreateRadioButtons(field, withOther: false);
                radios.SelectedIndex = OpenCodeFormFieldPresentation.InitialChoiceIndex(field);
                section.Children.Add(radios);
                _slots.Add(new FieldSlot
                {
                    Field = field,
                    Error = error,
                    FocusTarget = radios,
                    Read = () => new OpenCodeFormFieldInput(Choice: ReadChoice(radios, field)),
                });
                break;
            }

            case OpenCodeFormFieldKind.ChoiceWithCustom:
            {
                var radios = CreateRadioButtons(field, withOther: true);
                var otherIndex = field.Options.Count;
                var box = CreateTextBox(field, field.Placeholder ?? "在这里输入你的回答", multiline: true);
                AutomationProperties.SetAutomationId(box, FieldAutomationId(field.Key) + "_Custom");
                AutomationProperties.SetName(box, field.Label + "（其他，自己输入）");
                box.Margin = new Thickness(28, 0, 0, 0);
                var initialIndex = OpenCodeFormFieldPresentation.InitialChoiceIndex(field);
                var initialCustom = OpenCodeFormFieldPresentation.InitialCustomText(field);
                box.Text = initialCustom ?? string.Empty;
                radios.SelectedIndex = initialIndex >= 0 ? initialIndex : !string.IsNullOrEmpty(initialCustom) ? otherIndex : -1;
                // G2：只有选中“其他（自己输入）”时才展开自定义文本框；未选中时折叠隐藏（已输入文字仍保留在控件中）。
                box.Visibility = radios.SelectedIndex == otherIndex ? Visibility.Visible : Visibility.Collapsed;
                radios.SelectionChanged += (_, _) =>
                {
                    box.Visibility = radios.SelectedIndex == otherIndex ? Visibility.Visible : Visibility.Collapsed;
                };
                // 输入即选中“其他”；切到其他选项时保留已输入文字，但不会作为答案提交。
                box.TextChanged += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(box.Text) && radios.SelectedIndex != otherIndex)
                    {
                        radios.SelectedIndex = otherIndex;
                    }
                };
                section.Children.Add(radios);
                section.Children.Add(box);
                _slots.Add(new FieldSlot
                {
                    Field = field,
                    Error = error,
                    FocusTarget = radios,
                    OtherFocusTarget = box,
                    Read = () => radios.SelectedIndex == otherIndex
                        ? new OpenCodeFormFieldInput(Text: box.Text)
                        : new OpenCodeFormFieldInput(Choice: ReadChoice(radios, field)),
                    OtherEmpty = () => radios.SelectedIndex == otherIndex && string.IsNullOrWhiteSpace(box.Text),
                });
                break;
            }

            case OpenCodeFormFieldKind.Boolean:
            {
                var radios = new RadioButtons();
                radios.Items.Add(CreateRadioItem("是", null));
                radios.Items.Add(CreateRadioItem("否", null));
                radios.SelectedIndex = OpenCodeFormFieldPresentation.InitialBooleanIndex(field);
                AutomationProperties.SetAutomationId(radios, FieldAutomationId(field.Key));
                SetFieldAccessibility(radios, field);
                section.Children.Add(radios);
                _slots.Add(new FieldSlot
                {
                    Field = field,
                    Error = error,
                    FocusTarget = radios,
                    Read = () => new OpenCodeFormFieldInput(Boolean: radios.SelectedIndex switch { 0 => true, 1 => false, _ => null }),
                });
                break;
            }

            case OpenCodeFormFieldKind.Number:
            {
                var box = CreateTextBox(field, field.Placeholder ?? "数字，如 3.14", multiline: false);
                box.Text = OpenCodeFormFieldPresentation.InitialText(field) ?? string.Empty;
                section.Children.Add(box);
                _slots.Add(new FieldSlot { Field = field, Error = error, FocusTarget = box, Read = () => new OpenCodeFormFieldInput(Text: box.Text) });
                break;
            }

            case OpenCodeFormFieldKind.Integer:
            {
                // 文本框而非 NumberBox：NumberBox 以 double 承载，会舍入超过 2^53 的 Int64 默认值/输入。
                var box = CreateTextBox(field, field.Placeholder ?? "整数，如 42", multiline: false);
                box.Text = OpenCodeFormFieldPresentation.InitialText(field) ?? string.Empty;
                section.Children.Add(box);
                _slots.Add(new FieldSlot { Field = field, Error = error, FocusTarget = box, Read = () => new OpenCodeFormFieldInput(Text: box.Text) });
                break;
            }

            case OpenCodeFormFieldKind.MultiSelect:
            {
                var optionsPanel = new StackPanel { Spacing = 6 };
                AutomationProperties.SetAutomationId(optionsPanel, FieldAutomationId(field.Key));
                SetFieldAccessibility(optionsPanel, field);
                var boxes = new List<(string Value, CheckBox Box)>(field.Options.Count);
                var countText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
                void RefreshCount()
                {
                    // 统计面板中全部已勾选复选框（含可自定义多选的“其他”项）。
                    var selected = 0;
                    foreach (var child in optionsPanel.Children)
                    {
                        if (child is CheckBox { IsChecked: true })
                        {
                            selected++;
                        }
                    }

                    countText.Text = OpenCodeFormFieldPresentation.SelectedCountText(selected);
                }

                foreach (var option in field.Options)
                {
                    var check = new CheckBox
                    {
                        Content = CreateOptionContent(option.Label, option.Description),
                        IsChecked = OpenCodeFormFieldPresentation.IsInitiallySelected(field, option.Value),
                        Padding = new Thickness(8, 2, 0, 2),
                    };
                    AutomationProperties.SetAutomationId(check, OptionAutomationId(field.Key, option.Value));
                    AutomationProperties.SetName(check, option.Label);
                    if (!string.IsNullOrWhiteSpace(option.Description))
                    {
                        AutomationProperties.SetHelpText(check, option.Description!);
                    }

                    check.Checked += (_, _) => RefreshCount();
                    check.Unchecked += (_, _) => RefreshCount();
                    optionsPanel.Children.Add(check);
                    boxes.Add((option.Value, check));
                }

                // custom=true（D4）：最后一项“其他（自己输入）”复选框 + 文本框；勾选后文本追加在所选项之后。
                CheckBox? otherCheck = null;
                TextBox? otherBox = null;
                if (field.AllowsCustom)
                {
                    otherCheck = new CheckBox
                    {
                        Content = CreateOptionContent(OpenCodeFormFieldPresentation.OtherChoiceLabel, null),
                        Padding = new Thickness(8, 2, 0, 2),
                    };
                    AutomationProperties.SetAutomationId(otherCheck, FieldAutomationId(field.Key) + "_Other");
                    AutomationProperties.SetName(otherCheck, OpenCodeFormFieldPresentation.OtherChoiceLabel);
                    optionsPanel.Children.Add(otherCheck);
                    otherBox = CreateTextBox(field, field.Placeholder ?? "在这里输入你的补充回答", multiline: true);
                    AutomationProperties.SetAutomationId(otherBox, FieldAutomationId(field.Key) + "_Custom");
                    AutomationProperties.SetName(otherBox, field.Label + "（其他，自己输入）");
                    otherBox.Margin = new Thickness(28, 0, 0, 0);
                    var initialCustom = OpenCodeFormFieldPresentation.InitialCustomText(field);
                    otherBox.Text = initialCustom ?? string.Empty;
                    var initiallyChecked = !string.IsNullOrEmpty(initialCustom);
                    otherCheck.IsChecked = initiallyChecked;
                    // G2：只有勾选“其他（自己输入）”时才展开自定义文本框；未勾选时折叠隐藏（已输入文字仍保留在控件中）。
                    otherBox.Visibility = initiallyChecked ? Visibility.Visible : Visibility.Collapsed;
                    var check = otherCheck;
                    var box = otherBox;
                    check.Checked += (_, _) =>
                    {
                        box.Visibility = Visibility.Visible;
                        RefreshCount();
                    };
                    check.Unchecked += (_, _) =>
                    {
                        box.Visibility = Visibility.Collapsed;
                        RefreshCount();
                    };
                    // 输入即勾选“其他”；取消勾选时保留已输入文字，但不会作为答案提交。
                    box.TextChanged += (_, _) =>
                    {
                        if (!string.IsNullOrEmpty(box.Text) && check.IsChecked != true)
                        {
                            check.IsChecked = true;
                        }
                    };
                    optionsPanel.Children.Add(box);
                }

                RefreshCount();
                section.Children.Add(optionsPanel);
                section.Children.Add(countText);
                _slots.Add(new FieldSlot
                {
                    Field = field,
                    Error = error,
                    FocusTarget = boxes.Count > 0 ? boxes[0].Box : new CheckBox(),
                    OtherFocusTarget = otherBox,
                    Read = () =>
                    {
                        var selections = new List<string>();
                        foreach (var (value, box) in boxes)
                        {
                            if (box.IsChecked == true)
                            {
                                selections.Add(value);
                            }
                        }

                        var custom = otherCheck?.IsChecked == true ? otherBox!.Text : null;
                        return new OpenCodeFormFieldInput(Text: custom, Selections: selections);
                    },
                    OtherEmpty = otherCheck is null
                        ? null
                        : () => otherCheck.IsChecked == true && string.IsNullOrWhiteSpace(otherBox!.Text),
                });
                break;
            }
        }

        var hint = FieldHint(field);
        if (hint is not null)
        {
            section.Children.Add(OpenCodeInteractionUi.StyledText(hint, OpenCodeInteractionUi.CaptionSubtleStyleKey));
        }

        section.Children.Add(error);
        return section;
    }

    /// <summary>约束提示 + “已预填默认值”说明（仅当默认值产生可见预填时）；都没有时为 null。</summary>
    private static string? FieldHint(OpenCodeFormFieldPlan field)
    {
        var constraint = OpenCodeFormFieldPresentation.ConstraintHint(field);
        var prefilled = OpenCodeFormFieldPresentation.HasVisiblePrefill(field)
            ? "已预填默认值，可修改或清除；需手动提交"
            : null;
        return (constraint, prefilled) switch
        {
            (null, null) => null,
            ({ } c, null) => c,
            (null, { } p) => p,
            ({ } c, { } p) => c + " · " + p,
        };
    }

    /// <summary>选项内容：标签（Body）+ 较小说明（Caption 次要色）。</summary>
    private static UIElement CreateOptionContent(string label, string? description)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(OpenCodeInteractionUi.StyledText(label, OpenCodeInteractionUi.BodyStyleKey));
        if (!string.IsNullOrWhiteSpace(description))
        {
            panel.Children.Add(OpenCodeInteractionUi.StyledText(description!, OpenCodeInteractionUi.CaptionSubtleStyleKey));
        }

        return panel;
    }

    private static RadioButton CreateRadioItem(string label, string? description)
    {
        var item = new RadioButton
        {
            Content = CreateOptionContent(label, description),
            Padding = new Thickness(8, 2, 0, 2),
        };
        AutomationProperties.SetName(item, label);
        if (!string.IsNullOrWhiteSpace(description))
        {
            AutomationProperties.SetHelpText(item, description!);
        }

        return item;
    }

    private RadioButtons CreateRadioButtons(OpenCodeFormFieldPlan field, bool withOther)
    {
        var radios = new RadioButtons();
        foreach (var option in field.Options)
        {
            var item = CreateRadioItem(option.Label, option.Description);
            AutomationProperties.SetAutomationId(item, OptionAutomationId(field.Key, option.Value));
            radios.Items.Add(item);
        }

        if (withOther)
        {
            var other = CreateRadioItem(OpenCodeFormFieldPresentation.OtherChoiceLabel, null);
            AutomationProperties.SetAutomationId(other, FieldAutomationId(field.Key) + "_Other");
            radios.Items.Add(other);
        }

        AutomationProperties.SetAutomationId(radios, FieldAutomationId(field.Key));
        SetFieldAccessibility(radios, field);
        return radios;
    }

    /// <summary>可访问名称 = 字段标签；帮助文本 = 字段说明（均为表单 schema 文本，不含用户输入或默认值）。</summary>
    private static void SetFieldAccessibility(DependencyObject control, OpenCodeFormFieldPlan field)
    {
        AutomationProperties.SetName(control, field.Label);
        if (!string.IsNullOrWhiteSpace(field.Description))
        {
            AutomationProperties.SetHelpText(control, field.Description!);
        }
    }

    private TextBox CreateTextBox(OpenCodeFormFieldPlan field, string placeholder, bool multiline)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = multiline,
            MinHeight = multiline ? 64 : 32,
        };
        AutomationProperties.SetAutomationId(box, FieldAutomationId(field.Key));
        SetFieldAccessibility(box, field);
        return box;
    }

    private static string? ReadChoice(RadioButtons radios, OpenCodeFormFieldPlan field)
    {
        var index = radios.SelectedIndex;
        return index >= 0 && index < field.Options.Count ? field.Options[index].Value : null;
    }
}
