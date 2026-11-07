using System;
using System.Collections.Generic;
using System.Linq;
using FluentCode.Models;
using FluentCode.Services.OpenCode.Connection;

namespace FluentCode.Pages.OpenCode;

/// <summary>SSH 编辑器目标始终是远端绝对路径；不构造 UNC，不交给 Windows 资源管理器。</summary>
internal static class OpenCodeSshQuickAccess
{
    internal static WslQuickAccessState Resolve(OpenCodeConnectionKind? kind, bool connected, string? host, string? directory)
    {
        if (kind != OpenCodeConnectionKind.Ssh || !connected) return new(false, null, null, true);
        if (!SshConnectionTarget.TryParse(host, out _)) return new(true, null, "连接未提供有效的 SSH 主机", true);
        if (string.IsNullOrEmpty(directory) || !directory.StartsWith('/'))
            return new(true, null, "等待会话提供远端绝对工作目录（SSH 不将 ~ 作为本机路径）", true);
        if (directory.Any(char.IsControl)) return new(true, null, "此远端路径含控制字符，不能交给编辑器", true);
        var parts = new List<string>();
        foreach (string part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return new(true, null, "工作目录越过了远端根目录", true);
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        string path = "/" + string.Join('/', parts);
        return new(true, new(string.Empty, path, path, host), null, true);
    }

    internal static IReadOnlyList<WslHostEditor> Editors(IReadOnlyList<WslHostEditor> detected) => detected
        .Where(editor => editor.Name is "VS Code" or "Zed").ToArray();

    internal static WslHostLaunch EditorLaunch(WslQuickAccessTarget target, WslHostEditor editor)
    {
        if (target.SshHost is null || editor.Executable is null || editor.Name is not ("VS Code" or "Zed"))
            throw new InvalidOperationException("未检测到支持 SSH 的主机编辑器");
        var host = SshConnectionTarget.Parse(target.SshHost);
        string authority = (host.User is null ? string.Empty : host.User + "@")
            + (host.Host.Contains(':') ? "[" + host.Host + "]" : host.Host)
            + (host.Port is null ? string.Empty : ":" + host.Port.Value);
        return new(editor.Executable, editor.Name == "VS Code"
            ? new[] { "--remote", "ssh-remote+" + target.SshHost, target.LinuxDirectory }
            : new[] { "ssh://" + authority + string.Join('/', target.LinuxDirectory.Split('/').Select(Uri.EscapeDataString)) });
    }
}
