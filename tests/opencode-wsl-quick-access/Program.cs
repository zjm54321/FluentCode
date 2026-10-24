using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool condition, string name)
    {
        _assertions++;
        if (!condition) throw new Exception(name);
    }

    public static async Task Main()
    {
        // No real launcher, registry, network, or process access; preference IO uses an isolated temp directory.
        foreach (var kind in new OpenCodeConnectionKind?[] { null, OpenCodeConnectionKind.Remote, OpenCodeConnectionKind.Wsl })
        foreach (var connected in new[] { false, true })
        {
            var state = OpenCodeWslQuickAccess.Resolve(kind, connected, "测试 Distro", "/work");
            Check(state.Visible == (kind == OpenCodeConnectionKind.Wsl && connected), "persisted kind + connection gate (URLs never used)");
            var fake = new RecordingLauncher();
            await OpenCodeWslQuickAccess.LaunchAsync(state, null, fake);
            Check(fake.Explorers.Count == (state.Visible ? 1 : 0), "hidden state cannot launch");
        }
        Console.WriteLine("PASS visibility gating (Remote includes localhost: URL-independent)");

        WslQuickAccessState Resolve(string? path, string? home = null, string? distro = "发行版 Test")
            => OpenCodeWslQuickAccess.Resolve(OpenCodeConnectionKind.Wsl, true, distro, path, home);
        var special = Resolve("/home/真实 用户/中文 项目/$d & 'x';next");
        Check(special.Target?.HostPath == @"\\wsl.localhost\发行版 Test\home\真实 用户\中文 项目\$d & 'x';next", "UNC preserves argument data");
        Check(Resolve("/").Target?.HostPath == @"\\wsl.localhost\发行版 Test\", "root UNC");
        Check(Resolve("/a/./b/../c//").Target?.LinuxDirectory == "/a/c", "normalize Linux components");
        Check(Resolve("~/项目 空格", "/home/动态 用户").Target?.LinuxDirectory == "/home/动态 用户/项目 空格", "tilde expands from actual home only");
        Check(Resolve("~", "/root").Target?.LinuxDirectory == "/root", "bare tilde");
        foreach (var path in new[] { "~", "~/a", "~other/a", "relative", "", "/../escape", "/bad\\name", "/bad\"name", "/bad\nname" })
            Check(Resolve(path).Target is null && Resolve(path).Reason is not null, "unresolved/invalid path disabled: " + path);
        Check(Resolve(null).Target is null, "unknown directory disabled");
        Check(Resolve("/a", distro: "../bad").Target is null, "invalid distro disabled");
        Check(Resolve("/literal/~folder").Target?.LinuxDirectory == "/literal/~folder", "literal inner tilde untouched");
        var explorerInfo = OpenCodeWslQuickAccess.ExplorerLaunch(special.Target!, @"C:\Windows").StartInfo();
        Check(explorerInfo.FileName == @"C:\Windows\explorer.exe" && !explorerInfo.UseShellExecute,
            "Explorer narrow host fallback without shell");
        Check(explorerInfo.ArgumentList.Count == 1 && explorerInfo.ArgumentList[0] == special.Target!.HostPath,
            "Explorer receives a single UNC argument");
        Console.WriteLine("PASS UNC + Unicode/spaces/special chars + real-data tilde resolution");

        Check(WslQuickAccessHome.NeedsHome("~") && WslQuickAccessHome.NeedsHome("~/a")
            && !WslQuickAccessHome.NeedsHome("/home/absolute") && !WslQuickAccessHome.NeedsHome("~someone/a"), "only own tilde requests HOME");
        var homeRunner = new FakeHomeRunner { Output = "/srv/真实 用户\n" };
        var authoritative = await WslQuickAccessHome.ResolveAsync("发行版 Test", @"C:\Windows\System32\wsl.exe", homeRunner, CancellationToken.None);
        var homeCommand = homeRunner.Commands.Single().StartInfo();
        Check(!homeCommand.UseShellExecute && homeCommand.Arguments == "" && homeCommand.FileName == @"C:\Windows\System32\wsl.exe"
            && homeCommand.ArgumentList.SequenceEqual(new[] { "--distribution", "发行版 Test", "--exec", "printenv", "HOME" }), "HOME uses selected distro, no Linux/Windows shell or interpolated command");
        Check(Resolve("~/a", authoritative).Target?.HostPath == @"\\wsl.localhost\发行版 Test\srv\真实 用户\a", "actual distro HOME flows into tilde UNC resolution");
        Check(Resolve("~", authoritative).Target?.LinuxDirectory == "/srv/真实 用户", "bare tilde flows from authoritative source");
        homeRunner.Output = "/root\r\n";
        Check(await WslQuickAccessHome.ResolveAsync("Other Distro", "wsl.exe", homeRunner, CancellationToken.None) == "/root"
            && homeRunner.Commands.Last().Arguments[1] == "Other Distro", "distro and root-user home are dynamic");
        foreach (var output in new string?[] { null, "", "relative\n", "/home/a\n/home/b\n", "/bad\0name\n" })
        {
            homeRunner.Output = output;
            Check(await WslQuickAccessHome.ResolveAsync("发行版 Test", "wsl.exe", homeRunner, CancellationToken.None) is null,
                "missing/failed/malformed HOME never invents a user directory");
        }
        var previousReads = homeRunner.Commands.Count;
        Check(await WslQuickAccessHome.ResolveAsync("../invalid", "wsl.exe", homeRunner, CancellationToken.None) is null
            && homeRunner.Commands.Count == previousReads, "invalid distro never runs a command");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var observedCancellation = false;
            try { await WslQuickAccessHome.ResolveAsync("发行版 Test", "wsl.exe", homeRunner, cancelled.Token); }
            catch (OperationCanceledException) { observedCancellation = true; }
            Check(observedCancellation, "home reads honor connection/page cancellation");
        }
        Console.WriteLine("PASS selected-distro HOME command via injected runner, tilde wiring, failures/cancellation");

        const string mounted = "/mnt/c/Users/Zhang/source/repos/AIFrontend-history-20261020";
        const string windows = @"C:\Users\Zhang\source\repos\AIFrontend-history-20261020";
        Check(Resolve(mounted).Target?.HostPath == windows, "drvfs fallback is a host drive path, not WSL UNC");
        Check(Resolve("/mnt/d").Target?.HostPath == @"D:\", "drive mount root");
        Check(Resolve("/mnt/cat/work").Target?.HostPath == @"\\wsl.localhost\发行版 Test\mnt\cat\work", "not a drive mount boundary");
        var mappingRunner = new FakeHomeRunner { Output = windows + "\r\n" };
        var mapping = new WslHostPathMapping(@"C:\Windows\System32\wsl.exe", mappingRunner);
        var mapped = await mapping.MapAsync(Resolve(mounted).Target!);
        Check(mapped.HostPath == windows && mapped.LinuxDirectory == mounted, "wslpath keeps Linux path for VS Code");
        var mappingCommand = mappingRunner.Commands.Single().StartInfo();
        Check(!mappingCommand.UseShellExecute && mappingCommand.Arguments == ""
            && mappingCommand.ArgumentList.SequenceEqual(new[] { "--distribution", "发行版 Test", "--exec", "wslpath", "-w", mounted }), "wslpath exact arguments without any shell");
        await Task.WhenAll(mapping.MapAsync(Resolve(mounted).Target!), mapping.MapAsync(Resolve(mounted).Target!));
        Check(mappingRunner.Commands.Count == 1, "per distro/path mapping is cached including concurrent callers");
        mappingRunner.Output = @"E:\custom mount\项目" + "\n";
        Check((await mapping.MapAsync(Resolve("/windows/e/项目").Target!)).HostPath == @"E:\custom mount\项目", "wslpath respects a custom automount root");
        await mapping.MapAsync(Resolve(mounted, distro: "Other").Target!);
        Check(mappingRunner.Commands.Count == 3, "cache is separated by distro and Linux path");
        foreach (var output in new string?[] { null, "", "relative", "C:\\bad\npath" })
        {
            var failedMapping = new WslHostPathMapping("wsl.exe", new FakeHomeRunner { Output = output });
            Check((await failedMapping.MapAsync(Resolve(mounted).Target!)).HostPath == windows, "failed/malformed wslpath falls back to drive mapping");
            Check((await failedMapping.MapAsync(Resolve("/home/用户/项目").Target!)).HostPath == @"\\wsl.localhost\发行版 Test\home\用户\项目", "Linux-native fallback stays UNC");
        }
        var throwingMapping = new WslHostPathMapping("wsl.exe", new FakeHomeRunner { Failure = new System.ComponentModel.Win32Exception("unavailable") });
        Check((await throwingMapping.MapAsync(Resolve(mounted).Target!)).HostPath == windows, "process failure falls back");
        mappingRunner.Output = @"\\wsl.localhost\发行版 Test\home\用户\项目" + "\n";
        Check((await mapping.MapAsync(Resolve("/home/用户/项目").Target!)).HostPath == @"\\wsl.localhost\发行版 Test\home\用户\项目", "wslpath Linux-native success stays UNC");
        Check(OpenCodeWslQuickAccess.ExplorerLaunch(mapped, @"C:\Windows").Arguments.SequenceEqual(new[] { windows }), "Explorer gets mapped host path");
        Console.WriteLine("PASS wslpath success + custom automount + cache + failure fallbacks (pure fake runner)");

        var host = new FakeHost();
        host.Registry["Code.exe"] = @"C:\Editors\Code.exe";
        host.Files.Add(@"C:\Editors\Code.exe");
        host.Registry["VSCodium.exe"] = @"C:\Editors\VSCodium.exe";
        host.Files.Add(@"C:\Editors\VSCodium.exe");
        host.Variables["LOCALAPPDATA"] = @"C:\Users\测试 用户\AppData\Local";
        host.Files.Add(@"C:\Users\测试 用户\AppData\Local\Programs\Zed\zed.exe");
        host.Variables["PATH"] = @"C:\Emacs\bin;C:\Other\Microsoft VS Code\bin";
        host.Files.Add(@"C:\Emacs\bin\runemacs.exe");
        var editors = WslHostEditorDetection.Detect(host);
        Check(editors.Count == 4, "candidate matrix covers four editors");
        Check(editors[0].Name == "VS Code" && editors[0].Executable == @"C:\Editors\Code.exe" && editors[0].Reason is null, "VS Code registry first");
        foreach (var editor in editors.Skip(1))
            Check(editor.Executable is not null && editor.OpenMode == WslEditorOpenMode.HostFolder && editor.Reason is null, "installed host editor selectable");
        var launch = OpenCodeWslQuickAccess.EditorLaunch(special.Target!, editors[0]);
        var info = launch.StartInfo(); // This only constructs metadata; never Process.Start.
        Check(!info.UseShellExecute && info.Arguments == "", "no shell string");
        Check(info.FileName == @"C:\Editors\Code.exe", "native Windows editor executable (never .cmd)");
        Check(info.ArgumentList.SequenceEqual(new[] { "--remote", "wsl+发行版 Test", special.Target!.LinuxDirectory }), "VS Code remote distro + Linux path argument boundaries");
        Check(!info.ArgumentList.Any(a => a.StartsWith(@"\\wsl")), "never downgrade to UNC editor window");
        foreach (var editor in editors.Skip(1))
        {
            var recorderForChoice = new RecordingLauncher();
            await OpenCodeWslQuickAccess.LaunchAsync(special, editor, recorderForChoice);
            var chosen = recorderForChoice.Editors.Single().StartInfo();
            Check(chosen.FileName == editor.Executable, editor.Name + " launches the selected executable, not VS Code");
            Check(!chosen.UseShellExecute && chosen.Arguments == "" && chosen.ArgumentList.SequenceEqual(new[] { special.Target!.HostPath }),
                editor.Name + " receives one exact UNC folder argument without shell flags");
        }
        foreach (var editor in editors)
        {
            var mountedLaunch = OpenCodeWslQuickAccess.EditorLaunch(mapped, editor).StartInfo();
            Check(mountedLaunch.ArgumentList.SequenceEqual(editor.OpenMode == WslEditorOpenMode.RemoteWsl
                ? new[] { "--remote", "wsl+发行版 Test", mounted } : new[] { windows }), editor.Name + " mounted path arguments");
        }
        host.Registry.Clear();
        host.Files.Add(@"C:\Other\Microsoft VS Code\Code.exe");
        Check(WslHostEditorDetection.Detect(host)[0].Executable == @"C:\Other\Microsoft VS Code\Code.exe", "PATH bin resolves native code executable");
        host.Files.Clear();
        host.Registry["Code.exe"] = @"C:\stale\Code.exe";
        Check(WslHostEditorDetection.Detect(host).All(e => e.Executable is null && e.Reason == "未检测到安装"), "stale registry and missing executables not installed");
        foreach (var editor in WslHostEditorDetection.Detect(host))
        {
            var rejected = false;
            try { OpenCodeWslQuickAccess.EditorLaunch(special.Target!, editor); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "missing " + editor.Name + " cannot launch");
        }
        host.Variables["ProgramFiles"] = @"C:\Program Files";
        host.Files.Add(@"C:\Program Files\Microsoft VS Code\Code.exe");
        Check(WslHostEditorDetection.Detect(host)[0].Executable == @"C:\Program Files\Microsoft VS Code\Code.exe", "machine installation");
        Console.WriteLine("PASS four-editor detection + selected executable/ArgumentList through injected launcher");

        var preferenceRoot = Path.Combine(Path.GetTempPath(), "aif-wsl-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var preferencePath = Path.Combine(preferenceRoot, "editor.txt");
            Check(new WslQuickAccessEditorPreference(preferencePath).Load() is null, "no implicit editor choice");
            foreach (var editor in editors)
            {
                new WslQuickAccessEditorPreference(preferencePath).Save(editor.Name);
                Check(new WslQuickAccessEditorPreference(preferencePath).Load() == editor.Name, "editor choice survives recreation: " + editor.Name);
            }
        }
        finally { if (Directory.Exists(preferenceRoot)) Directory.Delete(preferenceRoot, true); }

        var recorder = new RecordingLauncher();
        await OpenCodeWslQuickAccess.LaunchAsync(Resolve("/old"), null, recorder);
        await OpenCodeWslQuickAccess.LaunchAsync(Resolve("/new 目录"), null, recorder);
        await OpenCodeWslQuickAccess.LaunchAsync(Resolve("/new 目录"), editors[0], recorder);
        Check(recorder.Explorers.Select(t => t.LinuxDirectory).SequenceEqual(new[] { "/old", "/new 目录" }), "directory refresh replaces Explorer target");
        Check(recorder.Editors.Single().Arguments[2] == "/new 目录", "editor uses refreshed directory");
        await OpenCodeWslQuickAccess.LaunchAsync(Resolve("~/unresolved"), null, recorder);
        Check(recorder.Explorers.Count == 2, "unresolved replacement cannot launch stale target");
        Console.WriteLine("PASS directory refresh + fake launch boundary");
        Console.WriteLine($"PASS 7 groups / {_assertions} assertions; no external applications launched");
    }

    private sealed class FakeHost : IWslHostEditorEnvironment
    {
        public Dictionary<string, string> Registry { get; } = new();
        public Dictionary<string, string> Variables { get; } = new();
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? AppPath(string executable) => Registry.GetValueOrDefault(executable);
        public string? EnvironmentVariable(string name) => Variables.GetValueOrDefault(name);
        public bool FileExists(string path) => Files.Contains(path);
    }
    private sealed class RecordingLauncher : IWslQuickAccessLauncher
    {
        public List<WslQuickAccessTarget> Explorers { get; } = new();
        public List<WslHostLaunch> Editors { get; } = new();
        public Task OpenExplorerAsync(WslQuickAccessTarget target) { Explorers.Add(target); return Task.CompletedTask; }
        public Task OpenEditorAsync(WslHostLaunch launch) { Editors.Add(launch); return Task.CompletedTask; }
    }
    private sealed class FakeHomeRunner : IWslHomeCommandRunner
    {
        public string? Output { get; set; }
        public Exception? Failure { get; set; }
        public List<WslHostLaunch> Commands { get; } = new();
        public Task<string?> ReadAsync(WslHostLaunch command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (Failure is { } failure) throw failure;
            return Task.FromResult(Output);
        }
    }
}
