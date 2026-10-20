using System.Collections.Generic;
using System.Linq;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using AIFrontend.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AIFrontend.Pages;

/// <summary>
/// 输入区周边（重设计 v3 §5 / §8 / §9.1）：权限遮罩、四态发送按钮外观、排队条。
/// 只负责呈现与焦点；权限决定、发送/中断/排队的路由与安全判定仍走既有路径，不在此重复。
/// </summary>
public sealed partial class OpenCodePage
{
    private readonly OpenCodePermissionOverlay _permissionOverlay = new();
    private readonly OpenCodeQueuedPromptsView _queuedPromptsView = new();
    // 发送按钮内的小进度环：只在“正在发送”（按钮禁用）时显示。wave4 G6：工作中是普通停止按钮，不再用进度环环绕停止方块。
    private readonly ProgressRing _sendWorkingRing = new()
    {
        Width = 18,
        Height = 18,
        IsActive = false,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    // 权限遮罩盖住输入区期间为 true（重设计 v3 §5）：输入框留在可视树中但禁用，发送按钮不可用。
    private bool _composerCoveredByPermission;
    private string? _queuedInboxSessionId;
    private IReadOnlyList<OpenCodeInboxItem>? _queuedInbox;
    private OpenCodeComposerSendMode? _sendMode;
    private bool _sendHasDraft;

    /// <summary>EventSync owner calls after publishing/resetting foreground activity. Never changes draft/editor state.</summary>
    private void RefreshComposerSessionActivity() => UpdateComposerButtons();

    private bool IsComposerSessionWorking(string sessionId)
        => OpenCodeComposerActivityPolicy.IsWorking(
            _pageActive && SourceReady && !_sessionMissing
                && string.Equals(sessionId, _selectedSessionId, System.StringComparison.Ordinal),
            _liveSessionStatuses.TryGetValue(sessionId, out var live) ? live.Working : null,
            _workingSessions.Contains(sessionId));

    // Integration seam: the gate owner adds SessionControlKind.Interrupt and starts that exact slot.
    // No implementation means fail closed, NEVER send an unprotected mutation. Settlement uses the same lease/operation.
    partial void BeginComposerInterruptProtection(OpenCodeInteractionGate.Lease lease, System.Guid operationId,
        string fingerprint, ref bool started);

    private void InitializeComposerOverlays()
    {
        PermissionOverlayHost.Children.Add(_permissionOverlay);
        QueuedPromptsHost.Children.Add(_queuedPromptsView);
        InitializeQueuedWithdraw();
        InitializePendingDeliveryActions();
        _conversationView.BindingCleared += (_, _) => ClearSteerReceipts();
        _conversationView.PendingSteerWithdrawRequested += (_, id) => _queuedPromptsView.RequestWithdraw(id);
        _conversationView.PendingSteerDeliveryChangeRequested += (_, request) => _queuedPromptsView.RequestDeliveryChange(request);
        _queuedPromptsView.BusyChanged += (_, _) => _conversationView.SetPendingSteerBusy(_queuedPromptsView.IsWithdrawBusy);

        // 发送按钮内容：原图标 + 进度环叠放于同一 32×32 按钮内（几何契约 §14 不变）。
        var icon = SendStopIcon;
        SendStopButton.Content = null;
        var content = new Grid();
        content.Children.Add(_sendWorkingRing);
        content.Children.Add(icon);
        AutomationProperties.SetAccessibilityView(_sendWorkingRing, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        SendStopButton.Content = content;

        if (_composerView is not null)
        {
            _composerView.CurrentValueSource = ComposerCurrentValue;
            // 重设计 wave3 F1：输入框左侧唯一的“+”按钮（原生 Button + MenuFlyout：添加附件 / 命令… / 丢弃草稿…）。
            ComposerLeadingHost.Children.Add(_composerView.AddButton);
        }
    }

    /// <summary>命令菜单右侧“当前：…”的取值；无可靠来源时返回 null（不显示，绝不虚构）。</summary>
    private string? ComposerCurrentValue(OpenCodeComposerClientActionKind kind)
        => kind switch
        {
            // L7：与状态行只读显示同源（会话真实状态）；未读到时不显示。
            OpenCodeComposerClientActionKind.ChooseAgent => _agentSelector.CurrentAgentDisplay,
            OpenCodeComposerClientActionKind.ChooseModel => _agentSelector.CurrentModelDisplay,
            _ => null,
        };

    /// <summary>
    /// 按交互面板中的权限请求同步遮罩；返回待处理权限数。遮罩显示时输入框留在可视树中、仅禁用。
    /// 输入法组合进行中暂不覆盖（避免打断组合与草稿），组合结束后的下一次刷新（约 2 秒轮询）再覆盖。
    /// </summary>
    private int SyncPermissionOverlay()
    {
        var wasCovering = _composerCoveredByPermission;
        // Sync can detach the focused final presenter; query ownership while it is still parented.
        var overlayHadFocus = wasCovering && _permissionOverlay.ContainsFocus();
        var composerHadFocus = Composer.FocusState != FocusState.Unfocused;
        var count = _permissionOverlay.Sync(_interactionsView.Children.OfType<OpenCodePermissionCard>());
        var covering = _pageActive && count > 0 && (wasCovering || !_isComposing);
        if (covering == wasCovering)
        {
            return count;
        }

        _composerCoveredByPermission = covering;
        PermissionOverlayHost.Visibility = covering ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposerButtons();

        if (covering && composerHadFocus)
        {
            // 焦点移到请求呈现根（不是任何决定按钮），防止正在输入时的 Enter 被当作“允许”。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_composerCoveredByPermission)
                {
                    _permissionOverlay.FocusCurrent();
                }
            });
        }
        else if (!covering && overlayHadFocus && _pageActive && !_suppressComposerFocus)
        {
            // 全部处理完：焦点回到输入框，草稿原样保留。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_composerCoveredByPermission && Composer.IsEnabled)
                {
                    Composer.Focus(FocusState.Programmatic);
                }
            });
        }

        return count;
    }

    /// <summary>排队条：当前会话的服务端 inbox，包括其他客户端排入的消息；撤回权限仍单独判定。</summary>
    private void UpdateQueuedPrompts()
    {
        var show = _pageActive
            && _queuedInbox is { Count: > 0 }
            && _selectedSessionId is { } selected
            && string.Equals(_queuedInboxSessionId, selected, System.StringComparison.Ordinal);
        ReconcilePendingDeliveryUpdates(show ? _queuedInbox : null);
        // Keep the accepted snapshot intact for ownership/PATCH/withdrawal reconciliation; partition only the UI.
        var accepted = show ? _queuedInbox!.Where(item => item.SessionId == _selectedSessionId).ToArray() : null;
        var steer = ProjectSteerReceipts(accepted);
        _conversationView.ApplyAdmittedSteer(_pageActive && SourceReady ? _selectedSessionId : null, steer, CanWithdrawQueued, CanUpdatePendingDelivery);
        _conversationView.SetPendingSteerBusy(_queuedPromptsView.IsWithdrawBusy);
        _queuedPromptsView.Apply(accepted, CanWithdrawQueued, CanUpdatePendingDelivery);
        QueuedPromptsHost.Visibility = _queuedPromptsView.HasItems ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            _queuedInbox = null;
            _queuedInboxSessionId = null;
        }
    }

    /// <summary>四态发送按钮外观（同一按钮；只切换内容与可访问性文案，不改变可用性/路由判定）。</summary>
    private void SetSendStopMode(OpenCodeComposerSendMode mode, bool hasDraft)
    {
        if (_sendMode == mode && _sendHasDraft == hasDraft)
        {
            return;
        }

        var accentChanged = _sendMode is null
            || OpenCodeComposerSendPresentation.UsesAccent(_sendMode.Value) != OpenCodeComposerSendPresentation.UsesAccent(mode);
        _sendMode = mode;
        _sendHasDraft = hasDraft;

        if (accentChanged)
        {
            var style = OpenCodeInteractionUi.TryStyle(OpenCodeComposerSendPresentation.UsesAccent(mode) ? "AccentButtonStyle" : "DefaultButtonStyle");
            if (style is not null)
            {
                SendStopButton.Style = style;
            }
        }

        // 发送 / 排队：发送 / 时钟图标；工作中：普通的强调色停止按钮（只有停止图标，点击 = 停止；wave4 G6 不再套进度环）；
        // 正在发送：仅小进度环（按钮禁用）。
        var glyph = OpenCodeComposerSendPresentation.Glyph(mode);
        var ring = OpenCodeComposerSendPresentation.ShowsProgress(mode);
        _sendWorkingRing.IsActive = ring;
        _sendWorkingRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        SendStopIcon.Glyph = glyph ?? "\uE724";
        SendStopIcon.FontSize = OpenCodeComposerSendPresentation.GlyphSize(mode);
        SendStopIcon.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;

        AutomationProperties.SetName(SendStopButton, OpenCodeComposerSendPresentation.Name(mode));
        var tip = OpenCodeComposerSendPresentation.ToolTip(mode, hasDraft);
        AutomationProperties.SetHelpText(SendStopButton, tip);
        ToolTipService.SetToolTip(SendStopButton, tip);
    }
}
