using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class ShellTerminalTests
{
    private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS shell " + label); }
    internal static string LongOutput()
    {
        var output = new StringBuilder("\r\n\n\r\n");
        for (var i = 0; i < 240; i++) output.Append($"TimeCreated  : 2026/10/30 20:{i / 60:D2}:{i % 60:D2}" + (i % 2 == 0 ? "\r\n" : "\n")
            + $"Id           : {i:D4}\nMessage      : 中文 PowerShell Format-List {i:D4}\r\n\r\n");
        return output.Append("\x1b[32mHasExited=True\x1b[0m\r\nExitCode=0\n\r\n").ToString();
    }
    internal static async Task Run()
    {
        var output = LongOutput();
        var preview = OpenCodeShellTerminalText.Preview(output);
        Check(preview.Split('\n').Length == 5 && !preview.Contains('\x1b') && preview.EndsWith("HasExited=True\nExitCode=0")
            && !preview.Contains("0000") && preview.Contains("0239"), "preview is the last five meaningful ANSI-free lines, without leading/trailing/interior blank padding");
        Check(OpenCodeShellTerminalText.Preview("\r\n \n\t\r\n").Length == 0, "blank-only output has no preview height");
        var surface = new Surface();
        var adapter = new OpenCodeShellTerminalAdapter(surface, new(Guid.NewGuid(), 1, "session", "shell"));
        const string command = "powershell.exe -NoProfile -Command \"Get-WinEvent -MaxEvents 240 | Format-List\"";
        await adapter.RenderAsync(output, default, command);
        var rendered = surface.Text;
        Check(rendered.StartsWith(OpenCodeShellTerminalText.HideCursor + "\x1b[36m$ " + command + "\x1b[0m\r\n") && rendered.Contains("Id           : 0000")
            && rendered.Contains("Id           : 0239") && rendered.Count(c => c == '\n') > 900,
            "native stream includes full command and complete >200-record stored output, not preview tail");
        Check(!rendered.Replace("\r\n", "").Contains('\n') && !rendered.Contains("\r\r\n"), "mixed CRLF/LF normalizes exactly once; blank lines remain in complete output");
        var writes = surface.Writes;
        await adapter.RenderAsync(output, default, command);
        Check(surface.Writes == writes && surface.Resets == 0, "unchanged snapshots preserve parser and selection");
        await adapter.RenderAsync(output + "live\r", default, command);
        await adapter.RenderAsync(output + "live\r\nnext\n", default, command);
        Check(surface.Text.EndsWith("live\r\nnext\r\n") && surface.Text.IndexOf("$ " + command) == surface.Text.LastIndexOf("$ " + command),
            "running append and CR/LF split retain complete output and never repeat prompt");
        await adapter.RenderAsync("replacement\n", default, command);
        Check(surface.Resets == 1 && surface.Text.EndsWith("replacement\r\n") && surface.Text.Contains("$ " + command), "replacement snapshot replays its command and complete output");
        var blocked = new Surface { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var serial = new OpenCodeShellTerminalAdapter(blocked, new(Guid.NewGuid(), 1, "session", "shell"));
        var first = serial.RenderAsync("one", default, command);
        var second = serial.RenderAsync("one\ntwo", default, command);
        Check(blocked.Initialized == 1 && !second.IsCompleted, "initial stored output and concurrent REST/SSE update cannot initialize competing HWNDs");
        blocked.Hold.SetResult(true); await Task.WhenAll(first, second);
        Check(blocked.Text.EndsWith("one\r\ntwo") && blocked.Initialized == 1, "queued native writes append in source order after initialization");
        var state = new OpenCodeShellTerminalState();
        state.Open(new("shell", command, "/work", OpenCodeTreeShellKind.Foreground, OpenCodeTreeStatus.Running, null, output));
        state.UseRecordedOutput();
        Check(state.Output == output && state.Command == command, "404 fallback retains full original tool output and command, never the preview");
        state.UpdateRecordedOutput(command, output + "SSE tail", false, true);
        Check(state.Output.EndsWith("SSE tail") && !state.Running, "tool-part-only shell updates stay live through accepted transcript output");
        state.Open(new("shell", command, "/work", OpenCodeTreeShellKind.Foreground, OpenCodeTreeStatus.Running, null, output));
        var pages = new[] { output[..1500], output[1500..12000], output[12000..] };
        long cursor = 0, size = Encoding.UTF8.GetByteCount(output);
        foreach (var page in pages)
        {
            cursor += Encoding.UTF8.GetByteCount(page);
            state.ApplyOutput(new() { Output = page, Cursor = cursor, Size = size, Truncated = false });
        }
        Check(!state.HasMore && state.Cursor == size && state.Output == output,
            "shell-output API byte-cursor pages retain every record, interior blank line and mixed newline to completion");
        await BackgroundOutput();
        var cursorSurface = new Surface();
        var cursorAdapter = new OpenCodeShellTerminalAdapter(cursorSurface, new(Guid.NewGuid(), 1, "session", "shell"));
        var controls = "before\x1b[?1;25hafter\x1b" + "creset";
        for (var length = 1; length <= controls.Length; length++) await cursorAdapter.RenderAsync(controls[..length], default, command);
        Check(cursorSurface.Resets == 0 && cursorSurface.Text.Contains("\x1b[?1;25h" + OpenCodeShellTerminalText.HideCursor)
            && cursorSurface.Text.Contains("\x1b" + "c" + OpenCodeShellTerminalText.HideCursor),
            "read-only caret stays hidden after fragmented DECSET/RIS without injecting escapes into partial CSI or replacing output");
    }

    private static async Task BackgroundOutput()
    {
        const string launch = "Command moved to the background (shell ID: sh_bg). Output is streaming to: /server/sh_bg.out";
        const string command = "while pgrep -f worker; do tail -40 /server/log; done";
        var real = LongOutput(); var infoReads = 0; var outputReads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/output", StringComparison.Ordinal))
            {
                outputReads++;
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new { output = real, cursor = Encoding.UTF8.GetByteCount(real), size = Encoding.UTF8.GetByteCount(real), truncated = false } })) };
            }
            infoReads++; return new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }));
        using var client = new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid", AuthenticationMode = OpenCodeAuthenticationMode.None }, true);
        var state = new OpenCodeShellTerminalState();
        state.Open(new("sh_bg", command, "/work", OpenCodeTreeShellKind.Background, OpenCodeTreeStatus.Running, "0", launch));
        await OpenCodeShellTerminalReader.ReadAsync(() => client.GetTreeShellAsync("sh_bg", "/work", default),
            cursor => client.ReadTreeShellOutputAsync("sh_bg", "/work", cursor, default), state, () => true, () => Task.CompletedTask, default);
        Check(infoReads == 1 && outputReads == 1 && state.Output == real && state.Notice is null,
            "collected shell metadata 404 still reads retained background output API instead of displaying launcher acknowledgement");
        var message = new OpenCodeMessage { Type = "synthetic", Description = command,
            Metadata = JsonSerializer.SerializeToElement(new { source = "shell", shellID = "sh_bg", state = "completed", exit = 0 }),
            Text = "<shell id=\"sh_bg\" state=\"completed\" command=\"tail -40 /server/log > /tmp/out\">\r\n" + real + "\r\n</shell>" };
        var completed = OpenCodeAgentTreeProjection.Shell(message)!;
        Check(completed is { Completed: true, Exit: "0", Status: OpenCodeTreeStatus.Succeeded } && completed.Output!.Contains("0239")
            && completed.Command == "tail -40 /server/log > /tmp/out", "machine shell completion retains real final output, exit code and quoted command even after collection");
        state.Open(new("sh_bg", command, "/work", OpenCodeTreeShellKind.Background, OpenCodeTreeStatus.Running, null, launch));
        state.ApplyCompletion(completed); state.UseRecordedOutput();
        state.UpdateRecordedOutput(command, launch, true, true);
        Check(state.Output.Contains("0239") && !state.Output.Contains("Command moved to the background") && state.StatusText == "已完成 · 退出码 0"
            && !state.Running && state.Notice is null, "durable completion replaces launch text and survives stale tool updates without cleared/unknown warning");
        state.ApplyCompletion(completed with { Exit = "7", Status = OpenCodeTreeStatus.Failed });
        Check(state.StatusText == "失败 · 退出码 7" && state.Failed, "collected failed background shell still reports its known numeric exit");
        Check(OpenCodeAgentTreeProjection.Shell(message with { Type = "user" }) is null,
            "ordinary user text cannot fabricate a shell completion or output source");
        state.Open(new("sh_missing", command, "/work", OpenCodeTreeShellKind.Background, OpenCodeTreeStatus.Running, null, launch));
        state.UseRecordedOutput();
        Check(state.Output == launch && state.Notice is not null, "launcher text plus warning is used only when neither retained API output nor completion exists");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request)); }
    private sealed class Surface : IOpenCodePersistentPtySurface
    {
        private readonly List<byte> _bytes = new();
        internal string Text => Encoding.UTF8.GetString(_bytes.ToArray());
        internal int Initialized, Writes, Resets;
        internal TaskCompletionSource<bool>? Hold;
        public async Task<bool> InitializeAsync(OpenCodePersistentPtyViewContext context, int columns, int rows, CancellationToken ct)
        { Initialized++; if (Hold is { } held) await held.Task.WaitAsync(ct); return true; }
        public Task ResetAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> checkpoint, int columns, int rows, CancellationToken ct)
        { Resets++; _bytes.Clear(); return Task.CompletedTask; }
        public Task WriteAsync(OpenCodePersistentPtyViewContext context, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        { if (bytes.Length > 0) { Writes++; _bytes.AddRange(bytes.ToArray()); } return Task.CompletedTask; }
        public void Dispose() { }
    }
}
