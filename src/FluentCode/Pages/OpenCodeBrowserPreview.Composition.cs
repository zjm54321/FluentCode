using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentCode.Models;
using FluentCode.Pages.OpenCode;
using FluentCode.Services.Notifications;
using FluentCode.Services.OpenCode.Browser;

namespace FluentCode.Pages;

public sealed partial class OpenCodePage
{
    private void ConfigureBrowserPreview(OpenCodeBrowserWorkspace workspace, OpenCodePageSource? source)
    {
        bool Current() => ReferenceEquals(_source, source) && source is { IsRevoked: false } && _client is not null;
        WslQuickAccessState Target(string path)
        {
            if (!Current()) return new(true, null, "会话服务器未连接");
            var settings = _homeSettings?.Current?.OpenCode;
            if (settings?.Kind is not (OpenCodeConnectionKind.Wsl or OpenCodeConnectionKind.Ssh))
                return new(true, null, "Remote 连接没有已授权的 WSL / SSH 编辑器路径映射");
            var directory = BrowserSessionDirectory(workspace.SessionId);
            if (!path.StartsWith('/'))
            {
                if (directory?.StartsWith('/') != true) return new(true, null, "会话未提供绝对服务器目录");
                path = directory.TrimEnd('/') + "/" + path;
            }
            return settings.Kind == OpenCodeConnectionKind.Ssh
                ? OpenCodeSshQuickAccess.Resolve(settings.Kind, true, settings.SshHost, path)
                : OpenCodeWslQuickAccess.Resolve(settings.Kind, true, settings.WslDistribution, path);
        }
        workspace.PreviewPathUnavailableReason = (path, editor) => Target(path) is var state && state.Target is null
            ? state.Reason : state.IsSsh && !editor ? "SSH v1 不支持在 Windows 资源管理器中打开远端路径"
                : editor && DetailPathEditor() is null ? "未检测到可用的主机编辑器" : null;
        workspace.OpenPreviewPathAsync = async (path, editor) =>
        {
            var state = Target(path);
            if (state.Target is not { } target) throw new IOException(state.Reason);
            if (state.IsSsh && !editor) throw new IOException("SSH v1 不支持在 Windows 资源管理器中打开远端路径");
            if (!editor)
            {
                // Existing Explorer route opens the containing folder, not a guessed /select shell command.
                var parent = target.LinuxDirectory[..(target.LinuxDirectory.LastIndexOf('/') + 1)];
                state = OpenCodeWslQuickAccess.Resolve(OpenCodeConnectionKind.Wsl, true, target.Distribution, parent);
                target = state.Target ?? throw new IOException(state.Reason);
            }
            var mapped = state.IsSsh ? target : await _wslHostPaths.MapAsync(target);
            if (!Current() || Target(path).Target != target) return;
            if (editor)
            {
                var selected = DetailPathEditor() ?? throw new IOException("未检测到可用的主机编辑器");
                await _wslQuickAccessLauncher.OpenEditorAsync(OpenCodeWslQuickAccess.EditorLaunch(mapped, selected));
            }
            else await _wslQuickAccessLauncher.OpenExplorerAsync(mapped);
        };
        workspace.ReadPreviewAsync = (path, ct) => ReadAsync(path, null, ct, OpenCodeBrowserPreviewPolicy.TextLimit);
        workspace.ReadPreviewResourceAsync = (previewPath, path, ct) => ReadAsync(previewPath, path, ct, OpenCodeBrowserPreviewResources.ResourceLimit);
        async Task<OpenCodeBrowserPreviewContent> ReadAsync(string previewPath, string? resourcePath, CancellationToken ct, int maximumBytes)
        {
            if (!Current() || BrowserSessionDirectory(workspace.SessionId) is not { } directory) throw new IOException("会话服务器未连接或缺少工作目录");
            if (!workspace.Pages.OfType<OpenCodeBrowserPage>().Any(page => !page.Closed && OpenCodeBrowserPreviewPolicy.SamePath(page.PreviewPath, previewPath)))
                throw new IOException("未找到明确授权的文件预览请求");
            var plan = resourcePath is null ? OpenCodeBrowserPreviewPolicy.Plan(directory, previewPath)
                : OpenCodeBrowserPreviewPolicy.ResourcePlan(directory, previewPath, resourcePath);
            if (plan.Route == OpenCodeBrowserPreviewRoute.Unavailable) throw new IOException(plan.Reason);
            return await _client!.ReadBrowserPreviewAsync(plan.Directory, plan.Path, ct, maximumBytes);
        }
    }
}
