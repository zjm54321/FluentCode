using System;
using System.Globalization;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Read-only shell output state. Cursors are server byte offsets, never string lengths.</summary>
internal sealed class OpenCodeShellTerminalState
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
    private string _apiOutput = string.Empty;
    private bool _hasApiOutput, _completed, _retainedCompleted;
    private string? _exit;

    internal void Open(OpenCodeTreeShell shell)
    {
        ShellId = shell.Id;
        Command = shell.Command;
        Cwd = shell.Cwd;
        Output = shell.Output ?? string.Empty;
        _recordedOutput = shell.Output;
        _hasApiOutput = false;
        _apiOutput = string.Empty;
        _completed = _retainedCompleted = shell.Completed || shell.Status != OpenCodeTreeStatus.Running && shell.Exit is not null;
        _exit = shell.Exit;
        Cursor = 0;
        HasMore = false;
        Notice = "正在读取输出…";
        TimeText = string.Empty;
        Running = shell.Status == OpenCodeTreeStatus.Running;
        StatusText = Running ? "运行中" : _completed ? CompletedStatus() : "状态不可用";
    }

    internal void ApplyInfo(OpenCodeTreeShellInfo info)
    {
        if (_retainedCompleted) return;
        Command = info.Command ?? Command;
        Cwd = info.Cwd ?? Cwd;
        Running = info.Status == "running";
        _completed = info.Status is "exited" or "killed" or "timeout";
        _exit = info.Exit?.ToString(CultureInfo.InvariantCulture);
        StatusText = Running ? "运行中" : _completed ? CompletedStatus() : "状态不可用";
        TimeText = info.Time?.Started is { } started ? "开始：" + Timestamp(started) : string.Empty;
        if (info.Time?.Completed is { } completed) TimeText += (TimeText.Length > 0 ? "\n" : string.Empty) + "结束：" + Timestamp(completed);
    }

    internal void ApplyOutput(OpenCodeShellOutput page)
    {
        if (page.Output is null || page.Cursor is not { } next || page.Size is not { } size || page.Truncated is null || next < Cursor || next > size)
            throw new OpenCodeProtocolException("终端输出响应缺少有效的输出或字节游标。");
        var previous = Cursor;
        if (next < size && next <= previous) throw new OpenCodeProtocolException("终端输出字节游标未前进。");
        _apiOutput = (Cursor == 0 ? string.Empty : _apiOutput) + page.Output;
        Output = _retainedCompleted && (_recordedOutput?.Length ?? 0) > _apiOutput.Length ? _recordedOutput! : _apiOutput;
        _hasApiOutput = true;
        Cursor = next;
        HasMore = next < size;
        Notice = page.Truncated == true ? "后端标记输出已截断" : null;
    }

    internal void UseRecordedOutput()
    {
        if (_retainedCompleted || _hasApiOutput)
        {
            if (_retainedCompleted) Output = _hasApiOutput && _apiOutput.Length > (_recordedOutput?.Length ?? 0) ? _apiOutput : _recordedOutput ?? Output;
            if (_completed) { Running = false; StatusText = CompletedStatus(); }
            HasMore = false; Notice = null;
            return;
        }
        Output = _recordedOutput ?? Output;
        if (Running) StatusText = "后端已清理 · 状态不可用";
        Running = false;
        HasMore = false;
        Notice = _recordedOutput is not null
            ? "后端已清理此终端；显示会话中记录的输出"
            : "后端已清理此终端；会话中没有记录的输出，已读取的内容予以保留";
    }

    internal void UpdateRecordedOutput(string command, string output, bool running, bool recordedOnly)
    {
        if (_retainedCompleted) return; // A launch acknowledgement cannot replace durable completion output.
        _recordedOutput = output;
        if (!recordedOnly && Cursor != 0) return;
        Command = command; Output = output; Running = running;
        StatusText = running ? "运行中" : "已完成";
        Notice = null;
    }

    internal void ApplyCompletion(OpenCodeTreeShell shell)
    {
        if (!shell.Completed) return;
        Command = shell.Command == shell.Id ? Command : shell.Command;
        _completed = _retainedCompleted = true; _exit = shell.Exit;
        _recordedOutput = shell.Output ?? string.Empty;
        Output = _hasApiOutput && _apiOutput.Length > _recordedOutput.Length && _apiOutput.Contains(_recordedOutput, StringComparison.Ordinal) ? _apiOutput : _recordedOutput;
        Running = HasMore = false; StatusText = CompletedStatus(); Notice = null;
    }

    private string CompletedStatus() => "已完成" + (_exit is { } exit ? " · 退出码 " + exit : " · 退出码不可用");

    internal void Close() { ShellId = null; Running = false; HasMore = false; }

    private static string Timestamp(double milliseconds)
        => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
