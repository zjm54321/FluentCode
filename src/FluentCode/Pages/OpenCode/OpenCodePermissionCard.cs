using System;
using FluentCode.Services.Appearance;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace FluentCode.Pages.OpenCode;

/// <summary>
/// 单个待处理权限请求：保存请求快照/指纹与卡片内状态机（确认 / 提交中 / 阻断 / 错误），
/// 并提供“允许一次 / 始终允许 / 拒绝”三个真实 decision 的操作入口。
/// </summary>
/// <remarks>
/// <para>
/// 语义严格取自 docs/opencode-integration.md 与 docs/notifications-and-tray.md：
/// decision 为 once / always / reject；“始终允许”仅保存本请求 save patterns 对应的项目规则
/// （不是全局或永久授权）；“拒绝”还会同时拒绝本会话其他待处理的权限请求。
/// 后两者先经一次原地显式确认（不使用 ContentDialog）。
/// </para>
/// <para>
/// 卡片自身不发送任何请求：决定经回调交给页面，由页面在提交前重新查询 pending 并比对指纹。
/// 提交期间按钮禁用（防重复并发）；结果未知或被作废时进入阻断态，只保留“重新查询状态”入口，
/// 绝不自动重发。
/// </para>
/// <para>
/// 重设计 v3 §5：本对象仍由 <see cref="OpenCodeInteractionsView"/> 按 id + 指纹增量维护（作为折叠、不占位的
/// 占位元素留在交互面板中），而可见呈现 <see cref="Presenter"/> 由 <see cref="OpenCodePermissionOverlay"/>
/// 放在输入区遮罩内，一次显示一个。状态机、回调与 AutomationId 与原卡片完全一致。
/// </para>
/// </remarks>
internal sealed class OpenCodePermissionCard : StackPanel
{
    // 用户可选等宽字体（F11）：每次填充内容时取当前值。
    private static FontFamily MonoFont => AppFonts.Monospace;

    private readonly Action<OpenCodePermissionRequest, string> _onDecision;
    private readonly Action<OpenCodePermissionRequest> _onRecheck;
    private readonly Action<OpenCodePermissionRequest, bool> _onResolution;

    private readonly ContentControl _presenter;
    private readonly TextBlock _positionText;
    private readonly TextBlock _headlineText;
    private readonly TextBlock _messageText;
    private readonly Expander _details;
    private readonly TextBlock _detailId;
    private readonly TextBlock _detailAction;
    private readonly TextBlock _detailResources;
    private readonly TextBlock _detailSave;
    private readonly TextBlock _detailSourceLabel;
    private readonly TextBlock _detailSource;
    private readonly TextBlock _detailMessageLabel;
    private readonly TextBlock _detailMessage;
    private readonly InfoBar _noticeBar;
    private readonly InfoBar _errorBar;
    private readonly StackPanel _actionsPanel;
    private readonly StackPanel _confirmPanel;
    private readonly Button _confirmButton;
    private readonly Button _confirmCancelButton;
    private readonly StackPanel _statusPanel;
    private readonly ProgressRing _progress;
    private readonly TextBlock _statusText;
    private readonly Button _recheckButton;
    private readonly Button _onceButton;
    private readonly Button _alwaysButton;
    private readonly Button _rejectButton;

    private string? _pendingDecision;
    private bool _pendingResolution;
    private bool _blocked;
    private int _otherPending;

    internal OpenCodePermissionCard(
        OpenCodePermissionRequest request,
        Action<OpenCodePermissionRequest, string> onDecision,
        Action<OpenCodePermissionRequest> onRecheck,
        Action<OpenCodePermissionRequest, bool> onResolution)
    {
        ArgumentNullException.ThrowIfNull(request);
        _onDecision = onDecision ?? throw new ArgumentNullException(nameof(onDecision));
        _onRecheck = onRecheck ?? throw new ArgumentNullException(nameof(onRecheck));
        _onResolution = onResolution ?? throw new ArgumentNullException(nameof(onResolution));

        Request = request;
        Fingerprint = OpenCodeInteractionFingerprint.ForPermission(request);

        // 交互面板中的占位：不可见、不占位、不可聚焦（可见呈现见 Presenter）。
        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        var id = Request.Id ?? string.Empty;

        // ---- 标题行：🔒 需要你的许可 ……………… 1 / 2 ----
        var icon = OpenCodeThemedXaml.Load<FontIcon>("FontIcon",
            "Glyph=\"&#xE72E;\" FontSize=\"16\" VerticalAlignment=\"Center\" Foreground=\"{ThemeResource SystemFillColorCautionBrush}\"")
            ?? new FontIcon { Glyph = "\uE72E", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var title = OpenCodeInteractionUi.StyledText("需要你的许可", OpenCodeInteractionUi.BodyStrongStyleKey);
        title.VerticalAlignment = VerticalAlignment.Center;
        _positionText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey);
        _positionText.VerticalAlignment = VerticalAlignment.Center;
        _positionText.TextWrapping = TextWrapping.NoWrap;
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(header, icon, 0);
        Place(header, title, 1);
        Place(header, _positionText, 2);

        // ---- 摘要：助手想 访问工作区外的目录：/proc/* ----
        _headlineText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStyleKey, selectable: true);
        _messageText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CaptionSubtleStyleKey, selectable: true);

        // ---- 详细信息（默认收起）：请求 ID、全部资源、save patterns、来源、服务端消息 ----
        _detailId = DetailValue();
        _detailAction = DetailValue();
        _detailResources = DetailValue();
        _detailSave = DetailValue();
        _detailSourceLabel = DetailLabel("来源");
        _detailSource = DetailValue();
        _detailMessageLabel = DetailLabel("服务端消息");
        _detailMessage = DetailValue();
        var detailBody = new StackPanel { Spacing = 2 };
        detailBody.Children.Add(DetailLabel("请求 ID"));
        detailBody.Children.Add(_detailId);
        detailBody.Children.Add(Spaced(DetailLabel("动作（action）")));
        detailBody.Children.Add(_detailAction);
        detailBody.Children.Add(Spaced(DetailLabel("全部资源")));
        detailBody.Children.Add(_detailResources);
        detailBody.Children.Add(Spaced(DetailLabel("“始终允许”会保存的规则（save patterns）")));
        detailBody.Children.Add(_detailSave);
        detailBody.Children.Add(Spaced(_detailSourceLabel));
        detailBody.Children.Add(_detailSource);
        detailBody.Children.Add(Spaced(_detailMessageLabel));
        detailBody.Children.Add(_detailMessage);
        _details = new Expander
        {
            Header = "详细信息",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer
            {
                Content = detailBody,
                MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        AutomationProperties.SetAutomationId(_details, "OpenCodePermissionDetails_" + id);
        AutomationProperties.SetName(_details, "权限请求的详细信息");

        // ---- 原地说明条：确认 / 阻断 / 结果未知（InfoBar 自带语义色，随主题更新） ----
        _noticeBar = new InfoBar { IsClosable = false, IsOpen = false, IsIconVisible = true };
        _errorBar = new InfoBar { IsClosable = false, IsOpen = false, Severity = InfoBarSeverity.Error, Title = "未能处理" };
        AutomationProperties.SetLiveSetting(_noticeBar, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_errorBar, AutomationLiveSetting.Assertive);

        // ---- 操作：[拒绝] [始终允许] [允许一次]（主操作在最右，Accent） ----
        _onceButton = CreateActionButton("允许一次", "OpenCodePermissionOnce_", OpenCodePermissionDecisions.Once,
            "只允许这一次（不保存任何规则）");
        _onceButton.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle");
        _alwaysButton = CreateActionButton("始终允许", "OpenCodePermissionAlways_", OpenCodePermissionDecisions.Always,
            "为本项目保存规则后允许（需要确认；不是全局授权）");
        _rejectButton = CreateActionButton("拒绝", "OpenCodePermissionReject_", OpenCodePermissionDecisions.Reject,
            "拒绝（需要确认；会同时拒绝本会话其他待处理的权限请求）");

        _actionsPanel = ButtonRow();
        _actionsPanel.Children.Add(_rejectButton);
        _actionsPanel.Children.Add(_alwaysButton);
        _actionsPanel.Children.Add(_onceButton);

        _confirmButton = new Button { Content = "确认", MinWidth = 96 };
        _confirmCancelButton = new Button { Content = "返回", MinWidth = 72 };
        AutomationProperties.SetAutomationId(_confirmButton, "OpenCodePermissionConfirm_" + id);
        AutomationProperties.SetAutomationId(_confirmCancelButton, "OpenCodePermissionConfirmCancel_" + id);
        _confirmButton.Click += (_, _) =>
        {
            if (_pendingResolution)
            {
                _pendingResolution = false;
                _onResolution(Request, true);
                return;
            }

            if (_pendingDecision is { } decision)
            {
                _pendingDecision = null;
                _onDecision(Request, decision);
            }
        };
        _confirmCancelButton.Click += (_, _) =>
        {
            if (_pendingResolution)
            {
                _pendingResolution = false;
                _onResolution(Request, false);
                return;
            }

            ResetToActions();
            FocusAction(_pendingFocusAfterCancel);
        };
        _confirmPanel = ButtonRow();
        _confirmPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Children.Add(_confirmCancelButton);
        _confirmPanel.Children.Add(_confirmButton);

        _progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusText = OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.BodyStyleKey);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        _recheckButton = new Button { Content = "重新查询状态" };
        AutomationProperties.SetAutomationId(_recheckButton, "OpenCodePermissionRecheck_" + id);
        _recheckButton.Click += (_, _) => _onRecheck(Request);
        _statusPanel = ButtonRow();
        _statusPanel.Visibility = Visibility.Collapsed;
        _statusPanel.Children.Add(_progress);
        _statusPanel.Children.Add(_statusText);
        _statusPanel.Children.Add(_recheckButton);

        var buttons = new Grid();
        buttons.Children.Add(_actionsPanel);
        buttons.Children.Add(_confirmPanel);
        buttons.Children.Add(_statusPanel);

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(header);
        root.Children.Add(_headlineText);
        root.Children.Add(_messageText);
        root.Children.Add(_details);
        root.Children.Add(_noticeBar);
        root.Children.Add(_errorBar);
        root.Children.Add(buttons);

        // 可聚焦的呈现根：导航/遮罩出现时把焦点放在这里（而不是任何会触发决定的按钮，避免误按 Enter）。
        _presenter = new ContentControl
        {
            Content = root,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        ApplyContent(request);
    }

    /// <summary>当前显示的请求快照（提交前由页面重新查询并比对指纹）。</summary>
    internal OpenCodePermissionRequest Request { get; private set; }

    /// <summary>当前快照指纹（提交前与原指纹比对，变化则不得提交）。</summary>
    internal string Fingerprint { get; private set; }

    internal string Id => Request.Id ?? string.Empty;

    /// <summary>遮罩内的可见呈现（同一时刻只有一个父元素：由 <see cref="OpenCodePermissionOverlay"/> 托管）。</summary>
    internal FrameworkElement Presenter => _presenter;

    /// <summary>页面/通知导航请求聚焦本请求（遮罩据此切换到本请求并聚焦其呈现根）。</summary>
    internal event EventHandler? FocusRequested;

    /// <summary>用同一请求的最新快照重绘（scope 变化时调用）；重置确认/阻断/错误状态。</summary>
    internal void Update(OpenCodePermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        Fingerprint = OpenCodeInteractionFingerprint.ForPermission(request);
        ApplyContent(request);
        ResetToActions();
    }

    /// <summary>遮罩告知本请求在待处理列表中的位置（“1 / 2”），并据此具体说明“拒绝”的连带范围。</summary>
    internal void SetPosition(int index, int total)
    {
        _positionText.Text = OpenCodePermissionPresentation.Position(index, total);
        _positionText.Visibility = _positionText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _otherPending = Math.Max(0, total - 1);
        if (_pendingDecision == OpenCodePermissionDecisions.Reject)
        {
            _noticeBar.Message = OpenCodePermissionPresentation.RejectConfirmText(_otherPending);
        }
    }

    /// <summary>全局忙碌（另有交互在提交/查询）时禁用本卡片全部按钮；阻断态下仅“重新查询”可见。</summary>
    internal void SetActionsEnabled(bool enabled)
    {
        _onceButton.IsEnabled = enabled;
        _alwaysButton.IsEnabled = enabled;
        _rejectButton.IsEnabled = enabled;
        _confirmButton.IsEnabled = enabled;
        _confirmCancelButton.IsEnabled = enabled;
        _recheckButton.IsEnabled = enabled;
    }

    /// <summary>提交中覆盖层：隐藏操作区并显示进度；结束时保持阻断态（若有）。</summary>
    internal void SetBusy(bool busy, string? label)
    {
        if (busy)
        {
            _pendingDecision = null;
            _pendingResolution = false;
            _noticeBar.IsOpen = false;
            _actionsPanel.Visibility = Visibility.Collapsed;
            _confirmPanel.Visibility = Visibility.Collapsed;
            _statusPanel.Visibility = Visibility.Visible;
            _recheckButton.Visibility = Visibility.Collapsed;
            _progress.IsActive = true;
            _progress.Visibility = Visibility.Visible;
            _statusText.Text = label ?? "正在提交…";
            _statusText.Visibility = Visibility.Visible;
            return;
        }

        _progress.IsActive = false;
        if (_blocked)
        {
            // 阻断态保持，直到重新查询成功或快照更新；仍处于阻断状态区时恢复其“重新查询状态”入口。
            if (!_pendingResolution && _statusPanel.Visibility == Visibility.Visible && _blockedDetail is { } detail)
            {
                MarkBlocked(detail);
            }

            return;
        }

        _statusPanel.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = Visibility.Visible;
    }

    private string? _blockedDetail;

    /// <summary>结果未知/被作废：进入阻断态，只允许显式“重新查询状态”，绝不自动重发。</summary>
    internal void MarkBlocked(string detail)
    {
        _blocked = true;
        _blockedDetail = detail;
        _pendingDecision = null;
        _pendingResolution = false;
        _progress.IsActive = false;
        _actionsPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Collapsed;
        ShowNotice(InfoBarSeverity.Warning, "结果未知", detail);
        _statusPanel.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Collapsed;
        _statusText.Text = string.Empty;
        _statusText.Visibility = Visibility.Collapsed;
        _recheckButton.Visibility = Visibility.Visible;
    }

    /// <summary>重新查询确认仍待处理且范围未变化：仅退出阻断态并恢复操作区。</summary>
    internal void Unblock()
    {
        if (!_blocked)
        {
            return;
        }

        _blocked = false;
        _progress.IsActive = false;
        _noticeBar.IsOpen = false;
        _statusPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = Visibility.Visible;
    }

    /// <summary>服务端明确拒绝（如 400/401/403）：保留卡片供检查后重试，错误文本常驻到下次操作或快照更新。</summary>
    internal void ShowError(string message)
    {
        _errorBar.Message = message;
        _errorBar.IsOpen = true;
    }

    /// <summary>
    /// 显式重新查询后该请求仍待处理且此前结果未知：在卡片内提供“仍要重新决定 / 视为已结算”的必要确认。
    /// 确认“仍要重新决定”需由用户显式接受可能重复生效的风险；绝不自动重发。
    /// </summary>
    internal void ShowResolutionConfirm(string detail)
    {
        _pendingDecision = null;
        _pendingResolution = true;
        _blocked = true;
        _progress.IsActive = false;
        _actionsPanel.Visibility = Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Collapsed;
        ShowNotice(InfoBarSeverity.Warning, "此前的结果未知", detail);
        _confirmButton.Content = "仍要重新决定";
        _confirmButton.Style = null;
        _confirmCancelButton.Content = "视为已结算";
        _confirmPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 把键盘/程序化焦点移到本请求的呈现根（不聚焦任何会触发决定/回答的按钮，避免导航后误操作）。
    /// 遮罩据 <see cref="FocusRequested"/> 先切换到本请求。
    /// </summary>
    internal void FocusCard()
    {
        FocusRequested?.Invoke(this, EventArgs.Empty);
        FocusPresenter();
    }

    /// <summary>聚焦呈现根（遮罩出现时由遮罩调用）；失败不影响可用性。</summary>
    internal bool FocusPresenter()
    {
        try
        {
            return _presenter.Focus(FocusState.Programmatic);
        }
        catch
        {
            return false;
        }
    }

    private Button? _pendingFocusAfterCancel;

    private Button CreateActionButton(string text, string automationIdPrefix, string decision, string tooltip)
    {
        var button = new Button { Content = text, MinWidth = 88 };
        AutomationProperties.SetAutomationId(button, automationIdPrefix + (Request.Id ?? string.Empty));
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => OnActionClicked(decision, button);
        return button;
    }

    private void OnActionClicked(string decision, Button source)
    {
        if (_blocked)
        {
            return;
        }

        if (decision == OpenCodePermissionDecisions.Once)
        {
            // “允许一次”只影响本请求，无需二次确认。
            _onDecision(Request, decision);
            return;
        }

        // always/reject 先经一次原地显式确认；拒绝的连带影响必须清楚告知。
        _pendingDecision = decision;
        _pendingFocusAfterCancel = source;
        if (decision == OpenCodePermissionDecisions.Reject)
        {
            ShowNotice(InfoBarSeverity.Error, "确认拒绝？", OpenCodePermissionPresentation.RejectConfirmText(_otherPending));
            _confirmButton.Content = "确认拒绝";
            _confirmButton.Style = null;
        }
        else
        {
            ShowNotice(InfoBarSeverity.Informational, "确认始终允许？", OpenCodePermissionPresentation.AlwaysConfirmText(Request));
            _confirmButton.Content = "确认始终允许";
            _confirmButton.Style = OpenCodeInteractionUi.TryStyle("AccentButtonStyle");
        }

        _confirmCancelButton.Content = "返回";
        _actionsPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Visible;
        // 焦点留在确认区的“返回”上：误按 Enter 只会退回，不会提交。
        FocusAction(_confirmCancelButton);
    }

    private void ResetToActions()
    {
        _blocked = false;
        _pendingDecision = null;
        _pendingResolution = false;
        _progress.IsActive = false;
        _noticeBar.IsOpen = false;
        _errorBar.Message = string.Empty;
        _errorBar.IsOpen = false;
        _confirmPanel.Visibility = Visibility.Collapsed;
        _statusPanel.Visibility = Visibility.Collapsed;
        _actionsPanel.Visibility = Visibility.Visible;
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        _noticeBar.Severity = severity;
        _noticeBar.Title = title;
        _noticeBar.Message = message;
        _noticeBar.IsOpen = true;
    }

    private static void FocusAction(Button? button)
    {
        if (button is null || !button.IsLoaded)
        {
            return;
        }

        try
        {
            button.Focus(FocusState.Programmatic);
        }
        catch
        {
        }
    }

    private void ApplyContent(OpenCodePermissionRequest request)
    {
        var action = OpenCodePermissionPresentation.DescribeAction(request.Action);
        var resources = OpenCodePermissionPresentation.SummarizeResources(request.Resources);
        _headlineText.Inlines.Clear();
        _headlineText.Inlines.Add(new Run { Text = "助手想" + action + (resources is null ? "。" : "：") });
        if (resources is not null)
        {
            _headlineText.Inlines.Add(new Run { Text = resources, FontFamily = MonoFont });
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            _messageText.Text = string.Empty;
            _messageText.Visibility = Visibility.Collapsed;
        }
        else
        {
            _messageText.Text = request.Message;
            _messageText.Visibility = Visibility.Visible;
        }

        _detailId.Text = request.Id ?? "（未提供）";
        _detailAction.Text = string.IsNullOrWhiteSpace(request.Action) ? "（未提供）" : request.Action;
        _detailResources.Text = OpenCodePermissionPresentation.DescribeList(request.Resources);
        _detailSave.Text = request.Save is { Count: > 0 }
            ? OpenCodePermissionPresentation.DescribeList(request.Save)
            : "（未提供）";
        var source = OpenCodePermissionPresentation.DescribeSource(request.Source);
        _detailSource.Text = source ?? string.Empty;
        _detailSourceLabel.Visibility = _detailSource.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        _detailMessage.Text = request.Message ?? string.Empty;
        _detailMessageLabel.Visibility = _detailMessage.Visibility =
            string.IsNullOrWhiteSpace(request.Message) ? Visibility.Collapsed : Visibility.Visible;

        var name = "权限请求：助手想" + action + (resources is null ? string.Empty : "：" + resources);
        AutomationProperties.SetName(this, "权限请求：" + (request.Action ?? "（未提供动作）"));
        AutomationProperties.SetName(_presenter, name);
    }

    private static StackPanel ButtonRow() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private static TextBlock DetailLabel(string text)
        => OpenCodeInteractionUi.StyledText(text, OpenCodeInteractionUi.CaptionSubtleStyleKey);

    private static TextBlock DetailValue()
        => OpenCodeInteractionUi.StyledText(string.Empty, OpenCodeInteractionUi.CodeStyleKey, selectable: true);

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 6, 0, 0);
        return element;
    }

    private static void Place(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}
