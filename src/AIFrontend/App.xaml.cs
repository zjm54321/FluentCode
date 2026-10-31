using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace AIFrontend;

/// <summary>
/// 提供特定于应用程序的行为以补充默认的 Application 类。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    // UI-thread picker ownership only; never expose or manufacture a different window/handle.
    internal Window? OpenCodeOwnerWindow => _window;

    public App()
    {
        this.InitializeComponent();

        this.UnhandledException += (sender, e) =>
        {
            try
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "artifacts", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "crash.log"),
                    $"{DateTime.Now:O}: {e.Exception.GetType().FullName}; HRESULT=0x{e.Exception.HResult:X8}\r\n" +
                    $"XAML: {e.Message}\r\n{e.Exception}\r\n");
                if (StartupSmokeRoot is { } root)
                    File.AppendAllText(Path.Combine(root, "failed.txt"), $"{e.Message}\r\n{e.Exception}\r\n");
            }
            catch { }
            // 任何界面操作出错都不应让整个程序闪退：记录后标记已处理，并在顶部提示条告知用户。
            e.Handled = true;
            Exception error = e.Exception;
            (_window as MainWindow)?.DispatcherQueue.TryEnqueue(() => (_window as MainWindow)?.ShowUnexpectedError(error));
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_closing) return;
        try
        {
            if (_window is not null) { ShowMainWindow(); return; }
            if (!await BecomePrimaryInstanceAsync()) { Exit(); return; }
            if (_closing) return;
            // 用户字体（F11）必须在任何界面创建前写入应用资源；失败时保持默认字体，不阻断启动。
            try { await Services.Appearance.AppFonts.InitializeAsync(Pages.NormalPage.SharedSettingsService); }
            catch (Exception ex) { RecordDiagnostic("app-fonts-initialize-failed", error: ex); }
            if (_closing) return;
            InitializeMotion();
            var window = new MainWindow();
            _window = window;
            _appDispatcher = window.DispatcherQueue;
            window.AppWindow.Closing += OnAppWindowClosing;
            window.Closed += OnNotificationWindowClosed;
            window.Activate();
            if (StartupSmokeRoot is not null) { StartStartupSmoke(window); return; }
            InitializeBackgroundMode(window);
            RecordDiagnostic("taskbar-initialize", taskbar: InitializeTaskbar(WinRT.Interop.WindowNative.GetWindowHandle(window)));
            StartCompletionNotifications(window);
            StartOpenCodeOwnership();
            ReadInitialActivationAfterRegistration();
        }
        catch (Exception ex)
        {
            if (StartupSmokeRoot is { } root)
                File.AppendAllText(Path.Combine(root, "failed.txt"), ex.ToString());
            RecordDiagnostic("app-launch-failed", error: ex);
            if (_window is null) { _closing = true; StopActivationOwnership(); Exit(); }
        }
    }
}
