using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

/// <summary>
/// 提问遮罩（重设计 wave3 F9）：与权限遮罩同一位置盖住输入卡片；取代原 ContentDialog 回答窗口。
/// 只负责呈现与焦点；提交/取消/重查的安全判定仍走 OpenCodeFormCard → 交互视图 → 页面的既有路径。
/// </summary>
/// <remarks>
/// 权限与提问同时待处理时权限遮罩优先，提问遮罩在权限遮罩关闭后显示。输入法组合进行中暂不覆盖
/// （避免打断组合与草稿），组合结束后的下一次刷新再覆盖。输入框留在可视树中、仅禁用，草稿原样保留；
/// 全部回答完后焦点回到输入框。
/// </remarks>
public sealed partial class OpenCodePage
{
    private readonly OpenCodeQuestionOverlay _questionOverlay = new();

    // 提问遮罩盖住输入区期间为 true：输入框留在可视树中但禁用，发送按钮不可用。
    private bool _composerCoveredByQuestion;

    /// <summary>输入区当前被权限或提问遮罩盖住（输入框禁用、草稿保留）。</summary>
    private bool ComposerCoveredByOverlay => _composerCoveredByPermission || _composerCoveredByQuestion;

    private void InitializeQuestionOverlay()
    {
        QuestionOverlayHost.Children.Add(_questionOverlay);
    }

    /// <summary>
    /// 先同步权限遮罩（优先），再同步提问遮罩；返回待处理权限数（与 <see cref="SyncPermissionOverlay"/> 相同语义，
    /// 页面据此计算“待处理交互”区条目数）。
    /// </summary>
    private int SyncComposerOverlays()
    {
        // 权限遮罩关闭时若焦点在其中，焦点应交给随后显示的提问遮罩，而不是落回被遮住的输入框。
        var permissionHadFocus = _composerCoveredByPermission && _permissionOverlay.ContainsFocus();
        var permissionCount = SyncPermissionOverlay();
        SyncQuestionOverlay(permissionHadFocus);
        return permissionCount;
    }

    private void SyncQuestionOverlay(bool permissionHadFocus)
    {
        var wasCovering = _composerCoveredByQuestion;
        // Sync can detach the focused editor; query ownership while it is still parented.
        var overlayHadFocus = wasCovering && _questionOverlay.ContainsFocus();
        var composerHadFocus = Composer.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;
        var count = _questionOverlay.Sync(_interactionsView.FormCards);
        var covering = _pageActive
            && count > 0
            && !_composerCoveredByPermission
            && (wasCovering || !_isComposing);
        if (covering == wasCovering)
        {
            return;
        }

        _composerCoveredByQuestion = covering;
        QuestionOverlayHost.Visibility = covering ? Visibility.Visible : Visibility.Collapsed;
        UpdateComposerButtons();

        if (covering && (composerHadFocus || permissionHadFocus))
        {
            // 焦点移到提问呈现根（不是任何输入或提交按钮），防止正在输入时的 Enter / 字符落进答案或误提交。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_composerCoveredByQuestion)
                {
                    _questionOverlay.FocusCurrent();
                }
            });
        }
        else if (!covering && overlayHadFocus && _pageActive && !_suppressComposerFocus)
        {
            // 全部回答完（或权限请求接管）：焦点回到输入框，草稿原样保留。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_composerCoveredByPermission)
                {
                    _permissionOverlay.FocusCurrent();
                }
                else if (!ComposerCoveredByOverlay && Composer.IsEnabled)
                {
                    Composer.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                }
            });
        }
    }
}
