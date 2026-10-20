using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Fail-closed read pipeline shared by the child window and headless lifecycle tests.</summary>
internal static class OpenCodeChildChatReadSession
{
    /// <summary>GET order=desc returns latest first; copy in reverse for the timeline, matching normal session entry.</summary>
    internal static List<OpenCodeMessage> OrderMessages(IReadOnlyList<OpenCodeMessage>? data)
    {
        var result = new List<OpenCodeMessage>(data?.Count ?? 0);
        if (data is not null)
            for (var i = data.Count - 1; i >= 0; i--) result.Add(data[i]);
        return result;
    }

    internal static async Task LoadAsync(
        Func<bool> current,
        Func<CancellationToken, Task<bool>> verifyAncestry,
        Func<CancellationToken, Task<OpenCodeSessionMessagesResponse>> readMessages,
        Action<OpenCodeSessionMessagesResponse> publish,
        Action<string> error,
        CancellationToken token)
    {
        bool Current() => !token.IsCancellationRequested && current();
        try
        {
            if (!Current()) return;
            var related = await verifyAncestry(token);
            if (!Current()) return;
            if (!related) { error("无法确认该子会话属于当前会话，未读取消息。"); return; }
            var messages = await readMessages(token);
            if (!Current()) return;
            publish(messages);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (Current()) error("无法读取子会话消息。请关闭后重试。");
        }
    }
}

/// <summary>Window actions: close never navigates; maximize requires loaded content and a current binding.</summary>
internal sealed class OpenCodeChildChatActions
{
    private readonly Func<bool> _current;
    private readonly Action _close;
    private readonly Func<Task> _maximize;
    private bool _ready;
    internal bool IsClosed { get; private set; }

    internal OpenCodeChildChatActions(Func<bool> current, Action close, Func<Task> maximize)
        => (_current, _close, _maximize) = (current, close, maximize);

    internal void MarkLoaded() => _ready = true;

    internal void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        _close();
    }

    internal async Task MaximizeAsync()
    {
        if (IsClosed || !_ready || !_current()) return;
        Close();
        await _maximize();
    }
}
