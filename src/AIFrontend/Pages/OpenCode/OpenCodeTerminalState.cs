using System;
using System.Globalization;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Read-only shell output state. Cursors are server byte offsets, never string lengths.</summary>
internal sealed class OpenCodeTerminalState
{
    internal string? ShellId { get; private set; }
    internal string Command { get; private set; } = string.Empty;
    internal string? Cwd { get; private set; }
    internal string StatusText { get; private set; } = "状态不可用";
    internal string TimeText { get; private set; } = string.Empty;
    internal string Output { get; private set; } = string.Empty;
    internal string? Notice { get; set; }
    internal long Cursor { get; private set; }
    internal bool Running { get; private set; }
    internal bool HasMore { get; private set; }
    private string? _recordedOutput;

    internal void Open(OpenCodeTreeShell shell)
    {
        ShellId = shell.Id;
        Command = shell.Command;
        Cwd = shell.Cwd;
        Output = shell.Output ?? string.Empty;
        _recordedOutput = shell.Output;
        Cursor = 0;
        HasMore = false;
        Notice = "正在读取输出…";
        TimeText = string.Empty;
        Running = shell.Status == OpenCodeTreeStatus.Running;
        StatusText = Running ? "运行中" : shell.Exit is { } exit ? "已退出 · 退出码 " + exit : "状态不可用";
    }

    internal void ApplyInfo(OpenCodeTreeShellInfo info)
    {
        Command = info.Command ?? Command;
        Cwd = info.Cwd ?? Cwd;
        Running = info.Status == "running";
        StatusText = Running ? "运行中" : info.Status is "exited" or "killed" or "timeout"
            ? "已退出" + (info.Exit is { } exit ? " · 退出码 " + exit.ToString(CultureInfo.InvariantCulture) : " · 退出码不可用")
            : "状态不可用";
        TimeText = info.Time?.Started is { } started ? "开始：" + Timestamp(started) : string.Empty;
        if (info.Time?.Completed is { } completed) TimeText += (TimeText.Length > 0 ? "\n" : string.Empty) + "结束：" + Timestamp(completed);
    }

    internal void ApplyOutput(OpenCodeShellOutput page)
    {
        if (page.Output is null || page.Cursor is not { } next || page.Size is not { } size || page.Truncated is null || next < Cursor || next > size)
            throw new OpenCodeProtocolException("终端输出响应缺少有效的输出或字节游标。");
        var previous = Cursor;
        if (next < size && next <= previous) throw new OpenCodeProtocolException("终端输出字节游标未前进。");
        Output = (Cursor == 0 ? string.Empty : Output) + page.Output;
        Cursor = next;
        HasMore = next < size;
        Notice = page.Truncated == true ? "后端标记输出已截断" : null;
    }

    internal void UseRecordedOutput()
    {
        Output = _recordedOutput ?? Output;
        if (Running) StatusText = "后端已清理 · 状态不可用";
        Running = false;
        HasMore = false;
        Notice = _recordedOutput is not null
            ? "后端已清理此终端；显示会话中记录的输出"
            : "后端已清理此终端；会话中没有记录的输出，已读取的内容予以保留";
    }

    internal void Close() { ShellId = null; Running = false; HasMore = false; }

    internal static bool ShouldFollow(bool following, double offset, double scrollableHeight)
        => following || scrollableHeight - offset <= 2;

    private static string Timestamp(double milliseconds)
        => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
