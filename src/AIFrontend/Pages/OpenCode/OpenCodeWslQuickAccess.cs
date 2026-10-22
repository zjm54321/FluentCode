using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AIFrontend.Models;

namespace AIFrontend.Pages.OpenCode;

internal sealed record WslQuickAccessTarget(string Distribution, string LinuxDirectory, string UncPath);
internal sealed record WslQuickAccessState(bool Visible, WslQuickAccessTarget? Target, string? Reason);
internal sealed record WslHostEditor(string Name, string? Executable, bool VerifiedWsl, string? Reason);
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
    public static WslHostLaunch ExplorerLaunch(WslQuickAccessTarget target, string windowsDirectory)
        => new(Path.Combine(windowsDirectory, "explorer.exe"), new[] { target.UncPath });

    public static Task LaunchAsync(WslQuickAccessState state, WslHostEditor? editor, IWslQuickAccessLauncher launcher)
    {
        if (!state.Visible || state.Target is not { } target) return Task.CompletedTask;
        return editor is null ? launcher.OpenExplorerAsync(target)
            : launcher.OpenEditorAsync(VsCodeLaunch(target, editor));
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
                return new(true, null, "无法展开 ~：服务端未提供此发行版的用户主目录");
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
        return new(true, new(distribution!, linux,
            @"\\wsl.localhost\" + distribution + "\\" + string.Join('\\', parts)), null);
    }

    public static WslHostLaunch VsCodeLaunch(WslQuickAccessTarget target, WslHostEditor editor)
    {
        if (editor.Name != "VS Code" || !editor.VerifiedWsl || editor.Executable is null)
            throw new InvalidOperationException("未检测到支持 WSL 远程模式的 VS Code");
        return new(editor.Executable, new[] { "--remote", "wsl+" + target.Distribution, target.LinuxDirectory });
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
        foreach (var (name, exe, folder) in new[] {
            ("VS Code", "Code.exe", "Microsoft VS Code"),
            ("VSCodium", "VSCodium.exe", "VSCodium"),
            ("Emacs", "runemacs.exe", "Emacs"),
            ("Zed", "zed.exe", "Zed") })
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
                if (name == "VS Code" && root.EndsWith(@"\bin", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(root[..^4] + "\\" + exe);
            }
            var installed = candidates.Find(host.FileExists);
            var verified = name == "VS Code";
            result.Add(new(name, installed, verified, installed is null ? "未检测到安装"
                : !verified ? "已安装；尚未核实此编辑器的 WSL 唤起方式" : null));
        }
        return result;
    }
}
