using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using FluentCode.Services.OpenCode;

namespace FluentCode.Pages.OpenCode;

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
    private bool _failed;

    // Navigation hints deliberately contain no authority. Re-read the accepted message/part,
    // including tools which never had a shell service ID (ordinary foreground/history shells).
    internal static OpenCodeShellTerminalRequest ResolveRequest(OpenCodeShellTerminalRequest request, IEnumerable<OpenCodeMessage> messages)
    {
        var message = messages.LastOrDefault(item => item.Id == request.MessageId);
        if (message is null) return request;
        if (request.PartId is { Length: > 0 } partId)
        {
            var part = message.Content?.FirstOrDefault(item => item.Id == partId);
            if (part is null || OpenCodeToolStates.Read(part.State) is not { } state) return request;
            var status = OpenCodeAgentTreeProjection.String(state.Metadata, "status");
            var running = status == "running" || state.Status is "running" or "streaming";
            var completed = !running && state.Status is "completed" or "error";
            var exit = state.Metadata is { ValueKind: JsonValueKind.Object } metadata && metadata.TryGetProperty("exit", out var code)
                && code.ValueKind == JsonValueKind.Number ? code.ToString() : null;
            return request with { Running = running, Completed = completed, Exit = exit,
                Output = state.Content is { Count: > 0 } content ? string.Join("\n", content.Where(item => item.Type == "text").Select(item => item.Text))
                    : state.Error?.Message ?? request.Output,
                Failed = state.Status == "error" || status is "timeout" or "killed" || exit is not null && exit != "0" };
        }
        if (OpenCodeAgentTreeProjection.Shell(message) is not { } shell) return request;
        return request with { Running = shell.Status == OpenCodeTreeStatus.Running, Completed = shell.Completed,
            Failed = shell.Status is OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted, Exit = shell.Exit };
    }

    internal void Open(OpenCodeTreeShell shell, bool readOutput = true)
    {
        ShellId = shell.Id;
        Command = shell.Command;
        Cwd = shell.Cwd;
        Output = shell.Output ?? string.Empty;
        _recordedOutput = shell.Output;
        _hasApiOutput = false;
        _apiOutput = string.Empty;
        _completed = _retainedCompleted = shell.Completed || shell.Status is OpenCodeTreeStatus.Succeeded or OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted
            || shell.Status != OpenCodeTreeStatus.Running && shell.Exit is not null;
        _failed = shell.Status is OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted;
        _exit = shell.Exit;
        Cursor = 0;
        HasMore = false;
        Notice = readOutput ? "正在读取输出…" : null;
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
        _failed = info.Status is "killed" or "timeout";
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

    internal void UpdateRecordedOutput(string command, string output, bool running, bool recordedOnly,
        bool completed = false, string? exit = null, bool failed = false)
    {
        if (_retainedCompleted) return; // A launch acknowledgement cannot replace durable completion output.
        _recordedOutput = output;
        if (completed)
        {
            ApplyCompletion(new(ShellId!, command, Cwd, OpenCodeTreeShellKind.Foreground,
                failed ? OpenCodeTreeStatus.Failed : OpenCodeTreeStatus.Succeeded, exit, output, true));
            return;
        }
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
        _failed = shell.Status is OpenCodeTreeStatus.Failed or OpenCodeTreeStatus.Interrupted;
        _recordedOutput = shell.Output ?? string.Empty;
        Output = _hasApiOutput && _apiOutput.Length > _recordedOutput.Length && _apiOutput.Contains(_recordedOutput, StringComparison.Ordinal) ? _apiOutput : _recordedOutput;
        Running = HasMore = false; StatusText = CompletedStatus(); Notice = null;
    }

    private string CompletedStatus() => _exit is { } exit ? (exit == "0" && !_failed ? "已完成 · 退出码 0" : "失败 · 退出码 " + exit) : _failed ? "失败" : "已完成 · 退出码不可用";
    internal bool Failed => !Running && _completed && (_failed || _exit is { } exit && exit != "0");

    internal void Close() { ShellId = null; Running = false; HasMore = false; }

    private static string Timestamp(double milliseconds)
        => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
}
