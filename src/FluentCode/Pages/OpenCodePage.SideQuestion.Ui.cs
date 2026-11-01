using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private readonly Queue<(OpenCodePageSource Source, OpenCodeSideQuestionAnswer Answer)> _sideQuestionAnswers = new();
    private bool _sideQuestionAnswerShowing;

    partial void RenderSideQuestionPending(int pending) => _composerView?.SetSideQuestionPending(pending);

    partial void PresentSideQuestionAnswer(OpenCodePageSource source, OpenCodeSideQuestionAnswer answer)
    {
        _sideQuestionAnswers.Enqueue((source, answer));
        TryPresentSideQuestionAnswers();
    }

    partial void TryPresentSideQuestionAnswers()
    {
        if (_sideQuestionAnswerShowing || _sideQuestionAnswers.Count == 0) return;
        if (!_pageActive) { _sideQuestionAnswers.Clear(); return; }
        if (_nativeDialogPending || _nativeActionOwner is not null || _composerPickerPending || _isSending || XamlRoot is null) return;
        // Defer visual-tree changes out of composer TextChanging / controller publication callbacks.
        _sideQuestionAnswerShowing = true;
        if (!DispatcherQueue.TryEnqueue(() => _ = ShowNextSideQuestionAnswerAsync())) _sideQuestionAnswerShowing = false;
    }

    private async Task ShowNextSideQuestionAnswerAsync()
    {
        try
        {
            if (!_pageActive || _nativeDialogPending || _nativeActionOwner is not null || _composerPickerPending || _isSending
                || XamlRoot is null || _sideQuestionAnswers.Count == 0) return;
            var (source, answer) = _sideQuestionAnswers.Dequeue();
            if (source.IsRevoked || !ReferenceEquals(source, _source)) return;
            _nativeDialogPending = true;
            UpdateComposerButtons();
            RefreshComposerView();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(source.RevocationToken,
                _pageCts?.Token ?? CancellationToken.None);
            try
            {
                await OpenCodeSideQuestionDialog.ShowAnswerAsync(XamlRoot, answer,
                    _sessionItems.TryGetValue(answer.SessionId, out var origin) && !string.IsNullOrWhiteSpace(origin.Title)
                        ? origin.Title : answer.SessionId, lifetime.Token);
            }
            catch (Exception)
            {
                if (_pageActive && !source.IsRevoked)
                    ShowMessage(InfoBarSeverity.Warning, "/btw 回答未能显示", "问题：" + OpenCodeSideQuestionPolicy.QuestionLine(answer.Question));
            }
            finally
            {
                _nativeDialogPending = false;
                if (_pageActive) UpdateComposerButtons();
                RefreshComposerView();
            }
        }
        finally
        {
            _sideQuestionAnswerShowing = false;
            // A busy dialog will call RefreshComposerView when it closes; do not spin the dispatcher.
            TryPresentSideQuestionAnswers();
        }
    }
}
