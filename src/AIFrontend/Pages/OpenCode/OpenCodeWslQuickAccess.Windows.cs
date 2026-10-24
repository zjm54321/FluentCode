using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AIFrontend.Pages.OpenCode;

internal sealed class WindowsWslHostEditorEnvironment : IWslHostEditorEnvironment
{
    public string? EnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
    public bool FileExists(string path) => File.Exists(path);
    public string? AppPath(string executable)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
                if (key?.GetValue(null) is string value) return value;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return null;
    }
}

internal sealed class WindowsWslQuickAccessLauncher : IWslQuickAccessLauncher
{
    public Task OpenExplorerAsync(WslQuickAccessTarget target)
    {
        // A WinRT "accepted" result is not proof that Explorer opened a WSL folder.
        // Use Explorer's native path argument directly for both drive paths and Linux UNC.
        Start(OpenCodeWslQuickAccess.ExplorerLaunch(target, Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        return Task.CompletedTask;
    }

    public Task OpenEditorAsync(WslHostLaunch launch)
    {
        Start(launch);
        return Task.CompletedTask;
    }

    private static void Start(WslHostLaunch launch)
    {
        using var process = Process.Start(launch.StartInfo())
            ?? throw new InvalidOperationException("无法启动主机应用");
    }
}
