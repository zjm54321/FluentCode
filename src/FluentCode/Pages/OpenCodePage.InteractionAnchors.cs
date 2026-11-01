using System;

namespace FluentCode.Pages;

/// <summary>
/// 提问条目在时间线中的放置（重设计 §4.1）：把交互视图的锚点解析接到会话视图的 question 工具锚点，
/// 时间线锚点变化（渲染 / 分页预插入 / 清空）后重新放置并刷新“待处理交互（N）”区头。
/// 只改变条目所在位置；提交 / 取消 / 重查 / 阻断状态机与回调完全不变。
/// </summary>
public sealed partial class OpenCodePage
{
    private void InitializeInteractionAnchors()
    {
        _interactionsView.AnchorResolver = (messageId, partId) => _conversationView.FindQuestionAnchor(messageId, partId);
        _conversationView.QuestionAnchorsChanged += OnQuestionAnchorsChanged;
    }

    private void OnQuestionAnchorsChanged(object? sender, EventArgs e)
    {
        _interactionsView.RefreshPlacement();
        UpdateInteractionsSection();
    }
}
