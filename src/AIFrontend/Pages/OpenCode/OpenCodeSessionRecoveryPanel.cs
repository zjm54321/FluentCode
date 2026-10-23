using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>
/// 整会话恢复的显式入口与“整会话影响”确认面板（纯 UI；不发送任何请求，决定经事件回到页面）。
/// </summary>
/// <remarks>
/// <para>
/// 与单卡确认（权限卡片/表单卡片的“仍要重新决定 / 视为已结算”）相互独立：整会话保护来自一次
/// “拒绝结果未知”，会影响本会话其他待处理请求，因此必须单独确认、不复用单卡确认。
/// 入口在页面存在有效会话时常驻显示（不依赖待处理卡片或保护提示）；点击后由页面在共享租约内读取
/// 真实来源键与完整权限列表，再回填本面板的确认文案与按钮（来源仍待处理 → 提供“解除并允许重新决定”；
/// 来源已消失 → 仅“确认已结算”；没有保护 → 页面如实提示，不显示本确认）。
/// 页面切换会话时会隐藏确认并清除绑定快照；本面板的迟到回调不能复活旧确认。
/// </para>
/// <para>文案保持产品语言，不暴露内部仲裁术语；不含请求正文、资源或凭据。</para>
/// </remarks>
internal sealed class OpenCodeSessionRecoveryPanel : StackPanel
{
    private readonly StackPanel _entryPanel;
    private readonly TextBlock _entryText;
    private readonly Button _entryButton;
    private readonly StackPanel _confirmPanel;
    private readonly TextBlock _confirmText;
    private readonly Button _retryButton;
    private readonly Button _settleButton;
    private readonly Button _cancelButton;
    private readonly StackPanel _busyRow;
    private readonly ProgressRing _progress;
    private readonly TextBlock _busyText;
    private string _entryDetail = string.Empty;

    internal OpenCodeSessionRecoveryPanel()
    {
        Orientation = Orientation.Vertical;
        Spacing = 8;

        _entryText = OpenCodeInteractionUi.Caption(string.Empty, 0.75);
        _entryButton = new Button { Content = "整会话恢复…" };
        AutomationProperties.SetAutomationId(_entryButton, "OpenCodeSessionRecoveryEntry");
        AutomationProperties.SetName(_entryButton, "整会话恢复");
        _entryButton.Click += (_, _) => RecoveryRequested?.Invoke(this, EventArgs.Empty);
        var entryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        entryRow.Children.Add(_entryButton);
        _entryPanel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };
        _entryPanel.Children.Add(_entryText);
        _entryPanel.Children.Add(entryRow);

        _confirmText = OpenCodeInteractionUi.Caption(string.Empty, 0.9);
        _retryButton = new Button { Content = "解除并允许重新决定" };
        AutomationProperties.SetAutomationId(_retryButton, "OpenCodeSessionRecoveryRetry");
        _retryButton.Click += (_, _) => RaiseDecision(true);
        _settleButton = new Button { Content = "视为已结算" };
        AutomationProperties.SetAutomationId(_settleButton, "OpenCodeSessionRecoverySettle");
        _settleButton.Click += (_, _) => RaiseDecision(false);
        _cancelButton = new Button { Content = "返回" };
        AutomationProperties.SetAutomationId(_cancelButton, "OpenCodeSessionRecoveryCancel");
        _cancelButton.Click += (_, _) => HideConfirm();
        var confirmButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        confirmButtons.Children.Add(_retryButton);
        confirmButtons.Children.Add(_settleButton);
        confirmButtons.Children.Add(_cancelButton);

        _progress = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _busyText = OpenCodeInteractionUi.Caption(string.Empty, 0.9);
        _busyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        _busyRow.Children.Add(_progress);
        _busyRow.Children.Add(_busyText);

        _confirmPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        _confirmPanel.Children.Add(_confirmText);
        _confirmPanel.Children.Add(confirmButtons);

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_entryPanel);
        root.Children.Add(_confirmPanel);
        root.Children.Add(_busyRow);
        var chrome = new Border
        {
            Style = OpenCodeInteractionUi.TryStyle("OpenCodeCardChromeStyle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = root,
        };
        Children.Add(chrome);

        Visibility = Visibility.Collapsed;
    }

    /// <summary>用户点击恢复入口（页面据此在共享租约内核对真实来源键）。</summary>
    internal event EventHandler? RecoveryRequested;

    /// <summary>用户在“整会话影响”确认中做出选择（AllowRetry=true 表示显式接受可能重复生效）。</summary>
    internal event EventHandler<OpenCodeSessionRecoveryDecisionEventArgs>? DecisionRequested;

    internal bool IsShowingEntry => _entryPanel.Visibility == Visibility.Visible;

    internal bool IsShowingConfirm => _confirmPanel.Visibility == Visibility.Visible;

    internal bool IsShowing => IsShowingEntry || IsShowingConfirm || _busyRow.Visibility == Visibility.Visible;

    /// <summary>显示恢复入口（附产品化说明）。</summary>
    internal void ShowEntry(string detail)
    {
        _entryDetail = detail;
        _entryText.Text = detail;
        _entryPanel.Visibility = Visibility.Visible;
        UpdateVisibility();
    }

    /// <summary>隐藏恢复入口并清除入口文案。</summary>
    internal void HideEntry()
    {
        _entryDetail = string.Empty;
        _entryPanel.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    /// <summary>
    /// 显示“整会话影响”确认；<paramref name="allowRetry"/> 为 false 时不提供“重新决定”
    /// （原请求已消失，只能按已结算）。
    /// </summary>
    internal void ShowConfirm(string detail, bool allowRetry)
    {
        _confirmText.Text = detail;
        _retryButton.Visibility = allowRetry ? Visibility.Visible : Visibility.Collapsed;
        _settleButton.Content = allowRetry ? "视为已结算" : "确认已结算";
        _entryPanel.Visibility = Visibility.Collapsed;
        _confirmPanel.Visibility = Visibility.Visible;
        UpdateVisibility();
    }

    /// <summary>收起确认；此前有入口文案时恢复入口显示（页面仍持有整会话保护提示）。</summary>
    internal void HideConfirm()
    {
        _confirmPanel.Visibility = Visibility.Collapsed;
        if (_entryDetail.Length > 0)
        {
            _entryText.Text = _entryDetail;
            _entryPanel.Visibility = Visibility.Visible;
        }

        UpdateVisibility();
    }

    /// <summary>忙碌态：禁用全部按钮并显示进度与说明（结束后由页面恢复）。</summary>
    internal void SetBusy(bool busy, string? label)
    {
        _entryButton.IsEnabled = !busy;
        _retryButton.IsEnabled = !busy;
        _settleButton.IsEnabled = !busy;
        _cancelButton.IsEnabled = !busy;
        _busyRow.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsActive = busy;
        if (busy)
        {
            _busyText.Text = label ?? "正在处理…";
        }

        UpdateVisibility();
    }

    private void RaiseDecision(bool allowRetry)
    {
        // 先收起确认，避免重复点击；页面在成功或失败后重新呈现入口/提示。
        HideConfirm();
        DecisionRequested?.Invoke(this, new OpenCodeSessionRecoveryDecisionEventArgs(allowRetry));
    }

    private void UpdateVisibility()
    {
        Visibility = IsShowingEntry || IsShowingConfirm || _busyRow.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

/// <summary>整会话恢复确认结果：AllowRetry 为 true 表示用户显式接受“可能重复生效”。</summary>
internal sealed class OpenCodeSessionRecoveryDecisionEventArgs : EventArgs
{
    internal OpenCodeSessionRecoveryDecisionEventArgs(bool allowRetry) => AllowRetry = allowRetry;

    /// <summary>是否允许在解除后重新决定（仅原请求仍待处理且用户显式选择时为 true）。</summary>
    public bool AllowRetry { get; }
}
