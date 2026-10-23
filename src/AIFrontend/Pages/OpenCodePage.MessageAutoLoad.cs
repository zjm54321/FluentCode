using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

/// <summary>
/// F3：消息历史自动加载的界面接线（替代原“加载更早消息”按钮）。分页游标、链状态、代际与单飞语义全部在
/// OpenCodePage.MessagePagination.cs；本文件只负责：订阅时间线 ViewChanged、顶部小进度环、以及在续页完成后
/// 用户仍停在顶部附近（或内容不足一屏）时继续加载下一页（每次都经同一受保护入口，异常/结束链不会被复活）。
/// </summary>
public sealed partial class OpenCodePage
{
    private bool _olderMessagesScrollHooked;
    private bool _olderMessagesWasLoading;
    private bool _olderMessagesWasMore;
    private string? _olderMessagesCursorAtStart;

    private void LoadOlderMessagesProgress_Loaded(object sender, RoutedEventArgs e) => HookOlderMessagesScroll();

    /// <summary>订阅时间线 ViewChanged（只一次；进度环 Loaded 与首次分页状态刷新都会尝试）。</summary>
    private void HookOlderMessagesScroll()
    {
        if (_olderMessagesScrollHooked || MessageScroll is null)
        {
            return;
        }

        _olderMessagesScrollHooked = true;
        MessageScroll.ViewChanged += (_, _) => OnMessageScrollViewChangedForHistory();
    }

    partial void OnOlderMessagesUiChanged(bool hasMore, bool canLoad, bool loading)
    {
        HookOlderMessagesScroll();
        if (LoadOlderMessagesProgress is { } ring)
        {
            var show = hasMore && loading;
            ring.IsActive = show;
            ring.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!_olderMessagesWasLoading && loading)
        {
            _olderMessagesCursorAtStart = _messageNextCursor;
        }

        // 只有成功推进了游标的续页才允许继续（失败/取消时游标不变：绝不自动重试，避免错误时反复请求）。
        var finished = _olderMessagesWasLoading && !loading
            && !string.Equals(_olderMessagesCursorAtStart, _messageNextCursor, System.StringComparison.Ordinal);
        var becameAvailable = !_olderMessagesWasMore && hasMore;
        _olderMessagesWasLoading = loading;
        _olderMessagesWasMore = hasMore;
        if (!canLoad || !(finished || becameAvailable))
        {
            return;
        }

        // 布局完成后复核（低优先级）：续页刚完成且仍在顶部阈值内 → 继续；内容已布局但不足一屏 → 继续。
        // 刚建立链（首屏）时只处理“不足一屏”，绝不因首屏滚底前的 0 偏移误触发。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!CanAutoLoadOlderMessages())
            {
                return;
            }

            var laidOut = MessageScroll.ExtentHeight > 0 && MessageScroll.ViewportHeight > 0;
            var notFilling = laidOut && MessageScroll.ScrollableHeight <= 0;
            var stillAtTop = finished && laidOut && MessageScroll.ScrollableHeight > 0
                && MessageScroll.VerticalOffset <= OlderMessagesAutoLoadThreshold;
            if (notFilling || stillAtTop)
            {
                _ = LoadOlderMessagesAsync();
            }
        });
    }
}
