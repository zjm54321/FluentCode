using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode.Browser;

internal sealed class OpenCodeBrowserChromeDownload(OpenCodeBrowserChromeWorkspace workspace,
    OpenCodeBrowserChromeDispatcher ui, string id, string url, string name, string mime) : IOpenCodeBrowserDownload
{
    private string? _destination;
    private bool _cancelled;
    private bool _finishing;
    public string Id { get; } = id;
    public string Url { get; } = url;
    public string Name { get; } = name;
    public string Mime { get; } = mime;
    public long? TotalBytes { get; private set; }
    public long BytesReceived { get; private set; }
    public OpenCodeBrowserDownloadState State { get; private set; } = OpenCodeBrowserDownloadState.InProgress;
    public event EventHandler? Changed;

    internal void SetDestination(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            Cancel();
            return;
        }
        _destination = Path.GetFullPath(path);
    }

    public void Cancel()
    {
        if (State != OpenCodeBrowserDownloadState.InProgress || _cancelled) return;
        _cancelled = true;
        _ = CancelAsync();
    }

    private async Task CancelAsync()
    {
        try { await workspace.CancelDownloadAsync(Id).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or OpenCodeBrowserChromeProtocolException) { }
        await ui.InvokeAsync(() =>
        {
            State = OpenCodeBrowserDownloadState.Interrupted;
            Changed?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);
    }

    internal void Progress(JsonObject progress)
    {
        if (State != OpenCodeBrowserDownloadState.InProgress || _finishing) return;
        var total = progress["totalBytes"]?.GetValue<double>() ?? 0;
        TotalBytes = total > 0 ? checked((long)total) : null;
        BytesReceived = checked((long)(progress["receivedBytes"]?.GetValue<double>() ?? 0));
        var state = progress["state"]?.GetValue<string>();
        if (state == "completed")
        {
            _finishing = true;
            _ = FinishAsync();
        }
        else
        {
            if (state == "canceled") State = OpenCodeBrowserDownloadState.Interrupted;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task FinishAsync()
    {
        var state = OpenCodeBrowserDownloadState.Interrupted;
        try
        {
            if (!_cancelled && _destination != null)
            {
                var source = Path.Combine(workspace.DownloadDirectory, Id);
                Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
                // Chrome's completed event guarantees that its GUID-named file has been closed.
                if (!source.Equals(_destination, StringComparison.OrdinalIgnoreCase)) File.Move(source, _destination, true);
                if (File.Exists(_destination)) state = OpenCodeBrowserDownloadState.Completed;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        await ui.InvokeAsync(() =>
        {
            State = _cancelled ? OpenCodeBrowserDownloadState.Interrupted : state;
            Changed?.Invoke(this, EventArgs.Empty);
        }).ConfigureAwait(false);
    }

    internal void OnOwnerClosing()
    {
        if (State == OpenCodeBrowserDownloadState.InProgress) Cancel();
    }

    internal void OnConnectionLost()
    {
        if (State != OpenCodeBrowserDownloadState.InProgress) return;
        _cancelled = true;
        State = OpenCodeBrowserDownloadState.Interrupted;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
