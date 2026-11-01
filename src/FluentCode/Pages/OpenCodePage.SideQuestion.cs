using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

// A9: official /btw side question. One-shot session.generate from the session's context; the answer is ephemeral
// (native dialog) and NEVER becomes a prompt, a queued/steered message or a history row. Allowed while busy.
// UI-only glue (dialog queue, pending pill) lives in OpenCodePage.SideQuestion.Ui.cs behind partial methods.
public sealed partial class OpenCodePage
{
    private readonly Dictionary<Guid, OpenCodeSideQuestionRequest> _sideQuestionsPending = new();
    // Last unanswered question per session: pre-fills the next /btw prompt so a failure never loses the text.
    private readonly Dictionary<string, string> _sideQuestionRetry = new(StringComparer.Ordinal);

    /// <summary>Pending /btw count; the composer shows a "/btw" indicator while any is in flight.</summary>
    internal int SideQuestionsPending => _sideQuestionsPending.Count;

    partial void RenderSideQuestionPending(int pending);
    partial void PresentSideQuestionAnswer(OpenCodePageSource source, OpenCodeSideQuestionAnswer answer);
    partial void TryPresentSideQuestionAnswers();

    /// <summary>
    /// Submit-path interception, before any send/queue/steer/create route. Returns true when the draft was a
    /// "/btw" request; the submission is then fully consumed here and no prompt transport is ever reached.
    /// A server command named "btw" that the user explicitly selected (chip) is NOT intercepted.
    /// </summary>
    private bool TryHandleSideQuestionSubmission(OpenCodeComposerSnapshot snapshot)
    {
        if (snapshot.SelectedCommand is not null || snapshot.ClientIntent is not null
            || !OpenCodeSideQuestionPolicy.TryParse(snapshot.Text, out var question)) return false;
        if (snapshot.Attachments.Count != 0)
        {
            ShowMessage(InfoBarSeverity.Warning, "/btw 不支持附件", "顺便问一句只发送文字问题；请先移除附件，草稿保持不变。");
            return true;
        }
        if (snapshot.IsUnboundDraft || _composerBinding is not { } binding || binding.SessionId.Length == 0
            || !OwnsComposerBinding(binding))
        {
            ShowMessage(InfoBarSeverity.Informational, "请先打开一个会话", "/btw 基于已有会话的上下文回答；开始或打开会话后再问。草稿保持不变。");
            return true;
        }
        if (question.Length == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, "/btw 需要一个问题", "请输入 /btw <问题>，例如：/btw 刚才为什么选择这个方案？草稿保持不变。");
            return true;
        }
        // Clear the trigger draft now (official clears the prompt); on failure the question is offered back.
        _composerController!.DiscardDraft(snapshot.Token);
        RefreshComposerView();
        StartSideQuestion(binding, question);
        return true;
    }

    /// <summary>Command-list path: /btw chosen from the menu (no argument) → ask for the question.</summary>
    private async Task AskNativeSideQuestionAsync(NativeActionOwner owner, string remaining)
    {
        var binding = owner.Ticket.Binding;
        if (binding.SessionId.Length == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, "请先打开一个会话", "/btw 基于已有会话的上下文回答；开始或打开会话后再问。草稿保持不变。");
            return;
        }
        var question = remaining.Trim();
        if (question.Length == 0) question = await AskSideQuestionTextAsync(binding.SessionId, string.Empty);
        if (question is null || !CanStartNativeAction(owner, owner.Origin) || binding.Source.IsRevoked) return;
        _composerController!.DiscardDraft(owner.Ticket.Token);
        RefreshComposerView();
        StartSideQuestion(binding, question);
    }

    private async Task<string?> AskSideQuestionTextAsync(string sessionId, string initial)
    {
        if (initial.Length == 0 && _sideQuestionRetry.TryGetValue(sessionId, out var retained)) initial = retained;
        _nativeDialogPending = true;
        UpdateComposerButtons();
        RefreshComposerView();
        string? question;
        try { question = await OpenCodeSideQuestionDialog.PromptAsync(XamlRoot, initial); }
        finally
        {
            _nativeDialogPending = false;
            if (_pageActive) UpdateComposerButtons();
            RefreshComposerView();
        }
        question = question?.Trim();
        return string.IsNullOrEmpty(question) ? null : question;
    }

    /// <summary>
    /// Fire-and-forget, pinned to the asking session's exact client/source. Several may be in flight; the asking
    /// session may be busy. Switching sessions/tabs does not cancel it; source revocation or page teardown does.
    /// </summary>
    private void StartSideQuestion(OpenCodeComposerBinding binding, string question)
    {
        var request = new OpenCodeSideQuestionRequest(Guid.NewGuid(), binding.SessionId, question);
        _sideQuestionsPending[request.Id] = request;
        RenderSideQuestionPending(_sideQuestionsPending.Count);
        _ = RunSideQuestionAsync(binding.Source, binding.Client, request);
    }

    private async Task RunSideQuestionAsync(OpenCodePageSource source, OpenCodeClient client, OpenCodeSideQuestionRequest request)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken,
            _pageCts?.Token ?? CancellationToken.None);
        try
        {
            Task<string>? pending = null;
            source.TryExecute(lifetime.Token, () => pending = client.GenerateSessionTextAsync(request.SessionId,
                OpenCodeClient.BuildSideQuestionPrompt(request.Question), lifetime.Token));
            if (pending is null) throw new OperationCanceledException(lifetime.Token);
            var text = (await pending).Trim();
            if (lifetime.IsCancellationRequested || source.IsRevoked || !_pageActive || !ReferenceEquals(source, _source)) return;
            if (_sideQuestionRetry.TryGetValue(request.SessionId, out var retained) && retained == request.Question)
                _sideQuestionRetry.Remove(request.SessionId);
            PresentSideQuestionAnswer(source, new(request.Id, request.SessionId, request.Question, text));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || source.IsRevoked)
        {
            // Connection replaced or page torn down: nothing to show, nothing was written anywhere.
            _sideQuestionRetry[request.SessionId] = request.Question;
        }
        catch (Exception error)
        {
            _sideQuestionRetry[request.SessionId] = request.Question;
            if (!_pageActive || source.IsRevoked || !ReferenceEquals(source, _source)) return;
            var restored = TryRestoreSideQuestionDraft(request);
            ShowMessage(InfoBarSeverity.Warning, "/btw 未能回答",
                OpenCodeSideQuestionPolicy.FailureReason(error) + (restored
                    ? "问题已放回输入框，按 Enter 可重试。"
                    : "从命令菜单选择 /btw 时会预填这个问题：" + OpenCodeSideQuestionPolicy.QuestionLine(request.Question)));
        }
        finally
        {
            _sideQuestionsPending.Remove(request.Id);
            RenderSideQuestionPending(_sideQuestionsPending.Count);
        }
    }

    /// <summary>Puts "/btw question" back only into the SAME session's empty, idle composer; never another session.</summary>
    private bool TryRestoreSideQuestionDraft(OpenCodeSideQuestionRequest request)
    {
        if (!_pageActive || _composerController is null || _composerView is null
            || _composerBinding is not { } binding || binding.SessionId != request.SessionId || !OwnsComposerBinding(binding)
            || HasPendingNativeAction() || _isSending || !_composerView.PrepareSubmission()) return false;
        var snapshot = _composerController.Snapshot;
        if (!snapshot.IsActive || snapshot.HasDraft || snapshot.IsBusy || snapshot.Text.Length != 0) return false;
        _composerController.SetText(snapshot.Token, OpenCodeSideQuestionPolicy.RetryDraft(request.Question));
        RefreshComposerView();
        return _composerController.Snapshot.Text == OpenCodeSideQuestionPolicy.RetryDraft(request.Question);
    }
}
