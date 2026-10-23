// Inert native-window substitute only. Page binding/HTTP gates and child action/read pipeline remain production code.
using System;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

// Explicit native subtree attachment is a boundary; factory, lifetime, metadata and image HTTP are real.
internal static class OpenCodeImageContext
{
    internal static void Bind(Microsoft.UI.Xaml.FrameworkElement subtree, OpenCodeMarkdownImageScope scope) { }
    internal static void Unbind(Microsoft.UI.Xaml.FrameworkElement subtree) { }
}

    internal sealed partial class OpenCodeToolDetailPopup
{
    internal static OpenCodeToolDetailPopup Shared { get; } = new();
    internal void Close() { }
}

internal sealed class OpenCodeChildChatWindow
{
    private readonly CancellationTokenSource _lifetime;
    private readonly OpenCodeChildChatActions _actions;
    internal static OpenCodeChildChatWindow? Latest { get; private set; }
    internal OpenCodeSessionMessagesResponse? Messages { get; private set; }
    internal string? Error { get; private set; }
    internal OpenCodeMarkdownImageScope? Images { get; private set; }
    internal TaskCompletionSource<bool> ContentReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Token => _lifetime.Token;
    internal bool IsOpen => !_actions.IsClosed && !Token.IsCancellationRequested;
    internal OpenCodeChildChatWindow(object root, CancellationToken token, Func<bool> current, Func<Task> maximize)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _actions = new OpenCodeChildChatActions(current, () => _lifetime.Cancel(), maximize);
        Latest = this;
    }
    internal void ShowMessages(OpenCodeSessionMessagesResponse response) { Messages = response; _actions.MarkLoaded(); ContentReady.TrySetResult(true); }
    internal void InitializeImages(Func<OpenCodeConversationView, CancellationToken, OpenCodeMarkdownImageScope> initialize)
        => Images = initialize(new OpenCodeConversationView(), Token);
    internal void ShowError(string message) { Error = message; ContentReady.TrySetResult(false); }
    internal void Close() { _actions.Close(); Images?.Dispose(); }
    internal Task Maximize() => _actions.MaximizeAsync();
}
