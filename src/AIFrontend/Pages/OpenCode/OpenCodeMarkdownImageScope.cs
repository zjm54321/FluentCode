using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Immutable provenance; the caller's current predicate compares every captured field to its live binding.</summary>
internal sealed record OpenCodeImageScopeIdentity(object Source, OpenCodeClient Client, long Revision,
    long SourceGeneration, long PageGeneration, long SessionGeneration, string SessionId);

/// <summary>One explicit session scope: one metadata read, coalesced relative references, no parent/global fallback.</summary>
internal sealed class OpenCodeMarkdownImageScope : IDisposable
{
    private readonly Func<bool> _current;
    private readonly Func<CancellationToken, Task<string>> _directory;
    private readonly Func<string, string, CancellationToken, Task<OpenCodeImageFile>> _read;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly object _sync = new();
    private readonly Dictionary<string, Task<OpenCodeImageFile>> _images = new(StringComparer.Ordinal);
    private Task<string>? _metadata;
    private bool _disposed;

    internal OpenCodeMarkdownImageScope(OpenCodeImageScopeIdentity identity, Func<bool> current,
        Func<CancellationToken, Task<string>> directory,
        Func<string, string, CancellationToken, Task<OpenCodeImageFile>> read, CancellationToken cancellationToken)
    {
        Identity = identity;
        _current = current;
        _directory = directory;
        _read = read;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _lifetime.Token;
    }

    internal OpenCodeImageScopeIdentity Identity { get; }
    internal bool IsCurrent => !_disposed && !_token.IsCancellationRequested && _current();

    internal async Task<OpenCodeImageFile> ReadAsync(string reference, CancellationToken renderToken)
    {
        renderToken.ThrowIfCancellationRequested();
        CheckCurrent();
        var normalized = OpenCodeImageReference.NormalizeRelativePath(reference);
        Task<OpenCodeImageFile> task;
        lock (_sync)
        {
            CheckCurrent();
            if (!_images.TryGetValue(normalized, out task!))
                _images[normalized] = task = ReadCoreAsync(normalized);
        }
        var image = await task.WaitAsync(renderToken);
        renderToken.ThrowIfCancellationRequested();
        CheckCurrent();
        return image;
    }

    private async Task<OpenCodeImageFile> ReadCoreAsync(string reference)
    {
        CheckCurrent();
        _metadata ??= _directory(_token);
        var directory = await _metadata;
        CheckCurrent();
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("会话未提供图片目录。");
        var image = await _read(directory, reference, _token);
        CheckCurrent();
        return image;
    }

    internal async Task<OpenCodeImageFile> ReadBrowserCaptureAsync(string path, CancellationToken renderToken)
    {
        CheckCurrent();
        _metadata ??= _directory(_token);
        var directory = await _metadata.WaitAsync(renderToken);
        CheckCurrent();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, renderToken);
        var image = await Identity.Client.ReadBrowserImageFileAsync(directory, path, linked.Token);
        renderToken.ThrowIfCancellationRequested();
        CheckCurrent();
        return image;
    }

    private void CheckCurrent()
    {
        if (!IsCurrent) throw new OperationCanceledException(_token);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _images.Clear();
        }
    }
}

/// <summary>Per-presenter publication lease, separate from shared session fetch coalescing.</summary>
internal sealed class OpenCodeImageRenderLifetime
{
    private CancellationTokenSource _lifetime = new();
    private int _generation;
    internal bool IsCanceled => _lifetime.IsCancellationRequested;

    internal (int Generation, CancellationToken Token) Begin()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        return (++_generation, _lifetime.Token);
    }

    internal bool IsCurrent(int generation, CancellationToken token)
        => generation == _generation && !token.IsCancellationRequested;

    internal void Cancel() => _lifetime.Cancel();
}
