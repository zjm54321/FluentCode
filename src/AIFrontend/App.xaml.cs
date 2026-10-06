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

    public App()
    {
        this.InitializeComponent();

        this.UnhandledException += (sender, e) =>
        {
            try
            {
                File.AppendAllText(@"C:\Users\Zhang\source\repos\AIFrontend\artifacts\logs\crash.log",
                    $"{DateTime.Now:O}: {e.Exception.GetType().FullName}; HRESULT=0x{e.Exception.HResult:X8}\r\n");
            }
            catch { }
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_closing) return;
        try
        {
            if (_window is not null) { _window.Activate(); return; }
            if (!await BecomePrimaryInstanceAsync()) { Exit(); return; }
            if (_closing) return;
            var window = new MainWindow();
            _window = window;
            _appDispatcher = window.DispatcherQueue;
            window.AppWindow.Closing += OnAppWindowClosing;
            window.Closed += OnNotificationWindowClosed;
            window.Activate();
            RecordDiagnostic("taskbar-initialize", taskbar: InitializeTaskbar(WinRT.Interop.WindowNative.GetWindowHandle(window)));
            StartCompletionNotifications(window);
            StartOpenCodeOwnership();
            ReadInitialActivationAfterRegistration();
        }
        catch (Exception ex)
        {
            RecordDiagnostic("app-launch-failed", error: ex);
            if (_window is null) { _closing = true; StopActivationOwnership(); Exit(); }
        }
    }
}
