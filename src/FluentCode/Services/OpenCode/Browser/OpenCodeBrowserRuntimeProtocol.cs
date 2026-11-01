using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Private bounded JSON-lines transport. The executable is always the prepared local node.exe.</summary>
internal sealed class OpenCodeBrowserRuntimeProtocol : IAsyncDisposable
{
    private const int MaxLine = 16 * 1024 * 1024;
    private const int MaxQueued = 24 * 1024 * 1024;
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> incoming = new();
    private readonly Channel<byte[]> output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<string, JsonObject, CancellationToken, Task<JsonNode?>> handler;
    private readonly Task reader;
    private readonly Task writer;
    private readonly Task errors;
    private long sequence;
    private int queued;
    private int disposed;
    private int failed;
    private readonly TaskCompletionSource<JsonObject> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OpenCodeBrowserRuntimeProtocol(Process process,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> handler)
    {
        this.process = process;
        this.handler = handler;
        reader = ReadAsync();
        writer = WriteAsync();
        errors = DrainErrorsAsync();
    }

    public Task<JsonObject> Ready => ready.Task;
    public CancellationToken Lifetime => lifetime.Token;

    public static OpenCodeBrowserRuntimeProtocol Start(string directory,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> handler)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Browser runtime directory must be absolute.", nameof(directory));
        foreach (var name in new[] { "node.exe", "sidecar.mjs", "audit-worker.mjs", "runtime-manifest.json", "node_modules/lighthouse/package.json" })
            if (!File.Exists(Path.Combine(directory, name)))
                throw new FileNotFoundException("Prepared browser runtime is missing. Run tools/browser-runtime/prepare.ps1 before building or publishing.", Path.Combine(directory, name));
        var info = new ProcessStartInfo(Path.Combine(directory, "node.exe"))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add(Path.Combine(directory, "sidecar.mjs"));
        // Do not inherit API credentials/endpoints or user Node preloads. Only Windows/temp
        // infrastructure crosses the process boundary; neither PATH nor npm configuration is needed.
        info.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        var child = Process.Start(info) ?? throw new InvalidOperationException("Could not start prepared browser runtime.");
        return new(child, handler);
    }

    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pending.Count >= 512) throw new InvalidOperationException("Browser runtime pending request limit exceeded.");
        var id = "c" + Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            Send(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters });
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending.TryRemove(id, out _) && cancellationToken.IsCancellationRequested)
            {
                try { Send(new JsonObject { ["cancel"] = id }); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { }
            }
        }
    }

    public void Send(JsonObject message)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        if (bytes.Length > MaxLine) throw new IOException("Browser runtime message exceeds 16 MiB.");
        var total = Interlocked.Add(ref queued, bytes.Length);
        if (total > MaxQueued || !output.Writer.TryWrite(bytes))
        {
            Interlocked.Add(ref queued, -bytes.Length);
            var error = new IOException("Browser runtime output queue limit exceeded.");
            Fail(error);
            throw error;
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var bytes in output.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(bytes, lifetime.Token).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(lifetime.Token).ConfigureAwait(false);
                }
                finally { Interlocked.Add(ref queued, -bytes.Length); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Fail(ex); }
    }

    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[64 * 1024];
            using var line = new MemoryStream();
            for (;;)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) throw new IOException("Browser runtime exited or closed stdout.");
                var start = 0;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != 10) continue;
                    Append(line, buffer, start, index - start);
                    if (line.Length > 0) Receive(JsonNode.Parse(line.GetBuffer().AsSpan(0, (int)line.Length))!.AsObject());
                    line.SetLength(0);
                    start = index + 1;
                }
                Append(line, buffer, start, count - start);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static void Append(MemoryStream line, byte[] buffer, int start, int count)
    {
        if (line.Length + count > MaxLine) throw new IOException("Browser runtime message exceeds 16 MiB.");
        line.Write(buffer, start, count);
    }

    private void Receive(JsonObject message)
    {
        if (message["event"]?.GetValue<string>() is "runtime.ready") { ready.TrySetResult(message); return; }
        if (message["cancel"]?.GetValue<string>() is { } cancel)
        { if (incoming.TryGetValue(cancel, out var operation)) operation.Cancel(); return; }
        var id = message["id"]!.GetValue<string>();
        if (message["method"]?.GetValue<string>() is { } method)
        {
            if (incoming.Count >= 512) throw new IOException("Browser runtime callback limit exceeded.");
            var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            if (!incoming.TryAdd(id, operation)) { operation.Dispose(); throw new IOException("Duplicate browser runtime callback ID."); }
            _ = HandleAsync(id, method, message["params"]?.AsObject() ?? new(), operation);
            return;
        }
        if (!pending.TryRemove(id, out var callback)) return;
        if (message["error"] is JsonObject error)
            callback.TrySetException(new InvalidOperationException(error["message"]?.GetValue<string>() ?? "Browser runtime operation failed."));
        else callback.TrySetResult(message["result"]?.DeepClone());
    }

    private async Task HandleAsync(string id, string method, JsonObject parameters, CancellationTokenSource operation)
    {
        try
        {
            JsonObject response;
            try
            {
                var result = await handler(method, parameters, operation.Token).ConfigureAwait(false);
                response = new() { ["id"] = id, ["result"] = result };
            }
            catch (Exception ex)
            {
                // Tunnel exceptions may contain authenticated HTTP details; never serialize those to Node.
                var text = method.StartsWith("tunnel.", StringComparison.Ordinal) ? "Browser tunnel operation failed." : ex.Message;
                response = new() { ["id"] = id, ["error"] = new JsonObject { ["message"] = text.Length > 2048 ? text[..2048] : text } };
            }
            Send(response);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { Fail(ex); }
        finally { incoming.TryRemove(id, out _); operation.Dispose(); }
    }

    private async Task DrainErrorsAsync()
    {
        try
        {
            var buffer = new char[2048];
            while (await process.StandardError.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false) != 0) { }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.Exchange(ref failed, 1) != 0) return;
        ready.TrySetException(error);
        foreach (var entry in pending) if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(error);
        output.Writer.TryComplete();
        if (!lifetime.IsCancellationRequested) lifetime.Cancel();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Fail(new ObjectDisposedException(nameof(OpenCodeBrowserRuntimeProtocol)));
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        await Task.WhenAll(reader, writer, errors).ConfigureAwait(false);
        process.Dispose();
        lifetime.Dispose();
    }
}
