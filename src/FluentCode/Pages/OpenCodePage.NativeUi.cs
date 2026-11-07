// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.NativeUi;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private void OnNativeUiAssistantRequested(object? sender, NativeUiAssistantRequestedEventArgs args)
    {
        // A card's event is a user intent, not send authority. Bind it to this live transcript, source and session.
        if (!DispatcherQueue.HasThreadAccess || !ReferenceEquals(sender, _conversationView) || !_pageActive || !SourceReady
            || _selectedSessionId is not { } session || _sessionMissing || IsChildComposerSession || _shellComposer.Active
            || _composerController is null || _composerView is null || _composerBinding is not { } binding || !OwnsComposerBinding(binding)
            || !_conversationView.ContainsRenderedMessage(args.ParentMessageId) || HasPendingNativeAction() || ComposerCoveredByOverlay
            || _isSending || _isComposing || _composerPickerPending || _composerAgentApplyPending || !_composerView.PrepareClientTextApplication())
        { args.Notice = "当前输入区不可用或有待处理操作，未发送"; return; }
        var snapshot = GetComposerSnapshot();
        if (!snapshot.IsActive || snapshot.IsBusy || snapshot.IsProtected || snapshot.HasDraft)
        { args.Notice = snapshot.HasDraft ? "输入区已有草稿；请先处理草稿，再点击卡片（不会覆盖）" : "输入区受保护，未发送"; return; }
        NativeUiComposerIntent intent;
        try { intent = NativeUiComposerPolicy.Create(args.Request, IsComposerSessionWorking(session), _composerDeliverySupported, NativeUiCatalog.Default.Limits); }
        catch (NativeUiLimitException error) { args.Notice = error.Message; return; }
        if (!intent.Allowed) { args.Notice = intent.Notice; return; }
        _composerController.SetText(snapshot.Token, intent.Text);
        var prepared = GetComposerSnapshot();
        if (!OwnsComposerBinding(binding) || !prepared.IsActive || prepared.Text != intent.Text)
        { args.Notice = "会话或草稿已变化，未发送"; return; }
        // Reconcile through the existing guarded editor writer before the ordinary submission boundary.
        _composerView.Render(prepared);
        RequestComposerSubmission(intent.Delivery); // busy => queue; then RouteComposerSubmissionAsync -> shared lease/send flow.
        args.Accepted = true; args.Notice = intent.Notice;
    }
}
