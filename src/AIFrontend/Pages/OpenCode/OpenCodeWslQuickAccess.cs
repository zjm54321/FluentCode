using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Pages.OpenCode;

internal sealed record WslQuickAccessTarget(string Distribution, string LinuxDirectory, string HostPath);
internal sealed record WslQuickAccessState(bool Visible, WslQuickAccessTarget? Target, string? Reason);
internal enum WslEditorOpenMode { RemoteWsl, HostFolder }
internal sealed record WslHostEditor(string Name, string? Executable, WslEditorOpenMode OpenMode, string? Reason);
internal sealed record WslEditorChoices(IReadOnlyList<WslHostEditor> Installed, WslHostEditor? Current)
{
    public static WslEditorChoices Resolve(IReadOnlyList<WslHostEditor> detected, string? preference)
    {
        var installed = new List<WslHostEditor>();
        foreach (var editor in detected) if (editor.Executable is not null) installed.Add(editor);
        return new(installed, installed.Find(editor => editor.Name == preference)
            ?? (installed.Count > 0 ? installed[0] : null));
    }
}
internal sealed class WslEditorRequestedEventArgs(string name, bool selectDefault = false) : EventArgs
{
    public string Name { get; } = name;
    public bool SelectDefault { get; } = selectDefault;
}

// Cache the asynchronous extraction, not UI controls (each icon needs its own visual owner).
internal sealed class WslEditorIconCache<T>(Func<string, Task<T?>> extract) where T : class
{
    private readonly Dictionary<string, Task<T?>> _icons = new(StringComparer.OrdinalIgnoreCase);
    internal Task<T?> GetAsync(string executable)
    {
        if (!_icons.TryGetValue(executable, out var icon)) _icons[executable] = icon = extract(executable);
        return icon;
    }
}
internal sealed record WslHostLaunch(string Executable, IReadOnlyList<string> Arguments)
{
    public ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        return info;
    }
}

// All external launching is behind this boundary. Headless tests inject a recording fake.
internal interface IWslQuickAccessLauncher
{
    Task OpenExplorerAsync(WslQuickAccessTarget target);
    Task OpenEditorAsync(WslHostLaunch launch);
}

internal static class OpenCodeWslQuickAccess
{
    // A menu choice persists and opens in the same action. Null selection is the primary/default action.
    internal static async Task LaunchEditorAsync(WslQuickAccessState state, IReadOnlyList<WslHostEditor> detected,
        string? preference, string? selection, Action<string> persistSelection, IWslQuickAccessLauncher launcher)
    {
        if (!state.Visible || state.Target is null) return;
        var choices = WslEditorChoices.Resolve(detected, preference);
        WslHostEditor? editor = choices.Current;
        if (selection is not null)
        {
            editor = null;
            foreach (var candidate in choices.Installed)
                if (candidate.Name == selection) { editor = candidate; break; }
        }
        if (editor is null) return;
        if (selection is not null) persistSelection(editor.Name);
        await LaunchAsync(state, editor, launcher);
    }

    public static WslHostLaunch ExplorerLaunch(WslQuickAccessTarget target, string windowsDirectory)
        => new(Path.Combine(windowsDirectory, "explorer.exe"), new[] { target.HostPath });

    public static Task LaunchAsync(WslQuickAccessState state, WslHostEditor? editor, IWslQuickAccessLauncher launcher)
    {
        if (!state.Visible || state.Target is not { } target) return Task.CompletedTask;
        return editor is null ? launcher.OpenExplorerAsync(target)
            : launcher.OpenEditorAsync(EditorLaunch(target, editor));
    }

    public static WslQuickAccessState Resolve(OpenCodeConnectionKind? kind, bool connected,
        string? distribution, string? directory, string? authoritativeHome = null)
    {
        if (kind != OpenCodeConnectionKind.Wsl || !connected) return new(false, null, null);
        if (!OpenCodeSettings.IsValidWslDistributionName(distribution))
            return new(true, null, "连接未提供有效的 WSL 发行版");
        if (directory is "~" || directory?.StartsWith("~/", StringComparison.Ordinal) == true)
        {
            if (authoritativeHome?.StartsWith('/') != true)
                return new(true, null, "无法展开 ~：尚未读取到此 WSL 发行版的用户主目录");
            directory = authoritativeHome.TrimEnd('/') + directory[1..];
        }
        if (string.IsNullOrEmpty(directory) || !directory.StartsWith('/'))
            return new(true, null, "等待会话提供绝对 Linux 工作目录");
        // UNC cannot represent these Linux filename characters without changing their meaning.
        foreach (var ch in directory)
            if (char.IsControl(ch) || ch is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return new(true, null, "此 Linux 路径含 Windows UNC 不支持的字符");
        var parts = new List<string>();
        foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return new(true, null, "工作目录越过了 Linux 根目录");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        var linux = "/" + string.Join('/', parts);
        return new(true, new(distribution!, linux, WslHostPathMapping.Fallback(distribution!, linux)), null);
    }

    public static WslHostLaunch EditorLaunch(WslQuickAccessTarget target, WslHostEditor editor)
    {
        if (editor.Executable is null)
            throw new InvalidOperationException("未检测到此主机编辑器");
        // VS Code's documented WSL CLI; other editors accept an ordinary Windows folder.
        // Host paths are mapped by wslpath (drive path for drvfs, UNC for Linux files).
        return new(editor.Executable, editor.OpenMode == WslEditorOpenMode.RemoteWsl
            ? new[] { "--remote", "wsl+" + target.Distribution, target.LinuxDirectory }
            : new[] { target.HostPath });
    }
}

internal interface IWslHostEditorEnvironment
{
    string? AppPath(string executable);
    string? EnvironmentVariable(string name);
    bool FileExists(string path);
}

internal static class WslHostEditorDetection
{
    public static IReadOnlyList<WslHostEditor> Detect(IWslHostEditorEnvironment host)
    {
        var result = new List<WslHostEditor>();
        foreach (var (name, exe, folder, mode) in new[] {
            ("VS Code", "Code.exe", "Microsoft VS Code", WslEditorOpenMode.RemoteWsl),
            ("VSCodium", "VSCodium.exe", "VSCodium", WslEditorOpenMode.HostFolder),
            ("Emacs", "runemacs.exe", "Emacs", WslEditorOpenMode.HostFolder),
            ("Zed", "zed.exe", "Zed", WslEditorOpenMode.HostFolder) })
        {
            var candidates = new List<string>();
            if (host.AppPath(exe) is { Length: > 0 } appPath) candidates.Add(appPath.Trim('"'));
            foreach (var env in new[] { "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)" })
                if (host.EnvironmentVariable(env) is { Length: > 0 } root)
                {
                    var prefix = root.TrimEnd('\\', '/') + (env == "LOCALAPPDATA" ? @"\Programs\" : "\\");
                    candidates.Add(prefix + folder + "\\" + exe);
                    candidates.Add(prefix + folder + @"\bin\" + exe);
                }
            foreach (var path in (host.EnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var root = path.Trim().Trim('"').TrimEnd('\\', '/');
                candidates.Add(root + "\\" + exe);
                // The official VS Code PATH entry is its bin directory. Resolve the native executable,
                // never execute code.cmd or cmd.exe, and never fall back to opening a UNC folder.
                if ((name == "VS Code" || name == "VSCodium") && root.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(root[..^4] + "\\" + exe);
            }
            var installed = candidates.Find(host.FileExists);
            result.Add(new(name, installed, mode, installed is null ? "未检测到安装" : null));
        }
        return result;
    }
}
