using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentCode.Pages;

/// <summary>
/// 会话页的浮动提示层（NoticeLayer）：SSH 安全提示、权限条、消息条和“状态未知”胶囊都盖在内容上方，
/// 不占布局行，所以出现、变化或关闭都不会推动时间线、输入区或首页。
/// 这里只“观察”四个具名元素（IsOpen / Visibility），不接管它们：ShowMessage 和其它直接写
/// MessageBar / PermissionBar 的代码照常工作。
/// </summary>
public sealed partial class OpenCodePage
{
    private bool _noticesWired;

    /// <summary>构造函数里调用一次；回调只挂在页面自己的子元素上，随页面一起回收。</summary>
    private void InitializeNotices()
    {
        if (_noticesWired) return;
        _noticesWired = true;
        WatchNoticeBar(SshConnectionAlert);
        WatchNoticeBar(PermissionBar);
        WatchNoticeBar(MessageBar);
        SessionStateHint.RegisterPropertyChangedCallback(VisibilityProperty, OnNoticeSourceChanged);
        // 浮层跟着它所属的界面一起隐藏：设置标签盖住会话区时、或工作区整体折叠（未配置）时不显示。
        SettingsTabHost.RegisterPropertyChangedCallback(VisibilityProperty, OnNoticeSourceChanged);
        WorkspaceGrid.RegisterPropertyChangedCallback(VisibilityProperty, OnNoticeSourceChanged);
        InitializeNoticeBounds();
        InitializeNoticeTiming();
        InitializeNoticeAirspace();
        RefreshNoticeLayer();
    }

    private void WatchNoticeBar(InfoBar bar)
    {
        bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, OnNoticeSourceChanged);
        bar.RegisterPropertyChangedCallback(VisibilityProperty, OnNoticeSourceChanged);
    }

    private void OnNoticeSourceChanged(DependencyObject sender, DependencyProperty property) => RefreshNoticeLayer();

    private static bool IsNoticeBarShown(InfoBar bar) => bar.IsOpen && bar.Visibility == Visibility.Visible;

    private static Visibility NoticeVisibility(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>每个底面只跟随自己的提示；全部关闭时整层折叠，不留任何可命中的区域。</summary>
    private void RefreshNoticeLayer()
    {
        bool ssh = IsNoticeBarShown(SshConnectionAlert);
        bool permission = IsNoticeBarShown(PermissionBar);
        bool message = IsNoticeBarShown(MessageBar);
        bool hint = SessionStateHint.Visibility == Visibility.Visible && _noticeHintRevealed;
        SshConnectionAlertSurface.Visibility = NoticeVisibility(ssh);
        PermissionBarSurface.Visibility = NoticeVisibility(permission);
        MessageBarSurface.Visibility = NoticeVisibility(message);
        SessionStateHintSurface.Visibility = NoticeVisibility(hint);

        bool hostShown = SettingsTabHost.Visibility != Visibility.Visible
            && WorkspaceGrid.Visibility == Visibility.Visible;
        bool shown = hostShown && (ssh || permission || message || hint);
        NoticeLayer.Visibility = NoticeVisibility(shown);
        PlayNoticeEntrance(shown);
        // 关闭时立即归还终端；打开时等这一轮布局完成后再按真实位置判断是否重叠。
        UpdateNoticeAirspace();
        if (shown) DispatcherQueue.TryEnqueue(UpdateNoticeAirspace);
        UpdateNoticeBounds();
    }
}
