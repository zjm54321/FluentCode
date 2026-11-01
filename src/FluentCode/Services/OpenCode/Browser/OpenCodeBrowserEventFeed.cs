using System;

namespace FluentCode.Services.OpenCode.Browser;

/// <summary>Fan-out from the existing page SSE pipeline, not a second HTTP subscription.</summary>
public interface IOpenCodeBrowserEventFeed
{
    bool IsConnected { get; }
    event EventHandler<OpenCodeEvent>? Control;
    event EventHandler? ConnectionChanged;
}

public sealed class OpenCodeBrowserEventFeed : IOpenCodeBrowserEventFeed
{
    public bool IsConnected { get; private set; }
    public event EventHandler<OpenCodeEvent>? Control;
    public event EventHandler? ConnectionChanged;

    public void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        IsConnected = connected;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Publish(OpenCodeEvent item)
    {
        if (IsConnected && item.Type == "rpc.experimental.browser.control") Control?.Invoke(this, item);
    }
}
