using System;
using System.Threading;
using AIFrontend.Pages.OpenCode;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;

namespace AIFrontend.Pages;

public sealed partial class OpenCodePage
{
    private OpenCodeMarkdownImageScope? _conversationImages;
    private CancellationTokenSource? _conversationImageLifetime;

    partial void ResetConversationImages()
    {
        _conversationImages?.Dispose();
        _conversationImages = null;
        _conversationImageLifetime?.Cancel();
        _conversationImageLifetime?.Dispose();
        _conversationImageLifetime = null;
        OpenCodeImageContext.Unbind(_conversationView);
    }

    partial void EnsureConversationImages()
    {
        if (!_pageActive || _sessionMissing || _source is not { IsRevoked: false } source
            || _client is not { } client || _selectedSessionId is not { } sessionId || _pageCts is null) return;
        var identity = new OpenCodeImageScopeIdentity(source, client, _sourceRevision,
            _sourceGeneration, _pageGeneration, _sessionGeneration, sessionId);
        if (_conversationImages is { IsCurrent: true } existing && existing.Identity == identity) return;
        ResetConversationImages();
        // Page lifetime, not the transient history/send token: those workflows retain this session's images.
        var lifetime = _conversationImageLifetime = CancellationTokenSource.CreateLinkedTokenSource(_pageCts.Token, source.RevocationToken);
        var token = lifetime.Token;
        bool Current() => !token.IsCancellationRequested && !source.IsRevoked
            && identity.PageGeneration == _pageGeneration && sessionId == _selectedSessionId && !_sessionMissing
            && IsCurrentBinding(source, identity.Revision, (int)identity.SourceGeneration, client, (int)identity.SessionGeneration);
        _conversationImages = BindMarkdownImages(_conversationView, identity, Current, token);
    }

    /// <summary>
    /// Parent integration calls once per captured session binding and disposes on replacement/unload.
    /// current must compare source/client/revision/all generations and selected session; child callers additionally
    /// require successful ancestry and current child-window binding. Identity.SessionId is the CHILD for child scopes.
    /// </summary>
    internal static OpenCodeMarkdownImageScope BindMarkdownImages(FrameworkElement subtree,
        OpenCodeImageScopeIdentity identity, Func<bool> current, CancellationToken bindingToken)
    {
        var scope = new OpenCodeMarkdownImageScope(identity, current, async token =>
        {
            // Scope checks current both before metadata and after the awaited response, before any image GET.
            var info = await identity.Client.GetSessionInfoAsync(identity.SessionId, token).ConfigureAwait(false);
            if (!string.Equals(info.Id, identity.SessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("图片会话信息不匹配。");
            return info.Location?.Directory ?? throw new InvalidOperationException("会话未提供图片目录。");
        }, identity.Client.ReadImageFileAsync, bindingToken);
        OpenCodeImageContext.Bind(subtree, scope);
        return scope;
    }
}
