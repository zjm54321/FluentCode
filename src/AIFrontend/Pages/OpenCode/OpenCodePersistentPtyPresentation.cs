using System;

namespace AIFrontend.Pages.OpenCode;

// Local view contracts, deliberately independent of PTY server/socket DTOs.
internal sealed record OpenCodePersistentPtyViewContext(Guid ViewId, long Generation, string SessionId, string PtyId);
internal enum OpenCodePersistentPtyPhase { Unavailable, Connecting, Replaying, Live, Disconnected, Exited }
internal enum OpenCodePersistentPtyRole { Observer, Controller }
internal sealed record OpenCodePersistentPtyPresentation(OpenCodePersistentPtyViewContext Context, string Title,
    string? Directory, OpenCodePersistentPtyPhase Phase, OpenCodePersistentPtyRole Role, string? ControllerLabel,
    bool CanTakeControl, bool InputAuthorized, string? Notice = null, string? OutputStatus = null, bool ControlConflict = false,
    string? Source = null, bool Failed = false)
{
    /// <summary>Read-only now, but a double-click on the terminal may acquire input here (never for recorded shell output).</summary>
    internal bool CanActivate => Phase == OpenCodePersistentPtyPhase.Live && CanTakeControl && !Writable;
    internal OpenCodeTerminalState TerminalState => Phase switch
    {
        OpenCodePersistentPtyPhase.Connecting or OpenCodePersistentPtyPhase.Replaying => OpenCodeTerminalState.Connecting,
        OpenCodePersistentPtyPhase.Live => ControlConflict ? OpenCodeTerminalState.Elsewhere : OpenCodeTerminalState.Running,
        OpenCodePersistentPtyPhase.Exited => Failed ? OpenCodeTerminalState.Failed : OpenCodeTerminalState.Done,
        _ => OpenCodeTerminalState.Disconnected,
    };
    internal bool Writable => Phase == OpenCodePersistentPtyPhase.Live && Role == OpenCodePersistentPtyRole.Controller && InputAuthorized;
    internal bool NeedsInputConsent => Phase == OpenCodePersistentPtyPhase.Live && CanTakeControl
        && (Role == OpenCodePersistentPtyRole.Observer || !InputAuthorized);
    internal string ConsentAction => Role == OpenCodePersistentPtyRole.Controller ? "启用输入" : "接管终端";
    internal string ConsentExplanation => Role == OpenCodePersistentPtyRole.Controller
        ? "当前连接已获分配控制权，但尚未启用输入。确认后，键盘输入将发送到此终端。"
        : "接管后，键盘输入将发送到此终端，当前控制者将转为只读观察。仅查看不会接管终端。";
    internal string Status => OutputStatus ?? (Phase switch
    {
        OpenCodePersistentPtyPhase.Connecting => "正在连接",
        OpenCodePersistentPtyPhase.Replaying => "正在恢复终端画面",
        OpenCodePersistentPtyPhase.Live => ControlConflict ? "其他客户端正在输入" : "运行中",
        OpenCodePersistentPtyPhase.Disconnected => "连接已断开",
        OpenCodePersistentPtyPhase.Exited => "已退出",
        _ => "终端不可用",
    });
}
internal enum OpenCodeTerminalState { Connecting, Running, Done, Failed, Elsewhere, Disconnected }
internal enum OpenCodeTerminalEntryKind { Pty, Shell }
/// <summary>One terminal in the right-panel strip and the overlay list: persistent PTYs and agent commands alike.</summary>
internal sealed record OpenCodeTerminalEntry(OpenCodeTerminalEntryKind Kind, string Id, string Title, string Source, string? Cwd,
    OpenCodeTerminalState State, string StateText, bool CanEnd);
internal sealed record OpenCodePersistentPtyInput(OpenCodePersistentPtyViewContext Context, ReadOnlyMemory<byte> Bytes, bool IsTerminalResponse = false);
internal sealed record OpenCodePersistentPtyResize(OpenCodePersistentPtyViewContext Context, int Columns, int Rows);
internal sealed record OpenCodePersistentPtySelection(OpenCodePersistentPtyViewContext Context, string Text);

internal static class OpenCodeTerminalNames
{
    /// <summary>
    /// Display title: an explicit title, else the shell's file name. Shell-reported paths (OSC 0/2) are skipped — the path is
    /// already the subtitle — and so is the foreground process, which flips to prompt helpers (starship) on every prompt.
    /// </summary>
    internal static string Title(string? title, string? command)
    {
        var pathLike = title is { } t && (t.StartsWith('/') || t.StartsWith('~') || t.Contains(":\\", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(title) && title != "终端" && !pathLike) return title;
        var name = (command ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 ? "终端" : name;
    }

    internal static string ExitText(int? exitCode)
        => exitCode is { } code ? (code == 0 ? "已完成 · 退出码 0" : "失败 · 退出码 " + code) : "已退出";
}

internal static class OpenCodePersistentPtyRendererPolicy
{
    internal static bool ValidGeometry(int columns, int rows) => columns is >= 2 and <= 1000 && rows is >= 1 and <= 1000;
    internal static bool AcceptsInputGeneration(bool writable, string expected, string? received)
        => writable && expected.Length > 0 && string.Equals(expected, received, StringComparison.Ordinal);
}
