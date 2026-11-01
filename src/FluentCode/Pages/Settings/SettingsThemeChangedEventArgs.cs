using System;
using FluentCode.Models;

namespace FluentCode.Pages.Settings;

/// <summary>
/// <see cref="SettingsView.ThemeChanged"/> 的事件参数：携带已成功持久化的新主题。
/// </summary>
/// <remarks>
/// 仅当主题选择已通过 <c>SettingsService.SaveAsync</c> 成功写入后才触发；
/// 订阅方（如主窗口）据此把主题应用到自己的根元素（<c>FrameworkElement.RequestedTheme</c>）。
/// 本类不做任何主题应用或持久化，仅承载取值。
/// </remarks>
public sealed class SettingsThemeChangedEventArgs : EventArgs
{
    /// <summary>创建事件参数。</summary>
    /// <param name="theme">已持久化的新主题。</param>
    public SettingsThemeChangedEventArgs(AppTheme theme)
    {
        Theme = theme;
    }

    /// <summary>已成功持久化的新主题。</summary>
    public AppTheme Theme { get; }
}
