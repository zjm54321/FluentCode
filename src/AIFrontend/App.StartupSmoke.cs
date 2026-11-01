using System;
using System.IO;

namespace AIFrontend;

public partial class App
{
    // Opt-in test process only: distinct AppInstance key, disposable storage/WebView profile,
    // and no tray/taskbar/toast registration or backend ownership alongside a user's instance.
    private static string? StartupSmokeRoot => Environment.GetEnvironmentVariable("AIF_STARTUP_SMOKE_ROOT")
        is { Length: > 0 } root ? Path.GetFullPath(root) : null;

    private void InitializeStartupSmokeDiagnostics()
    {
        if (StartupSmokeRoot is not { } root || Environment.GetEnvironmentVariable("AIF_THEME_REFRESH_TEST") != "1"
            && Environment.GetEnvironmentVariable("AIF_TRANSCRIPT_INPUT_TEST") != "1") return;
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, args) => File.AppendAllText(Path.Combine(root, "resource-errors.txt"), args.Message + Environment.NewLine);
    }

    private static void StartStartupSmoke(MainWindow window)
    {
        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Environment.GetEnvironmentVariable("AIF_TRANSCRIPT_INPUT_TEST") == "1")
                {
                    var failures = await window.VerifyTranscriptInputAsync(StartupSmokeRoot!);
                    if (failures > 0) { File.WriteAllText(Path.Combine(StartupSmokeRoot!, "failed.txt"), $"transcript input: {failures} failures"); return; }
                    File.WriteAllText(Path.Combine(StartupSmokeRoot!, "ready.txt"),
                        $"PID={Environment.ProcessId}; HWND={WinRT.Interop.WindowNative.GetWindowHandle(window)}; Base={AppContext.BaseDirectory}");
                    return;
                }
                if (Environment.GetEnvironmentVariable("AIF_THEME_REFRESH_TEST") == "1")
                {
                    await window.VerifyThemeRefreshAsync(StartupSmokeRoot!);
                    File.WriteAllText(Path.Combine(StartupSmokeRoot!, "ready.txt"),
                        $"PID={Environment.ProcessId}; HWND={WinRT.Interop.WindowNative.GetWindowHandle(window)}; Base={AppContext.BaseDirectory}");
                    return;
                }
                window.VerifyStartupMotion(() =>
                    File.WriteAllText(Path.Combine(StartupSmokeRoot!, "ready.txt"),
                        $"PID={Environment.ProcessId}; HWND={WinRT.Interop.WindowNative.GetWindowHandle(window)}; Base={AppContext.BaseDirectory}"),
                    ex => File.WriteAllText(Path.Combine(StartupSmokeRoot!, "failed.txt"), ex.ToString()));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(StartupSmokeRoot!, "failed.txt"), ex.ToString()); }
        };
        timer.Start();
    }
}
