using System;
using FluentCode.Services.Configuration;
using FluentCode.Services.OpenCode.Connection;
using Microsoft.UI.Xaml;

namespace FluentCode;

/// <summary>
/// 首次运行欢迎页（方案 A）：尚未添加任何 OpenCode（或在设置 › 账号与连接中断开）时整窗显示，
/// 标题栏只保留拖动区与系统窗口按钮。连接成功后（设置中出现有效连接）短暂保留成功状态，再切到首页标签。
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan WelcomeSuccessHold = TimeSpan.FromMilliseconds(800);

    private Pages.Welcome.WelcomeView? _welcome;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _welcomeExitTimer;
    private bool _welcomeSubscribed;

    private bool IsWelcomeShown => _welcome is not null;

    private void InitializeWelcome()
    {
        AppServices.Settings.Changed += Settings_ChangedForWelcome;
        _welcomeSubscribed = true;
        // Current 为空表示设置尚未加载：此时不猜测，等待加载完成的 Changed。
        if (AppServices.Settings.Current is { } current && !IsConfigured(current.OpenCode)) ShowWelcome();
    }

    private static bool IsConfigured(Models.OpenCodeSettings? settings) => OpenCodeConnectionProfile.Describe(settings).IsConfigured;

    private void Settings_ChangedForWelcome(object? sender, AppSettingsChangedEventArgs e)
    {
        bool configured = IsConfigured(e.Settings.OpenCode);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_titleBarClosed) return;
            // 以最新快照为准（多次变更可能乱序到达）。
            if (AppServices.Settings.Current is { } latest) configured = IsConfigured(latest.OpenCode);
            if (!configured)
            {
                _welcomeExitTimer?.Stop();
                ShowWelcome();
            }
            else if (_welcome is not null)
            {
                ScheduleWelcomeExit();
            }
        });
    }

    private void ShowWelcome()
    {
        if (_welcome is not null) return;
        var welcome = new Pages.Welcome.WelcomeView(AppServices.Settings, AppServices.Secrets);
        _welcome = welcome;
        WelcomeHost.Children.Clear();
        WelcomeHost.Children.Add(welcome);
        WelcomeHost.Visibility = Visibility.Visible;
        ContentFrame.Visibility = Visibility.Collapsed;
        Services.Appearance.AppFonts.RegisterRoot(welcome);
        SetSessionWindowTitle(null);
        UpdateTitleBarTarget();
    }

    /// <summary>成功状态停留片刻（用户能看到“已连接”），然后进入首页；进行中的操作结束前不切走。</summary>
    private void ScheduleWelcomeExit()
    {
        _welcomeExitTimer ??= CreateWelcomeExitTimer();
        _welcomeExitTimer.Stop();
        _welcomeExitTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateWelcomeExitTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = WelcomeSuccessHold;
        timer.IsRepeating = false;
        timer.Tick += WelcomeExitTimer_Tick;
        return timer;
    }

    private void WelcomeExitTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
    {
        timer.Stop();
        if (_welcome is { IsBusy: true }) { timer.Start(); return; }
        timer.Tick -= WelcomeExitTimer_Tick;
        _welcomeExitTimer = null;
        if (AppServices.Settings.Current is { } latest && IsConfigured(latest.OpenCode)) HideWelcome();
    }

    private void HideWelcome()
    {
        if (_welcome is not { } welcome) return;
        _welcome = null;
        welcome.Close();
        WelcomeHost.Children.Clear();
        WelcomeHost.Visibility = Visibility.Collapsed;
        ContentFrame.Visibility = Visibility.Visible;
        EnsureOpenCodePage();
        if (ContentFrame.Content is Pages.OpenCodePage page)
        {
            page.ShowHomeTab();
            SetSessionWindowTitle(page.CurrentSessionTitle);
        }
        UpdateTitleBarTarget();
    }

    private void ShutdownWelcome()
    {
        if (_welcomeSubscribed)
        {
            AppServices.Settings.Changed -= Settings_ChangedForWelcome;
            _welcomeSubscribed = false;
        }
        if (_welcomeExitTimer is not null)
        {
            _welcomeExitTimer.Stop();
            _welcomeExitTimer.Tick -= WelcomeExitTimer_Tick;
            _welcomeExitTimer = null;
        }
        _welcome?.Close();
        _welcome = null;
    }
}
