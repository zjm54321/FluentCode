using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;

internal static class TerminalTests
{
    internal static async Task Run()
    {
        var checks = 0;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
        foreach (var missingOutputOnly in new[] { false, true })
        {
            using var http = new HttpClient(new Handler(missingOutputOnly));
            using var client = new OpenCodeClient(http, new() { BaseUrl = "https://offline.invalid" }, transportSafetyConfirmed: true);
            using var host = new TerminalReadHost(client);
            host.State.Open(new("sh_done", "finished", "/work", OpenCodeTreeShellKind.Background,
                OpenCodeTreeStatus.Succeeded, "0", "first recorded line\nsecond recorded line"));
            await host.Read();
            Check(host.State.Output == "first recorded line\nsecond recorded line" && !host.State.Running && !host.State.HasMore,
                "BT2: 404 from info or output retains the complete recorded output and stops polling");
            Check(host.State.Notice?.Contains("会话中记录的输出", StringComparison.Ordinal) == true && host.Renders > 0,
                "BT2: actual read handler renders an explicit inline recorded-output notice");
        }
        var state = new OpenCodeTerminalState();
        state.Open(new("sh_missing", "cmd", null, OpenCodeTreeShellKind.Background, OpenCodeTreeStatus.Running, null, null));
        state.UseRecordedOutput();
        Check(state.Output == "" && !state.Running && state.Notice?.Contains("没有记录的输出", StringComparison.Ordinal) == true,
            "BT2: absent retained output is explained, not fabricated or crashed");
        state.Open(new("sh_partial", "cmd", null, OpenCodeTreeShellKind.Background, OpenCodeTreeStatus.Running, null, null));
        state.ApplyOutput(new() { Output = "server bytes", Cursor = 12, Size = 12, Truncated = false });
        state.UseRecordedOutput();
        Check(state.Output == "server bytes", "BT2: already-read server output survives cleanup when no recorded snapshot exists");
        Console.WriteLine($"PASS BT2: {checks} terminal read/fallback assertions; actual read handler, fake HTTP, no UI");
    }

    private sealed class Handler(bool missingOutputOnly) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var missing = !missingOutputOnly || request.RequestUri!.AbsolutePath.EndsWith("/output", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(missing ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(missing ? "{}" : "{\"data\":{\"id\":\"sh_done\",\"status\":\"exited\",\"exit\":0}}", Encoding.UTF8, "application/json"),
            });
        }
    }
}

internal sealed partial class TerminalReadHost : IDisposable
{
    private sealed record Binding(OpenCodeClient Client);
    private sealed class View { internal int Renders; internal void Render(OpenCodeTerminalState state) => Renders++; }
    private readonly OpenCodeTerminalState _terminalState = new();
    private readonly Binding? _terminalBinding;
    private readonly string? _treeDirectory = "/work";
    private readonly CancellationTokenSource? _terminalCts = new();
    private readonly View? _terminalView = new();
    private bool _terminalReading;
    private long _terminalVersion = 0;
    internal TerminalReadHost(OpenCodeClient client) => _terminalBinding = new(client);
    internal OpenCodeTerminalState State => _terminalState;
    internal int Renders => _terminalView!.Renders;
    private bool IsTreeCurrent(Binding binding) => ReferenceEquals(binding, _terminalBinding);
    private Task<T> TreeReadAsync<T>(Binding binding, Func<Task<T>> read, CancellationToken ct) => read();
    internal Task Read() => ReadTerminalOutputAsync();
    public void Dispose() => _terminalCts?.Dispose();
}
