namespace FluentCode.Pages.Settings;

/// <summary>
/// 离开应用内设置页（<see cref="SettingsPage"/>）时，对存在未保存输入的处理选择。
/// 由设置页在导航被拦下时经页内三态确认取得，供主窗口据此决定保存/放弃/留在本页。
/// </summary>
public enum SettingsLeaveChoice
{
    /// <summary>保存未提交的输入后再离开。</summary>
    Save = 0,

    /// <summary>放弃未提交的输入后离开。</summary>
    Discard = 1,

    /// <summary>取消离开，留在设置页。</summary>
    Cancel = 2,
}
