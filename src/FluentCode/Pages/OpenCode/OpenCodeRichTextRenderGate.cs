using System;

namespace FluentCode.Pages.OpenCode;

/// <summary>Skips duplicate rich-text renders while retaining lifecycle and image-context fences.</summary>
internal sealed class OpenCodeRichTextRenderGate
{
    private string? _source;
    private object? _style;
    private object? _imageContext;
    private bool _hasRendered;

    /// <summary>
    /// Records a render when the source/style pair or its binding generation changed.
    /// A canceled generation always renders so unload/reload can create fresh image leases.
    /// </summary>
    internal bool ShouldRender(string? source, object style, object? imageContext, bool generationCanceled)
    {
        if (!generationCanceled && _hasRendered
            && string.Equals(_source, source, StringComparison.Ordinal)
            && Equals(_style, style)
            && ReferenceEquals(_imageContext, imageContext))
        {
            return false;
        }

        _source = source;
        _style = style;
        _imageContext = imageContext;
        _hasRendered = true;
        return true;
    }
}

/// <summary>UI-independent adaptive cadence; only append-only updates may wait for the next render.</summary>
internal sealed class OpenCodeStreamingRenderSchedule
{
    private string? _requested;
    private double _nextRenderAt;
    internal bool HasPending { get; private set; }
    internal double LastRenderMilliseconds { get; private set; }
    internal double IntervalMilliseconds => Math.Max(100, 3 * LastRenderMilliseconds);

    internal bool Request(string? source, bool force, double now)
    {
        var growing = _requested is not null && source is not null && source.Length > _requested.Length
            && source.StartsWith(_requested, StringComparison.Ordinal);
        _requested = source;
        HasPending = !force && growing && now < _nextRenderAt;
        return !HasPending;
    }

    internal double RemainingMilliseconds(double now) => Math.Max(1, _nextRenderAt - now);

    internal void Rendered(double now, double duration)
    {
        HasPending = false;
        LastRenderMilliseconds = duration;
        _nextRenderAt = now + IntervalMilliseconds;
    }
}
