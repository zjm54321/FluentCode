using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Models;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.Notifications;
using AIFrontend.Services.OpenCode.Browser;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private void ConfigureBrowserPreview(OpenCodeBrowserWorkspace workspace, OpenCodePageSource? source)
    {
        bool Current() => ReferenceEquals(_source, source) && source is { IsRevoked: false } && _client is not null;
        WslQuickAccessState Target(string path)
        {
            if (!Current()) return new(true, null, "会话服务器未连接");
            var settings = _homeSettings?.Current?.OpenCode;
            if (settings?.Kind != OpenCodeConnectionKind.Wsl) return new(true, null, "Remote 连接没有已授权的 WSL 主机路径映射");
            var directory = KnownSessionDirectory(workspace.SessionId);
            if (!path.StartsWith('/'))
            {
                if (directory?.StartsWith('/') != true) return new(true, null, "会话未提供绝对服务器目录");
                path = directory.TrimEnd('/') + "/" + path;
            }
            return OpenCodeWslQuickAccess.Resolve(settings.Kind, true, settings.WslDistribution, path);
        }
        workspace.PreviewPathUnavailableReason = (path, editor) => Target(path) is { Target: null } state
            ? state.Reason : editor && DetailPathEditor() is null ? "未检测到可用的主机编辑器" : null;
        workspace.OpenPreviewPathAsync = async (path, editor) =>
        {
            var state = Target(path);
            if (state.Target is not { } target) throw new IOException(state.Reason);
            if (!editor)
            {
                // Existing Explorer route opens the containing folder, not a guessed /select shell command.
                var parent = target.LinuxDirectory[..(target.LinuxDirectory.LastIndexOf('/') + 1)];
                state = OpenCodeWslQuickAccess.Resolve(OpenCodeConnectionKind.Wsl, true, target.Distribution, parent);
                target = state.Target ?? throw new IOException(state.Reason);
            }
            var mapped = await _wslHostPaths.MapAsync(target);
            if (!Current() || Target(path).Target?.Distribution != target.Distribution) return;
            if (editor)
            {
                var selected = DetailPathEditor() ?? throw new IOException("未检测到可用的主机编辑器");
                await _wslQuickAccessLauncher.OpenEditorAsync(OpenCodeWslQuickAccess.EditorLaunch(mapped, selected));
            }
            else await _wslQuickAccessLauncher.OpenExplorerAsync(mapped);
        };
        workspace.ReadPreviewAsync = async (path, ct) =>
        {
            if (!Current() || KnownSessionDirectory(workspace.SessionId) is not { } directory) throw new IOException("会话服务器未连接或缺少工作目录");
            var plan = OpenCodeBrowserPreviewPolicy.Plan(directory, path, _homeSettings?.Current?.OpenCode.Kind == OpenCodeConnectionKind.Wsl);
            if (plan.Route == OpenCodeBrowserPreviewRoute.Unavailable) throw new IOException(plan.Reason);
            if (plan.Route == OpenCodeBrowserPreviewRoute.Http) return await _client!.ReadBrowserPreviewAsync(directory, plan.Path, ct);
            var state = Target(plan.Path);
            if (state.Target is not { } target) throw new IOException(state.Reason);
            var mapped = await _wslHostPaths.MapAsync(target);
            ct.ThrowIfCancellationRequested();
            if (!Current() || Target(plan.Path).Target != target) throw new IOException("会话或连接已切换");
            // ONLY the authorized WSL connection can cross this local-byte boundary. Never file://.
            var limit = OpenCodeBrowserPreviewPolicy.Limit(OpenCodeBrowserPreviewPolicy.Detect(path));
            await using var input = new FileStream(mapped.HostPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
            if (input.Length > limit) throw new IOException($"文件超过 {limit / 1024 / 1024} MiB 预览上限");
            using var output = new MemoryStream(); var buffer = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), ct); if (count == 0) break;
                if (output.Length > limit - count) throw new IOException($"文件超过 {limit / 1024 / 1024} MiB 预览上限");
                output.Write(buffer, 0, count);
            }
            return new(output.ToArray());
        };
    }
}
